using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>A problem report: one zip a player can send with a question (the user,
    /// 2026-10-29: players should not have to find the files themselves). It holds the state
    /// as the window sees it, the deploy history, the crash places learned, the settings, the
    /// Nexus Mods download history, the manager's own log, Boot Mod Loader's log, the names of the game's crash
    /// dumps, and MDL3's native part and logs when MDL3 is there. Never the dumps themselves (large, and the game's memory), never nexus.json
    /// itself (the sign-in and any key are in it), and the Windows user's folder is written
    /// as %USERPROFILE% everywhere.</summary>
    public static class Report
    {
        /// <summary>The most of Boot Mod Loader's log taken: its end, where the last sessions are.</summary>
        public const int LogTail = 1 << 20;

        /// <summary>The report written to out (a .zip, or a folder for it; null: the desktop):
        /// where it is, and a few lines that say what is in it.</summary>
        public static (string Path, string Summary) Write(Library lib, string game, string out_ = null)
        {
            var now = DateTime.Now;
            var name = "hd2qd-report-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip";
            string path;
            if (out_ == null)
            {
                var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                path = Path.Combine(Directory.Exists(desk) ? desk : Store.Home, name);
            }
            else path = Directory.Exists(out_) || out_.EndsWith("\\") || out_.EndsWith("/") ? Path.Combine(out_, name) : out_;
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // every script read now, so the report says what the window would once it has read them all
            try { LuaMods.Of(lib); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            var state = StateDoc.Build(lib, game);
            var summary = Summary(state, now);
            var entries = new List<(string Name, string Text)> { ("info.txt", null), ("summary.json", null), ("state.json", Json.Write(state, true)) };
            foreach (var f in new[] { "config.json", "deploy_history.json", "crash_sites.json" })
                if (File.Exists(Store.PathOf(f))) entries.Add((f, Json.Write(Store.Load(f), true)));
#if HAS_NEXUS
            // the downloads only: nexus.json also holds the sign-in
            var history = Json.List(Store.Load("nexus.json"), "history");
            if (history.Count > 0) entries.Add(("nexus_history.json", Json.Write(new Dictionary<string, object> { ["history"] = history }, true)));
#endif
            // the manager's own log: what it was asked to do and how that ended
            if (File.Exists(ManagerLog.PathOf)) entries.Add((ManagerLog.File, Tail(ManagerLog.PathOf, LogTail)));
            var logs = LuaMods.GameFolderOverride ?? LuaMods.GameFolder;
            var bml = Path.Combine(logs, "boot_mod_loader.log");
            if (File.Exists(bml)) entries.Add(("game/boot_mod_loader.log", Tail(bml, LogTail)));
            var sizes = new List<(string Name, long Bytes)>();
            Sized(sizes, ManagerLog.File, ManagerLog.PathOf);
            Sized(sizes, "game/boot_mod_loader.log", bml);
            var dumps = LuaMods.Crashes(20);
            if (dumps.Count > 0)
                entries.Add(("game/dumps.txt", string.Join(Environment.NewLine, dumps.Select(d =>
                    d.When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + d.File)) + Environment.NewLine));
            var mdl3 = new List<(string Name, string Text)>();
            if (Mdl3(lib, game, mdl3, sizes, LogBudget - entries.Sum(e => (long)(e.Text?.Length ?? 0))) is string native) summary += native + Environment.NewLine;
            entries.AddRange(mdl3);

            // a log that ran away (the user, 2026-11-03: another tool's log once grew to gigabytes,
            // its CPU high, and nobody saw it): every log's whole size, and the ones past
            // LogTooBig named in the summary, though only their ends are taken
            var facts = Facts(state);
            facts["logs"] = sizes.Select(s => (object)new Dictionary<string, object> { ["name"] = s.Name, ["bytes"] = s.Bytes }).ToList();
            var big = sizes.Where(s => s.Bytes > LogTooBig).ToList();
            facts["big_logs"] = big.Select(s => (object)s.Name).ToList();
            if (big.Count > 0)
                summary += "logs too large: " + string.Join(", ", big.Select(s => s.Name + " " + Megabytes(s.Bytes))) + " (only their ends are in the report)" + Environment.NewLine;
            entries[0] = ("info.txt", Info(now) + Environment.NewLine + summary);
            // what the upload sends beside the zip, read back from it (Upload)
            entries[1] = ("summary.json", Json.Write(facts, true));

            // written aside, then put in place: a report cut short is never left with the name
            var part = path + ".part";
            try
            {
                using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                    foreach (var (entry, text) in entries)
                        using (var w = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false)))
                            w.Write(Redact(text));
                if (File.Exists(path)) File.Delete(path);
                File.Move(part, path);
            }
            finally { try { File.Delete(part); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { } }
            return (path, summary);
        }

        /// <summary>The most of each MDL3, MDL or Data Hub log taken.</summary>
        public const int Mdl3LogTail = 1 << 18;

        /// <summary>The most text a report takes in all: past it, the rest of the MDL3 folders'
        /// logs is only listed (a folder of hundreds of logs made a report too large to send).</summary>
        public static long LogBudget = 12 << 20;

        /// <summary>A log past this is named in the summary as too large: a log that runs away
        /// (written over and over) is a problem of its own, and often the one asked about.</summary>
        public static long LogTooBig = 100L << 20;

        static void Sized(List<(string Name, long Bytes)> sizes, string name, string file)
        {
            try { var fi = new FileInfo(file); if (fi.Exists) sizes.Add((name, fi.Length)); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        static string Megabytes(long bytes) =>
            bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                              : (bytes / (double)(1 << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

        /// <summary>MDL3, when the library has it or this PC has its folder (the user, 2026-11-01: a
        /// player's MDL3 mods did nothing, and the report could not say whether its native part
        /// was there): the package the deploy uses, what is in %LOCALAPPDATA%\MDL3, where its boot
        /// loads the native part from, and the logs of MDL3, MDL and the shared Bingus folder,
        /// where Data Hub and the mods MDL3 runs write theirs (there, only those of the last
        /// week). The report's line on it, else null.</summary>
        static string Mdl3(Library lib, string game, List<(string Name, string Text)> entries, List<(string Name, long Bytes)> sizes, long budget)
        {
            try
            {
                var p = Mdl3Native.Wanted(lib);
                var data = Mdl3Native.DataDir();
                if (p == null && !Directory.Exists(data)) return null;
                string need = null;
                if (p != null)
                    try { need = Mdl3Native.Need(p, game) ?? "installed"; }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { need = "not readable (" + e.Message + ")"; }
                var files = new List<object>();
                if (Directory.Exists(data))
                    foreach (var f in Directory.GetFiles(data).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        var row = new Dictionary<string, object>
                        {
                            ["name"] = Path.GetFileName(f), ["bytes"] = new FileInfo(f).Length,
                            ["modified"] = File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        };
                        if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            row["sha256"] = Mdl3Native.Sha(f);
                            row["signed"] = Mdl3Native.Signed(f);
                        }
                        files.Add(row);
                    }
                entries.Add(("mdl3/native.json", Json.Write(new Dictionary<string, object>
                {
                    ["package"] = p == null ? null : new Dictionary<string, object>
                    {
                        ["folder"] = p.Folder, ["version"] = p.Version, ["native_version"] = p.NativeVersion, ["sha256"] = p.Sha256, ["problem"] = p.Problem,
                    },
                    ["native_part"] = need,
                    ["folder"] = data,
                    // where the DLL goes: MDL3's folder, or this program's when the game cannot open that path
                    ["dll_folder"] = Mdl3Native.DllHome(), ["code_page"] = (long)Mdl3Native.CodePage(),
                    ["files"] = files,
                    ["config"] = Mdl3Native.ReadJson(Path.Combine(data, "config.json")),
                    ["install_state"] = Mdl3Native.ReadJson(Path.Combine(data, "install-state.json")),
                }, true)));
                var week = DateTime.Now.AddDays(-7);
                var left = new List<string>();
                foreach (var (dir, every) in new[] { (@"MDL3\Logs", true), (@"MDL\Helldivers2\Logs", true), (@"CowboyBingus\Helldivers2\Logs", false) })
                {
                    var full = Path.Combine(Mdl3Native.LocalAppData(), dir);
                    if (!Directory.Exists(full)) continue;
                    // the newest first: past the budget, the older ones are only listed
                    foreach (var f in Directory.GetFiles(full, "*.log").OrderByDescending(File.GetLastWriteTimeUtc))
                        if (every || File.GetLastWriteTime(f) >= week)
                        {
                            var name = "mdl3/" + dir.Replace('\\', '/') + "/" + Path.GetFileName(f);
                            Sized(sizes, name, f);
                            if (budget <= 0) { left.Add(name); continue; }
                            var text = Tail(f, Mdl3LogTail);
                            budget -= text.Length;
                            entries.Add((name, text));
                        }
                }
                if (left.Count > 0)
                    entries.Add(("mdl3/left_out.txt", "past the report's size budget, these logs are not in it:" + Environment.NewLine + string.Join(Environment.NewLine, left) + Environment.NewLine));
                return p == null ? null : "MDL3 native part: " + need + " (MDL3 " + p.Version + ", native " + p.NativeVersion + ")";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                entries.Add(("mdl3/native.json", Json.Write(new Dictionary<string, object> { ["error"] = e.Message }, true)));
                return "MDL3 native part: not readable (" + e.Message + ")";
            }
        }

        /// <summary>The Windows user's folder (C:\Users\NAME) as %USERPROFILE%, as it is written
        /// in a path, in JSON (\\) and with forward slashes: their name is not the report's.</summary>
        public static string Redact(string text, string profile = null)
        {
            profile = (profile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))?.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(profile) || profile.Length < 3) return text;
            foreach (var form in new[] { profile.Replace("\\", "\\\\"), profile, profile.Replace('\\', '/') }.Distinct())
                text = Regex.Replace(text, Regex.Escape(form) + @"(?=[\\/""]|$)", "%USERPROFILE%", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return text;
        }

        static string Info(DateTime now) =>
            "hd2-mod-cli " + StateDoc.Version + " (" + (SelfUpdate.Official ? "official build" : "development build") +
#if HAS_NEXUS
            ", with Nexus Mods" +
#else
            ", without Nexus Mods" +
#endif
            ")" + Environment.NewLine +
            Windows() + (Environment.Is64BitOperatingSystem ? " 64-bit" : " 32-bit") +
            ", .NET " + Environment.Version + Environment.NewLine +
            "written " + now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + Environment.NewLine;

        /// <summary>Windows as its own settings name it ("Windows 11 Pro 24H2, build 26100.2314"):
        /// Environment.OSVersion says 6.2 on every Windows since 8 to a program like this one.</summary>
        static string Windows()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k?.GetValue("CurrentBuild") is string build)
                    {
                        var product = k.GetValue("ProductName") as string ?? "Windows";
                        // Windows 11 still calls itself Windows 10 there; its builds start at 22000
                        if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
                        var display = k.GetValue("DisplayVersion") as string ?? k.GetValue("ReleaseId") as string;
                        return product + (display != null ? " " + display : "") + ", build " + build +
                               (k.GetValue("UBR") is int ubr ? "." + ubr : "");
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is IOException) { }
            return "Windows " + Environment.OSVersion.Version;
        }

        /// <summary>A few lines on what the report holds, to be pasted where a zip cannot go.</summary>
        public static string Summary(Dictionary<string, object> state, DateTime now)
        {
            var mods = Json.List(state, "mods").OfType<Dictionary<string, object>>().ToList();
            var lua = Json.Obj(state, "lua_check");
            var issues = Json.List(lua, "issues").OfType<Dictionary<string, object>>().Select(i => Json.Str(i, "kind")).ToList();
            var clashes = Json.List(state, "conflicts").OfType<Dictionary<string, object>>()
                              .Where(c => !Json.Bool(c, "same") && !Json.Bool(c, "managed_boot")).ToList();
            var lines = new List<string>
            {
                "hd2-mod-cli " + StateDoc.Version + ", report of " + now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                "mods: " + mods.Count + " (" + mods.Count(m => Json.Bool(m, "enabled")) + " on)" +
                    ", loader: " + (Json.Str(lua, "running") ?? Json.Str(lua, "boot_kind") ?? "none") +
                    ", conflicts: " + clashes.Count,
                "Lua issues: " + (issues.Count == 0 ? "none" : issues.Count + " (" + string.Join(", ", issues.Distinct()) + ")"),
            };
            var crash = Json.Obj(lua, "crash");
            if (crash != null)
                lines.Add("last crash: " + DateTimeOffset.FromUnixTimeSeconds(Json.Long(crash, "when")).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                          (Json.Str(crash, "site") is string site ? " at " + site : ""));
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        /// <summary>What Summary says, as data: the report server lists and counts reports by it
        /// without opening their zips.</summary>
        public static Dictionary<string, object> Facts(Dictionary<string, object> state)
        {
            var mods = Json.List(state, "mods").OfType<Dictionary<string, object>>().ToList();
            var lua = Json.Obj(state, "lua_check");
            var facts = new Dictionary<string, object>
            {
                ["version"] = StateDoc.Version,
                ["mods"] = (long)mods.Count, ["mods_on"] = (long)mods.Count(m => Json.Bool(m, "enabled")),
                ["conflicts"] = (long)Json.List(state, "conflicts").OfType<Dictionary<string, object>>().Count(c => !Json.Bool(c, "same") && !Json.Bool(c, "managed_boot")),
                ["loader"] = Json.Str(lua, "running") ?? Json.Str(lua, "boot_kind"),
                ["lua_issues"] = Json.List(lua, "issues").OfType<Dictionary<string, object>>().Select(i => Json.Str(i, "kind")).Where(k => k != null).Distinct().Cast<object>().ToList(),
            };
            var crash = Json.Obj(lua, "crash");
            if (crash != null)
            {
                facts["crash_when"] = Json.Long(crash, "when");
                facts["crash_site"] = Json.Str(crash, "site");
            }
            return facts;
        }

        // -- uploading (the user, 2026-11-03: players send the report from the manager, with
        //    what the problem is in a few words; the maintainer's server keeps it 30 days) --

        /// <summary>Where reports go: HD2 Mod Report (utilities\hd2-mod-report), on our domain;
        /// *.workers.dev cannot be reached from mainland China. A test can say otherwise.</summary>
        public static string UploadUrl = Environment.GetEnvironmentVariable("HD2QD_REPORT_URL") ?? "https://report.patpatpatrick.com";

        /// <summary>The kinds of problem the window offers, as the server knows them.</summary>
        public static readonly string[] Kinds = { "crash_start", "crash_mission", "mod_not_working", "manager", "other" };

        /// <summary>The most of a player's note sent; the server keeps as much.</summary>
        public const int NoteMax = 1000;

        /// <summary>The largest report the server takes (its MAX_BYTES).</summary>
        public static long UploadMax = 20 << 20;

        /// <summary>An upload that did not go: Code is "too_large", "rate_limited", "network" or "server".</summary>
        public sealed class UploadFailed : Exception
        {
            public readonly string Code;
            public UploadFailed(string code, string detail) : base(code + ": " + detail) { Code = code; }
        }

        static readonly HttpClient http = MakeClient();

        static HttpClient MakeClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("HD2ModQuickDeploy/" + StateDoc.Version);
            return c;
        }

        /// <summary>Sends a report the manager wrote, with the kind of problem and the player's note
        /// (their user folder written as %USERPROFILE% there too) and the report's summary.json;
        /// the id the server gives it ("QD-7F3K2M"), for the player to quote.</summary>
        public static string Upload(string zip, string kind, string note)
        {
            if (!Kinds.Contains(kind)) throw new ArgumentException("unknown kind: " + kind);
            // the server's own limit (MAX_BYTES), said here before anything is sent
            var size = new FileInfo(zip).Length;
            if (size > UploadMax) throw new UploadFailed("too_large", size + " bytes");
            note = Redact((note ?? "").Trim());
            if (note.Length > NoteMax) note = note.Substring(0, NoteMax);
            object facts = new Dictionary<string, object>();
            using (var z = ZipFile.OpenRead(zip))
                if (z.GetEntry("summary.json") is ZipArchiveEntry e)
                    using (var r = new StreamReader(e.Open(), Encoding.UTF8))
                        try { facts = Json.Parse(r.ReadToEnd()); } catch (FormatException) { }
            var meta = Json.Write(new Dictionary<string, object> { ["kind"] = kind, ["note"] = note, ["version"] = StateDoc.Version, ["summary"] = facts });
            var header = Convert.ToBase64String(Encoding.UTF8.GetBytes(meta)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using (var req = new HttpRequestMessage(HttpMethod.Post, UploadUrl.TrimEnd('/') + "/upload") { Content = new ByteArrayContent(File.ReadAllBytes(zip)) })
            {
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                req.Headers.Add("X-Report-Meta", header);
                HttpResponseMessage res;
                try { res = http.SendAsync(req).GetAwaiter().GetResult(); }
                catch (Exception x) when (x is HttpRequestException || x is System.Threading.Tasks.TaskCanceledException) { throw new UploadFailed("network", x.Message); }
                using (res)
                {
                    var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Dictionary<string, object> doc = null;
                    try { doc = Json.Parse(text) as Dictionary<string, object>; } catch (FormatException) { }
                    if (res.IsSuccessStatusCode && Json.Str(doc, "id") is string id && Regex.IsMatch(id, "^QD-[0-9A-Z]{6}$")) return id;
                    int status = (int)res.StatusCode;
                    throw new UploadFailed(status == 413 ? "too_large" : status == 429 ? "rate_limited" : "server", Json.Str(doc, "error") ?? status.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>A report for the upload window, in the settings folder's reports\: the window
        /// lists what is in it before anything is sent. The five newest are kept there.</summary>
        public static (string Path, string Summary) Draft(Library lib, string game)
        {
            var dir = Store.PathOf("reports");
            Directory.CreateDirectory(dir);
            var made = Write(lib, game, dir + Path.DirectorySeparatorChar);
            foreach (var old in new DirectoryInfo(dir).GetFiles("hd2qd-report-*.zip").OrderByDescending(f => f.LastWriteTimeUtc).Skip(5))
                try { old.Delete(); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return made;
        }

        /// <summary>What a report holds: each file's name and size, for the player to see first.</summary>
        public static List<(string Name, long Bytes)> Contents(string zip)
        {
            using (var z = ZipFile.OpenRead(zip))
                return z.Entries.Select(e => (e.FullName, e.Length)).ToList();
        }

        /// <summary>A text file's last bytes (all of it when smaller), read while the game may
        /// still be writing it; from the start of a line.</summary>
        static string Tail(string path, int max)
        {
            try
            {
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long from = Math.Max(0, f.Length - max);
                    f.Seek(from, SeekOrigin.Begin);
                    var text = new StreamReader(f, Encoding.UTF8).ReadToEnd();
                    if (from > 0)
                    {
                        int nl = text.IndexOf('\n');
                        text = "[the first " + from + " bytes left out]" + Environment.NewLine + (nl >= 0 ? text.Substring(nl + 1) : text);
                    }
                    return text;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return "[not readable: " + e.Message + "]"; }
        }
    }
}
