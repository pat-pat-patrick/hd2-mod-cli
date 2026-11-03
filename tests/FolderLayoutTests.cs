using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>An old mod with no manifest.json and its patch files in several folders (the user,
    /// 2026-11-04): its folders become options (FolderLayout), variants a choice, parts each on.</summary>
    public class FolderLayouts : Fixture
    {
        static List<ManifestOption> Options() => Manifest.Of(Library.Load().Mods.Single()).Options;

        // what the game has: the text in each deployed patch file
        List<string> Deployed() => Directory.GetFiles(Data).Where(f => Deploy.Patch.IsMatch(Path.GetFileName(f)))
            .Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))).ToList();

        static bool Has(List<string> texts, string word) => texts.Any(t => t.Contains(word));

        /// <summary>Must live: variants of one item are one choice, the first deployed; another
        /// chosen, it replaces the first. A part alone is an option on its own, on. Must die: the
        /// variants are never deployed together (all three were, before).</summary>
        [Fact]
        public void Variants_are_a_choice_and_a_part_an_option_of_its_own()
        {
            Cli("add", "--whole", Mod("Old", new Dictionary<string, byte[]>
            {
                ["Red/" + A + ".patch_0"] = Archive(Bytes("variant red"), (1, 101), (1, 102)),
                ["Blue/" + A + ".patch_0"] = Archive(Bytes("variant blue"), (1, 101), (1, 102)),
                ["Cape/" + B + ".patch_0"] = Archive(Bytes("part cape"), (1, 200)),
            }));
            var o = Options();
            Assert.Equal(new[] { "Blue / Red", "Cape" }, o.Select(x => x.Name));
            Assert.Equal(new[] { "Blue", "Red" }, o[0].SubOptions.Select(s => s.Name));
            Assert.Empty(o[1].SubOptions);
            Cli("deploy");
            var game = Deployed();
            Assert.True(Has(game, "variant blue") && Has(game, "part cape"));
            Assert.False(Has(game, "variant red"));
            Cli("set", Library.Load().Mods.Single().Id, "1.2", "on", "--only");
            Cli("deploy");
            game = Deployed();
            Assert.True(Has(game, "variant red") && Has(game, "part cape"));
            Assert.False(Has(game, "variant blue"));
        }

        /// <summary>Parts that share nothing are each an option, all on: the game gets all of them,
        /// as before; one can now be turned off.</summary>
        [Fact]
        public void Parts_are_each_on_and_can_be_turned_off()
        {
            Cli("add", "--whole", Mod("Set", new Dictionary<string, byte[]>
            {
                ["Armor/" + A + ".patch_0"] = Archive(Bytes("armor"), (1, 101)),
                ["Helmet/" + B + ".patch_0"] = Archive(Bytes("helmet"), (1, 102)),
            }));
            Assert.Equal(new[] { "Armor", "Helmet" }, Options().Select(x => x.Name));
            Cli("deploy");
            Assert.True(Has(Deployed(), "armor") && Has(Deployed(), "helmet"));
            Cli("set", Library.Load().Mods.Single().Id, "2", "off");
            Cli("deploy");
            Assert.True(Has(Deployed(), "armor"));
            Assert.False(Has(Deployed(), "helmet"));
        }

        /// <summary>Parts for different armours that each carry the same shared mesh (the same
        /// bytes) are still parts (a player's Bunny Body_Common, 2026-11-04: as variants, only the
        /// first armour got the body). Must live: two options, both on. Must die: the same shared
        /// resource with other bytes makes them variants.</summary>
        [Fact]
        public void A_shared_resource_of_the_same_bytes_keeps_parts_apart()
        {
            var mesh = Archive(Bytes("shared mesh"), (1, 900));
            Cli("add", "--whole", Mod("Body", new Dictionary<string, byte[]>
            {
                ["CM-09/" + A + ".patch_0"] = mesh, ["CM-09/" + B + ".patch_0"] = Archive(Bytes("cm09"), (1, 101)),
                ["O-3/" + A + ".patch_0"] = mesh, ["O-3/" + B + ".patch_0"] = Archive(Bytes("o3"), (1, 102)),
            }));
            Assert.Equal(new[] { "CM-09", "O-3" }, Options().Select(x => x.Name));
            Cli("remove", Library.Load().Mods.Single().Id);
            Cli("add", "--whole", Mod("Body2", new Dictionary<string, byte[]>
            {
                ["CM-09/" + A + ".patch_0"] = Archive(Bytes("mesh one"), (1, 900)),
                ["O-3/" + A + ".patch_0"] = Archive(Bytes("mesh two"), (1, 900)),
            }));
            Assert.Equal(new[] { "CM-09 / O-3" }, Options().Select(x => x.Name));
        }

        /// <summary>Names without the folder they all sit in; variants under a folder of their own
        /// are named by it.</summary>
        [Fact]
        public void Names_leave_out_the_folder_they_all_sit_in()
        {
            Cli("add", "--whole", Mod("Pack", new Dictionary<string, byte[]>
            {
                ["Pack/Armor/Red/" + A + ".patch_0"] = Archive(Bytes("red"), (1, 101)),
                ["Pack/Armor/Blue/" + A + ".patch_0"] = Archive(Bytes("blue"), (1, 101)),
                ["Pack/Cape/" + B + ".patch_0"] = Archive(Bytes("cape"), (1, 200)),
            }));
            var o = Options();
            Assert.Equal(new[] { "Armor", "Cape" }, o.Select(x => x.Name));
            Assert.Equal(new[] { "Blue", "Red" }, o[0].SubOptions.Select(s => s.Name));
        }

        /// <summary>Must die: patch files at the top, or in one folder only, give no options: the
        /// mod is deployed whole, as before.</summary>
        [Fact]
        public void Files_at_the_top_or_one_folder_give_no_options()
        {
            Cli("add", "--whole", Mod("Top", new Dictionary<string, byte[]>
            {
                [A + ".patch_0"] = Archive(Bytes("top"), (1, 101)),
                ["Extra/" + B + ".patch_0"] = Archive(Bytes("extra"), (1, 101)),
            }));
            Assert.Null(Manifest.Of(Library.Load().Mods.Single()));
            Assert.Empty(FolderLayout.Of(Path.Combine(Root, "nothing here")));
        }
    }
}
