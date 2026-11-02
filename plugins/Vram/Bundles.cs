using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Hd2Mod.Vram
{
    /// <summary>The game's own resource packages, read as the HD2 SDK's bundle reader does
    /// (hd2fake/lib/slim_std.py; hd2_diag/asset_check_src/core.js is the same in JavaScript):
    /// bundles.nxa lists every package and where its pieces sit in bundles.NN.nxa, which are
    /// DSAR files of LZ4 chunks.</summary>
    sealed class Game : IDisposable
    {
        readonly string data;
        readonly Dictionary<string, (ulong Size, List<(ulong Oao, uint Uso, int Bundle)> Items)> packages =
            new Dictionary<string, (ulong, List<(ulong, uint, int)>)>();
        readonly Dictionary<int, Dsar> bundles = new Dictionary<int, Dsar>();

        public Game(string data)
        {
            this.data = data;
            using (var index = new Dsar(Path.Combine(data, "bundles.nxa")))
            {
                var bc = index.All();
                uint np = U32(bc, 0x10);
                for (uint k = 0; k < np; k++)
                {
                    int bl = 0x18 + (int)k * 0x18;
                    ulong size = U64(bc, bl);
                    int noff = (int)U32(bc, bl + 8), cnt = (int)U32(bc, bl + 12), ioff = (int)U32(bc, bl + 16);
                    int end = noff;
                    while (bc[end] != 0) end++;
                    var name = Encoding.ASCII.GetString(bc, noff, end - noff);
                    var items = new List<(ulong, uint, int)>(cnt);
                    for (int i = 0; i < cnt; i++)
                    {
                        int r = ioff + 0x10 * i;
                        items.Add((U64(bc, r), U32(bc, r + 8), bc[r + 0x0f]));
                    }
                    packages[name] = (size, items);
                }
            }
        }

        public IEnumerable<string> Packages => packages.Keys;

        Dsar Bundle(int b)
        {
            if (!bundles.TryGetValue(b, out var d))
                bundles[b] = d = new Dsar(Path.Combine(data, "bundles." + b.ToString("00") + ".nxa"));
            return d;
        }

        /// <summary>Bytes [x, x+len) of a package; fewer at its end.</summary>
        public byte[] Read(string name, ulong x, int len)
        {
            if (!packages.TryGetValue(name, out var p) || p.Items.Count == 0) return new byte[0];
            var out_ = new byte[len];
            int got = 0;
            while (got < len && x + (ulong)got < p.Size)
            {
                ulong at = x + (ulong)got;
                int i = 0;
                for (int j = 0; j < p.Items.Count; j++) if (p.Items[j].Oao <= at) i = j;
                var it = p.Items[i];
                ulong end = i + 1 < p.Items.Count ? p.Items[i + 1].Oao : p.Size;
                int take = (int)Math.Min((ulong)(len - got), end - at);
                int n = Bundle(it.Bundle).Range(it.Uso + (at - it.Oao), out_, got, take);
                got += n;
                if (n < take) break;
            }
            if (got < len) Array.Resize(ref out_, got);
            return out_;
        }

        public void Dispose()
        {
            foreach (var b in bundles.Values) b.Dispose();
        }

        public static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        public static ulong U64(byte[] b, int o) => BitConverter.ToUInt64(b, o);
    }

    /// <summary>A DSAR file: a 32-byte header with the chunk count at 8, then 32-byte chunk
    /// records { u64 offset unpacked, u64 offset in the file, u32 size unpacked, u32 size
    /// stored, u8 kind (3 = LZ4) }.</summary>
    sealed class Dsar : IDisposable
    {
        readonly FileStream f;
        readonly ulong[] uo, co;
        readonly uint[] us, cs;
        readonly byte[] ct;
        readonly Dictionary<int, byte[]> cache = new Dictionary<int, byte[]>();
        readonly Queue<int> order = new Queue<int>();

        public Dsar(string path)
        {
            f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
            var h = Bytes(0, 0x20);
            if (Encoding.ASCII.GetString(h, 0, 4) != "DSAR") throw new InvalidDataException(path + " is not a DSAR file");
            int n = (int)Game.U32(h, 8);
            var t = Bytes(0x20, 0x20 * n);
            uo = new ulong[n]; co = new ulong[n]; us = new uint[n]; cs = new uint[n]; ct = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int o = i * 0x20;
                uo[i] = Game.U64(t, o); co[i] = Game.U64(t, o + 8);
                us[i] = Game.U32(t, o + 16); cs[i] = Game.U32(t, o + 20); ct[i] = t[o + 24];
            }
        }

        byte[] Bytes(long at, int n)
        {
            var b = new byte[n];
            f.Seek(at, SeekOrigin.Begin);
            int got = 0;
            while (got < n)
            {
                int r = f.Read(b, got, n - got);
                if (r <= 0) throw new InvalidDataException("a DSAR file ends early");
                got += r;
            }
            return b;
        }

        byte[] Chunk(int i)
        {
            if (cache.TryGetValue(i, out var c)) return c;
            var d = Bytes((long)co[i], (int)cs[i]);
            if (ct[i] == 3) d = Lz4.Decode(d, (int)us[i]);
            if (cache.Count >= 48) cache.Remove(order.Dequeue());
            cache[i] = d;
            order.Enqueue(i);
            return d;
        }

        /// <summary>The last chunk that starts at or before x.</summary>
        int Find(ulong x)
        {
            int lo = 0, hi = uo.Length - 1;
            while (lo < hi)
            {
                int m = (lo + hi + 1) >> 1;
                if (uo[m] <= x) lo = m; else hi = m - 1;
            }
            return lo;
        }

        /// <summary>Unpacked bytes [x, x+len) into dst at at; how many there were.</summary>
        public int Range(ulong x, byte[] dst, int at, int len)
        {
            int got = 0, i = Find(x);
            while (got < len && i < uo.Length)
            {
                var d = Chunk(i);
                long start = (long)(x + (ulong)got) - (long)uo[i];
                if (start < 0 || start >= d.Length) break;
                int take = (int)Math.Min(d.Length - start, len - got);
                Buffer.BlockCopy(d, (int)start, dst, at + got, take);
                got += take;
                i++;
            }
            return got;
        }

        public byte[] All()
        {
            var parts = Enumerable.Range(0, uo.Length).Select(Chunk).ToList();
            var out_ = new byte[parts.Sum(p => (long)p.Length)];
            long o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, out_, (int)o, p.Length); o += p.Length; }
            return out_;
        }

        public void Dispose() => f.Dispose();
    }

    /// <summary>An LZ4 block (no frame): sequences of literals and a back-reference.</summary>
    static class Lz4
    {
        public static byte[] Decode(byte[] src, int outSize)
        {
            var dst = new byte[outSize];
            int si = 0, di = 0, n = src.Length;
            while (si < n)
            {
                int tok = src[si++], lit = tok >> 4, b;
                if (lit == 15) do { b = src[si++]; lit += b; } while (b == 255);
                Buffer.BlockCopy(src, si, dst, di, lit);
                si += lit; di += lit;
                if (si >= n) break;
                int off = src[si] | (src[si + 1] << 8);
                si += 2;
                int ml = tok & 15;
                if (ml == 15) do { b = src[si++]; ml += b; } while (b == 255);
                ml += 4;
                int p = di - off;
                if (off <= 0 || p < 0) throw new InvalidDataException("an LZ4 block refers before its start");
                for (int k = 0; k < ml; k++) dst[di++] = dst[p++];
            }
            if (di != outSize) throw new InvalidDataException("an LZ4 block unpacks to " + di + " bytes, not " + outSize);
            return dst;
        }
    }
}
