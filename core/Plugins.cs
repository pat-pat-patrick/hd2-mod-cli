using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Hd2Mod
{
    /// <summary>Plugins: HD2QD.*.dll in the settings folder's plugins\, each with a class Plugin
    /// that says its Api, its Kind ("panel": drawn in the manager's window; "window": a window of
    /// its own, opened by Open(owner, host)), its Title(lang) and its Summary(lang). None ships with
    /// the program: the player adds them (the user, 2026-11-02), so they outlast a new copy of the
    /// program. They share no types with the manager (the core is compiled into each program), so
    /// they are called by name, with strings, numbers, delegates and Json shapes. Each file is
    /// read once a process, and again after it is added or removed.</summary>
    public static partial class PluginFiles
    {
        /// <summary>Set by tests; else Dir.</summary>
        public static string Folder;

        public static string Dir => Folder ?? Path.Combine(Store.Home, "plugins");

        public static string PathOf(string file) => Path.Combine(Dir, file);

        static readonly System.Text.RegularExpressions.Regex Name =
            new System.Text.RegularExpressions.Regex(@"^HD2QD\.[A-Za-z0-9_-]+\.dll$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static readonly Dictionary<string, (Type Type, string Why)> loaded = new Dictionary<string, (Type, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The plugin class typeName (null: the one class named Plugin) of the file at
        /// path, or null with why: "missing", "api" (another version's), "broken". Read as bytes,
        /// not loaded from the file: a program unpacked from a download carries the internet zone,
        /// and the .NET Framework refuses LoadFrom on such a file.</summary>
        public static Type Load(string path, string typeName, int api, out string why)
        {
            var key = path + "|" + typeName;
            if (loaded.TryGetValue(key, out var hit)) { why = hit.Why; return hit.Type; }
            Type found = null;
            why = null;
            if (!File.Exists(path)) why = "missing";
            else
                try
                {
                    var asm = Assembly.Load(File.ReadAllBytes(path));
                    var t = typeName != null ? asm.GetType(typeName, true) : asm.GetTypes().Single(x => x.Name == "Plugin" && x.IsPublic);
                    var a = t.GetField("Api")?.GetValue(null);
                    if (!(a is int n) || n != api) why = "api";
                    else found = t;
                }
                catch (Exception e) when (e is BadImageFormatException || e is TypeLoadException || e is IOException || e is FileLoadException
                                          || e is InvalidOperationException || e is ReflectionTypeLoadException)
                {
                    why = "broken";
                }
            // a missing file is looked for again next time: the player may put it there
            if (why != "missing") loaded[key] = (found, why);
            return found;
        }

        /// <summary>The file read again next time (it was added, replaced or removed).</summary>
        static void Forget(string path)
        {
            foreach (var key in loaded.Keys.Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).ToList())
                loaded.Remove(key);
        }

        /// <summary>What a file offered as a plugin is: its file name and version, and why it
        /// cannot be added: "name" (not HD2QD.*.dll), "broken" (no plugin in it), "api" (for
        /// another version of this program); null when it can. Only its metadata is read
        /// (the Api is a constant), so nothing of it runs before the player agrees.</summary>
        public static (string File, string Version, string Why) Inspect(string path)
        {
            var file = Path.GetFileName(path);
            if (!Name.IsMatch(file)) return (file, null, "name");
            try
            {
                var asm = Assembly.Load(File.ReadAllBytes(path));
                var version = asm.GetName().Version?.ToString(3);
                var t = asm.GetTypes().Where(x => x.Name == "Plugin" && x.IsPublic).ToList();
                if (t.Count != 1) return (file, version, "broken");
                var a = t[0].GetField("Api");
                return (file, version, a != null && a.IsLiteral && a.GetRawConstantValue() is int n && n == Api ? null : "api");
            }
            catch (Exception e) when (e is BadImageFormatException || e is TypeLoadException || e is IOException || e is FileLoadException
                                      || e is ReflectionTypeLoadException || e is UnauthorizedAccessException)
            {
                return (file, null, "broken");
            }
        }

        /// <summary>A plugin copied into Dir, over one of the same name: its file name, its version
        /// and the version it replaced (null when it is new).</summary>
        public static (string File, string Version, string Replaced) Install(string path)
        {
            var (file, version, why) = Inspect(path);
            if (why != null)
                throw new ModError(why == "name" ? file + " is not a plugin: a plugin is a file named HD2QD.<name>.dll"
                                   : why == "api" ? file + " is for another version of this program"
                                   : file + " is not a plugin of this program, or it is damaged", "plugin_" + why, file);
            var to = PathOf(file);
            string replaced = File.Exists(to) ? Inspect(to).Version ?? "?" : null;
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Dir);
                File.Copy(path, to + ".tmp", true);
                if (File.Exists(to)) File.Delete(to);
                File.Move(to + ".tmp", to);
            }
            Forget(to);
            return (file, version, replaced);
        }

        /// <summary>An added plugin, by its file name (HD2QD.AssetCheck.dll, or AssetCheck), deleted from Dir.
        /// What it already loaded stays in this process until the program is closed.</summary>
        public static string Remove(string name)
        {
            var file = All().Select(p => p.File).FirstOrDefault(f =>
                string.Equals(f, name, StringComparison.OrdinalIgnoreCase) || string.Equals(f, "HD2QD." + name + ".dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(f, name + ".dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new ModError("no plugin " + name + " has been added (plugins lists them)", "plugin_missing", name);
            File.Delete(PathOf(file));
            Forget(PathOf(file));
            return file;
        }

        /// <summary>Every HD2QD.*.dll added: its file name, its plugin class (null when it cannot
        /// be used) and why not.</summary>
        public static List<(string File, Type Type, string Why)> All()
        {
            var found = new List<(string, Type, string)>();
            var dir = Dir;
            if (!Directory.Exists(dir)) return found;
            foreach (var path in Directory.GetFiles(dir, "HD2QD.*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var t = Load(path, null, Api, out var why);
                found.Add((Path.GetFileName(path), t, why));
            }
            return found;
        }

        /// <summary>The Api every plugin speaks; a plugin of another one is refused. 2 (2026-11-03):
        /// the language is a language tag ("en", "zh-CN", "pt-BR"), never a Chinese-or-not switch
        /// (the user: "哪有本地化这么写的"), so a plugin can speak any number of languages.</summary>
        public const int Api = 2;

        public static string Kind(Type t) => t?.GetField("Kind")?.GetValue(null) as string;
        /// <summary>A plugin's title and summary in lang (a language tag): the plugin's own best
        /// match for it, English when it has none.</summary>
        public static string Title(Type t, string lang) => t?.GetMethod("Title")?.Invoke(null, new object[] { lang }) as string;
        public static string Summary(Type t, string lang) => t?.GetMethod("Summary")?.Invoke(null, new object[] { lang }) as string;
        public static string Version(Type t) => t?.Assembly.GetName().Version?.ToString(3);

        /// <summary>A window plugin's Icon (optional, Api 2): one character of Segoe Fluent Icons /
        /// Segoe MDL2 Assets. A plugin with one gets a button in the window's title bar, which the
        /// player can take away (Pinned).</summary>
        public static string Icon(Type t) =>
            Kind(t) == "window" && t?.GetField("Icon")?.GetValue(null) is string s && s.Length > 0 ? s : null;

        /// <summary>Whether a plugin with an Icon shows its button in the title bar: yes unless the
        /// player took it away (gui.json "unpinned_plugins").</summary>
        public static bool Pinned(string file) =>
            !Json.List(Store.Load("gui.json"), "unpinned_plugins").OfType<string>().Contains(file, StringComparer.OrdinalIgnoreCase);

        public static void Pin(string file, bool on)
        {
            var d = Store.Load("gui.json");
            var off = Json.List(d, "unpinned_plugins").OfType<string>().Where(f => !string.Equals(f, file, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!on) off.Add(file);
            d["unpinned_plugins"] = off.Cast<object>().ToList();
            Store.Save("gui.json", d);
        }

        /// <summary>A plugin's static method, by name, with what it threw (not the reflection's wrapper).</summary>
        public static object Call(Type t, string method, params object[] args)
        {
            var m = t.GetMethod(method) ?? throw new MissingMethodException(t.FullName, method);
            try { return m.Invoke(null, args); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        static IEnumerable<(string File, Type Type)> Usable() => All().Where(p => p.Type != null).Select(p => (p.File, p.Type));

        // ------------------------------------------------------------ what plugins add to the manager

        /// <summary>What is said on a mod's row: each added plugin's ModNote(modFolder, host), in
        /// the host's language, as {plugin, text, tip, colour ("Yellow", "Blue", "Muted")}; and for
        /// an official plugin the mod asks for that is not added, that it is needed, as the
        /// manager's own words: {plugin, key "needs_plugin", args [title in that language, file],
        /// colour, needs}, which the window words. A plugin that fails says nothing (it is
        /// logged): a row never breaks on a plugin.</summary>
        public static List<Dictionary<string, object>> Notes(string modFolder, Dictionary<string, object> host)
        {
            var notes = new List<Dictionary<string, object>>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, type) in Usable())
            {
                added.Add(file);
                if (type.GetMethod("ModNote") == null) continue;
                try
                {
                    if (Call(type, "ModNote", modFolder, host) is Dictionary<string, object> n && Json.Str(n, "text") != null)
                    {
                        n["plugin"] = file;
                        notes.Add(n);
                    }
                }
                catch (Exception e) { ManagerLog.Warn("plugin " + file + ": ModNote: " + e.Message); }
            }
            // an official plugin the mod asks for by a file it ships (the list's wanted_by_file)
            foreach (var ask in Official().Where(p => p.WantedBy != null))
                if (!added.Contains(ask.File) && File.Exists(Path.Combine(modFolder, ask.WantedBy)))
                    notes.Add(new Dictionary<string, object>
                    {
                        ["plugin"] = ask.File, ["needs"] = true, ["colour"] = "Yellow", ["key"] = "needs_plugin",
                        ["args"] = new List<object> { ask.TitleIn(host.TryGetValue("lang", out var l) ? l as string : null), ask.File },
                    });
            return notes;
        }

        /// <summary>The command-line commands the added plugins offer (their Commands: rows of
        /// [name, help]); a name the manager has itself is left out.</summary>
        public static List<(string Name, string Help, string File, Type Type)> Commands(ICollection<string> taken)
        {
            var out_ = new List<(string, string, string, Type)>();
            foreach (var (file, type) in Usable())
            {
                string[][] rows = null;
                try { rows = type.GetField("Commands")?.GetValue(null) as string[][]; }
                catch (Exception e) { ManagerLog.Warn("plugin " + file + ": Commands: " + e.Message); }
                foreach (var r in rows ?? new string[0][])
                    if (r != null && r.Length >= 1 && !taken.Contains(r[0]) && !out_.Any(x => x.Item1 == r[0]))
                        out_.Add((r[0], r.Length > 1 ? r[1] : "", file, type));
            }
            return out_;
        }

        /// <summary>Each added plugin's Start(host) that has one, in the background at the window's
        /// start (a renewal, a cache): whether any of them changed something worth reading again.</summary>
        public static bool Start(Dictionary<string, object> host)
        {
            bool changed = false;
            foreach (var (file, type) in Usable())
            {
                if (type.GetMethod("Start") == null) continue;
                try { changed |= Call(type, "Start", host) is bool b && b; }
                catch (Exception e) { ManagerLog.Warn("plugin " + file + ": Start: " + e.Message); }
            }
            return changed;
        }
    }

    /// <summary>What the manager hands a plugin (every call takes this host): home, its settings
    /// folder (a plugin keeps its own files there); appdata, the player's roaming AppData (the
    /// game's saves\); lang, the language the manager shows, a language tag ("en", "zh-CN"),
    /// which a plugin answers in, or in English when it does not have it; data, the game's data folder (null without a game
    /// folder); mods, a Func of the enabled mods in load order, each {name, archives: [patch
    /// archives]} (the last to load wins a resource two ship); library, a Func of every mod in
    /// the library, each {name, path, enabled, settings: [the settings window's lists its Lua
    /// writes]}. The window adds what only it has (PluginsView).</summary>
    public static class PluginHost
    {
        /// <summary>Set by tests; else HD2QD_APPDATA, for a demo library's pictures, which must
        /// show none of the player's accounts; else the player's roaming AppData.</summary>
        public static string AppDataOverride;

        public static string AppData =>
            AppDataOverride ?? (Environment.GetEnvironmentVariable("HD2QD_APPDATA") is string said && said.Length > 0 ? said
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        /// <summary>The language the manager shows, a language tag: the window says its own; the
        /// command line speaks English.</summary>
        public static Func<string> Lang = () => "en";

        public static Dictionary<string, object> Make()
        {
            var game = Store.GameDir(null, false);
            return new Dictionary<string, object>
            {
                ["api"] = (long)PluginFiles.Api, ["home"] = Store.Home, ["appdata"] = AppData, ["lang"] = Lang(),
                ["data"] = game != null ? Paths.Join(game, "data") : null,
                ["mods"] = (Func<List<Dictionary<string, object>>>)(() => Mods(Library.Load())),
                ["library"] = (Func<List<Dictionary<string, object>>>)(() => LibraryMods(Library.Load())),
            };
        }

        /// <summary>The enabled mods in load order, each {name, archives}.</summary>
        public static List<Dictionary<string, object>> Mods(Library lib)
        {
            var mods = new List<Dictionary<string, object>>();
            foreach (var w in Deploy.Plan(lib, new Dictionary<string, string>()))
            {
                if (!Deploy.Patch.IsMatch(w.Name)) continue;
                var last = mods.Count > 0 ? mods[mods.Count - 1] : null;
                if (last == null || (string)last["name"] != w.Mod)
                    mods.Add(last = new Dictionary<string, object> { ["name"] = w.Mod, ["archives"] = new List<object>() });
                ((List<object>)last["archives"]).Add(w.Src);
            }
            return mods;
        }

        /// <summary>Every library mod, each {name, path, enabled, settings}; reading the settings
        /// lists may read archives not read yet (LuaMods).</summary>
        public static List<Dictionary<string, object>> LibraryMods(Library lib)
        {
            var settings = LuaMods.Of(lib).ToDictionary(ml => ml.Mod.Id, ml => ml.Settings.Cast<object>().ToList());
            return lib.Mods.Select(m => new Dictionary<string, object>
            {
                ["name"] = m.Name, ["path"] = m.Path, ["enabled"] = m.Enabled,
                ["settings"] = settings.TryGetValue(m.Id, out var s) ? s : new List<object>(),
            }).ToList();
        }
    }
}
