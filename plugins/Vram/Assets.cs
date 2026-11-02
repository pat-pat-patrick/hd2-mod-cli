using System;
using System.Collections.Generic;
using System.Linq;

namespace Hd2Mod.Vram
{
    /// <summary>A model (unit) as the checks need it: its vertex streams and its meshes with their
    /// sections, culling boxes, chains and LOD index; its lights. A port of hd2_diag
    /// asset_check_src/core.js (Unit), itself of hd2fake/lib/hdunit.py; the test asks the two to
    /// agree on real mods.</summary>
    sealed class UnitData
    {
        public uint Version;
        public readonly List<(List<(int Type, uint Format)> Comps, uint Verts, uint Stride, uint Vbo)> Streams = new List<(List<(int, uint)>, uint, uint, uint)>();
        public readonly List<Mesh> Meshes = new List<Mesh>();
        public readonly List<byte> LightFlags = new List<byte>();
        public readonly List<(ushort Parent, uint Name)> Transforms = new List<(ushort, uint)>();

        public sealed class Mesh
        {
            public int I;
            public float[] Aabb = new float[7];
            public uint MeshId, Tf, Stream;
            public int Lod;
            public List<(uint Voff, uint Verts, uint Idx)> Sections = new List<(uint, uint, uint)>();
        }

        public UnitData(byte[] b)
        {
            Version = U32(b, 0x2c);
            uint transform = U32(b, 0x34), lights = U32(b, 0x38), stream = U32(b, 0x5c), mesh = U32(b, 0x64);
            if (stream != 0)
            {
                uint n = U32(b, (int)stream);
                for (uint i = 0; i < n; i++)
                {
                    int s = (int)(stream + U32(b, (int)(stream + 4 + i * 4)));
                    ulong nc = BitConverter.ToUInt64(Need(b, s + 328, 8), s + 328);
                    var comps = new List<(int, uint)>();
                    for (ulong c = 0; c < nc && c < 64; c++)
                        comps.Add(((int)U32(b, s + 8 + (int)c * 20), U32(b, s + 12 + (int)c * 20)));
                    Streams.Add((comps, U32(b, s + 352), U32(b, s + 356), U32(b, s + 416)));
                }
            }
            uint nm = U32(b, (int)mesh);
            for (uint k = 0; k < nm; k++)
            {
                int bs = (int)(mesh + U32(b, (int)(mesh + 4 + k * 4)));
                uint ns = U32(b, bs + 0x78), soff = U32(b, bs + 0x7c);
                var m = new Mesh { I = (int)k, MeshId = U32(b, bs + 0x28), Tf = U32(b, bs + 0x30), Lod = (int)U32(b, bs + 0x38), Stream = U32(b, bs + 0x3c) };
                for (uint j = 0; j < ns; j++)
                {
                    int so = (int)(bs + soff + j * 24);
                    m.Sections.Add((U32(b, so + 4), U32(b, so + 8), U32(b, so + 16)));
                }
                for (int a = 0; a < 7; a++) m.Aabb[a] = BitConverter.ToSingle(Need(b, bs + 8 + a * 4, 4), bs + 8 + a * 4);
                Meshes.Add(m);
            }
            if (lights != 0)
            {
                uint n = U32(b, (int)lights);
                for (uint i = 0; i < n; i++) LightFlags.Add(Need(b, (int)(lights + 16 + i * 0x70 + 72), 1)[(int)(lights + 16 + i * 0x70 + 72)]);
            }
            if (transform != 0)
            {
                uint n = U32(b, (int)transform);
                int ent = (int)(transform + 16 + n * 128), names = ent + (int)n * 4;
                for (int i = 0; i < n; i++)
                    Transforms.Add((BitConverter.ToUInt16(Need(b, ent + i * 4 + 2, 2), ent + i * 4 + 2), U32(b, names + i * 4)));
            }
        }

        static byte[] Need(byte[] b, int o, int n)
        {
            if (o < 0 || o + n > b.Length) throw new IndexOutOfRangeException("read past the end of a unit");
            return b;
        }

        static uint U32(byte[] b, int o) => BitConverter.ToUInt32(Need(b, o, 4), o);
    }

    /// <summary>What the checks keep of a model, mod's or game's: each mesh's id, triangles,
    /// drawn sections and chain; the biggest culling box; each stream's bytes per vertex; each
    /// light's flags. The game's are kept in the table's cache.</summary>
    sealed class UnitSummary
    {
        public readonly List<(uint MeshId, int Tris, int Secs, string Chain)> Levels = new List<(uint, int, int, string)>();
        public float Box;
        public readonly List<uint> Strides = new List<uint>();
        public readonly List<byte> Lights = new List<byte>();

