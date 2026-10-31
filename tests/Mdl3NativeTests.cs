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
        static readonly Func<string, string> RealRedirect = Mdl3Native.Redirected;
        string Local;
        string signedBy = "dll";

        public Mdl3Natives()
        {
            Local = Path.Combine(Root, "local");
            Directory.CreateDirectory(Local);
            Mdl3Native.LocalAppData = () => Local;
            // "signed" is what a test says: a DLL whose text starts with the word
            Mdl3Native.Signed = p => File.ReadAllText(p).StartsWith(signedBy);
            Mdl3Native.Redirected = f => null;
            Mdl3Native.Last = null;
        }

        public override void Dispose()
        {
            Mdl3Native.LocalAppData = () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Mdl3Native.Signed = Mdl3Native.SignedByPublisher;
            Mdl3Native.Redirected = RealRedirect;
            base.Dispose();
        }

        static string Sha(string text)
        {
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(Bytes(text))).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>An MDL3 package as dev.21 ships it: BUILD.json, Native\the DLL, install.ps1, a boot.</summary>
        string Package(string name, string version, string dll, string build = null, string installer = null, string extraNative = null,
                       string boot = null, string guid = "b0ae9d35-4a69-5918-b43d-51d4d10ff001")
        {
            var files = Files(
                ("BUILD.json", build ?? "{\"native_path\":\"mdl_native_bridge.dll\",\"native_sha256\":\"" + Sha(dll) +
                               "\",\"native_version\":\"0.4.5\",\"requires_signature\":false,\"version\":\"" + version + "\"}"),
                ("Native/mdl_native_bridge.dll", dll),
                ("install.ps1", installer ?? "$folders=@('MDL3/Logs','MDL/Helldivers2/Mods','HD2Runtime/mod_data')"),
                ("MDL3/" + A + ".patch_0", "boot " + version));
            if (extraNative != null) files["Native/" + extraNative] = Bytes("x");
            if (boot != null) files["MDL3/" + A + ".patch_0"] = LuaArchive((LuaMods.BootId, boot));
            return Mod(name, files, "{\"Guid\":\"" + guid + "\",\"Name\":\"" + name + "\"," +
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
            // the folders MDL3's boot writes into first (dev.21), not install.ps1's list
            foreach (var f in new[] { "MDL3/Logs", "MDL/Helldivers2/Mods", "MDL/Helldivers2/Logs", "MDL/Helldivers2/Languages/Options",
                                      "CowboyBingus/Helldivers2/Logs", "HD2Runtime/mod_data" })
                Assert.True(Directory.Exists(Path.Combine(Local, f)), f);
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
        public void Writes_that_windows_sends_elsewhere_install_nothing()
        {
            Mdl3Native.Redirected = f => Path.Combine(Root, "LocalCache", "MDL3");
            var said = Cli("add", Package("MDL3 dev.21", "3.0.0-dev.21", "dll one"));
            Assert.Contains("where the game does not look", said);
            Assert.False(File.Exists(Installed));
        }

        [Fact]
        public void A_path_the_games_lua_cannot_open_is_written_as_its_short_name()
        {
            Local = Path.Combine(Root, "用户", "Local");
            Directory.CreateDirectory(Local);
            var said = Cli("add", Package("MDL3 dev.21", "3.0.0-dev.21", "dll one"));
            if (said.Contains("no 8.3 short name"))
            {
                Assert.False(File.Exists(Installed));          // a volume without short names: refused
                return;
            }
            Assert.Contains("installed", said);
            var dll = Json.Str(Config(), "dll");
            Assert.True(dll.All(c => c < 127), dll);
            Assert.Equal(File.ReadAllText(Installed), File.ReadAllText(dll));
            Assert.DoesNotContain("MDL3", Cli("deploy"));                // the short name is the same place
        }

        /// <summary>MDL3 dev.25 (2026-11-01) ships its boot obfuscated: no first line names it. Its
        /// package does: a native DLL signed by MDL3's publisher (the user: the DLL is what counts).</summary>
        [Fact]
        public void An_obfuscated_boot_is_mdl3s_by_its_signed_package()
        {
            const string obfuscated = "local e,K do local T=2 local s=0 local S=table.remove end\n";
            Mod Named(string n) => Library.Load().Mods.Single(m => m.Name == n);
            Cli("add", Package("MDL3 dev.25", "3.0.0-dev.25", "dll one", boot: obfuscated));
            Assert.Equal("mdl3", Loaders.KindOf(Named("MDL3 dev.25")));
            Assert.Equal("3.0.0-dev.25", Loaders.VersionOf(Named("MDL3 dev.25")));
            var lib = Library.Load();
            Assert.Equal("mdl3", LuaMods.Check(lib, LuaMods.Of(lib)).Running);
            Assert.Contains("start-up script (boot): MDL3 dev.25 (MDL3 3.0.0-dev.25)", Cli("lua"));

            // the signed DLL decides alone: a BUILD.json with another hash or none still is MDL3
            Cli("add", Package("Other hash", "3.0.0-dev.26", "dll three", boot: obfuscated, guid: "22222222-2222-2222-2222-222222222222",
                               build: "{\"native_path\":\"mdl_native_bridge.dll\",\"native_sha256\":\"" + Sha("dll four") + "\",\"version\":\"3.0.0-dev.26\"}"));
            Assert.Equal("mdl3", Loaders.KindOf(Named("Other hash")));
            Assert.Equal("3.0.0-dev.26", Loaders.VersionOf(Named("Other hash")));
            Cli("add", Mod("No BUILD.json", new Dictionary<string, byte[]>
            {
                ["Native/mdl_native_bridge.dll"] = Bytes("dll five"), [A + ".patch_0"] = LuaArchive((LuaMods.BootId, obfuscated)),
            }));
            Assert.Equal("mdl3", Loaders.KindOf(Named("No BUILD.json")));

            // must fail: the same boot with a DLL not signed by the publisher, and with no DLL at all
            signedBy = "nobody";
            Cli("add", Package("Unsigned", "3.0.0-dev.25", "dll two", boot: obfuscated, guid: "11111111-1111-1111-1111-111111111111"));
            Assert.Null(Loaders.KindOf(Named("Unsigned")));
            signedBy = "dll";
            Cli("add", Mod("Boot only", new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, obfuscated)) }));
            Assert.Null(Loaders.KindOf(Named("Boot only")));
        }

        /// <summary>The user, 2026-11-01: a player's MDL3 did nothing, and the report could not say
        /// whether the native part was there.</summary>
        [Fact]
        public void The_report_says_whether_the_native_part_is_there_and_holds_the_logs()
        {
            LuaMods.GameFolderOverride = Path.Combine(Root, "game logs");
            try
            {
                Cli("add", Package("MDL3 dev.21", "3.0.0-dev.21", "dll one"));
                File.WriteAllText(Path.Combine(Local, "MDL3", "Logs", "MDL3.log"), "native host started");
                File.WriteAllText(Path.Combine(Local, "MDL", "Helldivers2", "Logs", "MDL.log"), "mods started");
                var bingus = Path.Combine(Local, "CowboyBingus", "Helldivers2", "Logs");
                File.WriteAllText(Path.Combine(bingus, "HD2DataHub.log"), "E_SERIALIZE codec: structure limit");
                File.WriteAllText(Path.Combine(bingus, "Old.log"), "a month ago");
                File.SetLastWriteTime(Path.Combine(bingus, "Old.log"), DateTime.Now.AddDays(-30));

                var (zip, summary) = Report.Write(Library.Load(), Game, Path.Combine(Root, "r1.zip"));
                Assert.Contains("MDL3 native part: installed (MDL3 3.0.0-dev.21, native 0.4.5)", summary);
                Dictionary<string, string> e;
                using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
                    e = z.Entries.ToDictionary(x => x.FullName, x => new StreamReader(x.Open()).ReadToEnd());
                Assert.Contains("MDL3 native part: installed", e["info.txt"]);
                var native = (Dictionary<string, object>)Json.Parse(e["mdl3/native.json"]);
                Assert.Equal("installed", Json.Str(native, "native_part"));
                var dll = Json.List(native, "files").OfType<Dictionary<string, object>>().Single(f => Json.Str(f, "name") == "mdl_native_bridge.dll");
                Assert.Equal(Sha("dll one"), Json.Str(dll, "sha256"));
                Assert.True(Json.Bool(dll, "signed"));
                Assert.Equal("3.0.0-dev.21", Json.Str(Json.Obj(native, "install_state"), "version"));
                Assert.Equal("native host started", e["mdl3/MDL3/Logs/MDL3.log"]);
                Assert.Equal("mods started", e["mdl3/MDL/Helldivers2/Logs/MDL.log"]);
                Assert.Contains("structure limit", e["mdl3/CowboyBingus/Helldivers2/Logs/HD2DataHub.log"]);
                Assert.DoesNotContain("mdl3/CowboyBingus/Helldivers2/Logs/Old.log", e.Keys);

                // the player who never ran MDL3's installer
                File.Delete(Installed);
                Assert.Contains("MDL3 native part: not installed (MDL3 3.0.0-dev.21", Report.Write(Library.Load(), Game, Path.Combine(Root, "r2.zip")).Summary);
            }
            finally { LuaMods.GameFolderOverride = null; }
        }

        [Fact]
        public void A_report_without_mdl3_says_nothing_of_it()
        {
            LuaMods.GameFolderOverride = Path.Combine(Root, "game logs");
            try
            {
                Cli("add", Mod("Cape", Files((A + ".patch_0", "cape"))));
                var (zip, summary) = Report.Write(Library.Load(), Game, Path.Combine(Root, "r.zip"));
                Assert.DoesNotContain("MDL3", summary);
                using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
                    Assert.DoesNotContain(z.Entries, x => x.FullName.StartsWith("mdl3/"));
            }
            finally { LuaMods.GameFolderOverride = null; }
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
