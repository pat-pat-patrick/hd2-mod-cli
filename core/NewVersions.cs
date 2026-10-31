using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>Whether a mod being added is a new version of one in the library (the user,
    /// 2026-11-01: dropping any mod's new version in should update it, not only MDL3's):
    /// - the same manifest Guid (Commands.Add) or the same download source, a Nexus Mods mod or
    ///   a GitHub repository: the same mod, put in its place at once;
    /// - only the name, without its version, date or the suffixes managers add: asked, since a
    ///   name alike is not proof.
    /// A mod whose manifest Guid differs from the new one's is another mod, whatever else matches.</summary>
    public static class NewVersions
    {
        /// <summary>What was found: the mod, and whether it is sure (the source) or asked (the name).</summary>
        public class Found
        {
            public Mod Mod;
            public bool Sure;
        }

        /// <summary>The mod in the library the new one (its folder, the file it came from, the name
        /// it would get) is a new version of, else null.</summary>
        public static Found Of(Library lib, string folder, string file, string name)
        {
            var guid = Guid(folder);
            var others = lib.Mods.Where(m => !SamePath(m.Path, folder) && (guid == null || Guid(m.Path) is var g && (g == null || g.Equals(guid, StringComparison.OrdinalIgnoreCase)))).ToList();
            var key = Key(name) ?? Key(file != null ? Path.GetFileNameWithoutExtension(file) : null);

            // the download source: the same Nexus Mods mod or GitHub repository
            if (file != null && File.Exists(file))
            {
                var probe = new Mod();
                Origins.FromFile(probe, file);
                var nexus = probe.Nexus?.Mod ?? Origins.NexusModOf(Path.GetFileNameWithoutExtension(file));
                var same = others.Where(m =>
                    (nexus > 0 && m.Nexus != null && m.Nexus.Mod == nexus && (probe.Nexus == null || SameGame(m.Nexus.Game, probe.Nexus.Game))) ||
                    (probe.GitHub != null && m.GitHub != null && string.Equals(m.GitHub.Owner, probe.GitHub.Owner, StringComparison.OrdinalIgnoreCase)
                                                             && string.Equals(m.GitHub.Repo, probe.GitHub.Repo, StringComparison.OrdinalIgnoreCase))).ToList();
                if (same.Count == 1)
                {
                    // one Nexus page can hold several files, each a mod of its own: when both
                    // file names name their file and the names differ, it is asked
                    var was = Key(Origins.NexusFileName(same[0].Source));
                    var now = Key(Origins.NexusFileName(file));
                    return new Found { Mod = same[0], Sure = was == null || now == null || was == now };
                }
                if (same.Count > 1)
                {
                    var named = same.Where(m => key != null && Names(m).Contains(key)).ToList();
                    if (named.Count == 1) return new Found { Mod = named[0], Sure = false };
                }
            }

            // the name, without what changes from version to version
            if (key == null) return null;
            var alike = others.Where(m => Names(m).Contains(key)).ToList();
            return alike.Count == 1 ? new Found { Mod = alike[0], Sure = false } : null;
        }

        static IEnumerable<string> Names(Mod m) =>
            new[] { m.Name, m.RealName, Path.GetFileName((m.Path ?? "").TrimEnd('\\', '/')) }.Select(Key).Where(k => k != null).Distinct();

        static bool SamePath(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        static bool SameGame(string a, string b) => a == null || b == null || a.Equals(b, StringComparison.OrdinalIgnoreCase);

        static string Guid(string folder)
        {
            try { return Manifest.Read(folder)?.Guid is string g && g.Trim().Length > 0 ? g.Trim() : null; }
            catch (Exception e) when (e is ModError || e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        static readonly Regex[] Changing =
        {
            new Regex(@"_(AR\d+|[0-9a-f]{12})$", RegexOptions.IgnoreCase),                                         // HD2 Arsenal's and this program's folder suffixes
            new Regex(@"\(\d+\)"),                                                                               // a browser's second download: "(1)"
            new Regex(@"(?<![\p{L}\d])v?\d+(?:\.\d+)+(?:[-+][0-9a-z]+(?:\.\d+)*)*(?![\p{L}\d])", RegexOptions.IgnoreCase),  // 1.2, v1.2.3, 3.0.0-dev.25
            new Regex(@"(?<![\p{L}\d])v\d+(?![\p{L}\d])", RegexOptions.IgnoreCase),                               // v2
            new Regex(@"(?<!\d)\d{4}-\d\d-\d\d(?!\d)"),                                                           // 2026-10-03
            new Regex(@"(?<!\d)(?:20)?\d{6}(?!\d)"),                                                             // 20261003, 260823
        };
        static readonly Regex Separators = new Regex(@"[\s_\-.,·+()（）\[\]【】]+");

        /// <summary>A mod's name without what changes from version to version: its version, a date,
        /// the suffixes managers add to folders, a browser's "(1)"; case and separators alike. Null
        /// when too little is left to tell mods apart.</summary>
        public static string Key(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var s = Origins.NexusFileName(name) ?? name;
            foreach (var r in Changing) s = r.Replace(s, " ");
            s = Separators.Replace(s, " ").Trim().ToLowerInvariant();
            return s.Length >= 3 ? s : null;
        }
    }
}
