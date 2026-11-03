using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>Options for a mod that has none of its own and keeps its patch files in two or
    /// more folders, none at its top: an old mod with no manifest.json (the user, 2026-11-04:
    /// every folder was deployed, and variants of one item overrode each other with no choice).
    /// Folders that replace some of the same resources are variants, one option to choose one of
    /// them; a folder that shares none is a part, an option of its own, on by default. Which is
    /// which is read from the resources in the patch files, never guessed from the names.</summary>
    public static class FolderLayout
    {
        // each mod folder's options while its patch files are unchanged
        static readonly Dictionary<string, (string Sig, List<ManifestOption> Options)> kept =
            new Dictionary<string, (string, List<ManifestOption>)>(StringComparer.OrdinalIgnoreCase);

        public static List<ManifestOption> Of(string folder)
        {
            var none = new List<ManifestOption>();
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return none;
                if (Deploy.List(folder).Any(f => Deploy.Patch.IsMatch(f))) return none;      // files at the top: the whole folder, as before
                var found = new List<string>();
                Deploy.Walk(folder, found);
                if (found.Count < 2) return none;
                var patches = found.Select(f => Deploy.List(f).Where(n => Deploy.Patch.IsMatch(n)).Select(n => Paths.Join(f, n)).ToList()).ToList();
                var sig = string.Join("|", patches.SelectMany(p => p).Select(p =>
                {
                    var fi = new FileInfo(p);
                    return p + ":" + fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
                }));
                lock (kept) if (kept.TryGetValue(folder, out var k) && k.Sig == sig) return k.Options;
                var options = Build(folder, found, patches);
                lock (kept) kept[folder] = (sig, options);
                return options;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return none; }
        }

        static List<ManifestOption> Build(string folder, List<string> found, List<List<string>> patches)
        {
            int n = found.Count;
            // variants: folders joined by a resource they replace with other bytes, directly or
            // through another. One they all ship the same bytes of is no choice: per-armour parts
            // of one body each carry the shared mesh (a player's Bunny Body_Common, 2026-11-04)
            var wanted = new List<Wanted>();
            for (int i = 0; i < n; i++)
                foreach (var p in patches[i]) wanted.Add(new Wanted { Name = Path.GetFileName(p), Src = p, Mod = i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            var root = Enumerable.Range(0, n).ToArray();
            int Find(int x) { while (root[x] != x) x = root[x] = root[root[x]]; return x; }
            foreach (var c in Deploy.Clashes(wanted).Where(c => !c.Same))
            {
                var ms = c.Mods.Select(m => int.Parse(m, System.Globalization.CultureInfo.InvariantCulture)).ToList();
                for (int k = 1; k < ms.Count; k++) root[Find(ms[k])] = Find(ms[0]);
            }

            // where each folder is, from the mod's top ("Pack\Red"), and its words without what
            // they all start with ("Red")
            var rel = found.Select(f => f.Substring(folder.Length).Trim('\\', '/')).ToList();
            var parts = rel.Select(r => r.Split('\\', '/')).ToList();
            int common = 0;
            while (parts.All(p => p.Length > common + 1) && parts.All(p => string.Equals(p[common], parts[0][common], StringComparison.OrdinalIgnoreCase))) common++;
            var words = parts.Select(p => p.Skip(common).ToArray()).ToList();

            var options = new List<ManifestOption>();
            var used = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            string Unique(string name)
            {
                var s = name;
                for (int k = 2; !used.Add(s); k++) s = name + " (" + k + ")";
                return s;
            }
            foreach (var group in Enumerable.Range(0, n).GroupBy(Find).OrderBy(g => g.Min()))
            {
                var idx = group.OrderBy(i => i).ToList();
                if (idx.Count == 1)
                {
                    int i = idx[0];
                    options.Add(new ManifestOption { Name = Unique(string.Join("/", words[i])), Include = new List<string> { rel[i] } });
                    continue;
                }
                // a choice: named by the folder the variants share, else by the variants themselves
                int shared = 0;
                while (idx.All(i => words[i].Length > shared + 1) && idx.All(i => string.Equals(words[i][shared], words[idx[0]][shared], StringComparison.OrdinalIgnoreCase))) shared++;
                var subs = idx.Select(i => new ManifestOption { Name = string.Join("/", words[i].Skip(shared)), Include = new List<string> { rel[i] } }).ToList();
                var name = shared > 0 ? string.Join("/", words[idx[0]].Take(shared))
                                      : string.Join(" / ", subs.Take(3).Select(s => s.Name)) + (subs.Count > 3 ? " …" : "");
                options.Add(new ManifestOption { Name = Unique(name), SubOptions = subs });
            }
            return options;
        }
    }
}
