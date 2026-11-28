using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Qd.Sample
{
    /// <summary>A plugin made for the manager's tests: every call the manager makes, answered in
    /// the plainest way, so the tests see what the manager does with the answers.</summary>
    /// <summary>An exception type the manager does not know.</summary>
    public sealed class SampleError : Exception { public SampleError(string m) : base(m) { } }

    public static class Plugin
    {
        public const int Api = 2;
        public const string Kind = "window";

        /// <summary>The key that brings its window up over the game (PluginKeys), as an author may
        /// write it.</summary>
        public const string OverlayKey = "Shift + Page Down";

        /// <summary>Its words, one table per language tag; anything else is English.</summary>
        static readonly Dictionary<string, Dictionary<string, string>> Words = new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string> { ["title"] = "Sample plugin", ["summary"] = "A plugin for the tests.", ["note"] = "Sampled", ["tag"] = "SAMPLE", ["tag_tip"] = "Holds a sample.", ["locked"] = "Sample: not unlocked" },
            ["zh-CN"] = new Dictionary<string, string> { ["title"] = "示例插件", ["summary"] = "测试用插件。", ["note"] = "已采样", ["tag"] = "示例", ["tag_tip"] = "含有示例。", ["locked"] = "示例：未解锁" },
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

        /// <summary>A mod with sample_tags.txt holds a sample: one tag, given twice (the manager
        /// keeps one), and one without text (left out). sample_throw.txt makes it fail.</summary>
        public static List<Dictionary<string, object>> ModTags(string modFolder, Dictionary<string, object> host)
        {
            if (File.Exists(Path.Combine(modFolder, "sample_throw.txt"))) throw new InvalidOperationException("asked to fail");
            if (!File.Exists(Path.Combine(modFolder, "sample_tags.txt"))) return null;
            var lang = host["lang"] as string;
            var tag = new Dictionary<string, object> { ["text"] = Say(lang, "tag"), ["tip"] = Say(lang, "tag_tip") };
            return new List<Dictionary<string, object>> { tag, tag, new Dictionary<string, object> { ["tip"] = "no text" } };
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
            ["why"] = "no sample game", ["lua"] = "luajit",
            // the loaders the sample game runs: sample_loaders.txt (comma-separated) when it is there, else any
            ["loaders"] = File.Exists(Path.Combine((string)host["home"], "sample_loaders.txt"))
                ? File.ReadAllText(Path.Combine((string)host["home"], "sample_loaders.txt")).Split(',').Cast<object>().ToList() : null,
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
            Refuse(host);
            var lines = Lines(host);
            File.WriteAllLines(Deployed(host), lines);
            return new Dictionary<string, object> { ["said"] = "sample: " + lines.Count + " mods", ["refused"] = new List<object>() };
        }

        /// <summary>The sample game refuses, with an exception of the plugin's own (as HellForge
        /// while a session runs), when the settings folder holds sample_refuse.txt.</summary>
        static void Refuse(Dictionary<string, object> host)
        {
            if (File.Exists(Path.Combine((string)host["home"], "sample_refuse.txt"))) throw new SampleError("a session is running");
        }

        /// <summary>A folder holding sample_claim.txt is the sample game's own kind of mod.</summary>
        public static bool TargetClaims(string folder, Dictionary<string, object> host) => File.Exists(Path.Combine(folder, "sample_claim.txt"));

        public static string TargetPurge(Dictionary<string, object> host)
        {
            Refuse(host);
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

        // ---------------------------------------------------------------- protected mods (large asset protection)

        /// <summary>A mod folder with sample_protected.txt ("deploy" or "session" in it) is the
        /// sample's protected mod; its plain\ folder stands for what decrypting gives. Each call is
        /// a line in the settings folder's sample_modsource.txt; with sample_reenter.txt there, it
        /// first asks the host for the mods (as a plugin may), which plans again.</summary>
        public static Dictionary<string, object> ModSource(string modFolder, Dictionary<string, object> host)
        {
            var mark = Path.Combine(modFolder, "sample_protected.txt");
            if (!File.Exists(mark)) return null;
            var home = (string)host["home"];
            File.AppendAllText(Path.Combine(home, "sample_modsource.txt"), Path.GetFileName(modFolder) + "\n");
            if (File.Exists(Path.Combine(home, "sample_reenter.txt"))) ((Func<List<Dictionary<string, object>>>)host["mods"])();
            var mode = File.ReadAllText(mark).Trim();
            var folder = StageOf(modFolder, host);
            if (folder != null && File.Exists(Path.Combine(folder, ".ready")))
                return new Dictionary<string, object> { ["folder"] = folder, ["mode"] = mode };
            return new Dictionary<string, object> { ["folder"] = "", ["mode"] = mode, ["why"] = Say(host["lang"] as string, "locked") };
        }

        static string StageOf(string modFolder, Dictionary<string, object> host) =>
            host["stage"] is string stage ? Path.Combine(stage, Path.GetFileName(modFolder)) : null;

        /// <summary>Each protected mod's plain\ copied into its stage folder, unless it is there
        /// already (never written again); a session mod only for a session. Each call is a line in
        /// sample_prepared.txt; with sample_locked.txt in the settings folder nothing is unlocked.</summary>
        public static bool PrepareDeploy(Dictionary<string, object> host)
        {
            var home = (string)host["home"];
            bool session = host.TryGetValue("for_session", out var fs) && fs is bool b && b;
            File.AppendAllText(Path.Combine(home, "sample_prepared.txt"), (session ? "session" : "deploy") + "\n");
            if (File.Exists(Path.Combine(home, "sample_locked.txt"))) return false;
            foreach (var path in Protected(host))
            {
                var mark = Path.Combine(path, "sample_protected.txt");
                if (File.ReadAllText(mark).Trim() == "session" && !session) continue;
                var to = StageOf(path, host);
                if (to == null || File.Exists(Path.Combine(to, ".ready"))) continue;
                Copy(Path.Combine(path, "plain"), to);
                File.WriteAllText(Path.Combine(to, ".ready"), "1");
            }
            return true;
        }

        /// <summary>The session mods' stage folders deleted; a line in sample_ended.txt.</summary>
        public static void EndSession(Dictionary<string, object> host)
        {
            File.AppendAllText(Path.Combine((string)host["home"], "sample_ended.txt"), "1\n");
            foreach (var path in Protected(host))
                if (File.ReadAllText(Path.Combine(path, "sample_protected.txt")).Trim() == "session" && StageOf(path, host) is string to && Directory.Exists(to))
                    Directory.Delete(to, true);
        }

        static IEnumerable<string> Protected(Dictionary<string, object> host) =>
            ((Func<List<Dictionary<string, object>>>)host["library"])().Select(m => (string)m["path"])
                .Where(p => File.Exists(Path.Combine(p, "sample_protected.txt"))).ToList();

        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
        }

        /// <summary>Leaves a file in the settings folder, and says it changed something.</summary>
        public static bool Start(Dictionary<string, object> host)
        {
            File.WriteAllText(Path.Combine((string)host["home"], "sample_started.txt"), "1");
            return true;
        }
    }
}
