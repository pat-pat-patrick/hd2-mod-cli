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

        // what a mod's archives make it, by the archives' sizes and times
        static readonly Dictionary<string, (string Sig, string Kind)> known = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"bml", "chef", "bsl", "mdl3", or null: as LuaMods names a loader, but read only from the
        /// archives that hold a boot or Chef Shared Loader's core (a row scan for the others), so a
        /// state read can ask it of every mod without reading their scripts.</summary>
        public static string KindOf(Mod mod)
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
        public static Mod Chosen(Library lib, Dictionary<string, object> cfg = null)
        {
            var loaders = Of(lib);
            if (loaders.Count == 0) return null;
            string choice = null;
            try { choice = Json.Str(cfg ?? Store.Config(), "loader"); }
            catch (Exception e) when (e is IOException || e is FormatException) { }
            if (choice == None) return null;
            var picked = choice == null ? null : loaders.FirstOrDefault(m => m.Id == choice);
            return picked ?? loaders.LastOrDefault(m => m.Enabled);
        }

        /// <summary>The first Boot Mod Loader that hands the mods made for Chef Shared Loader over to it
        /// (its documented hand-off, read from Chef 1.2.1's source; assumed until tried in game).</summary>
        public static readonly Version FirstHandOff = new Version(0, 13, 0);

        /// <summary>Set by a deploy: every archive is read, not only those already known (the window
        /// reads its states with LuaMods.Lazy, and a deploy must not leave a helper out).</summary>
        [ThreadStatic] public static bool ReadAll;

        /// <summary>The version a loader's boot names (Boot Mod Loader's marker), else null.</summary>
        public static string VersionOf(Mod mod)
        {
            try
            {
                foreach (var folder in Deploy.IncludedFolders(mod).Where(Directory.Exists))
                    foreach (var f in Deploy.List(folder).Where(f => Deploy.Patch.IsMatch(f)))
                    {
                        var p = Paths.Join(folder, f);
                        if (!Deploy.HoldsLua(p, new[] { LuaMods.BootId })) continue;
                        var boot = LuaMods.Read(p).FirstOrDefault(s => s.Id == LuaMods.BootId);
                        if (boot?.BootVersion != null) return boot.BootVersion;
                    }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ModError) { }
            return null;
        }

        /// <summary>Whether a mod's scripts can only start on Chef Shared Loader: it says so, or uses its
        /// boot-style or chef interface, or has a name Boot Mod Loader does not take (LuaMods.Of's rule).</summary>
        // the answers, by the mod's archives' sizes and times; only complete ones (every archive read)
        static readonly Dictionary<string, (string Sig, bool Needs)> needs = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);

        public static bool NeedsChef(Mod mod)
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
                lock (needs) if (needs.TryGetValue(mod.Path, out var k) && k.Sig == sig) return k.Needs;
                bool complete = true, found = false;
                foreach (var p in files)
                {
                    if (!Deploy.Types(p).Contains(Deploy.LuaType)) continue;
                    var scripts = LuaMods.Read(p, LuaMods.Lazy && !ReadAll);
                    if (scripts == null) { complete = false; continue; }
                    if (scripts.Any(s => s.ChefOnly || s.ChefApi || s.Header == "Boot" || (s.Header == "Addon" && !s.BmlName))) { found = true; break; }
                }
                if (found || complete) lock (needs) needs[mod.Path] = (sig, found);
                return found;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ModError) { }
            return false;
        }

        /// <summary>Chef Shared Loader as a helper (the user, 2026-10-26: "先试试协调方案"): with Boot Mod
        /// Loader 0.13.0 or later in effect, the library holding Chef, and an enabled mod made for Chef only,
        /// Chef goes to the game too, just before Boot Mod Loader (whose boot wins), and Boot Mod Loader
        /// hands those mods to it. Null otherwise.</summary>
        public static Mod Helper(Library lib) => HelperFor(lib, Chosen(lib));

        /// <summary>Helper, had this loader been chosen (null: none).</summary>
        public static Mod HelperFor(Library lib, Mod chosen)
        {
            if (chosen == null || KindOf(chosen) != "bml") return null;
            if (!Version.TryParse(VersionOf(chosen) ?? "", out var v) || v < FirstHandOff) return null;
            var chef = Of(lib).LastOrDefault(m => KindOf(m) == "chef");
            if (chef == null) return null;
            return lib.Mods.Any(m => m.Enabled && KindOf(m) == null && NeedsChef(m)) ? chef : null;
        }

        /// <summary>The mods a deploy takes, in the order it numbers them: every enabled mod that
        /// is no loader, in the library's order, then Chef Shared Loader when it helps, then the
        /// loader in effect.</summary>
        public static List<Mod> DeployOrder(Library lib) => DeployOrderFor(lib, Chosen(lib));

        /// <summary>DeployOrder, had this loader been chosen (null: none).</summary>
        public static List<Mod> DeployOrderFor(Library lib, Mod chosen)
        {
            var out_ = lib.Mods.Where(m => m.Enabled && KindOf(m) == null).ToList();
            var helper = HelperFor(lib, chosen);
            if (helper != null) out_.Add(helper);
            if (chosen != null) out_.Add(chosen);
            return out_;
        }

        /// <summary>The choice made: a loader of the library by name or id, or "none".</summary>
        public static Mod Choose(Library lib, string which)
        {
            var cfg = Store.Config();
            if (string.Equals(which, None, StringComparison.OrdinalIgnoreCase))
            {
                cfg["loader"] = None;
                Store.Save("config.json", cfg);
                return null;
            }
            var loaders = Of(lib);
            var m = loaders.FirstOrDefault(x => x.Id == which)
                    ?? loaders.FirstOrDefault(x => string.Equals(x.Name, which, StringComparison.CurrentCultureIgnoreCase))
                    ?? throw new ModError("no loader in the library is called " + which + " (" +
                                          (loaders.Count == 0 ? "there is none" : string.Join(", ", loaders.Select(x => x.Name))) + ")");
            cfg["loader"] = m.Id;
            Store.Save("config.json", cfg);
            return m;
        }
    }
}
