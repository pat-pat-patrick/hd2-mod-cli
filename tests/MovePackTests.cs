using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>The move package (MovePack; the user, 2026-11-25: "一键搬迁"): one computer's whole
    /// setup packed into a file, put in place on another with every path made over.</summary>
    public class MovePackTests : Fixture
    {
        readonly string settingsHere, other, otherGame, otherSettings;
        readonly long keptLimit = MovePack.PartLimit;

        public MovePackTests()
        {
            settingsHere = Path.Combine(Root, "appdata here");
            other = Path.Combine(Root, "other computer");
            otherGame = Path.Combine(other, "game");
            otherSettings = Path.Combine(other, "appdata");
            Directory.CreateDirectory(settingsHere);
            Directory.CreateDirectory(Path.Combine(otherGame, "data"));
            MovePack.GameSettingsOverride = settingsHere;
            MovePack.SplitAnywhere = true;
        }

        public override void Dispose()
        {
            MovePack.GameSettingsOverride = null;
            MovePack.PartLimit = keptLimit;
            MovePack.SplitAnywhere = false;
            base.Dispose();
        }

        static string IdOf(string name) => Library.Load().Mods.Single(m => m.Name == name).Id;

        /// <summary>Bytes that compress (text) and bytes that do not (noise), several pieces long.</summary>
        static byte[] Big(int size, bool noise)
        {
            var b = new byte[size];
            if (noise) new Random(7).NextBytes(b);
            else for (int i = 0; i < size; i++) b[i] = (byte)"helldivers never die "[i % 21];
            return b;
        }

        /// <summary>A library of three mods, as a player has it: an option picked, one off, a label,
        /// a plugin, the window's settings and two mods' settings files beside the game's own log.</summary>
        void APlayersSetup()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", new Dictionary<string, byte[]>
            {
                ["red/" + A + ".patch_0"] = Bytes("two red"),
                ["blue/" + A + ".patch_0"] = Bytes("two blue"),
                ["blue/" + A + ".patch_0.gpu_resources"] = Big(9 << 20, false),
                ["blue/" + A + ".patch_0.stream"] = Big(5 << 20, true),
            }, "{\"Name\": \"Two\", \"Options\": [{\"Name\": \"Colour\", \"SubOptions\": [" +
               "{\"Name\": \"Red\", \"Include\": [\"red\"]}, {\"Name\": \"Blue\", \"Include\": [\"blue\"]}]}]}"));
            Cli("add", Mod("Three", Files((B + ".patch_0", "three"), ("empty.txt", ""))));
            Cli("set", IdOf("Two"), "1.2", "on");
            Cli("disable", IdOf("Three"));
            Cli("tag", IdOf("One"), "Looks");
            Directory.CreateDirectory(Path.Combine(Store.Home, "plugins"));
            File.WriteAllText(Path.Combine(Store.Home, "plugins", "HD2QD.Fake.dll"), "plugin");
            Store.Save("gui.json", new Dictionary<string, object> { ["lang"] = "zh-CN", ["window"] = new Dictionary<string, object> { ["left"] = 5L } });
            File.WriteAllText(Path.Combine(settingsHere, "super_holo_hud_settings.txt"), "colour=blue");
            File.WriteAllText(Path.Combine(settingsHere, "TurretLaser.ini"), "[a]");
            File.WriteAllText(Path.Combine(settingsHere, "console.log"), "the game's own");
        }

        /// <summary>The other computer: a settings folder of its own, nothing in it yet.</summary>
        void OnTheOtherComputer()
        {
            Store.HomeOverride = Path.Combine(other, "home");
            MovePack.GameSettingsOverride = otherSettings;
        }

        static Dictionary<string, string> Tree(string root) =>
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                     .ToDictionary(f => f.Substring(root.Length + 1), f => Convert.ToBase64String(System.Security.Cryptography.SHA256.Create().ComputeHash(File.ReadAllBytes(f))));

        /// <summary>Must live: packed in three parts (FAT32's limit made small), put in place on
        /// another computer: every mod's files byte for byte under the new mod folder, the order,
        /// on/off, option and label as they were, the plugin and the mods' settings files with
        /// them (not the game's log), the window's language but not its place on the old screen;
        /// a deploy then fills the new game folder.</summary>
        [Fact]
        public void A_setup_moves_whole_to_another_computer()
        {
            APlayersSetup();
            var before = Library.Load();
            var trees = before.Mods.ToDictionary(m => m.Name, m => Tree(m.Path));
            MovePack.PartLimit = 2 << 20;
            var pack = Path.Combine(Root, "usb", "move" + MovePack.Extension);
            var said = Cli("move-export", pack);
            Assert.Contains("packed: 3 mods", said);
            Assert.True(File.Exists(pack + ".2") && File.Exists(pack + ".3"), said);
            Assert.Empty(Directory.GetFiles(Path.Combine(Root, "usb"), "*.part"));
            var size = Directory.GetFiles(Path.Combine(Root, "usb")).Sum(f => new FileInfo(f).Length);
            Assert.True(size < (12L << 20), "the text compresses: " + size);   // 14 MB in, the noise stays 5 MB

            OnTheOtherComputer();
            Assert.Contains("mods: 3", Cli("move-info", pack));
            var into = Path.Combine(other, "HD2 Mods");
            said = Cli("move-restore", pack, "--game", otherGame, "--into", into);
            Assert.Contains("restored: 3 mods", said);

            var lib = Library.Load();
            Assert.Equal(new[] { "One", "Two", "Three" }, lib.Mods.Select(m => m.Name));
            Assert.Equal(new[] { true, true, false }, lib.Mods.Select(m => m.Enabled));
            Assert.Equal(before.Mods.Select(m => m.Id), lib.Mods.Select(m => m.Id));
            Assert.Equal(new[] { false, true }, lib.Mods.Single(m => m.Name == "Two").Options.Single().Subs.Select(s => s.Enabled));
            Assert.Equal(new[] { "Looks" }, lib.Mods.Single(m => m.Name == "One").Tags);
            foreach (var m in lib.Mods)
            {
                Assert.StartsWith(into + "\\", m.Path);
                Assert.Equal(trees[m.Name], Tree(m.Path));
            }
            var cfg = Store.Config();
            Assert.Equal(Paths.Abs(otherGame), Json.Str(cfg, "game_dir"));
            Assert.Equal(into, Json.Str(cfg, "mods_dir"));
            // a plugin runs as this program does: held for the window to add, never loaded from a package
            Assert.Equal("plugin", File.ReadAllText(Path.Combine(MovePack.Held, "HD2QD.Fake.dll")));
            Assert.False(File.Exists(Path.Combine(Store.Home, "plugins", "HD2QD.Fake.dll")));
            Assert.Contains("hd2qd plugin-add", said);
            Assert.Equal("colour=blue", File.ReadAllText(Path.Combine(otherSettings, "super_holo_hud_settings.txt")));
            Assert.True(File.Exists(Path.Combine(otherSettings, "TurretLaser.ini")));
            Assert.False(File.Exists(Path.Combine(otherSettings, "console.log")));
            var gui = Store.Load("gui.json");
            Assert.Equal("zh-CN", Json.Str(gui, "lang"));
            Assert.Null(Json.Obj(gui, "window"));
            Assert.Empty(Directory.GetFiles(Store.Home, "*.moving", SearchOption.AllDirectories));

            Cli("deploy");
            Assert.Equal("one", File.ReadAllText(Path.Combine(otherGame, "data", A + ".patch_0")));
            Assert.Equal("two blue", File.ReadAllText(Path.Combine(otherGame, "data", A + ".patch_1")));
            Assert.False(File.Exists(Path.Combine(otherGame, "data", B + ".patch_0")));      // Three is off
        }

        /// <summary>Must die: a package damaged on the way, one missing a part, a part of another
        /// package, and a library that already has mods (without --replace) are refused, and
        /// leave the library and the mod folder as they were.</summary>
        [Fact]
        public void A_package_that_is_not_whole_changes_nothing()
        {
            APlayersSetup();
            MovePack.PartLimit = 2 << 20;
            var usb = Path.Combine(Root, "usb");
            var pack = Path.Combine(usb, "move" + MovePack.Extension);
            Cli("move-export", pack);
            var otherPack = Path.Combine(Root, "usb2", "move" + MovePack.Extension);
            Cli("move-export", otherPack);
            OnTheOtherComputer();
            var into = Path.Combine(other, "HD2 Mods");

            // damaged: a byte changed in the middle of part 2
            var two = File.ReadAllBytes(pack + ".2");
            two[two.Length / 2] ^= 0x5A;
            File.WriteAllBytes(pack + ".2", two);
            Assert.Equal("move_damaged", Refused("move-restore", pack, "--game", otherGame, "--into", into));
            Assert.Empty(Library.Load().Mods);
            Assert.False(Directory.Exists(into) && Directory.EnumerateFileSystemEntries(into).Any());
            Assert.False(File.Exists(Path.Combine(otherSettings, "super_holo_hud_settings.txt")));

            // a part of another package in its place
            File.Copy(otherPack + ".2", pack + ".2", true);
            Assert.Equal("move_part_other", Refused("move-restore", pack, "--game", otherGame, "--into", into));

            // the other package's first two parts, its last left behind
            File.Copy(otherPack, pack, true);
            File.Delete(pack + ".3");
            Assert.Equal("move_short", Refused("move-restore", pack, "--game", otherGame, "--into", into));
            Assert.Empty(Library.Load().Mods);
            Assert.False(Directory.Exists(into) && Directory.EnumerateFileSystemEntries(into).Any());

            // whole again: restored; a second time is refused, the library has mods now
            Cli("move-restore", otherPack, "--game", otherGame, "--into", into);
            Assert.Equal(3, Library.Load().Mods.Count);
            Assert.Equal("move_not_empty", Refused("move-restore", otherPack, "--game", otherGame, "--into", into));
            // --replace: the second copy goes into folders of its own beside the first
            Cli("move-restore", otherPack, "--game", otherGame, "--into", into, "--replace");
            var lib = Library.Load();
            Assert.Equal(3, lib.Mods.Count);
            Assert.All(lib.Mods, m => Assert.EndsWith(" (2)", m.Path));
        }

        /// <summary>Must die: not a package at all; a folder given to move-export gets a file named
        /// for today in it.</summary>
        [Fact]
        public void Not_a_package_is_refused()
        {
            var junk = Path.Combine(Root, "junk" + MovePack.Extension);
            File.WriteAllText(junk, "nothing here, really nothing");
            Assert.Equal("move_not_pack", Refused("move-info", junk));
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            var dir = Path.Combine(Root, "out");
            Directory.CreateDirectory(dir);
            Cli("move-export", dir);
            Assert.Single(Directory.GetFiles(dir, "*" + MovePack.Extension));
        }

        [Fact]
        public void Only_the_mods_settings_files_go()
        {
            Assert.True(MovePack.IsModSetting("super_holo_hud_settings.txt"));
            Assert.True(MovePack.IsModSetting("ActionCam_profiles.txt"));
            Assert.True(MovePack.IsModSetting("ammo_lamp.ini"));
            Assert.True(MovePack.IsModSetting("chef_data_x__learned.lua"));
            Assert.False(MovePack.IsModSetting("console.log"));
            Assert.False(MovePack.IsModSetting("ammo_probe_live.lua"));
            Assert.False(MovePack.IsModSetting("dap_obj_01_after.bin"));
        }
    }
}
