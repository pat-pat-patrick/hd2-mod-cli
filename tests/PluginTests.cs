using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>Plugins as the manager handles any of them, on HD2QD.Sample.dll (tests\SamplePlugin):
    /// added to the settings folder, replaced, removed, refused, and what it adds (a word on mod
    /// rows, commands, a start). The manager knows no plugin by name.</summary>
    public class Plugins : Fixture
    {
        static string Sample => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HD2QD.Sample.dll");

        public Plugins() => PluginFiles.Folder = null;      // the settings folder's plugins\, as the program has it

        void AddSample() => Assert.Contains("added HD2QD.Sample.dll 1.0.0 to", Cli("plugin-add", Sample));

        [Fact]
        public void A_plugin_is_added_to_the_settings_folder_replaced_and_removed()
        {
            Assert.Equal(Path.Combine(Store.Home, "plugins"), PluginFiles.Dir);
            Assert.Empty(PluginFiles.All());
            Assert.Contains("no plugin has been added", Cli("plugins"));

            AddSample();
            Assert.True(File.Exists(Path.Combine(Store.Home, "plugins", "HD2QD.Sample.dll")));
            var p = PluginFiles.All().Single();
            Assert.NotNull(p.Type);
            Assert.Equal("Sample plugin", PluginFiles.Title(p.Type, false));
            Assert.Equal("window", PluginFiles.Kind(p.Type));
            Assert.Contains("HD2QD.Sample.dll 1.0.0  Sample plugin", Cli("plugins"));
            Assert.Equal(("HD2QD.Sample.dll", "1.0.0", "1.0.0"), PluginFiles.Install(Sample));

            Assert.Contains("removed HD2QD.Sample.dll", Cli("plugin-remove", "Sample"));
            Assert.Empty(PluginFiles.All());
            Assert.Equal("plugin_missing", Assert.Throws<ModError>(() => PluginFiles.Remove("Sample")).Kind);
        }

        /// <summary>What is not a plugin is refused before anything of it runs or is copied.</summary>
        [Fact]
        public void A_file_that_is_not_a_plugin_is_refused()
        {
            var renamed = Path.Combine(Root, "Sample.dll");                       // a plugin, but not named HD2QD.*.dll
            File.Copy(Sample, renamed);
            Assert.Equal("name", PluginFiles.Inspect(renamed).Why);
            var other = Path.Combine(Root, "HD2QD.Other.dll");                    // an assembly with no class Plugin
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xunit.assert.dll"), other);
            Assert.Equal("broken", PluginFiles.Inspect(other).Why);
            var text = Path.Combine(Root, "HD2QD.Text.dll");
            File.WriteAllText(text, "not an assembly");
            Assert.Equal("broken", PluginFiles.Inspect(text).Why);
            Assert.Equal("plugin_broken", Assert.Throws<ModError>(() => PluginFiles.Install(other)).Kind);
            CliCode(1, "plugin-add", renamed);
            Assert.False(Directory.Exists(PluginFiles.Dir) && Directory.GetFiles(PluginFiles.Dir).Length > 0);
        }

        /// <summary>A plugin's word on a mod's row reaches the state; one that fails says
        /// nothing, and the row is still there.</summary>
        [Fact]
        public void A_plugins_word_on_a_mod_reaches_the_state()
        {
            var noted = Mod("Noted", Files(("sample_note.txt", "x"), (A + ".patch_0", "a")));
            var failing = Mod("Failing", Files(("sample_throw.txt", "x"), (B + ".patch_0", "b")));
            Cli("add", noted, "--name", "Noted");
            Cli("add", failing, "--name", "Failing");
            Dictionary<string, object> Row(string name) =>
                Json.List(StateDoc.Build(Library.Load(), Game, false), "mods").Cast<Dictionary<string, object>>().Single(m => Json.Str(m, "name") == name);
            Assert.Empty(Json.List(Row("Noted"), "notes"));                       // not added yet
            AddSample();
            var note = Json.List(Row("Noted"), "notes").Cast<Dictionary<string, object>>().Single();
            Assert.Equal("Sampled", Json.Str(note, "text"));
            Assert.Equal("已采样", Json.Str(note, "text_zh"));
            Assert.Equal("HD2QD.Sample.dll", Json.Str(note, "plugin"));
            Assert.Empty(Json.List(Row("Failing"), "notes"));
        }

        /// <summary>A mod that ships a file an official plugin reads (hd2_license.json) says it
        /// needs that plugin while it is not added.</summary>
        [Fact]
        public void A_mod_says_which_official_plugin_it_needs()
        {
            var licensed = Mod("Licensed", Files(("hd2_license.json", "{}"), (A + ".patch_0", "a")));
            Cli("add", licensed, "--name", "Licensed");
            var note = PluginFiles.Notes(licensed, PluginHost.Make(false)).Single();
            Assert.True(Json.Bool(note, "needs"));
            Assert.Equal("HD2QD.License.dll", Json.Str(note, "plugin"));
            Assert.Equal("需要“会员授权”插件", Json.Str(note, "text_zh"));
            var needed = PluginFiles.Needed(Library.Load()).Single();
            Assert.Equal("HD2QD.License.dll", needed.Plugin);
            Assert.Equal(new[] { "Licensed" }, needed.Mods);
        }

        /// <summary>A plugin's commands run from the manager's command line; a name the manager has
        /// stays the manager's; what the plugin throws is a refusal.</summary>
        [Fact]
        public void A_plugins_commands_run_from_the_command_line()
        {
            CliCode(2, "sample-echo", "a");                                       // not added: no such command
            AddSample();
            Assert.Contains("echo a b in " + Store.Home, Cli("sample-echo", "a", "b"));
            Assert.Contains("sample-echo", Cli("--help"));
            Assert.DoesNotContain("a name the manager has", Cli("--help"));
            CliCode(1, "sample-echo", "--fail");
            Cli("list");                                                          // still the manager's
        }

        [Fact]
        public void A_plugins_start_runs_with_the_host()
        {
            Assert.False(PluginFiles.Start(PluginHost.Make(false)));               // none added
            AddSample();
            Assert.True(PluginFiles.Start(PluginHost.Make(false)));
            Assert.True(File.Exists(Path.Combine(Store.Home, "sample_started.txt")));
        }

        /// <summary>What every call gets: the settings folder, AppData, the language, the game's
        /// data folder, and the enabled mods and the whole library on demand.</summary>
        [Fact]
        public void The_host_carries_the_folders_and_the_library()
        {
            var a = Mod("Alpha", Files((A + ".patch_0", "a")));
            Cli("add", a, "--name", "Alpha");
            var h = PluginHost.Make(true);
            Assert.Equal(Store.Home, h["home"]);
            Assert.Equal(true, h["zh"]);
            Assert.Equal(Data, h["data"]);
            var mods = ((Func<List<Dictionary<string, object>>>)h["mods"])();
            Assert.Equal("Alpha", Json.Str(mods.Single(), "name"));
            var lib = ((Func<List<Dictionary<string, object>>>)h["library"])();
            Assert.Equal(a, Json.Str(lib.Single(), "path"));
            Assert.True(Json.Bool(lib.Single(), "enabled"));
        }
    }
}
