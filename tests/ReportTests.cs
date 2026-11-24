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
    /// <summary>The problem report (hd2qd report, Settings in the window).</summary>
    public class Reports : Fixture
    {
        static Dictionary<string, string> Entries(string zip)
        {
            using (var z = ZipFile.OpenRead(zip))
                return z.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd());
        }

        [Fact]
        public void TheReportHoldsTheStateAndTheLogsButNeverTheSignIn()
        {
            var logs = Path.Combine(Root, "logs");
            Directory.CreateDirectory(Path.Combine(logs, "dumps"));
            File.WriteAllText(Path.Combine(logs, "boot_mod_loader.log"), "12:00:00 == boot-mod-loader-0.13.0 2026-10-29 12:00:00\n");
            File.WriteAllBytes(Path.Combine(logs, "dumps", "dump-1.dmp"), new byte[] { 1, 2, 3 });
            LuaMods.GameFolderOverride = logs;
            try
            {
                Cli("add", Mod("Cape", Files((A + ".patch_0", "cape"))));
                Store.Save("deploy_history.json", new Dictionary<string, object> { ["deploys"] = new List<object>() });
                Store.Save("nexus.json", new Dictionary<string, object>
                {
                    ["oauth"] = "SECRET-SIGN-IN", ["key"] = "SECRET-KEY",
                    ["history"] = new List<object> { new Dictionary<string, object> { ["name"] = "Cape", ["result"] = "added" } },
                });
                var outDir = Path.Combine(Root, "out");
                Directory.CreateDirectory(outDir);
                var said = Cli("report", "--out", outDir);
                var zip = Directory.GetFiles(outDir, "hd2qd-report-*.zip").Single();
                Assert.Contains("report written: " + zip, said);
                Assert.Contains("mods: 1 (1 on)", said);
                Assert.Empty(Directory.GetFiles(outDir, "*.part"));

                var e = Entries(zip);
                Assert.Contains("info.txt", e.Keys);
                Assert.Contains("Cape", e["state.json"]);
                Assert.Contains("deploy_history.json", e.Keys);
                Assert.Contains("config.json", e.Keys);
                Assert.Contains("boot-mod-loader-0.13.0", e["game/boot_mod_loader.log"]);
                Assert.Contains("dump-1.dmp", e["game/dumps.txt"]);
                Assert.DoesNotContain(e.Keys, k => k.EndsWith(".dmp"));
                Assert.DoesNotContain(e.Keys, k => k == "nexus.json");
                Assert.All(e.Values, text => Assert.DoesNotContain("SECRET", text));
#if HAS_NEXUS
                Assert.Contains("\"added\"", e["nexus_history.json"]);
#else
                Assert.DoesNotContain("nexus_history.json", e.Keys);
#endif
            }
            finally { LuaMods.GameFolderOverride = null; }
        }

        /// <summary>The Windows user's folder, however it is written, and nothing that only
        /// starts the same.</summary>
        [Fact]
        public void TheUserFolderIsWrittenAsUserProfile()
        {
            const string profile = @"C:\Users\Bob";
            Assert.Equal(@"%USERPROFILE%\mods", Report.Redact(@"C:\Users\Bob\mods", profile));
            Assert.Equal(@"""%USERPROFILE%\\mods""", Report.Redact(@"""C:\\Users\\Bob\\mods""", profile));
            Assert.Equal("%USERPROFILE%/mods", Report.Redact("c:/users/bob/mods", profile));
            Assert.Equal(@"""home"": ""%USERPROFILE%""", Report.Redact(@"""home"": ""C:\\Users\\Bob""", profile));
            Assert.Equal(@"C:\Users\Bobby\mods", Report.Redact(@"C:\Users\Bobby\mods", profile));
        }

        /// <summary>"Logs" in a mod's manifest: each declared file that exists, once, as game/NAME;
        /// nothing outside the game's log folder, and nothing but .log and .txt files.</summary>
        [Fact]
        public void TheLogsAModDeclaresAreInTheReport()
        {
            var logs = Path.Combine(Root, "logs");
            Directory.CreateDirectory(Path.Combine(logs, "sub"));
            File.WriteAllText(Path.Combine(logs, "boot_mod_loader.log"), "the loader's own\n");
            File.WriteAllText(Path.Combine(logs, "cape.log"), "cape started\n");
            File.WriteAllText(Path.Combine(logs, "Boots.TXT"), "boots started\n");
            File.WriteAllText(Path.Combine(logs, "user_settings.config"), "SECRET-SETTINGS");
            File.WriteAllText(Path.Combine(logs, "sub", "deep.log"), "SECRET-DEEP");
            File.WriteAllText(Path.Combine(Root, "outside.log"), "SECRET-OUTSIDE");
            LuaMods.GameFolderOverride = logs;
            try
            {
                Cli("add", Mod("Cape", Files((A + ".patch_0", "cape")), @"{ ""Name"": ""Cape"",
                    ""Logs"": [""cape.log"", ""../outside.log"", ""..\\outside.log"", ""sub\\deep.log"", ""sub/deep.log"",
                               """ + logs.Replace("\\", "\\\\") + @"\\cape.log"", ""user_settings.config"", ""boot_mod_loader.log"",
                               ""not_written_yet.log"", ""left_out.txt"", 7] }"));
                // a second mod naming the same file, in another case: taken once
                Cli("add", Mod("Boots", Files((B + ".patch_0", "boots")), @"{ ""Name"": ""Boots"", ""logs"": [""boots.txt"", ""CAPE.LOG""] }"));
                var (zip, _) = Report.Write(Library.Load(), Game, Path.Combine(Root, "r.zip"));
                var e = Entries(zip);
                Assert.Equal("cape started\n", e["game/cape.log"]);
                Assert.Equal("boots started\n", e.First(kv => kv.Key.Equals("game/boots.txt", StringComparison.OrdinalIgnoreCase)).Value);
                Assert.Equal("the loader's own\n", e["game/boot_mod_loader.log"]);
                Assert.Single(e.Keys, k => k.Equals("game/cape.log", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(e.Keys, k => k.Contains("not_written_yet") || k.Contains("left_out") || k.Contains("deep") || k.Contains("outside") || k.Contains("settings"));
                Assert.All(e.Values, text => Assert.DoesNotContain("SECRET", text));
                Assert.Contains("\"game/cape.log\"", e["summary.json"]);
            }
            finally { LuaMods.GameFolderOverride = null; }
        }

        [Fact]
        public void ALogNameIsAPlainLogFileName()
        {
            Assert.Equal("my_mod.log", Manifest.LogName(" my_mod.log "));
            Assert.Equal("Debug Output.TXT", Manifest.LogName("Debug Output.TXT"));
            foreach (var bad in new[] { null, "", "..", "../a.log", "..\\a.log", "a\\b.log", "a/b.log", "C:\\a.log", "C:a.log",
                                        "a..log", ".log", "a.log.", "a.config", "a.log:stream.log", "a<b.log", "a\0.log" })
                Assert.Null(Manifest.LogName(bad));
        }

        /// <summary>Past the report's size budget, a declared log is only listed.</summary>
        [Fact]
        public void DeclaredLogsPastTheBudgetAreOnlyListed()
        {
            var logs = Path.Combine(Root, "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "cape.log"), "cape started\n");
            LuaMods.GameFolderOverride = logs;
            var budget = Report.LogBudget;
            Report.LogBudget = 1;
            try
            {
                Cli("add", Mod("Cape", Files((A + ".patch_0", "cape")), @"{ ""Name"": ""Cape"", ""Logs"": [""cape.log""] }"));
                var (zip, _) = Report.Write(Library.Load(), Game, Path.Combine(Root, "r.zip"));
                var e = Entries(zip);
                Assert.DoesNotContain("game/cape.log", e.Keys);
                Assert.Contains("game/cape.log", e["game/left_out.txt"]);
                Assert.Contains("\"game/cape.log\"", e["summary.json"]);
            }
            finally { Report.LogBudget = budget; LuaMods.GameFolderOverride = null; }
        }

        [Fact]
        public void ALongLogKeepsItsEnd()
        {
            var logs = Path.Combine(Root, "logs");
            Directory.CreateDirectory(logs);
            var line = new string('x', 99) + "\n";
            File.WriteAllText(Path.Combine(logs, "boot_mod_loader.log"),
                              string.Concat(Enumerable.Repeat(line, Report.LogTail / line.Length + 100)) + "the last session\n");
            LuaMods.GameFolderOverride = logs;
            try
            {
                var (zip, _) = Report.Write(Library.Load(), Game, Path.Combine(Root, "r.zip"));
                var log = Entries(zip)["game/boot_mod_loader.log"];
                Assert.StartsWith("[the first ", log);
                Assert.EndsWith("the last session\n", log);
                Assert.True(log.Length <= Report.LogTail + 100);
            }
            finally { LuaMods.GameFolderOverride = null; }
        }
    }
}
