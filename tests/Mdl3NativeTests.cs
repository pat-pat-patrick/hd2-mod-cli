using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>MDL3's native part installed by the manager (the user, 2026-10-31): a fake package,
    /// %LOCALAPPDATA% in the test's folder, the signature check replaced.</summary>
    public class Mdl3Natives : Fixture
    {
        readonly string Local;
        string signedBy = "dll";

        public Mdl3Natives()
        {
            Local = Path.Combine(Root, "local");
            Directory.CreateDirectory(Local);
            Mdl3Native.LocalAppData = () => Local;
            // "signed" is what a test says: a DLL whose text starts with the word
            Mdl3Native.Signed = p => File.ReadAllText(p).StartsWith(signedBy);
            Mdl3Native.Last = null;
        }

        public override void Dispose()
        {
            Mdl3Native.LocalAppData = () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Mdl3Native.Signed = Mdl3Native.SignedByPublisher;
            base.Dispose();
        }

        static string Sha(string text)
        {
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(Bytes(text))).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>An MDL3 package as dev.21 ships it: BUILD.json, Native\the DLL, install.ps1, a boot.</summary>
        string Package(string name, string version, string dll, string build = null, string installer = null, string extraNative = null)
        {
            var files = Files(
                ("BUILD.json", build ?? "{\"native_path\":\"mdl_native_bridge.dll\",\"native_sha256\":\"" + Sha(dll) +
                               "\",\"native_version\":\"0.4.5\",\"requires_signature\":false,\"version\":\"" + version + "\"}"),
                ("Native/mdl_native_bridge.dll", dll),
                ("install.ps1", installer ?? "$folders=@('MDL3/Logs','MDL/Helldivers2/Mods','HD2Runtime/mod_data')"),
                ("MDL3/" + A + ".patch_0", "boot " + version));
            if (extraNative != null) files["Native/" + extraNative] = Bytes("x");
            return Mod(name, files, "{\"Guid\":\"b0ae9d35-4a69-5918-b43d-51d4d10ff001\",\"Name\":\"" + name + "\"," +
                                    "\"Options\":[{\"Name\":\"MDL3\",\"Include\":[\"MDL3\"]}]}");
        }

        string Installed => Path.Combine(Local, "MDL3", "mdl_native_bridge.dll");

        Dictionary<string, object> Config() =>
            (Dictionary<string, object>)Json.Parse(File.ReadAllText(Path.Combine(Local, "MDL3", "config.json")));

        [Fact]
        public void A_new_player_drags_it_in_and_it_is_installed()
        {
            var said = Cli("add", Package("MDL3 dev.21", "3.0.0-dev.21", "dll one"));
            Assert.Contains("native part 0.4.5 (MDL3 3.0.0-dev.21) installed", said);
            Assert.Equal("dll one", File.ReadAllText(Installed));
            var c = Config();
            Assert.Equal(Installed, Json.Str(c, "dll"));
            Assert.Equal(Game, Json.Str(c, "game_dir"));
            var state = (Dictionary<string, object>)Json.Parse(File.ReadAllText(Path.Combine(Local, "MDL3", "install-state.json")));
            Assert.Equal("MDL3", Json.Str(state, "product"));
            Assert.Equal("3.0.0-dev.21", Json.Str(state, "version"));
            // the folders install.ps1 names, read from its text
            Assert.True(Directory.Exists(Path.Combine(Local, "MDL3", "Logs")));
            Assert.True(Directory.Exists(Path.Combine(Local, "MDL", "Helldivers2", "Mods")));
            Assert.True(Directory.Exists(Path.Combine(Local, "HD2Runtime", "mod_data")));
            Assert.Equal("installed", Mdl3Native.Last.Kind);
            // nothing more to do: a deploy leaves it alone
            Assert.DoesNotContain("MDL3", Cli("deploy"));
        }

        [Fact]
        public void An_update_replaces_the_dll_and_keeps_the_settings()
        {
            Cli("add", Package("MDL3 dev.21", "3.0.0-dev.21", "dll one"));
            var c = Config();
            c["task_jit"] = "off";
            c["a_setting_of_a_later_version"] = 7L;
            File.WriteAllText(Path.Combine(Local, "MDL3", "config.json"), Json.Write(c, true));
            var said = Cli("add", Package("MDL3 dev.30", "3.0.0-dev.30", "dll two"));    // same Guid: a replace
            Assert.Contains("replaced", said);
            Assert.Contains("(MDL3 3.0.0-dev.30) installed", said);
            Assert.Equal("dll two", File.ReadAllText(Installed));
            c = Config();
            Assert.Equal("off", Json.Str(c, "task_jit"));
            Assert.Equal(7L, Json.Long(c, "a_setting_of_a_later_version"));
            // the one before is in a backup, as install.ps1 keeps it
            var backup = Directory.GetDirectories(Path.Combine(Local, "MDL3", "Backups")).OrderBy(d => d).Last();
            Assert.Equal("dll one", File.ReadAllText(Path.Combine(backup, "mdl_native_bridge.dll")));
        }

        [Fact]
        public void A_dll_not_signed_by_the_publisher_is_not_installed()
        {
            signedBy = "nobody";
            var said = Cli("add", Package("MDL3 dev.21", "3.0.0-dev.21", "dll one"));
            Assert.Contains("not installed", said);
            Assert.Contains("not signed by " + Mdl3Native.Publisher, said);
            Assert.False(File.Exists(Installed));
            Assert.Equal("failed", Mdl3Native.Last.Kind);
        }

        [Fact]
        public void What_a_later_version_asks_for_and_this_cannot_follow_goes_to_its_own_installer()
        {
            var said = Cli("add", Package("MDL3 dev.40", "3.0.0-dev.40", "dll one", extraNative: "mdl_helper.dll"));
            Assert.Contains("more than one native file", said);
            Assert.Contains("Run the installer in", said);
            Assert.False(File.Exists(Installed));
            var dll = "dll one";
            said = Cli("add", Package("MDL3 dev.41", "3.0.0-dev.41", dll,
                build: "{\"native_path\":\"mdl_native_bridge.dll\",\"native_sha256\":\"" + Sha(dll) + "\",\"requires_signature\":true,\"version\":\"3.0.0-dev.41\"}"));
            Assert.Contains("requires_signature", said);
            Assert.False(File.Exists(Installed));
        }

        [Fact]
        public void A_dll_that_is_not_the_one_build_json_names_is_refused()
        {
            var said = Cli("add", Package("MDL3 dev.21", "3.0.0-dev.21", "dll one",
                build: "{\"native_path\":\"mdl_native_bridge.dll\",\"native_sha256\":\"" + Sha("other") + "\",\"requires_signature\":false,\"version\":\"3.0.0-dev.21\"}"));
            Assert.Contains("not the one BUILD.json names", said);
            Assert.False(File.Exists(Installed));
        }

        [Fact]
        public void With_the_game_running_it_waits_for_the_deploy()
        {
            Deploy.GameRunning = () => true;
            var said = Cli("add", Package("MDL3 dev.21", "3.0.0-dev.21", "dll one"));
            Assert.Contains("installed at the next deploy", said);
            Assert.False(File.Exists(Installed));
            Assert.Equal("waits", Mdl3Native.Last.Kind);
            Deploy.GameRunning = () => false;
            Assert.Contains("(MDL3 3.0.0-dev.21) installed", Cli("deploy"));
            Assert.Equal("dll one", File.ReadAllText(Installed));
        }

        [Fact]
        public void A_rollback_or_another_game_folder_is_put_right_at_the_deploy()
        {
            Cli("add", Package("MDL3 dev.21", "3.0.0-dev.21", "dll one"));
            var c = Config();
            c["game_dir"] = Path.Combine(Root, "elsewhere");
            File.WriteAllText(Path.Combine(Local, "MDL3", "config.json"), Json.Write(c, true));
            Assert.Contains("would install", Cli("deploy", "--dry-run"));
            Assert.Contains("installed", Cli("deploy"));
            Assert.Equal(Game, Json.Str(Config(), "game_dir"));
        }

        [Fact]
        public void A_disabled_mdl3_or_another_mod_installs_nothing()
        {
            Cli("add", Mod("Plain", Files(("x/" + A + ".patch_0", "plain"))));
            Assert.False(Directory.Exists(Path.Combine(Local, "MDL3")));
            Cli("add", Package("MDL3 dev.21", "3.0.0-dev.21", "dll one"));
            File.Delete(Installed);
            Cli("disable", "MDL3 dev.21");
            Assert.DoesNotContain("MDL3", Cli("deploy"));
            Assert.False(File.Exists(Installed));
        }

        [Fact]
        public void An_unsigned_file_fails_the_real_signature_check()
        {
            var f = Path.Combine(Root, "unsigned.dll");
            File.WriteAllText(f, "not a signed file");
            Assert.False(Mdl3Native.SignedByPublisher(f));
        }
    }
}
