using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>Two mods a player could not add (2026-11-15): a zip whose folder names are GBK,
    /// and a mod dropped in again while the profile in use did not have it.</summary>
    public class AddedAgain : Fixture
    {
        /// <summary>A zip of files whose names are written in GBK, without the UTF-8 flag, as a
        /// tool on a Chinese Windows writes them.</summary>
        string GbkZip(string name, Dictionary<string, string> files)
        {
            var zip = Path.Combine(Root, name + ".zip");
            using (var fs = File.Create(zip))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create, false, Encoding.GetEncoding(936)))
                foreach (var kv in files)
                    using (var w = new StreamWriter(z.CreateEntry(kv.Key).Open(), new UTF8Encoding(false)))
                        w.Write(kv.Value);
            return zip;
        }

        [Fact]
        public void A_zip_whose_folder_names_are_GBK_keeps_them()
        {
            var zip = GbkZip("skin", new Dictionary<string, string>
            {
                ["manifest.json"] = "{\"Version\": 1, \"Guid\": \"b1b4d002-0000-4000-8000-000000000001\", \"Name\": \"Skin\", " +
                                    "\"Options\": [{\"Name\": \"材质包\", \"Include\": [\"材质包\"]}]}",
                ["材质包/" + A + ".patch_0"] = "skin",
            });
            var raw = ZipLzma.Directory(zip);
            Assert.Contains(raw, e => (e.Flags & 0x800) == 0 && e.Name.Any(b => b >= 0x80));   // the case itself
            Cli("add", zip);
            var mod = Library.Load().Mods.Single();
            Assert.True(Directory.Exists(Path.Combine(mod.Path, "材质包")));
            Assert.Contains(A + ".patch_0", Cli("list", "--files"));
        }

        [Fact]
        public void Names_without_the_flag_are_UTF_8_when_they_can_be()
        {
            ZipLzma.Entry E(string name, Encoding enc, int flags = 0) => new ZipLzma.Entry { Name = enc.GetBytes(name), Flags = flags };
            var gbk = Encoding.GetEncoding(936);
            Assert.Equal(65001, ZipLzma.NameEncoding(new List<ZipLzma.Entry> { E("材质包/", Encoding.UTF8) }).CodePage);
            Assert.Equal(936, ZipLzma.NameEncoding(new List<ZipLzma.Entry> { E("材质包/", gbk), E("icon.png", gbk) }).CodePage);
            // a name with the flag is UTF-8 whatever the others are; ASCII alone says nothing
            Assert.Equal(936, ZipLzma.NameEncoding(new List<ZipLzma.Entry> { E("材质包/", Encoding.UTF8, 0x800), E("材质包/", gbk) }).CodePage);
            Assert.Equal(65001, ZipLzma.NameEncoding(new List<ZipLzma.Entry> { E("a/b.patch_0", gbk) }).CodePage);
        }

        /// <summary>The library has the mod (imported into another profile), the profile in use
        /// does not: dropped in again, it comes into the profile in use, on.</summary>
        [Fact]
        public void A_mod_added_again_comes_into_the_profile_in_use()
        {
            const string manifest = "{\"Version\": 1, \"Guid\": \"8236065a-0000-4000-8000-000000000002\", \"Name\": \"Scope\"}";
            Cli("add", Mod("Scope", Files((A + ".patch_0", "v1")), manifest));
            Cli("profiles-on");
            Assert.Contains("made 'Turrets', empty ('Default' is still in use)", Cli("profile-new", "Turrets", "--empty"));
            Assert.Contains("made 'Copy', a copy of 'Default'", Cli("profile-new", "Copy"));
            Cli("profile-use", "Turrets", "--no-deploy");
            var lib = Library.Load();
            Assert.False(Profiles.InProfile(lib, lib.Mods.Single()));
            var said = Cli("add", Mod("Scope v2", Files((A + ".patch_0", "v2")), manifest));
            Assert.Contains("put into the profile in use: Turrets", said);
            lib = Library.Load();
            var mod = lib.Mods.Single();
            Assert.True(Profiles.InProfile(lib, mod));
            Assert.True(mod.Enabled);
            Assert.Contains("* Turrets  (1 mods, 1 on)", Cli("profiles"));
            // the profile it was in keeps it
            Assert.Contains("Default  (1 mods", Cli("profiles"));
        }

        /// <summary>An update (hd2qd replace) leaves a mod in the profiles it is in.</summary>
        [Fact]
        public void A_replace_leaves_the_profiles_as_they_are()
        {
            Cli("add", Mod("Scope", Files((A + ".patch_0", "v1"))));
            Cli("profiles-on");
            Cli("profile-new", "Turrets", "--empty");
            Cli("profile-use", "Turrets", "--no-deploy");
            Cli("replace", "Scope", Mod("Scope v2", Files((A + ".patch_0", "v2"))));
            var lib = Library.Load();
            Assert.False(Profiles.InProfile(lib, lib.Mods.Single()));
        }
    }
}
