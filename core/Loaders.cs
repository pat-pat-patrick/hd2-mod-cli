using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>The loaders (Boot Mod Loader, Chef Shared Loader, Bingus Shared Loader, MDL3) are not
    /// mods the player orders: the user (2026-10-26) wants them out of the list and chosen in a
    /// panel of their own, the manager doing the rest. One is in effect at a time, chosen for
    /// every profile alike (config.json "loader": its id, or "none"); unset, it is the one in
    /// effect before there was a choice, the last one enabled. A deploy takes the chosen loader
    /// whatever its own switch, puts it after every other mod (its boot is the one the game
    /// runs, the load order does not matter), and leaves the other loaders out.</summary>
    public static class Loaders
    {
        public const string None = "none";

        // one state read asks the same things of every mod many times over: which loader it is,
        // and each loader's facts, for every choice of loader and every helper, each time listing
        // every mod's folders and opening the loaders' archives (1.3 s of a 2.4 s read, a library
        // of 103 mods, the user, 2026-11-03). Within one read (Pass) each answer is kept, by the
        // mod object that read has: its folders do not change under it.
        [ThreadStatic] static Dictionary<(object, string), object> passed;

        /// <summary>Answers kept until the result is disposed (a state read); nested, the outer
        /// one keeps them.</summary>
        public static IDisposable Pass()
        {
            if (passed != null) return new PassEnd(false);
            passed = new Dictionary<(object, string), object>();
            return new PassEnd(true);
        }

        sealed class PassEnd : IDisposable
        {
            readonly bool mine;
            public PassEnd(bool mine) { this.mine = mine; }
            public void Dispose() { if (mine) passed = null; }
        }

        static T Kept<T>(object of, string what, Func<T> make)
        {
            var p = passed;
            if (p == null || of == null) return make();
            if (p.TryGetValue((of, what), out var had)) return (T)had;
            var made = make();
            p[(of, what)] = made;
            return made;
        }

        // what a mod's archives make it, by the archives' sizes and times
        static readonly Dictionary<string, (string Sig, string Kind)> known = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"bml", "chef", "bsl", "mdl3", or null: as LuaMods names a loader, but read only from the
        /// archives that hold a boot or Chef Shared Loader's core (a row scan for the others), so a
        /// state read can ask it of every mod without reading their scripts.</summary>
        public static string KindOf(Mod mod) => Kept(mod, "kind", () => KindOfNow(mod));

        static string KindOfNow(Mod mod)
        {
            if (mod == null || !Directory.Exists(mod.Path)) return null;
            List<string> folders;
            try { folders = Deploy.IncludedFolders(mod); }
            catch (ModError) { return null; }
            var files = new List<string>();
            foreach (var folder in folders.Where(Directory.Exists))
                foreach (var f in Deploy.List(folder).Where(f => Deploy.Patch.IsMatch(f))) files.Add(Paths.Join(folder, f));
            var sig = string.Join("|", files.Select(p =>
            {
                var fi = new FileInfo(p);
                return p + ":" + fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
            }));
            lock (known) if (known.TryGetValue(mod.Path, out var k) && k.Sig == sig) return k.Kind;
            string kind = null;
            bool chefCore = false;
            string boot = null;
            foreach (var p in files)
            {
                try
                {
                    if (!Deploy.HoldsLua(p, new[] { LuaMods.BootId, LuaMods.ChefCoreId })) continue;
                    foreach (var s in LuaMods.Read(p))
                    {
                        if (s.Id == LuaMods.ChefCoreId) chefCore = true;
                        if (s.Id == LuaMods.BootId && boot == null) boot = s.BootKind;
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            if (chefCore || boot == "chef") kind = "chef";
            else if (boot == "bml" || boot == "bsl" || boot == "mdl3") kind = boot;
            lock (known) known[mod.Path] = (sig, kind);
            return kind;
        }

        /// <summary>The loaders of the library, in its order.</summary>
        public static List<Mod> Of(Library lib) => lib.Mods.Where(m => KindOf(m) != null).ToList();

        /// <summary>The loader in effect: the one chosen, else the last one enabled; null for none.</summary>
        public static Mod Chosen(Library lib, Dictionary<string, object> cfg = null) =>
            cfg == null ? Kept(lib, "chosen", () => ChosenNow(lib, null)) : ChosenNow(lib, cfg);

        /// <summary>Where the choice is kept: "loader" for the official game, "loader@&lt;target&gt;" for
        /// a plugin's deploy target, whose game may load Lua another way (HellForge: Bingus Shared
        /// Loader only, 2026-11-11). Unset there, the loader in effect is the last one on there.</summary>
        static string ConfigKey(Library lib) => Targets.IsOfficial(lib) ? "loader" : "loader@" + Targets.Current(lib);

        static Mod ChosenNow(Library lib, Dictionary<string, object> cfg)
        {
            var loaders = Of(lib);
            if (loaders.Count == 0) return null;
            string choice = null;
            try { choice = Json.Str(cfg ?? Store.Config(), ConfigKey(lib)); }
            catch (Exception e) when (e is IOException || e is FormatException) { }
            if (choice == None) return null;
            var picked = choice == null ? null : loaders.FirstOrDefault(m => m.Id == choice);
            if (picked != null && !Targets.Belongs(lib, picked)) picked = null;       // a loader for another target (Mod.For)
            return picked ?? loaders.LastOrDefault(m => m.Enabled);
        }

        /// <summary>Set by a deploy: every archive is read, not only those already known (the window
        /// reads its states with LuaMods.Lazy, and a deploy must not leave a helper out).</summary>
        [ThreadStatic] public static bool ReadAll;

        /// <summary>A loader's version (its boot's, or Chef's core's) and the feature markers its code
        /// carries (LoaderCaps).</summary>
        static (string Version, List<string> Marks) Facts(Mod mod) => Kept(mod, "facts", () => FactsNow(mod));

        static (string Version, List<string> Marks) FactsNow(Mod mod)
        {
            string version = null;
            var marks = new List<string>();
            try
            {
                foreach (var folder in Deploy.IncludedFolders(mod).Where(Directory.Exists))
                    foreach (var f in Deploy.List(folder).Where(f => Deploy.Patch.IsMatch(f)))
                    {
                        var p = Paths.Join(folder, f);
                        if (!Deploy.HoldsLua(p, new[] { LuaMods.BootId, LuaMods.ChefCoreId })) continue;
                        foreach (var s in LuaMods.Read(p).Where(s => s.Id == LuaMods.BootId || s.Id == LuaMods.ChefCoreId))
                        {
                            version = version ?? s.BootVersion;
                            marks.AddRange(s.Marks);
                        }
                    }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ModError) { }
            return (version, marks.Distinct().ToList());
        }

        /// <summary>The version a loader's boot or Chef's core names, else null.</summary>
        public static string VersionOf(Mod mod) => Facts(mod).Version;

        /// <summary>A loader's capabilities (LoaderCaps); none for a mod that is no loader.</summary>
        public static List<string> CapsOf(Mod mod)
        {
            var kind = KindOf(mod);
            if (kind == null) return new List<string>();
            var (version, marks) = Facts(mod);
            return LoaderCaps.Of(kind, version, marks);
        }

        /// <summary>Whether a mod's scripts can only start on Chef Shared Loader: it says so, or uses its
        /// boot-style or chef interface, or has a name Boot Mod Loader does not take (LuaMods.Of's rule).</summary>
        // the answers, by the mod's archives' sizes and times; only complete ones (every archive read)
        static readonly Dictionary<string, (string Sig, bool Needs)> needs = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);

        public static bool NeedsChef(Mod mod) => Kept(mod, "needs_chef", () =>
            AnyScript(mod, needs, s => s.ChefOnly || s.ChefApi || s.Header == "Boot" || (s.Header == "Addon" && !s.BmlName)));

        /// <summary>Whether a mod's scripts read MDL3 or HD2Data (LuaScript.Mdl3Api).</summary>
        static readonly Dictionary<string, (string Sig, bool Needs)> usesMdl3 = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);

        public static bool UsesMdl3(Mod mod) => Kept(mod, "uses_mdl3", () => AnyScript(mod, usesMdl3, s => s.Mdl3Api));

        /// <summary>Whether any of a mod's scripts is so, kept in answers.</summary>
        static bool AnyScript(Mod mod, Dictionary<string, (string Sig, bool Needs)> answers, Func<LuaScript, bool> so)
        {
            if (mod == null || !Directory.Exists(mod.Path)) return false;
            try
            {
                var files = new List<string>();
                foreach (var folder in Deploy.IncludedFolders(mod).Where(Directory.Exists))
                    foreach (var f in Deploy.List(folder).Where(f => Deploy.Patch.IsMatch(f))) files.Add(Paths.Join(folder, f));
                var sig = string.Join("|", files.Select(p =>
                {
                    var fi = new FileInfo(p);
                    return p + ":" + fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
                }));
                lock (answers) if (answers.TryGetValue(mod.Path, out var k) && k.Sig == sig) return k.Needs;
                bool complete = true, found = false;
                foreach (var p in files)
                {
                    if (!Deploy.Types(p).Contains(Deploy.LuaType)) continue;
                    var scripts = LuaMods.Read(p, LuaMods.Lazy && !ReadAll);
                    if (scripts == null) { complete = false; continue; }
                    if (scripts.Any(so)) { found = true; break; }
                }
                if (found || complete) lock (answers) answers[mod.Path] = (sig, found);
                return found;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ModError) { }
            return false;
        }

        /// <summary>Chef Shared Loader as a helper (the user, 2026-10-26: "先试试协调方案"): the loader in
        /// effect hands the mods made for Chef to it (LoaderCaps.Beside "hand_off": Boot Mod Loader 0.13.0
        /// or later, Chef 1.2.0 or later), and an enabled mod is made for Chef only. Chef goes to the
        /// game too, just before the loader (whose boot wins). Null otherwise.</summary>
        public static Mod Helper(Library lib) => HelperFor(lib, Chosen(lib));

        /// <summary>Helper, had this loader been chosen (null: none).</summary>
        public static Mod HelperFor(Library lib, Mod chosen) =>
            BesideFor(lib, chosen, "hand_off", m => NeedsChef(m));

        /// <summary>MDL3 as the loader's guest (the user, 2026-11-01: mods made for different loaders
        /// used together): the loader in effect runs guests and MDL3 is a quiet guest that gives what
        /// it does not (LoaderCaps.Beside "guest": Boot Mod Loader 0.12.0 or later), and an enabled mod
        /// reads MDL3 or HD2Data. MDL3 goes to the game too, before the loader, its boot off the
        /// start-up slot for the loader to run (GuestBoots). MDL3 then starts no mod of its own (see
        /// LoaderCaps.QuietGuest) and gives its MDL3 global, audio and native part. Mods that use them
        /// look for them each frame (SHH, HD2 Data Hub 0.3.0), not at load, which is before a guest
        /// runs. Seen working in a player's video with Quick Deploy 0.12.0, where MDL3 dev.25 became a
        /// guest by accident; not yet tried in game here. Null otherwise.</summary>
        public static Mod Mdl3GuestFor(Library lib, Mod chosen) =>
            BesideFor(lib, chosen, "guest", m => UsesMdl3(m));

        /// <summary>The library's last loader that goes beside the one chosen this way, when an enabled
        /// mod needs it.</summary>
        static Mod BesideFor(Library lib, Mod chosen, string how, Func<Mod, bool> needs)
        {
            if (chosen == null) return null;
            var caps = CapsOf(chosen);
            var other = Of(lib).LastOrDefault(m => m.Id != chosen.Id && LoaderCaps.Beside(caps, CapsOf(m)) == how);
            if (other == null) return null;
            return lib.Mods.Any(m => m.Enabled && KindOf(m) == null && needs(m)) ? other : null;
        }

        /// <summary>The loaders that go to the game beside the one in effect, in deploy order: MDL3 as a
        /// guest, Chef Shared Loader as a helper.</summary>
        public static List<Mod> Helpers(Library lib) => HelpersFor(lib, Chosen(lib));

        /// <summary>Helpers, had this loader been chosen (null: none).</summary>
        public static List<Mod> HelpersFor(Library lib, Mod chosen) =>
            new[] { Mdl3GuestFor(lib, chosen), HelperFor(lib, chosen) }.Where(m => m != null).ToList();

        /// <summary>The mods a deploy takes, in the order it numbers them: every enabled mod that
        /// is no loader, in the library's order, then MDL3 as a guest and Chef Shared Loader when
        /// they help, then the loader in effect.</summary>
        public static List<Mod> DeployOrder(Library lib) => DeployOrderFor(lib, Chosen(lib));

        /// <summary>DeployOrder, had this loader been chosen (null: none).</summary>
        public static List<Mod> DeployOrderFor(Library lib, Mod chosen)
        {
            var out_ = lib.Mods.Where(m => m.Enabled && KindOf(m) == null).ToList();
            out_.AddRange(HelpersFor(lib, chosen));
            if (chosen != null) out_.Add(chosen);
            return out_;
        }

        /// <summary>The choice made: a loader of the library by name or id, or "none".</summary>
        public static Mod Choose(Library lib, string which)
        {
            var cfg = Store.Config();
            if (string.Equals(which, None, StringComparison.OrdinalIgnoreCase))
            {
                cfg[ConfigKey(lib)] = None;
                Store.Save("config.json", cfg);
                return null;
            }
            var loaders = Of(lib);
            var m = loaders.FirstOrDefault(x => x.Id == which)
                    ?? loaders.FirstOrDefault(x => string.Equals(x.Name, which, StringComparison.CurrentCultureIgnoreCase))
                    ?? throw new ModError("no loader in the library is called " + which + " (" +
                                          (loaders.Count == 0 ? "there is none" : string.Join(", ", loaders.Select(x => x.Name))) + ")");
            cfg[ConfigKey(lib)] = m.Id;
            Store.Save("config.json", cfg);
            return m;
        }
    }
}
