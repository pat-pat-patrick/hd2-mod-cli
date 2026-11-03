using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>The window's words are one file per language (gui\QuickDeploy\Lang\&lt;tag&gt;.json;
    /// the user, 2026-11-03: never English-and-Chinese pairs). English has every word the code
    /// asks for by name; another language has only words English has, and names itself.</summary>
    public class LanguageFiles
    {
        /// <summary>The repository: the first folder up from the tests' output that has the window's
        /// language files (the output's depth differs between builds).</summary>
        static readonly string Repo = FindRepo();

        static string FindRepo()
        {
            for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, @"gui\QuickDeploy\Lang"))) return d.FullName;
            throw new DirectoryNotFoundException(@"no gui\QuickDeploy\Lang above " + AppDomain.CurrentDomain.BaseDirectory);
        }

        static Dictionary<string, object> Read(string path) => (Dictionary<string, object>)Json.Parse(File.ReadAllText(path));

        static Dictionary<string, object> English()
        {
            var en = Read(Path.Combine(Repo, @"gui\QuickDeploy\Lang\en.json"));
            var ours = Path.Combine(Repo, @"private\gui\Lang\en.json");                 // our own builds' words, when there
            if (File.Exists(ours)) foreach (var kv in Read(ours)) en[kv.Key] = kv.Value;
            return en;
        }

        [Fact]
        public void Every_word_the_window_asks_for_by_name_is_in_English()
        {
            var en = English();
            var asked = new SortedSet<string>(StringComparer.Ordinal);
            var sources = Directory.GetFiles(Path.Combine(Repo, @"gui\QuickDeploy"), "*.cs")
                .Concat(Directory.Exists(Path.Combine(Repo, @"private\gui")) ? Directory.GetFiles(Path.Combine(Repo, @"private\gui"), "*.cs") : new string[0]);
            foreach (var f in sources)
                foreach (Match m in Regex.Matches(File.ReadAllText(f), @"Texts\.(?:Get|F|Has)\(\s*""([A-Za-z0-9_]+)""\s*[,)]"))
                    asked.Add(m.Groups[1].Value);
            Assert.True(asked.Count > 300, "only " + asked.Count + " words found: the pattern is wrong");
            Assert.Equal("", string.Join(" ", asked.Where(k => !en.ContainsKey(k))));
        }

        [Fact]
        public void Each_language_names_itself_and_has_only_words_English_has()
        {
            var dir = Path.Combine(Repo, @"gui\QuickDeploy\Lang");
            var en = Read(Path.Combine(dir, "en.json"));
            var files = Directory.GetFiles(dir, "*.json");
            Assert.Contains(files, f => Path.GetFileName(f) == "zh-CN.json");
            foreach (var f in files)
            {
                var t = Read(f);
                Assert.True(t.ContainsKey("_language"), Path.GetFileName(f) + " has no _language");
                Assert.Equal("", string.Join(" ", t.Keys.Where(k => !en.ContainsKey(k) && !(k.EndsWith("_1") && en.ContainsKey(k.Substring(0, k.Length - 2))))));
            }
        }
    }
}
