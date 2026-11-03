using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>Sending a problem report to the mods' author (Report.Upload, the window's upload
    /// dialog; the server is utilities\hd2-mod-report), against a fake of the server on localhost.</summary>
    public class ReportUploads : Fixture
    {
        readonly System.Net.HttpListener server = new System.Net.HttpListener();
        // what the fake answers next, and what it was sent
        int status = 200;
        string answer = "{\"id\": \"QD-7F3K2M\"}";
        readonly List<(string Path, string Meta, string Type, byte[] Body)> sent = new List<(string, string, string, byte[])>();

        public ReportUploads()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var url = "http://localhost:" + port + "/";
            server.Prefixes.Add(url);
            server.Start();
            Report.UploadUrl = url;
            System.Threading.Tasks.Task.Run(() =>
            {
                while (server.IsListening)
                {
                    System.Net.HttpListenerContext c;
                    try { c = server.GetContext(); } catch (Exception) { return; }
                    var body = new MemoryStream();
                    c.Request.InputStream.CopyTo(body);
                    lock (sent) sent.Add((c.Request.Url.AbsolutePath, c.Request.Headers["X-Report-Meta"], c.Request.ContentType, body.ToArray()));
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    c.Response.StatusCode = status;
                    c.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    c.Response.Close();
                }
            });
            // no game logs of the player's: an empty folder of the test's own
            LuaMods.GameFolderOverride = Path.Combine(Root, "logs");
            Directory.CreateDirectory(LuaMods.GameFolderOverride);
            // nor MDL3's: the report reads %LOCALAPPDATA%
            Mdl3Native.LocalAppData = () => Path.Combine(Root, "local");
        }

        public override void Dispose()
        {
            server.Close();
            LuaMods.GameFolderOverride = null;
            Mdl3Native.LocalAppData = () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Report.LogTooBig = 100L << 20;
            Report.LogBudget = 12 << 20;
            Report.UploadMax = 20 << 20;
            Report.UploadUrl = "http://127.0.0.1:1";
            base.Dispose();
        }

        static Dictionary<string, object> Meta(string header)
        {
            var b64 = header.Replace('-', '+').Replace('_', '/');
            b64 += new string('=', (4 - b64.Length % 4) % 4);
            return (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        }

        /// <summary>Must live: the zip as it is on disk, the kind, the note with the player's user
        /// folder hidden, and the report's summary go; the server's id comes back.</summary>
        [Fact]
        public void A_report_goes_with_its_kind_note_and_summary()
        {
            Cli("add", Mod("Cape", Files((A + ".patch_0", "cape"))));
            var (zip, _) = Report.Draft(Library.Load(), Game);
            Assert.StartsWith(Path.Combine(Store.Home, "reports"), zip);
            Assert.Contains(Report.Contents(zip), e => e.Name == "summary.json");
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal("QD-7F3K2M", Report.Upload(zip, "crash_mission", "  崩溃了，mod 在 " + profile + "\\mods  "));
            var (path, header, type, body) = sent.Single();
            Assert.Equal("/upload", path);
            Assert.Equal("application/zip", type);
            Assert.Equal(File.ReadAllBytes(zip), body);
            var meta = Meta(header);
            Assert.Equal("crash_mission", Json.Str(meta, "kind"));
            Assert.Equal("崩溃了，mod 在 %USERPROFILE%\\mods", Json.Str(meta, "note"));
            Assert.Equal(StateDoc.Version, Json.Str(meta, "version"));
            Assert.Equal(1L, Json.Long(Json.Obj(meta, "summary"), "mods"));
        }

        /// <summary>Must die: what the server refuses, or an answer that is no id, is an error the
        /// window can name; an unknown kind is never sent.</summary>
        [Fact]
        public void A_refused_upload_says_why()
        {
            var (zip, _) = Report.Draft(Library.Load(), Game);
            foreach (var (code, said, why) in new[] { (429, "{\"error\": \"rate_limited\"}", "rate_limited"), (413, "{\"error\": \"too_large\"}", "too_large"),
                                                      (200, "<html>a captive portal</html>", "server"), (500, "{}", "server") })
            {
                status = code;
                answer = said;
                Assert.Equal(why, Assert.Throws<Report.UploadFailed>(() => Report.Upload(zip, "other", "")).Code);
            }
            Report.UploadUrl = "http://127.0.0.1:1";                         // nobody there
            Assert.Equal("network", Assert.Throws<Report.UploadFailed>(() => Report.Upload(zip, "other", "")).Code);
            int before = sent.Count;
            Assert.Throws<ArgumentException>(() => Report.Upload(zip, "nonsense", ""));
            Assert.Equal(before, sent.Count);
        }

        /// <summary>A note longer than the server keeps is cut to NoteMax here.</summary>
        [Fact]
        public void A_long_note_is_cut()
        {
            var (zip, _) = Report.Draft(Library.Load(), Game);
            Report.Upload(zip, "other", new string('字', Report.NoteMax + 200));
            Assert.Equal(Report.NoteMax, Json.Str(Meta(sent.Single().Meta), "note").Length);
        }

        /// <summary>The window's reports are kept in the settings folder, the five newest.</summary>
        [Fact]
        public void The_five_newest_drafts_are_kept()
        {
            var dir = Path.Combine(Store.Home, "reports");
            Directory.CreateDirectory(dir);
            for (int i = 0; i < 7; i++)
            {
                var old = Path.Combine(dir, "hd2qd-report-2020010" + i + "-000000.zip");
                File.WriteAllBytes(old, new byte[] { 1 });
                File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc));
            }
            var (zip, _) = Report.Draft(Library.Load(), Game);
            var left = Directory.GetFiles(dir, "hd2qd-report-*.zip").Select(Path.GetFileName).OrderBy(x => x).ToList();
            Assert.Equal(5, left.Count);
            Assert.Contains(Path.GetFileName(zip), left);
            Assert.DoesNotContain("hd2qd-report-20200100-000000.zip", left);
        }

        static Dictionary<string, string> Entries(string zip)
        {
            using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
                return z.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd());
        }

        /// <summary>A log that ran away (the user, 2026-11-03: another tool's log grew to gigabytes
        /// and nobody saw it) is named in the summary with its size, and only its end is taken.
        /// Must live: the large one named. Must die: the small one not.</summary>
        [Fact]
        public void A_runaway_log_is_named_and_only_its_end_taken()
        {
            Report.LogTooBig = 4000;
            File.WriteAllText(Path.Combine(LuaMods.GameFolderOverride, "boot_mod_loader.log"), string.Concat(Enumerable.Repeat("the same line again\n", 2000)));
            Cli("add", Mod("Cape", Files((A + ".patch_0", "cape"))));        // the manager's own log: small
            var (zip, summary) = Report.Draft(Library.Load(), Game);
            var e = Entries(zip);
            var facts = (Dictionary<string, object>)Json.Parse(e["summary.json"]);
            Assert.Equal(new[] { "game/boot_mod_loader.log" }, Json.List(facts, "big_logs").OfType<string>());
            Assert.Contains(Json.List(facts, "logs").OfType<Dictionary<string, object>>(), l => Json.Str(l, "name") == "hd2qd.log");
            Assert.Equal(40000L, Json.List(facts, "logs").OfType<Dictionary<string, object>>().Single(l => Json.Str(l, "name") == "game/boot_mod_loader.log")["bytes"]);
            Assert.Contains("logs too large: game/boot_mod_loader.log", summary);
            Assert.Contains("logs too large: game/boot_mod_loader.log", e["info.txt"]);
        }

        /// <summary>Past the report's budget, older logs are only listed (a folder of hundreds of
        /// logs). Must live: the newest are in it. Must die: the oldest are not, but named.</summary>
        [Fact]
        public void Logs_past_the_budget_are_listed_not_taken()
        {
            var dir = Path.Combine(Root, "local", "MDL3", "Logs");
            Directory.CreateDirectory(dir);
            for (int i = 0; i < 5; i++)
            {
                var f = Path.Combine(dir, "run" + i + ".log");
                File.WriteAllText(f, new string('x', 100000));
                File.SetLastWriteTime(f, DateTime.Now.AddMinutes(i - 10));     // run4 the newest
            }
            Report.LogBudget = 250000 + new FileInfo(Path.Combine(Store.Home, "config.json")).Length + 50000;
            var e = Entries(Report.Draft(Library.Load(), Game).Path);
            Assert.Contains("mdl3/MDL3/Logs/run4.log", e.Keys);
            Assert.DoesNotContain("mdl3/MDL3/Logs/run0.log", e.Keys);
            Assert.Contains("mdl3/MDL3/Logs/run0.log", e["mdl3/left_out.txt"]);
            Assert.True(e.Keys.Count(k => k.StartsWith("mdl3/MDL3/Logs/run")) < 5);
        }

        /// <summary>Must die: a report past the server's limit is never sent.</summary>
        [Fact]
        public void A_report_too_large_is_not_sent()
        {
            var (zip, _) = Report.Draft(Library.Load(), Game);
            Report.UploadMax = 10;
            Assert.Equal("too_large", Assert.Throws<Report.UploadFailed>(() => Report.Upload(zip, "other", "")).Code);
            Assert.Empty(sent);
        }

        /// <summary>The command line: report --upload KIND --note TEXT says the id.</summary>
        [Fact]
        public void The_command_line_uploads()
        {
            var outDir = Path.Combine(Root, "out");
            Directory.CreateDirectory(outDir);
            Assert.Contains("uploaded: QD-7F3K2M", Cli("report", "--out", outDir, "--upload", "manager", "--note", "the window froze"));
            Assert.Equal("the window froze", Json.Str(Meta(sent.Single().Meta), "note"));
            CliCode(1, "report", "--out", outDir, "--upload", "nonsense");        // refused, and nothing sent
            Assert.Single(sent);
        }
    }
}
