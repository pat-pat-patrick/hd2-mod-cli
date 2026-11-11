using System.IO;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>What the delete question says is left of a mod, by what it was added from (issue
    /// 16, 2026-11-09: it always said the archive was kept, also for a mod added as a plain folder,
    /// whose folder is the only copy).</summary>
    public class DeleteAsk : Fixture
    {
        /// <summary>Must live: an archive still on the disk outside the folder is said to be kept;
        /// a page to download it again is named. Must die: a folder added as itself, an archive
        /// since deleted, an archive inside the folder, and no source say nothing is left.</summary>
        [Fact]
        public void The_delete_question_says_what_is_left_by_the_source()
        {
            var folder = Path.Combine(Root, "Mod One");
            Directory.CreateDirectory(folder);
            var zip = Path.Combine(Root, "Mod One.zip");
            File.WriteAllBytes(zip, new byte[] { 0x50, 0x4B, 3, 4 });

            Assert.Equal("archive", Ops.LeftAfterDelete(zip, folder));
            Assert.Equal("page", Ops.LeftAfterDelete("https://www.nexusmods.com/helldivers2/mods/1", folder));

            Assert.Equal("none", Ops.LeftAfterDelete(folder, folder));                     // added as a folder
            Assert.Equal("none", Ops.LeftAfterDelete(folder + "\\", folder));
            Assert.Equal("none", Ops.LeftAfterDelete(Path.Combine(Root, "gone.zip"), folder)); // the archive deleted since
            var inside = Path.Combine(folder, "copy.zip");
            File.WriteAllBytes(inside, new byte[] { 0x50, 0x4B, 3, 4 });
            Assert.Equal("none", Ops.LeftAfterDelete(inside, folder));                     // goes with the folder
            Assert.Equal("none", Ops.LeftAfterDelete(null, folder));
            Assert.Equal("none", Ops.LeftAfterDelete("", folder));
        }
    }
}
