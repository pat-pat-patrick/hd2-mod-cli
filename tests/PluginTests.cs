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

        /// <summary>A key of the tests' own, trusted as the maintainer's while the action runs.</summary>
        static void AsMaintainer(System.Security.Cryptography.RSA key, Action act)
        {
            var p = key.ExportParameters(false);
            var k = new PluginSignature.Key { Owner = "official", Modulus = p.Modulus, Exponent = p.Exponent };
            PluginSignature.Trusted.Add(k);
            try { act(); }
            finally { PluginSignature.Trusted.Remove(k); }
        }

        /// <summary>(2026-11-28: an official build not in the official list was called a third-party
        /// plugin.) Must live: the sample signed by the maintainer's key, renamed, its version not
        /// in the list, is official, added under its assembly's name, and loads as before.</summary>
        [Fact]
        public void A_plugin_the_maintainer_signed_is_official_at_any_version()
        {
            using (var key = new System.Security.Cryptography.RSACryptoServiceProvider(3072))
            {
                var signed = Path.Combine(Root, "1731234_HD2QD.Sample (1).dll");
                File.WriteAllBytes(signed, PluginSignature.Sign(File.ReadAllBytes(Sample), key));
                OfficialList = List(@"{""version"": ""0.9.0"", ""tag"": ""v0.9.0"", ""sha256"": """ + new string('0', 64) + @"""}");
                try
                {
                    AsMaintainer(key, () =>
                    {
                        Assert.Equal("official", PluginSignature.Signer(signed));
                        Assert.True(PluginFiles.IsOfficialRelease(signed));
                        Assert.Equal(("HD2QD.Sample.dll", "1.0.0", (string)null), PluginFiles.Inspect(signed));
                        Assert.Contains("added HD2QD.Sample.dll 1.0.0 to", Cli("plugin-add", signed));
                        Assert.True(PluginFiles.IsOfficial("HD2QD.Sample.dll"));
                        Assert.Equal("HD2QD.Sample.dll", PluginFiles.OfficialOf("HD2QD.Sample.dll")?.File);    // its entry, by its name
                        Assert.Contains("HD2QD.Sample.dll 1.0.0  Sample plugin", Cli("plugins"));
                    });
                }
                finally { OfficialList = List(null); }
            }
        }

        /// <summary>Must die: a signed file changed after signing, an unsigned one and one signed by
        /// a key not trusted are not official (and, renamed, are refused for their name).</summary>
        [Fact]
        public void A_changed_unsigned_or_foreign_signed_plugin_is_not_official()
        {
            using (var key = new System.Security.Cryptography.RSACryptoServiceProvider(3072))
            using (var other = new System.Security.Cryptography.RSACryptoServiceProvider(3072))
            {
                var good = PluginSignature.Sign(File.ReadAllBytes(Sample), key);
                var changed = (byte[])good.Clone();
                changed[1000] ^= 0x01;
                var cases = new Dictionary<string, byte[]>
                {
                    ["changed"] = changed,
                    ["unsigned"] = File.ReadAllBytes(Sample),
                    ["foreign"] = PluginSignature.Sign(File.ReadAllBytes(Sample), other),
                };
                AsMaintainer(key, () =>
                {
                    foreach (var c in cases)
                    {
                        var path = Path.Combine(Root, c.Key + "_HD2QD.Sample (1).dll");
                        File.WriteAllBytes(path, c.Value);
                        Assert.Null(PluginSignature.Signer(path));
                        Assert.False(PluginFiles.IsOfficialRelease(path), c.Key);
                        Assert.Equal("name", PluginFiles.Inspect(path).Why);
                    }
                    // the signature checked again once the file changes in place
                    var inPlace = Path.Combine(Root, "HD2QD.Sample.dll");
                    File.WriteAllBytes(inPlace, good);
                    Assert.True(PluginFiles.IsOfficialRelease(inPlace));
                    File.WriteAllBytes(inPlace, changed);
                    File.SetLastWriteTimeUtc(inPlace, DateTime.UtcNow.AddMinutes(1));
                    Assert.False(PluginFiles.IsOfficialRelease(inPlace));
                });
            }
        }

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

        /// <summary>Files dropped on the Plugins page: the .dll files are offered as plugins, several
        /// at once, and go in through the same Install as the button's and plugin-add's (a second
        /// drop of one is an update); folders, archives, other files and a .dll that is not there
        /// are set apart to be named, never installed.</summary>
        [Fact]
        public void Dropped_files_split_into_plugins_and_the_rest_and_install_as_chosen_ones()
        {
            var dir = Path.Combine(Store.Home, "dropped");
            Directory.CreateDirectory(Path.Combine(dir, "a folder"));
            var dll = Path.Combine(dir, "HD2QD.Sample.dll");
            File.Copy(Sample, dll);
            var other = Path.Combine(dir, "HD2QD.Other.DLL");     // the extension in any case
            File.Copy(Sample, other);
            var zip = Path.Combine(dir, "mod.zip");
            File.WriteAllBytes(zip, new byte[] { 1 });
            var txt = Path.Combine(dir, "readme.txt");
            File.WriteAllText(txt, "x");
            var gone = Path.Combine(dir, "HD2QD.Gone.dll");

            var (plugins, others) = PluginFiles.Dropped(new[] { zip, dll, Path.Combine(dir, "a folder"), txt, gone, other, dll, "", null });
            Assert.Equal(new[] { dll, other }, plugins);
            Assert.Equal(new[] { zip, Path.Combine(dir, "a folder"), txt, gone }, others);
            Assert.Empty(PluginFiles.Dropped(null).Plugins);

            // the first drop adds, the second replaces: the same path as the button's
            Assert.Equal(("HD2QD.Sample.dll", "1.0.0", (string)null), PluginFiles.Install(plugins[0]));
            Assert.Equal(("HD2QD.Sample.dll", "1.0.0", "1.0.0"), PluginFiles.Install(plugins[0]));
            Assert.Single(PluginFiles.All());
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

        /// <summary>A plugin is one copy in the process however often its file is read: WPF finds a
        /// window's XAML by the assembly's name, and a second copy broke the Settings plugin's
        /// window (a player, 2026-11-14). Must live: read to be added, read again on a cancelled
        /// add and on a replace by the same file, it stays one assembly and one plugin class. Must
        /// die: a second file of it under another name ("HD2QD.Sample (1).dll") is not loaded.</summary>
        [Fact]
        public void A_plugin_is_one_copy_however_often_it_is_read()
        {
            int Copies() => AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == "HD2QD.Sample");
            AddSample();
            var used = PluginFiles.All().Single().Type;
            int before = Copies();
            Assert.Null(PluginFiles.Inspect(Sample).Why);                           // picked again, then cancelled
            PluginFiles.Install(Sample);                                             // the same file over itself
            Assert.Same(used, PluginFiles.All().Single().Type);
            File.Copy(Sample, Path.Combine(PluginFiles.Dir, "HD2QD.Sample (1).dll"));
            Assert.Equal("HD2QD.Sample.dll", PluginFiles.All().Single().File);
            Assert.Equal(before, Copies());
        }

        /// <summary>An official release is known by its SHA-256 alone, under any name, and goes in
        /// under the list's name with its own version (the user, 2026-11-10: a file passed around by
        /// hand is renamed by a browser or a chat program). Must live: renamed, its bytes listed.
        /// Must die: the same renamed file while the list does not have its bytes is refused.</summary>
        [Fact]
        public void A_renamed_official_release_is_known_by_its_sha256()
        {
            var renamed = Path.Combine(Root, "1731234_HD2QD.Sample (1).dll");
            File.Copy(Sample, renamed);
            OfficialList = List(@"{""version"": ""1.0.0"", ""tag"": ""v1.0.0"", ""sha256"": """ + new string('0', 64) + @"""}");
            try
            {
                Assert.Equal(("1731234_HD2QD.Sample (1).dll", (string)null, "name"), PluginFiles.Inspect(renamed));
                CliCode(1, "plugin-add", renamed);
                Assert.Empty(PluginFiles.All());

                OfficialList = List(@"{""version"": ""1.0.0"", ""tag"": ""v1.0.0"", ""sha256"": """ + Sha(Sample) + @"""}");
                Assert.Equal(("HD2QD.Sample.dll", "1.0.0", (string)null), PluginFiles.Inspect(renamed));
                Assert.Contains("added HD2QD.Sample.dll 1.0.0 to", Cli("plugin-add", renamed));
                Assert.Equal("HD2QD.Sample.dll", PluginFiles.All().Single().File);
                Assert.NotNull(PluginFiles.OfficialOf("HD2QD.Sample.dll"));
            }
            finally { OfficialList = List(null); }
        }

        /// <summary>An official release is added without the trusted-source question (the user,
        /// 2026-11-14: it was asked at every update of an official plugin). Must live: the file
        /// whose SHA-256 the list names is an official release. Must die: the same file while the
        /// list has other bytes, or no list at all, is not, and is still asked about.</summary>
        [Fact]
        public void Only_a_file_the_official_list_names_skips_the_trust_question()
        {
            OfficialList = List(@"{""version"": ""1.0.0"", ""tag"": ""v1.0.0"", ""sha256"": """ + new string('0', 64) + @"""}");
            try
            {
                Assert.False(PluginFiles.IsOfficialRelease(Sample));
                OfficialList = List(@"{""version"": ""1.0.0"", ""tag"": ""v1.0.0"", ""sha256"": """ + Sha(Sample) + @"""}");
                Assert.True(PluginFiles.IsOfficialRelease(Sample));
            }
            finally { OfficialList = List(null); }
            Assert.False(PluginFiles.IsOfficialRelease(Sample));
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

        /// <summary>A plugin's tags for what a mod holds (ModTags) reach the state in the
        /// window's language, once each; a failing plugin gives none; removed, its tags go.</summary>
        [Fact]
        public void A_plugins_tags_for_what_a_mod_holds_reach_the_state()
        {
            var tagged = Mod("Tagged", Files(("sample_tags.txt", "x"), (A + ".patch_0", "a")));
            var failing = Mod("Failing", Files(("sample_throw.txt", "x"), (B + ".patch_0", "b")));
            var plain = Mod("Plain", Files(("c0ffee00c0ffee00.patch_0", "c")));
            Cli("add", tagged, "--name", "Tagged");
            Cli("add", failing, "--name", "Failing");
            Cli("add", plain, "--name", "Plain");
            List<Dictionary<string, object>> TagsOf(string name) =>
                Json.List(Json.List(StateDoc.Build(Library.Load(), Game, false), "mods").Cast<Dictionary<string, object>>().Single(m => Json.Str(m, "name") == name), "plugin_tags")
                    .Cast<Dictionary<string, object>>().ToList();
            Assert.Empty(TagsOf("Tagged"));                                      // not added yet
            AddSample();
            var tag = TagsOf("Tagged").Single();
            Assert.Equal("SAMPLE", Json.Str(tag, "text"));
            Assert.Equal("Holds a sample.", Json.Str(tag, "tip"));
            Assert.Equal("HD2QD.Sample.dll", Json.Str(tag, "plugin"));
            Assert.Empty(TagsOf("Plain"));
            Assert.Empty(TagsOf("Failing"));
            PluginHost.Lang = () => "zh-CN";
            Assert.Equal("示例", Json.Str(TagsOf("Tagged").Single(), "text"));
            PluginFiles.Remove("Sample");
            Assert.Empty(TagsOf("Tagged"));
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

        /// <summary>The author's copy of GitHub (Mirror, the user, 2026-11-24: players in mainland
        /// China cannot reach GitHub). Must live: the list from the copy when GitHub cannot be
        /// reached, or does not answer in time; GitHub's list when it answers. Must die: both
        /// unreachable gives GitHub's reason and keeps the list read before.</summary>
        [Fact]
        public void The_list_comes_from_the_authors_copy_when_GitHub_cannot_be_reached()
        {
            PluginFiles.OfficialJson = KeptList;
            PluginFiles.OfficialLocaleJson = KeptLocale;
            var u8 = System.Text.Encoding.UTF8;
            var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);   // takes the connection, never answers
            silent.Start();
            try
            {
                PluginFiles.ListUrl = "http://127.0.0.1:1/plugins.json";                // GitHub: unreachable
                Mirror.Base = ServeMany(2, new Dictionary<string, byte[]>
                {
                    ["/dl/plugins/plugins.json"] = u8.GetBytes(OfficialList), ["/dl/plugins/locales/zh-CN.json"] = u8.GetBytes(ZhList),
                }) + "/dl";
                Assert.Equal((true, (string)null), PluginFiles.RefreshList("zh-CN"));
                Assert.Equal("示例插件", PluginFiles.OfficialNamed("Sample").TitleIn("zh-CN"));

                PluginFiles.ListUrl = "http://127.0.0.1:" + ((System.Net.IPEndPoint)silent.LocalEndpoint).Port + "/plugins.json";
                Mirror.ListWait = TimeSpan.FromMilliseconds(400);                     // GitHub: no answer in time
                Mirror.Base = ServeMany(1, new Dictionary<string, byte[]> { ["/dl/plugins/plugins.json"] = u8.GetBytes(List(null).Replace("A sample.", "From the copy.")) }) + "/dl";
                Assert.Equal((true, (string)null), PluginFiles.RefreshList());
                Assert.Equal("From the copy.", PluginFiles.OfficialNamed("Sample").Summary);

                PluginFiles.ListUrl = ServeMany(1, new Dictionary<string, byte[]> { ["/plugins.json"] = u8.GetBytes(OfficialList) }) + "/plugins.json";
                Mirror.Base = "http://127.0.0.1:1/dl";                                // GitHub answers: the copy is not needed
                Assert.Equal((true, (string)null), PluginFiles.RefreshList());
                Assert.Equal("A sample.", PluginFiles.OfficialNamed("Sample").Summary);

                PluginFiles.ListUrl = "http://127.0.0.1:1/plugins.json";              // both unreachable
                Assert.Equal((false, "offline"), PluginFiles.RefreshList());
                Assert.Equal(2, PluginFiles.Official().Count);
            }
            finally { silent.Stop(); Mirror.Base = null; Mirror.ListWait = TimeSpan.FromSeconds(8); PluginFiles.ListUrl = null; }
        }

        /// <summary>A plugin's file is taken from the author's copy first, but only when it is the
        /// very file the list names (SHA-256). Must live: the copy's file, GitHub not asked; a
        /// copy with other bytes is passed over for GitHub's. Must die: other bytes in the copy and
        /// GitHub unreachable adds nothing.</summary>
        [Fact]
        public void A_plugin_is_taken_from_the_authors_copy_only_when_it_is_the_listed_file()
        {
            OfficialList = List(@"{""version"": ""1.0.0"", ""tag"": ""v1.0.0"", ""sha256"": """ + Sha(Sample) + @"""}");
            try
            {
                var (copy, copyAsked) = Serve(File.ReadAllBytes(Sample));
                Mirror.Base = copy + "/dl";
                PluginFiles.Downloads = "http://127.0.0.1:1";                         // GitHub: unreachable
                Assert.Contains("added HD2QD.Sample.dll 1.0.0 to", Cli("plugin-add", "Sample"));
                Assert.Equal("/dl/github/someone/sample/releases/download/v1.0.0/HD2QD.Sample.dll", copyAsked.Result);
                Cli("plugin-remove", "Sample");

                var (bad, _) = Serve(Encoding("not the plugin"));
                Mirror.Base = bad + "/dl";
                var (github, githubAsked) = Serve(File.ReadAllBytes(Sample));
                PluginFiles.Downloads = github;
                Assert.Contains("added HD2QD.Sample.dll 1.0.0 to", Cli("plugin-add", "Sample"));
                Assert.Equal("/someone/sample/releases/download/v1.0.0/HD2QD.Sample.dll", githubAsked.Result);
                Cli("plugin-remove", "Sample");

                var (bad2, _) = Serve(Encoding("not the plugin"));
                Mirror.Base = bad2 + "/dl";
                PluginFiles.Downloads = "http://127.0.0.1:1";
                Assert.Equal("offline", Assert.Throws<ModError>(() => PluginFiles.Fetch(PluginFiles.OfficialNamed("Sample"))).Kind);
                Assert.Empty(PluginFiles.All());
            }
            finally { OfficialList = List(null); Mirror.Base = null; PluginFiles.Downloads = "http://127.0.0.1:1"; }
        }

        /// <summary>This program's own update check reads the author's copy of its releases when
        /// GitHub cannot be reached. Must die: both unreachable is an error, as before.</summary>
        [Fact]
        public void The_update_check_reads_the_authors_copy_when_GitHub_cannot_be_reached()
        {
            var api = Updates.GitHubApi;
            try
            {
                Updates.GitHubApi = "http://127.0.0.1:1";
                var releases = @"[{""tag_name"": ""v98.0.0"", ""prerelease"": false, ""draft"": false, ""html_url"": ""https://example.test/get"", ""body"": """"},
                                  {""tag_name"": ""v99.0.0-dev"", ""prerelease"": true, ""draft"": false, ""html_url"": ""https://example.test/get"", ""body"": """"}]";
                Mirror.Base = ServeMany(1, new Dictionary<string, byte[]> { ["/dl/releases/hd2-mod-cli.json"] = Encoding(releases) }) + "/dl";
                var found = SelfUpdate.Check();
                Assert.Equal(SelfUpdate.Official ? "v98.0.0" : "v99.0.0-dev", found.Latest);
                Assert.True(found.Newer);

                Mirror.Base = "http://127.0.0.1:1/dl";
                Assert.Equal("offline", Assert.Throws<ModError>(() => SelfUpdate.Check()).Kind);
            }
            finally { Updates.GitHubApi = api; Mirror.Base = null; }
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
            Assert.Equal(new byte[] { 0, 0, 1, 0 }, icon.Take(4).ToArray());    // an .ico: the app.ico it ships
            var own = new byte[] { 0, 0, 1, 0, 1, 0, 42 };                        // one read at run time comes first
            File.WriteAllBytes(Path.Combine(Store.Home, "sample_icon.ico"), own);
            Assert.Equal(own, PluginFiles.IconBytes(t));
            File.Delete(Path.Combine(Store.Home, "sample_icon.ico"));
            Assert.Equal(icon, PluginFiles.IconBytes(t));
            Assert.True(PluginFiles.Pinned("HD2QD.Sample.dll"));
            PluginFiles.Pin("HD2QD.Sample.dll", false);
            Assert.False(PluginFiles.Pinned("hd2qd.sample.dll"));
            PluginFiles.Pin("HD2QD.Sample.dll", true);
            Assert.True(PluginFiles.Pinned("HD2QD.Sample.dll"));
        }

        /// <summary>(0.22.0) The host says this program's version, so a plugin can say what it
        /// needs of it (the members' plugin: a manager that runs on in the notification area).</summary>
        [Fact]
        public void The_host_says_its_version()
        {
            Assert.Equal(StateDoc.Version, PluginHost.Make()["version"]);
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

        /// <summary>A window plugin's OverlayKey (the user, 2026-11-28: a key that brings a
        /// plugin up in the game, for every plugin): read in one spelling, the player's own
        /// choice over it ("" for none, null for the plugin's again), and a second plugin with
        /// the same key is not held twice.</summary>
        /// <summary>plugin-key on the command line, as the plugin's card has it (the user,
        /// 2026-11-28: every feature with its command). Must live: the plugin's own key shown by
        /// plugins, another set, none, the plugin's own again; a key typed in any spelling. Must
        /// die: a name that is not a key, and a plugin with no key over the game.</summary>
        [Fact]
        public void A_plugin_s_key_is_set_on_the_command_line()
        {
            Assert.Equal("page down", PluginKeys.Valid("Page Down"));
            Assert.Equal("ctrl+shift+f10", PluginKeys.Valid("Shift + Ctrl + F10"));
            Assert.Equal("alt+numpad +", PluginKeys.Valid("alt+numpad +"));
            Assert.Null(PluginKeys.Valid("f25"));
            Assert.Null(PluginKeys.Valid("win+a"));
            Assert.Null(PluginKeys.Valid(""));
            AddSample();
            Assert.Contains("key over the game: shift+page down (the plugin's own)", Cli("plugins"));
            Assert.Contains("HD2QD.Sample.dll: ctrl+f10 (chosen", Cli("plugin-key", "Sample", "--set", "Ctrl+F10"));
            Assert.Equal("ctrl+f10", PluginKeys.Chosen("HD2QD.Sample.dll", "shift+page down"));
            Assert.Contains("HD2QD.Sample.dll: none (chosen", Cli("plugin-key", "Sample", "--clear"));
            Assert.Contains("HD2QD.Sample.dll: shift+page down (the plugin's own)", Cli("plugin-key", "Sample", "--default"));
            var err = new StringWriter();
            Assert.Equal(1, Commands.Run(new[] { "plugin-key", "Sample", "--set", "hyper+x" }, new StringWriter(), err));
            Assert.Contains("is not a key", err.ToString());
            Assert.Equal("shift+page down", PluginKeys.Chosen("HD2QD.Sample.dll", "shift+page down"));
            err = new StringWriter();
            Assert.Equal(1, Commands.Run(new[] { "plugin-key", "NotAdded" }, new StringWriter(), err));
            Assert.Contains("no plugin NotAdded", err.ToString());
        }

        [Fact]
        public void A_plugin_s_key_is_its_own_or_the_player_s_and_never_held_twice()
        {
            AddSample();
            var t = PluginFiles.All().Single().Type;
            Assert.Equal("shift+page down", PluginKeys.Declared(t));
            Assert.Null(PluginKeys.Declared(typeof(string)));                    // no Kind, no key
            Assert.Equal("ctrl+alt+shift+f10", PluginKeys.Normal(" Shift+ALT + ctrl+F10 "));
            Assert.Equal("ctrl+numpad +", PluginKeys.Normal("Ctrl+Numpad +"));
            Assert.Equal("numpad +", PluginKeys.Normal("numpad +"));
            Assert.Equal("", PluginKeys.Normal("  "));

            var plan = PluginKeys.Plan(new[] { ("HD2QD.Sample.dll", t), ("HD2QD.Other.dll", t), ("HD2QD.Plain.dll", typeof(string)) });
            Assert.Equal(new[] { "HD2QD.Sample.dll", "HD2QD.Other.dll" }, plan.Select(b => b.File));
            Assert.Null(plan[0].SameAs);
            Assert.Equal("HD2QD.Sample.dll", plan[1].SameAs);

            PluginKeys.Choose("HD2QD.Other.dll", "Page Up");
            PluginKeys.Choose("HD2QD.Sample.dll", "");
            plan = PluginKeys.Plan(new[] { ("HD2QD.Sample.dll", t), ("HD2QD.Other.dll", t) });
            Assert.Equal("", plan[0].Key);
            Assert.Equal("page up", plan[1].Key);
            Assert.Null(plan[1].SameAs);

            PluginKeys.Choose("HD2QD.Sample.dll", null);                             // the plugin's own again
            Assert.Equal("shift+page down", PluginKeys.Chosen("HD2QD.Sample.dll", PluginKeys.Declared(t)));
        }

        /// <summary>A community list (community.json, the user, 2026-11-28: a store where
        /// third-party plugins are shown), checked by its pull requests but not reviewed.</summary>
        static string CommunityList(string file, string versions, bool button = false) => @"{""format"": 1, ""plugins"": [
            {""file"": """ + file + @""", ""title"": ""Someone's plugin"", ""summary"": ""Theirs."", ""author"": ""someone-else"",
             ""repository"": ""someone-else/theirs"", ""license"": ""MIT"", ""api"": 2" + (button ? @", ""button"": true" : "") + @", ""versions"": [" + versions + @"]},
            {""file"": ""HD2QD.License.dll"", ""title"": ""Not the official one"", ""repository"": ""someone-else/fake"", ""license"": ""MIT"", ""api"": 2,
             ""versions"": [{""version"": ""9.0.0"", ""tag"": ""v9"", ""sha256"": """ + new string('1', 64) + @"""}]}]}";

        /// <summary>Community plugins are listed apart from the official ones. Must live: one
        /// listed is offered, marked community, with its author and licence, in the command line
        /// under its own heading. Must die: one with an official plugin's file name is never
        /// offered (and never replaces the official one), and none gets a title-bar button.</summary>
        [Fact]
        public void Community_plugins_are_listed_apart_never_as_an_official_one()
        {
            PluginFiles.CommunityJson = () => CommunityList("HD2QD.Theirs.dll", @"{""version"": ""1.0.0"", ""tag"": ""v1.0.0"", ""sha256"": """ + new string('2', 64) + @"""}", true);
            var theirs = PluginFiles.Community().Single();
            Assert.Equal("HD2QD.Theirs.dll", theirs.File);
            Assert.True(theirs.Community);
            Assert.False(theirs.Button);
            Assert.Equal("MIT", theirs.License);
            Assert.Equal(new[] { "HD2QD.Sample.dll", "HD2QD.License.dll", "HD2QD.Theirs.dll" }, PluginFiles.Available(Library.Load()).Select(p => p.Plugin.File));
            Assert.False(PluginFiles.OfficialNamed("License").Community);              // the official one, never the community entry
            Assert.Null(PluginFiles.OfficialNamed("Theirs"));                          // install plans name official plugins only
            Assert.Equal("HD2QD.Theirs.dll", PluginFiles.ListedNamed("Theirs").File);
            var said = Cli("plugins");
            Assert.Contains("community plugins not added, not reviewed", said);
            Assert.Contains("HD2QD.Theirs.dll  Someone's plugin  by someone-else, MIT, https://github.com/someone-else/theirs", said);
        }

        /// <summary>A community plugin is downloaded and checked as an official one is, but is asked
        /// about (the GUI), said not to be reviewed (the command line), and offered its updates.
        /// Must live: plugin-add by its name adds the listed bytes; a newer listed version is an
        /// update. Must die: it is not an official release (the trust question is asked), and other
        /// bytes than those listed are refused.</summary>
        [Fact]
        public void A_community_plugin_is_downloaded_checked_and_updated_but_never_official()
        {
            OfficialList = @"{""format"": 2, ""plugins"": []}";
            try
            {
                PluginFiles.CommunityJson = () => CommunityList("HD2QD.Sample.dll", @"{""version"": ""1.0.0"", ""tag"": ""v1.0.0"", ""sha256"": """ + Sha(Sample) + @"""}");
                Assert.False(PluginFiles.IsOfficialRelease(Sample));
                Assert.NotNull(PluginFiles.CommunityRelease(Sample));
                var (url, asked) = Serve(File.ReadAllBytes(Sample));
                PluginFiles.Downloads = url;
                var said = Cli("plugin-add", "Sample");
                Assert.Contains("HD2QD.Sample.dll is a community plugin by someone-else, not reviewed", said);
                Assert.Contains("added HD2QD.Sample.dll 1.0.0 to", said);
                Assert.Equal("/someone-else/theirs/releases/download/v1.0.0/HD2QD.Sample.dll", asked.Result);
                Assert.NotNull(PluginFiles.CommunityOf("HD2QD.Sample.dll"));
                Assert.Null(PluginFiles.OfficialOf("HD2QD.Sample.dll"));
                var t = PluginFiles.All().Single().Type;
                Assert.Null(PluginFiles.UpdateOf("HD2QD.Sample.dll", t));

                PluginFiles.CommunityJson = () => CommunityList("HD2QD.Sample.dll", @"{""version"": ""1.0.0"", ""tag"": ""v1.0.0"", ""sha256"": """ + Sha(Sample) + @"""},
                    {""version"": ""1.1.0"", ""tag"": ""v1.1.0"", ""sha256"": """ + new string('3', 64) + @"""}");
                Assert.Equal("1.1.0", PluginFiles.UpdateOf("HD2QD.Sample.dll", t).Value.Newest);
                var (url2, _) = Serve(Encoding("not the plugin"));
                PluginFiles.Downloads = url2;
                Assert.Equal("plugin_sha", Assert.Throws<ModError>(() => PluginFiles.Fetch(PluginFiles.ListedNamed("Sample"))).Kind);
            }
            finally { OfficialList = List(null); }
        }

        /// <summary>The community list is read beside the official one and kept the same way. Must
        /// live: read with it, from GitHub. Must die: withdrawn (404), it is gone; offline, the copy
        /// read before stays.</summary>
        [Fact]
        public void The_community_list_is_read_beside_the_official_one()
        {
            PluginFiles.OfficialJson = KeptList;
            PluginFiles.CommunityJson = KeptCommunity;
            var u8 = System.Text.Encoding.UTF8;
            var community = CommunityList("HD2QD.Theirs.dll", "");
            PluginFiles.ListUrl = ServeMany(2, new Dictionary<string, byte[]>
            {
                ["/l/plugins.json"] = u8.GetBytes(OfficialList), ["/l/community.json"] = u8.GetBytes(community),
            }) + "/l/plugins.json";
            Assert.Equal((true, (string)null), PluginFiles.RefreshList());
            Assert.Equal(new[] { "HD2QD.Theirs.dll" }, PluginFiles.Community().Select(p => p.File));

            PluginFiles.ListUrl = ServeMany(1, new Dictionary<string, byte[]> { ["/l/plugins.json"] = u8.GetBytes(OfficialList) }) + "/l/plugins.json";
            Assert.Equal((false, (string)null), PluginFiles.RefreshList());            // the community list could not be asked: kept
            Assert.Single(PluginFiles.Community());

            PluginFiles.ListUrl = ServeMany(2, new Dictionary<string, byte[]> { ["/l/plugins.json"] = u8.GetBytes(OfficialList) }) + "/l/plugins.json";
            Assert.Equal((true, (string)null), PluginFiles.RefreshList());             // withdrawn: gone
            Assert.Empty(PluginFiles.Community());
        }
    }
}
