using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>A settings file a power cut left all zero bytes (a player's gui.json,
    /// 2026-11-28: the window stopped at every start).</summary>
    public class DamagedSettings : Fixture
    {
        static void Zero(string name) => File.WriteAllBytes(Store.PathOf(name), new byte[64]);

        static void Write(string name, string json) => File.WriteAllText(Store.PathOf(name), json);

        [Fact]
        public void A_zeroed_file_takes_its_bak_and_is_kept_aside()
        {
            Store.Save("gui.json", new Dictionary<string, object> { ["lang"] = "zh-CN" });
            Store.Save("gui.json", new Dictionary<string, object> { ["lang"] = "en" });   // the first is now gui.json.bak
            Zero("gui.json");
            Assert.Equal("zh-CN", Json.Str(Store.Load("gui.json"), "lang"));
            Assert.Equal(64, new FileInfo(Store.PathOf("gui.json.broken")).Length);
            Assert.Equal("zh-CN", Json.Str(Store.ReadJson(Store.PathOf("gui.json")) as Dictionary<string, object>, "lang"));
            var warn = LoaderLogs.Read(LoaderLogs.Of("manager").First()).Single(e => e.Level == "warn");
            Assert.Contains("gui.json was damaged", warn.Text);
            Assert.DoesNotContain("\0", warn.Text);
        }

        [Fact]
        public void Window_preferences_with_no_bak_start_empty()
        {
            Zero("gui.json");
            Assert.Empty(Store.Load("gui.json"));
            Assert.False(File.Exists(Store.PathOf("gui.json")));
            Assert.True(File.Exists(Store.PathOf("gui.json.broken")));
            Assert.True(PluginFiles.Pinned("HD2QD.License.dll"));       // the start that stopped the window
        }

        [Fact]
        public void A_damaged_bak_counts_as_none()
        {
            Zero("gui.json");
            Zero("gui.json.bak");
            Assert.Empty(Store.Load("gui.json"));
        }

        [Fact]
        public void A_record_of_the_players_mods_with_no_bak_stops_with_its_name()
        {
            Zero("deployed.json");
            var e = Assert.Throws<ModError>(() => Store.Load("deployed.json"));
            Assert.Contains("deployed.json", e.Message);
            Assert.DoesNotContain("\0", e.Message);
            Assert.Equal(64, new FileInfo(Store.PathOf("deployed.json")).Length);   // left as it was
            Assert.False(File.Exists(Store.PathOf("deployed.json.broken")));
        }

        [Fact]
        public void A_record_of_the_players_mods_takes_its_bak()
        {
            Write("deployed.json.bak", "{\"files\": {}}");
            Zero("deployed.json");
            Assert.True(Store.Load("deployed.json").ContainsKey("files"));
        }

        [Fact]
        public void Saving_leaves_no_temporary_file()
        {
            Store.Save("gui.json", new Dictionary<string, object> { ["lang"] = "en" });
            Store.Save("gui.json", new Dictionary<string, object> { ["lang"] = "ja" });
            Assert.False(File.Exists(Store.PathOf("gui.json.tmp")));
            Assert.Equal("ja", Json.Str(Store.Load("gui.json"), "lang"));
        }
    }
}