        public static UnitSummary Of(UnitData u)
        {
            var s = new UnitSummary();
            foreach (var x in u.Meshes)
            {
                int tris = 0, secs = 0;
                foreach (var q in x.Sections) { tris += (int)(q.Idx / 3); if (q.Idx != 0) secs++; }
                s.Levels.Add((x.MeshId, tris, secs, Checks.ChainOf((int)x.Tf, u.Transforms)));
                s.Box = Math.Max(s.Box, Checks.Extent(x.Aabb));
            }
            s.Strides.AddRange(u.Streams.Select(x => x.Stride));
            s.Lights.AddRange(u.LightFlags);
            return s;
        }
    }

    /// <summary>The checks of hd2_diag asset_cost_scan.py (RULES) as asset_check_src/core.js has
    /// them: a mod's model or texture next to the game's copy, each finding a flag. Evidence
    /// levels are in Plugin.Rules.</summary>
    static class Checks
    {
        public static string ChainOf(int tf, List<(ushort Parent, uint Name)> names)
        {
            for (int seen = 0; tf >= 0 && tf < names.Count && seen < 64; seen++)
            {
                var h = names[tf].Name;
                if (Chains.Shadow.Contains(h)) return "shadow";
                if (Chains.Game.Contains(h)) return "game";
                if (names[tf].Parent == tf) break;
                tf = names[tf].Parent;
            }
            return "?";
        }

        public static float Extent(float[] aabb)
        {
            float m = 0;
            for (int i = 0; i < 6; i++) m = Math.Max(m, Math.Abs(aabb[i]));
            return m;
        }

        /// <summary>One chain's triangle counts, most detailed first. LodIndex is not the level on
        /// skinned units, so ranked by triangles (the game's own count for a mesh the game has).</summary>
        static List<int> Ladder(List<(uint MeshId, int Tris, int Secs, string Chain)> levels, string chain, Dictionary<uint, int> orderBy = null)
        {
            return levels.Where(m => m.Chain == chain)
                .Select((m, i) => (Key: -(orderBy != null && orderBy.TryGetValue(m.MeshId, out var t) ? t : m.Tris), I: i, M: m))
                .OrderBy(e => e.Key).ThenBy(e => e.I).Select(e => e.M.Tris).ToList();
        }

        static readonly Dictionary<int, int> Sizes = new Dictionary<int, int> { [0] = 4, [1] = 8, [2] = 12, [3] = 16, [4] = 4, [24] = 16, [28] = 4, [29] = 4, [30] = 4, [33] = 4, [35] = 8 };
        static readonly Dictionary<int, int> SizesOld = new Dictionary<int, int> { [0] = 4, [1] = 8, [2] = 12, [3] = 16, [4] = 4, [20] = 16, [24] = 4, [25] = 4, [26] = 4, [29] = 4, [31] = 8 };

        /// <summary>The farthest vertex of a mesh from its positions in the GPU bytes, or null.</summary>
        static float? TrueExtent(UnitData u, byte[] gpu, UnitData.Mesh x)
        {
            if (gpu == null || gpu.Length == 0 || x.Sections.Count == 0 || x.Stream >= u.Streams.Count) return null;
            var st = u.Streams[(int)x.Stream];
            if (!st.Comps.Any(c => c.Type == 0 && c.Format == 2)) return null;
            var sizes = u.Version == 10800437 ? SizesOld : Sizes;
            int off = 0;
            foreach (var c in st.Comps)
            {
                if (c.Type == 0) break;
                if (!sizes.TryGetValue((int)c.Format, out var n)) return null;
                off += n;
            }
            long vs = long.MaxValue, ve = long.MinValue;
            foreach (var s in x.Sections) { vs = Math.Min(vs, s.Voff); ve = Math.Max(ve, (long)s.Voff + s.Verts); }
            float m = 0;
            bool any = false;
            for (long v = vs; v < ve; v++)
            {
                long p = st.Vbo + v * st.Stride + off;
                if (p + 12 > gpu.Length) return null;
                m = Math.Max(m, Math.Max(Math.Abs(BitConverter.ToSingle(gpu, (int)p)), Math.Max(Math.Abs(BitConverter.ToSingle(gpu, (int)p + 4)), Math.Abs(BitConverter.ToSingle(gpu, (int)p + 8)))));
                any = true;
            }
            return any ? m : (float?)null;
        }

