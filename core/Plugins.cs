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
    /// its own, opened by Open(owner, host)), its Title(zh) and its Summary(zh). None ships with
    /// the program: the player adds them (the user, 2026-11-02), so they outlast a new copy of the
    /// program. They share no types with the manager (the core is compiled into each program), so
    /// they are called by name, with strings, numbers, delegates and Json shapes. Each file is
    /// read once a process, and again after it is added or removed.</summary>
    public static class PluginFiles
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
                return (file, version, a != null && a.IsLiteral && a.GetRawConstantValue() is int n && n == Api(path) ? null : "api");
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

        /// <summary>An added plugin, by its file name (HD2QD.Vram.dll, or Vram), deleted from Dir.
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
                var t = Load(path, null, Api(path), out var why);
                found.Add((Path.GetFileName(path), t, why));
            }
            return found;
        }

        /// <summary>The Api this manager speaks with each known plugin; 1 for one it does not know.</summary>
        static int Api(string path) =>
            string.Equals(Path.GetFileName(path), "HD2QD.License.dll", StringComparison.OrdinalIgnoreCase) ? Licenses.Api
            : string.Equals(Path.GetFileName(path), "HD2QD.Vram.dll", StringComparison.OrdinalIgnoreCase) ? Vram.Api : 1;

        public static string Kind(Type t) => t?.GetField("Kind")?.GetValue(null) as string;
        public static string Title(Type t, bool zh) => t?.GetMethod("Title")?.Invoke(null, new object[] { zh }) as string;
        public static string Summary(Type t, bool zh) => t?.GetMethod("Summary")?.Invoke(null, new object[] { zh }) as string;
        public static string Version(Type t) => t?.Assembly.GetName().Version?.ToString(3);
    }

    /// <summary>The VRAM estimate plugin, HD2QD.Vram.dll (plugins\Vram): what each enabled mod's
    /// textures and models take on the graphics card next to the game's copies they replace. A
    /// window plugin; the command line's `vram` uses its Estimate.</summary>
    public static class Vram
    {
        public const int Api = 1;

        static string why;
        static Type Plugin => PluginFiles.Load(PluginFiles.PathOf("HD2QD.Vram.dll"), "Hd2Mod.Vram.Plugin", Api, out why);

        public static bool Present => Plugin != null;

        /// <summary>For tests: the plugin's class (its assembly holds the LZ4 reader).</summary>
        internal static Type PluginType => Plugin;

        /// <summary>The enabled mods in load order (the last to load wins a resource two ship),
        /// each {name, archives: [the patch archives it deploys]}, as the plugin takes them.</summary>
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

        /// <summary>The estimate for the library's enabled mods (the plugin's Estimate).</summary>
        public static Dictionary<string, object> Estimate(Library lib, string data)
        {
            var t = Plugin ?? throw new ModError("the asset check plugin (HD2QD.Vram.dll) " +
                (why == "missing" ? "has not been added (plugin-add, or the window's Plugins page)" : why == "api" ? "is for another version of this program" : "is damaged"), "vram_plugin");
            try { return t.GetMethod("Estimate").Invoke(null, new object[] { Store.Home, data, Mods(lib), null }) as Dictionary<string, object>; }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }
    }

    /// <summary>The license plugin, HD2QD.License.dll (plugins\License), once added: mods
    /// that ship an hd2_license.json are for their author's members, and the plugin signs the
    /// player in to the author's issuer and keeps the license the mod checks in game. Without
    /// the file the manager works as before and such a mod is only marked. The plugin shares no
    /// types with the manager (the core is compiled into each program), so it is called by
    /// name, with strings, numbers and Json shapes.</summary>
    public static class Licenses
    {
        public const int Api = 1;

        /// <summary>Set by tests; else in PluginFiles.Dir.</summary>
        public static string DllPath;
        /// <summary>Set by tests; else HD2QD_APPDATA, for a demo library's pictures, which must
        /// show none of the player's accounts or licenses; else the player's roaming AppData (the
        /// game's saves\, and the licenses\ the mods read).</summary>
        public static string AppDataOverride;

        static string AppData =>
            AppDataOverride ?? (Environment.GetEnvironmentVariable("HD2QD_APPDATA") is string said && said.Length > 0 ? said
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        static string why;

        static Type Plugin => PluginFiles.Load(DllPath ?? PluginFiles.PathOf("HD2QD.License.dll"), "Hd2Mod.License.Plugin", Api, out why);

        public static bool Present => Plugin != null;

        /// <summary>The plugin's version ("1.0.0"), or null without one.</summary>
        public static string Version => Plugin?.Assembly.GetName().Version?.ToString(3);

        /// <summary>For tests: the plugin's class, to set its Clock.</summary>
        internal static Type PluginType => Plugin;

        /// <summary>Why there is no plugin: "missing", "api" (another version's), "broken"; null when there is one.</summary>
        public static string Why => Plugin == null ? why : null;

        static object Call(string name, params object[] args)
        {
            var t = Plugin ?? throw new ModError(Why == "missing"
                ? "the license plugin (HD2QD.License.dll) has not been added (plugin-add, or the window's Plugins page)"
                : "the license plugin (HD2QD.License.dll) " + (Why == "api" ? "is for another version of this program" : "is damaged"), "license_plugin");
            try { return t.GetMethod(name).Invoke(null, args); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        /// <summary>The mod's {issuer, name, key}, {error} for a broken declaration, or null for
        /// a mod that needs no license (or when there is no plugin to read it).</summary>
        public static Dictionary<string, object> Declaration(string modFolder) =>
            Present ? Call("Declaration", modFolder) as Dictionary<string, object> : Declared(modFolder);

        /// <summary>Without the plugin, only whether the mod says it needs one.</summary>
        static Dictionary<string, object> Declared(string modFolder) =>
            File.Exists(Path.Combine(modFolder, "hd2_license.json")) ? new Dictionary<string, object> { ["error"] = "no_plugin" } : null;

        /// <summary>What the state says of a mod: null when it needs no license; else {issuer,
        /// name, state, expires?, accounts?, renew_due?}, state as Plugin.Status, or "no_plugin"
        /// / "bad_declaration".</summary>
        public static Dictionary<string, object> StateOf(string modFolder)
        {
            var d = Declaration(modFolder);
            if (d == null) return null;
            var error = Json.Str(d, "error");
            if (error != null) return new Dictionary<string, object> { ["state"] = error };
            return Call("Status", Store.Home, AppData, d) as Dictionary<string, object>;
        }

        /// <summary>Where an issuer's license stands here, by its declaration.</summary>
        public static Dictionary<string, object> Status(Dictionary<string, object> decl) =>
            Call("Status", Store.Home, AppData, decl) as Dictionary<string, object>;

        public static List<string> Accounts() => Call("Accounts", AppData) as List<string>;

        public static Dictionary<string, object> BeginSignIn(Dictionary<string, object> decl) =>
            Call("BeginSignIn", AppData, decl) as Dictionary<string, object>;

        public static Dictionary<string, object> FinishSignIn(Dictionary<string, object> decl, string nonce, TimeSpan wait, CancellationToken cancel) =>
            Call("FinishSignIn", Store.Home, AppData, decl, nonce, (int)wait.TotalSeconds, cancel) as Dictionary<string, object>;

        public static Dictionary<string, object> Renew(Dictionary<string, object> decl, bool force) =>
            Call("Renew", Store.Home, AppData, decl, force) as Dictionary<string, object>;

        public static void Forget(string issuer) => Call("Forget", Store.Home, AppData, issuer);

        /// <summary>Every issuer the library's mods declare, each with its declaration and the
        /// mods that use it, in the library's order.</summary>
        public static List<(Dictionary<string, object> Decl, List<Mod> Mods)> Issuers(Library lib)
        {
            var found = new List<(Dictionary<string, object>, List<Mod>)>();
            foreach (var m in lib.Mods)
            {
                if (!Directory.Exists(m.Path)) continue;
                var d = Declaration(m.Path);
                if (d == null || Json.Str(d, "error") != null) continue;
                var at = found.FindIndex(x => Json.Str(x.Item1, "issuer") == Json.Str(d, "issuer") && Json.Str(x.Item1, "key") == Json.Str(d, "key"));
                if (at < 0) found.Add((d, new List<Mod> { m }));
                else found[at].Item2.Add(m);
            }
            return found;
        }

        /// <summary>Issuers(lib), then every issuer signed in here that no mod in the library
        /// names, with no mods: a sign-in stays in sight (and can be signed out) after its mods
        /// leave the library (the user, 2026-11-02: removing the probe hid it).</summary>
        public static List<(Dictionary<string, object> Decl, List<Mod> Mods)> Known(Library lib)
        {
            var found = Issuers(lib);
            foreach (var d in Call("SignedIn", Store.Home) as List<Dictionary<string, object>> ?? new List<Dictionary<string, object>>())
                if (!found.Any(x => Json.Str(x.Decl, "issuer") == Json.Str(d, "issuer")))
                    found.Add((d, new List<Mod>()));
            return found;
        }
    }
}
