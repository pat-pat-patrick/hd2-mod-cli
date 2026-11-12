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

        /// <summary>A target's game runs only the loaders its plugin names (the user, 2026-11-12:
        /// on HellForge the loader is Bingus Shared Loader). Bingus Shared Loader is known by the
        /// manifest it ships (v20 has no boot script) and runs without the start-up slot.</summary>
        [Fact]
        public void ATargetRunsOnlyTheLoadersItsPluginNames()
        {
            Cli("add", Mod("Boot Mod Loader", new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- Boot Mod Loader\nlocal MARKER = 'boot-mod-loader-0.13.4'\n")) }));
            var bsl = Mod("Bingus Shared Loader", new Dictionary<string, byte[]>
            {
                ["data/" + B + ".patch_0"] = LuaArchive((0x9BA626AFA44A3AA3UL, "local BingusRuntime = {}\n")),
                ["BingusSharedLoader-manifest.json"] = System.Text.Encoding.UTF8.GetBytes(@"{""name"": ""Bingus Shared Loader"", ""revision"": ""loader-v20"", ""display_version"": ""v20""}"),
            });
            Cli("add", bsl);
            Cli("add", Mod("Lamp", new Dictionary<string, byte[]> { [B + ".patch_0"] = LuaArchive((0x1111UL, "-- HD2-Addon: mods/me/lamp\nlocal x = 1\n")) }));
            PluginFiles.Install(Sample);
            File.WriteAllText(Path.Combine(Store.Home, "sample_loaders.txt"), "bsl");

            var lib = Library.Load();
            Assert.Equal("bsl", Loaders.KindOf(lib.Mods.Single(m => m.Name == "Bingus Shared Loader")));
            Assert.Equal(2, Loaders.Of(lib).Count);                                  // the official game runs any
            Cli("target", "sample");
            lib = Library.Load();
            Assert.Equal(new[] { "Bingus Shared Loader" }, Loaders.Of(lib).Select(m => m.Name));
            Assert.Equal("Bingus Shared Loader", Loaders.Chosen(lib)?.Name);
            Assert.Equal("loader_not_here", Refused("loader", "Boot Mod Loader"));

            var lua = Cli("lua");
            Assert.Contains("loader running: Bingus Shared Loader v20", lua);
            Assert.DoesNotContain("no loader runs", lua);
            Assert.DoesNotContain("Boot Mod Loader 0.13.4 (not used)", lua);        // not offered where it cannot run
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

        /// <summary>A target plugin that throws refuses: hd2qd ends with exit code 1 and the
        /// plugin's message, not an unhandled exception (2026-11-12: HellForge's "a session is
        /// running" ended hd2qd.exe, .NET Runtime event 1026).</summary>
        [Fact]
        public void APluginThatThrowsRefusesTheDeployAndThePurge()
        {
            AddTwo();
            PluginFiles.Install(Sample);
            Cli("target", "sample");
            File.WriteAllText(Path.Combine(Store.Home, "sample_refuse.txt"), "1");
            var err = new StringWriter();
            Assert.Equal(1, Commands.Run(new[] { "deploy" }, new StringWriter(), err, out var refused));
            Assert.Equal("plugin_failed", refused.Kind);
            Assert.Equal("a session is running", refused.Message);
            Assert.Contains("hd2qd: a session is running", err.ToString());
            Assert.Equal("plugin_failed", Refused("purge", "--yes"));
            Assert.False(File.Exists(Path.Combine(Store.Home, "sample_target.txt")));

            File.Delete(Path.Combine(Store.Home, "sample_refuse.txt"));
            Assert.Contains("sample: ", Cli("deploy"));
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
