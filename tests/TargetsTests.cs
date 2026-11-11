using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>Deploy targets (Targets, PluginTargets): each target keeps its own list; a plugin's
    /// target gets the deploy, the game folder is not touched. On HD2QD.Sample.dll's target "sample".</summary>
    public class DeployTargets : Fixture
    {
        static string Sample => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HD2QD.Sample.dll");

        public DeployTargets()
        {
            PluginFiles.Folder = null;
            PluginFiles.OfficialJson = () => null;
        }

        string AddTwo()
        {
            var a = Mod("Alpha", Files(("Main/" + A + ".patch_0", "alpha")),
                        @"{""Guid"": ""aaaaaaaa-0000-0000-0000-000000000001"", ""Name"": ""Alpha"", ""Options"": [{""Name"": ""Main"", ""Include"": [""Main""]},
                           {""Name"": ""Extra"", ""Include"": [""Extra""]}]}");
            Mod("Alpha", Files(("Extra/" + B + ".patch_0", "alpha extra")));
            var b = Mod("Beta", Files((A + ".patch_0", "beta")));
            Cli("add", a);
            Cli("add", b);
            return a;
        }

        /// <summary>What the game's Lua cannot run follows the target in use (the user, 2026-11-12:
        /// on HellForge the window still said the game does not run LuaJIT bytecode). The official
        /// game's Lua 5.1 refuses LuaJIT bytecode, its libraries and its syntax; a target whose
        /// plugin says "luajit" (HellForge's older game) refuses only code LuaJIT cannot compile.</summary>
        [Fact]
        public void WhatTheGamesLuaCannotRunFollowsTheTarget()
        {
            AddTwo();
            PluginFiles.Install(Sample);
            var lib = Library.Load();
            Assert.Null(LuaMods.GameLua(lib));
            Cli("target", "sample");
            Assert.Equal("luajit", LuaMods.GameLua(Library.Load()));

            var scripts = new List<LuaScript>
            {
                new LuaScript { Name = "bytecode", Bytecode = true, LuaJitBytecode = true },
                new LuaScript { Name = "ffi", Jit = { "ffi" } },
                new LuaScript { Name = "number", Error = (3, "malformed number near '0x1ULL'"), ErrorKind = "luajit_number" },
                new LuaScript { Name = "goto", Error = (5, "'=' expected near 'continue'"), ErrorKind = "goto" },
                new LuaScript { Name = "slash", Error = (7, "unexpected symbol near '/'"), ErrorKind = "new_operator" },
            };
            var official = new ModLua { Scripts = scripts };
            Assert.Equal(new[] { "bytecode", "ffi", "number", "goto", "slash" }, official.Broken.Select(s => s.Name));
            var luajit = new ModLua { Scripts = scripts, OnLuaJit = true };
            Assert.Equal(new[] { "slash" }, luajit.Broken.Select(s => s.Name));

            PluginFiles.Remove("HD2QD.Sample.dll");                          // no plugin: the official game's Lua
            Assert.Null(LuaMods.GameLua(Library.Load()));
        }

        [Fact]
        public void EachTargetKeepsItsOwnList()
        {
            AddTwo();
            PluginFiles.Install(Sample);
            Assert.Contains("* official", Cli("targets"));
            Assert.Contains("sample", Cli("targets"));
            Cli("deploy");
            var official = Deployed();
            Assert.Equal(3, official.Count(f => Deploy.Patch.IsMatch(f)));

            // HellForge's list starts as the official one; changed there, the official one stays
            Assert.Contains("(was official)", Cli("target", "sample"));
            Cli("disable", "Beta");
            Cli("set", "Alpha", "Extra", "off");
            var lib = Library.Load();
            Assert.Equal("sample", lib.Target);
            Assert.False(lib.Mods.Single(m => m.Name == "Beta").Enabled);

            // the deploy goes to the plugin, with the options chosen; the game folder is not touched
            var said = Cli("deploy");
            Assert.Contains("sample: 1 mods", said);
            var deployed = File.ReadAllLines(Path.Combine(Store.Home, "sample_target.txt"));
            Assert.Single(deployed);
            Assert.Contains("Alpha: " + A + ".patch_0", deployed[0]);
            Assert.DoesNotContain(B, deployed[0]);
            Assert.Equal(official, Deployed());

            // back to official: its list as it was left
            Cli("target", "official");
            lib = Library.Load();
            Assert.Null(lib.Target);
            Assert.True(lib.Mods.Single(m => m.Name == "Beta").Enabled);
            Assert.True(lib.Mods.Single(m => m.Name.StartsWith("Alpha")).Options.Single(o => o.Name == "Extra").Enabled);
            // and sample's again as it was left there
            Cli("target", "sample");
            Assert.False(Library.Load().Mods.Single(m => m.Name == "Beta").Enabled);

            // purge on the target purges the target only
            Assert.Contains("sample: purged", Cli("purge", "--yes"));
            Assert.Equal(official, Deployed());
        }

        [Fact]
        public void AModAddedOnOneTargetComesInOffOnTheOther()
        {
            AddTwo();
            PluginFiles.Install(Sample);
            Cli("target", "sample");
            var c = Mod("Gamma", Files((B + ".patch_0", "gamma")));
            Cli("add", c);
            Assert.True(Library.Load().Mods.Single(m => m.Name == "Gamma").Enabled);
            Cli("target", "official");
            var lib = Library.Load();
            Assert.False(lib.Mods.Single(m => m.Name == "Gamma").Enabled);
            Assert.Equal("Gamma", lib.Mods.Last().Name);
        }

        [Fact]
        public void TheStateSaysTheTargetAndWhatWaits()
        {
            AddTwo();
            PluginFiles.Install(Sample);
            var st = StateDoc.Build(Library.Load(), Store.GameDir(null, false));
            var t = Json.Obj(st, "target");
            Assert.Equal("official", Json.Str(t, "id"));
            Assert.Contains(Json.List(t, "offers").OfType<Dictionary<string, object>>(), o => Json.Str(o, "id") == "sample" && Json.Bool(o, "found"));
            Cli("target", "sample");
            t = Json.Obj(StateDoc.Build(Library.Load(), Store.GameDir(null, false)), "target");
            Assert.Equal(1L, Json.Long(t, "pending"));
            Cli("deploy");
            t = Json.Obj(StateDoc.Build(Library.Load(), Store.GameDir(null, false)), "target");
            Assert.Equal(0L, Json.Long(t, "pending"));
        }

        [Fact]
        public void ATargetNoPluginOffersCannotBeDeployed()
        {
            AddTwo();
            Assert.Equal("no_target", Refused("target", "sample"));
            PluginFiles.Install(Sample);
            Cli("target", "sample");
            File.WriteAllText(Path.Combine(Store.Home, "sample_missing.txt"), "1");
            Assert.Equal("target_missing", Refused("deploy"));
            PluginFiles.Remove("HD2QD.Sample.dll");
            Assert.Equal("no_target", Refused("deploy"));
            Assert.Contains("no added plugin provides it", Cli("targets"));
            Cli("target", "official");                    // the way back is always open
            Assert.Null(Library.Load().Target);
        }

        [Fact]
        public void ProfilesStayWithTheirTarget()
        {
            AddTwo();
            PluginFiles.Install(Sample);
            Cli("profiles-on");
            Cli("profile-new", "Raid", "--empty");
            Cli("target", "sample");
            Cli("profiles-off");
            Cli("target", "official");
            var lib = Library.Load();
            Assert.True(lib.ProfilesOn);
            Assert.Contains(lib.Profiles, p => p.Name == "Raid");
            Cli("target", "sample");
            Assert.False(Library.Load().ProfilesOn);
        }
    }
}
