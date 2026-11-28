using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>Install plans (Recipes): the list read, a plan found by its name, where each step
    /// stands, a step done without a download, and the mod's log read after a game.</summary>
    public class RecipeTests : Fixture
    {
        const string LoaderGuid = "3f7a2c91-6d4e-4b58-9a0c-e15b82d4f6a7", HubGuid = "b0ae9d35-4a69-5918-b43d-51d4d10ff001",
                     HudGuid = "5b8e2c41-9d37-4f6a-b0e3-7c14a9d2f685";

        const string List = @"{""recipes"":[{
            ""id"":""super-holo-hud"", ""names"":[""Super Holo HUD"",""SHH"",""全息HUD""],
            ""title"":{""en"":""Super Holo HUD"",""zh-CN"":""Super Holo HUD""}, ""target"":""official"",
            ""steps"":[
              {""name"":{""en"":""Boot Mod Loader""},""guid"":""" + LoaderGuid + @""",""github"":""o/bml"",""loader"":true,
               ""mirror"":{""tag"":""v1"",""asset"":""bml.zip"",""sha256"":""" + Sha + @""",""size"":5,""url"":""https://dl.example/github/o/bml/v1/bml.zip""}},
              {""name"":{""en"":""MDL3 and HD2 Data Hub"",""zh-CN"":""MDL3 与 HD2 Data Hub""},""guid"":""" + HubGuid + @""",""github"":""z/mdl3"",""options"":[""MDL3"",""HD2 Data Hub""]},
              {""name"":{""en"":""Member licenses plugin""},""plugin"":""HD2QD.License.dll""},
              {""name"":{""en"":""Super Holo HUD""},""guid"":""" + HudGuid + @""",""options"":[""Super Holo HUD""],""main"":true,
               ""pages"":[{""label"":{""zh-CN"":""爱发电""},""url"":""https://afdian.example/a"",""langs"":[""zh-CN""]},{""label"":""Patreon"",""url"":""https://patreon.example/c""},{""label"":""x"",""url"":""http://plain.example/""}]},
              {""name"":""Settings"",""plugin"":""HD2QD.Settings.dll"",""optional"":true}],
            ""checks"":[
              {""log"":""hud.log"",""kind"":""start"",""find"":""hud started""},
              {""log"":""hud.log"",""kind"":""problem"",""find"":""assets: not installed"",""clear"":""assets: found"",""say"":{""en"":""no assets""}},
              {""log"":""hud.log"",""kind"":""problem"",""find"":""hub: absent"",""say"":{""en"":""no hub""}},
              {""log"":""..\\elsewhere.log"",""kind"":""problem"",""find"":""x""}]},
          {""id"":""broken"",""title"":""Broken"",""steps"":[{""name"":""both"",""plugin"":""HD2QD.X.dll"",""guid"":""" + HudGuid + @"""}]},
          {""id"":""mirror-without-github"",""title"":""M"",""steps"":[{""name"":""m"",""guid"":""" + HudGuid + @""",""mirror"":{""tag"":""v1"",""asset"":""a.zip"",""sha256"":""" + Sha + @"""}}]}
        ]}";
        const string Sha = "9245b12c031a8d3b7cdf780a637fb140983626524c180378b56d32d361bf2831";

        readonly HashSet<string> plugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Func<string, bool> keptAdded = Recipes.PluginAdded;

        public RecipeTests()
        {
            Recipes.TestText = List;
            Recipes.PluginAdded = f => plugins.Contains(f);
        }

        public override void Dispose()
        {
            Recipes.TestText = null;
            Recipes.PluginAdded = keptAdded;
            LuaMods.GameFolderOverride = null;
            base.Dispose();
        }

        static string Manifest(string name, string guid, params string[] options) =>
            "{\"Version\":1,\"Guid\":\"" + guid + "\",\"Name\":\"" + name + "\",\"Options\":[" +
            string.Join(",", options.Select(o => "{\"Name\":\"" + o + "\",\"Include\":[\"" + o + "\"]}")) + "]}";

        string AddMod(string name, string guid, params string[] options)
        {
            var files = new Dictionary<string, byte[]>();
            foreach (var o in options) files[o + "/" + A + ".patch_0"] = Archive((1, (ulong)(name.Length * 100 + o.Length)));
            Cli("add", Mod(name, files, Manifest(name, guid, options)));
            return Library.Load().Mods.Last().Id;
        }

        string AddLoader(string folder, string guid = LoaderGuid)
        {
            Cli("add", Mod(folder, new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- Boot Mod Loader\nlocal MARKER = \"boot-mod-loader-0.14.1\"\n")) },
                           Manifest(folder, guid)));
            return Library.Load().Mods.Last().Id;
        }

        [Fact]
        public void The_list_keeps_only_plans_of_a_known_shape()
        {
            var list = Recipes.Kept();
            Assert.Equal(new[] { "super-holo-hud" }, list.Select(r => r.Id));      // a step both plugin and mod, a mirror with no repository: left out
            var r = list[0];
            Assert.Equal(5, r.Steps.Count);
            Assert.True(r.Steps[0].Loader);
            Assert.Equal("https://dl.example/github/o/bml/v1/bml.zip", r.Steps[0].Mirror.Url);    // a file of the author's R2 bucket, by its address
            Assert.Equal(new[] { "MDL3", "HD2 Data Hub" }, r.Steps[1].Options);
            Assert.True(r.Steps[4].Optional);
            Assert.Equal(new[] { "https://afdian.example/a", "https://patreon.example/c" }, r.Steps[3].Pages.Select(p => p.Url));   // never plain http
            Assert.Equal(3, r.Checks.Count);                                         // a log outside the game's log folder: left out
            Assert.Equal("MDL3 与 HD2 Data Hub", Recipes.In(r.Steps[1].Name, "zh-CN"));
            Assert.Equal("MDL3 and HD2 Data Hub", Recipes.In(r.Steps[1].Name, "pt-BR"));
            Assert.Equal("Settings", Recipes.In(r.Steps[4].Name, "zh-CN"));
            Assert.Throws<FormatException>(() => Recipes.Parse("[1,2]"));
        }

        [Fact]
        public void A_plan_is_offered_once_its_mod_is_added_and_only_while_something_is_missing()
        {
            var r = Recipes.Kept()[0];
            Assert.Empty(Recipes.Unfinished(r, Library.Load()));                 // its mod not added: nothing offered

            var hud = AddMod("Super Holo HUD", HudGuid, "Super Holo HUD");
            var left = Recipes.Unfinished(r, Library.Load());
            Assert.Equal(new[] { "Boot Mod Loader", "MDL3 and HD2 Data Hub", "Member licenses plugin" }, left.Select(s => Recipes.In(s.Step.Name, "en")));   // never the optional one

            Cli("disable", hud);
            Assert.Empty(Recipes.Unfinished(r, Library.Load()));                 // its mod off: not offered
            Cli("enable", hud);

            AddLoader("Boot Mod Loader");
            AddMod("MDL3", HubGuid, "MDL3", "HD2 Data Hub");
            foreach (var s in r.Steps.Where(s => s.Guid != null && !s.Main)) Recipes.Run(r, s);
            plugins.Add("HD2QD.License.dll");
            Assert.Empty(Recipes.Unfinished(r, Library.Load()));                 // all there: nothing to offer
        }

        [Fact]
        public void A_plan_is_found_by_any_of_its_names_however_typed()
        {
            var list = Recipes.Kept();
            foreach (var typed in new[] { "super holo hud", "Super-Holo-HUD", "  SHH ", "superholohud", "全息 HUD", "super-holo-hud" })
                Assert.Equal("super-holo-hud", Recipes.Find(list, typed)?.Id);
            foreach (var typed in new[] { "16662", "super holo", "", "https://github.com/o/r" })
                Assert.Null(Recipes.Find(list, typed));
        }

        [Fact]
        public void Each_step_says_where_it_stands_and_a_step_is_done_without_a_download()
        {
            var r = Recipes.Kept()[0];
            var states = Recipes.States(r, Library.Load());
            Assert.Equal(new[] { "missing", "missing", "missing", "missing", "missing" }, states.Select(s => s.Is));

            // in the library, an option off and the mod off: "off", then Run turns them on, with no download
            var hub = AddMod("MDL3", HubGuid, "MDL3", "HD2 Data Hub");
            Cli("set", hub, "HD2 Data Hub", "off");
            Assert.Equal("off", Recipes.StateOf(r, r.Steps[1], Library.Load()).Is);
            Recipes.Run(r, r.Steps[1]);
            Assert.Equal("done", Recipes.StateOf(r, r.Steps[1], Library.Load()).Is);
            Assert.All(Manifest_Merged(hub), o => Assert.True(o.Enabled));

            var hud = AddMod("Super Holo HUD", HudGuid, "Super Holo HUD");
            Cli("disable", hud);
            Assert.Equal("off", Recipes.StateOf(r, r.Steps[3], Library.Load()).Is);
            Recipes.Run(r, r.Steps[3]);
            Assert.True(Library.Load().Mods.First(m => m.Id == hud).Enabled);
            Assert.Equal("done", Recipes.StateOf(r, r.Steps[3], Library.Load()).Is);

            // a loader: chosen, not switched on
            var other = AddLoader("Another loader", "11111111-2222-3333-4444-555555555555");
            var bml = AddLoader("Boot Mod Loader");
            Cli("loader", other);
            Assert.Equal("loader", Recipes.StateOf(r, r.Steps[0], Library.Load()).Is);
            Recipes.Run(r, r.Steps[0]);
            Assert.Equal(bml, Loaders.Chosen(Library.Load()).Id);
            Assert.Equal("done", Recipes.StateOf(r, r.Steps[0], Library.Load()).Is);

            // a plugin: done once added
            Assert.Equal("missing", Recipes.StateOf(r, r.Steps[2], Library.Load()).Is);
            plugins.Add("HD2QD.License.dll");
            Assert.Equal("done", Recipes.StateOf(r, r.Steps[2], Library.Load()).Is);
        }

        static List<OptionChoice> Manifest_Merged(string id)
        {
            var m = Library.Load().Mods.First(x => x.Id == id);
            return Hd2Mod.Manifest.Merged(Hd2Mod.Manifest.Of(m), m.Options);
        }

        [Fact]
        public void A_mod_got_by_hand_is_refused_with_where_to_get_it()
        {
            var r = Recipes.Kept()[0];
            var e = Assert.Throws<ModError>(() => Recipes.Run(r, r.Steps[3]));
            Assert.Equal("recipe_manual", e.Kind);
            var said = Cli("install", "--check", "shh");
            Assert.Contains("[ ] Super Holo HUD: get it by hand: https://patreon.example/c", said);     // the 爱发电 page is for Chinese only
            Assert.DoesNotContain("afdian", said);
            Assert.Contains("[ ] Settings (optional)", said);
            Assert.Equal("recipe_none", Refused("install", "no such mod"));
        }

        [Fact]
        public void The_log_says_how_the_last_game_went()
        {
            var r = Recipes.Kept()[0];
            var logs = Path.Combine(Root, "game logs");
            Directory.CreateDirectory(logs);
            LuaMods.GameFolderOverride = logs;
            Assert.Null(Recipes.Read(r));                                  // never ran here

            var log = Path.Combine(logs, "hud.log");
            File.WriteAllText(log, "10:00 hud started\n10:01 hub: absent\n", new UTF8Encoding(false));
            var o = Recipes.Read(r);
            Assert.True(o.Started);
            Assert.Equal(new[] { "no hub" }, o.Problems.Select(p => p.Say["en"]));

            // a later game: the earlier one's problem is not this one's; one cleared later in the game is none
            File.AppendAllText(log, "11:00 hud started\n11:00 assets: not installed\n11:00 assets: found\n");
            o = Recipes.Read(r);
            Assert.True(o.Started);
            Assert.Empty(o.Problems);
            File.AppendAllText(log, "12:00 hud started\n12:00 assets: not installed\n");
            Assert.Equal(new[] { "no assets" }, Recipes.Read(r).Problems.Select(p => p.Say["en"]));

            // a log with no start line: the mod never began a game
            File.WriteAllText(log, "something else\n");
            Assert.False(Recipes.Read(r).Started);
        }
    }
}
