using System.IO;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>The loaders' logs on the Lua loaders page: entries, sessions, levels (lines as the
    /// loaders wrote them on a real install, 2026-11-01).</summary>
    public class LoaderLogsRead : Fixture
    {
        public LoaderLogsRead()
        {
            LoaderLogs.RoamingOverride = Path.Combine(Root, "roaming");
            LoaderLogs.LocalOverride = Path.Combine(Root, "local");
        }

        public override void Dispose()
        {
            LoaderLogs.RoamingOverride = LoaderLogs.LocalOverride = null;
            base.Dispose();
        }

        static void Put(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        [Fact]
        public void Boot_Mod_Loaders_log_in_sessions_and_levels()
        {
            var s = LoaderLogs.Of("bml").Single();
            Put(s.Path,
                "11:10:46 == boot-mod-loader-0.13.4 2026-10-30 11:10:46 (Lua 5.1)\n" +
                "11:10:46 mods/a/x (patch_3, 100 bytes): loaded in 3 ms\n" +
                "11:27:42 == boot-mod-loader-0.13.4 2026-10-30 11:27:42 (Lua 5.1)\n" +
                "11:27:42 NOT THIS GAME VERSION: stingray:46805\n" +
                "11:27:42 mods/a/y (patch_4): FAILED: attempt to index a nil value\n" +
                "11:27:42 started 1 of 2 mods in 12 ms\n");
            var e = LoaderLogs.Read(s);
            Assert.Equal(6, e.Count);
            Assert.Equal(new[] { 1, 1, 0, 0, 0, 0 }, e.Select(x => x.Session));
            Assert.Equal("11:27:42", e[4].Time);
            Assert.Equal("mods/a/y (patch_4): FAILED: attempt to index a nil value", e[4].Text);
            Assert.Equal(new[] { "info", "info", "info", "warn", "error", "info" }, e.Select(x => x.Level));
        }

        [Fact]
        public void A_stack_traceback_belongs_to_its_error()
        {
            var s = LoaderLogs.Of("mdl3").First(x => x.Name == "MDL3.log");
            Put(s.Path,
                "2026-10-30 15:23:00 BOOT version=3.0.0-dev.18 game_vm=Lua 5.1\n" +
                "2026-10-30 15:23:00 SYS_BATCH_FAILED E_BUDGET E_BUDGET: instruction/time/stop limit\n" +
                "stack traceback:\n\tbridge_runtime: in function 'pcall'\n" +
                "2026-11-01 00:25:13 ESC_UI waiting_menu open=false active=false\n");
            var e = LoaderLogs.Read(s);
            Assert.Equal(3, e.Count);
            Assert.Equal("error", e[1].Level);
            Assert.Contains("in function 'pcall'", e[1].Text);
            Assert.Equal("2026-10-30 15:23:00", e[1].Time);
            Assert.Equal("debug", e[2].Level);
        }

        [Fact]
        public void Chef_times_are_read_past_its_clock()
        {
            var s = LoaderLogs.Of("chef").Single();
            Put(s.Path, "10:38:08    0.0 s | ==== Chef Shared Loader 1.3.0 starting\n" +
                        "10:38:08    0.0 s | two copies of mods/p/b: running the one deployed last\n" +
                        "10:39:09   60.0 s | status: loader avg 0.048 ms\n");
            var e = LoaderLogs.Read(s);
            Assert.Equal("==== Chef Shared Loader 1.3.0 starting", e[0].Text);
            Assert.Equal(new[] { "info", "warn", "debug" }, e.Select(x => x.Level));
        }

        /// <summary>Must-pass and must-fail pairs: a word near an error word is not an error.</summary>
        [Fact]
        public void Levels_from_the_words()
        {
            Assert.Equal("error", LoaderLogs.Level("SYS_BATCH_FAILED E_BUDGET"));
            Assert.Equal("info", LoaderLogs.Level("Startup finished: 12 loaded, 0 failed"));
            Assert.Equal("error", LoaderLogs.Level("Startup finished: 11 loaded, 1 failed"));
            Assert.Equal("info", LoaderLogs.Level("mods/cowboybingus/vehicle_stability: not installed"));
            Assert.Equal("info", LoaderLogs.Level("mods/p/x: already started by Boot Mod Loader (loaded), not started again"));
            Assert.Equal("warn", LoaderLogs.Level("mods/p/x: stopped - Off. It has no shutdown of its own: anything it changed outside its update stays until restart"));
            Assert.Equal("debug", LoaderLogs.Level("gc: full collection: 85 MB -> 48 MB"));
            Assert.Equal("info", LoaderLogs.Level("started 8 of 8 mods in 353 ms"));
        }

        [Fact]
        public void A_log_that_is_not_there_has_no_entries()
        {
            Assert.Empty(LoaderLogs.Read(LoaderLogs.Of("bsl").Single()));
            Assert.Empty(LoaderLogs.Of("other"));
        }
    }
}

namespace Hd2Mod.Tests
{
    /// <summary>The manager's own log (the user, 2026-11-01): what was run and how it ended.</summary>
    public class ManagerLogWritten : Fixture
    {
        [Fact]
        public void Changes_are_logged_with_their_end_and_reads_are_not()
        {
            Cli("add", Mod("Cape", Files((A + ".patch_0", "cape"))));
            Cli("list");
            CliCode(1, "remove", "no such mod");
            var entries = LoaderLogs.Read(LoaderLogs.Of("manager").First());
            Assert.True(entries.All(e => e.Written));
            Assert.Contains(entries, e => e.Level == "info" && e.Text.StartsWith("[cli] run: hd2qd add"));
            Assert.Contains(entries, e => e.Level == "debug" && e.Text.Contains("added 1 Cape"));
            Assert.Contains(entries, e => e.Level == "info" && e.Text.StartsWith("[cli] done: add in "));
            Assert.Contains(entries, e => e.Level == "warn" && e.Text.StartsWith("[cli] refused: remove"));
            Assert.DoesNotContain(entries, e => e.Text.Contains("hd2qd list"));
        }

        [Fact]
        public void A_long_log_starts_again_and_keeps_the_one_before()
        {
            Cli("add", Mod("Cape", Files((A + ".patch_0", "cape"))));
            File.AppendAllText(ManagerLog.PathOf, new string('x', (int)ManagerLog.Max + 10));
            ManagerLog.Info("after");
            Assert.True(new FileInfo(ManagerLog.PathOf).Length < 1000);
            Assert.True(File.Exists(ManagerLog.PreviousPath));
            Assert.Equal("[cli] after", LoaderLogs.Read(LoaderLogs.Of("manager").First()).Single().Text);
        }

        [Fact]
        public void The_problem_report_holds_it()
        {
            LuaMods.GameFolderOverride = Path.Combine(Root, "game logs");
            try
            {
                Cli("add", Mod("Cape", Files((A + ".patch_0", "cape"))));
                var (zip, _) = Report.Write(Library.Load(), Game, Path.Combine(Root, "r.zip"));
                using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
                    Assert.Contains("run: hd2qd add", new StreamReader(z.GetEntry(ManagerLog.File).Open()).ReadToEnd());
            }
            finally { LuaMods.GameFolderOverride = null; }
        }
    }
}
