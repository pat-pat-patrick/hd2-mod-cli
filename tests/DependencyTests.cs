using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>A mod's own "Requires" (the user, 2026-11-28: dependencies as a package manager has
    /// them): read from its manifest, its dependencies' needs first, versions, the author's list
    /// lending words and mirror files, and what is offered once the mod is in the library.</summary>
    public class DependencyTests : Fixture
    {
        const string Hud = "5b8e2c41-9d37-4f6a-b0e3-7c14a9d2f685", Assets = "4a018c18-b228-450f-b462-ef198bdc1788",
                     Hub = "b0ae9d35-4a69-5918-b43d-51d4d10ff001", Deep = "11111111-2222-3333-4444-555555555555";
        const string Sha = "9245b12c031a8d3b7cdf780a637fb140983626524c180378b56d32d361bf2831";

        // the author's list: words and a mirror file for Assets, log checks for the HUD
        const string List = @"{""recipes"":[{""id"":""super-holo-hud"",""title"":""Super Holo HUD"",""target"":""official"",
            ""steps"":[
              {""name"":{""en"":""Super Holo HUD Assets"",""zh-CN"":""资源包""},""guid"":""" + Assets + @""",""github"":""o/assets"",
               ""mirror"":{""tag"":""v2.2.0"",""asset"":""a.zip"",""sha256"":""" + Sha + @""",""size"":5,""url"":""https://dl.example/a.zip""}},
              {""name"":""Super Holo HUD"",""guid"":""" + Hud + @""",""main"":true}],
            ""checks"":[{""log"":""hud.log"",""kind"":""start"",""find"":""started""}]}]}";

        readonly HashSet<string> plugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Func<string, bool> keptAdded = Recipes.PluginAdded;

        public DependencyTests()
        {
            Recipes.TestText = List;
            Recipes.PluginAdded = f => plugins.Contains(f);
        }

        public override void Dispose()
        {
            Recipes.TestText = null;
            Recipes.PluginAdded = keptAdded;
            base.Dispose();
        }

        string Add(string folder, string guid, string requires = null, params string[] options)
        {
            var files = new Dictionary<string, byte[]>();
            foreach (var o in options.DefaultIfEmpty("Main")) files[o + "/" + A + ".patch_0"] = Archive((1, (ulong)(folder.Length * 100 + o.Length)));
            var opts = string.Join(",", options.DefaultIfEmpty("Main").Select(o => "{\"Name\":\"" + o + "\",\"Include\":[\"" + o + "\"]}"));
            Cli("add", Mod(folder, files, "{\"Version\":1,\"Guid\":\"" + guid + "\",\"Name\":\"" + folder + "\",\"Options\":[" + opts + "]" +
                                         (requires != null ? ",\"Requires\":" + requires : "") + "}"));
            return Library.Load().Mods.Last().Id;
        }

        Mod ModCalled(string id) => Library.Load().Mods.First(m => m.Id == id);

        [Fact]
        public void Versions_compare_by_their_numbers_with_a_pre_release_before_its_release()
        {
            Assert.True(Recipes.CompareVersion("2.1.2", "2.2.0") < 0);
            Assert.True(Recipes.CompareVersion("v2.10.0", "2.9.9") > 0);
            Assert.Equal(0, Recipes.CompareVersion("v2.2", "2.2.0"));
            Assert.True(Recipes.CompareVersion("3.0.0-dev.57", "3.0.0") < 0);
            Assert.True(Recipes.CompareVersion("3.0.0-dev.57", "3.0.0-dev.19") > 0);
            Assert.True(Recipes.CompareVersion("1.259.4", "1.259.10") < 0);
        }

        [Fact]
        public void The_manifest_says_what_it_requires_in_any_case_and_odd_entries_are_left_out()
        {
            var hud = Add("Super Holo HUD", Hud, @"[
                {""name"":""Super Holo HUD Assets"",""guid"":""" + Assets + @""",""github"":""o/assets"",""version"":""2.2.0"",""options"":[""Super Holo HUD Assets""]},
                {""Name"":""MDL3"",""Guid"":""" + Hub + @""",""GitHub"":""z/mdl3"",""Options"":[""MDL3"",""HD2 Data Hub""]},
                {""Plugin"":""HD2QD.License.dll""},
                {""Plugin"":""..\\evil.dll""},
                {""Name"":""both"",""Guid"":""" + Deep + @""",""Plugin"":""HD2QD.X.dll""},
                {""Name"":""bad repo"",""Guid"":""" + Deep + @""",""GitHub"":""https://evil.example/x""}]");
            var man = Manifest.Read(ModCalled(hud).Path);
            Assert.Equal(6, man.Requires.Count);
            Assert.Equal("2.2.0", man.Requires[0].Version);
            Assert.Equal(new[] { "MDL3", "HD2 Data Hub" }, man.Requires[1].Options);

            var r = Recipes.PlanFor(Library.Load(), ModCalled(hud));
            Assert.Equal("mod:" + Hud, r.Id);
            Assert.Equal(new[] { Assets, Hub, null, Hud }, r.Steps.Select(s => s.Guid));             // the odd ones left out, the mod itself last
            Assert.Equal("HD2QD.License.dll", r.Steps[2].Plugin);
            Assert.True(r.Steps[3].Main);
            // the author's list lends its words, its mirror file (same repository) and its log checks
            Assert.Equal("资源包", Recipes.In(r.Steps[0].Name, "zh-CN"));
            Assert.Equal("https://dl.example/a.zip", r.Steps[0].Mirror.Url);
            Assert.Null(r.Steps[1].Mirror);
            Assert.Equal("official", r.Target);
            Assert.Single(r.Checks);
        }

        [Fact]
        public void A_dependency_already_added_brings_its_own_needs_first_and_a_cycle_ends()
        {
            // the HUD needs Assets; Assets (added) needs Deep, which needs the HUD back
            var hud = Add("Super Holo HUD", Hud, @"[{""name"":""Assets"",""guid"":""" + Assets + @""",""github"":""o/assets""}]");
            Add("Assets v2.2.0", Assets, @"[{""name"":""Deep"",""guid"":""" + Deep + @""",""github"":""o/deep"",""version"":""1.0""}]");
            Add("Deep v0.9", Deep, @"[{""name"":""HUD"",""guid"":""" + Hud + @""",""github"":""o/hud""}]");
            var r = Recipes.PlanFor(Library.Load(), ModCalled(hud));
            Assert.Equal(new[] { Deep, Assets, Hud }, r.Steps.Select(s => s.Guid));
            var states = Recipes.States(r, Library.Load());
            Assert.Equal("old", states[0].Is);                       // 0.9 in the library, 1.0 needed
            Assert.Equal("0.9", states[0].Have);
            Assert.Equal("done", states[1].Is);
            Assert.Equal(new[] { Deep }, Recipes.Unfinished(r, Library.Load()).Select(s => s.Step.Guid));
            Assert.True(Recipes.CanDo(states[0]));
        }

        [Fact]
        public void A_dependency_named_twice_asks_the_newer_version_and_every_option()
        {
            var hud = Add("Super Holo HUD", Hud, @"[
                {""name"":""Hub"",""guid"":""" + Hub + @""",""github"":""z/mdl3"",""version"":""3.0.0-dev.19"",""options"":[""MDL3""]},
                {""name"":""Assets"",""guid"":""" + Assets + @""",""github"":""o/assets""}]");
            Add("Assets v2.2.0", Assets, @"[{""name"":""Hub"",""guid"":""" + Hub + @""",""github"":""z/mdl3"",""version"":""3.0.0-dev.43"",""options"":[""HD2 Data Hub""]}]");
            var r = Recipes.PlanFor(Library.Load(), ModCalled(hud));
            var hub = r.Steps.Single(s => s.Guid == Hub);
            Assert.Equal("3.0.0-dev.43", hub.Version);
            Assert.Equal(new[] { "HD2 Data Hub", "MDL3" }, hub.Options.OrderBy(x => x));
        }

        [Fact]
        public void A_mod_s_list_is_offered_while_something_is_missing_and_not_after()
        {
            var hud = Add("Super Holo HUD", Hud, @"[{""name"":""Assets"",""guid"":""" + Assets + @""",""github"":""o/assets"",""version"":""2.2.0"",""options"":[""Super Holo HUD Assets""]},
                                                    {""plugin"":""HD2QD.License.dll""}]");
            var plans = Recipes.Plans(Library.Load());
            Assert.Equal(new[] { "mod:" + Hud }, plans.Select(p => p.Id));
            Assert.Equal(2, Recipes.Unfinished(plans[0], Library.Load()).Count);

            var assets = Add("Assets v2.1.2", Assets, null, "Super Holo HUD Assets");
            Assert.Equal("old", Recipes.Unfinished(Recipes.Plans(Library.Load())[0], Library.Load())[0].Is);
            // the newer release in its place (as a download would bring it): done
            Cli("replace", assets, Mod("Assets v2.2.0", new Dictionary<string, byte[]> { ["Super Holo HUD Assets/" + A + ".patch_0"] = Archive((1, 777)) },
                                       "{\"Version\":1,\"Guid\":\"" + Assets + "\",\"Name\":\"Assets\",\"Options\":[{\"Name\":\"Super Holo HUD Assets\",\"Include\":[\"Super Holo HUD Assets\"]}]}"));
            plugins.Add("HD2QD.License.dll");
            Assert.Empty(Recipes.Unfinished(Recipes.Plans(Library.Load())[0], Library.Load()));

            Cli("disable", hud);
            Assert.Empty(Recipes.Plans(Library.Load()));                // a mod that is off offers nothing
        }

        [Fact]
        public void What_needs_a_mod_is_said_before_it_goes_and_what_was_pulled_in_for_it_is_offered_after()
        {
            var hud = Add("Super Holo HUD", Hud, @"[{""name"":""Assets"",""guid"":""" + Assets + @""",""github"":""o/assets""}]");
            var assets = Add("Assets v2.2.0", Assets);
            var other = Add("Other", Deep);
            Assert.Equal(new[] { "Super Holo HUD" }, Recipes.NeededBy(Library.Load(), ModCalled(assets)).Select(m => m.Name));
            Assert.Empty(Recipes.NeededBy(Library.Load(), ModCalled(other)));
            Assert.Contains("note: Assets v2.2.0 is needed by Super Holo HUD", Cli("disable", assets));
            Cli("enable", assets);
            Assert.DoesNotContain("needed by", Cli("disable", other));

            // marked as installed for the HUD (as the install list marks it): kept, and offered once the HUD is gone
            var lib = Library.Load();
            lib.Mods.First(m => m.Id == assets).InstalledFor.Add(Hud);
            lib.Save();
            Assert.Equal(new[] { Hud }, ModCalled(assets).InstalledFor);                   // kept in library.json
            Assert.Empty(Recipes.Orphans(Library.Load()));                                  // the HUD still needs it
            Cli("disable", hud);
            Assert.Empty(Recipes.Orphans(Library.Load()));                                  // off is not gone
            var said = Cli("remove", hud);
            Assert.Contains("no longer needed by any mod", said);
            Assert.Equal(new[] { "Assets v2.2.0" }, Recipes.Orphans(Library.Load()).Select(m => m.Name));
            Assert.Empty(Recipes.Orphans(Library.Load()).Where(m => m.Id == other));       // one the player added is never offered
        }

        [Fact]
        public void An_install_list_never_offers_a_loader_for_removal()
        {
            var hud = Add("Super Holo HUD", Hud, @"[{""name"":""BML"",""guid"":""" + Assets + @""",""github"":""o/bml"",""loader"":true}]");
            Cli("add", Mod("BML", new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- Boot Mod Loader\nlocal MARKER = \"boot-mod-loader-0.14.1\"\n")) },
                           "{\"Version\":1,\"Guid\":\"" + Assets + "\",\"Name\":\"BML\",\"Options\":[]}"));
            var lib = Library.Load();
            lib.Mods.Last().InstalledFor.Add(Hud);
            lib.Save();
            Cli("remove", hud);
            Assert.Empty(Recipes.Orphans(Library.Load()));       // older Lua mods use a loader without saying so
        }

        [Fact]
        public void After_an_update_what_the_new_version_needs_is_done_with_it()
        {
            // the new version asks for an option of its dependency that is off: turned on with it
            var hub = Add("MDL3", Hub, null, "MDL3", "HD2 Data Hub");
            Cli("set", hub, "HD2 Data Hub", "off");
            var hud = Add("Super Holo HUD", Hud, @"[{""name"":""MDL3"",""guid"":""" + Hub + @""",""github"":""z/mdl3"",""options"":[""MDL3"",""HD2 Data Hub""]}]");
            var said = Recipes.RunAll(() => Recipes.PlanFor(Library.Load(), ModCalled(hud)));
            Assert.Single(said);
            Assert.Empty(Recipes.Unfinished(Recipes.PlanFor(Library.Load(), ModCalled(hud)), Library.Load()));
        }

        [Fact]
        public void Install_on_the_command_line_takes_a_library_mod_s_own_list()
        {
            Add("My Mod", Deep, @"[{""plugin"":""HD2QD.License.dll""}]");
            var said = Cli("install", "--check", "My Mod");
            Assert.Contains("[ ] Member licenses plugin", said.Replace("HD2QD.License.dll", "Member licenses plugin"));
            Assert.Contains("[x] My Mod", said);
        }

        static Dictionary<string, object> Row(string mod, string name, string url = "", bool external = false, string game = "6119", string notes = "") =>
            new Dictionary<string, object> { ["modId"] = mod, ["modName"] = name, ["url"] = url, ["externalRequirement"] = external, ["gameId"] = game, ["notes"] = notes };

        [Fact]
        public void A_Nexus_page_s_requirements_keep_other_mods_and_known_sites_not_managers_jokes_or_tools()
        {
            var needs = Recipes.Needs(new[]
            {
                Row("4664", "HD2 Arsenal - Mod Manager - Companion Desktop App"),
                Row("109", "HD2ModManager", notes: "v1.3.0.1"),
                Row("15925", "Improved Reticle Suite -( IRS )-", notes: "Load before Better Aim Reticles"),
                Row("15925", "Improved Reticle Suite again"),
                Row("2388", "itself"),
                Row("5", "another game's mod", game: "1704"),
                Row("0", "Reading Comprehension", "https://www.student.unsw.edu.au/reading-understanding", true, "0"),
                Row("0", "ReShade", "https://reshade.me/", true, "0"),
                Row("0", "Ability to Read.", "https://www.nexusmods.com/mysummercar/mods/6122", true, "0"),
                Row("0", "Stratagem Icons Manager", "https://www.nexusmods.com/helldivers2/mods/2336", true, "0"),
                Row("0", "hd2-repatcher", "https://github.com/RaidingForPants/hd2-repatcher/releases", true, "0"),
                Row("0", "Swimsuit", "https://gamebanana.com/mods/669221", true, "0"),
            }, 2388);
            Assert.Equal(new[] { "Improved Reticle Suite -( IRS )-", "Stratagem Icons Manager", "hd2-repatcher", "Swimsuit" }, needs.Select(n => n.Name));
            Assert.Equal(new long[] { 15925, 2336, 0, 0 }, needs.Select(n => n.Mod));
            Assert.Equal("Load before Better Aim Reticles", needs[0].Notes);
            Assert.Equal("https://gamebanana.com/mods/669221", needs[3].Url);
        }

        void KeepNeeds(long mod, params (long Mod, string Name, string Url)[] needs) =>
            Store.Save(Recipes.NeedsFile, new Dictionary<string, object>
            {
                [mod.ToString()] = new Dictionary<string, object>
                {
                    ["read"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["needs"] = needs.Select(n => (object)new Dictionary<string, object> { ["mod"] = n.Mod, ["name"] = n.Name, ["url"] = n.Url }).ToList(),
                },
            });

        void FromNexus(string id, long mod)
        {
            var lib = Library.Load();
            lib.Mods.First(m => m.Id == id).Nexus = new NexusRef { Game = "helldivers2", Mod = mod };
            lib.Save();
        }

        [Fact]
        public void A_mod_that_names_nothing_shows_its_Nexus_page_s_requirements_as_hints()
        {
            var bar = Add("Better Aim Reticles", Deep);
            FromNexus(bar, 2388);
            // never read: no list
            var lib = Library.Load();
            Assert.Null(Recipes.PlanFor(lib, ModCalled(bar), new List<Recipes.Recipe>()));
            KeepNeeds(2388, (15925, "Improved Reticle Suite -( IRS )-", null), (2336, "Stratagem Icons Manager", null), (0, "hd2-repatcher", "https://github.com/RaidingForPants/hd2-repatcher/releases"));
            // one of them added from Nexus Mods, one from elsewhere under the page's own name
            var irs = Add("IRS", "22222222-2222-3333-4444-555555555555");
            FromNexus(irs, 15925);
            Add("Stratagem Icons Manager", "33333333-2222-3333-4444-555555555555");
            lib = Library.Load();
            var plan = Recipes.PlanFor(lib, ModCalled(bar), new List<Recipes.Recipe>());
            Assert.Equal("nexus:2388", plan.Id);
            Assert.All(plan.Steps.Where(s => !s.Main), s => Assert.True(s.Hint));
            var states = Recipes.States(plan, lib);
            Assert.Equal(new[] { "done", "done", "missing", "done" }, states.Select(s => s.Is));
            // never downloaded, holds nothing back, still said once the mod is added
            Assert.DoesNotContain(states, Recipes.CanDo);
            Assert.All(states, s => Assert.True(Recipes.Spare(s)));
            Assert.Equal(new[] { "hd2-repatcher" }, Recipes.Unfinished(plan, lib).Select(s => Recipes.In(s.Step.Name, "en")));
            Assert.Empty(Recipes.NeededBy(lib, ModCalled(irs), true, new List<Recipes.Recipe>()));
            Assert.Equal("recipe_manual", Assert.Throws<ModError>(() => Recipes.Run(plan, plan.Steps[2])).Kind);
            // the mod's own Requires come first: the page is not asked then
            var own = Add("Own", "44444444-2222-3333-4444-555555555555", @"[{""Name"":""IRS"",""Guid"":""22222222-2222-3333-4444-555555555555"",""Nexus"":15925}]");
            FromNexus(own, 2388);
            Assert.False(Recipes.PlanFor(Library.Load(), ModCalled(own), new List<Recipes.Recipe>()).Steps.Any(s => s.Hint));
        }

        [Fact]
        public void A_Nexus_Mods_dependency_is_the_player_s_to_download_without_premium_and_known_by_its_number()
        {
            var kept = Recipes.NexusDirect;
            Recipes.NexusDirect = () => false;
            try
            {
                var hud = Add("Needs IRS", Deep, @"[{""Name"":""IRS"",""Nexus"":""15925"",""Page"":""https://example.com/irs"",""File"":""IRS""}]");
                var plan = Recipes.PlanFor(Library.Load(), ModCalled(hud), new List<Recipes.Recipe>());
                var step = plan.Steps[0];
                Assert.Equal(15925, step.Nexus);
                Assert.Equal("https://example.com/irs", step.Pages.Single().Url);
                Assert.Equal("https://www.nexusmods.com/helldivers2/mods/15925?tab=files", Recipes.SitesOf(step).Single().Page);
                var st = Recipes.StateOf(plan, step, Library.Load());
                Assert.Equal("missing", st.Is);
                Assert.False(Recipes.CanDo(st));
                Assert.Single(Recipes.Unfinished(plan, Library.Load()));
                Assert.Empty(Recipes.RunAll(() => Recipes.PlanFor(Library.Load(), ModCalled(hud), new List<Recipes.Recipe>())));
                Assert.Contains("nexusmods.com/helldivers2/mods/15925", Cli("install", "Needs IRS", "--check"));
                // the page's button (nxm://) brought it: known by its number, then only turned on
                var irs = Add("Improved Reticle Suite", "22222222-2222-3333-4444-555555555555");
                FromNexus(irs, 15925);
                Cli("disable", irs);
                st = Recipes.StateOf(plan, step, Library.Load());
                Assert.Equal("off", st.Is);
                Assert.True(Recipes.CanDo(st));
                Recipes.RunAll(() => Recipes.PlanFor(Library.Load(), ModCalled(hud), new List<Recipes.Recipe>()));
                Assert.True(Recipes.StateOf(plan, step, Library.Load()).Done);
                Assert.Equal(new[] { "Needs IRS" }, Recipes.NeededBy(Library.Load(), ModCalled(irs), true, new List<Recipes.Recipe>()).Select(m => m.Name));
                // with premium it would come from Nexus Mods itself
                Recipes.NexusDirect = () => true;
                Assert.Equal(new[] { "Nexus Mods" }, Recipes.AutoSites(step));
            }
            finally { Recipes.NexusDirect = kept; }
        }

        [Fact]
        public void The_author_s_list_names_mods_on_other_sites_but_GitHub_only_with_a_Guid()
        {
            var list = Recipes.Parse(@"{""recipes"":[
                {""id"":""a"",""title"":""A"",""steps"":[{""name"":""X"",""nexus"":15925,""file"":""IRS""},{""name"":""Y"",""gamebanana"":""669221""},{""name"":""A"",""guid"":""" + Deep + @""",""main"":true}]},
                {""id"":""b"",""title"":""B"",""steps"":[{""name"":""X"",""github"":""o/r""},{""name"":""B"",""guid"":""" + Deep + @""",""main"":true}]},
                {""id"":""c"",""title"":""C"",""steps"":[{""name"":""X"",""modworkshop"":""x1""},{""name"":""C"",""guid"":""" + Deep + @""",""main"":true}]}]}");
            Assert.Equal(new[] { "a" }, list.Select(r => r.Id));
            Assert.Equal(15925, list[0].Steps[0].Nexus);
            Assert.Equal("IRS", list[0].Steps[0].File);
            Assert.Equal("669221", list[0].Steps[1].Banana);
        }
    }
}
