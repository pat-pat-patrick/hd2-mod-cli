using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>Where a mod came from, for update checks on its own channel (the user, 2026-10-24:
    /// "从 GitHub 下下来的，就从 GitHub 检查更新"): Nexus Mods, a GitHub release, or another
    /// site's page (no check there).</summary>
    public static class Origins
    {
        static readonly Regex GitHubRelease = new Regex(@"github\.com/([\w.-]+)/([\w.-]+)/releases/(?:download|tag)/([^/?#]+)", RegexOptions.IgnoreCase);
        static readonly Regex GitHubRepo = new Regex(@"github\.com/([\w.-]+)/([\w.-]+?)(?:\.git)?(?:[/?#]|$)", RegexOptions.IgnoreCase);
        static readonly Regex NexusPage = new Regex(@"nexusmods\.com/([\w]+)/mods/(\d+)", RegexOptions.IgnoreCase);
        // Nexus Mods' download names: "<name> <mod id> <version> <yyyy-mm-ddThh-mmZ> <code>.zip"
        // (as the browser saves them), or with dashes between the parts
        static readonly Regex NexusName = new Regex(@"^(?<name>.*?)[ -](?<mod>\d{2,7})[ -](?<ver>[^ ]+?)[ -](?<date>\d{4}-\d\d-\d\dT\d\d-\d\dZ)", RegexOptions.IgnoreCase);

        /// <summary>Origin fields set on a mod from a downloaded file: the address Windows kept with it
        /// (its Zone.Identifier: HostUrl, ReferrerUrl) and, for Nexus Mods, its file name.</summary>
        public static void FromFile(Mod mod, string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
            string zone = null;
            try { zone = Native.ReadStream(file, "Zone.Identifier"); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            if (zone == null) return;
            string host = Field(zone, "HostUrl"), referrer = Field(zone, "ReferrerUrl");
            foreach (var url in new[] { host, referrer })
            {
                var g = GitHubRelease.Match(url ?? "");
                if (g.Success) { mod.GitHub = new GitHubRef { Owner = g.Groups[1].Value, Repo = g.Groups[2].Value, Tag = Uri.UnescapeDataString(g.Groups[3].Value) }; return; }
            }
            if ((host ?? "").IndexOf("nexusmods.com", StringComparison.OrdinalIgnoreCase) >= 0 || (host ?? "").IndexOf("nexus-cdn", StringComparison.OrdinalIgnoreCase) >= 0
                || (referrer ?? "").IndexOf("nexusmods.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var r = new NexusRef { Game = Nexus.Game };
                var page = NexusPage.Match(referrer ?? "");
                if (page.Success) { r.Game = page.Groups[1].Value.ToLowerInvariant(); r.Mod = long.Parse(page.Groups[2].Value); }
                var n = NexusName.Match(Path.GetFileNameWithoutExtension(file));
                if (n.Success)
                {
                    if (r.Mod == 0) r.Mod = long.Parse(n.Groups["mod"].Value);
                    r.Version = n.Groups["ver"].Value;
                    if (DateTime.TryParseExact(n.Groups["date"].Value, "yyyy-MM-dd'T'HH-mm'Z'", CultureInfo.InvariantCulture,
                                               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
                        r.Time = (long)(when - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                }
                if (r.Mod > 0) { mod.Nexus = r; return; }
            }
            if (!string.IsNullOrEmpty(referrer) && referrer.StartsWith("http", StringComparison.OrdinalIgnoreCase)) mod.Page = referrer;
        }

        /// <summary>The file's own name in a Nexus Mods download name ("Super Holo HUD" in "Super Holo
        /// HUD 12345 1.2.0 2026-10-31T10-00Z 1-1761900000.zip"), from a path or a name; else null.</summary>
        public static string NexusFileName(string pathOrName)
        {
            if (string.IsNullOrEmpty(pathOrName)) return null;
            var stem = pathOrName.IndexOfAny(new[] { '\\', '/' }) >= 0 ? Path.GetFileNameWithoutExtension(pathOrName) : pathOrName;
            var n = NexusName.Match(stem);
            return n.Success && n.Groups["name"].Value.Trim().Length > 0 ? n.Groups["name"].Value.Trim() : null;
        }

        /// <summary>The Nexus Mods mod id in a download name, else 0.</summary>
        public static long NexusModOf(string name)
        {
            var n = NexusName.Match(name ?? "");
            return n.Success && long.TryParse(n.Groups["mod"].Value, out var id) ? id : 0;
        }

        static string Field(string zone, string key)
        {
            var m = Regex.Match(zone, "^" + key + @"=(.+)$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>An origin set by hand from an address: a Nexus mod page, a GitHub repository or
        /// release, else another page. False for text that is not an address.</summary>
        public static bool FromUrl(Mod mod, string url)
        {
            url = (url ?? "").Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
            var n = NexusPage.Match(url);
            if (n.Success)
            {
                mod.Nexus = new NexusRef { Game = n.Groups[1].Value.ToLowerInvariant(), Mod = long.Parse(n.Groups[2].Value) };
                mod.GitHub = null;
                mod.Page = null;
                return true;
            }
            var g = GitHubRelease.Match(url);
            var repo = GitHubRepo.Match(url);
            if (g.Success || repo.Success)
            {
                mod.GitHub = g.Success ? new GitHubRef { Owner = g.Groups[1].Value, Repo = g.Groups[2].Value, Tag = Uri.UnescapeDataString(g.Groups[3].Value) }
                                       : new GitHubRef { Owner = repo.Groups[1].Value, Repo = repo.Groups[2].Value };
                mod.Nexus = null;
                mod.Page = null;
                return true;
            }
            mod.Page = url;
            mod.Nexus = null;
            mod.GitHub = null;
            return true;
        }

        /// <summary>Mods with no origin yet get the one HD2 Arsenal recorded for the same folder (its
        /// nexusData): a library imported before origins were kept learns them. How many learnt.</summary>
        public static int FromArsenal(Library lib)
        {
            if (Store.ArsenalFile() == null) return 0;
            Library theirs;
            try { theirs = Ops.ImportArsenal().Lib; }
            catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException) { return 0; }
            var by = new Dictionary<string, NexusRef>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in theirs.Mods) if (m.Nexus != null) by[Paths.Abs(m.Path).TrimEnd('\\')] = m.Nexus;
            int n = 0;
            foreach (var m in lib.Mods)
                if (m.Nexus == null && m.GitHub == null && m.Page == null && by.TryGetValue(Paths.Abs(m.Path).TrimEnd('\\'), out var r)) { m.Nexus = r; n++; }
            return n;
        }

        // a version written in a name: 1.2, v0.16.1, V0.3.6 (not a date, not part of a longer number)
        static readonly Regex InName = new Regex(@"(?<![\dA-Za-z.])[vV]?(\d+(?:\.\d+)+)(?!\.?\d)");

        /// <summary>The mod's version, for the window, and where it was found: "nexus" or "github"
        /// (recorded with the download), "file" (a Nexus Mods download's name, kept as the
        /// folder's), "name" (the name it came with, not one the player gave it), "folder"; nulls
        /// when none says it. One exception to that order: when the installed folder carries a
        /// version that is not the recorded one, the files were replaced since the download, and
        /// the folder is what is installed (the user, 2026-10-25: Boot Mod Loader 0.9.0 installed
        /// from a build of its own, 0.7.0 shown from the Nexus Mods record). Update checks do not
        /// use this. The manifest's "Version" is its format's version (always 1), not the mod's.</summary>
        public static (string Version, string From) VersionOf(Mod m)
        {
            var folder = Path.GetFileName((m.Path ?? "").TrimEnd('\\', '/'));
            var n = NexusName.Match(folder);
            var f = n.Success ? null : InName.Match(folder);
            string inFolder = n.Success ? n.Groups["ver"].Value : f.Success ? f.Groups[1].Value : null;
            string fromFolder = n.Success ? "file" : "folder";
            // a Nexus Mods version is a record of the file downloaded only with that file's id or
            // its time: without either it was the page's newest, written in by an older match
            var downloaded = m.Nexus != null && (m.Nexus.File > 0 || m.Nexus.Time > 0);
            var recorded = downloaded && !string.IsNullOrWhiteSpace(m.Nexus.Version) ? (m.Nexus.Version.Trim(), "nexus")
                         : !string.IsNullOrWhiteSpace(m.GitHub?.Tag) ? (m.GitHub.Tag.Trim(), "github")
                         : ((string)null, (string)null);
            if (recorded.Item1 != null)
                return inFolder != null && !SameVersion(inFolder, recorded.Item1) ? (inFolder, fromFolder) : recorded;
            if (n.Success) return (inFolder, "file");
            var x = InName.Match(m.RealName ?? m.Name ?? "");
            if (x.Success) return (x.Groups[1].Value, "name");
            if (inFolder != null) return (inFolder, "folder");
            return (null, null);
        }

        /// <summary>Two ways of writing one version: "v2.0" and "2.0".</summary>
        static bool SameVersion(string a, string b) =>
            string.Equals(a.TrimStart('v', 'V'), b.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

        /// <summary>The channel's name and the mod's page there, for the window; nulls without one.</summary>
        public static (string Channel, string Page) Of(Mod m)
        {
            if (m.Nexus != null) return ("Nexus Mods", Nexus.Page(m.Nexus));
            if (m.GitHub != null) return ("GitHub", "https://github.com/" + m.GitHub.Owner + "/" + m.GitHub.Repo + "/releases");
            if (m.Page != null) return (Uri.TryCreate(m.Page, UriKind.Absolute, out var u) ? u.Host : m.Page, m.Page);
            return (null, null);
        }
    }
}
