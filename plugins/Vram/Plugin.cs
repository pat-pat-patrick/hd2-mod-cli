using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod.Vram
{
    /// <summary>Asset check: the textures and models each deployed mod brings, next to the
    /// game's own copies they replace. Whether they are built as well as the game's (detail
    /// levels, triangles, shadows, culling boxes, lights, mipmaps, compression, streaming:
    /// hd2_diag asset_cost_scan.py's rules, as asset_check_src/core.js has them), and what they
    /// take on the graphics card. From the files, not measured: what is loaded at all depends on
    /// the mission and the settings.
    ///
    /// A window plugin: the manager lists it on its plugins page and calls Open. Called by name
    /// (core\Plugins.cs, PluginFiles), so every method takes and gives strings, numbers, bools,
    /// delegates and the Json shapes only.</summary>
    public static partial class Plugin
    {
        /// <summary>Raised when the calls change shape; the manager refuses a plugin of another Api.</summary>
        public const int Api = 1;
        /// <summary>"window": the plugin has a window of its own, opened from its card.</summary>
        public const string Kind = "window";

        public static string Title(bool zh) => zh ? "资源检查" : "Asset check";

        public static string Summary(bool zh) => zh
            ? "检查各已启用 MOD 的模型与贴图的优化情况（细节层级、三角面数、阴影、剔除盒、灯光、mipmap、压缩），并估算其显存占用，与所替换的原版资源对比。"
            : "Checks how well each enabled mod's models and textures are optimized (detail levels, triangle counts, shadows, culling boxes, lights, mipmaps, compression) and estimates their video memory use against the game resources they replace.";

        public const ulong Unit = 0xE0A48D0BE9A7453F, Texture = 0xCD4238C6A0C69E32, Physics = 0x5F7203C8F280DAB8;
        const string CacheName = "vram_game_table.bin";
        const uint CacheMagic = 0x33525648;          // "HVR3": with mips and the models' summaries

        // -- the game's own copies ------------------------------------------------------

        /// <summary>A resource as the game or a mod ships it: its GPU bytes resident and
        /// streamed, and for a texture its size, DXGI format, layers and mips (0 for a model).</summary>
        public struct Item
        {
            public uint Resident, Streamed;
            public ushort W, H, Format, Layers, Mips;
            public ulong Max => (ulong)Resident + Streamed;
        }

        /// <summary>The game's copies: every texture and model, each model's summary (unreadable
        /// ones listed), and the path the game's physics resource of the same id names (a model's
        /// only readable name).</summary>
        sealed class Copies
        {
            public readonly Dictionary<(ulong, ulong), Item> Items = new Dictionary<(ulong, ulong), Item>();
            public readonly Dictionary<ulong, UnitSummary> Units = new Dictionary<ulong, UnitSummary>();
            public readonly HashSet<ulong> Unreadable = new HashSet<ulong>();
            public readonly Dictionary<ulong, string> Paths = new Dictionary<ulong, string>();
        }

        /// <summary>Every texture and model the game ships, by (id, type).</summary>
        public static Dictionary<(ulong, ulong), Item> GameTable(string home, string data, Action<double> progress = null) =>
            Load(home, data, progress).Items;

        /// <summary>The game's copies, from the cache in home while bundles.nxa is unchanged; else
        /// read from the game (a few seconds) and cached.</summary>
        static Copies Load(string home, string data, Action<double> progress)
        {
            var index = new FileInfo(Path.Combine(data, "bundles.nxa"));
            if (!index.Exists) throw new FileNotFoundException("bundles.nxa is not in " + data);
            long stamp = index.Length ^ index.LastWriteTimeUtc.Ticks;
            var cache = Path.Combine(home, CacheName);
            var kept = ReadCache(cache, stamp);
            if (kept != null) return kept;
            var copies = ReadGame(data, progress);
            WriteCache(cache, stamp, copies);
            return copies;
        }

        static Copies ReadGame(string data, Action<double> progress)
        {
            var c = new Copies();
            using (var game = new Game(data))
            {
                var names = game.Packages.Where(n => n.IndexOf('.') < 0).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var rows = new List<(string Pkg, ulong Off, uint Size, ulong Fid, ulong Tid)>();
                var seen = new HashSet<(ulong, ulong)>();
                for (int n = 0; n < names.Count; n++)
                {
                    if (progress != null && n % 100 == 0) progress(0.5 * n / names.Count);
                    var pkg = names[n];
                    var pre = game.Read(pkg, 0, 12);
                    if (pre.Length < 12) continue;
                    uint magic = Game.U32(pre, 0), tc = Game.U32(pre, 4), fc = Game.U32(pre, 8);
                    if (magic != 0xF0000011 || tc > 4096 || fc > 100000) continue;
                    int b = 0x48 + (int)tc * 32;
                    var head = game.Read(pkg, 0, b + (int)fc * 80);
                    for (int i = 0; i < fc; i++)
                    {
                        int o = b + i * 80;
                        if (o + 80 > head.Length) break;
                        ulong tid = Game.U64(head, o + 8);
                        if (tid != Texture && tid != Unit && tid != Physics) continue;
                        ulong fid = Game.U64(head, o);
                        if (!seen.Add((fid, tid))) continue;           // the first package holding it, as the scan does
                        if (tid != Physics) c.Items[(fid, tid)] = new Item { Streamed = Game.U32(head, o + 60), Resident = Game.U32(head, o + 64) };
                        rows.Add((pkg, Game.U64(head, o + 16), Game.U32(head, o + 56), fid, tid));
                    }
                }
                // in package order, so a bundle's chunks are read once
                rows.Sort((x, y) => string.CompareOrdinal(x.Pkg, y.Pkg) != 0 ? string.CompareOrdinal(x.Pkg, y.Pkg) : x.Off.CompareTo(y.Off));
                for (int r = 0; r < rows.Count; r++)
                {
                    if (progress != null && r % 500 == 0) progress(0.5 + 0.5 * r / rows.Count);
                    var x = rows[r];
                    if (x.Tid == Texture)
                    {
                        var it = c.Items[(x.Fid, x.Tid)];
                        TextureHeader(game.Read(x.Pkg, x.Off, (int)Math.Min(x.Size, 340u)), ref it);
                        c.Items[(x.Fid, x.Tid)] = it;
                    }
                    else if (x.Tid == Unit)
                    {
                        try { c.Units[x.Fid] = UnitSummary.Of(new UnitData(game.Read(x.Pkg, x.Off, (int)x.Size))); }
                        catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException) { c.Unreadable.Add(x.Fid); }
                    }
                }
                // the models' names: their physics resource's path, a C string at 32
                foreach (var x in rows)
                {
                    if (x.Tid != Physics || !c.Items.ContainsKey((x.Fid, Unit)) || x.Size <= 32) continue;
                    var b = game.Read(x.Pkg, x.Off, (int)Math.Min(x.Size, 32u + 512));
                    int e = 32;
                    while (e < b.Length && b[e] != 0) e++;
                    if (e < b.Length && e > 32) c.Paths[x.Fid] = Encoding.GetEncoding(28591).GetString(b, 32, e - 32);
                }
            }
            progress?.Invoke(1);
            return c;
        }

        /// <summary>A texture's main bytes: 3 x u32, 15 mip records of 12 bytes, then a 148-byte
        /// DDS header with a DX10 part (hd2_diag asset_cost_scan.py, tex_info).</summary>
        static void TextureHeader(byte[] main, ref Item it)
        {
            if (main.Length < 192 + 148) return;
            if (Encoding.ASCII.GetString(main, 192, 4) != "DDS " || Encoding.ASCII.GetString(main, 192 + 84, 4) != "DX10") return;
            it.H = (ushort)Math.Min(Game.U32(main, 192 + 12), 65535);
            it.W = (ushort)Math.Min(Game.U32(main, 192 + 16), 65535);
            it.Mips = (ushort)Math.Max(1u, Math.Min(Game.U32(main, 192 + 28), 65535));
            it.Format = (ushort)Math.Min(Game.U32(main, 192 + 128), 65535);
            // an array's layers, six faces each for a cube (DX10 miscFlag 4)
            uint layers = Math.Max(1u, Game.U32(main, 192 + 140)) * ((Game.U32(main, 192 + 136) & 4) != 0 ? 6u : 1u);
            it.Layers = (ushort)Math.Min(layers, 65535u);
        }

        static Copies ReadCache(string path, long stamp)
        {
            try
            {
                using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
                {
                    if (r.ReadUInt32() != CacheMagic || r.ReadInt64() != stamp) return null;
                    var c = new Copies();
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        var key = (r.ReadUInt64(), r.ReadUInt64());
                        c.Items[key] = new Item
                        {
                            Resident = r.ReadUInt32(), Streamed = r.ReadUInt32(), W = r.ReadUInt16(), H = r.ReadUInt16(),
                            Format = r.ReadUInt16(), Layers = r.ReadUInt16(), Mips = r.ReadUInt16(),
                        };
                    }
                    n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        ulong fid = r.ReadUInt64();
                        var s = new UnitSummary { Box = r.ReadSingle() };
                        int k = r.ReadInt32();
                        for (int j = 0; j < k; j++) s.Strides.Add(r.ReadUInt32());
                        k = r.ReadInt32();
                        s.Lights.AddRange(r.ReadBytes(k));
                        k = r.ReadInt32();
                        for (int j = 0; j < k; j++) s.Levels.Add((r.ReadUInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadString()));
                        c.Units[fid] = s;
                    }
                    n = r.ReadInt32();
                    for (int i = 0; i < n; i++) c.Unreadable.Add(r.ReadUInt64());
                    n = r.ReadInt32();
                    for (int i = 0; i < n; i++) c.Paths[r.ReadUInt64()] = r.ReadString();
                    return c;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        static void WriteCache(string path, long stamp, Copies c)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var w = new BinaryWriter(File.Create(path + ".tmp"), Encoding.UTF8))
                {
                    w.Write(CacheMagic);
                    w.Write(stamp);
                    w.Write(c.Items.Count);
                    foreach (var kv in c.Items)
                    {
                        w.Write(kv.Key.Item1); w.Write(kv.Key.Item2);
                        var v = kv.Value;
                        w.Write(v.Resident); w.Write(v.Streamed); w.Write(v.W); w.Write(v.H); w.Write(v.Format); w.Write(v.Layers); w.Write(v.Mips);
                    }
                    w.Write(c.Units.Count);
                    foreach (var kv in c.Units)
                    {
                        w.Write(kv.Key);
                        var s = kv.Value;
                        w.Write(s.Box);
                        w.Write(s.Strides.Count); foreach (var x in s.Strides) w.Write(x);
                        w.Write(s.Lights.Count); w.Write(s.Lights.ToArray());
                        w.Write(s.Levels.Count);
                        foreach (var l in s.Levels) { w.Write(l.MeshId); w.Write(l.Tris); w.Write(l.Secs); w.Write(l.Chain); }
                    }
                    w.Write(c.Unreadable.Count); foreach (var x in c.Unreadable) w.Write(x);
                    w.Write(c.Paths.Count); foreach (var kv in c.Paths) { w.Write(kv.Key); w.Write(kv.Value); }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }      // read again next time
        }

        // -- the mods ----------------------------------------------------------------------

        /// <summary>A texture's or model's row in a patch archive: where its main and GPU bytes are.</summary>
        struct Row
        {
            public string Archive;
            public ulong Fid, Tid, Off, Goff;
            public uint Fsz, Ssz, Gsz;
        }

        /// <summary>The textures and models in one patch archive (the 80-byte file rows after the
        /// header and the 32-byte type rows).</summary>
        static List<Row> ArchiveRows(string path)
        {
            var out_ = new List<Row>();
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var head = new byte[12];
                if (f.Read(head, 0, 12) < 12 || Game.U32(head, 0) != 0xF0000011) return out_;
                uint tc = Game.U32(head, 4), fc = Game.U32(head, 8);
                if (tc > 4096 || fc > 100000) return out_;
                var rows = new byte[fc * 80];
                f.Seek(0x48 + tc * 32L, SeekOrigin.Begin);
                if (f.Read(rows, 0, rows.Length) < rows.Length) return out_;
                for (int i = 0; i < fc; i++)
                {
                    int o = i * 80;
                    ulong tid = Game.U64(rows, o + 8);
                    if (tid != Texture && tid != Unit) continue;
                    out_.Add(new Row
                    {
                        Archive = path, Fid = Game.U64(rows, o), Tid = tid, Off = Game.U64(rows, o + 16), Goff = Game.U64(rows, o + 32),
                        Fsz = Game.U32(rows, o + 56), Ssz = Game.U32(rows, o + 60), Gsz = Game.U32(rows, o + 64),
                    });
                }
            }
            return out_;
        }

        static byte[] Slice(string path, ulong off, uint size)
        {
            if (size == 0 || !File.Exists(path)) return null;
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if ((ulong)f.Length < off + size) return null;
                var b = new byte[size];
                f.Seek((long)off, SeekOrigin.Begin);
                int got = 0;
                while (got < b.Length) { int n = f.Read(b, got, b.Length - got); if (n <= 0) return null; got += n; }
                return b;
            }
        }

        /// <summary>A model the game may draw many of at once, by its path: an enemy or a map object.</summary>
        static string Many(string path)
        {
            if (path == null) return null;
            if (Regex.IsMatch(path, @"(^|/)cha_|cyborg|terminid|illuminate|automaton|(^|/)il_|(^|/)bug_", RegexOptions.IgnoreCase)) return "enemy";
            if (Regex.IsMatch(path, @"(^|/)cy_|structures|scrap|(^|/)prop", RegexOptions.IgnoreCase)) return "object";
            return null;
        }

        /// <summary>Findings that cost frames or memory for nothing (asset_check_src page.html, WASTE).</summary>
        static readonly string[] Waste = { "lod_flat", "shadow_heavy", "tris_heavy", "lod_missing", "aabb_inflated", "light_shadow",
                                           "light_volumetric", "tex_no_mips", "tex_uncompressed", "tex_not_streamed", "tex_big_resident" };

        /// <summary>The check. mods: in load order (the last to load wins a resource two mods ship),
        /// each {name, archives: [patch archive paths it deploys]}. Returns {mods: [...], total},
        /// each mod {name, level ("many": a heavier model the game may draw many of; "waste": some
        /// finding of Waste; "fine"), max, resident, game_max, extra, textures, models, new, lost,
        /// flags: {flag: count}, items: [...]}; each item {type, id, path, many, flags, facts
        /// (models), w, h, layers, mips, format, max, resident, in_game, game_max, game_w, game_h,
        /// game_layers, game_format}. Bytes as numbers; extra: max minus the game's copies it
        /// replaces; lost: resources a later mod overrides, left out of the rest.</summary>
        public static Dictionary<string, object> Estimate(string home, string data, List<Dictionary<string, object>> mods, Action<double> progress = null)
        {
            var game = Load(home, data, progress == null ? (Action<double>)null : p => progress(0.6 * p));
            var per = new List<(string Name, List<Row> Rows)>();
            var winner = new Dictionary<(ulong, ulong), int>();
            for (int m = 0; m < mods.Count; m++)
            {
                var rows = new List<Row>();
                foreach (var a in (mods[m].TryGetValue("archives", out var v) ? v as List<object> : null) ?? new List<object>())
                    if (a is string path && File.Exists(path))
                        try { rows.AddRange(ArchiveRows(path)); }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                foreach (var r in rows) winner[(r.Fid, r.Tid)] = m;
                per.Add((mods[m].TryGetValue("name", out var n) ? n as string : "?", rows));
            }

            var list = new List<object>();
            long tMax = 0, tResident = 0, tGame = 0;
            for (int m = 0; m < per.Count; m++)
            {
                progress?.Invoke(0.6 + 0.4 * m / Math.Max(1, per.Count));
                var (name, rows) = per[m];
                long max = 0, resident = 0, gameMax = 0;
                int textures = 0, models = 0, fresh = 0, lost = 0;
                var flagCount = new Dictionary<string, object>();
                bool many = false;
                // the last row of a resource in the mod's own archives is the one that loads
                var mine = new Dictionary<(ulong, ulong), Row>();
                foreach (var r in rows) mine[(r.Fid, r.Tid)] = r;
                var items = new List<Dictionary<string, object>>();
                foreach (var kv in mine)
                {
                    if (winner[kv.Key] != m) { lost++; continue; }
                    var r = kv.Value;
                    bool known = game.Items.TryGetValue(kv.Key, out var g);
                    var it = new Item { Resident = r.Gsz, Streamed = r.Ssz };
                    var item = new Dictionary<string, object> { ["id"] = r.Fid.ToString("x16"), ["in_game"] = known };
                    List<string> flags;
                    if (r.Tid == Texture)
                    {
                        var main = Slice(r.Archive, r.Off, Math.Min(r.Fsz, 340u));
                        if (main != null) TextureHeader(main, ref it);
                        flags = it.W > 0 ? Checks.Texture(it, known, g) : new List<string>();
                        textures++;
                        item["type"] = "texture";
                        item["w"] = (long)it.W; item["h"] = (long)it.H; item["format"] = FormatName(it.Format);
                        item["layers"] = (long)Math.Max((ushort)1, it.Layers); item["mips"] = (long)it.Mips;
                        item["game_w"] = (long)g.W; item["game_h"] = (long)g.H; item["game_format"] = known ? FormatName(g.Format) : null;
                        item["game_layers"] = (long)Math.Max((ushort)1, g.Layers);
                    }
                    else
                    {
                        models++;
                        item["type"] = "model";
                        game.Paths.TryGetValue(r.Fid, out var path);
                        item["path"] = path;
                        var hint = Many(path);
                        item["many"] = hint;
                        flags = new List<string>();
                        try
                        {
                            var main = Slice(r.Archive, r.Off, r.Fsz);
                            if (main == null) throw new IndexOutOfRangeException("the model's bytes are not all there");
                            var mu = new UnitData(main);
                            if (game.Unreadable.Contains(r.Fid)) item["error"] = "the game's copy could not be read";
                            else
                            {
                                game.Units.TryGetValue(r.Fid, out var vs);
                                var (f, facts) = Checks.Unit(mu, Slice(r.Archive + ".gpu_resources", r.Goff, r.Gsz), vs);
                                flags = f;
                                item["facts"] = facts;
                                if (hint != null && (f.Contains("lod_flat") || f.Contains("tris_heavy") || f.Contains("shadow_heavy"))) many = true;
                            }
                        }
                        catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is IOException)
                        {
                            item["error"] = e.Message;
                        }
                    }
                    if (!known) fresh++;
                    max += (long)it.Max; resident += it.Resident; gameMax += (long)g.Max;
                    item["flags"] = flags.Cast<object>().ToList();
                    item["max"] = (long)it.Max; item["resident"] = (long)it.Resident; item["game_max"] = known ? (long)g.Max : 0L;
                    foreach (var fl in flags) flagCount[fl] = (flagCount.TryGetValue(fl, out var c0) ? (long)c0 : 0L) + 1;
                    items.Add(item);
                }
                tMax += max; tResident += resident; tGame += gameMax;
                bool waste = flagCount.Keys.Any(k => Waste.Contains(k));
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = name, ["level"] = many ? "many" : waste ? "waste" : "fine",
                    ["max"] = max, ["resident"] = resident, ["game_max"] = gameMax, ["extra"] = max - gameMax,
                    ["textures"] = (long)textures, ["models"] = (long)models, ["new"] = (long)fresh, ["lost"] = (long)lost,
                    ["flags"] = flagCount,
                    ["items"] = items.OrderByDescending(x => (long)x["max"] - (long)x["game_max"]).Cast<object>().ToList(),
                });
            }
            progress?.Invoke(1);
            return new Dictionary<string, object>
            {
                ["mods"] = list,
                ["total"] = new Dictionary<string, object> { ["max"] = tMax, ["resident"] = tResident, ["game_max"] = tGame, ["extra"] = tMax - tGame },
                ["game_resources"] = (long)game.Items.Count,
            };
        }

        static readonly Dictionary<int, string> Dxgi = new Dictionary<int, string>
        {
            [2] = "RGBA32F", [10] = "RGBA16F", [11] = "RGBA16", [24] = "RGB10A2", [26] = "R11G11B10F", [28] = "RGBA8",
            [29] = "RGBA8_SRGB", [41] = "R32F", [49] = "RG8", [54] = "R16F", [56] = "R16", [61] = "R8", [65] = "A8",
            [71] = "BC1", [72] = "BC1_SRGB", [74] = "BC2", [75] = "BC2_SRGB", [77] = "BC3", [78] = "BC3_SRGB", [80] = "BC4",
            [81] = "BC4S", [83] = "BC5", [84] = "BC5S", [87] = "BGRA8", [91] = "BGRA8_SRGB", [95] = "BC6H", [96] = "BC6HS",
            [98] = "BC7", [99] = "BC7_SRGB",
        };

        internal static string FormatName(ushort f) => f == 0 ? null : Dxgi.TryGetValue(f, out var s) ? s : f.ToString();
    }
}
