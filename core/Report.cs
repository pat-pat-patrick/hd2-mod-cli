using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>A problem report: one zip a player can send with a question (the user,
    /// 2026-10-29: players should not have to find the files themselves). It holds the state
    /// as the window sees it, the deploy history, the crash places learned, the settings, the
    /// Nexus Mods download history, Boot Mod Loader's log, the names of the game's crash
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
            var mdl3 = new List<(string Name, string Text)>();
            if (Mdl3(lib, game, mdl3) is string native) summary += native + Environment.NewLine;

            var entries = new List<(string Name, string Text)>
            {
                ("info.txt", Info(now) + Environment.NewLine + summary),
                ("state.json", Json.Write(state, true)),
            };
            foreach (var f in new[] { "config.json", "deploy_history.json", "crash_sites.json" })
                if (File.Exists(Store.PathOf(f))) entries.Add((f, Json.Write(Store.Load(f), true)));
#if HAS_NEXUS
            // the downloads only: nexus.json also holds the sign-in
            var history = Json.List(Store.Load("nexus.json"), "history");
            if (history.Count > 0) entries.Add(("nexus_history.json", Json.Write(new Dictionary<string, object> { ["history"] = history }, true)));
#endif
            var logs = LuaMods.GameFolderOverride ?? LuaMods.GameFolder;
            var bml = Path.Combine(logs, "boot_mod_loader.log");
            if (File.Exists(bml)) entries.Add(("game/boot_mod_loader.log", Tail(bml, LogTail)));
            var dumps = LuaMods.Crashes(20);
            if (dumps.Count > 0)
                entries.Add(("game/dumps.txt", string.Join(Environment.NewLine, dumps.Select(d =>
                    d.When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + d.File)) + Environment.NewLine));
            entries.AddRange(mdl3);

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

        /// <summary>MDL3, when the library has it or this PC has its folder (the user, 2026-11-01: a
        /// player's MDL3 mods did nothing, and the report could not say whether its native part
        /// was there): the package the deploy uses, what is in %LOCALAPPDATA%\MDL3, where its boot
        /// loads the native part from, and the logs of MDL3, MDL and the shared Bingus folder,
        /// where Data Hub and the mods MDL3 runs write theirs (there, only those of the last
        /// week). The report's line on it, else null.</summary>
        static string Mdl3(Library lib, string game, List<(string Name, string Text)> entries)
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
                    ["files"] = files,
                    ["config"] = Mdl3Native.ReadJson(Path.Combine(data, "config.json")),
                    ["install_state"] = Mdl3Native.ReadJson(Path.Combine(data, "install-state.json")),
                }, true)));
                var week = DateTime.Now.AddDays(-7);
                foreach (var (dir, every) in new[] { (@"MDL3\Logs", true), (@"MDL\Helldivers2\Logs", true), (@"CowboyBingus\Helldivers2\Logs", false) })
                {
                    var full = Path.Combine(Mdl3Native.LocalAppData(), dir);
                    if (!Directory.Exists(full)) continue;
                    foreach (var f in Directory.GetFiles(full, "*.log").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        if (every || File.GetLastWriteTime(f) >= week)
                            entries.Add(("mdl3/" + dir.Replace('\\', '/') + "/" + Path.GetFileName(f), Tail(f, Mdl3LogTail)));
                }
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
