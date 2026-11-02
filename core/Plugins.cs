using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Hd2Mod
{
    /// <summary>The license plugin, HD2QD.License.dll beside the program (plugins\License): mods
    /// that ship an hd2_license.json are for their author's members, and the plugin signs the
    /// player in to the author's issuer and keeps the license the mod checks in game. Without
    /// the file the manager works as before and such a mod is only marked. The plugin shares no
    /// types with the manager (the core is compiled into each program), so it is called by
    /// name, with strings, numbers and Json shapes.</summary>
    public static class Licenses
    {
        public const int Api = 1;

        /// <summary>Set by tests; else beside the running program.</summary>
        public static string DllPath;
        /// <summary>Set by tests; else HD2QD_APPDATA, for a demo library's pictures, which must
        /// show none of the player's accounts or licenses; else the player's roaming AppData (the
        /// game's saves\, and the licenses\ the mods read).</summary>
        public static string AppDataOverride;

        static string AppData =>
            AppDataOverride ?? (Environment.GetEnvironmentVariable("HD2QD_APPDATA") is string said && said.Length > 0 ? said
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        static Type plugin;
        static string loadedFrom, why;

        /// <summary>The plugin's class, or null (Why says why). Read as bytes, not loaded from
        /// the file: a program unpacked from a download carries the internet zone, and the
        /// .NET Framework refuses LoadFrom on such a file.</summary>
        static Type Plugin
        {
            get
            {
                var path = DllPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HD2QD.License.dll");
                if (loadedFrom == path) return plugin;
                loadedFrom = path;
                plugin = null;
                why = null;
                if (!File.Exists(path)) { why = "missing"; return null; }
                try
                {
                    var t = Assembly.Load(File.ReadAllBytes(path)).GetType("Hd2Mod.License.Plugin", true);
                    var api = t.GetField("Api")?.GetValue(null);
                    if (!(api is int n) || n != Api) { why = "api"; return null; }
                    plugin = t;
                }
                catch (Exception e) when (e is BadImageFormatException || e is TypeLoadException || e is IOException || e is FileLoadException)
                {
                    why = "broken";
                }
                return plugin;
            }
        }

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
                ? "the license plugin (HD2QD.License.dll) is not beside this program"
                : "the license plugin (HD2QD.License.dll) beside this program is " + (Why == "api" ? "for another version" : "damaged"), "license_plugin");
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
