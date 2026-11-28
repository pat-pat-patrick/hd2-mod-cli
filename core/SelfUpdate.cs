using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>What the last check of this program's own releases found.</summary>
    public class SelfUpdateInfo
    {
        /// <summary>The newest release's tag and page, or null when none counts for this build.</summary>
        public string Latest, Page;
        public bool Prerelease;
        /// <summary>Whether it is newer than the version running.</summary>
        public bool Newer;
        /// <summary>When it was checked (Unix seconds); 0: never.</summary>
        public long CheckedAt;
        /// <summary>What each release newer than the one running says, newest first: its tag,
        /// page and text (GitHub's release body, in both languages: SelfUpdate.NotesIn).</summary>
        public List<(string Tag, string Page, string Body)> Notes = new List<(string, string, string)>();
        /// <summary>The newest release's files and their SHA-256 (lower-case hex), from GitHub's
        /// "digest" of each: what Install downloads and checks. A file GitHub gave no digest is
        /// not in it.</summary>
        public Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>This program's own updates, from its GitHub releases (the user, 2026-10-25). The
    /// development build is published there as pre-releases, the official build as releases: the
    /// official build counts only releases, so it is never offered a build without Nexus Mods;
    /// the development build counts both. The last result is kept, so the window can show it
    /// before (or without) going online; the automatic check runs at most once a day. Install
    /// puts the newest release in place of the program's own files (the user, 2026-11-26:
    /// downloading every update from GitHub by hand was a chore).</summary>
    public static class SelfUpdate
    {
        public const string Repo = "pat-pat-patrick/hd2-mod-cli";
        const string File = "self_update.json";
        const long Day = 24 * 3600;

#if HAS_NEXUS
        public const bool Official = true;
#else
        public const bool Official = false;
#endif

        /// <summary>A tag's version as numbers: "v0.9.0", "0.9", "v1.2.3-dev" -> [0,9,0]...; null when it has none.</summary>
        public static int[] Numbers(string tag)
        {
            var m = Regex.Match(tag ?? "", @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) return null;
            return Enumerable.Range(1, 3).Select(i => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0).ToArray();
        }

        public static int Compare(int[] a, int[] b)
        {
            for (int i = 0; i < 3; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return 0;
        }

        /// <summary>The newest release that counts for this build, out of GitHub's list of them.</summary>
        public static SelfUpdateInfo Pick(List<Dictionary<string, object>> releases, string running, bool official)
        {
            var now = Numbers(running);
            var counted = releases
                .Where(r => !Json.Bool(r, "draft", false) && (!official || !Json.Bool(r, "prerelease", false)))
                .Select(r => (R: r, N: Numbers(Json.Str(r, "tag_name"))))
                .Where(x => x.N != null)
                .OrderByDescending(x => x.N[0]).ThenByDescending(x => x.N[1]).ThenByDescending(x => x.N[2])
                .ToList();
            if (counted.Count == 0) return new SelfUpdateInfo();
            var best = counted[0];
            return new SelfUpdateInfo
            {
                Latest = Json.Str(best.R, "tag_name"), Page = Json.Str(best.R, "html_url"),
                Prerelease = Json.Bool(best.R, "prerelease", false),
                Newer = now != null && Compare(best.N, now) > 0,
                // every version the player would get by updating, so the window can say all of it
                Notes = now == null ? new List<(string, string, string)>() : counted.Where(x => Compare(x.N, now) > 0)
                    .Select(x => (Json.Str(x.R, "tag_name"), Json.Str(x.R, "html_url"), Json.Str(x.R, "body") ?? "")).ToList(),
                Files = FilesOf(best.R),
            };
        }

        /// <summary>A release's files with GitHub's digest ("sha256:&lt;hex&gt;"), as name -> hex.</summary>
        static Dictionary<string, string> FilesOf(Dictionary<string, object> release)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in (Json.List(release, "assets") ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                var name = Json.Str(a, "name");
                var digest = Json.Str(a, "digest");
                if (name != null && digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    files[name] = digest.Substring(7).ToLowerInvariant();
            }
            return files;
        }

        /// <summary>A release text in one language, lang a language tag ("zh-CN"). Each language's
        /// part follows a line &lt;!-- tag --&gt; (&lt;!-- en --&gt;, &lt;!-- zh --&gt;, &lt;!-- pt-BR --&gt;); what comes
        /// before the first is for all. A part is the language's when its tag is lang or lang's
        /// language (zh for zh-CN). Without a part for lang: the English part, else every part, and
        /// Same is true, as for a text with no markers.</summary>
        public static (string Text, bool Same) NotesIn(string body, string lang)
        {
            body = (body ?? "").Replace("\r\n", "\n");
            var parts = Regex.Split(body, @"^[ \t]*<!--\s*([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)\s*-->[ \t]*$", RegexOptions.Multiline);
            if (parts.Length == 1) return (body.Trim(), true);
            // each part starts with the marker line's own end
            string Part(int i) => parts[i + 1].StartsWith("\n") ? parts[i + 1].Substring(1) : parts[i + 1];
            string Language(string tag) => tag.Split('-')[0].ToLowerInvariant();
            bool Of(string marker, string tag) =>
                string.Equals(marker, tag, StringComparison.OrdinalIgnoreCase) || (!marker.Contains("-") && Language(marker) == Language(tag ?? ""));
            string Pick(Func<string, bool> mine)
            {
                var sb = new StringBuilder(parts[0]);
                bool any = false;
                for (int i = 1; i + 1 < parts.Length; i += 2)
                    if (mine(parts[i])) { sb.Append(Part(i)); any = true; }
                return any ? sb.ToString().Trim() : null;
            }
            var own = Pick(m => Of(m, lang ?? "en"));
            if (own != null) return (own, false);
            return (Pick(m => Of(m, "en")) ?? Pick(m => true), true);
        }

        /// <summary>Whether the window should show the notes of the newest version by itself: it
        /// is newer than the one running, and its notes have not been shown yet (once a version,
        /// the user, 2026-11-02).</summary>
        public static bool NotesDue(SelfUpdateInfo u) => u.Newer && u.Latest != null && Json.Str(Store.Load(File), "notes_shown") != u.Latest;

        public static void NotesShown(string tag)
        {
            var d = Store.Load(File);
            d["notes_shown"] = tag;
            Store.Save(File, d);
        }

        /// <summary>GitHub asked now; the result kept.</summary>
        public static SelfUpdateInfo Check()
        {
            string text;
            // GitHub first, briefly; then the author's copy of the same list (Mirror)
            try { text = Updates.Get(Updates.GitHubApi.TrimEnd('/') + "/repos/" + Repo + "/releases?per_page=30", Mirror.On ? Mirror.ListWait : (TimeSpan?)null); }
            catch (ModError) when (Mirror.On) { text = Updates.Get(Mirror.Url("releases/hd2-mod-cli.json")); }
            var list = Json.Parse(text) as List<object>;
            var found = Pick((list ?? new List<object>()).OfType<Dictionary<string, object>>().ToList(), StateDoc.Version, Official);
            found.CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var d = Store.Load(File);
            d["checked_at"] = found.CheckedAt;
            d["latest"] = found.Latest;
            d["page"] = found.Page;
            d["prerelease"] = found.Prerelease;
            d["notes"] = found.Notes.Select(n => (object)new Dictionary<string, object> { ["tag"] = n.Tag, ["page"] = n.Page, ["body"] = n.Body }).ToList();
            d["files"] = found.Files.ToDictionary(f => f.Key, f => (object)f.Value);
            Store.Save(File, d);
            return found;
        }

        /// <summary>The last result, compared again with the version running now: once the new
        /// version is the one running, it is not newer any more. A pre-release a development
        /// build found in the same folder is nothing to the official build.</summary>
        public static SelfUpdateInfo Last()
        {
            var d = Store.Load(File);
            var latest = Json.Str(d, "latest");
            var pre = Json.Bool(d, "prerelease", false);
            if (Official && pre) latest = null;
            var n = Numbers(latest);
            var now = Numbers(StateDoc.Version);
            return new SelfUpdateInfo
            {
                Latest = latest, Page = latest == null ? null : Json.Str(d, "page"), Prerelease = latest != null && pre,
                CheckedAt = Official && pre ? 0 : Json.Long(d, "checked_at"),
                Newer = n != null && now != null && Compare(n, now) > 0,
                // the ones still newer than the version running now
                Notes = latest == null || now == null ? new List<(string, string, string)>()
                    : (Json.List(d, "notes") ?? new List<object>()).OfType<Dictionary<string, object>>()
                        .Where(x => Numbers(Json.Str(x, "tag")) is int[] t && Compare(t, now) > 0)
                        .Select(x => (Json.Str(x, "tag"), Json.Str(x, "page"), Json.Str(x, "body") ?? "")).ToList(),
                Files = latest == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : (Json.Obj(d, "files") ?? new Dictionary<string, object>()).Where(f => f.Value is string)
                        .ToDictionary(f => f.Key, f => ((string)f.Value).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase),
            };
        }

        /// <summary>The release files: the window, and the command line beside it.</summary>
        public const string WindowAsset = "HD2.Mod.Quick.Deploy.exe", CliAsset = "hd2qd.exe";

        /// <summary>The window's file as it is on disk (GitHub's asset name has dots for spaces).</summary>
        public const string WindowFile = "HD2 Mod Quick Deploy.exe";

        /// <summary>The newest release in place of this program's files: window, the window's exe
        /// (the one running); cli, hd2qd.exe beside it, updated too when it is there (null: not).
        /// Each file is downloaded (the author's copy first, then GitHub, as plugins are), taken
        /// only when its SHA-256 is GitHub's digest and its version is the release's, written
        /// beside the old one as .new, and only once every file is there put in place: the old
        /// file renamed .old (a running exe can be renamed, not overwritten), the new one given
        /// its name. The running program carries on as it is; it is the next start that runs the
        /// new version. The .old files go at that start (CleanOld). Returns what could not be
        /// updated beside the window (the command line), else null.</summary>
        public static string Install(SelfUpdateInfo u, string window, string cli)
        {
            if (u == null || u.Latest == null || !u.Newer) throw new ModError("no newer version to install", "self_none_newer");
            var wanted = new List<(string Asset, string Path)> { (WindowAsset, window) };
            if (cli != null && System.IO.File.Exists(cli)) wanted.Add((CliAsset, cli));
            var ready = new List<(string Path, string New)>();
            try
            {
                foreach (var (asset, path) in wanted)
                {
                    if (!u.Files.TryGetValue(asset, out var sha))
                        throw new ModError(u.Latest + ": GitHub gives no SHA-256 for " + asset, "self_no_digest", asset);
                    var bytes = Download(u.Latest, asset, sha);
                    CheckVersion(bytes, u.Latest, asset);
                    var fresh = path + ".new";
                    try { System.IO.File.WriteAllBytes(fresh, bytes); }
                    catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
                    {
                        throw new ModError("cannot write beside " + path + ": " + e.Message, "self_folder", Path.GetDirectoryName(path));
                    }
                    ready.Add((path, fresh));
                }
                // the window first: the update is the window's; the command line is a companion
                Swap(ready[0].Path, ready[0].New);
                string left = null;
                for (int i = 1; i < ready.Count; i++)
                    try { Swap(ready[i].Path, ready[i].New); }
                    catch (Exception e) when (e is UnauthorizedAccessException || e is IOException) { left = ready[i].Path + ": " + e.Message; }
                return left;
            }
            finally
            {
                foreach (var r in ready)
                    try { if (System.IO.File.Exists(r.New)) System.IO.File.Delete(r.New); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
        }

        static string Sha(byte[] b) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }

        /// <summary>A release file: the author's copy when it is the very file (Mirror), else GitHub's.</summary>
        static byte[] Download(string tag, string asset, string sha)
        {
            if (Mirror.On)
                try
                {
                    var copy = Updates.GetBytes(Mirror.ReleaseFile(Repo, tag, asset));
                    if (Sha(copy) == sha) return copy;
                }
                catch (ModError) { }
            var bytes = Updates.GetBytes(PluginFiles.Downloads.TrimEnd('/') + "/" + Repo + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(asset));
            var got = Sha(bytes);
            if (got != sha) throw new ModError("the download of " + asset + " " + tag + " is not GitHub's file (SHA-256 " + got + ")", "self_sha", asset);
            return bytes;
        }

        /// <summary>The file must be a program of the release's version (its assembly version's
        /// first three numbers are the tag's): never an older build, never something else.</summary>
        static void CheckVersion(byte[] bytes, string tag, string asset)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "hd2qd-update-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            try
            {
                System.IO.File.WriteAllBytes(tmp, bytes);
                Version v;
                try { v = System.Reflection.AssemblyName.GetAssemblyName(tmp).Version; }
                catch (Exception e) when (e is BadImageFormatException || e is FileLoadException)
                {
                    throw new ModError(asset + " " + tag + " is not a program of this kind", "self_bad_file", asset);
                }
                var want = Numbers(tag);
                if (v == null || want == null || v.Major != want[0] || v.Minor != want[1] || Math.Max(v.Build, 0) != want[2])
                    throw new ModError(asset + " " + tag + " is version " + v + ", not " + tag, "self_bad_file", asset);
            }
            finally { try { System.IO.File.Delete(tmp); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { } }
        }

        /// <summary>path's file renamed path.old (an older .old removed first; a .old still in use
        /// kept, and this one named .old-&lt;time&gt;), then fresh given path's name; when that
        /// fails, the old file is put back.</summary>
        static void Swap(string path, string fresh)
        {
            var old = path + ".old";
            try { if (System.IO.File.Exists(old)) System.IO.File.Delete(old); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { old = path + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss"); }
            System.IO.File.Move(path, old);
            try { System.IO.File.Move(fresh, path); }
            catch (Exception) { System.IO.File.Move(old, path); throw; }
        }

        /// <summary>At the start: what an update left beside these files (path.old, path.old-*,
        /// path.new) removed, when nothing holds it any more.</summary>
        public static void CleanOld(params string[] paths)
        {
            foreach (var path in paths.Where(p => p != null))
            {
                var dir = Path.GetDirectoryName(path);
                var name = Path.GetFileName(path);
                if (dir == null || !Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, name + ".old*").Concat(Directory.GetFiles(dir, name + ".new")))
                    try { System.IO.File.Delete(f); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
        }

        /// <summary>Whether to check by itself (on unless turned off).</summary>
        public static bool Auto
        {
            get => Json.Bool(Store.Load(File), "auto", true);
            set { var d = Store.Load(File); d["auto"] = value; Store.Save(File, d); }
        }

        /// <summary>Whether the automatic check is due: on, and not checked in the last day.</summary>
        public static bool Due() => Auto && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Last().CheckedAt >= Day;
    }
}
