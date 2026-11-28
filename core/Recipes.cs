using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>Install plans (the user, 2026-11-28: "在查找框里输入 super holo HUD，然后开始启动这一
    /// 系列的流程"): a mod that needs several others, a plugin and a sign-in, set up in one go from
    /// its name. The author's list (recipes.json, on the author's copy beside the official plugins,
    /// Mirror) names each plan's steps: an official plugin, or a mod known by its manifest Guid,
    /// taken from its GitHub releases (the newest file of that Guid, GitHubUpdates) or, when the
    /// list names one, the author's copy of a given file, checked against its SHA-256; a mod with
    /// no source is got by hand from the pages named. A step that is done is left alone: a mod the
    /// library already has is only turned on, its options set, and the loader chosen. After a
    /// game, the mod's own log says whether it ran (Checks), and what stopped it.</summary>
    public static class Recipes
    {
        public class Page
        {
            public Dictionary<string, string> Label = new Dictionary<string, string>();
            public string Url;
            /// <summary>Shown only in these languages (language tags); empty: in every one.</summary>
            public List<string> Langs = new List<string>();
        }

        /// <summary>Whether a page is shown in this language. Nothing guesses where a member
        /// sponsored (the user, 2026-11-28: a time zone is no guide; the member package itself
        /// says 爱发电 or Patreon): a member chooses the page of the platform they pay on, and only
        /// the language decides what is offered (English never shows 爱发电, Langs).</summary>
        public static bool Shown(Page p, string lang) => p.Langs.Count == 0 || p.Langs.Contains(lang);

        /// <summary>A file of the author's copy, as the list names it: a release file of the step's
        /// repository, at Url (the homepage's static files, or the author's R2 bucket for a file too
        /// large for those: the user, 2026-11-28), else at its place in the Mirror.</summary>
        public class MirrorFile
        {
            public string Tag, Asset, Sha256, Url;
            public long Size;
        }

        public class Step
        {
            public Dictionary<string, string> Name = new Dictionary<string, string>(), Note = new Dictionary<string, string>();
            /// <summary>An official plugin, by its file (HD2QD.License.dll).</summary>
            public string Plugin;
            /// <summary>A mod: its manifest Guid, and the repository (owner/repo) whose releases carry
            /// it; null for a mod got by hand from Pages.</summary>
            public string Guid, GitHub;
            /// <summary>Chosen as the loader in effect (Loaders).</summary>
            public bool Loader;
            /// <summary>Not needed for the mod to run (shown, not done by Install all).</summary>
            public bool Optional;
            /// <summary>The mod the plan is for: once the player adds it (a member package says by
            /// itself which platform it is from, so nothing has to guess: the user, 2026-11-28), the
            /// window offers the plan for the rest (Unfinished).</summary>
            public bool Main;
            /// <summary>The mod's options that must be on, by name.</summary>
            public List<string> Options = new List<string>();
            public List<Page> Pages = new List<Page>();
            public MirrorFile Mirror;
            /// <summary>The oldest version of the mod that will do (null: any): an older one in the
            /// library is "old", and replaced by a newer release.</summary>
            public string Version;
            /// <summary>The mod on the other sites (most mods are not on GitHub: the user,
            /// 2026-11-28): its number on Nexus Mods (0: none), GameBanana, ModWorkshop (null:
            /// none). Without a Guid, the library's mod of that page is this one.</summary>
            public long Nexus;
            public string Banana, Workshop;
            /// <summary>Which of a page's files: one whose name or label holds this (Key); null when
            /// the page has one file only.</summary>
            public string File;
            /// <summary>Named on the mod's Nexus Mods page, not by its author's manifest
            /// (NexusNeeds): shown with its page, never downloaded by Install all.</summary>
            public bool Hint;
        }

        /// <summary>The step's sites in the order they are tried, each with the mod's page there.</summary>
        public static List<(string Site, string Page)> SitesOf(Step s)
        {
            var o = new List<(string, string)>();
            if (s.GitHub != null && s.Guid != null) o.Add(("GitHub", "https://github.com/" + s.GitHub + "/releases"));
            if (s.Banana != null) o.Add((Sites.Banana, Sites.PageOf(Sites.Banana, s.Banana)));
            if (s.Workshop != null) o.Add((Sites.Workshop, Sites.PageOf(Sites.Workshop, s.Workshop)));
            if (s.Nexus > 0) o.Add(("Nexus Mods", Hd2Mod.Nexus.Page(new NexusRef { Game = Hd2Mod.Nexus.Game, Mod = s.Nexus }) + "?tab=files"));
            return o;
        }

        /// <summary>The sites this program downloads the step from by itself, here and now: GitHub,
        /// GameBanana, ModWorkshop always (a ModWorkshop author may still have turned mod managers
        /// off: then its page), Nexus Mods only for a premium account (NexusDirect).</summary>
        public static List<string> AutoSites(Step s) =>
            s.Hint ? new List<string>() : SitesOf(s).Select(x => x.Site).Where(x => x != "Nexus Mods" || NexusDirect()).ToList();

        /// <summary>Whether Nexus Mods hands this program its files without the site's button: an
        /// account signed in that is premium. Asked of what is kept here only, never online.</summary>
        public static Func<bool> NexusDirect = () =>
        {
#if HAS_NEXUS
            try
            {
                if (NexusOAuth.SignedIn) return NexusOAuth.Account().Premium;
                return Hd2Mod.Nexus.Credential() != null;      // only our own builds have another
            }
            catch (ModError) { return false; }
#else
            return false;
#endif
        };

        /// <summary>A line of a mod's log: "start" when the mod began a game, "problem" when
        /// something stopped it, with what to say; a problem that a later Clear line follows (the
        /// mod found it after all: a hub that connects late, files that arrive) is not one.</summary>
        public class Check
        {
            public string Log, Find, Kind, Clear;
            public Dictionary<string, string> Say = new Dictionary<string, string>();
        }

        public class Recipe
        {
            public string Id, Target;
            public List<string> Names = new List<string>();
            public Dictionary<string, string> Title = new Dictionary<string, string>(), Summary = new Dictionary<string, string>();
            public List<Step> Steps = new List<Step>();
            public List<Check> Checks = new List<Check>();
        }

        /// <summary>Words in lang (a language tag), else in its base language, else English, else any.</summary>
        public static string In(Dictionary<string, string> words, string lang)
        {
            if (words == null || words.Count == 0) return null;
            if (lang != null && words.TryGetValue(lang, out var w)) return w;
            var head = lang?.Split('-')[0];
            if (head != null && words.TryGetValue(head, out w)) return w;
            if (words.TryGetValue("en", out w)) return w;
            return words.Values.First();
        }

        // ------------------------------------------------------------ the list

        public const string FileName = "recipes.json";

        /// <summary>Where the list is read: the author's copy (Mirror); HD2QD_RECIPES (a file or an
        /// address) for tests.</summary>
        public static string Source => Environment.GetEnvironmentVariable("HD2QD_RECIPES") is string s && s.Length > 0 ? s
                                     : Mirror.On ? Mirror.Url(FileName) : null;

        /// <summary>The list's text, set by tests: then nothing is read or kept.</summary>
        public static string TestText;

        /// <summary>The list as last read (kept in the settings folder), else empty.</summary>
        public static List<Recipe> Kept()
        {
            if (TestText != null) return Parse(TestText);
            var p = Store.PathOf(FileName);
            try { return File.Exists(p) ? Parse(File.ReadAllText(p, Encoding.UTF8)) : new List<Recipe>(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { return new List<Recipe>(); }
        }

        /// <summary>The list read again and kept; why not: "offline", "not_list" (the kept copy stays).</summary>
        public static string Refresh()
        {
            if (TestText != null) return null;
            var from = Source;
            if (from == null) return "offline";
            string text;
            try { text = File.Exists(from) ? File.ReadAllText(from, Encoding.UTF8) : Updates.Get(from, TimeSpan.FromSeconds(15)); }
            catch (ModError e) { return e.Kind ?? "offline"; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException) { return "offline"; }
            try { if (Parse(text).Count == 0) return "not_list"; }
            catch (FormatException) { return "not_list"; }
            try
            {
                Directory.CreateDirectory(Store.Home);
                File.WriteAllText(Store.PathOf(FileName), text, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return null;
        }

        static Dictionary<string, string> Words(object o)
        {
            var d = new Dictionary<string, string>();
            if (o is string s) d["en"] = s;
            else if (o is Dictionary<string, object> m) foreach (var kv in m) if (kv.Value is string v) d[kv.Key] = v;
            return d;
        }

        static object Get(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v : null;

        static List<string> Strings(object o) => (o as List<object> ?? new List<object>()).OfType<string>().ToList();

        /// <summary>A mod's number on a site, written as a number or as digits; null otherwise.</summary>
        static string Digits(object o)
        {
            if (o == null || o is bool) return null;
            var t = Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture).Trim();
            return Regex.IsMatch(t, @"^[1-9]\d{0,11}$") ? t : null;
        }

        static readonly Regex GuidShape = new Regex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
        static readonly Regex RepoShape = new Regex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$");
        static readonly Regex Sha256Shape = new Regex(@"^[0-9a-f]{64}$");

        /// <summary>The list's plans. A plan or step of a shape this program does not know is left
        /// out (a newer list may carry more); text that is no JSON object is a FormatException.</summary>
        public static List<Recipe> Parse(string text)
        {
            var root = Json.Parse(text) as Dictionary<string, object> ?? throw new FormatException("not a JSON object");
            var list = new List<Recipe>();
            foreach (var o in Json.List(root, "recipes").OfType<Dictionary<string, object>>())
            {
                var r = new Recipe
                {
                    Id = Json.Str(o, "id"), Target = Json.Str(o, "target"),
                    Names = Strings(Get(o, "names")), Title = Words(Get(o, "title")), Summary = Words(Get(o, "summary")),
                };
                if (string.IsNullOrEmpty(r.Id) || r.Title.Count == 0) continue;
                bool ok = true;
                foreach (var so in Json.List(o, "steps").OfType<Dictionary<string, object>>())
                {
                    var s = new Step
                    {
                        Name = Words(Get(so, "name")), Note = Words(Get(so, "note")),
                        Plugin = Json.Str(so, "plugin"), Guid = Json.Str(so, "guid"), GitHub = Json.Str(so, "github"),
                        Loader = Json.Bool(so, "loader"), Optional = Json.Bool(so, "optional"), Main = Json.Bool(so, "main"), Options = Strings(Get(so, "options")), Version = Json.Str(so, "version"),
                        Nexus = Json.Long(so, "nexus") > 0 ? Json.Long(so, "nexus") : 0, Banana = Digits(Get(so, "gamebanana")), Workshop = Digits(Get(so, "modworkshop")), File = Json.Str(so, "file"),
                    };
                    foreach (var po in Json.List(so, "pages").OfType<Dictionary<string, object>>())
                        if (Json.Str(po, "url") is string u && u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            s.Pages.Add(new Page { Url = u, Label = Words(Get(po, "label")), Langs = Strings(Get(po, "langs")) });
                    if (Get(so, "mirror") is Dictionary<string, object> mo)
                    {
                        var m = new MirrorFile { Tag = Json.Str(mo, "tag"), Asset = Json.Str(mo, "asset"), Sha256 = (Json.Str(mo, "sha256") ?? "").ToLowerInvariant(), Size = Json.Long(mo, "size"),
                                                Url = Json.Str(mo, "url") is string mu && mu.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? mu : null };
                        if (!string.IsNullOrEmpty(m.Tag) && !string.IsNullOrEmpty(m.Asset) && Sha256Shape.IsMatch(m.Sha256)) s.Mirror = m;
                    }
                    if (s.Guid != null && !GuidShape.IsMatch(s.Guid)) { ok = false; break; }
                    bool plugin = !string.IsNullOrEmpty(s.Plugin), mod = s.Guid != null || s.Nexus > 0 || s.Banana != null || s.Workshop != null;
                    if (plugin == mod || s.Name.Count == 0 || (s.GitHub != null && (!RepoShape.IsMatch(s.GitHub) || s.Guid == null)) || (s.Mirror != null && s.GitHub == null)) { ok = false; break; }
                    r.Steps.Add(s);
                }
                foreach (var co in Json.List(o, "checks").OfType<Dictionary<string, object>>())
                {
                    var c = new Check { Log = Json.Str(co, "log"), Find = Json.Str(co, "find"), Kind = Json.Str(co, "kind"), Clear = Json.Str(co, "clear"), Say = Words(Get(co, "say")) };
                    if (string.IsNullOrEmpty(c.Log) || string.IsNullOrEmpty(c.Find) || (c.Kind != "start" && c.Kind != "problem")) continue;
                    if (c.Log.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || c.Log.Contains("..")) continue;      // a file of the game's log folder only
                    r.Checks.Add(c);
                }
                if (ok && r.Steps.Count > 0) list.Add(r);
            }
            return list;
        }

        /// <summary>What is compared of a name: lower case, without spaces, dashes, dots or underscores.</summary>
        public static string Key(string s) => Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"[\s\-_.·]+", "");

        /// <summary>The plan a typed name names (its id or one of its names, Key-equal), else null.</summary>
        public static Recipe Find(IEnumerable<Recipe> list, string typed)
        {
            var k = Key(typed);
            if (k.Length == 0) return null;
            return list.FirstOrDefault(r => Key(r.Id) == k || r.Names.Any(n => Key(n) == k) || r.Title.Values.Any(t => Key(t) == k));
        }

        // ------------------------------------------------------------ where each step stands

        /// <summary>A step now: Is "done"; "missing" (not added); "old" (older than Step.Version);
        /// "off" (in the library, not on in
        /// the profile in use or an option off); "loader" (not the loader in effect); "waiting" (a
        /// protected mod its plugin has not made ready: Why says why, in the plugin's words, else
        /// WhyKey); "target" (the library deploys to another target, Recipe.Target names the one).
        /// Mod: the library's mod, when it has one.</summary>
        public class State
        {
            public Step Step;
            public string Is, Why, WhyKey;
            /// <summary>For "old": the version the library has (null: it says none).</summary>
            public string Have;
            public Mod Mod;
            public bool Done => Is == "done";
        }

        /// <summary>The library's mod of this Guid: the one in the profile in use and on first, then
        /// the last in the order.</summary>
        public static Mod ModOf(Library lib, string guid)
        {
            Mod best = null;
            int bestRank = -1;
            foreach (var m in lib.Mods)
            {
                if (!string.Equals(GuidOf(m), guid, StringComparison.OrdinalIgnoreCase)) continue;
                int rank = (Profiles.InProfile(lib, m) ? 2 : 0) + (m.Enabled ? 1 : 0);
                if (rank >= bestRank) { best = m; bestRank = rank; }
            }
            return best;
        }

        /// <summary>Whether a mod of the library is the step's: by the manifest Guid when both have
        /// one (two files of one page may be two mods); else by where it came from, the page the
        /// step names; a Nexus page's hint also by its name, since a player's copy from elsewhere
        /// carries no Nexus number.</summary>
        public static bool Is(Step s, Mod m)
        {
            if (s.Plugin != null || m == null) return false;
            if (s.Guid != null)
            {
                var g = GuidOf(m);
                if (g != null || (s.Nexus <= 0 && s.Banana == null && s.Workshop == null)) return string.Equals(g, s.Guid, StringComparison.OrdinalIgnoreCase);
            }
            if (s.Nexus > 0 && m.Nexus != null && m.Nexus.Mod == s.Nexus) return true;
            if (m.Page != null && (s.Banana != null || s.Workshop != null))
            {
                var (site, id) = Sites.Of(m.Page);
                // the page's file it names, when the download recorded which (PageGot)
                bool file = s.File == null || m.PageGot == null || (m.PageGot.Name == null && m.PageGot.Label == null)
                            || Key(m.PageGot.Name).Contains(Key(s.File)) || Key(m.PageGot.Label).Contains(Key(s.File));
                if (site == Sites.Banana && id == s.Banana && file) return true;
                if (site == Sites.Workshop && id == s.Workshop && file) return true;
            }
            if (s.Hint)
            {
                var k = Key(In(s.Name, "en"));
                if (k.Length > 0 && (Key(m.Name) == k || Key(m.RealName) == k)) return true;
            }
            return false;
        }

        /// <summary>The library's mod of this step (Is), as ModOf chooses among several.</summary>
        public static Mod ModFor(Library lib, Step s)
        {
            Mod best = null;
            int bestRank = -1;
            foreach (var m in lib.Mods)
            {
                if (!Is(s, m)) continue;
                int rank = (Profiles.InProfile(lib, m) ? 2 : 0) + (m.Enabled ? 1 : 0);
                if (rank >= bestRank) { best = m; bestRank = rank; }
            }
            return best;
        }

        /// <summary>What tells a step from another: its plugin, Guid, or page.</summary>
        public static string StepKey(Step s) =>
            s.Plugin ?? s.Guid ?? (s.Nexus > 0 ? "nexus:" + s.Nexus : s.Banana != null ? "gamebanana:" + s.Banana : s.Workshop != null ? "modworkshop:" + s.Workshop : "name:" + Key(In(s.Name, "en")))
                                + (s.Guid == null && s.File != null ? "|" + Key(s.File) : "");

        // a mod's manifest Guid, by its manifest's size and time
        static readonly Dictionary<string, (string Sig, string Guid)> guids = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        static string GuidOf(Mod m)
        {
            if (string.IsNullOrEmpty(m.Path)) return null;
            var p = Path.Combine(m.Path, "manifest.json");
            string sig;
            try { var fi = new FileInfo(p); if (!fi.Exists) return null; sig = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException) { return null; }
            lock (guids) if (guids.TryGetValue(p, out var had) && had.Sig == sig) return had.Guid;
            string g = null;
            try { g = Manifest.Read(m.Path)?.Guid; }
            catch (Exception e) when (e is ModError || e is IOException || e is UnauthorizedAccessException || e is FormatException) { }
            lock (guids) guids[p] = (sig, g);
            return g;
        }

        /// <summary>Whether a plugin of this file is added and usable.</summary>
        public static Func<string, bool> PluginAdded = file =>
            PluginFiles.All().Any(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase) && p.Type != null);

        public static List<State> States(Recipe r, Library lib) => r.Steps.Select(s => StateOf(r, s, lib)).ToList();

        public static State StateOf(Recipe r, Step s, Library lib)
        {
            var st = new State { Step = s };
            if (s.Plugin != null) { st.Is = PluginAdded(s.Plugin) ? "done" : "missing"; return st; }
            var m = st.Mod = ModFor(lib, s);
            if (m == null) { st.Is = "missing"; return st; }
            // a Nexus page's hint asks for nothing more than being there
            if (s.Hint) { st.Is = "done"; return st; }
            if (r.Target == "official" && !Targets.IsOfficial(lib)) { st.Is = "target"; return st; }
            if (s.Version != null)
            {
                var have = Origins.VersionOf(m).Version;
                // a version nothing says (a folder unpacked by hand) is taken as it is: never replaced on a guess
                if (have != null && CompareVersion(have, s.Version) < 0) { st.Is = "old"; st.Have = have; return st; }
            }
            // a loader is not switched on: the one chosen goes to the game whatever its switch
            // (Loaders); any copy of it in the library will do, chosen by name: one that is only
            // the last loader on gives way to the next one added (MDL3, a later step)
            if (s.Loader)
            {
                var chosen = Loaders.Chosen(lib);
                string kept = null;
                try { kept = Json.Str(Store.Config(), "loader"); }
                catch (Exception e) when (e is IOException || e is FormatException) { }
                bool it = chosen != null && chosen.Id == kept && Is(s, chosen);
                if (it) st.Mod = chosen;
                st.Is = it ? "done" : "loader";
                return st;
            }
            if (!Profiles.InProfile(lib, m) || (!m.Enabled && Loaders.KindOf(m) == null) || OptionsOff(m, s).Count > 0) { st.Is = "off"; return st; }
            var note = Protected.Note(lib, m);
            // a mod made for the game session only: ready, it goes in when the game is started from here
            if (note != null && Json.Str(note, "key") == "protected_session") { st.Is = "done"; st.WhyKey = "protected_session"; return st; }
            if (note != null)
            {
                st.Is = "waiting";
                st.Why = Json.Str(note, "text");
                st.WhyKey = Json.Str(note, "key");
                return st;
            }
            st.Is = "done";
            return st;
        }

        /// <summary>The steps still to do for a plan whose mod the library has and deploys (its
        /// main step's mod, on, in the profile in use, for the target in use, which is the plan's):
        /// the needed ones that are not added, not on or not the loader. Empty otherwise.</summary>
        public static List<State> Unfinished(Recipe r, Library lib)
        {
            var none = new List<State>();
            var main = r.Steps.FirstOrDefault(s => s.Main);
            if (main == null) return none;
            var m = ModFor(lib, main);
            if (m == null || !m.Enabled || !Profiles.InProfile(lib, m) || !Targets.Belongs(lib, m)) return none;
            if (r.Target == "official" && !Targets.IsOfficial(lib)) return none;
            return States(r, lib).Where(s => !s.Step.Main && !s.Step.Optional && (s.Is == "missing" || s.Is == "old" || s.Is == "off" || s.Is == "loader")).ToList();
        }

        /// <summary>Whether a step can be done here with no hand: added from a site this program
        /// downloads from (AutoSites) or the official plugins, a newer release, turned on, chosen
        /// as the loader. A Nexus page's hint never is.</summary>
        public static bool CanDo(State s) =>
            !s.Step.Hint && (s.Is == "off" || s.Is == "loader" || ((s.Is == "missing" || s.Is == "old") && (s.Step.Plugin != null || AutoSites(s.Step).Count > 0)));

        /// <summary>Whether the game may start without this step: done, optional, a hint, or a
        /// protected mod its plugin unlocks before the deploy.</summary>
        public static bool Spare(State s) => s.Done || s.Step.Optional || s.Step.Hint || s.Is == "waiting";

        /// <summary>The kinds of a refusal that mean "by hand": no site here gives the file; its
        /// page is shown instead.</summary>
        public static readonly string[] ByHand = { "recipe_manual", "recipe_choose", "recipe_no_release", "recipe_too_old" };

        /// <summary>The steps of a list done in turn until none is left that can be (each done one
        /// may add more: a dependency's own needs show once it is added), at most passes times;
        /// plan gives the list as it is now. What each one did, in order.</summary>
        public static List<string> RunAll(Func<Recipe> plan, int passes = 12)
        {
            var said = new List<string>();
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byHand = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < passes; i++)
            {
                var r = plan();
                if (r == null) break;
                var lib = Library.Load();
                var next = r.Steps.Where(s => !s.Optional && !s.Main && !byHand.Contains(StepKey(s))).Select(s => StateOf(r, s, lib)).FirstOrDefault(CanDo);
                if (next == null) break;
                var key = StepKey(next.Step) + "|" + next.Is;
                if (!tried.Add(key)) throw new ModError(In(next.Step.Name, "en") + " is still " + next.Is + " after it was done once", "recipe_stuck");
                try { said.Add(In(next.Step.Name, "en") + ": " + Run(r, next.Step)); }
                catch (ModError e) when (ByHand.Contains(e.Kind))
                {
                    // the rest go on: this one is the player's, from its page
                    byHand.Add(StepKey(next.Step));
                    said.Add(In(next.Step.Name, "en") + ": by hand (" + e.Message + ")" + string.Concat(SitesOf(next.Step).Select(x => " " + x.Page)));
                }
            }
            return said;
        }

        /// <summary>The options a step needs that are off (by name; one the mod does not have counts as off).</summary>
        static List<string> OptionsOff(Mod m, Step s)
        {
            if (s.Options.Count == 0) return new List<string>();
            List<OptionChoice> chosen;
            try { chosen = Manifest.Merged(Manifest.Of(m), m.Options); }
            catch (Exception e) when (e is ModError || e is IOException || e is UnauthorizedAccessException) { return s.Options.ToList(); }
            return s.Options.Where(n => !chosen.Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase) && c.Enabled)).ToList();
        }

        /// <summary>Two versions compared: their numbers in turn (a leading v, and words, left
        /// out), then one with a pre-release suffix (3.0.0-dev.57) before the same without it, then
        /// the suffixes' numbers. Negative when a is older.</summary>
        public static int CompareVersion(string a, string b)
        {
            (List<long> Main, bool Pre, List<long> Suffix) Of(string v)
            {
                v = (v ?? "").Trim().TrimStart('v', 'V');
                int dash = v.IndexOf('-');
                string main = dash >= 0 ? v.Substring(0, dash) : v, suffix = dash >= 0 ? v.Substring(dash + 1) : "";
                List<long> Nums(string x) => Regex.Matches(x, @"\d+").Cast<Match>().Select(m => long.TryParse(m.Value, out var n) ? n : 0).ToList();
                return (Nums(main), dash >= 0, Nums(suffix));
            }
            int Seq(List<long> x, List<long> y)
            {
                for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
                {
                    long p = i < x.Count ? x[i] : 0, q = i < y.Count ? y[i] : 0;
                    if (p != q) return p < q ? -1 : 1;
                }
                return 0;
            }
            var (am, ap, asx) = Of(a);
            var (bm, bp, bsx) = Of(b);
            int c = Seq(am, bm);
            if (c != 0) return c;
            if (ap != bp) return ap ? -1 : 1;
            return Seq(asx, bsx);
        }

        // ------------------------------------------------------------ a mod's own dependencies

        /// <summary>The install list of a mod of the library: what its manifest's "Requires" names
        /// (and, for those already added, what theirs name, first), then the mod itself (the main
        /// step), with the author's list lending its words, mirror files and log checks to the
        /// same Guids; the author's list's plan for the mod when its manifest names nothing (a
        /// release from before Requires). Null when there is neither.</summary>
        public static Recipe PlanFor(Library lib, Mod main, List<Recipe> list = null)
        {
            list = list ?? Kept();
            var guid = GuidOf(main);
            var listed = guid == null ? null : list.FirstOrDefault(x => x.Steps.Any(s => s.Main && string.Equals(s.Guid, guid, StringComparison.OrdinalIgnoreCase)));
            Manifest man = null;
            try { man = string.IsNullOrEmpty(main.Path) ? null : Manifest.Read(main.Path); }
            catch (Exception e) when (e is ModError || e is IOException || e is UnauthorizedAccessException) { }
            if (man == null || man.Requires.Count == 0) return listed ?? FromNexusPage(main, guid);
            if (guid == null && main.Nexus == null) return listed;      // nothing would know the mod again

            var mainKey = guid != null ? "mod:" + guid.ToLowerInvariant() : main.Nexus != null ? "nexus:" + main.Nexus.Mod : "id:" + main.Id;
            var r = new Recipe
            {
                Id = mainKey, Title = new Dictionary<string, string> { ["en"] = main.Name },
                Summary = listed?.Summary ?? new Dictionary<string, string>(), Checks = listed?.Checks ?? new List<Check>(),
                Target = listed?.Target ?? (man.Targets != null && man.Targets.Count == 1 && man.Targets[0] == "official" ? "official" : null),
            };
            var byKey = new Dictionary<string, Step>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { guid ?? mainKey };
            void Add(Manifest of)
            {
                foreach (var q in of.Requires)
                {
                    var step = FromRequirement(q, list);
                    if (step == null || Is(step, main)) continue;    // the mod itself, named back
                    var key = StepKey(step);
                    if (byKey.TryGetValue(key, out var had))
                    {
                        // named twice: the newer of the versions asked for, every option either needs
                        if (step.Version != null && (had.Version == null || CompareVersion(step.Version, had.Version) > 0)) had.Version = step.Version;
                        foreach (var o in step.Options) if (!had.Options.Contains(o, StringComparer.OrdinalIgnoreCase)) had.Options.Add(o);
                        had.Loader |= step.Loader;
                        had.Optional &= step.Optional;
                        continue;
                    }
                    if (step.Plugin == null && visiting.Add(step.Guid ?? key))
                    {
                        // one already added: what it needs comes before it (a cycle is cut where it closes)
                        var dm = ModFor(lib, step);
                        Manifest dman = null;
                        try { dman = dm == null || string.IsNullOrEmpty(dm.Path) ? null : Manifest.Read(dm.Path); }
                        catch (Exception e) when (e is ModError || e is IOException || e is UnauthorizedAccessException) { }
                        if (dman != null) Add(dman);
                    }
                    if (byKey.ContainsKey(key)) continue;
                    byKey[key] = step;
                    r.Steps.Add(step);
                }
            }
            Add(man);
            var mainStep = listed?.Steps.FirstOrDefault(s => s.Main);
            r.Steps.Add(new Step
            {
                Name = mainStep?.Name ?? new Dictionary<string, string> { ["en"] = main.Name }, Note = mainStep?.Note ?? new Dictionary<string, string>(),
                Guid = guid, Main = true, Options = mainStep?.Options ?? new List<string>(), Nexus = guid == null ? main.Nexus?.Mod ?? 0 : 0,
            });
            return r;
        }

        static readonly Regex PluginFile = new Regex(@"^HD2QD\.[A-Za-z0-9_.-]+\.dll$", RegexOptions.IgnoreCase);

        /// <summary>A manifest's requirement as a step; null when its shape is not one this program
        /// takes (both or neither of a mod and a plugin, a repository not owner/repo). The author's
        /// list lends the words, pages and mirror file it has for the same Guid or plugin; the mirror
        /// file only for the same repository.</summary>
        static Step FromRequirement(Requirement q, List<Recipe> list)
        {
            if (q.Guid != null && !GuidShape.IsMatch(q.Guid)) return null;
            bool plugin = !string.IsNullOrEmpty(q.Plugin), mod = q.Guid != null || q.Nexus > 0 || q.GameBanana != null || q.ModWorkshop != null;
            if (plugin == mod) return null;
            if (plugin && !PluginFile.IsMatch(q.Plugin)) return null;
            if (q.GitHub != null && (!RepoShape.IsMatch(q.GitHub) || q.Guid == null)) return null;
            var known = list.SelectMany(x => x.Steps).FirstOrDefault(x => !x.Main && (plugin
                            ? string.Equals(x.Plugin, q.Plugin, StringComparison.OrdinalIgnoreCase)
                            : q.Guid != null && string.Equals(x.Guid, q.Guid, StringComparison.OrdinalIgnoreCase)));
            var s = new Step
            {
                Name = known?.Name ?? new Dictionary<string, string> { ["en"] = q.Name ?? q.Plugin ?? q.Guid ?? ("Nexus Mods " + q.Nexus) },
                Note = known?.Note ?? new Dictionary<string, string>(),
                Plugin = plugin ? q.Plugin : null, Guid = q.Guid,
                GitHub = q.GitHub ?? (q.Guid != null ? known?.GitHub : null),
                Nexus = q.Nexus > 0 ? q.Nexus : known?.Nexus ?? 0, Banana = q.GameBanana ?? known?.Banana, Workshop = q.ModWorkshop ?? known?.Workshop, File = q.File ?? known?.File,
                Loader = q.Loader, Optional = q.Optional, Version = q.Version,
                Options = q.Options.ToList(), Pages = known?.Pages.ToList() ?? new List<Page>(),
            };
            // a page the author names: the way to a mod no site here gives
            if (q.Page != null && !s.Pages.Any(p => p.Url == q.Page)) s.Pages.Add(new Page { Url = q.Page });
            if (known?.Mirror != null && string.Equals(known.GitHub, s.GitHub, StringComparison.OrdinalIgnoreCase)) s.Mirror = known.Mirror;
            return s;
        }

        /// <summary>Every install list a mod of the library has (PlanFor), for the mods that are on
        /// in the profile in use and for the target in use.</summary>
        public static List<Recipe> Plans(Library lib, List<Recipe> list = null)
        {
            list = list ?? Kept();
            var out_ = new List<Recipe>();
            foreach (var m in lib.Mods)
            {
                if (!m.Enabled || !Profiles.InProfile(lib, m) || !Targets.Belongs(lib, m)) continue;
                var p = PlanFor(lib, m, list);
                if (p != null && !out_.Any(x => x.Id == p.Id)) out_.Add(p);
            }
            return out_;
        }

        /// <summary>The mods of the library whose install lists need this one (not as an optional
        /// step): on and in the profile in use only when onOnly. Their names, each once.</summary>
        public static List<Mod> NeededBy(Library lib, Mod m, bool onOnly = true, List<Recipe> list = null)
        {
            var out_ = new List<Mod>();
            list = list ?? Kept();
            foreach (var x in lib.Mods)
            {
                if (x.Id == m.Id || (onOnly && (!x.Enabled || !Profiles.InProfile(lib, x)))) continue;
                var p = PlanFor(lib, x, list);
                // a Nexus page's hint holds nothing back: what a page lists is not always so
                if (p != null && p.Steps.Any(s => !s.Main && !s.Optional && !s.Hint && Is(s, m)))
                    out_.Add(x);
            }
            return out_;
        }

        /// <summary>The mods an install list pulled in (InstalledFor) that nothing in the library
        /// needs now, on or off: offered for removal once the mod they came for is gone. Never a
        /// loader: mods from before Requires use one without saying so.</summary>
        public static List<Mod> Orphans(Library lib, List<Recipe> list = null)
        {
            list = list ?? Kept();
            return lib.Mods.Where(m => m.InstalledFor.Count > 0 && Loaders.KindOf(m) == null && NeededBy(lib, m, false, list).Count == 0).ToList();
        }

        // ------------------------------------------------------------ what a Nexus Mods page lists

        // Most mods are on Nexus Mods and say nothing in their manifest (the user, 2026-11-28: "加读
        // Nexus 页面上的 Requirements"). Their pages' Requirements are read without a key, kept here,
        // and shown as hints: with their pages, never downloaded, holding nothing back. Of the
        // 1000 most downloaded Helldivers 2 mods (2026-11-28) 292 list some, nearly all of them a
        // mod manager (HD2ModManager 143 times, HD2 Arsenal 136): those are left out; outside
        // addresses were mostly jokes ("Reading Comprehension") or tools (ReShade): only a page of
        // a mod site this program knows is kept.

        /// <summary>A row of a page's Requirements: another Helldivers 2 mod on Nexus Mods (Mod),
        /// or a page elsewhere (Url).</summary>
        public class Need
        {
            public long Mod;
            public string Name, Notes, Url;
        }

        /// <summary>The mod managers pages name as a requirement: HD2ModManager, HD2 Arsenal.</summary>
        public static readonly long[] Managers = { 109, 4664 };

        public const string NeedsFile = "nexus_requirements.json";
        static (string Path, string Sig, Dictionary<string, object> Data) needsRead = (null, null, null);
        static readonly object needsLock = new object();
        const long HelldiversOnNexus = 6119;

        const string NeedsQuery = "query Needs($ids: [CompositeDomainWithIdInput!]!) { legacyModsByDomain(ids: $ids, count: 50) { nodes { modId " +
                                  "modRequirements { nexusRequirements { nodes { modId modName url notes externalRequirement gameId } } } } } }";

        static long Num(object o) =>
            o is string t ? (long.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0)
            : o != null && !(o is bool) && !(o is List<object>) && !(o is Dictionary<string, object>) ? Convert.ToInt64(o, System.Globalization.CultureInfo.InvariantCulture) : 0;

        /// <summary>What a mod's Nexus Mods page lists, as last read; null when never read.</summary>
        public static List<Need> NeedsKept(long mod)
        {
            // read again only when the file changed: every plan of the library asks (NeededBy)
            Dictionary<string, object> d;
            try
            {
                var fi = new FileInfo(Store.PathOf(NeedsFile));
                var sig = fi.Exists ? fi.Length + ":" + fi.LastWriteTimeUtc.Ticks : "-";
                lock (needsLock)
                {
                    if (needsRead.Sig != sig || needsRead.Path != fi.FullName) needsRead = (fi.FullName, sig, fi.Exists ? Store.Load(NeedsFile) : new Dictionary<string, object>());
                    d = needsRead.Data;
                }
            }
            catch (Exception e) when (e is IOException || e is ModError || e is FormatException || e is UnauthorizedAccessException) { return null; }
            var e1 = Json.Obj(d, mod.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (e1 == null) return null;
            return Json.List(e1, "needs").OfType<Dictionary<string, object>>()
                       .Select(x => new Need { Mod = Num(Get(x, "mod")), Name = Json.Str(x, "name"), Notes = Json.Str(x, "notes"), Url = Json.Str(x, "url") }).ToList();
        }

        /// <summary>The pages' Requirements read again for these Nexus Mods mods, those read longer
        /// than maxAge ago (a day) or never, forty to a request, and kept. How many were read.</summary>
        public static int RefreshNeeds(IEnumerable<long> mods, TimeSpan? maxAge = null)
        {
            var age = (long)(maxAge ?? TimeSpan.FromDays(1)).TotalSeconds;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> d;
            try { d = Store.Load(NeedsFile); }
            catch (Exception e) when (e is ModError || e is IOException) { d = new Dictionary<string, object>(); }      // only a record of pages: read again
            var due = mods.Where(m => m > 0).Distinct().Where(m => !(Json.Obj(d, m.ToString(System.Globalization.CultureInfo.InvariantCulture)) is Dictionary<string, object> e && now - Num(Get(e, "read")) < age)).ToList();
            int n = 0;
            for (int i = 0; i < due.Count; i += 40)
            {
                var part = due.Skip(i).Take(40).ToList();
                Dictionary<string, object> data;
                try
                {
                    data = Hd2Mod.Nexus.PostGraphQL(NeedsQuery, new Dictionary<string, object>
                    {
                        ["ids"] = part.Select(m => (object)new Dictionary<string, object> { ["gameDomain"] = Hd2Mod.Nexus.Game, ["modId"] = m }).ToList(),
                    });
                }
                catch (ModError e) { ManagerLog.Warn("Nexus Mods requirements: " + e.Message); break; }
                foreach (var node in Json.List(Json.Obj(data, "legacyModsByDomain"), "nodes").OfType<Dictionary<string, object>>())
                {
                    var mod = Num(Get(node, "modId"));
                    if (!part.Contains(mod)) continue;
                    var rows = Json.List(Json.Obj(Json.Obj(node, "modRequirements"), "nexusRequirements"), "nodes").OfType<Dictionary<string, object>>();
                    d[mod.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new Dictionary<string, object>
                    {
                        ["read"] = now,
                        ["needs"] = Needs(rows, mod).Select(x => (object)new Dictionary<string, object> { ["mod"] = x.Mod, ["name"] = x.Name, ["notes"] = x.Notes, ["url"] = x.Url }).ToList(),
                    };
                    n++;
                }
            }
            if (n > 0) Store.Save(NeedsFile, d);
            return n;
        }

        /// <summary>The rows of a page's Requirements worth showing: other Helldivers 2 mods on
        /// Nexus Mods but the mod managers (Managers) and the mod itself; an outside row only when
        /// its address is a Helldivers 2 page on Nexus Mods (then its number), or a page of
        /// GitHub, GameBanana, ModWorkshop or AyakaMods. Each once.</summary>
        public static List<Need> Needs(IEnumerable<Dictionary<string, object>> rows, long self)
        {
            var out_ = new List<Need>();
            foreach (var r in rows)
            {
                var need = new Need { Name = (Json.Str(r, "modName") ?? "").Trim(), Notes = string.IsNullOrWhiteSpace(Json.Str(r, "notes")) ? null : Json.Str(r, "notes").Trim() };
                var url = (Json.Str(r, "url") ?? "").Trim();
                if (!Json.Bool(r, "externalRequirement"))
                {
                    if (Num(Get(r, "gameId")) != HelldiversOnNexus) continue;
                    need.Mod = Num(Get(r, "modId"));
                }
                else if (url.IndexOf("nexusmods.com/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { need.Mod = Hd2Mod.Nexus.ModNumber(url) ?? 0; }
                    catch (ModError) { continue; }      // another game's page
                }
                else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                         && (Sites.Of(url).Site is string site && site != Sites.Direct || Regex.IsMatch(url, @"^https://github\.com/[\w.-]+/[\w.-]+", RegexOptions.IgnoreCase)))
                    need.Url = url;
                else continue;
                if (need.Url == null && (need.Mod <= 0 || need.Mod == self || Managers.Contains(need.Mod))) continue;
                if (need.Name.Length == 0) need.Name = need.Url ?? "Nexus Mods " + need.Mod;
                if (out_.Any(x => (x.Mod > 0 && x.Mod == need.Mod) || (x.Url != null && x.Url == need.Url))) continue;
                out_.Add(need);
            }
            return out_;
        }

        /// <summary>A mod that names nothing itself, by what its Nexus Mods page lists (as last
        /// read, NeedsKept): each row a hint, then the mod. Null when it lists nothing.</summary>
        static Recipe FromNexusPage(Mod main, string guid)
        {
            if (main.Nexus == null || main.Nexus.Mod <= 0) return null;
            var needs = NeedsKept(main.Nexus.Mod);
            if (needs == null || needs.Count == 0) return null;
            var r = new Recipe { Id = "nexus:" + main.Nexus.Mod, Title = new Dictionary<string, string> { ["en"] = main.Name } };
            foreach (var n in needs)
            {
                var s = new Step { Name = new Dictionary<string, string> { ["en"] = n.Name }, Hint = true, Nexus = n.Mod };
                if (n.Notes != null) s.Note["en"] = n.Notes;
                if (n.Url != null)
                {
                    s.Pages.Add(new Page { Url = n.Url });
                    var (site, id) = Sites.Of(n.Url);
                    if (site == Sites.Banana) s.Banana = id;
                    else if (site == Sites.Workshop) s.Workshop = id;
                }
                r.Steps.Add(s);
            }
            r.Steps.Add(new Step { Name = new Dictionary<string, string> { ["en"] = main.Name }, Main = true, Guid = guid, Nexus = main.Nexus.Mod });
            return r;
        }

        // ------------------------------------------------------------ doing a step

        /// <summary>A command of this program, run here; its refusal thrown.</summary>
        static string Do(params string[] argv)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            int code = Commands.Run(argv, o, e, out var refused);
            if (refused != null) throw refused;
            if (code != 0) throw new ModError((e.ToString() + o.ToString()).Trim());
            return o.ToString().Trim();
        }

        /// <summary>A step done: an official plugin added; a mod downloaded and added, then turned on,
        /// its options set and, for the loader, chosen. Refused for a mod got by hand ("recipe_manual")
        /// or one its plugin has not made ready ("recipe_waiting"). Blocks: never on the window's thread.</summary>
        public static string Run(Recipe r, Step s, Action<long, long> progress = null) => Run(r, s, progress, null);

        /// <summary>The same; downloaded: the step's file already downloaded (Download), added and then deleted.</summary>
        public static string Run(Recipe r, Step s, Action<long, long> progress, string downloaded)
        {
            var lib = Library.Load();
            var st = StateOf(r, s, lib);
            var said = new List<string>();
            if (st.Is == "done") return In(s.Name, "en") + ": already done";
            if (st.Is == "target") throw new ModError("the library deploys to " + Targets.Current(lib) + ": switch to the official game first", "recipe_target");
            if (st.Is == "waiting") throw new ModError(st.Why ?? "its plugin has not made it ready", "recipe_waiting");
            if (s.Plugin != null)
            {
                // the official list read first when this computer never read it (or it is older than the plan)
                var official = PluginFiles.OfficialNamed(s.Plugin);
                if (official == null) { PluginFiles.RefreshList(); official = PluginFiles.OfficialNamed(s.Plugin); }
                if (official == null) throw new ModError("no official plugin " + s.Plugin, "recipe_no_plugin", s.Plugin);
                var path = PluginFiles.Fetch(official);
                try { var (file, version, _) = PluginFiles.Install(path); return "added " + file + " " + version; }
                finally { try { Directory.Delete(Path.GetDirectoryName(path), true); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { } }
            }
            if (s.Hint) throw new ModError(In(s.Name, "en") + " is named on the mod's Nexus Mods page: got by hand", "recipe_manual");
            if (st.Is == "old")
            {
                // a newer release in its place: its order, on/off and options kept (replace)
                if (AutoSites(s).Count == 0) throw new ModError(In(s.Name, "en") + " " + s.Version + " or newer is got by hand", "recipe_manual");
                var file = downloaded ?? Download(s, progress);
                try { said.Add(Do("replace", st.Mod.Id, file)); }
                finally { try { File.Delete(file); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { } }
                lib = Library.Load();
                Came(lib, new[] { st.Mod.Id }, file);
                st.Mod = ModFor(lib, s) ?? throw new ModError("the file is not " + In(s.Name, "en") + " (" + Says(s) + ")", "recipe_not_it");
            }
            if (st.Is == "missing")
            {
                if (AutoSites(s).Count == 0) throw new ModError(In(s.Name, "en") + " is got by hand", "recipe_manual");
                var file = downloaded ?? Download(s, progress);
                var before = new HashSet<string>(lib.Mods.Select(x => x.Id));
                try { said.Add(Do("add", file)); }
                finally { try { File.Delete(file); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { } }
                lib = Library.Load();
                Came(lib, lib.Mods.Where(x => !before.Contains(x.Id)).Select(x => x.Id), file);
                st.Mod = ModFor(lib, s) ?? throw new ModError("the file added is not " + In(s.Name, "en") + " (" + Says(s) + ")", "recipe_not_it");
                // pulled in for the plan's mod: offered for removal with it (Orphans)
                var forGuid = r.Steps.FirstOrDefault(x => x.Main)?.Guid;
                if (forGuid != null && !st.Mod.InstalledFor.Contains(forGuid, StringComparer.OrdinalIgnoreCase))
                {
                    st.Mod.InstalledFor.Add(forGuid);
                    lib.Save();
                }
            }
            said.AddRange(TurnOn(s, st.Mod.Id));
            return string.Join("; ", said.Where(x => x.Length > 0).Select(x => x.Split('\n')[0]));
        }

        /// <summary>A mod of the library made as the step needs: in the profile in use, on, its
        /// options on, and the loader in effect when it is one.</summary>
        static List<string> TurnOn(Step s, string id)
        {
            var said = new List<string>();
            var lib = Library.Load();
            var m = lib.Mods.First(x => x.Id == id);
            if (lib.ProfilesOn && !Profiles.InProfile(lib, m)) said.Add(Do("profile-add", id));
            lib = Library.Load();
            m = lib.Mods.First(x => x.Id == id);
            if (!m.Enabled && (!s.Loader || Loaders.KindOf(m) == null)) said.Add(Do("enable", id));
            lib = Library.Load();
            m = lib.Mods.First(x => x.Id == id);
            foreach (var name in OptionsOff(m, s)) said.Add(Do("set", id, name, "on"));
            // chosen by name, not left as the last loader on: the plan's next step may add another (MDL3)
            if (s.Loader)
            {
                string kept = null;
                try { kept = Json.Str(Store.Config(), "loader"); }
                catch (Exception e) when (e is IOException || e is FormatException) { }
                if (kept != id || Loaders.Chosen(Library.Load())?.Id != id) said.Add(Do("loader", id));
            }
            return said;
        }

        /// <summary>The step's file downloaded into the default mod folder from the first of its
        /// sites that gives it here (AutoSites, in that order): one that does not (no release of
        /// the Guid, a page with several files and no File to choose, a ModWorkshop author who
        /// turned mod managers off, Nexus Mods without its button) gives way to the next. The
        /// last one's refusal when none does; "recipe_manual" when there is none to try.</summary>
        public static string Download(Step s, Action<long, long> progress)
        {
            ModError last = null;
            foreach (var site in AutoSites(s))
            {
                try
                {
                    if (site == "GitHub") return FromGitHub(s, progress);
                    if (site == Sites.Banana || site == Sites.Workshop) return FromSite(s, site, progress);
                    if (site == "Nexus Mods") return FromNexus(s, progress);
                }
                catch (ModError e)
                {
                    ManagerLog.Warn(In(s.Name, "en") + " from " + site + ": " + e.Message);
                    last = e;
                }
            }
            throw last ?? new ModError(In(s.Name, "en") + " is got by hand", "recipe_manual");
        }

        /// <summary>What a step's mod is known by, for a refusal.</summary>
        static string Says(Step s) =>
            s.Guid != null ? "no mod of Guid " + s.Guid : "no mod from " + string.Join(", ", SitesOf(s).Select(x => x.Page));

        /// <summary>One of a page's files: the one File names (the newest of those it names), else
        /// the only one; "recipe_choose" when the player has to.</summary>
        static T Pick<T>(Step s, List<T> files, Func<T, string> name, Func<T, string> label, Func<T, long> time, string page)
        {
            var pick = files;
            if (s.File != null)
            {
                var k = Key(s.File);
                pick = files.Where(f => Key(name(f)).Contains(k) || Key(label(f)).Contains(k)).OrderByDescending(time).ToList();
                if (pick.Count == 0) throw new ModError(page + " has no file named " + s.File, "recipe_choose", page);
                return pick[0];
            }
            if (pick.Count == 1) return pick[0];
            if (pick.Count == 0) throw new ModError(page + " offers no file", "recipe_manual", page);
            throw new ModError(page + " offers " + pick.Count + " files: the player chooses", "recipe_choose", page);
        }

        static void NewEnough(Step s, string version, string site)
        {
            if (s.Version != null && version != null && CompareVersion(version, s.Version) < 0)
                throw new ModError("the newest " + In(s.Name, "en") + " on " + site + " is " + version + ", older than the " + s.Version + " needed", "recipe_too_old", s.Version);
        }

        /// <summary>From GameBanana or ModWorkshop: the file the step names on the mod's page,
        /// with the page as its origin (Sites.Fetch), so its updates are checked there.</summary>
        static string FromSite(Step s, string site, Action<long, long> progress)
        {
            var page = Sites.PageOf(site, site == Sites.Banana ? s.Banana : s.Workshop);
            var o = Sites.Look(page);
            if (o.Files.Count == 0) throw new ModError(site + " offers no file of " + In(s.Name, "en") + " to download here (" + (o.NoFiles ?? "no_files") + ")", "recipe_manual", page);
            var f = Pick(s, o.Files, x => x.Name, x => x.Label, x => x.Time, page);
            NewEnough(s, f.Version ?? o.Version, site);
            return Sites.Fetch(page, f, progress);
        }

        // a file Nexus Mods gave, and what it is there: recorded on the mod once added (Came)
        static readonly Dictionary<string, NexusRef> fromNexus = new Dictionary<string, NexusRef>(StringComparer.OrdinalIgnoreCase);

        /// <summary>From Nexus Mods, for a premium account (NexusDirect): the main file the step
        /// names on the mod's page. Nexus Mods refusing it (no premium) is "recipe_manual": the
        /// page's own button then.</summary>
        static string FromNexus(Step s, Action<long, long> progress)
        {
#if HAS_NEXUS
            var r = new NexusRef { Game = Hd2Mod.Nexus.Game, Mod = s.Nexus };
            var page = Hd2Mod.Nexus.Page(r) + "?tab=files";
            var files = Updates.Files(r).Where(x => (Json.Str(x, "category") ?? "") == "MAIN").ToList();
            var f = Pick(s, files, x => Json.Str(x, "name"), x => null, x => Json.Long(x, "date"), page);
            NewEnough(s, Json.Str(f, "version"), "Nexus Mods");
            var key = Hd2Mod.Nexus.Credential() ?? throw new ModError("not signed in to Nexus Mods", "recipe_manual", page);
            var link = new NxmLink { Game = r.Game, Mod = s.Nexus, File = Json.Long(f, "fileId") };
            string file;
            try { file = Hd2Mod.Nexus.Download(link, key, progress).File; }
            catch (ModError e) when (e.Kind == "nexus_refused") { throw new ModError("Nexus Mods gives " + In(s.Name, "en") + " through its page's button only: " + e.Message, "recipe_manual", page); }
            lock (fromNexus) fromNexus[file] = new NexusRef { Game = r.Game, Mod = s.Nexus, File = link.File, Version = Json.Str(f, "version"), Time = Json.Long(f, "date") };
            return file;
#else
            throw new ModError("this build does not download from Nexus Mods", "recipe_manual");
#endif
        }

        /// <summary>The mods a file just became: a Nexus Mods download's record written on them
        /// (its mod, file, version and time), as a download through the site's button has it.</summary>
        static void Came(Library lib, IEnumerable<string> ids, string file)
        {
            NexusRef r;
            lock (fromNexus) { if (!fromNexus.TryGetValue(file, out r)) return; fromNexus.Remove(file); }
            bool changed = false;
            foreach (var m in lib.Mods.Where(x => ids.Contains(x.Id)))
            {
                m.Nexus = new NexusRef { Game = r.Game, Mod = r.Mod, File = r.File, Version = r.Version, Time = r.Time };
                changed = true;
            }
            if (changed) lib.Save();
        }

        /// <summary>The step's file from GitHub, its origin the release it is from (so its updates
        /// are checked there): the author's copy of the file the list names, when it is that very
        /// file (SHA-256); else the newest release file of the step's Guid on GitHub.</summary>
        static string FromGitHub(Step s, Action<long, long> progress)
        {
            var (owner, repo) = (s.GitHub.Split('/')[0], s.GitHub.Split('/')[1]);
            // the author's copy only when it is new enough
            if (s.Mirror != null && Mirror.On && (s.Version == null || CompareVersion(GitHubUpdates.Label(s.Mirror.Tag, s.Mirror.Asset) ?? "", s.Version) >= 0))
            {
                var github = "https://github.com/" + s.GitHub + "/releases/download/" + Uri.EscapeDataString(s.Mirror.Tag) + "/" + Uri.EscapeDataString(s.Mirror.Asset);
                var page = "https://github.com/" + s.GitHub + "/releases/tag/" + Uri.EscapeDataString(s.Mirror.Tag);
                string got = null;
                try
                {
                    got = Sites.Download(s.Mirror.Url ?? Mirror.ReleaseFile(s.GitHub, s.Mirror.Tag, s.Mirror.Asset), page,
                                         new PageRef { Version = s.Mirror.Tag, Name = s.Mirror.Asset }, Store.ModsDir(), progress, github);
                    if (Sha256(got) == s.Mirror.Sha256) return got;
                    ManagerLog.Warn("the author's copy of " + s.Mirror.Asset + " is not the file the list names: from GitHub instead");
                }
                catch (ModError e) { ManagerLog.Warn("the author's copy of " + s.Mirror.Asset + ": " + e.Message + " -- from GitHub instead"); }
                if (got != null) try { File.Delete(got); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            var a = GitHubUpdates.Newest(new GitHubRef { Owner = owner, Repo = repo }, s.Guid)
                    ?? throw new ModError("no release of " + s.GitHub + " holds " + In(s.Name, "en"), "recipe_no_release", s.GitHub);
            if (s.Version != null && CompareVersion(GitHubUpdates.Label(a.Tag, a.Name) ?? "", s.Version) < 0)
                throw new ModError("the newest release of " + In(s.Name, "en") + " on GitHub is " + GitHubUpdates.Label(a.Tag, a.Name) + ", older than the " + s.Version + " needed", "recipe_too_old", s.Version);
            return Sites.Fetch(a.Page ?? "https://github.com/" + s.GitHub + "/releases", new SiteFile { Name = a.Name, Url = a.Url, Version = a.Tag, Time = a.Uploaded, Size = a.Size }, progress);
        }

        static string Sha256(string path)
        {
            using (var h = SHA256.Create())
            using (var f = File.OpenRead(path))
                return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        // ------------------------------------------------------------ after a game

        /// <summary>What the mod's log says of its last game: whether there was one (Started) and
        /// when its log was last written, and the problems it named in that game.</summary>
        public class Outcome
        {
            public bool Started;
            public DateTime When;
            public List<Check> Problems = new List<Check>();
        }

        /// <summary>Most of a log read (its end).</summary>
        public const int LogTail = 1 << 19;

        /// <summary>The plan's logs read in the game's log folder (LuaMods.GameFolder): the last
        /// "start" line of each, and the "problem" lines after it. Null when no log of the plan
        /// exists (the mod never ran here).</summary>
        public static Outcome Read(Recipe r, string folder = null)
        {
            folder = folder ?? LuaMods.GameFolderOverride ?? LuaMods.GameFolder;
            // read again only when a log changed: the window asks at every drawing of its page
            var sig = new StringBuilder(folder);
            foreach (var log in r.Checks.Select(c => c.Log).Distinct(StringComparer.OrdinalIgnoreCase))
                try { var fi = new FileInfo(Path.Combine(folder, log)); sig.Append('|').Append(fi.Exists ? fi.Length + ":" + fi.LastWriteTimeUtc.Ticks : "-"); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { sig.Append("|?"); }
            lock (read) if (read.TryGetValue(r, out var had) && had.Sig == sig.ToString()) return had.Outcome;
            var o = ReadNow(r, folder);
            lock (read) read[r] = (sig.ToString(), o);
            return o;
        }

        static readonly Dictionary<Recipe, (string Sig, Outcome Outcome)> read = new Dictionary<Recipe, (string, Outcome)>();

        static Outcome ReadNow(Recipe r, string folder)
        {
            Outcome o = null;
            foreach (var log in r.Checks.Select(c => c.Log).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var p = Path.Combine(folder, log);
                string text;
                DateTime when;
                try
                {
                    if (!File.Exists(p)) continue;
                    when = File.GetLastWriteTime(p);
                    using (var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long from = Math.Max(0, f.Length - LogTail);
                        f.Seek(from, SeekOrigin.Begin);
                        var buf = new byte[f.Length - from];
                        int n = 0, k;
                        while (n < buf.Length && (k = f.Read(buf, n, buf.Length - n)) > 0) n += k;
                        text = Encoding.UTF8.GetString(buf, 0, n);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { continue; }
                o = o ?? new Outcome();
                if (when > o.When) o.When = when;
                var mine = r.Checks.Where(c => string.Equals(c.Log, log, StringComparison.OrdinalIgnoreCase)).ToList();
                int start = mine.Where(c => c.Kind == "start").Select(c => text.LastIndexOf(c.Find, StringComparison.Ordinal)).DefaultIfEmpty(-1).Max();
                if (start >= 0) o.Started = true;
                var game = start >= 0 ? text.Substring(start) : text;
                foreach (var c in mine.Where(c => c.Kind == "problem"))
                {
                    int at = game.LastIndexOf(c.Find, StringComparison.Ordinal);
                    if (at < 0 || o.Problems.Contains(c)) continue;
                    if (!string.IsNullOrEmpty(c.Clear) && game.IndexOf(c.Clear, at, StringComparison.Ordinal) > at) continue;
                    o.Problems.Add(c);
                }
            }
            return o;
        }
    }
}
