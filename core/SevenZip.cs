using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Hd2Mod
{
    /// <summary>A 7z reader of our own (the user, 2026-11-02: "LZMA codec is unsupported"). Windows
    /// 10's tar.exe is libarchive built without liblzma, so it reads no LZMA or LZMA2 7z, and
    /// nearly every 7z is one; Windows 11's has liblzma 5.4.3. This reads the methods 7-Zip uses
    /// for mod files (LZMA, LZMA2, BCJ, Delta, Deflate, Copy) and checks every file's CRC. Any
    /// other method throws NotSupportedException ("password" in the message for AES), and a
    /// damaged archive InvalidDataException; the caller then asks tar. Written from the 7z format
    /// description (7zFormat.txt) and the LZMA specification in the LZMA SDK (public domain).</summary>
    public static class SevenZip
    {
        /// <summary>Whether the file starts with a 7z signature.</summary>
        public static bool Is(string path)
        {
            var head = new byte[6];
            using (var f = File.OpenRead(path))
                return f.Read(head, 0, 6) == 6 && head[0] == '7' && head[1] == 'z' && head[2] == 0xBC && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C;
        }

        /// <summary>Every file and folder of the archive written into the folder `into`, which no
        /// path may leave.</summary>
        public static void Extract(string archive, string into)
        {
            var root = Paths.Norm(into);
            var a = Read(archive);
            var files = a.Files.Where(f => !f.Anti).ToList();
            var targets = new List<string>();
            foreach (var f in files)
            {
                var target = Paths.Norm(Paths.Join(into, f.Name));
                if (f.Name.Length == 0 || (!target.StartsWith(root + "\\", StringComparison.Ordinal) && target != root))
                    throw new InvalidDataException("a path leaves the folder: " + f.Name);
                targets.Add(target);
            }
            Directory.CreateDirectory(into);
            for (int i = 0; i < files.Count; i++)
                if (files[i].Dir) Directory.CreateDirectory(targets[i]);
                else if (!files[i].HasStream) { Directory.CreateDirectory(Path.GetDirectoryName(targets[i])); File.WriteAllBytes(targets[i], new byte[0]); }

            // the files with data, folder by folder, in the archive's order
            var buf = new byte[1 << 16];
            int next = 0;
            var withData = Enumerable.Range(0, files.Count).Where(i => files[i].HasStream).ToList();
            var st = a.Main;
            if (st == null || st.Folders.Length == 0)
            {
                if (withData.Count > 0) throw new InvalidDataException("files without their data");
                return;
            }
            int sub = 0;
            for (int fo = 0; fo < st.Folders.Length; fo++)
            {
                using (var data = Unpacked(archive, st, fo))
                    for (int k = 0; k < st.NumSub[fo]; k++, sub++)
                    {
                        if (next >= withData.Count) throw new InvalidDataException("more data than files");
                        int i = withData[next++];
                        Directory.CreateDirectory(Path.GetDirectoryName(targets[i]));
                        uint crc = 0xFFFFFFFF;
                        using (var o = new FileStream(targets[i], FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        {
                            long left = st.SubSizes[sub];
                            while (left > 0)
                            {
                                int n = data.Read(buf, 0, (int)Math.Min(buf.Length, left));
                                if (n <= 0) throw new InvalidDataException("the data of " + files[i].Name + " ends early");
                                crc = Crc.Update(crc, buf, 0, n);
                                o.Write(buf, 0, n);
                                left -= n;
                            }
                        }
                        if (st.SubCrcs[sub] is uint want && want != ~crc)
                            throw new InvalidDataException("CRC error in " + files[i].Name);
                    }
            }
            if (next != withData.Count) throw new InvalidDataException("files without their data");
        }

        // ---- the archive's index ----

        sealed class Coder { public byte[] Id; public int NumIn = 1, NumOut = 1; public byte[] Props; }

        sealed class Folder
        {
            public Coder[] Coders;
            public (int In, int Out)[] Bonds;
            public int[] PackedIn;
            public long[] Sizes;
            public uint? Crc;
            public int MainOut() => Enumerable.Range(0, Sizes.Length).First(o => Bonds.All(b => b.Out != o));
            public long Size => Sizes[MainOut()];
        }

        sealed class Streams
        {
            public long PackPos;
            public long[] PackSizes = new long[0];
            public Folder[] Folders = new Folder[0];
            public int[] NumSub;
            public long[] SubSizes;
            public uint?[] SubCrcs;
        }

        sealed class Entry { public string Name = ""; public bool HasStream = true, Dir, Anti; }

        sealed class Archive { public Streams Main; public List<Entry> Files = new List<Entry>(); }

        const int kEnd = 0, kHeader = 1, kArchiveProperties = 2, kAdditionalStreamsInfo = 3, kMainStreamsInfo = 4,
            kFilesInfo = 5, kPackInfo = 6, kUnpackInfo = 7, kSubStreamsInfo = 8, kSize = 9, kCrc = 10, kFolder = 11,
            kCodersUnpackSize = 12, kNumUnpackStream = 13, kEmptyStream = 14, kEmptyFile = 15, kAnti = 16, kName = 17,
            kWinAttributes = 21, kEncodedHeader = 23;

        static Archive Read(string archive)
        {
            byte[] next;
            using (var f = File.OpenRead(archive))
            {
                var start = new byte[32];
                if (f.Read(start, 0, 32) != 32) throw new InvalidDataException("not a 7z");
                if (Crc.Of(start, 12, 20) != BitConverter.ToUInt32(start, 8)) throw new InvalidDataException("the 7z start header is damaged");
                long offset = BitConverter.ToInt64(start, 12), size = BitConverter.ToInt64(start, 20);
                if (offset < 0 || size < 0 || size > int.MaxValue || 32 + offset + size > f.Length) throw new InvalidDataException("the 7z is cut off");
                next = new byte[size];
                f.Position = 32 + offset;
                ReadFully(f, next, (int)size);
                if (Crc.Of(next, 0, next.Length) != BitConverter.ToUInt32(start, 28)) throw new InvalidDataException("the 7z index is damaged");
            }
            var a = new Archive();
            if (next.Length == 0) return a;               // an empty archive
            for (int round = 0; ; round++)
            {
                var h = new Hdr(next);
                int id = (int)h.Num();
                if (id == kHeader) { ReadHeader(h, a); return a; }
                if (id != kEncodedHeader || round > 3) throw new InvalidDataException("not a 7z index");
                // the index itself is packed: unpack it and read again
                var st = ReadStreams(h);
                var ms = new MemoryStream();
                for (int fo = 0; fo < st.Folders.Length; fo++)
                    using (var s = Unpacked(archive, st, fo))
                    {
                        var part = new byte[st.Folders[fo].Size];
                        ReadFully(s, part, part.Length);
                        if (st.Folders[fo].Crc is uint c && c != Crc.Of(part, 0, part.Length)) throw new InvalidDataException("the 7z index is damaged");
                        ms.Write(part, 0, part.Length);
                    }
                next = ms.ToArray();
            }
        }

        static void ReadHeader(Hdr h, Archive a)
        {
            int id = (int)h.Num();
            if (id == kArchiveProperties) { while (h.Num() != kEnd) h.Skip(); id = (int)h.Num(); }
            if (id == kAdditionalStreamsInfo) { ReadStreams(h); id = (int)h.Num(); }
            if (id == kMainStreamsInfo) { a.Main = ReadStreams(h); id = (int)h.Num(); }
            if (id == kFilesInfo) { ReadFiles(h, a); id = (int)h.Num(); }
            if (id != kEnd) throw new InvalidDataException("unexpected 7z index entry " + id);
        }

        static Streams ReadStreams(Hdr h)
        {
            var st = new Streams();
            int id = (int)h.Num();
            if (id == kPackInfo)
            {
                st.PackPos = h.Long();
                int n = h.Count();
                st.PackSizes = new long[n];
                for (id = (int)h.Num(); id != kEnd; id = (int)h.Num())
                    if (id == kSize) for (int i = 0; i < n; i++) st.PackSizes[i] = h.Long();
                    else if (id == kCrc) { var def = h.Defined(n); for (int i = 0; i < n; i++) if (def[i]) h.UInt(); }
                    else h.Skip();
                id = (int)h.Num();
            }
            if (id == kUnpackInfo)
            {
                if (h.Num() != kFolder) throw new InvalidDataException("7z folders expected");
                int n = h.Count();
                if (h.Byte() != 0) throw new NotSupportedException("7z folders kept elsewhere");
                st.Folders = new Folder[n];
                for (int i = 0; i < n; i++) st.Folders[i] = ReadFolder(h);
                if (h.Num() != kCodersUnpackSize) throw new InvalidDataException("7z sizes expected");
                foreach (var fo in st.Folders)
                    for (int o = 0; o < fo.Sizes.Length; o++) fo.Sizes[o] = h.Long();
                for (id = (int)h.Num(); id != kEnd; id = (int)h.Num())
                    if (id == kCrc)
                    {
                        var def = h.Defined(n);
                        for (int i = 0; i < n; i++) if (def[i]) st.Folders[i].Crc = h.UInt();
                    }
                    else h.Skip();
                id = (int)h.Num();
            }
            int nf = st.Folders.Length;
            st.NumSub = Enumerable.Repeat(1, nf).ToArray();
            if (id == kSubStreamsInfo)
            {
                for (id = (int)h.Num(); id != kCrc && id != kSize && id != kEnd; id = (int)h.Num())
                    if (id == kNumUnpackStream) for (int i = 0; i < nf; i++) st.NumSub[i] = h.Count();
                    else h.Skip();
                var sizes = new List<long>();
                for (int i = 0; i < nf; i++)
                {
                    if (st.NumSub[i] == 0) continue;
                    long sum = 0;
                    if (id == kSize)
                        for (int j = 1; j < st.NumSub[i]; j++) { long s = h.Long(); sizes.Add(s); sum += s; }
                    else if (st.NumSub[i] > 1) throw new InvalidDataException("7z sizes missing");
                    if (sum > st.Folders[i].Size) throw new InvalidDataException("7z sizes too large");
                    sizes.Add(st.Folders[i].Size - sum);
                }
                st.SubSizes = sizes.ToArray();
                if (id == kSize) id = (int)h.Num();
                st.SubCrcs = KnownCrcs(st);
                // CRCs of the streams whose folder CRC does not already say it
                int unknown = Enumerable.Range(0, nf).Sum(i => st.NumSub[i] == 1 && st.Folders[i].Crc != null ? 0 : st.NumSub[i]);
                for (; id != kEnd; id = (int)h.Num())
                    if (id == kCrc)
                    {
                        var def = h.Defined(unknown);
                        int u = 0, s = 0;
                        for (int i = 0; i < nf; i++)
                            if (st.NumSub[i] == 1 && st.Folders[i].Crc != null) s++;
                            else for (int j = 0; j < st.NumSub[i]; j++, s++, u++) if (def[u]) st.SubCrcs[s] = h.UInt();
                    }
                    else h.Skip();
                id = (int)h.Num();
            }
            else
            {
                st.SubSizes = st.Folders.Select(f => f.Size).ToArray();
                st.SubCrcs = KnownCrcs(st);
            }
            if (id != kEnd) throw new InvalidDataException("unexpected 7z streams entry " + id);
            return st;
        }

        static uint?[] KnownCrcs(Streams st)
        {
            var crcs = new List<uint?>();
            for (int i = 0; i < st.Folders.Length; i++)
                for (int j = 0; j < st.NumSub[i]; j++) crcs.Add(st.NumSub[i] == 1 ? st.Folders[i].Crc : null);
            return crcs.ToArray();
        }

        static Folder ReadFolder(Hdr h)
        {
            var fo = new Folder { Coders = new Coder[h.Count()] };
            int ins = 0, outs = 0;
            for (int i = 0; i < fo.Coders.Length; i++)
            {
                int flags = h.Byte();
                if ((flags & 0x80) != 0) throw new NotSupportedException("7z alternative methods");
                var c = new Coder { Id = h.Bytes(flags & 0xF) };
                if ((flags & 0x10) != 0) { c.NumIn = h.Count(); c.NumOut = h.Count(); }
                if ((flags & 0x20) != 0) c.Props = h.Bytes(h.Count());
                fo.Coders[i] = c;
                ins += c.NumIn; outs += c.NumOut;
            }
            fo.Bonds = new (int, int)[outs - 1];
            for (int i = 0; i < fo.Bonds.Length; i++) fo.Bonds[i] = (h.Count(), h.Count());
            int packed = ins - fo.Bonds.Length;
            if (packed < 1) throw new InvalidDataException("a 7z folder without data");
            if (packed == 1) fo.PackedIn = new[] { Enumerable.Range(0, ins).First(x => fo.Bonds.All(b => b.In != x)) };
            else { fo.PackedIn = new int[packed]; for (int i = 0; i < packed; i++) fo.PackedIn[i] = h.Count(); }
            fo.Sizes = new long[outs];
            return fo;
        }

        static void ReadFiles(Hdr h, Archive a)
        {
            int n = h.Count();
            for (int i = 0; i < n; i++) a.Files.Add(new Entry());
            bool[] emptyStream = null, emptyFile = null, anti = null;
            for (int id = (int)h.Num(); id != kEnd; id = (int)h.Num())
            {
                long size = h.Long();
                int end = h.Pos + (int)size;
                if (size < 0 || end > h.Length) throw new InvalidDataException("7z file list damaged");
                int empties = emptyStream?.Count(x => x) ?? 0;
                switch (id)
                {
                    case kEmptyStream: emptyStream = h.Bits(n); break;
                    case kEmptyFile: emptyFile = h.Bits(empties); break;
                    case kAnti: anti = h.Bits(empties); break;
                    case kName:
                        if (h.Byte() != 0) throw new NotSupportedException("7z names kept elsewhere");
                        for (int i = 0; i < n; i++) a.Files[i].Name = h.Utf16();
                        break;
                    case kWinAttributes:
                        {
                            var def = h.Defined(n);
                            if (h.Byte() != 0) throw new NotSupportedException("7z attributes kept elsewhere");
                            for (int i = 0; i < n; i++)
                                if (def[i] && (h.UInt() & 0x10) != 0) a.Files[i].Dir = true;
                            break;
                        }
                }
                h.Pos = end;
            }
            for (int i = 0, e = 0; i < n; i++)
            {
                var f = a.Files[i];
                if (emptyStream == null || !emptyStream[i]) { f.HasStream = true; f.Dir = false; continue; }
                f.HasStream = false;
                bool file = emptyFile != null && emptyFile[e];
                f.Dir = !file;
                f.Anti = anti != null && anti[e];
                e++;
            }
        }

        /// <summary>The index's own number and bit formats.</summary>
        sealed class Hdr
        {
            readonly byte[] b;
            public int Pos;
            public Hdr(byte[] b) { this.b = b; }
            public int Length => b.Length;
            public int Byte()
            {
                if (Pos >= b.Length) throw new InvalidDataException("the 7z index ends early");
                return b[Pos++];
            }
            public byte[] Bytes(int n)
            {
                if (n < 0 || Pos + n > b.Length) throw new InvalidDataException("the 7z index ends early");
                var r = new byte[n];
                Array.Copy(b, Pos, r, 0, n);
                Pos += n;
                return r;
            }
            public ulong Num()
            {
                int first = Byte(), mask = 0x80;
                ulong v = 0;
                for (int i = 0; i < 8; i++)
                {
                    if ((first & mask) == 0) return v | ((ulong)(first & (mask - 1)) << (8 * i));
                    v |= (ulong)Byte() << (8 * i);
                    mask >>= 1;
                }
                return v;
            }
            public long Long()
            {
                var v = Num();
                if (v > long.MaxValue) throw new InvalidDataException("7z size out of range");
                return (long)v;
            }
            public int Count()
            {
                var v = Num();
                if (v > 1 << 24) throw new InvalidDataException("7z count out of range");
                return (int)v;
            }
            public uint UInt() { var r = BitConverter.ToUInt32(Bytes(4), 0); return r; }
            public void Skip() { var n = Long(); if (n > b.Length - Pos) throw new InvalidDataException("the 7z index ends early"); Pos += (int)n; }
            public bool[] Bits(int n)
            {
                var r = new bool[n];
                int cur = 0, mask = 0;
                for (int i = 0; i < n; i++)
                {
                    if (mask == 0) { cur = Byte(); mask = 0x80; }
                    r[i] = (cur & mask) != 0;
                    mask >>= 1;
                }
                return r;
            }
            public bool[] Defined(int n) => Byte() != 0 ? Enumerable.Repeat(true, n).ToArray() : Bits(n);
            public string Utf16()
            {
                int s = Pos;
                while (true)
                {
                    if (Pos + 1 >= b.Length) throw new InvalidDataException("a 7z name ends early");
                    if (b[Pos] == 0 && b[Pos + 1] == 0) break;
                    Pos += 2;
                }
                var r = Encoding.Unicode.GetString(b, s, Pos - s);
                Pos += 2;
                return r;
            }
        }

        static void ReadFully(Stream s, byte[] b, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) throw new InvalidDataException("the 7z data ends early");
                got += r;
            }
        }

        // ---- a folder's data through its coders ----

        static Stream Unpacked(string archive, Streams st, int fo)
        {
            var folder = st.Folders[fo];
            int firstPack = st.Folders.Take(fo).Sum(f => f.PackedIn.Length);
            long pos = 32 + st.PackPos + st.PackSizes.Take(firstPack).Sum();
            var packs = new Stream[folder.PackedIn.Length];
            for (int i = 0; i < packs.Length; i++)
            {
                if (firstPack + i >= st.PackSizes.Length) throw new InvalidDataException("7z data missing");
                var f = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                f.Position = pos;
                packs[i] = new Part(f, st.PackSizes[firstPack + i], true);
                pos += st.PackSizes[firstPack + i];
            }
            try
            {
                var s = Out(folder, folder.MainOut(), packs);
                return new Part(s, folder.Size, true, packs);
            }
            catch
            {
                foreach (var p in packs) p.Dispose();
                throw;
            }
        }

        static Stream Out(Folder fo, int outIndex, Stream[] packs)
        {
            int c = 0, ins = 0, outs = 0;
            while (outs + fo.Coders[c].NumOut <= outIndex) { ins += fo.Coders[c].NumIn; outs += fo.Coders[c].NumOut; c++; }
            var coder = fo.Coders[c];
            var id = BitConverter.ToString(coder.Id).Replace("-", "");
            if (coder.NumIn != 1 || coder.NumOut != 1)
                throw new NotSupportedException(id == "0303011B" ? "the BCJ2 method" : "method " + id);
            Stream input;
            int bond = Array.FindIndex(fo.Bonds, b => b.In == ins);
            if (bond >= 0) input = Out(fo, fo.Bonds[bond].Out, packs);
            else input = packs[Array.IndexOf(fo.PackedIn, ins)];
            long size = fo.Sizes[outIndex];
            switch (id)
            {
                case "00": return input;
                case "030101": return new LzmaStream(input, coder.Props, size);
                case "21": return new Lzma2Stream(input, coder.Props, size);
                case "03030103": return new X86Stream(input);
                case "03": return new DeltaStream(input, coder.Props);
                case "040108": return new DeflateStream(input, CompressionMode.Decompress, true);
                case "06F10701": throw new NotSupportedException("it is protected by a password");
                default: throw new NotSupportedException("method " + id);
            }
        }

        /// <summary>Base of the read-only streams below.</summary>
        abstract class ReadOnly : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>The first `length` bytes of a stream.</summary>
        sealed class Part : ReadOnly
        {
            readonly Stream s;
            readonly bool own;
            readonly Stream[] also;
            long left;
            public Part(Stream s, long length, bool own, Stream[] also = null) { this.s = s; left = length; this.own = own; this.also = also; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (left <= 0) return 0;
                int n = s.Read(buffer, offset, (int)Math.Min(count, left));
                if (n <= 0) throw new InvalidDataException("the 7z data ends early");
                left -= n;
                return n;
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing && own) { s.Dispose(); if (also != null) foreach (var a in also) a.Dispose(); }
                base.Dispose(disposing);
            }
        }

        /// <summary>The range decoder of LZMA.</summary>
        sealed class Range
        {
            Stream src;
            byte[] buf = new byte[1 << 16];
            int pos, len;
            uint range, code;
            public Range(Stream src) { this.src = src; }

            /// <summary>Reads one LZMA2 chunk's packed bytes from now on.</summary>
            public void Load(byte[] chunk, int n) { src = null; buf = chunk; pos = 0; len = n; }

            byte Next()
            {
                if (pos == len)
                {
                    if (src == null) throw new InvalidDataException("an LZMA2 chunk ends early");
                    len = src.Read(buf, 0, buf.Length);
                    pos = 0;
                    if (len <= 0) { len = 0; throw new InvalidDataException("the LZMA data ends early"); }
                }
                return buf[pos++];
            }

            public void Init()
            {
                if (Next() != 0) throw new InvalidDataException("the LZMA data is damaged");
                code = 0;
                range = 0xFFFFFFFF;
                for (int i = 0; i < 4; i++) code = (code << 8) | Next();
                if (code == range) throw new InvalidDataException("the LZMA data is damaged");
            }

            public int Bit(ushort[] p, int i)
            {
                uint prob = p[i];
                uint bound = (range >> 11) * prob;
                int bit;
                if (code < bound) { range = bound; p[i] = (ushort)(prob + ((2048 - prob) >> 5)); bit = 0; }
                else { range -= bound; code -= bound; p[i] = (ushort)(prob - (prob >> 5)); bit = 1; }
                if (range < 1u << 24) { range <<= 8; code = (code << 8) | Next(); }
                return bit;
            }

            public uint Direct(int n)
            {
                uint r = 0;
                do
                {
                    range >>= 1;
                    code -= range;
                    uint t = 0 - (code >> 31);
                    code += range & t;
                    if (code == range) throw new InvalidDataException("the LZMA data is damaged");
                    if (range < 1u << 24) { range <<= 8; code = (code << 8) | Next(); }
                    r = (r << 1) + (t + 1);
                } while (--n > 0);
                return r;
            }

            public int Tree(ushort[] p, int b, int bits)
            {
                int m = 1;
                for (int i = 0; i < bits; i++) m = (m << 1) + Bit(p, b + m);
                return m - (1 << bits);
            }

            public uint Reverse(ushort[] p, int b, int bits)
            {
                int m = 1;
                uint sym = 0;
                for (int i = 0; i < bits; i++)
                {
                    int bit = Bit(p, b + m);
                    m = (m << 1) + bit;
                    sym |= (uint)bit << i;
                }
                return sym;
            }
        }

        sealed class LenCoder
        {
            readonly ushort[] choice = new ushort[2], low = new ushort[16 << 3], mid = new ushort[16 << 3], high = new ushort[256];
            public void Reset() { Fill(choice); Fill(low); Fill(mid); Fill(high); }
            public int Decode(Range rc, int posState)
            {
                if (rc.Bit(choice, 0) == 0) return rc.Tree(low, posState << 3, 3);
                if (rc.Bit(choice, 1) == 0) return 8 + rc.Tree(mid, posState << 3, 3);
                return 16 + rc.Tree(high, 0, 8);
            }
        }

        static void Fill(ushort[] p) { for (int i = 0; i < p.Length; i++) p[i] = 1024; }

        /// <summary>The LZMA decoder with its window; LZMA and LZMA2 feed it.</summary>
        sealed class Lzma
        {
            readonly byte[] win;
            int wpos;
            public readonly int WinSize;
            long pos;                       // bytes since the dictionary was last reset
            public long Total;              // bytes ever decoded
            int lc, lp, pb;
            ushort[] lit = new ushort[0];
            readonly ushort[] isMatch = new ushort[12 << 4], isRep = new ushort[12], isRepG0 = new ushort[12], isRepG1 = new ushort[12],
                isRepG2 = new ushort[12], isRep0Long = new ushort[12 << 4], posSlot = new ushort[4 << 6], posDec = new ushort[115], align = new ushort[16];
            readonly LenCoder len = new LenCoder(), repLen = new LenCoder();
            uint rep0, rep1, rep2, rep3;
            int state, pending;
            public bool Ended;

            public Lzma(long dict, long size)
            {
                long w = Math.Max(4096, Math.Min(dict, Math.Max(size, 1)));
                if (w > int.MaxValue - 64) throw new NotSupportedException("an LZMA dictionary of " + dict + " bytes");
                try { win = new byte[w]; }
                catch (OutOfMemoryException) { throw new NotSupportedException("an LZMA dictionary of " + dict + " bytes"); }
                WinSize = (int)w;
            }

            public void Props(int lc, int lp, int pb)
            {
                if (lc > 8 || lp > 4 || pb > 4) throw new InvalidDataException("LZMA properties out of range");
                if (lit.Length != 0x300 << (lc + lp)) lit = new ushort[0x300 << (lc + lp)];
                this.lc = lc; this.lp = lp; this.pb = pb;
            }

            public void ResetDict() => pos = 0;

            public void ResetState()
            {
                Fill(lit); Fill(isMatch); Fill(isRep); Fill(isRepG0); Fill(isRepG1); Fill(isRepG2); Fill(isRep0Long);
                Fill(posSlot); Fill(posDec); Fill(align);
                len.Reset(); repLen.Reset();
                rep0 = rep1 = rep2 = rep3 = 0;
                state = 0;
                pending = 0;
            }

            void Put(byte b)
            {
                win[wpos] = b;
                if (++wpos == WinSize) wpos = 0;
                pos++;
                Total++;
            }

            byte Back(uint dist)
            {
                int i = wpos - (int)dist - 1;
                if (i < 0) i += WinSize;
                return win[i];
            }

            public void PutRaw(byte[] b, int n) { for (int i = 0; i < n; i++) Put(b[i]); }

            /// <summary>The last n bytes decoded.</summary>
            public void CopyLast(int n, byte[] to, int at)
            {
                int s = wpos - n;
                if (s >= 0) { Buffer.BlockCopy(win, s, to, at, n); return; }
                s += WinSize;
                int first = WinSize - s;
                Buffer.BlockCopy(win, s, to, at, first);
                Buffer.BlockCopy(win, 0, to, at + first, n - first);
            }

            /// <summary>Decodes n more bytes, fewer only at an end marker.</summary>
            public void Decode(Range rc, int n)
            {
                long end = Total + n;
                int pbMask = (1 << pb) - 1, lpMask = (1 << lp) - 1;
                while (pending > 0 && Total < end) { Put(Back(rep0)); pending--; }
                while (Total < end)
                {
                    int posState = (int)pos & pbMask;
                    if (rc.Bit(isMatch, (state << 4) + posState) == 0)
                    {
                        int prev = pos > 0 ? Back(0) : 0;
                        int b = 0x300 * (((((int)pos) & lpMask) << lc) + (prev >> (8 - lc)));
                        int sym = 1;
                        if (state >= 7)
                        {
                            int match = Back(rep0);
                            do
                            {
                                int mb = (match >> 7) & 1;
                                match <<= 1;
                                int bit = rc.Bit(lit, b + ((1 + mb) << 8) + sym);
                                sym = (sym << 1) | bit;
                                if (mb != bit) break;
                            } while (sym < 0x100);
                        }
                        while (sym < 0x100) sym = (sym << 1) | rc.Bit(lit, b + sym);
                        Put((byte)sym);
                        state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                        continue;
                    }
                    int l;
                    if (rc.Bit(isRep, state) != 0)
                    {
                        if (pos == 0) throw new InvalidDataException("the LZMA data is damaged");
                        if (rc.Bit(isRepG0, state) == 0)
                        {
                            if (rc.Bit(isRep0Long, (state << 4) + posState) == 0)
                            {
                                state = state < 7 ? 9 : 11;
                                Put(Back(rep0));
                                continue;
                            }
                        }
                        else
                        {
                            uint dist;
                            if (rc.Bit(isRepG1, state) == 0) dist = rep1;
                            else
                            {
                                if (rc.Bit(isRepG2, state) == 0) dist = rep2;
                                else { dist = rep3; rep3 = rep2; }
                                rep2 = rep1;
                            }
                            rep1 = rep0;
                            rep0 = dist;
                        }
                        l = repLen.Decode(rc, posState);
                        state = state < 7 ? 8 : 11;
                    }
                    else
                    {
                        rep3 = rep2; rep2 = rep1; rep1 = rep0;
                        l = len.Decode(rc, posState);
                        state = state < 7 ? 7 : 10;
                        rep0 = Distance(rc, l);
                        if (rep0 == 0xFFFFFFFF) { Ended = true; return; }
                    }
                    if (rep0 >= pos || rep0 >= (uint)WinSize) throw new InvalidDataException("the LZMA data is damaged");
                    l += 2;
                    while (l > 0 && Total < end) { Put(Back(rep0)); l--; }
                    pending = l;
                }
            }

            uint Distance(Range rc, int l)
            {
                int slot = rc.Tree(posSlot, (l < 3 ? l : 3) << 6, 6);
                if (slot < 4) return (uint)slot;
                int nd = (slot >> 1) - 1;
                uint dist = (2u | (uint)(slot & 1)) << nd;
                if (slot < 14) return dist + rc.Reverse(posDec, (int)dist - slot, nd);
                dist += rc.Direct(nd - 4) << 4;
                return dist + rc.Reverse(align, 0, 4);
            }
        }

        /// <summary>An LZMA stream of a known size (props: the 5 bytes of its properties), for
        /// zip entries packed with LZMA (ZipLzma).</summary>
        internal static Stream LzmaOf(Stream input, byte[] props, long size) => new LzmaStream(input, props, size);

        sealed class LzmaStream : ReadOnly
        {
            readonly Lzma lz;
            readonly Range rc;
            readonly long size;
            public LzmaStream(Stream input, byte[] props, long size)
            {
                if (props == null || props.Length < 5) throw new InvalidDataException("LZMA properties missing");
                int d = props[0];
                if (d >= 9 * 5 * 5) throw new InvalidDataException("LZMA properties out of range");
                lz = new Lzma(BitConverter.ToUInt32(props, 1), size);
                lz.Props(d % 9, d / 9 % 5, d / 45);
                lz.ResetState();
                rc = new Range(input);
                rc.Init();
                this.size = size;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = (int)Math.Min(Math.Min(count, size - lz.Total), lz.WinSize);
                if (n <= 0) return 0;
                long before = lz.Total;
                lz.Decode(rc, n);
                if (lz.Total - before != n) throw new InvalidDataException("the LZMA data ends early");
                lz.CopyLast(n, buffer, offset);
                return n;
            }
        }

        sealed class Lzma2Stream : ReadOnly
        {
            readonly Stream input;
            readonly Lzma lz;
            readonly Range rc = new Range(null);
            readonly byte[] chunk = new byte[1 << 16];
            readonly long size;
            int left;
            bool raw, done;
            public Lzma2Stream(Stream input, byte[] props, long size)
            {
                if (props == null || props.Length < 1 || props[0] > 40) throw new InvalidDataException("LZMA2 properties out of range");
                long dict = props[0] == 40 ? 0xFFFFFFFFL : (2L | (props[0] & 1u)) << (props[0] / 2 + 11);
                lz = new Lzma(dict, size);
                this.input = input;
                this.size = size;
            }
            int Byte()
            {
                int b = input.ReadByte();
                if (b < 0) throw new InvalidDataException("the LZMA2 data ends early");
                return b;
            }
            bool NextChunk()
            {
                int c = Byte();
                if (c == 0) return false;
                if (c == 1 || c == 2)
                {
                    if (c == 1) lz.ResetDict();
                    left = ((Byte() << 8) | Byte()) + 1;
                    raw = true;
                    return true;
                }
                if (c < 0x80) throw new InvalidDataException("the LZMA2 data is damaged");
                left = ((c & 0x1F) << 16) + (Byte() << 8) + Byte() + 1;
                int packed = ((Byte() << 8) | Byte()) + 1;
                int mode = (c >> 5) & 3;
                if (mode == 3) lz.ResetDict();
                if (mode >= 2)
                {
                    int p = Byte();
                    if (p >= 9 * 5 * 5 || p % 9 + p / 9 % 5 > 4) throw new InvalidDataException("LZMA2 properties out of range");
                    lz.Props(p % 9, p / 9 % 5, p / 45);
                }
                if (mode >= 1) lz.ResetState();
                ReadFully(input, chunk, packed);
                rc.Load(chunk, packed);
                rc.Init();
                raw = false;
                return true;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int want = (int)Math.Min(Math.Min(count, size - lz.Total), lz.WinSize);
                if (want <= 0) return 0;
                int got = 0;
                while (got < want)
                {
                    if (left == 0)
                    {
                        if (done || !NextChunk()) { done = true; break; }
                        continue;
                    }
                    int k = Math.Min(left, want - got);
                    if (raw)
                    {
                        ReadFully(input, chunk, k);
                        lz.PutRaw(chunk, k);
                    }
                    else
                    {
                        long before = lz.Total;
                        lz.Decode(rc, k);
                        if (lz.Total - before != k) throw new InvalidDataException("the LZMA2 data is damaged");
                    }
                    left -= k;
                    got += k;
                }
                if (got < want) throw new InvalidDataException("the LZMA2 data ends early");
                lz.CopyLast(got, buffer, offset);
                return got;
            }
        }

        /// <summary>The x86 branch converter (BCJ) undone.</summary>
        sealed class X86Stream : ReadOnly
        {
            static readonly bool[] Allowed = { true, true, true, false, true, false, false, false };
            static readonly int[] BitNumber = { 0, 1, 2, 2, 3, 3, 3, 3 };
            readonly Stream input;
            readonly byte[] b = new byte[1 << 16];
            int start, conv, fill;
            bool eof;
            uint ip, prevMask, prevPos = unchecked((uint)-5);
            public X86Stream(Stream input) { this.input = input; }

            static bool Ms(byte x) => x == 0 || x == 0xFF;

            int Code(int size)
            {
                if (size < 5) return 0;
                if (ip - prevPos > 5) prevPos = ip - 5;
                int limit = size - 5, i = 0;
                while (i <= limit)
                {
                    byte x = b[i];
                    if (x != 0xE8 && x != 0xE9) { i++; continue; }
                    uint off = ip + (uint)i - prevPos;
                    prevPos = ip + (uint)i;
                    if (off > 5) prevMask = 0;
                    else for (uint k = 0; k < off; k++) { prevMask &= 0x77; prevMask <<= 1; }
                    x = b[i + 4];
                    if (Ms(x) && Allowed[(prevMask >> 1) & 7] && (prevMask >> 1) < 0x10)
                    {
                        uint src = ((uint)x << 24) | ((uint)b[i + 3] << 16) | ((uint)b[i + 2] << 8) | b[i + 1];
                        uint dest;
                        while (true)
                        {
                            dest = src - (ip + (uint)i + 5);
                            if (prevMask == 0) break;
                            int n = BitNumber[prevMask >> 1];
                            x = (byte)(dest >> (24 - n * 8));
                            if (!Ms(x)) break;
                            src = dest ^ ((1u << (32 - n * 8)) - 1);
                        }
                        b[i + 4] = (byte)~(((dest >> 24) & 1) - 1);
                        b[i + 3] = (byte)(dest >> 16);
                        b[i + 2] = (byte)(dest >> 8);
                        b[i + 1] = (byte)dest;
                        i += 5;
                        prevMask = 0;
                    }
                    else
                    {
                        i++;
                        prevMask |= 1;
                        if (Ms(x)) prevMask |= 0x10;
                    }
                }
                ip += (uint)i;
                return i;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (start == conv)
                {
                    Buffer.BlockCopy(b, conv, b, 0, fill - conv);
                    fill -= conv;
                    start = conv = 0;
                    while (!eof && fill < b.Length)
                    {
                        int r = input.Read(b, fill, b.Length - fill);
                        if (r <= 0) eof = true; else fill += r;
                    }
                    int k = Code(fill);
                    conv = eof ? fill : k;
                    if (conv == 0) return 0;
                }
                int n = Math.Min(count, conv - start);
                Buffer.BlockCopy(b, start, buffer, offset, n);
                start += n;
                return n;
            }
        }

        /// <summary>The Delta filter undone.</summary>
        sealed class DeltaStream : ReadOnly
        {
            readonly Stream input;
            readonly byte[] hist = new byte[256];
            readonly int dist;
            int hp;
            public DeltaStream(Stream input, byte[] props)
            {
                if (props == null || props.Length < 1) throw new InvalidDataException("Delta properties missing");
                this.input = input;
                dist = props[0] + 1;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = input.Read(buffer, offset, count);
                for (int i = 0; i < n; i++)
                {
                    byte o = (byte)(buffer[offset + i] + hist[(hp - dist) & 0xFF]);
                    hist[hp & 0xFF] = o;
                    hp++;
                    buffer[offset + i] = o;
                }
                return n;
            }
        }

        static class Crc
        {
            static readonly uint[] Table = Make();
            static uint[] Make()
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    t[i] = c;
                }
                return t;
            }
            public static uint Update(uint crc, byte[] b, int at, int n)
            {
                for (int i = at; i < at + n; i++) crc = Table[(crc ^ b[i]) & 0xFF] ^ (crc >> 8);
                return crc;
            }
            public static uint Of(byte[] b, int at, int n) => ~Update(0xFFFFFFFF, b, at, n);
        }
    }
}
