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

        public Plugins()
        {
            PluginFiles.Folder = null;      // the settings folder's plugins\, as the program has it
            PluginFiles.OfficialJson = () => OfficialList;
            PluginFiles.OfficialLocaleJson = lang => lang == "zh-CN" ? ZhList : null;
            PluginFiles.Downloads = "http://127.0.0.1:1";
        }

        /// <summary>An official list naming the sample (released or not) and one a mod asks for.</summary>
        static string OfficialList = List(null);

        static string List(string sampleVersions) => @"{""format"": 1, ""plugins"": [
            {""file"": ""HD2QD.Sample.dll"", ""title"": ""Sample plugin"", ""summary"": ""A sample."", ""author"": ""someone"",
             ""repository"": ""someone/sample"", ""asset"": ""HD2QD.Sample.dll"", ""api"": 2, ""versions"": [" + (sampleVersions ?? "") + @"]},
            {""file"": ""HD2QD.License.dll"", ""title"": ""Member licenses"", ""repository"": ""someone/license"",
             ""api"": 2, ""wanted_by_file"": ""hd2_license.json"", ""versions"": []},
            {""file"": ""HD2QD.Later.dll"", ""title"": ""For a later manager"", ""repository"": ""someone/later"", ""api"": 3, ""versions"": []}]}";

        /// <summary>The Chinese translations of that list (locales/zh-CN.json): a title only for the sample.</summary>
        const string ZhList = @"{""HD2QD.Sample.dll"": {""title"": ""示例插件""}, ""HD2QD.License.dll"": {""title"": ""会员授权"", ""summary"": ""登录。""}}";

        /// <summary>Answers on a port of its own, one connection each: the bytes for a path, else 404.</summary>
        static string ServeMany(int answers, Dictionary<string, byte[]> files)
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < answers; i++)
                    using (var c = l.AcceptTcpClient())
                    using (var s = c.GetStream())
                    {
                        var head = new System.Text.StringBuilder();
                        var b = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n") && s.Read(b, 0, 1) == 1) head.Append((char)b[0]);
                        var found = files.TryGetValue(head.ToString().Split(' ')[1], out var body);
                        body = body ?? new byte[0];
                        var h = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 " + (found ? "200 OK" : "404 Not Found") + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                        s.Write(h, 0, h.Length);
                        s.Write(body, 0, body.Length);
                    }
                l.Stop();
            });
            return "http://127.0.0.1:" + ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        }

        static string Sha(string path)
        {
            using (var h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>One answer on a port of its own: these bytes, whatever is asked; the path asked.</summary>
        static (string Base, System.Threading.Tasks.Task<string> Asked) Serve(byte[] body)
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            var url = "http://127.0.0.1:" + ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            var asked = System.Threading.Tasks.Task.Run(() =>
            {
                var head = new System.Text.StringBuilder();
                using (var c = l.AcceptTcpClient())
                using (var s = c.GetStream())
                {
                    var b = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n") && s.Read(b, 0, 1) == 1) head.Append((char)b[0]);
                    var h = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    s.Write(h, 0, h.Length);
                    s.Write(body, 0, body.Length);
                    s.Flush();
                }
                l.Stop();
                return head.ToString().Split(' ')[1];
            });
            return (url, asked);
        }

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
            Assert.Equal("Sample plugin", PluginFiles.Title(p.Type, "en"));
            Assert.Equal("示例插件", PluginFiles.Title(p.Type, "zh-CN"));
            Assert.Equal("Sample plugin", PluginFiles.Title(p.Type, "pt-BR"));    // a language it does not have: English
            Assert.Equal("window", PluginFiles.Kind(p.Type));
            Assert.Contains("HD2QD.Sample.dll 1.0.0  Sample plugin", Cli("plugins"));
            Assert.Equal(("HD2QD.Sample.dll", "1.0.0", "1.0.0"), PluginFiles.Install(Sample));

            Assert.Contains("removed HD2QD.Sample.dll", Cli("plugin-remove", "Sample"));
            Assert.Empty(PluginFiles.All());
            Assert.Equal("plugin_missing", Assert.Throws<ModError>(() => PluginFiles.Remove("Sample")).Kind);
        }

        /// <summary>Removing Resource names deletes the lists it downloaded, which the manager reads
        /// itself (the user, 2026-11-04: the names still showed). Must live: they go with it. Must
        /// die: another plugin's removal leaves them.</summary>
        [Fact]
        public void Removing_resource_names_deletes_what_it_downloaded()
        {
            AddSample();
            File.WriteAllBytes(Path.Combine(PluginFiles.Dir, "HD2QD.ResourceNames.dll"), new byte[] { 1, 2, 3 });
            var lists = Path.Combine(Store.Home, "resource_names");
            Directory.CreateDirectory(lists);
            File.WriteAllText(ResourceNames.Downloaded, "101 Helmet One\n");
            File.WriteAllText(GameArchives.Downloaded, "{}");
            Cli("plugin-remove", "Sample");
            Assert.True(File.Exists(ResourceNames.Downloaded));
            Assert.Contains("and what it downloaded: " + lists, Cli("plugin-remove", "ResourceNames"));
            Assert.False(Directory.Exists(lists));
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
            Assert.Equal("HD2QD.Sample.dll", Json.Str(note, "plugin"));
            PluginHost.Lang = () => "zh-CN";                                     // the window in Chinese: the plugin answers in it
            Assert.Equal("已采样", Json.Str(Json.List(Row("Noted"), "notes").Cast<Dictionary<string, object>>().Single(), "text"));
            Assert.Empty(Json.List(Row("Failing"), "notes"));
        }

        /// <summary>A mod that ships a file an official plugin reads (hd2_license.json) says it
        /// needs that plugin while it is not added; the Plugins page lists it first.</summary>
        [Fact]
        public void A_mod_says_which_official_plugin_it_needs()
        {
            var licensed = Mod("Licensed", Files(("hd2_license.json", "{}"), (A + ".patch_0", "a")));
            Cli("add", licensed, "--name", "Licensed");
            PluginHost.Lang = () => "zh-CN";
            var note = PluginFiles.Notes(licensed, PluginHost.Make()).Single();
            Assert.True(Json.Bool(note, "needs"));
            Assert.Equal("HD2QD.License.dll", Json.Str(note, "plugin"));
            Assert.Equal("needs_plugin", Json.Str(note, "key"));                 // the manager's own words: the window words them
            Assert.Equal(new object[] { "会员授权", "HD2QD.License.dll" }, Json.List(note, "args"));
            var first = PluginFiles.Available(Library.Load()).First();
            Assert.Equal("HD2QD.License.dll", first.Plugin.File);
            Assert.Equal(new[] { "Licensed" }, first.Mods);
        }

        /// <summary>The official plugins not added are listed (the user, 2026-11-03: the page
        /// said nothing of what can be added); one for another Api is not; an added one leaves
        /// the list, and comes back when it is removed.</summary>
        [Fact]
        public void The_official_plugins_not_added_are_listed()
        {
            Assert.Equal(new[] { "HD2QD.Sample.dll", "HD2QD.License.dll" }, PluginFiles.Available(Library.Load()).Select(p => p.Plugin.File));
            var said = Cli("plugins");
            Assert.Contains("official plugins not added", said);
            Assert.Contains("HD2QD.Sample.dll  Sample plugin  not released yet", said);
            Assert.DoesNotContain("HD2QD.Later.dll", said);
            AddSample();
            Assert.Equal(new[] { "HD2QD.License.dll" }, PluginFiles.Available(Library.Load()).Select(p => p.Plugin.File));
            Cli("plugin-remove", "Sample");
            Assert.Equal(2, PluginFiles.Available(Library.Load()).Count);
        }

        /// <summary>The official list is read from its repository, not carried by the program (the
        /// user, 2026-11-03: "列表不能内置"), and kept: offline, or when what comes back is not
        /// a list, the copy read before stays.</summary>
        [Fact]
        public void The_official_list_is_read_from_its_repository_and_kept()
        {
            PluginFiles.OfficialJson = KeptList;
            Assert.Empty(PluginFiles.Official());                                 // never read: nothing to show
            Assert.Equal((false, "offline"), PluginFiles.RefreshList());
            Assert.Contains("the official plugin list could not be read", Cli("plugins"));

            var (url, asked) = Serve(System.Text.Encoding.UTF8.GetBytes(OfficialList));
            PluginFiles.ListUrl = url + "/pat-pat-patrick/hd2qd-plugins/main/plugins.json";
            Assert.Equal((true, (string)null), PluginFiles.RefreshList());
            Assert.Equal("/pat-pat-patrick/hd2qd-plugins/main/plugins.json", asked.Result);
            Assert.Equal(new[] { "HD2QD.Sample.dll", "HD2QD.License.dll" }, PluginFiles.Official().Select(p => p.File));
            Assert.True(File.Exists(Path.Combine(Store.Home, "official_plugins.json")));

            PluginFiles.ListUrl = null;
            Assert.Equal((false, "offline"), PluginFiles.RefreshList());
            Assert.Equal(2, PluginFiles.Official().Count);
            var (url2, _) = Serve(System.Text.Encoding.UTF8.GetBytes("<html>not found</html>"));
            PluginFiles.ListUrl = url2 + "/plugins.json";
            Assert.Equal((false, "not_list"), PluginFiles.RefreshList());
            Assert.Equal(2, PluginFiles.Official().Count);
            Assert.Contains("HD2QD.Sample.dll  Sample plugin  not released yet", Cli("plugins"));
        }

        /// <summary>A language's translations are one file beside the list (the user, 2026-11-03:
        /// a field per language would not scale): read with it, each field falling back to
        /// English, and gone again when the file is.</summary>
        [Fact]
        public void The_translations_are_a_file_of_their_own()
        {
            PluginFiles.OfficialJson = KeptList;
            PluginFiles.OfficialLocaleJson = KeptLocale;
            var u8 = System.Text.Encoding.UTF8;
            var url = ServeMany(2, new Dictionary<string, byte[]>
            {
                ["/l/plugins.json"] = u8.GetBytes(OfficialList), ["/l/locales/zh-CN.json"] = u8.GetBytes(ZhList),
            });
            PluginFiles.ListUrl = url + "/l/plugins.json";
            Assert.Equal((true, (string)null), PluginFiles.RefreshList("zh-CN"));
            var sample = PluginFiles.OfficialNamed("Sample");
            Assert.Equal("示例插件", sample.TitleIn("zh-CN"));
            Assert.Equal("A sample.", sample.SummaryIn("zh-CN"));                 // not translated: English
            Assert.Equal("Sample plugin", sample.TitleIn("de"));                  // no such language: English
            Assert.Equal("Sample plugin", sample.TitleIn("en"));
            Assert.Equal("登录。", PluginFiles.OfficialNamed("License").SummaryIn("zh-CN"));

            url = ServeMany(2, new Dictionary<string, byte[]> { ["/l/plugins.json"] = u8.GetBytes(OfficialList) });
            PluginFiles.ListUrl = url + "/l/plugins.json";
            Assert.Equal((true, (string)null), PluginFiles.RefreshList("zh-CN"));   // the translations withdrawn
            Assert.Equal("Sample plugin", PluginFiles.OfficialNamed("Sample").TitleIn("zh-CN"));
        }

        /// <summary>plugin-add by an official plugin's name downloads its newest release from its
        /// repository and adds it only when the bytes are the ones the list names.</summary>
        [Fact]
        public void An_official_plugin_is_downloaded_and_checked()
        {
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "sample"), "a file that is not the plugin");
            try
            {
                var err = new StringWriter();                                      // a name, not that file
                Assert.Equal(1, Commands.Run(new[] { "plugin-add", "Sample" }, new StringWriter(), err));
                Assert.Contains("not been released yet", err.ToString());
            }
            finally { File.Delete(Path.Combine(Environment.CurrentDirectory, "sample")); }
            Assert.Empty(PluginFiles.All());

            OfficialList = List(@"{""version"": ""0.9.0"", ""tag"": ""v0.9.0"", ""sha256"": ""00""}, {""version"": ""1.0.0"", ""tag"": ""v1.0.0"", ""sha256"": """ + Sha(Sample) + @"""}");
            try
            {
                var (url, asked) = Serve(File.ReadAllBytes(Sample));
                PluginFiles.Downloads = url;
                Assert.Contains("added HD2QD.Sample.dll 1.0.0 to", Cli("plugin-add", "Sample"));
                Assert.Equal("/someone/sample/releases/download/v1.0.0/HD2QD.Sample.dll", asked.Result);
                Cli("plugin-remove", "Sample");

                var (url2, _) = Serve(Encoding("not the plugin"));                  // other bytes: refused
                PluginFiles.Downloads = url2;
                Assert.Equal("plugin_sha", Assert.Throws<ModError>(() => PluginFiles.Fetch(PluginFiles.OfficialNamed("Sample"))).Kind);
                Assert.Empty(PluginFiles.All());
            }
            finally { OfficialList = List(null); }
        }

        /// <summary>An added plugin is offered the official list's newer release, and plugin-update
        /// puts it in place (the user, 2026-11-03: an added plugin was never offered its new
        /// version). Must live: 1.0.0 added, 1.1.0 listed. Must die: the same or an older version
        /// listed offers nothing.</summary>
        [Fact]
        public void An_added_plugin_is_offered_its_newer_release()
        {
            AddSample();
            var t = PluginFiles.All().Single().Type;
            string V(string v) => @"{""version"": """ + v + @""", ""tag"": ""v" + v + @""", ""sha256"": """ + Sha(Sample) + @"""}";
            try
            {
                OfficialList = List(V("1.0.0"));
                Assert.Null(PluginFiles.UpdateOf("HD2QD.Sample.dll", t));
                Assert.Contains("every added plugin is the newest release", Cli("plugin-update"));
                OfficialList = List(V("0.9.0"));
                Assert.Null(PluginFiles.UpdateOf("HD2QD.Sample.dll", t));

                OfficialList = List(V("1.0.0") + ", " + V("1.1.0"));
                var u = PluginFiles.UpdateOf("HD2QD.Sample.dll", t);
                Assert.Equal(("1.0.0", "1.1.0"), (u.Value.Added, u.Value.Newest));
                Assert.Contains("1.1.0 is out (plugin-update)", Cli("plugins"));
                var (url, asked) = Serve(File.ReadAllBytes(Sample));
                PluginFiles.Downloads = url;
                Assert.Contains("updated HD2QD.Sample.dll 1.0.0 -> 1.1.0", Cli("plugin-update"));
                Assert.Equal("/someone/sample/releases/download/v1.1.0/HD2QD.Sample.dll", asked.Result);
            }
            finally { OfficialList = List(null); }
        }

        static byte[] Encoding(string s) => System.Text.Encoding.UTF8.GetBytes(s);

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

        /// <summary>A title-bar button only for an official plugin the official list gives one, proved
        /// by its SHA-256, not its name (the user, 2026-11-03: the title bar is the manager's own);
        /// drawn with the plugin's own app.ico; shown unless the player takes it away.</summary>
        [Fact]
        public void Only_an_official_plugin_the_list_names_gets_a_title_bar_button()
        {
            AddSample();
            var t = PluginFiles.All().Single().Type;
            string Entry(string sha, bool button) =>
                @"{""format"": 2, ""plugins"": [{""file"": ""HD2QD.Sample.dll"", ""title"": ""Sample plugin"", ""repository"": ""someone/sample"", ""api"": 2"
                + (button ? @", ""button"": true" : "") + @", ""versions"": [{""version"": ""1.0.0"", ""tag"": ""v1.0.0"", ""sha256"": """ + sha + @"""}]}]}";
            var real = Sha(Sample);
            PluginFiles.OfficialJson = () => Entry(real, true);
            Assert.True(PluginFiles.HasButton("HD2QD.Sample.dll", t));
            PluginFiles.OfficialJson = () => Entry(real, false);                 // the list gives it none
            Assert.False(PluginFiles.HasButton("HD2QD.Sample.dll", t));
            PluginFiles.OfficialJson = () => Entry(new string('0', 64), true);   // the same name, other bytes: not official
            Assert.False(PluginFiles.HasButton("HD2QD.Sample.dll", t));
            PluginFiles.OfficialJson = () => OfficialList;                       // named, never released: not official
            Assert.False(PluginFiles.HasButton("HD2QD.Sample.dll", t));
            var icon = PluginFiles.IconBytes(t);
            Assert.Equal(new byte[] { 0, 0, 1, 0 }, icon.Take(4).ToArray());    // an .ico
            Assert.True(PluginFiles.Pinned("HD2QD.Sample.dll"));
            PluginFiles.Pin("HD2QD.Sample.dll", false);
            Assert.False(PluginFiles.Pinned("hd2qd.sample.dll"));
            PluginFiles.Pin("HD2QD.Sample.dll", true);
            Assert.True(PluginFiles.Pinned("HD2QD.Sample.dll"));
        }

        [Fact]
        public void A_plugins_start_runs_with_the_host()
        {
            Assert.False(PluginFiles.Start(PluginHost.Make()));                    // none added
            AddSample();
            Assert.True(PluginFiles.Start(PluginHost.Make()));
            Assert.True(File.Exists(Path.Combine(Store.Home, "sample_started.txt")));
        }

        /// <summary>What every call gets: the settings folder, AppData, the language, the game's
        /// data folder, and the enabled mods and the whole library on demand.</summary>
        [Fact]
        public void The_host_carries_the_folders_and_the_library()
        {
            var a = Mod("Alpha", Files((A + ".patch_0", "a")));
            Cli("add", a, "--name", "Alpha");
            PluginHost.Lang = () => "pt-BR";
            var h = PluginHost.Make();
            Assert.Equal(Store.Home, h["home"]);
            Assert.Equal("pt-BR", h["lang"]);
            Assert.False(h.ContainsKey("zh"));
            Assert.Equal(Data, h["data"]);
            var mods = ((Func<List<Dictionary<string, object>>>)h["mods"])();
            Assert.Equal("Alpha", Json.Str(mods.Single(), "name"));
            var lib = ((Func<List<Dictionary<string, object>>>)h["library"])();
            Assert.Equal(a, Json.Str(lib.Single(), "path"));
            Assert.True(Json.Bool(lib.Single(), "enabled"));
        }
    }
}
