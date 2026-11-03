using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>Our own 7z reader (the user, 2026-11-02: "LZMA codec is unsupported" from Windows
    /// 10's tar). The samples in data\sevenzip are one made-up mod in every method py7zr writes
    /// (make.py there); expected.txt is what each must unpack to.</summary>
    public class SevenZipReader : Fixture
    {
        static readonly string Samples = Path.Combine(AppContext.BaseDirectory, "data", "sevenzip");

        public override void Dispose()
        {
            Ops.Tar = null;
            base.Dispose();
        }

        static string Sample(string name) => Path.Combine(Samples, name + ".7z");

        /// <summary>Every folder ("x/") and file ("x sha256") under root, as make.py lists them.</summary>
        static string Listing(string root)
        {
            using (var sha = SHA256.Create())
            {
                var lines = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(p =>
                {
                    var rel = p.Substring(root.Length + 1).Replace('\\', '/');
                    return Directory.Exists(p) ? rel + "/" : rel + " " + string.Concat(sha.ComputeHash(File.ReadAllBytes(p)).Select(b => b.ToString("x2")));
                }).ToList();
                lines.Sort(string.CompareOrdinal);
                return string.Join("\n", lines) + "\n";
            }
        }

        static string Expected => File.ReadAllText(Path.Combine(Samples, "expected.txt"));

        /// <summary>Stands for Windows 10's tar, which reads no LZMA: tar fails, our reader goes on.</summary>
        void NoTar() => Ops.Tar = Path.Combine(Root, "no-tar.exe");

        [Theory]
        [InlineData("bcj_lzma2")]
        [InlineData("bcj_lzma")]
        [InlineData("lzma")]
        [InlineData("lzma2")]
        [InlineData("delta_lzma2")]
        [InlineData("deflate")]
        [InlineData("copy")]
        public void Every_method_7_Zip_uses_for_mod_files_is_read(string name)
        {
            var into = Path.Combine(Root, name);
            SevenZip.Extract(Sample(name), into);
            Assert.Equal(Expected, Listing(into));
        }

        [Theory]
        [InlineData("password")]
        [InlineData("password_index")]
        public void A_password_is_said_as_one(string name)
        {
            var e = Assert.Throws<NotSupportedException>(() => SevenZip.Extract(Sample(name), Path.Combine(Root, name)));
            Assert.Contains("password", e.Message);
            NoTar();
            Assert.Equal("archive_password", Refused("add", Sample(name)));
            Assert.Empty(Library.Load().Mods);
        }

        [Fact]
        public void A_method_it_cannot_read_is_named()
        {
            var e = Assert.Throws<NotSupportedException>(() => SevenZip.Extract(Sample("bzip2"), Path.Combine(Root, "b")));
            Assert.Contains("040202", e.Message);
            NoTar();
            Assert.Equal("not_zip", Refused("add", Sample("bzip2")));
            Assert.Empty(Library.Load().Mods);
            Assert.False(Directory.Exists(Store.ModsDir()) && Directory.GetFileSystemEntries(Store.ModsDir()).Length > 0, "nothing is left behind");
        }

        /// <summary>Must-fail and must-pass: one byte changed in the packed data fails the CRC
        /// (or the LZMA2 stream) and is refused as damaged; the sample itself unpacks.</summary>
        [Fact]
        public void A_changed_byte_is_refused_as_damaged()
        {
            var bytes = File.ReadAllBytes(Sample("lzma2"));
            bytes[32 + 3000] ^= 0x40;
            var bad = Path.Combine(Root, "Bad.7z");
            File.WriteAllBytes(bad, bytes);
            Assert.Throws<InvalidDataException>(() => SevenZip.Extract(bad, Path.Combine(Root, "bad")));
            NoTar();
            Assert.Equal("archive_damaged", Refused("add", bad));
            Assert.Empty(Library.Load().Mods);
            Assert.False(Directory.Exists(Store.ModsDir()) && Directory.GetFileSystemEntries(Store.ModsDir()).Length > 0, "nothing is left behind");
            Cli("add", Sample("lzma2"));
            Assert.Single(Library.Load().Mods);
        }

        /// <summary>A zip whose entries are packed with LZMA (method 14), which .NET's zip reader
        /// refuses: HD2 Clear Sight 0.0.5 (the user, 2026-11-03: many players could not add it).
        /// Must live: every file as Python wrote it. Must die: an entry whose LZMA properties are
        /// out of range is refused as damaged, and nothing is left behind.</summary>
        [Fact]
        public void A_zip_packed_with_LZMA_is_added()
        {
            var samples = Path.Combine(AppContext.BaseDirectory, "data", "ziplzma");
            Cli("add", Path.Combine(samples, "lzma.zip"));
            var mod = Library.Load().Mods.Single();
            Assert.Equal("LZMA Sample", mod.Name);
            Assert.Equal(File.ReadAllText(Path.Combine(samples, "expected.txt")), Listing(mod.Path));
            Cli("remove", mod.Id);

            // the first entry's LZMA properties byte (after its local header and the 4 bytes of
            // version and properties' length) set out of range
            var bad = Path.Combine(Root, "bad.zip");
            var bytes = File.ReadAllBytes(Path.Combine(samples, "lzma.zip"));
            int at = 30 + BitConverter.ToUInt16(bytes, 26) + BitConverter.ToUInt16(bytes, 28) + 4;
            bytes[at] = 0xFF;
            File.WriteAllBytes(bad, bytes);
            Assert.Equal("archive_damaged", Refused("add", bad));
            Assert.Empty(Library.Load().Mods);
            Assert.Empty(Directory.GetDirectories(Store.ModsDir(), "bad_*"));        // nothing is left behind
        }

        /// <summary>Where tar cannot read the 7z (Windows 10), the mod is added all the same.</summary>
        [Fact]
        public void Without_tar_a_7z_is_added_by_our_reader()
        {
            NoTar();
            Cli("add", Sample("bcj_lzma2"));
            var mod = Library.Load().Mods.Single();
            // the one folder at the top, "Thing", is the mod
            Assert.Equal(Expected.Replace("Thing/", "").Substring(1), Listing(mod.Path));
            Assert.Single(PlanList());
        }
    }
}
