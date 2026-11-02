using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Hd2Mod.Vram
{
    /// <summary>VRAM estimate: what the textures and models of each mod deployed take on the
    /// graphics card, next to the game's own copy they replace. An estimate from the files, not a
    /// measurement: a texture's resident part (.gpu_resources) is on the card while it is loaded,
    /// its streamed part (.stream, the finer mips) only as far as the game streams it in; what
    /// is loaded at all depends on the mission and the settings.
    ///
    /// A window plugin: the manager lists it on its plugins page and calls Open. Called by name
    /// (core\Plugins.cs, the window's PluginHost), so every method takes and gives strings,
    /// numbers, bools, delegates and the Json shapes only.</summary>
    public static partial class Plugin
    {
        /// <summary>Raised when the calls change shape; the manager refuses a plugin of another Api.</summary>
        public const int Api = 1;
        /// <summary>"window": the plugin has a window of its own, opened from its card.</summary>
        public const string Kind = "window";

        public static string Title(bool zh) => zh ? "显存估算" : "VRAM estimate";

        public static string Summary(bool zh) => zh
            ? "估算每个已启用 MOD 的贴图和模型在显卡上占用多少显存，并与它替换的原版资源对比。"
            : "Estimates how much video memory each enabled mod's textures and models take, next to the game's own copies they replace.";

        public const ulong Unit = 0xE0A48D0BE9A7453F, Texture = 0xCD4238C6A0C69E32;
        const string CacheName = "vram_game_table.bin";
        const uint CacheMagic = 0x32525648;          // "HVR2": with the layer count

        // -- the game's own copies ------------------------------------------------------

        /// <summary>A resource as the game or a mod ships it: its GPU bytes resident and
        /// streamed, and for a texture its size and DXGI format (0 for a model).</summary>
        public struct Item
        {
            public uint Resident, Streamed;
            public ushort W, H, Format, Layers;
            public ulong Max => (ulong)Resident + Streamed;
        }

        /// <summary>Every texture and model the game ships, by (id, type), from the cache in home
        /// while bundles.nxa is unchanged; else read from the game (about 10 s) and cached.</summary>
        public static Dictionary<(ulong, ulong), Item> GameTable(string home, string data, Action<double> progress = null)
        {
            var index = new FileInfo(Path.Combine(data, "bundles.nxa"));
            if (!index.Exists) throw new FileNotFoundException("bundles.nxa is not in " + data);
            long stamp = index.Length ^ index.LastWriteTimeUtc.Ticks;
            var cache = Path.Combine(home, CacheName);
            var kept = ReadCache(cache, stamp);
            if (kept != null) return kept;
            var table = ReadGame(data, progress);
            WriteCache(cache, stamp, table);
            return table;
        }

        static Dictionary<(ulong, ulong), Item> ReadGame(string data, Action<double> progress)
        {
            var found = new Dictionary<(ulong, ulong), Item>();
            using (var game = new Game(data))
            {
                var names = game.Packages.Where(n => n.IndexOf('.') < 0).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var textures = new List<(string Pkg, ulong Off, uint Size, (ulong, ulong) Key)>();
                for (int n = 0; n < names.Count; n++)
                {
                    if (progress != null && n % 100 == 0) progress(0.8 * n / names.Count);
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
                        if (tid != Texture && tid != Unit) continue;
                        var key = (Game.U64(head, o), tid);
                        if (found.ContainsKey(key)) continue;          // the first package holding it, as the scan does
                        found[key] = new Item { Streamed = Game.U32(head, o + 60), Resident = Game.U32(head, o + 64) };
                        if (tid == Texture) textures.Add((pkg, Game.U64(head, o + 16), Game.U32(head, o + 56), key));
                    }
                }
                // each texture's header for its size and format, in package order
                for (int t = 0; t < textures.Count; t++)
                {
                    if (progress != null && t % 500 == 0) progress(0.8 + 0.2 * t / textures.Count);
                    var x = textures[t];
                    var it = found[x.Key];
                    TextureHeader(game.Read(x.Pkg, x.Off, (int)Math.Min(x.Size, 340u)), ref it);
                    found[x.Key] = it;
                }
            }
            progress?.Invoke(1);
            return found;
        }

        /// <summary>A texture's main bytes: 3 x u32, 15 mip records of 12 bytes, then a 148-byte
        /// DDS header with a DX10 part (hd2_diag asset_cost_scan.py, tex_info).</summary>
        static void TextureHeader(byte[] main, ref Item it)
        {
            if (main.Length < 192 + 148) return;
            if (Encoding.ASCII.GetString(main, 192, 4) != "DDS " || Encoding.ASCII.GetString(main, 192 + 84, 4) != "DX10") return;
            it.H = (ushort)Math.Min(Game.U32(main, 192 + 12), 65535);
            it.W = (ushort)Math.Min(Game.U32(main, 192 + 16), 65535);
            it.Format = (ushort)Math.Min(Game.U32(main, 192 + 128), 65535);
            // an array's layers, six faces each for a cube (DX10 miscFlag 4)
            uint layers = Math.Max(1u, Game.U32(main, 192 + 140)) * ((Game.U32(main, 192 + 136) & 4) != 0 ? 6u : 1u);
            it.Layers = (ushort)Math.Min(layers, 65535u);
        }

        static Dictionary<(ulong, ulong), Item> ReadCache(string path, long stamp)
        {
            try
            {
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadUInt32() != CacheMagic || r.ReadInt64() != stamp) return null;
                    int n = r.ReadInt32();
                    var t = new Dictionary<(ulong, ulong), Item>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var key = (r.ReadUInt64(), r.ReadUInt64());
                        t[key] = new Item { Resident = r.ReadUInt32(), Streamed = r.ReadUInt32(), W = r.ReadUInt16(), H = r.ReadUInt16(), Format = r.ReadUInt16(), Layers = r.ReadUInt16() };
                    }
                    return t;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        static void WriteCache(string path, long stamp, Dictionary<(ulong, ulong), Item> t)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var w = new BinaryWriter(File.Create(path + ".tmp")))
                {
                    w.Write(CacheMagic);
                    w.Write(stamp);
                    w.Write(t.Count);
                    foreach (var kv in t)
                    {
                        w.Write(kv.Key.Item1); w.Write(kv.Key.Item2);
                        w.Write(kv.Value.Resident); w.Write(kv.Value.Streamed);
                        w.Write(kv.Value.W); w.Write(kv.Value.H); w.Write(kv.Value.Format); w.Write(kv.Value.Layers);
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }      // read again next time
        }

        // -- the mods ----------------------------------------------------------------------

        /// <summary>The textures and models in one patch archive (the 80-byte file rows after the
        /// header and the 32-byte type rows), each texture's header read for its size.</summary>
        static List<((ulong, ulong) Key, Item It)> ArchiveItems(string path)
        {
            var out_ = new List<((ulong, ulong), Item)>();
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var head = new byte[12];
                if (f.Read(head, 0, 12) < 12 || Game.U32(head, 0) != 0xF0000011) return out_;
                uint tc = Game.U32(head, 4), fc = Game.U32(head, 8);
                if (tc > 4096 || fc > 100000) return out_;
                long b = 0x48 + tc * 32L;
                var rows = new byte[fc * 80];
                f.Seek(b, SeekOrigin.Begin);
                if (f.Read(rows, 0, rows.Length) < rows.Length) return out_;
                for (int i = 0; i < fc; i++)
                {
                    int o = i * 80;
                    ulong tid = Game.U64(rows, o + 8);
                    if (tid != Texture && tid != Unit) continue;
                    var it = new Item { Streamed = Game.U32(rows, o + 60), Resident = Game.U32(rows, o + 64) };
                    if (tid == Texture)
                    {
                        ulong off = Game.U64(rows, o + 16);
                        uint size = Math.Min(Game.U32(rows, o + 56), 340u);
                        if (off + size <= (ulong)f.Length)
                        {
                            var main = new byte[size];
                            f.Seek((long)off, SeekOrigin.Begin);
                            f.Read(main, 0, (int)size);
                            TextureHeader(main, ref it);
                        }
                    }
                    out_.Add(((Game.U64(rows, o), tid), it));
                }
            }
            return out_;
        }

        /// <summary>The estimate. mods: in load order (the last to load wins a resource two mods
        /// ship), each {name, archives: [patch archive paths it deploys]}. Returns
        /// {mods: [...], total: {...}}, each mod {name, max, resident, game_max, extra, textures,
        /// models, new, lost, items: [{type, id, w, h, format, max, resident, game_max, game_w,
        /// game_h, game_format}]}; bytes as numbers. max: resident + streamed; extra: max minus
        /// the game's copies it replaces (what it adds); lost: resources a later mod overrides,
        /// left out of the rest.</summary>
        public static Dictionary<string, object> Estimate(string home, string data, List<Dictionary<string, object>> mods, Action<double> progress = null)
        {
            var game = GameTable(home, data, progress == null ? (Action<double>)null : p => progress(0.9 * p));
            var per = new List<(string Name, List<((ulong, ulong) Key, Item It)> Items)>();
            var winner = new Dictionary<(ulong, ulong), int>();
            for (int m = 0; m < mods.Count; m++)
            {
                var items = new List<((ulong, ulong), Item)>();
                foreach (var a in (mods[m].TryGetValue("archives", out var v) ? v as List<object> : null) ?? new List<object>())
                    if (a is string path && File.Exists(path))
                        try { items.AddRange(ArchiveItems(path)); }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                foreach (var (key, _) in items) winner[key] = m;
                per.Add((mods[m].TryGetValue("name", out var n) ? n as string : "?", items));
            }
            progress?.Invoke(1);

            var rows = new List<object>();
            long tMax = 0, tResident = 0, tGame = 0;
            for (int m = 0; m < per.Count; m++)
            {
                var (name, items) = per[m];
                long max = 0, resident = 0, gameMax = 0;
                int textures = 0, models = 0, fresh = 0, lost = 0;
                var seen = new HashSet<(ulong, ulong)>();
                var list = new List<Dictionary<string, object>>();
                foreach (var (key, it) in items)
                {
                    if (!seen.Add(key)) continue;                          // one option's copy, counted once
                    if (winner[key] != m) { lost++; continue; }
                    game.TryGetValue(key, out var g);
                    bool known = game.ContainsKey(key);
                    if (!known) fresh++;
                    if (key.Item2 == Texture) textures++; else models++;
                    max += (long)it.Max; resident += it.Resident; gameMax += (long)g.Max;
                    list.Add(new Dictionary<string, object>
                    {
                        ["type"] = key.Item2 == Texture ? "texture" : "model", ["id"] = key.Item1.ToString("x16"),
                        ["w"] = (long)it.W, ["h"] = (long)it.H, ["format"] = Format(it.Format), ["layers"] = (long)Math.Max((ushort)1, it.Layers),
                        ["max"] = (long)it.Max, ["resident"] = (long)it.Resident,
                        ["game_max"] = known ? (long)g.Max : 0L, ["in_game"] = known,
                        ["game_w"] = (long)g.W, ["game_h"] = (long)g.H, ["game_format"] = known ? Format(g.Format) : null, ["game_layers"] = (long)Math.Max((ushort)1, g.Layers),
                    });
                }
                tMax += max; tResident += resident; tGame += gameMax;
                rows.Add(new Dictionary<string, object>
                {
                    ["name"] = name, ["max"] = max, ["resident"] = resident, ["game_max"] = gameMax, ["extra"] = max - gameMax,
                    ["textures"] = (long)textures, ["models"] = (long)models, ["new"] = (long)fresh, ["lost"] = (long)lost,
                    ["items"] = list.OrderByDescending(x => (long)x["max"] - (long)x["game_max"]).Cast<object>().ToList(),
                });
            }
            return new Dictionary<string, object>
            {
                ["mods"] = rows,
                ["total"] = new Dictionary<string, object> { ["max"] = tMax, ["resident"] = tResident, ["game_max"] = tGame, ["extra"] = tMax - tGame },
                ["game_resources"] = (long)game.Count,
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

        static string Format(ushort f) => f == 0 ? null : Dxgi.TryGetValue(f, out var s) ? s : f.ToString();
    }
}
