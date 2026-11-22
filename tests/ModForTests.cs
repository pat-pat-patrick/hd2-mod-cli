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

        string Declared(string name, string targets, string guid = null, string body = null) =>
            Mod(name, Files((A + ".patch_0", body ?? name)),
                @"{""Guid"": """ + (guid ?? System.Guid.NewGuid().ToString()) + @""", ""Name"": """ + name + @"""" + (targets == null ? "" : @", ""Targets"": " + targets) + "}");

        /// <summary>A manifest's "Targets" says which games a mod is for (the user, 2026-11-15): the
        /// library takes it as Mod.For, the player is not asked, and the for command cannot change
        /// it. Names in any case; one name, that target only; several, each of them.</summary>
        [Fact]
        public void AManifestThatSaysItsTargetsDecidesThem()
        {
            PluginFiles.Install(Sample);
            Cli("add", Declared("Port", @"[""Sample""]"));
            Cli("add", Declared("Both", @"[""Official"", ""sample""]"));
            Cli("add", Declared("Vanilla", @"[""official""]"));
            Cli("add", Declared("Plain", null));
            Cli("add", Declared("Empty", "[]"));
            var lib = Library.Load();
            Mod Get(string n) => lib.Mods.Single(m => m.Name == n);
            Assert.Equal("sample", Get("Port").For);
            Assert.True(Get("Port").ForDeclared);
            Assert.False(Get("Port").Enabled);                           // the official game is in use
            Assert.Null(Get("Both").For);
            Assert.True(Get("Both").ForDeclared);
            Assert.True(Get("Both").Enabled);
            Assert.Equal("official", Get("Vanilla").For);
            Assert.True(Get("Vanilla").ForDeclared);
            // must live: no "Targets", or none named in it, and the player chooses, as before
            Assert.False(Get("Plain").ForDeclared);
            Assert.False(Get("Empty").ForDeclared);
            Cli("for", "Plain", "sample");
            Assert.Equal("sample", Library.Load().Mods.Single(m => m.Name == "Plain").For);
            // must die: a declared mod's target is not the command's to change
            Assert.Equal("for_declared", Refused("for", "Port", "both"));
            Assert.Equal("sample", Library.Load().Mods.Single(m => m.Name == "Port").For);
            // the state says so, for the window to show no choice
            Assert.Contains("\"for_declared\": true", Cli("state"));

            // a manifest changed in place counts at the next load, and a mod no longer for the
            // target in use goes off
            var both = Library.Load().Mods.Single(m => m.Name == "Both").Path;
            File.WriteAllText(Path.Combine(both, "manifest.json"), @"{""Guid"": ""cccccccc-0000-0000-0000-000000000009"", ""Name"": ""Both"", ""Targets"": [""Sample""], ""Description"": ""now the sample game's only""}");
            lib = Library.Load();
            Assert.Equal("sample", Get("Both").For);
            Assert.False(Get("Both").Enabled);
        }

        /// <summary>A mod added whose Guid the library has: a declared one is not asked about. For
        /// the same target, it is that build's new version; for another the library offers, it
        /// goes beside as that target's build.</summary>
        [Fact]
        public void ADeclaredBuildOfTheSameModIsNotAskedAbout()
        {
            PluginFiles.Install(Sample);
            Cli("add", Declared("Holo", @"[""Official""]", Guid1, "official build"));
            Cli("add", Declared("Holo port", @"[""Sample""]", Guid1, "the sample game's build"));
            var lib = Library.Load();
            Assert.Equal(2, lib.Mods.Count);
            var off = lib.Mods.Single(m => m.For == "official");
            var port = lib.Mods.Single(m => m.For == "sample");
            Assert.Equal(port.Id, off.Pair);
            // its new version replaces the port only
            var port2 = Declared("Holo port 2", @"[""Sample""]", Guid1, "the sample game's newer build");
            Cli("add", port2);
            lib = Library.Load();
            Assert.Equal(2, lib.Mods.Count);
            Assert.Equal(Path.GetFullPath(port2).TrimEnd('\\'), Path.GetFullPath(lib.Mods.Single(m => m.For == "sample").Path).TrimEnd('\\'));
            // must die: the same mod undeclared is still asked about
            Assert.Equal("same_guid", Refused("add", Build("Holo again", "which one?")));
        }

        /// <summary>A mod's first build that declares its targets, added over an earlier version
        /// that declared none (the user, 2026-11-20: Boot Mod Loader 0.14.1 went beside 0.13.4
        /// as its official build, switched off, and 0.13.4 kept running). That entry, for both
        /// games, in no pair and with no target chosen, is replaced like any new version.</summary>
        [Theory]
        [InlineData(false, @"[""Official""]")]
        [InlineData(true, @"[""Official""]")]
        [InlineData(true, @"[""Sample""]")]
        [InlineData(true, @"[""Official"", ""Sample""]")]
        public void AFirstDeclaredBuildReplacesTheUndeclaredVersion(bool plugin, string targets)
        {
            if (plugin) PluginFiles.Install(Sample);
            Cli("add", Declared("Loader v1", null, Guid1, "old build"));
            var v2 = Declared("Loader v2", targets, Guid1, "new build");
            Assert.Contains("replaced", Cli("add", v2));
            var lib = Library.Load();
            var mod = lib.Mods.Single();
            Assert.Equal(Path.GetFullPath(v2).TrimEnd('\\'), Path.GetFullPath(mod.Path).TrimEnd('\\'));
            Assert.Null(mod.Pair);
            Assert.True(mod.ForDeclared);
            Assert.Equal(mod.For == null || mod.For == "official", mod.Enabled);   // on wherever the game in use is its target
        }

        /// <summary>Must live: an undeclared entry the player put on another target, or one already
        /// in a pair, is not the new build's earlier version; a declared build goes beside it.</summary>
        [Fact]
        public void ADeclaredBuildStillGoesBesideAnotherTargetsEntry()
        {
            PluginFiles.Install(Sample);
            Cli("add", Declared("Holo v1", null, Guid1, "old build"));
            Cli("for", "Holo v1", "sample");
            Assert.DoesNotContain("replaced", Cli("add", Declared("Holo v2", @"[""Official""]", Guid1, "new build")));
            var lib = Library.Load();
            Assert.Equal(2, lib.Mods.Count);
            var off = lib.Mods.Single(m => m.For == "official");
            var port = lib.Mods.Single(m => m.For == "sample");
            Assert.Equal(port.Id, off.Pair);
            Assert.True(off.Enabled);
            Assert.False(port.Enabled);
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
    
        [Fact]
        public void EachTargetHasItsOwnLoader()
        {
            PluginFiles.Install(Sample);
            var bml = Mod("Boot Mod Loader", Files((A + ".patch_0", "bml")), @"{""Guid"": ""cccccccc-0000-0000-0000-000000000001"", ""Name"": ""Boot Mod Loader""}");
            Cli("add", bml);
            var lib = Library.Load();
            var loader = lib.Mods.Single();
            Assert.Equal(loader.Id, Loaders.Chosen(lib, Store.Config())?.Id ?? loader.Id);
            Cli("for", "Boot Mod Loader", "official");
            Cli("target", "sample");
            lib = Library.Load();
            Assert.Null(Loaders.Chosen(lib, Store.Config()));                // the official game's loader is not the sample game's
            Cli("target", "official");
            Assert.NotNull(Loaders.Chosen(Library.Load(), Store.Config()) ?? Library.Load().Mods.Single());
        }
}
}
