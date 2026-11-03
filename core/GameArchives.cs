using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>Which of the game's own archives holds each resource, and HD2SDK's names of those
    /// archives (hashlists\archivehashes.json: category, archive hash, name; "Armor",
    /// "082f22454170efd6", "SR-24 Street Scout"). A resource a mod replaces is named by the game
    /// item it belongs to when the name list (ResourceNames) has no name for it (the user,
    /// 2026-11-03: a character mod's body models had none; all eight of one were found this way).
    /// The map is read from the game once and kept in game_archives.bin until bundles.nxa
    /// changes, and only when there are archive names to give.</summary>
    public static class GameArchives
    {
        /// <summary>Tests only: the archive names file, and the map, instead of the real ones.</summary>
        public static string NamesOverride;
        public static Dictionary<ulong, ulong[]> MapOverride;

        /// <summary>The list the Resource names plugin downloads beside the name list.</summary>
        public static string Downloaded => Store.PathOf(Path.Combine("resource_names", "archivehashes.json"));

        /// <summary>archivehashes.json: in HD2SDK_HASHLISTS, else the downloaded one, else the newest
        /// Blender's HD2SDK add-on; null when there is none.</summary>
        public static string NamesFile()
        {
            if (NamesOverride != null) return File.Exists(NamesOverride) ? NamesOverride : null;
            var env = Environment.GetEnvironmentVariable("HD2SDK_HASHLISTS");
            if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "archivehashes.json"))) return Path.Combine(env, "archivehashes.json");
            if (File.Exists(Downloaded)) return Downloaded;
            try
            {
                var blender = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blender Foundation", "Blender");
                if (!Directory.Exists(blender)) return null;
                return Directory.GetDirectories(blender)
                    .Select(v => Path.Combine(v, "scripts", "addons", "HD2SDK-CommunityEdition", "hashlists", "archivehashes.json"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        static Dictionary<ulong, (string Category, string Name)> names;
        static string namesFrom;
        static DateTime namesChecked;

        /// <summary>The archives' names by hash; looked for again at most every two seconds, read
        /// again when the file changed.</summary>
        public static Dictionary<ulong, (string Category, string Name)> Names()
        {
            if (names != null && NamesOverride == null && DateTime.UtcNow - namesChecked < TimeSpan.FromSeconds(2)) return names;
            var file = NamesFile();
            namesChecked = NamesOverride != null ? DateTime.MinValue : DateTime.UtcNow;
            string key;
            try { key = file == null ? "" : file + ":" + File.GetLastWriteTimeUtc(file).Ticks; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { key = ""; }
            if (names != null && namesFrom == key) return names;
            var d = new Dictionary<ulong, (string, string)>();
            if (file != null)
                try
                {
                    if (Json.Parse(File.ReadAllText(file)) is Dictionary<string, object> all)
                        foreach (var cat in all)
                            if (cat.Value is Dictionary<string, object> archives)
                                foreach (var a in archives)
                                    if (a.Value is string n && n.Length > 0 && ulong.TryParse(a.Key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) && !d.ContainsKey(h))
                                        d[h] = (cat.Key, n);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { }
            names = d;
            namesFrom = key;
            return d;
        }

        static Dictionary<ulong, ulong[]> map;
        static long mapStamp;
        const uint CacheMagic = 0x32524147;          // "GAR2": up to eight archives a resource

        /// <summary>Each resource's game archives (at most eight), from game_archives.bin while
        /// bundles.nxa is unchanged, else read from the game (a few seconds, once a game update);
        /// null when the game folder has no bundles.nxa.</summary>
        public static Dictionary<ulong, ulong[]> Map(string game)
        {
            if (MapOverride != null) return MapOverride;
            if (game == null) return null;
            var data = Paths.Join(game, "data");
            var index = new FileInfo(Paths.Join(data, "bundles.nxa"));
            if (!index.Exists) return null;
            long stamp = index.Length ^ index.LastWriteTimeUtc.Ticks;
            if (map != null && mapStamp == stamp) return map;
            var cache = Store.PathOf("game_archives.bin");
            var kept = ReadCache(cache, stamp);
            if (kept == null)
            {
                try { kept = ReadGame(data); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is IndexOutOfRangeException)
                {
                    ManagerLog.Warn("the game's archives could not be read: " + e.Message);
                    return null;
                }
                WriteCache(cache, stamp, kept);
            }
            map = kept;
            mapStamp = stamp;
            return map;
        }

        static Dictionary<ulong, ulong[]> ReadGame(string data)
        {
            var found = new Dictionary<ulong, List<ulong>>();
            using (var g = new GameBundles(data))
                foreach (var pkg in g.Packages.Where(n => n.IndexOf('.') < 0))
                {
                    if (!ulong.TryParse(pkg, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var archive)) continue;
                    var pre = g.Read(pkg, 0, 12);
                    if (pre.Length < 12 || GameBundles.U32(pre, 0) != 0xF0000011) continue;
                    uint tc = GameBundles.U32(pre, 4), fc = GameBundles.U32(pre, 8);
                    if (tc > 4096 || fc > 200000) continue;
                    int b = 0x48 + (int)tc * 32;
                    var head = g.Read(pkg, 0, b + (int)fc * 80);
                    for (int i = 0; i < fc && b + i * 80 + 8 <= head.Length; i++)
                    {
                        ulong fid = GameBundles.U64(head, b + i * 80);
                        if (!found.TryGetValue(fid, out var list)) found[fid] = list = new List<ulong>();
                        if (list.Count < 8 && !list.Contains(archive)) list.Add(archive);
                    }
                }
            return found.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        }

        static Dictionary<ulong, ulong[]> ReadCache(string path, long stamp)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadUInt32() != CacheMagic || r.ReadInt64() != stamp) return null;
                    int n = r.ReadInt32();
                    var d = new Dictionary<ulong, ulong[]>(n);
                    for (int i = 0; i < n; i++)
                    {
                        ulong fid = r.ReadUInt64();
                        var a = new ulong[r.ReadByte()];
                        for (int j = 0; j < a.Length; j++) a[j] = r.ReadUInt64();
                        d[fid] = a;
                    }
                    return d;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        static void WriteCache(string path, long stamp, Dictionary<ulong, ulong[]> d)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var w = new BinaryWriter(File.Create(path + ".tmp")))
                {
                    w.Write(CacheMagic);
                    w.Write(stamp);
                    w.Write(d.Count);
                    foreach (var kv in d)
                    {
                        w.Write(kv.Key);
                        w.Write((byte)kv.Value.Length);
                        foreach (var a in kv.Value) w.Write(a);
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>The named game archives a resource is in: every one of them, since the game
        /// shares a resource between items (one body model in two armour sets: replacing it changes
        /// both, 2026-11-03); empty when there are no archive names or none of its archives is named.</summary>
        public static List<(string Category, string Name)> Of(ulong id, string game)
        {
            var n = Names();
            if (n.Count == 0) return new List<(string, string)>();
            var m = Map(game);
            if (m == null || !m.TryGetValue(id, out var archives)) return new List<(string, string)>();
            return archives.Where(n.ContainsKey).Select(a => n[a]).Distinct().ToList();
        }

        /// <summary>What a mod's patch archives replace, by game item: the named archive of each
        /// resource the game has, the items with most resources first, at most `most`.</summary>
        public static List<(string Category, string Name)> Replaces(IEnumerable<string> archives, string game, int most = 6)
        {
            var n = Names();
            if (n.Count == 0) return new List<(string, string)>();
            var m = Map(game);
            if (m == null) return new List<(string, string)>();
            var count = new Dictionary<(string, string), int>();
            foreach (var src in archives)
            {
                List<(ulong Type, ulong Id)> keys;
                try { keys = Deploy.Resources(src); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { continue; }
                foreach (var k in keys.Select(k => k.Id).Distinct())
                    if (m.TryGetValue(k, out var a))
                        foreach (var named in a.Where(n.ContainsKey).Select(h => n[h]).Distinct())
                            count[named] = (count.TryGetValue(named, out var c) ? c : 0) + 1;
            }
            return count.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Item2, StringComparer.Ordinal).Take(most).Select(kv => kv.Key).ToList();
        }
    }
}
