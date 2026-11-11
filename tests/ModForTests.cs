using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>Mods for one deploy target only, and two builds of one mod side by side (Mod.For,
    /// Mod.Pair; the user, 2026-11-11: a code mod may run on both games, or have one build for each).
    /// On HD2QD.Sample.dll's target "sample".</summary>
    public class ModFor : Fixture
    {
        static string Sample => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HD2QD.Sample.dll");
        const string Guid1 = "bbbbbbbb-0000-0000-0000-000000000001";

        public ModFor()
        {
            PluginFiles.Folder = null;
            PluginFiles.OfficialJson = () => null;
        }

        string Build(string name, string body) =>
            Mod(name, Files((A + ".patch_0", body)), @"{""Guid"": """ + Guid1 + @""", ""Name"": ""Holo""}");

        [Fact]
        public void TheSameModAddedAgainIsAskedAboutWhileATargetIsOffered()
        {
            Cli("add", Build("Holo official", "official build"));
            // no target offered: a new version, as always
            var newer = Build("Holo newer", "newer official build");
            Assert.Contains("replaced", Cli("add", newer));
            Assert.Single(Library.Load().Mods);

            PluginFiles.Install(Sample);
            var port = Build("Holo port", "the sample game's build");
            Assert.Equal("same_guid", Refused("add", port));
            Assert.Single(Library.Load().Mods);

            // the sample game's build beside it: a pair, each for its own target
            Cli("add", port, "--for", "sample");
            var lib = Library.Load();
            Assert.Equal(2, lib.Mods.Count);
            var off = lib.Mods[0];
            var mine = lib.Mods[1];
            Assert.Equal("official", off.For);
            Assert.Equal("sample", mine.For);
            Assert.Equal(mine.Id, off.Pair);
            Assert.Equal(off.Id, mine.Pair);
            Assert.True(off.Enabled);
            Assert.False(mine.Enabled);                                   // the official game is in use

            // a new version of the port replaces the port only
            var port2 = Build("Holo port 2", "the sample game's newer build");
            Cli("add", port2, "--for", "sample");
            lib = Library.Load();
            Assert.Equal(2, lib.Mods.Count);
            Assert.Equal(Path.GetFullPath(port2).TrimEnd('\\'), Path.GetFullPath(lib.Mods.Single(m => m.For == "sample").Path).TrimEnd('\\'));
            // without saying which: asked
            Assert.Equal("same_guid", Refused("add", Build("Holo again", "which one?")));

            // each target turns on its own build; the other one cannot be turned on there
            Assert.Equal("for_other_target", Refused("enable", Library.Load().Mods.Single(m => m.For == "sample").Id));
            Cli("target", "sample");
            lib = Library.Load();
            Assert.False(lib.Mods.Single(m => m.For == "official").Enabled);
            Cli("enable", lib.Mods.Single(m => m.For == "sample").Id);
            Assert.True(Library.Load().Mods.Single(m => m.For == "sample").Enabled);
            var err = new StringWriter();
            Commands.Run(new[] { "enable", lib.Mods.Single(m => m.For == "official").Id }, new StringWriter(), err, out var why);
            Assert.True(why?.Kind == "for_other_target", "refused: " + err + " kind " + why?.Kind + " mods: " + string.Join(", ", Library.Load().Mods.Select(m => m.Id + "=" + m.Name + "/" + m.For)));
            var deployed = Cli("deploy");
            Assert.Contains("sample: 1 mods", deployed);
            var sampleBuild = Library.Load().Mods.Single(m => m.For == "sample");
            var line = File.ReadAllText(Path.Combine(Store.Home, "sample_target.txt"));
            Assert.StartsWith(sampleBuild.Id + " ", line);              // the sample game's build, and only it
        }

        [Fact]
        public void APairTurnsItsOtherBuildOff()
        {
            PluginFiles.Install(Sample);
            var a = Mod("Light A", Files((A + ".patch_0", "a")));
            var b = Mod("Light B", Files((B + ".patch_0", "b")));
            Cli("add", a);
            Cli("add", b);
            Cli("pair", "Light A", "Light B");
            Assert.False(Library.Load().Mods.Single(m => m.Name == "Light B").Enabled);   // both were on: the one higher in the list stays
            Cli("enable", "Light A");
            var lib = Library.Load();
            Assert.True(lib.Mods.Single(m => m.Name == "Light A").Enabled);
            Assert.False(lib.Mods.Single(m => m.Name == "Light B").Enabled);
            Cli("unpair", "Light A");
            Cli("enable", "Light B");
            lib = Library.Load();
            Assert.True(lib.Mods.All(m => m.Enabled));
            Assert.True(lib.Mods.All(m => m.Pair == null));
        }

        [Fact]
        public void ATargetsOwnKindOfModIsForItOnlyAndDeploysWhole()
        {
            PluginFiles.Install(Sample);
            var own = Path.Combine(Root, "mods", "Native");
            Directory.CreateDirectory(own);
            File.WriteAllText(Path.Combine(own, "sample_claim.txt"), "1");
            File.WriteAllText(Path.Combine(own, "code.c"), "int x;");
            Cli("add", own);
            var mod = Library.Load().Mods.Single();
            Assert.Equal("sample", mod.For);
            Assert.False(mod.Enabled);                                    // the official game is in use
            Assert.Equal("for_other_target", Refused("enable", "Native"));
            Assert.Empty(PluginHost.Mods(Library.Load()));                // the official game's plugins see no such mod
            Cli("target", "sample");
            Cli("enable", "Native");
            var listed = PluginHost.Mods(Library.Load()).Single();
            Assert.Equal(mod.Id, listed["id"]);
            Assert.Empty((List<object>)listed["files"]);                  // no patch files: the plugin takes the folder
            Cli("deploy");
            Assert.Contains("Native", File.ReadAllText(Path.Combine(Store.Home, "sample_target.txt")));
            // the official game's list never had it on, and its game folder is untouched
            Cli("target", "official");
            Assert.False(Library.Load().Mods.Single().Enabled);
            Cli("for", "Native", "both");
            Cli("enable", "Native");
            Assert.True(Library.Load().Mods.Single().Enabled);
            Cli("for", "Native", "sample");
            Assert.False(Library.Load().Mods.Single().Enabled);
        }
    }
}
