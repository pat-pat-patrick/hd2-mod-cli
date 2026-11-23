using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>AyakaMods, GameBanana, ModWorkshop and direct links (issue 26): what each page says,
    /// which file is installed, what is newer, and a download that records where it came from.</summary>
    public class SitesReading : Fixture
    {
        [Fact]
        public void Addresses_name_their_site_and_mod()
        {
            Assert.Equal((Sites.Ayaka, "4101"), Sites.Of("https://ayakamods.com/mods/enhanced-bolt-pistol.4101/"));
            Assert.Equal((Sites.Ayaka, "4101"), Sites.Of("https://ayakamods.com/mods/4101/"));
            Assert.Equal((Sites.Ayaka, "1"), Sites.Of("https://ayakamods.com/mods/x.1/download"));
            Assert.Equal((Sites.Banana, "669221"), Sites.Of("https://gamebanana.com/mods/669221"));
            Assert.Equal((Sites.Workshop, "51110"), Sites.Of("https://modworkshop.net/mod/51110"));
            Assert.Equal(Sites.Direct, Sites.Of("https://files.example.com/mods/thing_8430d.zip").Site);
            // not a direct link: a page, an archive on plain http, Nexus Mods' and GitHub's own
            Assert.Null(Sites.Of("https://example.com/mods/thing").Site);
            Assert.Null(Sites.Of("http://example.com/thing.zip").Site);
            Assert.Null(Sites.Of("https://github.com/o/r/releases/download/v1/a.zip").Site);
            Assert.Null(Sites.Of("https://www.nexusmods.com/helldivers2/mods/1").Site);
            Assert.Equal("files.example.com", Sites.Channel("https://files.example.com/a.7z"));
            Assert.Equal("GameBanana", Sites.Channel("https://gamebanana.com/mods/1"));
        }

        [Fact]
        public void AyakaMods_says_its_version_in_the_page_s_JSON_LD()
        {
            var html = "<html><script type=\"application/ld+json\">[{\"@type\":\"BreadcrumbList\"},{\"@type\":\"SoftwareApplication\",\"name\":\"Bolt &amp; Force\"," +
                       "\"softwareVersion\":\"2026-10-19\",\"dateModified\":\"2026-10-19T10:59:34+01:00\",\"author\":{\"@type\":\"Person\",\"name\":\"Bans\"}}]</script></html>";
            var o = Sites.ParseAyaka(html);
            Assert.Equal("Bolt & Force", o.Name);
            Assert.Equal("2026-10-19", o.Version);
            Assert.Equal(new DateTimeOffset(2026, 10, 19, 9, 59, 34, TimeSpan.Zero).ToUnixTimeSeconds(), o.Time);
            Assert.Equal("Bans", o.Author);
            Assert.Null(Sites.ParseAyaka("<html>no data</html>"));
            Assert.True(Sites.Challenge("<script src=\"/cdn-cgi/challenge-platform/h/b/orchestrate\"></script>"));
            Assert.False(Sites.Challenge(html));
        }

        const string Banana = "{\"_sName\":\"Swimsuit\",\"_sVersion\":\"1.3.0\",\"_tsDateUpdated\":1700000100,\"_aSubmitter\":{\"_sName\":\"kkkkk76\"},\"_aFiles\":[" +
            "{\"_idRow\":11,\"_sFile\":\"sa-8_no_helm_8430d.zip\",\"_sDescription\":\"SA-8 NO HELM\",\"_tsDateAdded\":1700000000,\"_sAvResult\":\"clean\",\"_sMd5Checksum\":\"aaa\",\"_sDownloadUrl\":\"https://evil.example/x.zip\"}," +
            "{\"_idRow\":12,\"_sFile\":\"dp-00_1acbd.zip\",\"_sDescription\":\"DP-00\",\"_tsDateAdded\":1700000001,\"_sAvResult\":\"clean\",\"_sMd5Checksum\":\"bbb\"}," +
            "{\"_idRow\":13,\"_sFile\":\"old.zip\",\"_bIsArchived\":true,\"_tsDateAdded\":1600000000}," +
            "{\"_idRow\":14,\"_sFile\":\"bad.zip\",\"_sAvResult\":\"infected\",\"_tsDateAdded\":1600000000}," +
            "{\"_idRow\":15,\"_sFile\":\"tool.exe\",\"_tsDateAdded\":1600000000}]}";

        [Fact]
        public void GameBanana_offers_its_live_archives_by_its_own_links()
        {
            var o = Sites.ParseBanana(Banana);
            Assert.Equal(new[] { "12", "11" }, o.Files.Select(f => f.Id));
            Assert.All(o.Files, f => Assert.StartsWith(Sites.BananaBase + "/dl/", f.Url));
            Assert.Equal("1.3.0", o.Files[0].Version);
            Assert.Equal(1700000100, o.Time);
            Assert.Equal("kkkkk76", o.Author);
            Assert.Throws<ModError>(() => Sites.ParseBanana("{\"_bIsTrashed\":true}"));
        }

        [Fact]
        public void ModWorkshop_keeps_to_what_its_author_allows()
        {
            var plain = Sites.ParseWorkshop("{\"name\":\"Pizza\",\"version\":\"4.3\",\"bumped_at\":\"2026-04-30T04:30:31.000000Z\",\"visibility\":\"public\"," +
                                            "\"download\":{\"id\":96181,\"name\":\"Mr Pizza\",\"file\":\"51110_x.zip\",\"type\":\"zip\",\"created_at\":\"2026-04-30T04:30:21Z\"}," +
                                            "\"changelog\":\"v4.3 - Fixed.\\n!!!\\nv4.2 - Older.\"}", out int n);
            Assert.Equal(Sites.WorkshopApi + "/files/96181/download", plain.Files.Single().Url);
            Assert.Equal("Mr Pizza.zip", plain.Files[0].Name);
            Assert.Equal(new[] { "v4.3 - Fixed.", "v4.2 - Older." }, plain.Changelog);
            var off = Sites.ParseWorkshop("{\"version\":\"1\",\"disable_mod_managers\":true,\"download\":{\"id\":1,\"file\":\"a.zip\"}}", out n);
            Assert.Empty(off.Files);
            Assert.Equal("author_off", off.NoFiles);
            var link = Sites.ParseWorkshop("{\"version\":\"1\",\"download_type\":\"link\",\"download\":{\"id\":5,\"url\":\"https://example.com\"}}", out n);
            Assert.Equal("elsewhere", link.NoFiles);
            Assert.Throws<ModError>(() => Sites.ParseWorkshop("{\"suspended\":true}", out n));
        }

        static Mod Installed(string page, PageRef got = null, string md5 = null, string source = null) =>
            new Mod { Id = "m", Name = "Swimsuit", Path = "C:\\nowhere\\Swimsuit", Page = page, PageGot = got, ArchiveMd5 = md5, Source = source };

        static UpdateInfo Decide(Mod m, SiteOffer o)
        {
            var u = new UpdateInfo();
            Sites.Decide(m, o, u);
            return u;
        }

        [Fact]
        public void GameBanana_knows_the_installed_file_by_its_MD5_then_its_line()
        {
            var page = "https://gamebanana.com/mods/1";
            var o = Sites.ParseBanana(Banana);
            Assert.Equal("current", Decide(Installed(page, md5: "AAA"), o).Status);

            // the file recorded is gone (a new upload took its place): its label finds its line
            var u = Decide(Installed(page, new PageRef { File = "9", Version = "1.2.0", Time = 1690000000, Name = "sa-8_no_helm_77777.zip", Label = "SA-8 NO HELM" }), o);
            Assert.Equal("update", u.Status);
            Assert.Equal("1.2.0", u.Current);
            Assert.Equal("1.3.0", u.Latest);
            Assert.Equal("11", u.LatestTag);
            Assert.Equal(Sites.BananaBase + "/dl/11", u.Download);

            // several files and nothing known of the installed one: the page, and why
            var none = Decide(Installed(page), o);
            Assert.Equal("unknown", none.Status);
            Assert.Null(none.Download);
            Assert.Equal("page_only_several", none.Why);
        }

        [Fact]
        public void AyakaMods_compares_its_last_update_with_the_download()
        {
            var page = "https://ayakamods.com/mods/x.1/";
            var o = new SiteOffer { Site = Sites.Ayaka, Page = page, Version = "2026-10-19", Time = 1760000000, NoFiles = "sign_in" };
            var newer = Decide(Installed(page, new PageRef { Time = 1750000000 }), o);
            Assert.Equal("update", newer.Status);
            Assert.Null(newer.Download);
            Assert.Equal("page_only_sign_in", newer.Why);
            // downloaded after the upload, or within a minute of it: the same
            Assert.Equal("current", Decide(Installed(page, new PageRef { Time = 1760000030 }), o).Status);
            // nothing recorded: not called an update
            Assert.Equal("unknown", Decide(Installed(page), o).Status);
            // the folder's name says the page's version
            var named = Installed(page);
            named.Path = "C:\\nowhere\\Bolt v2.1";
            Assert.Equal("current", Decide(named, new SiteOffer { Site = Sites.Ayaka, Page = page, Version = "2.1", Time = 1760000000 }).Status);
        }

        [Fact]
        public void A_direct_link_compares_the_server_s_ETag()
        {
            var url = "https://files.example.com/thing.zip";
            var o = new SiteOffer { Site = Sites.Direct, Page = url, ETag = "\"b\"", Time = 1700000000 };
            o.Files.Add(new SiteFile { Url = url, Name = "thing.zip" });
            Assert.Equal("current", Decide(Installed(url, new PageRef { ETag = "\"b\"" }), o).Status);
            var u = Decide(Installed(url, new PageRef { ETag = "\"a\"" }), o);
            Assert.Equal("update", u.Status);
            Assert.Equal(url, u.Download);
            Assert.Equal("unknown", Decide(Installed(url), o).Status);
        }

        [Fact]
        public void A_download_s_Zone_Identifier_carries_the_page_and_what_it_offered()
        {
            var file = Path.Combine(Root, "dp-00_1acbd.zip");
            File.WriteAllBytes(file, new byte[] { 1 });
            Assert.True(Native.WriteStream(file, "Zone.Identifier", Sites.Zone(Sites.BananaBase + "/dl/12", "https://gamebanana.com/mods/1",
                new PageRef { Version = "1.3.0", File = "12", Time = 1700000001, Name = "dp-00_1acbd.zip", Label = "DP-00" })));
            var m = new Mod();
            Origins.FromFile(m, file);
            Assert.Equal("https://gamebanana.com/mods/1", m.Page);
            Assert.Equal("12", m.PageGot.File);
            Assert.Equal("DP-00", m.PageGot.Label);
            Assert.Equal(1700000001, m.PageGot.Time);
            Assert.Equal("GameBanana", Origins.Of(m).Channel);

            // a browser's download of a page we check: when it was downloaded
            var other = Path.Combine(Root, "other.zip");
            File.WriteAllBytes(other, new byte[] { 1 });
            Hd2ModTestsZone(other, "https://ayakamods.com/mods/x.1/download", "https://ayakamods.com/mods/x.1/");
            // (writing the stream moves the file's time, as the browser's own writing does)
            File.SetLastWriteTimeUtc(other, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
            var b = new Mod();
            Origins.FromFile(b, other);
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), b.PageGot.Time);

            // kept by the library
            var lib = new Library();
            m.Id = "x"; m.Name = "x"; m.Path = Root;
            lib.Mods.Add(m);
            lib.Save();
            var again = Library.Load().Mods.Single();
            Assert.Equal("DP-00", again.PageGot.Label);
            Assert.Equal("dp-00_1acbd.zip", again.PageGot.Name);
            Assert.Equal("1.3.0", again.PageGot.Version);
        }

        static void Hd2ModTestsZone(string file, string host, string referrer) =>
            Native.WriteStream(file, "Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=" + referrer + "\r\nHostUrl=" + host + "\r\n");
    }

    /// <summary>The sites asked through a server here: a link added, a check, an update, and the
    /// courtesy kept with a site that turns us away.</summary>
    public class SitesOnline : Fixture
    {
        readonly System.Net.HttpListener server = new System.Net.HttpListener();
        readonly string baseUrl;
        readonly string[] kept = { Sites.AyakaBase, Sites.BananaBase, Sites.WorkshopApi };
        readonly TimeSpan keptWait = Sites.RetryWait;
        public readonly List<string> Seen = new List<string>();
        /// <summary>The GameBanana mod's files, as the fake answers them (a test changes them).</summary>
        string files;
        readonly Dictionary<string, byte[]> zips = new Dictionary<string, byte[]>();
        int ayakaRefusals;

        public SitesOnline()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            baseUrl = "http://localhost:" + port + "/";
            server.Prefixes.Add(baseUrl);
            server.Start();
            Sites.AyakaBase = baseUrl + "ayaka";
            Sites.BananaBase = baseUrl + "gb";
            Sites.WorkshopApi = baseUrl + "mw";
            Sites.RetryWait = TimeSpan.FromMilliseconds(50);
            foreach (var k in Sites.Gap.Keys.ToList()) Sites.Gap[k] = TimeSpan.Zero;
            Sites.BeginCheck();
            zips["11"] = Zip(("Swim/manifest.json", "{\"Name\": \"Swimsuit SA-8\"}"), ("Swim/" + A + ".patch_0", "one"));
            zips["21"] = Zip(("Swim/manifest.json", "{\"Name\": \"Swimsuit SA-8\"}"), ("Swim/" + A + ".patch_0", "two"));
            zips["12"] = Zip(("Dp/manifest.json", "{\"Name\": \"Swimsuit DP-00\"}"), ("Dp/" + A + ".patch_0", "dp"));
            files = File11 + "," + File12;
            System.Threading.Tasks.Task.Run(Serve);
        }

        const string File11 = "{\"_idRow\":11,\"_sFile\":\"sa-8_8430d.zip\",\"_sDescription\":\"SA-8\",\"_sVersion\":\"1.0\",\"_tsDateAdded\":1700000000,\"_sAvResult\":\"clean\"}";
        const string File12 = "{\"_idRow\":12,\"_sFile\":\"dp-00_1acbd.zip\",\"_sDescription\":\"DP-00\",\"_sVersion\":\"1.0\",\"_tsDateAdded\":1700000001,\"_sAvResult\":\"clean\"}";
        const string File21 = "{\"_idRow\":21,\"_sFile\":\"sa-8_9f00e.zip\",\"_sDescription\":\"SA-8\",\"_sVersion\":\"1.1\",\"_tsDateAdded\":1710000000,\"_sAvResult\":\"clean\"}";

        static byte[] Zip(params (string Name, string Body)[] entries)
        {
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var (name, body) in entries)
                    using (var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false))) w.Write(body);
            return ms.ToArray();
        }

        void Serve()
        {
            while (server.IsListening)
            {
                System.Net.HttpListenerContext c;
                try { c = server.GetContext(); }
                catch (Exception) { return; }
                var path = c.Request.Url.AbsolutePath;
                lock (Seen) Seen.Add(c.Request.HttpMethod + " " + path + " " + c.Request.UserAgent);
                int status = 200;
                byte[] body;
                if (path == "/gb/apiv11/Mod/1")
                    body = Encoding.UTF8.GetBytes("{\"_sName\":\"Swimsuit\",\"_sVersion\":\"\",\"_aFiles\":[" + files + "]}");
                else if (path.StartsWith("/gb/dl/") && zips.TryGetValue(path.Substring(7), out var z))
                {
                    c.Response.AddHeader("Content-Disposition", "attachment; filename=\"" + (path.EndsWith("21") ? "sa-8_9f00e.zip" : path.EndsWith("11") ? "sa-8_8430d.zip" : "dp-00_1acbd.zip") + "\"");
                    body = z;
                }
                else if (path == "/ayaka/mods/7/")
                {
                    if (ayakaRefusals-- > 0)
                    {
                        status = 403;
                        c.Response.AddHeader("cf-mitigated", "challenge");
                        body = Encoding.UTF8.GetBytes("<html>Just a moment...<script src=\"/cdn-cgi/challenge-platform/x\"></script></html>");
                    }
                    else body = Encoding.UTF8.GetBytes("<script type=\"application/ld+json\">{\"@type\":\"SoftwareApplication\",\"name\":\"Bolt\",\"softwareVersion\":\"2\",\"dateModified\":\"2026-10-19T10:00:00Z\"}</script>");
                }
                else { status = 404; body = Encoding.UTF8.GetBytes("no"); }
                c.Response.StatusCode = status;
                c.Response.ContentLength64 = body.Length;
                try { c.Response.OutputStream.Write(body, 0, body.Length); c.Response.Close(); }
                catch (Exception) { }
            }
        }

        public override void Dispose()
        {
            server.Stop();
            Sites.AyakaBase = kept[0]; Sites.BananaBase = kept[1]; Sites.WorkshopApi = kept[2];
            Sites.RetryWait = keptWait;
            Sites.Gap[Sites.Ayaka] = TimeSpan.FromSeconds(2);
            Sites.Gap[Sites.Workshop] = TimeSpan.FromMilliseconds(700);
            Sites.Gap[Sites.Banana] = TimeSpan.FromMilliseconds(500);
            Sites.BeginCheck();
            base.Dispose();
        }

        /// <summary>The download page's look-up (Sites.Look): a link's page with what the window's
        /// card shows, its files, or why there are none to download.</summary>
        [Fact]
        public void A_link_is_looked_up_with_its_page_s_words_and_files()
        {
            var gb = Sites.Look("https://gamebanana.com/mods/1");
            Assert.Equal(("GameBanana", "1", "Swimsuit"), (gb.Shown, gb.Id, gb.Name));
            Assert.Equal(new[] { "12", "11" }, gb.Files.Select(f => f.Id));
            Assert.Null(gb.NoFiles);
            var ayaka = Sites.Look("https://ayakamods.com/mods/bolt.7/");
            Assert.Equal(("AyakaMods", "Bolt", "2", "sign_in"), (ayaka.Shown, ayaka.Name, ayaka.Version, ayaka.NoFiles));
            Assert.Empty(ayaka.Files);
            // Nexus Mods and a page of no known site: nothing asked of the network
            int asked = Seen.Count;
            var nexus = Sites.Look("https://www.nexusmods.com/helldivers2/mods/16662");
            Assert.Equal(("Nexus Mods", "sign_in"), (nexus.Shown, nexus.NoFiles));
            var other = Sites.Look("https://example.com/mods/thing");
            Assert.Equal(("unknown_page", "https://example.com/mods/thing"), (other.NoFiles, other.Page));
            Assert.Equal(asked, Seen.Count);
            Assert.Equal("bad_url", Assert.Throws<ModError>(() => Sites.Look("16662")).Kind);
        }

        [Fact]
        public void A_link_s_file_is_added_checked_and_updated_in_place()
        {
            // a page with two files asks which one
            Assert.Equal("choose_file", Refused("add-link", "https://gamebanana.com/mods/1"));
            Cli("add-link", "https://gamebanana.com/mods/1", "--file", "11");
            var mod = Library.Load().Mods.Single();
            Assert.Equal("Swimsuit SA-8", mod.Name);
            Assert.Equal("https://gamebanana.com/mods/1", mod.Page);
            Assert.Equal("https://gamebanana.com/mods/1", mod.Source);
            Assert.Equal("11", mod.PageGot.File);
            Assert.Equal("SA-8", mod.PageGot.Label);
            Assert.Empty(Directory.GetFiles(Path.Combine(Root, "library"), "*.zip"));       // the archive went
            Assert.StartsWith("current", Cli("check-updates").Trim());

            // the author uploads 1.1 in the old file's place: its line is SA-8, not DP-00
            files = File21 + "," + File12;
            var said = Cli("check-updates");
            Assert.Contains("update    Swimsuit SA-8  [GameBanana]  1.0 -> 1.1", said);
            Cli("update", "Swimsuit SA-8");
            mod = Library.Load().Mods.Single();
            Assert.Equal("21", mod.PageGot.File);
            Assert.Equal("1.1", mod.PageGot.Version);
            Assert.Contains("two", File.ReadAllText(Directory.GetFiles(mod.Path, "*.patch_0", SearchOption.AllDirectories).Single()));
            Assert.StartsWith("current", Cli("check-updates").Trim());
            Assert.All(Seen, s => Assert.Contains("HD2ModQuickDeploy/", s));
        }

        [Fact]
        public void A_page_s_other_file_dropped_in_is_asked_about_not_put_in_place()
        {
            Cli("add-link", "https://gamebanana.com/mods/1", "--file", "11");
            // DP-00 from the same page is another file's line: asked, not a replace
            Assert.Equal("maybe_update", Refused("add-link", "https://gamebanana.com/mods/1", "--file", "12"));
            Cli("add-link", "https://gamebanana.com/mods/1", "--file", "12", "--new");
            Assert.Equal(2, Library.Load().Mods.Count);
            // SA-8's newer file from the same page: put in its place at once
            files = File21 + "," + File12;
            Cli("add-link", "https://gamebanana.com/mods/1", "--file", "21");
            var mods = Library.Load().Mods;
            Assert.Equal(2, mods.Count);
            Assert.Equal("21", mods.Single(m => m.Name == "Swimsuit SA-8").PageGot.File);
        }

        [Fact]
        public void A_Cloudflare_challenge_is_reported_never_retried_and_not_asked_again_in_the_check()
        {
            ayakaRefusals = 1;
            var lib = new Library();
            foreach (var n in new[] { "a", "b" })
                lib.Mods.Add(new Mod { Id = n, Name = n, Path = Path.Combine(Root, n), Page = "https://ayakamods.com/mods/x.7/", PageGot = new PageRef { Time = 1 } });
            foreach (var m in lib.Mods) Directory.CreateDirectory(m.Path);
            lib.Save();
            var results = Updates.Check(Library.Load(), learn: false);
            Assert.All(results, u => Assert.Equal("site_refuses", u.Why));
            Assert.Single(Seen, s => s.StartsWith("GET /ayaka/mods/7/"));
            // the next check asks again, and the page answers
            var next = Updates.Check(Library.Load(), learn: false);
            Assert.All(next, u => Assert.Equal("update", u.Status));
            Assert.All(next, u => Assert.Equal("page_only_sign_in", u.Why));
        }
    }
}
