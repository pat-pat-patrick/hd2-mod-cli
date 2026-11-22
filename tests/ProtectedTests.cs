using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>Large asset protection (Protected, Session), on HD2QD.Sample.dll's ModSource,
    /// PrepareDeploy and EndSession: a protected mod's files come from its stage folder; one that
    /// is not ready is left out and the rest deploys; a session mod is in the game only during a
    /// session, and turning it on or off moves the other files instead of writing them again.</summary>
    public class ProtectedMods : Fixture
    {
        static string Sample => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HD2QD.Sample.dll");

        public ProtectedMods()
        {
            PluginFiles.Folder = null;      // the settings folder's plugins\
            Session.Sleep = _ => { };
        }

        public override void Dispose()
        {
            Session.Sleep = System.Threading.Thread.Sleep;
            base.Dispose();
        }

        void AddSample() => Assert.Contains("added HD2QD.Sample.dll", Cli("plugin-add", Sample));

        string Stage => Path.Combine(Game, "hd2qd_stage");

        /// <summary>A mod whose companions hold bytes of their own (empty ones are all the same file
        /// to a deploy, which then moves one mod's companion to another's name).</summary>
        string Plain(string name, string rel, string body, string manifest = null)
        {
            var folder = Mod(name, Files((rel, body)), manifest);
            foreach (var ext in Deploy.Companions) File.WriteAllText(Path.Combine(folder, rel.Replace('/', '\\')) + ext, body + ext);
            return folder;
        }

        /// <summary>A protected mod: its license says deploy "game" in this mode, and what decrypting
        /// gives is in plain\ (the sample plugin copies it to the stage).</summary>
        string Protect(string name, string mode, string body, string rel = null, string manifest = null)
        {
            var folder = Plain(name, "plain/" + (rel ?? A + ".patch_0"), body, manifest);
            File.WriteAllText(Path.Combine(folder, "hd2_license.json"), J(new Dictionary<string, object>
            {
                ["protocol"] = 1L, ["issuer"] = "license.example", ["name"] = "Someone",
                ["payload"] = new Dictionary<string, object> { ["kind"] = "static", ["id"] = name.ToLowerInvariant(), ["deploy"] = "game", ["mode"] = mode },
            }));
            File.WriteAllText(Path.Combine(folder, "sample_protected.txt"), mode);
            return folder;
        }

        Dictionary<string, object> Row(string name) =>
            Json.List(StateDoc.Build(Library.Load(), Game, false), "mods").Cast<Dictionary<string, object>>().Single(m => Json.Str(m, "name") == name);

        static List<Dictionary<string, object>> Notes(Dictionary<string, object> row) =>
            Json.List(row, "notes").Cast<Dictionary<string, object>>().ToList();

        string Home(string file) => Path.Combine(Store.Home, file);

        static string PathOf(string name) => Library.Load().Mods.Single(m => m.Name == name).Path;

        /// <summary>A mod's stage folder as the sample plugin makes it.</summary>
        string StagedOf(string name) => Path.Combine(Stage, Path.GetFileName(PathOf(name)));

        [Fact]
        public void A_protected_mod_is_known_by_its_license_alone()
        {
            var prot = Protect("Prot", "session", "secret");
            Assert.Equal("session", Protected.ModeOf(prot));
            File.WriteAllText(Path.Combine(prot, "hd2_license.json"), "{\"payload\": {\"deploy\": \"game\"}}");
            Assert.Equal("deploy", Protected.ModeOf(prot));                  // no mode: deploy
            File.WriteAllText(Path.Combine(prot, "hd2_license.json"), "{\"payload\": {\"deploy\": \"licenses/x.bin\"}}");
            Assert.Null(Protected.ModeOf(prot));                             // member content of the other kind
            File.WriteAllText(Path.Combine(prot, "hd2_license.json"), "{not json");
            Assert.Null(Protected.ModeOf(prot));
            Assert.Null(Protected.ModeOf(Plain("One", B + ".patch_0", "one")));
            Assert.Equal(Path.Combine(Game, "hd2qd_stage"), PluginHost.Make()["stage"]);
        }

        /// <summary>Must live: a protected mod that is not ready is left out, said on its row, and
        /// the rest deploys (the window's deploy passes no errors, so an error would stop it).
        /// Must die: its files are taken from its stage folder, through its options' includes, never
        /// from the mod's own folder.</summary>
        [Fact]
        public void A_mod_not_ready_is_withheld_and_the_rest_deploys()
        {
            AddSample();
            var manifest = J(new Dictionary<string, object>
            {
                ["Version"] = 1L, ["Name"] = "Prot",
                ["Options"] = new List<object> { new Dictionary<string, object> { ["Name"] = "Main", ["Include"] = new List<object> { "opt" } } },
            });
            var prot = Protect("Prot", "deploy", "secret", "opt/" + A + ".patch_0", manifest);
            var one = Plain("One", B + ".patch_0", "one");
            Cli("add", prot);
            Cli("add", one);
            File.WriteAllText(Home("sample_locked.txt"), "1");

            var said = Cli("deploy");
            Assert.Contains("left out, not ready (protected): Prot", said);
            Assert.Equal(new[] { B + ".patch_0", B + ".patch_0.gpu_resources", B + ".patch_0.stream" }, Deployed());
            Assert.Equal("one", Body(B + ".patch_0"));
            var note = Notes(Row("Prot")).Single();
            Assert.Equal("Sample: not unlocked", Json.Str(note, "text"));     // the plugin's own words
            Assert.Null(Json.Str(Row("Prot"), "error"));
            Assert.Equal("deploy", Json.Str(Row("Prot"), "protected"));

            File.Delete(Home("sample_locked.txt"));
            said = Cli("deploy");
            Assert.DoesNotContain("left out", said);
            Assert.Equal("secret", Body(A + ".patch_0"));
            var staged = Path.Combine(StagedOf("Prot"), "opt", A + ".patch_0");
            Assert.True(Same(Path.Combine(Data, A + ".patch_0"), staged));    // a hard link to the stage
            Assert.Equal(staged, Json.Str(Json.Obj(Json.Obj(Store.Load("deployed.json"), "files"), A + ".patch_0"), "src"));
            Assert.Empty(Notes(Row("Prot")));
            Assert.False(Json.Bool(Json.Obj(Json.Obj(Store.Load("deployed.json"), "files"), A + ".patch_0"), "session"));
        }

        [Fact]
        public void Without_its_plugin_a_protected_mod_is_not_deployed()
        {
            var prot = Protect("Prot", "deploy", "secret");
            Cli("add", prot);
            Cli("add", Plain("One", B + ".patch_0", "one"));
            Cli("deploy");
            Assert.DoesNotContain(Deployed(), f => f.StartsWith(A));         // not even what lies in its own folder
            Assert.Equal("protected_no_plugin", Json.Str(Notes(Row("Prot")).Single(), "key"));
            Assert.DoesNotContain(Deploy.Plan(Library.Load()), w => w.Mod == "Prot");
        }

        /// <summary>A session mod outside a session is withheld; a session puts it in by hard links
        /// and moves the other mods' files to their new numbers, and its end moves them back and
        /// deletes the session's files, then asks the plugin to end it. Nothing is written twice.</summary>
        [Fact]
        public void A_session_on_and_off_only_renames_the_other_files()
        {
            AddSample();
            Cli("add", Protect("Prot", "session", "secret"));
            Cli("add", Plain("One", A + ".patch_0", "one"));
            Cli("deploy");
            Assert.Equal("one", Body(A + ".patch_0"));
            Assert.Equal("protected_session", Json.Str(Notes(Row("Prot")).Single(), "key"));
            Assert.False(Directory.Exists(StagedOf("Prot")));     // a deploy's prepare leaves session mods alone

            Session.Begin(Game);
            Assert.True(Protected.Prepare(Library.Load(), true));
            Assert.Equal("session", File.ReadAllLines(Home("sample_prepared.txt")).Last());
            var on = Deploy.Run(Library.Load(), Game);
            Assert.Equal((3, 3, 0, 3, 0), (on.Written, on.Linked, on.Copied, on.Moved, on.Deleted));
            Assert.Equal("secret", Body(A + ".patch_0"));
            Assert.Equal("one", Body(A + ".patch_1"));
            Assert.True(Same(Path.Combine(Data, A + ".patch_1"), Path.Combine(PathOf("One"), A + ".patch_0")));
            var files = Json.Obj(Store.Load("deployed.json"), "files");
            Assert.True(Json.Bool(Json.Obj(files, A + ".patch_0"), "session"));
            Assert.False(Json.Bool(Json.Obj(files, A + ".patch_1"), "session"));
            Assert.True(Json.Bool(StateDoc.Build(Library.Load(), Game), "session"));

            var off = Session.End(Library.Load(), Game);
            Assert.True(off.Done);
            Assert.Equal((0, 3, 3), (off.Deployed.Written, off.Deployed.Moved, off.Deployed.Deleted));
            Assert.Equal(new[] { A + ".patch_0", A + ".patch_0.gpu_resources", A + ".patch_0.stream" }, Deployed());
            Assert.Equal("one", Body(A + ".patch_0"));
            Assert.True(File.Exists(Home("sample_ended.txt")));
            Assert.False(Directory.Exists(StagedOf("Prot")));
            Assert.False(Session.Leftover());
        }

        /// <summary>The manager closed during a game: at its next start the session's files are
        /// found (the session still open, or only the marks in deployed.json) and taken out.</summary>
        [Fact]
        public void A_session_left_behind_is_ended()
        {
            AddSample();
            Cli("add", Protect("Prot", "session", "secret"));
            Cli("add", Plain("One", B + ".patch_0", "one"));
            Session.Begin(Game);
            Protected.Prepare(Library.Load(), true);
            Deploy.Run(Library.Load(), Game);
            Assert.Contains(A + ".patch_0", Deployed());
            Assert.True(Session.Leftover());

            Assert.True(Session.End(Library.Load(), Game).Done);
            Assert.DoesNotContain(A + ".patch_0", Deployed());
            Assert.Contains(B + ".patch_0", Deployed());

            // closed, but the deploy that would take its files out never ran
            Session.Begin(Game);
            Protected.Prepare(Library.Load(), true);
            Deploy.Run(Library.Load(), Game);
            Session.Close();
            Assert.False(Session.IsOpen());
            Assert.True(Session.Leftover());
            Assert.True(Session.End(Library.Load(), Game).Done);
            Assert.DoesNotContain(A + ".patch_0", Deployed());
            Assert.False(Session.Leftover());
        }

        /// <summary>The game running again when a session would end: nothing changes, the session's
        /// files stay marked for the next try.</summary>
        [Fact]
        public void A_session_end_waits_for_the_game()
        {
            AddSample();
            Cli("add", Protect("Prot", "session", "secret"));
            Session.Begin(Game);
            Protected.Prepare(Library.Load(), true);
            Deploy.Run(Library.Load(), Game);
            Deploy.GameRunning = () => true;
            var r = Session.End(Library.Load(), Game);
            Assert.Equal((false, "game_running"), (r.Done, r.Why));
            Assert.Contains(A + ".patch_0", Deployed());
            Assert.False(File.Exists(Home("sample_ended.txt")));               // its stage kept while data\ links to it
            Assert.True(Session.Leftover());
            Deploy.GameRunning = () => false;
            Assert.True(Session.End(Library.Load(), Game).Done);
            Assert.Empty(Deployed());
        }

        /// <summary>The plugin is asked once a protected mod a state read, even when it asks the
        /// host for the mods while it answers (which plans again).</summary>
        [Fact]
        public void ModSource_is_asked_once_a_mod_a_state_read()
        {
            AddSample();
            Cli("add", Protect("Prot", "deploy", "secret"));
            Cli("add", Protect("Prot2", "deploy", "secret2", B + ".patch_0"));
            Cli("add", Plain("One", "7ef5a4f8e5bc2d4c.patch_0", "one"));
            Cli("deploy");
            File.WriteAllText(Home("sample_reenter.txt"), "1");
            File.Delete(Home("sample_modsource.txt"));
            var st = StateDoc.Build(Library.Load(), Game);
            Assert.Equal(new[] { "Prot", "Prot2" }, File.ReadAllLines(Home("sample_modsource.txt")).OrderBy(x => x));
            Assert.Equal(0L, Json.Long(Json.Obj(st, "summary"), "written"));
            File.Delete(Home("sample_modsource.txt"));
            Deploy.Run(Library.Load(), Game);
            Assert.Equal(2, File.ReadAllLines(Home("sample_modsource.txt")).Length);
        }

        /// <summary>No plugin's list of mods holds a protected one: its plaintext goes to the official
        /// game only (a deploy target of a plugin, or any plugin reading host["mods"]).</summary>
        [Fact]
        public void Plugins_never_get_a_protected_mod()
        {
            AddSample();
            Cli("add", Protect("Prot", "deploy", "secret"));
            Cli("add", Plain("One", B + ".patch_0", "one"));
            Cli("deploy");
            Assert.Contains(A + ".patch_0", Deployed());
            var mods = PluginHost.Mods(Library.Load());
            Assert.Equal(new[] { "One" }, mods.Select(m => (string)m["name"]));
            Assert.DoesNotContain(mods.SelectMany(m => (List<object>)m["files"]).Cast<Dictionary<string, object>>(),
                                  f => ((string)f["src"]).StartsWith(Stage, StringComparison.OrdinalIgnoreCase));
            Cli("target", "sample");
            Assert.Equal("protected_target", Json.Str(Notes(Row("Prot")).First(), "key"));
        }
    }
}
