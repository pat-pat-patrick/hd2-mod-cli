using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>A new version dropped in takes the old one's place (the user, 2026-11-01): by its
    /// download source at once, by its name alone after a question.</summary>
    public class NewVersionsDropped : Fixture
    {
        /// <summary>A mod as a downloaded zip, with where it came from.</summary>
        string Download(string file, string body, string host = null, string referrer = null)
        {
            var folder = Mod("src " + file, Files((A + ".patch_0", body)));
            var zip = Path.Combine(Root, file);
            ZipFile.CreateFromDirectory(folder, zip);
            if (host != null) OnlineSources.Zone(zip, host, referrer);
            return zip;
        }

        const string NexusCdn = "https://cf-files.nexusmods.com/cdn/x";

        List<Mod> Mods => Library.Load().Mods;

        [Fact]
        public void The_same_nexus_mod_or_github_repository_takes_its_place_at_once()
        {
            Cli("add", Download("Cape 16320 1.0.0 2026-10-01T10-00Z aaaa.zip", "cape one", NexusCdn, "https://www.nexusmods.com/"));
            Cli("disable", "1");
            var said = Cli("add", Download("Cape 16320 1.1.0 2026-10-20T10-00Z bbbb.zip", "cape two", NexusCdn, "https://www.nexusmods.com/"));
            Assert.Contains("a new version of", said);
            Assert.Single(Mods);
            Assert.False(Mods[0].Enabled);                                       // its place and its switch kept
            Assert.Equal("cape two", File.ReadAllText(Path.Combine(Mods[0].Path, A + ".patch_0")));

            const string gh = "https://github.com/someone/lamp/releases/download/";
            Cli("add", Download("lamp.zip", "lamp one", "https://objects.githubusercontent.com/x", gh + "v1.0/lamp.zip"));
            Assert.Contains("a new version of", Cli("add", Download("lamp-v1.1.zip", "lamp two", "https://objects.githubusercontent.com/x", gh + "v1.1/lamp-v1.1.zip")));
            Assert.Equal(2, Mods.Count);
        }

        /// <summary>One Nexus page can hold several files, each a mod of its own.</summary>
        [Fact]
        public void Another_file_of_the_same_nexus_page_is_asked_about()
        {
            Cli("add", Download("Cape Main 16320 1.0.0 2026-10-01T10-00Z aaaa.zip", "main", NexusCdn, "https://www.nexusmods.com/"));
            var e = new StringWriter();
            Assert.Equal(1, Commands.Run(new[] { "add", Download("Cape Chinese 16320 1.0.0 2026-10-01T10-00Z bbbb.zip", "zh", NexusCdn, "https://www.nexusmods.com/") }, new StringWriter(), e));
            Assert.Contains("looks like a new version of", e.ToString());
            Assert.Single(Mods);
        }

        [Fact]
        public void A_name_alone_is_asked_about_and_both_answers_work()
        {
            Cli("add", Mod("Lamp v1.0", Files((A + ".patch_0", "one"))));
            var v12 = Mod("Lamp v1.2", Files((A + ".patch_0", "two")));
            var e = new StringWriter();
            Assert.Equal(1, Commands.Run(new[] { "add", v12 }, new StringWriter(), e));
            Assert.Contains("looks like a new version of 'Lamp v1.0'", e.ToString());
            Assert.Single(Mods);
            // the answers the window gives: its new version, or a mod of its own
            Cli("replace", Mods[0].Id, v12);
            Assert.Single(Mods);
            Assert.Equal(v12, Mods[0].Path);
            var v13 = Mod("Lamp v1.3", Files((A + ".patch_0", "three")));
            Cli("add", v13, "--new");
            Assert.Equal(2, Mods.Count);
            // the same name exactly: asked too, and --new gives it a name of its own
            var again = Mod(Path.Combine("again", "Lamp v1.3"), Files((A + ".patch_0", "four")));
            Assert.Equal(1, Commands.Run(new[] { "add", again }, new StringWriter(), new StringWriter()));
            Cli("add", again, "--new");
            Assert.Contains("Lamp v1.3 (2)", Mods.Select(m => m.Name));
        }

        [Fact]
        public void Mods_that_only_look_alike_are_added_without_a_question()
        {
            Cli("add", Mod("Armor Pack 1", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Armor Pack 2", Files((A + ".patch_0", "two"))));        // a number is not a version
            Cli("add", Mod("Red Cape", Files((A + ".patch_0", "red"))));
            Cli("add", Mod("Blue Cape", Files((A + ".patch_0", "blue"))));
            // a manifest Guid of its own: another mod, however alike the name
            Cli("add", Mod("Torch v1", Files((A + ".patch_0", "t1")), "{\"Guid\":\"aaaaaaaa-0000-0000-0000-000000000001\",\"Name\":\"Torch v1\"}"));
            Cli("add", Mod("Torch v2", Files((A + ".patch_0", "t2")), "{\"Guid\":\"aaaaaaaa-0000-0000-0000-000000000002\",\"Name\":\"Torch v2\"}"));
            Assert.Equal(6, Mods.Count);
        }

        [Fact]
        public void A_name_without_what_changes_between_versions()
        {
            Assert.Equal(NewVersions.Key("MDL3 3.0.0-dev.21 预览版"), NewVersions.Key("MDL3 3.0.0-dev.25 预览版"));
            Assert.Equal(NewVersions.Key("MDL3-3.0.0-dev.21-preview-Data-Hub-0.3.0-r2_7350f91f500e"), NewVersions.Key("MDL3-3.0.0-dev.25-preview-Data-Hub-0.3.0"));
            Assert.Equal(NewVersions.Key("20261003_无后座修复"), NewVersions.Key("20261101_无后座修复"));
            Assert.Equal(NewVersions.Key("Cape_AR123456"), NewVersions.Key("Cape (1)"));
            Assert.Equal("cape", NewVersions.Key("Cape 16320 1.1.0 2026-10-20T10-00Z bbbb"));
            Assert.NotEqual(NewVersions.Key("Armor Pack 1"), NewVersions.Key("Armor Pack 2"));
            Assert.Null(NewVersions.Key("v1.2"));
        }
    }
}
