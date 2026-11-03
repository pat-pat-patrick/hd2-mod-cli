using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Hd2Mod;

namespace QuickDeploy
{
    /// <summary>The window's own words, one file per language (Lang\<tag>.json): the language
    /// chosen in the window, else Windows's, else English.</summary>
    public static partial class Texts
    {

        /// <summary>Whether the window shows the lines that explain things. Off for a player who
        /// knows the program (the user, 2026-10-24: "一看就知道，不需要看这些东西").</summary>
        public static bool Hints = LoadHints();

        static bool LoadHints()
        {
            try { return Json.Bool(Store.Load("gui.json"), "hints", true); }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.FormatException || e is System.UnauthorizedAccessException) { return true; }
        }

        /// <summary>The explaining lines shown or hidden, kept for the next start.</summary>
        public static void ShowHints(bool on)
        {
            Hints = on;
            try
            {
                var d = Store.Load("gui.json");
                d["hints"] = on;
                Store.Save("gui.json", d);
            }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.UnauthorizedAccessException) { }
        }

        /// <summary>Whether a fast wheel on the list shows a jump button by the pointer (the user,
        /// 2026-10-26: a setting, on by default).</summary>
        public static bool FlingButton = LoadFlag("fling_button", true);

        static bool LoadFlag(string key, bool otherwise)
        {
            try { return Json.Bool(Store.Load("gui.json"), key, otherwise); }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.FormatException || e is System.UnauthorizedAccessException) { return otherwise; }
        }

        /// <summary>The jump button turned on or off, kept for the next start.</summary>
        public static void ShowFlingButton(bool on)
        {
            FlingButton = on;
            try
            {
                var d = Store.Load("gui.json");
                d["fling_button"] = on;
                Store.Save("gui.json", d);
            }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.UnauthorizedAccessException) { }
        }

        /// <summary>Whether the list is shown with the mod that wins at its top. Only the way the
        /// list is drawn: the load order, what wins and the numbers stay as they are (the user,
        /// 2026-10-25, after HD2 Arsenal's "将第一个模组设置为最高优先级").</summary>
        public static bool TopWins = LoadTopWins();

        static bool LoadTopWins()
        {
            try { return Json.Bool(Store.Load("gui.json"), "top_wins", false); }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.FormatException || e is System.UnauthorizedAccessException) { return false; }
        }

        /// <summary>The list's direction chosen, kept for the next start.</summary>
        public static void ShowTopWins(bool on)
        {
            TopWins = on;
            try
            {
                var d = Store.Load("gui.json");
                d["top_wins"] = on;
                Store.Save("gui.json", d);
            }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.UnauthorizedAccessException) { }
        }

        /// <summary>The window's words, one file per language, embedded: Lang\<tag>.json (and our
        /// own builds' words in private\gui\Lang\<tag>.json). Adding a language is adding a file
        /// (the user, 2026-11-03: never a pair of English and Chinese). Each file names its language
        /// in "_language". A word missing in a language is the English one.</summary>
        static readonly Dictionary<string, Dictionary<string, string>> ByTag = Load();

        static Dictionary<string, Dictionary<string, string>> Load()
        {
            var asm = typeof(Texts).Assembly;
            var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            // the public files first, then our own builds' (they only add words)
            foreach (var name in asm.GetManifestResourceNames().OrderBy(n => n.StartsWith("Ours.") ? 1 : 0))
            {
                var bare = name.StartsWith("Ours.") ? name.Substring(5) : name;
                if (!bare.StartsWith("Lang.") || !bare.EndsWith(".json")) continue;
                var tag = bare.Substring(5, bare.Length - 10);
                using (var r = new StreamReader(asm.GetManifestResourceStream(name), Encoding.UTF8))
                {
                    if (!(Json.Parse(r.ReadToEnd()) is Dictionary<string, object> d)) continue;
                    if (!all.TryGetValue(tag, out var t)) all[tag] = t = new Dictionary<string, string>();
                    foreach (var kv in d) if (kv.Value is string s) t[kv.Key] = s;
                }
            }
            return all;
        }

        /// <summary>The window's language, a language tag ("en", "zh-CN"): the window's own setting
        /// (gui.json "lang"; the user, 2026-10-24: not the settings window's), else Windows's.</summary>
        public static string Lang = Language();

        /// <summary>The languages the window has: tag and name; English last, as the picker always showed it (中文 | English), like HD2 Mod Setting GUI.</summary>
        public static List<(string Tag, string Name)> Languages =>
            ByTag.Where(kv => kv.Value.ContainsKey("_language")).OrderBy(kv => kv.Key == "en" ? 1 : 0).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                 .Select(kv => (kv.Key, kv.Value["_language"])).ToList();

        /// <summary>Of the languages the window has, the one for a tag: itself, else one of the
        /// same language (zh-TW: zh-CN), else English.</summary>
        public static string Nearest(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "en";
            if (ByTag.ContainsKey(tag)) return ByTag.Keys.First(k => string.Equals(k, tag, StringComparison.OrdinalIgnoreCase));
            var language = tag.Split('-')[0];
            return ByTag.Keys.Where(k => string.Equals(k.Split('-')[0], language, StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault() ?? "en";
        }

        static string Language()
        {
            try
            {
                var s = Json.Str(Store.Load("gui.json"), "lang");
                if (!string.IsNullOrEmpty(s)) return Nearest(s);           // "zh" from before 2026-11-03: zh-CN
            }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.FormatException || e is System.UnauthorizedAccessException) { }
            return Nearest(CultureInfo.CurrentUICulture.Name);
        }

        /// <summary>The language chosen in the window, kept for the next start.</summary>
        public static void Choose(string tag)
        {
            Lang = Nearest(tag);
            try
            {
                var d = Store.Load("gui.json");
                d["lang"] = Lang;
                Store.Save("gui.json", d);
            }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.FormatException || e is System.UnauthorizedAccessException) { }
        }

        static Dictionary<string, string> Words(string tag) => ByTag.TryGetValue(tag, out var t) ? t : new Dictionary<string, string>();

        public static bool Has(string key) => Words("en").ContainsKey(key) || Words(Lang).ContainsKey(key);

        public static string Get(string key) =>
            Words(Lang).TryGetValue(key, out var s) ? s : Words("en").TryGetValue(key, out var e) ? e : key;

        /// <summary>A text with its values. A count of one ({0} is 1) takes the key's "_1" text
        /// where the language that has the key has one: "1 file", not "1 files".</summary>
        public static string F(string key, params object[] args)
        {
            var t = Words(Lang).ContainsKey(key) ? Words(Lang) : Words("en");
            if (args.Length > 0 && Convert.ToString(args[0], CultureInfo.InvariantCulture) == "1" && t.ContainsKey(key + "_1"))
                key += "_1";
            return string.Format(t.TryGetValue(key, out var s) ? s : Get(key), args);
        }


        /// <summary>A size as the design writes it: 0 B, 812 KB, 3.4 MB, 2.84 GB.</summary>
        public static string Size(long b)
        {
            if (b <= 0) return "0 B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString(b < 10 * 1024 ? "0.0" : "0", CultureInfo.InvariantCulture) + " KB";
            double mb = b / 1048576.0;
            if (mb < 1000) return mb.ToString(mb < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB";
            return (mb / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        public static string N(int n) => n.ToString("#,0", CultureInfo.InvariantCulture);

        /// <summary>Between the items of a list in running text, as the language writes it (hard-coded
        /// 、 showed in the English window, 2026-11-01).</summary>
        public static string Sep => Get("sep_list");
    }
}
