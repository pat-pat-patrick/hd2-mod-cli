using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Qd.Sample
{
    /// <summary>A plugin made for the manager's tests: every call the manager makes, answered in
    /// the plainest way, so the tests see what the manager does with the answers.</summary>
    public static class Plugin
    {
        public const int Api = 2;
        public const string Kind = "window";

        /// <summary>Its words, one table per language tag; anything else is English.</summary>
        static readonly Dictionary<string, Dictionary<string, string>> Words = new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string> { ["title"] = "Sample plugin", ["summary"] = "A plugin for the tests.", ["note"] = "Sampled" },
            ["zh-CN"] = new Dictionary<string, string> { ["title"] = "示例插件", ["summary"] = "测试用插件。", ["note"] = "已采样" },
        };

        static string Say(string lang, string key) =>
            lang != null && Words.TryGetValue(lang, out var w) && w.TryGetValue(key, out var s) ? s : Words["en"][key];

        public static string Title(string lang) => Say(lang, "title");

        public static string Summary(string lang) => Say(lang, "summary");

        public static void Open(object owner, Dictionary<string, object> host) { }

        /// <summary>A word on the row of a mod with sample_note.txt; a mod with sample_throw.txt
        /// makes it fail, which must not break the row.</summary>
        public static Dictionary<string, object> ModNote(string modFolder, Dictionary<string, object> host)
        {
            if (File.Exists(Path.Combine(modFolder, "sample_throw.txt"))) throw new InvalidOperationException("asked to fail");
            return File.Exists(Path.Combine(modFolder, "sample_note.txt"))
                ? new Dictionary<string, object> { ["text"] = Say(host["lang"] as string, "note"), ["colour"] = "Blue" }
                : null;
        }

        /// <summary>sample-echo, and list, a name the manager has itself (left out).</summary>
        public static readonly string[][] Commands =
        {
            new[] { "sample-echo", "prints its arguments" },
            new[] { "list", "a name the manager has" },
        };

        public static int Run(string command, string[] args, TextWriter o, Dictionary<string, object> host)
        {
            if (args.Contains("--fail")) throw new InvalidOperationException("asked to fail");
            o.WriteLine("echo " + string.Join(" ", args) + " in " + host["home"]);
            return 0;
        }

        // ---------------------------------------------------------------- a deploy target ("sample")

        /// <summary>A deploy target, found unless the settings folder holds sample_missing.txt.</summary>
        public static Dictionary<string, object> Target(Dictionary<string, object> host) => new Dictionary<string, object>
        {
            ["id"] = "sample", ["title"] = "Sample game", ["found"] = !File.Exists(Path.Combine((string)host["home"], "sample_missing.txt")),
            ["why"] = "no sample game",
        };

        static string Deployed(Dictionary<string, object> host) => Path.Combine((string)host["home"], "sample_target.txt");

        static List<string> Lines(Dictionary<string, object> host) =>
            ((Func<List<Dictionary<string, object>>>)host["mods"])().Select(m => m["id"] + " " + m["name"] + ": " +
                string.Join(",", ((List<object>)m["files"]).Cast<Dictionary<string, object>>().Select(f => f["name"]))).ToList();

        /// <summary>Pending: the list in use differs from what was deployed.</summary>
        public static Dictionary<string, object> TargetStatus(Dictionary<string, object> host) => new Dictionary<string, object>
        {
            ["pending"] = File.Exists(Deployed(host)) && File.ReadAllLines(Deployed(host)).SequenceEqual(Lines(host)) ? 0L : 1L,
            ["running"] = false,
        };

        /// <summary>Writes the list in use, one mod a line, into sample_target.txt.</summary>
        public static Dictionary<string, object> TargetDeploy(Dictionary<string, object> host)
        {
            var lines = Lines(host);
            File.WriteAllLines(Deployed(host), lines);
            return new Dictionary<string, object> { ["said"] = "sample: " + lines.Count + " mods", ["refused"] = new List<object>() };
        }

        /// <summary>A folder holding sample_claim.txt is the sample game's own kind of mod.</summary>
        public static bool TargetClaims(string folder, Dictionary<string, object> host) => File.Exists(Path.Combine(folder, "sample_claim.txt"));

        public static string TargetPurge(Dictionary<string, object> host)
        {
            File.Delete(Deployed(host));
            return "sample: purged";
        }

        /// <summary>An icon read at run time: sample_icon.ico in the settings folder when it is
        /// there (as the HellForge plugin reads HellForge's own), else none, and the manager draws
        /// the app.ico this plugin ships.</summary>
        public static byte[] Icon(Dictionary<string, object> host)
        {
            var f = Path.Combine((string)host["home"], "sample_icon.ico");
            return File.Exists(f) ? File.ReadAllBytes(f) : null;
        }

        /// <summary>Leaves a file in the settings folder, and says it changed something.</summary>
        public static bool Start(Dictionary<string, object> host)
        {
            File.WriteAllText(Path.Combine((string)host["home"], "sample_started.txt"), "1");
            return true;
        }
    }
}
