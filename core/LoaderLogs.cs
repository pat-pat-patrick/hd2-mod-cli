using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>The Lua loaders' own logs, for the Lua loaders page (the user, 2026-11-01: show
    /// the logs, with levels, to follow the important lines). Where each loader writes (seen on a
    /// real install, 2026-11-01):
    /// - Boot Mod Loader: %APPDATA%\Arrowhead\Helldivers2\boot_mod_loader.log, "HH:MM:SS text",
    ///   a session from "== boot-mod-loader-";
    /// - Chef Shared Loader: chef_loader.log there, "HH:MM:SS   0.0 s | text", a session from
    ///   "==== Chef Shared Loader";
    /// - Bingus Shared Loader: %LOCALAPPDATA%\CowboyBingus\Helldivers2\Logs\BingusSharedLoader.log,
    ///   one session, no times;
    /// - MDL3: %LOCALAPPDATA%\MDL3\Logs\MDL3.log ("YYYY-MM-DD HH:MM:SS TAG ...", a stack traceback
    ///   under an error, a session from "BOOT version="), MDL\Helldivers2\Logs\MDL.log and
    ///   CowboyBingus\Helldivers2\Logs\MDL-Boot.log (one session each).
    /// None of them writes a level, so the level is read from the words (Level): an assumption,
    /// shown as such.</summary>
    public static class LoaderLogs
    {
        public class Source
        {
            /// <summary>The loader ("bml", "chef", "bsl", "mdl3", "uml"), the file's name and its path.</summary>
            public string Kind, Name, Path;
            /// <summary>How a session starts in it; null: the file is one session.</summary>
            public Regex Session;
            public bool Exists => File.Exists(Path);
        }

        public class Entry
        {
            /// <summary>The time as the log writes it ("" when it writes none), the level
            /// ("error", "warn", "info", "debug") and the text, with the lines that belong to it.</summary>
            public string Time, Level, Text;
            /// <summary>Which session from the end: 0 the last one.</summary>
            public int Session;
            /// <summary>The log wrote the level itself (else it is read from the words).</summary>
            public bool Written;
        }

        /// <summary>Tests only: the folders the logs are in.</summary>
        public static string RoamingOverride, LocalOverride;

        static string Roaming => RoamingOverride ?? LuaMods.GameFolder;
        static string Local => LocalOverride ?? Mdl3Native.LocalAppData();
        // MDL3's own: under %ProgramData% when it moved there (Mdl3Native.Root)
        static string Mdl3Home => LocalOverride ?? Mdl3Native.Root();

        /// <summary>The logs a loader of this kind writes, there or not.</summary>
        public static List<Source> Of(string kind)
        {
            var bingus = System.IO.Path.Combine(Local, "CowboyBingus", "Helldivers2", "Logs");
            switch (kind)
            {
                case "bml": return new List<Source> { new Source { Kind = kind, Name = "boot_mod_loader.log", Path = System.IO.Path.Combine(Roaming, "boot_mod_loader.log"), Session = new Regex(@"^\d\d:\d\d:\d\d == boot-mod-loader-") } };
                case "chef": return new List<Source> { new Source { Kind = kind, Name = "chef_loader.log", Path = System.IO.Path.Combine(Roaming, "chef_loader.log"), Session = new Regex(@"\| ==== Chef Shared Loader") } };
                case "bsl": return new List<Source> { new Source { Kind = kind, Name = "BingusSharedLoader.log", Path = System.IO.Path.Combine(bingus, "BingusSharedLoader.log") } };
                case "mdl3":
                    bingus = System.IO.Path.Combine(Mdl3Home, "CowboyBingus", "Helldivers2", "Logs");
                    return new List<Source>
                    {
                        new Source { Kind = kind, Name = "MDL3.log", Path = System.IO.Path.Combine(Mdl3Home, "MDL3", "Logs", "MDL3.log"), Session = new Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d BOOT ") },
                        new Source { Kind = kind, Name = "MDL.log", Path = System.IO.Path.Combine(Mdl3Home, "MDL", "Helldivers2", "Logs", "MDL.log") },
                        new Source { Kind = kind, Name = "MDL-Boot.log", Path = System.IO.Path.Combine(bingus, "MDL-Boot.log") },
                    };
                // Universal Mod Loader: the first of its places it can write (its boot, v2.0.3)
                case "uml":
                    return new List<Source>
                    {
                        new Source { Kind = kind, Name = "UniversalModLoader.log", Path = System.IO.Path.Combine(Roaming, "UniversalModLoader.log"), Session = new Regex(@"^--- Universal Mod Loader v\S+ Booted at ") },
                        new Source { Kind = kind, Name = @"Logs\UniversalModLoader.log", Path = System.IO.Path.Combine(Local,"UniversalModLoader", "Logs", "UniversalModLoader.log"), Session = new Regex(@"^--- Universal Mod Loader v\S+ Booted at ") },
                    };
                // the manager's own (ManagerLog): a session from each start of the window
                case "manager":
                    return new List<Source>
                    {
                        new Source { Kind = kind, Name = ManagerLog.File, Path = ManagerLog.PathOf, Session = new Regex(@"^\S+ \S+ INFO  \[window\] window started") },
                        new Source { Kind = kind, Name = ManagerLog.Previous, Path = ManagerLog.PreviousPath },
                    };
                default: return new List<Source>();
            }
        }

        /// <summary>The most of a log read: its end.</summary>
        public const int Tail = 1 << 20;

        static readonly Regex Stamp = new Regex(@"^(?:(\d{4}-\d\d-\d\d) )?(\d\d:\d\d:\d\d)\s+(?:\d+\.\d+ s \| )?");

        /// <summary>A log's entries from its end (Tail), oldest first: a line that starts with
        /// white space or "stack traceback" belongs to the entry before it.</summary>
        public static List<Entry> Read(Source s)
        {
            var text = ReadTail(s.Path, Tail);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var entries = new List<Entry>();
            var starts = new List<int>();
            foreach (var line in lines)
            {
                if (line.Length == 0) continue;
                bool more = entries.Count > 0 && (char.IsWhiteSpace(line[0]) || line.StartsWith("stack traceback", StringComparison.Ordinal));
                if (more)
                {
                    var last = entries[entries.Count - 1];
                    last.Text += "\n" + line;
                    var lv = Level(line);
                    if (!last.Written && Rank(lv) < Rank(last.Level) && lv != "debug") last.Level = lv;
                    continue;
                }
                if (s.Session != null && s.Session.IsMatch(line)) starts.Add(entries.Count);
                var m = Stamp.Match(line);
                var body = m.Success ? line.Substring(m.Length) : line;
                // a level the log writes itself (the manager's own) is taken as written
                var said = Written.Match(body);
                if (said.Success) body = body.Substring(said.Length);
                entries.Add(new Entry
                {
                    Time = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value + " " : "") + m.Groups[2].Value : "",
                    Level = said.Success ? said.Groups[1].Value.ToLowerInvariant() : Level(body), Text = body, Written = said.Success,
                });
            }
            // sessions counted from the end: 0 is the last one
            for (int i = 0; i < entries.Count; i++)
                entries[i].Session = s.Session == null ? 0 : starts.Count(x => x > i);
            return entries;
        }

        static readonly Regex Written = new Regex(@"^(ERROR|WARN|INFO|DEBUG)\s+");

        static int Rank(string level) => level == "error" ? 0 : level == "warn" ? 1 : level == "info" ? 2 : 3;

        static readonly Regex Error = new Regex(
            @"(?-i:(?:\b|_)FAILED\b)|(?<!\b0 )\bfailed\b|\berror\b|\bE_[A-Z_]{3,}\b|does not compile|cannot run|stack traceback|\bexception\b|attempt to (?:index|call|compare|perform|concatenate)|\bcrash", RegexOptions.IgnoreCase);
        static readonly Regex Warn = new Regex(
            @"\bwarn(?:ing)?\b|NOT THIS GAME VERSION|two copies|\bskipped\b|\bmissing\b|\bunavailable\b|over .{0,40}limit|too big|timed out|\bdeprecated\b|stays until restart|\brefus", RegexOptions.IgnoreCase);
        static readonly Regex Debug = new Regex(
            @"^(?:gc:|status:|ESC_UI |world \d+:|screen \d|changes:|handle_epoch|heap )|^\s+changes:", RegexOptions.IgnoreCase);

        /// <summary>A line's level from its words, since no loader writes one (assumed).</summary>
        public static string Level(string text)
        {
            if (Error.IsMatch(text)) return "error";
            if (Warn.IsMatch(text)) return "warn";
            if (Debug.IsMatch(text)) return "debug";
            return "info";
        }

        static string ReadTail(string path, int max)
        {
            try
            {
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long from = Math.Max(0, f.Length - max);
                    f.Seek(from, SeekOrigin.Begin);
                    var text = new StreamReader(f, Encoding.UTF8).ReadToEnd();
                    if (from > 0) { int nl = text.IndexOf('\n'); text = nl >= 0 ? text.Substring(nl + 1) : text; }
                    return text;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return ""; }
        }
    }
}
