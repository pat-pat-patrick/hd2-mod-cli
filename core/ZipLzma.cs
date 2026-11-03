using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hd2Mod
{
    /// <summary>Zip entries packed with LZMA (zip method 14), which .NET's zip reader refuses ("The
    /// archive entry was compressed using an unsupported compression method"): HD2 Clear Sight
    /// 0.0.5 on Nexus Mods is such a zip, and players could not add it (the user, 2026-11-03).
    /// The zip's central directory is read here, so each entry's raw bytes can be found, and an
    /// LZMA entry is unpacked by the 7z reader's LZMA decoder (SevenZip.LzmaOf).</summary>
    public static class ZipLzma
    {
        public const int Method = 14;

        /// <summary>One entry as the central directory gives it, in the directory's order (the
        /// order .NET's ZipArchive lists them in).</summary>
        public sealed class Entry
        {
            public int Method;
            public long Compressed, Size, Offset;
        }

        /// <summary>The central directory's entries; zip64 sizes and offsets are read.</summary>
        public static List<Entry> Directory(string path)
        {
            using (var f = File.OpenRead(path))
            {
                // the end record: in the last 64 KB and 22 bytes (its comment is at most 64 KB)
                int tail = (int)Math.Min(f.Length, 65557);
                var end = new byte[tail];
                f.Seek(f.Length - tail, SeekOrigin.Begin);
                Fill(f, end);
                int at = -1;
                for (int i = tail - 22; i >= 0; i--)
                    if (BitConverter.ToUInt32(end, i) == 0x06054b50) { at = i; break; }
                if (at < 0) throw new InvalidDataException("no end of central directory");
                long count = BitConverter.ToUInt16(end, at + 10);
                long dirOffset = BitConverter.ToUInt32(end, at + 16);
                long endAt = f.Length - tail + at;
                // zip64: the locator just before the end record names the zip64 end record
                if ((count == 0xFFFF || dirOffset == 0xFFFFFFFF) && endAt >= 20)
                {
                    var loc = new byte[20];
                    f.Seek(endAt - 20, SeekOrigin.Begin);
                    Fill(f, loc);
                    if (BitConverter.ToUInt32(loc, 0) == 0x07064b50)
                    {
                        var z64 = new byte[56];
                        f.Seek((long)BitConverter.ToUInt64(loc, 8), SeekOrigin.Begin);
                        Fill(f, z64);
                        if (BitConverter.ToUInt32(z64, 0) != 0x06064b50) throw new InvalidDataException("the zip64 end record is not there");
                        count = (long)BitConverter.ToUInt64(z64, 32);
                        dirOffset = (long)BitConverter.ToUInt64(z64, 48);
                    }
                }
                var out_ = new List<Entry>();
                f.Seek(dirOffset, SeekOrigin.Begin);
                var head = new byte[46];
                for (long n = 0; n < count; n++)
                {
                    Fill(f, head);
                    if (BitConverter.ToUInt32(head, 0) != 0x02014b50) throw new InvalidDataException("a central directory entry is damaged");
                    int nameLen = BitConverter.ToUInt16(head, 28), extraLen = BitConverter.ToUInt16(head, 30), commentLen = BitConverter.ToUInt16(head, 32);
                    var e = new Entry
                    {
                        Method = BitConverter.ToUInt16(head, 10),
                        Compressed = BitConverter.ToUInt32(head, 20), Size = BitConverter.ToUInt32(head, 24), Offset = BitConverter.ToUInt32(head, 42),
                    };
                    f.Seek(nameLen, SeekOrigin.Current);
                    var extra = new byte[extraLen];
                    Fill(f, extra);
                    f.Seek(commentLen, SeekOrigin.Current);
                    // the zip64 extra field: in order, those of size, compressed size and offset that are 0xFFFFFFFF
                    for (int i = 0; i + 4 <= extra.Length;)
                    {
                        int id = BitConverter.ToUInt16(extra, i), len = BitConverter.ToUInt16(extra, i + 2);
                        if (id == 1)
                        {
                            int p = i + 4;
                            if (e.Size == 0xFFFFFFFF && p + 8 <= i + 4 + len) { e.Size = (long)BitConverter.ToUInt64(extra, p); p += 8; }
                            if (e.Compressed == 0xFFFFFFFF && p + 8 <= i + 4 + len) { e.Compressed = (long)BitConverter.ToUInt64(extra, p); p += 8; }
                            if (e.Offset == 0xFFFFFFFF && p + 8 <= i + 4 + len) e.Offset = (long)BitConverter.ToUInt64(extra, p);
                        }
                        i += 4 + len;
                    }
                    out_.Add(e);
                }
                return out_;
            }
        }

        /// <summary>An LZMA entry written to target: after its local header, two bytes of the
        /// LZMA SDK's version, two of the properties' length, the properties, then the stream.</summary>
        public static void Extract(string path, Entry e, string target)
        {
            using (var f = File.OpenRead(path))
            {
                var local = new byte[30];
                f.Seek(e.Offset, SeekOrigin.Begin);
                Fill(f, local);
                if (BitConverter.ToUInt32(local, 0) != 0x04034b50) throw new InvalidDataException("a local entry header is damaged");
                f.Seek(BitConverter.ToUInt16(local, 26) + BitConverter.ToUInt16(local, 28), SeekOrigin.Current);
                var lead = new byte[4];
                Fill(f, lead);
                var props = new byte[BitConverter.ToUInt16(lead, 2)];
                Fill(f, props);
                using (var lz = SevenZip.LzmaOf(f, props, e.Size))
                using (var o = File.Create(target))
                    lz.CopyTo(o, 1 << 16);
            }
        }

        static void Fill(Stream f, byte[] b)
        {
            int got = 0;
            while (got < b.Length)
            {
                int n = f.Read(b, got, b.Length - got);
                if (n <= 0) throw new InvalidDataException("the zip ends early");
                got += n;
            }
        }
    }
}
