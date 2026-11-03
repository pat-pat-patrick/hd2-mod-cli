using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Hd2Mod
{
    /// <summary>An official plugin, as the official list names it (hd2qd-plugins, plugins.json):
    /// what the Plugins page shows of a plugin not added, where its releases are, and the file a
    /// mod ships when it needs it.</summary>
    public class OfficialPlugin
    {
        /// <summary>Title and Summary are English, the list's own; TitleIn and SummaryIn give a language's.</summary>
        public string File, Title, Summary, Author, Repository, Asset, WantedBy;
        public int Api;
        public List<(string Version, string Tag, string Sha256)> Versions = new List<(string, string, string)>();

        /// <summary>The newest version released, else null (not released yet).</summary>
        public (string Version, string Tag, string Sha256)? Latest =>
            Versions.Count == 0 ? ((string, string, string)?)null
            : Versions.OrderBy(v => v.Version, Comparer<string>.Create((a, b) => SelfUpdate.Compare(SelfUpdate.Numbers(a) ?? new int[3], SelfUpdate.Numbers(b) ?? new int[3]))).Last();

        /// <summary>In lang (a language tag, "zh-CN"), else English.</summary>
        public string TitleIn(string lang) => PluginFiles.Translated(lang, File, "title") ?? Title;
        public string SummaryIn(string lang) => PluginFiles.Translated(lang, File, "summary") ?? Summary;
    }

    public static partial class PluginFiles
    {
        /// <summary>The official list (hd2qd-plugins' plugins.json), read from its repository, so
        /// that a plugin added to it shows without a new copy of this program (the user,
        /// 2026-11-03: "列表不能内置"). HD2QD_PLUGIN_LIST for tests.</summary>
        public static string ListUrl = Environment.GetEnvironmentVariable("HD2QD_PLUGIN_LIST")
            ?? "https://raw.githubusercontent.com/pat-pat-patrick/hd2qd-plugins/main/plugins.json";

        /// <summary>The list as last read, kept in the settings folder: shown at once, and while
        /// GitHub cannot be reached.</summary>
        public static string ListCache => Store.PathOf("official_plugins.json");

        /// <summary>A language's translations of the list (locales/<lang>.json beside it, the
        /// user, 2026-11-03: one field per language would not scale): for each plugin file, its
        /// title and summary. English is the list itself.</summary>
        public static string LocaleUrl(string lang) => ListUrl.Substring(0, ListUrl.LastIndexOf('/') + 1) + "locales/" + lang + ".json";

        public static string LocaleCache(string lang) => Store.PathOf("official_plugins." + lang + ".json");

        /// <summary>The list's text: the kept copy, else null (never read yet). Set by tests.</summary>
        public static Func<string> OfficialJson = () => Kept(ListCache);

        /// <summary>A language's translations, the kept copy, else null. Set by tests.</summary>
        public static Func<string, string> OfficialLocaleJson = lang => Kept(LocaleCache(lang));

        static readonly Dictionary<string, (DateTime Time, string Text)> keptRead = new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A kept copy, read again only when it changed: every mod row asks (Notes).</summary>
        static string Kept(string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            if (!keptRead.TryGetValue(fi.FullName, out var r) || r.Time != fi.LastWriteTimeUtc)
                keptRead[fi.FullName] = r = (fi.LastWriteTimeUtc, System.IO.File.ReadAllText(fi.FullName));
            return r.Text;
        }

        static void Keep(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path + ".tmp", text);
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.File.Move(path + ".tmp", path);
        }

        static string Why(ModError e) => e.Kind == "offline" || e.Kind == "missing" || e.Kind == "busy" ? e.Kind : "error";

        static bool IsObject(string text, string member)
        {
            try { return Json.Parse(text) is Dictionary<string, object> d && (member == null || d.TryGetValue(member, out var l) && l is List<object>); }
            catch (FormatException) { return false; }
        }

        /// <summary>The list read again from its repository and kept, with the translations of
        /// lang (a language tag, "zh-CN"; null or "en": none): whether either changed, and why the
        /// list could not be read: "offline", "missing" (not published), "busy", "error",
        /// "not_list"; then the kept copy stays. A language with no translations is shown in English.</summary>
        public static (bool Changed, string Why) RefreshList(string lang = null)
        {
            string text;
            if (string.IsNullOrEmpty(ListUrl)) return (false, "offline");     // tests: no network
            try { text = Updates.Get(ListUrl); }
            catch (ModError e) { return (false, Why(e)); }
            if (!IsObject(text, "plugins")) return (false, "not_list");
            bool changed = Kept(ListCache) != text;
            Keep(ListCache, text);
            if (lang != null && lang != "en")
                try
                {
                    var t = Updates.Get(LocaleUrl(lang));
                    if (IsObject(t, null))
                    {
                        changed |= Kept(LocaleCache(lang)) != t;
                        Keep(LocaleCache(lang), t);
                    }
                }
                catch (ModError e) when (e.Kind == "missing")
                {
                    // not translated (any more): English
                    changed |= System.IO.File.Exists(LocaleCache(lang));
                    System.IO.File.Delete(LocaleCache(lang));
                }
                catch (ModError) { }                   // offline now: the kept translations stay
            return (changed, null);
        }

        static readonly Dictionary<string, (string Json, Dictionary<string, object> Map)> localeRead = new Dictionary<string, (string, Dictionary<string, object>)>();

        /// <summary>A plugin's field (title, summary) in lang, else null (not translated).</summary>
        public static string Translated(string lang, string file, string field)
        {
            if (lang == null || lang == "en" || file == null) return null;
            var json = OfficialLocaleJson(lang);
            if (json == null) return null;
            if (!localeRead.TryGetValue(lang, out var r) || !ReferenceEquals(r.Json, json))
            {
                Dictionary<string, object> map;
                try { map = Json.Parse(json) as Dictionary<string, object>; }
                catch (FormatException) { map = null; }
                localeRead[lang] = r = (json, map);
            }
            return r.Map != null && r.Map.TryGetValue(file, out var e) && e is Dictionary<string, object> d ? Json.Str(d, field) : null;
        }

        /// <summary>Where a release's files are downloaded from; HD2QD_GITHUB_DOWNLOADS for tests.</summary>
        public static string Downloads = Environment.GetEnvironmentVariable("HD2QD_GITHUB_DOWNLOADS") ?? "https://github.com";

        static (string Json, List<OfficialPlugin> List) officialRead;

        /// <summary>The official plugins this program can use (those of its Api), in the list's order.</summary>
        public static List<OfficialPlugin> Official()
        {
            // read once for each text: every mod row asks (Notes)
            var json = OfficialJson();
            if (json != null && ReferenceEquals(json, officialRead.Json)) return officialRead.List;
            var out_ = new List<OfficialPlugin>();
            officialRead = (json, out_);
            Dictionary<string, object> d;
            try { d = json == null ? null : Json.Parse(json) as Dictionary<string, object>; }
            catch (FormatException) { d = null; }
            if (d == null) return out_;
            foreach (var p in Json.List(d, "plugins").OfType<Dictionary<string, object>>())
            {
                var o = new OfficialPlugin
                {
                    File = Json.Str(p, "file"), Title = Json.Str(p, "title"),
                    Summary = Json.Str(p, "summary"), Author = Json.Str(p, "author"),
                    Repository = Json.Str(p, "repository"), Asset = Json.Str(p, "asset"), WantedBy = Json.Str(p, "wanted_by_file"),
                    Api = (int)Json.Long(p, "api"),
                };
                foreach (var v in Json.List(p, "versions").OfType<Dictionary<string, object>>())
                    if (Json.Str(v, "version") != null && Json.Str(v, "tag") != null && Json.Str(v, "sha256") != null)
                        o.Versions.Add((Json.Str(v, "version"), Json.Str(v, "tag"), Json.Str(v, "sha256").ToLowerInvariant()));
                if (o.File != null && Name.IsMatch(o.File) && o.Api == Api) out_.Add(o);
            }
            return out_;
        }

        /// <summary>The official plugins not added, each with the library's mods that need it
        /// (they ship its wanted_by_file): those first, the rest in the list's order.</summary>
        public static List<(OfficialPlugin Plugin, List<string> Mods)> Available(Library lib)
        {
            var added = new HashSet<string>(All().Select(p => p.File), StringComparer.OrdinalIgnoreCase);
            return Official().Where(p => !added.Contains(p.File))
                .Select(p => (p, p.WantedBy == null ? new List<string>()
                    : lib.Mods.Where(m => Directory.Exists(m.Path) && System.IO.File.Exists(Path.Combine(m.Path, p.WantedBy))).Select(m => m.Name).ToList()))
                .OrderBy(x => x.Item2.Count > 0 ? 0 : 1)
                .ToList();
        }

        /// <summary>An official plugin by its file name (HD2QD.Repatch.dll, or Repatch), else null.</summary>
        public static OfficialPlugin OfficialNamed(string name) =>
            Official().FirstOrDefault(p => string.Equals(p.File, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.File, "HD2QD." + name + ".dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.File, name + ".dll", StringComparison.OrdinalIgnoreCase));

        /// <summary>The newest release of an official plugin, downloaded from its repository into a
        /// folder of its own under the temporary folder and checked against the list's SHA-256:
        /// the file's path, named as the plugin, ready for Inspect and Install. Nothing of it runs.</summary>
        public static string Fetch(OfficialPlugin p)
        {
            var v = p.Latest ?? throw new ModError(p.File + " has not been released yet", "plugin_unreleased", p.File);
            var url = Downloads.TrimEnd('/') + "/" + p.Repository + "/releases/download/" + Uri.EscapeDataString(v.Tag) + "/" + Uri.EscapeDataString(p.Asset ?? p.File);
            var bytes = Updates.GetBytes(url);
            string sha;
            using (var h = SHA256.Create()) sha = BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (sha != v.Sha256)
                throw new ModError("the download of " + p.File + " " + v.Version + " is not the file the official list names (SHA-256 " + sha + ")", "plugin_sha", p.File);
            var dir = Path.Combine(Path.GetTempPath(), "hd2qd-plugin-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, p.File);
            System.IO.File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}
