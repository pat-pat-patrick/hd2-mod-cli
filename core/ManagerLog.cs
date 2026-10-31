using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Hd2Mod
{
    /// <summary>The manager's own log (the user, 2026-11-01), hd2qd.log in the settings folder,
    /// for the window, the command line and the problem report alike: what was run and how it
    /// ended. One entry is "YYYY-MM-DD HH:MM:SS LEVEL [where] text", its further lines indented;
    /// the level is written, not guessed (ERROR, WARN, INFO, DEBUG). Past Max the log becomes
    /// hd2qd.previous.log and a new one starts. Writing it never stops what is logged.</summary>
    public static class ManagerLog
    {
        public const string File = "hd2qd.log", Previous = "hd2qd.previous.log";
        public const long Max = 2 << 20;

        /// <summary>Who writes: "cli" or "window".</summary>
        public static string Where = "cli";

        static readonly object gate = new object();

        public static string PathOf => System.IO.Path.Combine(Store.Home, File);
        public static string PreviousPath => System.IO.Path.Combine(Store.Home, Previous);

        public static void Error(string text) => Write("ERROR", text);
        public static void Warn(string text) => Write("WARN", text);
        public static void Info(string text) => Write("INFO", text);
        public static void Debug(string text) => Write("DEBUG", text);

        public static void Write(string level, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                // only beside settings that are there: a log alone would make the folder
                if (!Directory.Exists(Store.Home)) return;
                var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(' ').Append(level.PadRight(5)).Append(" [").Append(Where).Append("] ").Append(lines[0]).Append("\r\n");
                foreach (var l in lines.Skip(1)) sb.Append("    ").Append(l).Append("\r\n");
                lock (gate)
                {
                    var path = PathOf;
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > Max)
                    {
                        System.IO.File.Copy(path, PreviousPath, true);
                        System.IO.File.Delete(path);
                    }
                    using (var f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    using (var w = new StreamWriter(f, new UTF8Encoding(false)))
                        w.Write(sb.ToString());
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>A command line as it would be typed.</summary>
        public static string Line(string[] argv) =>
            "hd2qd " + string.Join(" ", argv.Select(a => a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\""));

        /// <summary>A writer that also keeps what passes through it, for the log.</summary>
        public class Tee : TextWriter
        {
            readonly TextWriter to;
            public readonly StringBuilder Seen = new StringBuilder();
            public Tee(TextWriter to) { this.to = to; }
            public override Encoding Encoding => to.Encoding;
            public override void Write(char value) { to.Write(value); Seen.Append(value); }
            public override void Write(string value) { to.Write(value); Seen.Append(value); }
        }

        /// <summary>The most of a command's output kept in the log.</summary>
        public const int OutputLines = 60;

        /// <summary>A command's output, at DEBUG, its last lines when it is long.</summary>
        public static void Output(string text)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0) return;
            if (lines.Count > OutputLines)
                lines = new[] { "[" + (lines.Count - OutputLines) + " lines before left out]" }.Concat(lines.Skip(lines.Count - OutputLines)).ToList();
            Debug("output:\n" + string.Join("\n", lines));
        }
    }
}
