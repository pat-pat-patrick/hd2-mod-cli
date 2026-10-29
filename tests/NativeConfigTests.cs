using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    public class NativeConfigTests : Fixture
    {
        const string Entities = "game/generated_entities.dl_bin", Balls = "game/generated_stratagem_settings.dl_bin";
        readonly Dictionary<string, string> ids = new Dictionary<string, string>();
        readonly Dictionary<string, string> sources = new Dictionary<string, string>();

        public override void Dispose() { NativeConfigs.BeforeReplace = null; base.Dispose(); }

        Dictionary<string, object> Feature(string name)
        {
            var guid = Guid.NewGuid().ToString();
            var manifest = "{\"Version\":1,\"Guid\":\"" + guid + "\",\"Name\":\"" + name + "\",\"Options\":[{\"Name\":\"Feature\",\"Include\":[\"Install\"]},{\"Name\":\"Helper\",\"Include\":[\"Helper\"]}]}";
            var folder = Mod(name, new Dictionary<string, byte[]>
            {
                ["Install/" + A + ".patch_0"] = LuaArchive((0xb5b0c0ffee5b5b11, "local payload = 1\n")),
                ["Helper/" + A + ".patch_0"] = LuaArchive((LuaMods.BootId, "local installer = 1\n")),
            }, manifest);
            Cli("add", folder);
            ids[name] = Library.Load().Mods.Single(m => m.Name == name).Id;
            return new Dictionary<string, object>
            {
                ["id"] = name, ["mod_id"] = ids[name], ["manifest_guid"] = guid, ["payload"] = "Install/" + A + ".patch_0",
                ["archives"] = new[] { "Install/" + A + ".patch_0", "Helper/" + A + ".patch_0" }.Select(p => (object)new Dictionary<string, object>
                { ["path"] = p, ["sha256"] = NativeConfigs.HashOf(Path.Combine(folder, p)) }).ToList(),
            };
        }

        Dictionary<string, object> Variant(string text, params string[] features)
        {
            var path = Path.Combine(Root, "image-" + sources.Count + ".bin");
            File.WriteAllBytes(path, Bytes(text));
            sources[text] = path;
            return new Dictionary<string, object> { ["features"] = features.Cast<object>().ToList(), ["file"] = path, ["sha256"] = NativeConfigs.HashOf(path) };
        }

        void Setup()
        {
            Directory.CreateDirectory(Path.Combine(Data, "game"));
            File.WriteAllBytes(Path.Combine(Data, Entities), Bytes("stock entities"));
            File.WriteAllBytes(Path.Combine(Data, Balls), Bytes("stock balls"));
            var vault = Feature("vault"); var npc = Feature("npc"); var balls = Feature("balls");
            Store.Save("native-configs.json", new Dictionary<string, object>
            {
                ["version"] = 1L, ["targets"] = new List<object>
                {
                    new Dictionary<string, object> { ["target"] = Entities, ["features"] = new List<object> { vault, npc },
                        ["variants"] = new List<object> { Variant("stock entities"), Variant("vault entities", "vault"), Variant("npc entities", "npc"), Variant("both entities", "npc", "vault") } },
                    new Dictionary<string, object> { ["target"] = Balls, ["features"] = new List<object> { balls },
                        ["variants"] = new List<object> { Variant("stock balls"), Variant("modified balls", "balls") } },
                },
            });
        }

        void Select(int mask)
        {
            foreach (var pair in new[] { ("vault", 1), ("npc", 2), ("balls", 4) })
                Cli((mask & pair.Item2) != 0 ? "enable" : "disable", ids[pair.Item1]);
        }

        [Fact]
        public void AllEightCombinationsAndReverseTransitionsKeepIndependentSwitches()
        {
            Setup();
            var originalRecipes = File.ReadAllBytes(Store.PathOf("native-configs.json"));
            foreach (int mask in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 6, 5, 4, 3, 2, 1, 0 })
            {
                Select(mask);
                var lib = Library.Load();
                Assert.Empty(Deploy.Plan(lib)); // no legacy payload or installer is sent to the game
                var before = File.ReadAllBytes(Path.Combine(Data, Entities));
                Deploy.Run(lib, Game, dryRun: true);
                Assert.Equal(before, File.ReadAllBytes(Path.Combine(Data, Entities)));
                Deploy.Run(lib, Game, verify: true);
                var expected = new[] { "stock entities", "vault entities", "npc entities", "both entities" }[mask & 3];
                Assert.Equal(Bytes(expected), File.ReadAllBytes(Path.Combine(Data, Entities)));
                Assert.Equal(Bytes((mask & 4) != 0 ? "modified balls" : "stock balls"), File.ReadAllBytes(Path.Combine(Data, Balls)));
                Assert.True(Deploy.Check(lib, Game, true).Ok);
                Assert.Equal(0, NativeConfigs.Prepare(lib, Game).Changes);
                Assert.Equal(0, Deploy.Run(lib, Game).NativeWritten);
                Assert.Empty(LuaMods.Of(lib));
            }
            Assert.Equal(originalRecipes, File.ReadAllBytes(Store.PathOf("native-configs.json")));
            Assert.Empty(Json.Obj(Store.Load("native-config-deployed.json"), "targets"));
        }

        [Fact]
        public void RemovingNativeInstallersKeepsLaterHudAndBootPatchesContiguous()
        {
            Setup();
            Cli("add", Mod("Earlier texture", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive((12ul, 1ul)) }));
            Cli("add", Mod("HUD", new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((123ul, "-- HD2-Addon: mods/test/hud\nlocal hud = true\n")) }));
            Cli("add", Mod("Chef", new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- Chef Shared Loader start-up stub 1.2.3\nlocal boot = true\n"), (LuaMods.ChefCoreId, "-- Chef Shared Loader 1.2.3\n")) }));
            var lib = Library.Load();
            var earlier = lib.Mods.Single(m => m.Name == "Earlier texture");
            lib.Mods.Remove(earlier); lib.Mods.Insert(0, earlier); lib.Save();
            foreach (int mask in Enumerable.Range(0, 8))
            {
                Select(mask); lib = Library.Load();
                var plan = Deploy.Plan(lib);
                var mains = plan.Where(w => Deploy.Patch.IsMatch(w.Name)).ToList();
                Assert.Equal(new[] { A + ".patch_0", A + ".patch_1", A + ".patch_2" }, mains.Select(w => w.Name));
                Assert.Equal(new[] { "Earlier texture", "HUD", "Chef" }, mains.Select(w => w.Mod));
                foreach (var main in mains)
                    foreach (var suffix in Deploy.Companions)
                        Assert.Contains(plan, w => w.Name == main.Name + suffix && w.Src == main.Src + suffix);
                Deploy.Run(lib, Game, verify: true);
                Assert.True(Deploy.Check(lib, Game, true).Ok);
                Assert.Equal(File.ReadAllBytes(mains[2].Src), File.ReadAllBytes(Path.Combine(Data, A + ".patch_2")));
            }
        }

        [Fact]
        public void UnknownFilesOrChangedCompositionAreRefusedBeforePatchDeployment()
        {
            Setup(); Select(7);
            Cli("add", Mod("Texture", new Dictionary<string, byte[]> { [B + ".patch_0"] = Archive(Bytes("texture"), (12ul, 42ul)) }));
            File.WriteAllBytes(Path.Combine(Data, Entities), Bytes("external unknown edit"));
            Assert.Throws<ModError>(() => Deploy.Run(Library.Load(), Game));
            Assert.False(File.Exists(Path.Combine(Data, B + ".patch_0")));
            Assert.Equal(Bytes("stock balls"), File.ReadAllBytes(Path.Combine(Data, Balls)));
            File.WriteAllBytes(Path.Combine(Data, Entities), Bytes("stock entities"));
            File.WriteAllBytes(sources["both entities"], Bytes("tampered image"));
            Assert.Throws<ModError>(() => Deploy.Run(Library.Load(), Game));
            Assert.Equal(Bytes("stock entities"), File.ReadAllBytes(Path.Combine(Data, Entities)));
        }

        [Fact]
        public void FailureOnSecondConfigRollsBackFirstAndLeavesOwnershipUnchanged()
        {
            Setup(); Select(7);
            NativeConfigs.BeforeReplace = target => { if (target == Balls) throw new IOException("injected second replacement failure"); };
            Assert.Throws<IOException>(() => Deploy.Run(Library.Load(), Game));
            Assert.Equal(Bytes("stock entities"), File.ReadAllBytes(Path.Combine(Data, Entities)));
            Assert.Equal(Bytes("stock balls"), File.ReadAllBytes(Path.Combine(Data, Balls)));
            Assert.Empty(Store.Load("native-config-deployed.json"));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(Data, "game"), "*.hd2qd-native-*"));
        }

        [Fact]
        public void OptionOffAndRemovingEntireModsRestoreWithoutAnUninstallHelper()
        {
            Setup(); Select(7); Deploy.Run(Library.Load(), Game);
            Cli("set", ids["npc"], "Feature", "off");
            Deploy.Run(Library.Load(), Game);
            Assert.Equal(Bytes("vault entities"), File.ReadAllBytes(Path.Combine(Data, Entities)));
            var lib = Library.Load(); lib.Mods.Clear(); lib.Save();
            Deploy.Run(Library.Load(), Game);
            Assert.Equal(Bytes("stock entities"), File.ReadAllBytes(Path.Combine(Data, Entities)));
            Assert.Equal(Bytes("stock balls"), File.ReadAllBytes(Path.Combine(Data, Balls)));
        }

        [Fact]
        public void PurgeRestoresNativeFilesAndGameRunningBlocksNativeOnlyChanges()
        {
            Setup(); Select(7);
            Deploy.GameRunning = () => true;
            Assert.Throws<ModError>(() => Deploy.Run(Library.Load(), Game));
            Deploy.GameRunning = () => false;
            Deploy.Run(Library.Load(), Game);
            Deploy.Purge(Game);
            Assert.Equal(Bytes("stock entities"), File.ReadAllBytes(Path.Combine(Data, Entities)));
            Assert.Equal(Bytes("stock balls"), File.ReadAllBytes(Path.Combine(Data, Balls)));
        }

        [Fact]
        public void LegacyOwnershipIsAdoptedAndOnlyControlledEntriesAreRemoved()
        {
            Setup(); Select(7);
            File.WriteAllBytes(Path.Combine(Data, Entities), Bytes("vault entities"));
            var legacy = Path.Combine(Game, "HD2FileMods.state");
            File.WriteAllText(legacy, "data/" + Entities + "|Vaulting|old-mod|old-stock\n" + "data/game/other.dl_bin|Other|x|y\n");
            Deploy.Run(Library.Load(), Game);
            Assert.Equal(Bytes("both entities"), File.ReadAllBytes(Path.Combine(Data, Entities)));
            Assert.Equal("data/game/other.dl_bin|Other|x|y", File.ReadAllText(legacy));
            Select(0); Deploy.Run(Library.Load(), Game);
            Assert.Equal(Bytes("stock entities"), File.ReadAllBytes(Path.Combine(Data, Entities)));
        }

        [Fact]
        public void UnregisteredPayloadOrChangedModIsRefusedInsteadOfSilentlyOverridden()
        {
            Setup(); Select(1);
            var own = Path.Combine(Library.Load().Mods.Single(m => m.Id == ids["vault"]).Path, "Install", A + ".patch_0");
            var original = File.ReadAllBytes(own);
            File.AppendAllText(own, "changed version");
            Assert.Throws<ModError>(() => Deploy.Run(Library.Load(), Game));
            File.WriteAllBytes(own, original);
            var header = Bytes("name=Unknown\nsize=4\ntarget=data/" + Entities + "\n");
            var body = Bytes("HD2FM1").Concat(BitConverter.GetBytes((uint)header.Length)).Concat(header).Concat(new byte[4]).ToArray();
            var resource = BitConverter.GetBytes((uint)body.Length).Concat(BitConverter.GetBytes(2u)).Concat(body).ToArray();
            var archive = Archive(resource, (Deploy.LuaType, 0xb5b0c0ffee5b5b16));
            BitConverter.GetBytes(Deploy.LuaType).CopyTo(archive, 0x48 + 8);
            Cli("add", Mod("Unknown file mod", new Dictionary<string, byte[]> { [A + ".patch_0"] = archive }));
            Assert.Throws<ModError>(() => Deploy.Run(Library.Load(), Game));
            Assert.Equal(Bytes("stock entities"), File.ReadAllBytes(Path.Combine(Data, Entities)));
        }

        [Fact]
        public void ReimportFindsNativeModByManifestAndAdoptsAnAlreadyMatchingFile()
        {
            Setup(); Select(7);
            var lib = Library.Load(); var npc = lib.Mods.Single(m => m.Id == ids["npc"]);
            lib.Mods.Remove(npc); lib.Save();
            Cli("add", npc.Path);
            File.WriteAllBytes(Path.Combine(Data, Entities), Bytes("both entities"));
            File.WriteAllBytes(Path.Combine(Data, Balls), Bytes("modified balls"));
            Assert.Equal(2, NativeConfigs.Prepare(Library.Load(), Game).Changes); // ownership must still be adopted
            Deploy.Run(Library.Load(), Game);
            var newId = Library.Load().Mods.Single(m => m.Name == "npc").Id;
            Assert.NotEqual(ids["npc"], newId);
            Cli("disable", newId); Deploy.Run(Library.Load(), Game);
            Assert.Equal(Bytes("vault entities"), File.ReadAllBytes(Path.Combine(Data, Entities)));
        }
    }
}
