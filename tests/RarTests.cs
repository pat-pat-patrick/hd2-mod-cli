using System.IO;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>A rar Windows's own reader (tar.exe) cannot read is said as such, so the window can
    /// tell the player to unpack it with an archive program (issue 22, 2026-11-12: "Truncated data
    /// in huffman tables" from a rar another program unpacked); anything else tar refuses stays
    /// not_zip.</summary>
    public class RarRefusals : Fixture
    {
        string Write(string name, byte[] head)
        {
            var p = Path.Combine(Root, name);
            File.WriteAllBytes(p, head.Concat(Enumerable.Range(0, 4096).Select(i => (byte)(i * 37 + 11))).ToArray());
            return p;
        }

        [Fact]
        public void A_rar_tar_cannot_read_is_rar_unreadable()
        {
            // a RAR 5 signature, then nothing tar can read
            var rar = Write("broken.rar", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });
            Assert.Equal("rar_unreadable", Refused("add", rar));
            Assert.Empty(Library.Load().Mods);
        }

        [Fact]
        public void A_file_named_rar_that_is_no_rar_stays_not_zip()
        {
            var other = Write("other.rar", new byte[] { 0x00, 0x01, 0x02, 0x03 });
            Assert.Equal("not_zip", Refused("add", other));
        }
    }
}
