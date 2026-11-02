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
        public const int Api = 1;
        public const string Kind = "window";

        public static string Title(bool zh) => zh ? "示例插件" : "Sample plugin";

        public static string Summary(bool zh) => zh ? "测试用插件。" : "A plugin for the tests.";

        public static void Open(object owner, Dictionary<string, object> host) { }

        /// <summary>A word on the row of a mod with sample_note.txt; a mod with sample_throw.txt
        /// makes it fail, which must not break the row.</summary>
        public static Dictionary<string, object> ModNote(string modFolder, Dictionary<string, object> host)
        {
            if (File.Exists(Path.Combine(modFolder, "sample_throw.txt"))) throw new InvalidOperationException("asked to fail");
            return File.Exists(Path.Combine(modFolder, "sample_note.txt"))
                ? new Dictionary<string, object> { ["text"] = "Sampled", ["text_zh"] = "已采样", ["colour"] = "Blue" }
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

        /// <summary>Leaves a file in the settings folder, and says it changed something.</summary>
        public static bool Start(Dictionary<string, object> host)
        {
            File.WriteAllText(Path.Combine((string)host["home"], "sample_started.txt"), "1");
            return true;
        }
    }
}