        /// <summary>A mod's model next to the game's copy (null: the game has none): its flags, and
        /// the facts they rest on (triangles near to far, visible and shadow chains, mod and game).</summary>
        public static (List<string> Flags, Dictionary<string, object> Facts) Unit(UnitData mu, byte[] gpu, UnitSummary vs)
        {
            var flags = new List<string>();
            var facts = new Dictionary<string, object>();
            var ml = UnitSummary.Of(mu).Levels;
            var vl = vs?.Levels;
            var vt = vl != null && vl.Count > 0 ? vl.GroupBy(m => m.MeshId).ToDictionary(g => g.Key, g => g.Last().Tris) : null;
            List<int> mg = Ladder(ml, "game", vt), vg = vl != null && vl.Count > 0 ? Ladder(vl, "game") : new List<int>();
            List<int> ms = Ladder(ml, "shadow", vt), vsh = vl != null && vl.Count > 0 ? Ladder(vl, "shadow") : new List<int>();
            if (mg.Count == 0)
            {
                mg = Ladder(ml, "?");
                vg = vl != null && vl.Count > 0 ? Ladder(vl, "?") : new List<int>();
            }
            facts["game"] = new Dictionary<string, object> { ["mod"] = L(mg), ["game"] = L(vg) };
            facts["shadow"] = new Dictionary<string, object> { ["mod"] = L(ms), ["game"] = L(vsh) };
            if (mg.Count > 0)
            {
                int top = mg[0];
                if (vg.Count > 0)
                {
                    if (top > Math.Max(2 * vg[0], vg[0] + 10000)) flags.Add("tris_heavy");
                    if (mg.Count < vg.Count && vg.Count >= 2 && top >= vg[0]) flags.Add("lod_missing");
                    if (mg.Count >= 2 && vg.Count >= 2)
                    {
                        int farM = mg[mg.Count - 1], farV = vg[vg.Count - 1];
                        if (farM > 0.5 * top && farM > 3 * farV && farM > 3000) flags.Add("lod_flat");
                    }
                }
                else if (mg.Count >= 2 && mg[mg.Count - 1] > 0.5 * top && top > 5000) flags.Add("lod_flat");
            }
            int vs0 = vsh.Count > 0 ? vsh[0] : 0;
            if (ms.Count > 0 && ms[0] > Math.Max(2 * vs0, vs0 + 5000)) flags.Add("shadow_heavy");

            float worst = 0;
            var drawn = mu.Meshes.Where(x => x.Sections.Any(q => q.Idx != 0)).ToList();
            foreach (var x in drawn)
            {
                float box = Extent(x.Aabb);
                var real = TrueExtent(mu, gpu, x);
                if (real.HasValue && real.Value > 0 && box > 2 * real.Value && box - real.Value > 1.0f) worst = Math.Max(worst, box - real.Value);
            }
            if (vs != null)
            {
                float mbox = 0;
                foreach (var x in drawn) mbox = Math.Max(mbox, Extent(x.Aabb));
                if (mbox > 2 * vs.Box && mbox - vs.Box > 1.0f) worst = Math.Max(worst, mbox - vs.Box);
            }
            if (worst > 0) { flags.Add("aabb_inflated"); facts["aabb_over"] = (double)worst; }
            if (vl != null && vl.Count > 0)
            {
                var vmesh = new Dictionary<uint, int>();
                foreach (var m in vl) vmesh[m.MeshId] = m.Secs;
                var add = ml.Where(m => vmesh.ContainsKey(m.MeshId)).Select(m => m.Secs - vmesh[m.MeshId]).ToList();
                if (add.Count > 0 && add.Max() > 0) { flags.Add("sections_added"); facts["sections_added"] = (long)add.Max(); }
                for (int i = 0; i < Math.Min(vs.Strides.Count, mu.Streams.Count); i++)
                    if (mu.Streams[i].Stride > vs.Strides[i]) { flags.Add("stride_wider"); break; }
            }
            var vli = vs?.Lights ?? new List<byte>();
            var mli = mu.LightFlags;
            var added = mli.Count >= vli.Count ? mli.Skip(vli.Count).ToList() : mli.ToList();
            if (added.Any(l => (l & 0x1) != 0)) flags.Add("light_shadow");
            if (added.Any(l => (l & 0x10) != 0)) flags.Add("light_volumetric");
            facts["lights_added"] = (long)added.Count;
            return (flags, facts);
        }

        static List<object> L(List<int> xs) => xs.Select(x => (object)(long)x).ToList();

        /// <summary>A mod's texture next to the game's copy (game: whether there is one).</summary>
        public static List<string> Texture(Plugin.Item m, bool hasGame, Plugin.Item v)
        {
            var flags = new List<string>();
            int big = Math.Max(m.W, m.H);
            bool bc = Plugin.FormatName(m.Format)?.StartsWith("BC") == true;
            if (!bc && (long)m.W * m.H >= 512 * 512) flags.Add("tex_uncompressed");
            if (m.Mips <= 1 && big >= 256) flags.Add("tex_no_mips");
            if (hasGame)
            {
                if ((long)m.W * m.H > (long)v.W * v.H && m.Max >= 1e6) flags.Add("tex_bigger");
                if (v.Streamed > 0 && m.Streamed == 0 && m.Max > v.Resident * 2.0 && big >= 512) flags.Add("tex_not_streamed");
            }
            else if (big >= 4096 && m.Streamed == 0) flags.Add("tex_big_resident");
            return flags;
        }
    }
}
