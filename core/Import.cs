using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>A mod manager found on this computer whose list can be imported.</summary>
    public class ImportSource
    {
        /// <summary>"arsenal", "hd2mm" or "echelon".</summary>
        public string Key, Name, Where;
        public int Mods;
        /// <summary>HD2ModManager's storage folder when it is not in its usual place, else null.</summary>
        public string Dir;
        /// <summary>The manager's own list file, when the player chose it (not in its usual place), else null.</summary>
        public string File;
        /// <summary>Its list was found but could not be read: why (ProblemKind "bad_format" when its
        /// shape is unknown, "bad_version" for a version this program does not read). Null when read.</summary>
        public string Problem, ProblemKind;

        /// <summary>A manager whose list was found and could not be read.</summary>
        public static ImportSource Unreadable(string key, string name, string where, Exception e) =>
            new ImportSource { Key = key, Name = name, Where = where, Problem = e.Message, ProblemKind = (e as ModError)?.Kind };
        /// <summary>The manager's profiles, when it keeps more than one; else empty.</summary>
        public List<string> ProfileNames = new List<string>();
    }

    /// <summary>One of another manager's profiles: its mods (the ones of ImportedProfiles.All), in
    /// its order, each with its on/off and options there.</summary>
    public class ImportedProfile
    {
        public string Name;
        public List<(Mod Mod, bool On, List<OptionChoice> Options)> Mods = new List<(Mod, bool, List<OptionChoice>)>();
    }

    /// <summary>Another manager's profiles: every mod in any of them, once (All), each profile, and
    /// the one in use there.</summary>
    public class ImportedProfiles
    {
        public Library All = new Library();
        public List<ImportedProfile> Profiles = new List<ImportedProfile>();
        public string InUse;
    }

    /// <summary>Other mod managers' lists, read only, merged into the library: a mod already in
    /// it (the same folder, the same manifest Guid, or the same name) is left as it is.</summary>
    public static class Importers
    {
        /// <summary>Which manager keeps its mods in this folder, by the marks each leaves on its mod
        /// folders (the user, 2026-10-26: a manager's store is imported through that manager's own
        /// list, never as loose mods). Half of the mod folders in it at least, and two or more:
        /// "arsenal": names ending in "_AR" and six digits (HD2 Arsenal's copies);
        /// "echelon": names ending in " (" and twelve hex digits ")", no manifest.json, the files in
        /// numbered set folders (0, opt\0, ...);
        /// "hd2mm": a manifest.json with a Guid in each, and at least one that HD2ModManager wrote
        /// for a mod it imported ("A locally imported mod.").
        /// Null for any other folder.</summary>
        public static string StoreOf(string folder)
        {
            List<string> subs;
            try { subs = Directory.EnumerateDirectories(folder).ToList(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { return null; }
            int all = 0, ar = 0, ec = 0, guid = 0, local = 0;
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                bool manifest = System.IO.File.Exists(Path.Combine(sub, "manifest.json"));
                bool sets;
                try
                {
                    sets = Directory.EnumerateDirectories(sub).Select(Path.GetFileName).Any(n => SetFolder.IsMatch(n))
                        || Directory.Exists(Path.Combine(sub, "opt")) && Directory.EnumerateDirectories(Path.Combine(sub, "opt")).Select(Path.GetFileName).Any(n => SetFolder.IsMatch(n));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { sets = false; }
                bool patches;
                try { patches = Deploy.List(sub).Any(f => Deploy.Patch.IsMatch(f)); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { patches = false; }
                if (!manifest && !patches && !sets && !ArsenalName.IsMatch(name)) continue;          // no mod folder
                all++;
                if (ArsenalName.IsMatch(name)) ar++;
                if (EchelonName.IsMatch(name) && !manifest && sets) ec++;
                if (manifest)
                {
                    string text = "";
                    try { text = System.IO.File.ReadAllText(Path.Combine(sub, "manifest.json")); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                    if (text.IndexOf("\"Guid\"", StringComparison.OrdinalIgnoreCase) >= 0) guid++;
                    if (text.IndexOf("A locally imported mod.", StringComparison.Ordinal) >= 0) local++;
                }
            }
            bool Most(int n) => n >= 2 && n * 2 >= all;
            if (Most(ar)) return "arsenal";
            if (Most(ec)) return "echelon";
            if (Most(guid) && local > 0) return "hd2mm";
            return null;
        }

        static readonly System.Text.RegularExpressions.Regex ArsenalName = new System.Text.RegularExpressions.Regex(@"_AR\d{6}$");
        static readonly System.Text.RegularExpressions.Regex EchelonName = new System.Text.RegularExpressions.Regex(@" \([0-9a-f]{12}\)$");
        static readonly System.Text.RegularExpressions.Regex SetFolder = new System.Text.RegularExpressions.Regex(@"^\d+$");

        /// <summary>The name, the list file and its usual folder of each manager StoreOf knows.</summary>
        public static (string Name, string File, string Usual) ConfigOf(string key)
        {
            string Env(string v) => Environment.GetEnvironmentVariable(v) ?? "";
            switch (key)
            {
                case "arsenal": return ("HD2 Arsenal", "hd2a_data.json", Path.Combine(Env("LOCALAPPDATA"), "hd2arsenal"));
                case "hd2mm": return ("HD2ModManager", "enabled.json", Path.Combine(Env("LOCALAPPDATA"), "Helldivers2ModManager"));
                case "echelon": return ("Echelon", "library.json", Path.Combine(Env("APPDATA"), "Echelon"));
            }
            return (key, null, null);
        }

        /// <summary>A list file the player chose for a manager's store, read: how many of the mods it
        /// lists are in the folder (0: it is not that folder's list), and for HD2ModManager its storage
        /// folder (the one that holds enabled.json and Mods). Its profiles, when it keeps several.</summary>
        public static ImportSource FromChosenFile(string key, string file, string folder)
        {
            var root = Path.GetFullPath(folder).TrimEnd('\\');
            bool Inside(string p)
            {
                // an entry with no path is in no folder (an empty path is refused: Paths.AbsOrEmpty)
                if (string.IsNullOrWhiteSpace(p)) return false;
                var x = Path.GetFullPath(p).TrimEnd('\\');
                return string.Equals(x, root, StringComparison.OrdinalIgnoreCase) || x.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
            }
            var (name, _, _) = ConfigOf(key);
            var src = new ImportSource { Key = key, Name = name, Where = root };
            List<Mod> mods;
            if (key == "arsenal")
            {
                var p = ArsenalProfiles(file);
                mods = p.All.Mods;
                src.File = file;
                var names = p.Profiles.Select(x => x.Name).ToList();
                if (names.Count > 1) src.ProfileNames = names;
            }
            else if (key == "echelon")
            {
                mods = ReadEchelon(file).Mods;
                src.File = file;
                var names = EchelonProfiles(file)?.Profiles.Select(x => x.Name).ToList() ?? new List<string>();
                if (names.Count > 1) src.ProfileNames = names;
            }
            else
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(file));
                mods = ReadHd2mm(dir).Mods;
                src.Dir = dir;
            }
            src.Mods = mods.Count(m => Inside(m.Path));
            return src;
        }

        /// <summary>HD2ModManager's own folder (its Mods\ and enabled.json), when it is there.</summary>
        public static string Hd2mmDir()
        {
            var b = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            var d = string.IsNullOrEmpty(b) ? null : Path.Combine(b, "Helldivers2ModManager");
            return d != null && Directory.Exists(Path.Combine(d, "Mods")) ? d : null;
        }

        /// <summary>HD2ModManager's storage folder when the folder given is it, its Mods folder, or
        /// the program's own folder: settings.json beside Helldivers2ModManager.exe names the
        /// storage folder, which its settings let a player move (the user, 2026-10-25). Else null.</summary>
        public static string Hd2mmStorage(string folder)
        {
            try
            {
                // its own list file says so: a Mods folder of manifests alone is any mod folder
                bool Is(string d) => d != null && Directory.Exists(Path.Combine(d, "Mods")) && File.Exists(Path.Combine(d, "enabled.json"));
                var f = Path.GetFullPath(folder).TrimEnd('\\');
                if (Is(f)) return f;
                var up = Path.GetDirectoryName(f);
                if (string.Equals(Path.GetFileName(f), "Mods", StringComparison.OrdinalIgnoreCase) && Is(up)) return up;
                var settings = Path.Combine(f, "settings.json");
                if (File.Exists(settings) && File.Exists(Path.Combine(f, "Helldivers2ModManager.exe")))
                {
                    var s = Json.Str(Store.ReadJson(settings) as Dictionary<string, object>, "StorageDirectory");
                    if (!string.IsNullOrEmpty(s) && Is(s)) return Path.GetFullPath(s).TrimEnd('\\');
                }
            }
            catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException ||
                                      e is ArgumentException || e is NotSupportedException) { }
            return null;
        }

        /// <summary>Echelon's own list, when it is there: %APPDATA%\Echelon\library.json, which it
        /// writes for other tools to read (its author asked, 2026-10-13). HD2QD_ECHELON names
        /// another file, for tests.</summary>
        public static string EchelonFile()
        {
            var said = Environment.GetEnvironmentVariable("HD2QD_ECHELON");
            if (!string.IsNullOrEmpty(said)) return File.Exists(said) ? said : null;
            var b = Environment.GetEnvironmentVariable("APPDATA");
            var f = string.IsNullOrEmpty(b) ? null : Path.Combine(b, "Echelon", "library.json");
            return f != null && File.Exists(f) ? f : null;
        }

        /// <summary>Echelon's list (format "echelon-library", version 1), read only: each mod's
        /// folder, the name it is called there, whether it is on, and the options it has chosen.
        /// An option with choices is a pick-one, and "selected" is the choice that is on. The
        /// options are matched to the mod's own manifest by name when it is deployed.</summary>
        public static Library ReadEchelon(string file = null)
        {
            file = file ?? EchelonFile();
            if (file == null) throw new ModError("Echelon's library.json was not found", "no_source");
            var d = Store.ReadJson(file) as Dictionary<string, object> ?? new Dictionary<string, object>();
            var format = Json.Str(d, "format");
            if (format != "echelon-library") throw new ModError("this is not Echelon's library.json (format " + (format ?? "none") + ")", "bad_format");
            if (Json.Long(d, "version") != 1) throw new ModError("Echelon's library.json is version " + Json.Long(d, "version") + ", which this program does not read", "bad_version");
            var lib = new Library { ImportedFrom = "Echelon" };
            var theirIds = new List<string>();
            foreach (var m in Json.List(d, "mods").OfType<Dictionary<string, object>>())
            {
                var folder = Json.Str(m, "folder");
                if (string.IsNullOrEmpty(folder)) continue;
                // Echelon keeps its own copy of the mod, one folder per set (opt/0, opt/1, ...)
                // and no manifest.json, so its options are the only thing that says which folder
                // belongs to which option: they come in as the mod's layout.
                var sets = Json.List(m, "sets").OfType<Dictionary<string, object>>().ToList();
                List<string> Folders(Dictionary<string, object> o, string key)
                {
                    var out_ = new List<string>();
                    foreach (var x in Json.List(o, key))
                    {
                        if (!(x is long i) || i < 0 || i >= sets.Count) continue;
                        var first = Json.List(sets[(int)i], "files").OfType<string>().FirstOrDefault() ?? "";
                        int cut = first.LastIndexOf('/');
                        var dir = cut < 0 ? "" : first.Substring(0, cut).Replace('/', '\\');
                        if (dir.Length > 0 && !out_.Contains(dir, StringComparer.OrdinalIgnoreCase)) out_.Add(dir);
                    }
                    return out_;
                }
                var options = new List<OptionChoice>();
                var layout = new List<ManifestOption>();
                foreach (var o in Json.List(m, "options").OfType<Dictionary<string, object>>())
                {
                    var choices = Json.List(o, "choices").OfType<Dictionary<string, object>>().ToList();
                    bool picked = o.TryGetValue("selected", out var sel) && sel != null;
                    long which = picked ? Json.Long(o, "selected") : 0;
                    var name = Json.Str(o, "name") ?? "";
                    options.Add(new OptionChoice
                    {
                        Name = name,
                        Enabled = Json.Bool(o, "enabled", true),
                        Subs = choices.Select((c, i) => new SubChoice { Name = Json.Str(c, "name") ?? "", Enabled = i == which }).ToList(),
                    });
                    layout.Add(new ManifestOption
                    {
                        Name = name,
                        Include = Folders(o, "sets"),
                        SubOptions = choices.Select(c => new ManifestOption { Name = Json.Str(c, "name") ?? "", Include = Folders(c, "sets") }).ToList(),
                    });
                }
                // nothing to say where the files are: leave the folder to be read as it is
                if (!layout.Any(o => o.Include.Count > 0 || o.SubOptions.Any(x => x.Include.Count > 0))) layout.Clear();
                lib.Mods.Add(new Mod
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = Json.Str(m, "name") ?? Path.GetFileName(folder.TrimEnd('\\')),
                    Path = folder, Source = folder,
                    Enabled = Json.Bool(m, "enabled", true),
                    Options = options, Layout = layout,
                });
                theirIds.Add(Json.Str(m, "id") ?? "");
                lib.Mods[lib.Mods.Count - 1].ImportKey = Json.Str(m, "id");
            }
            // What library.json leaves out is in Echelon's own state.json. It is Echelon's own
            // file, not one written for other tools, so a version not known here leaves it unread.
            var dir = Path.GetDirectoryName(Path.GetFullPath(file));
            var state = EchelonState(Path.Combine(dir, "state.json"));
            if (state == null) return lib;
            // the pictures and the words: a tester, 2026-10-25, saw neither come in. Echelon keeps
            // them beside its library, not in the mod's folder.
            var theirLib = Json.Obj(state, "library");
            for (int i = 0; i < lib.Mods.Count; i++)
            {
                var their = Json.Obj(theirLib, theirIds[i]);
                if (their == null) continue;
                var mod = lib.Mods[i];
                mod.Description = Json.Str(their, "description");
                // its labels there, as labels here (plain names; Echelon's colours have no place here)
                foreach (var t in Json.List(their, "tags"))
                    if (t is string tag && tag.Trim().Length > 0 && !mod.Tags.Contains(tag.Trim(), StringComparer.CurrentCultureIgnoreCase))
                        mod.Tags.Add(tag.Trim());
                mod.ImportPicture = EchelonPicture(dir, Json.Str(their, "image"));
                // where it came from (the same tester: "来源也都没有显示"); a version left blank
                // there is no version
                var nx = Json.Obj(their, "nexus");
                if (nx != null && Json.Long(nx, "mod_id") > 0)
                    mod.Nexus = new NexusRef
                    {
                        Game = Nexus.Game, Mod = Json.Long(nx, "mod_id"), File = Json.Long(nx, "file_id"),
                        Version = string.IsNullOrWhiteSpace(Json.Str(nx, "version")) ? null : Json.Str(nx, "version").Trim(),
                    };
                var theirOptions = Json.List(their, "options").OfType<Dictionary<string, object>>().ToList();
                foreach (var o in mod.Layout)
                {
                    var t = theirOptions.FirstOrDefault(x => Json.Str(x, "name") == o.Name);
                    if (t == null) continue;
                    o.Description = Json.Str(t, "description") ?? "";
                    o.Image = EchelonPicture(dir, Json.Str(t, "image"));
                    var theirSubs = Json.List(t, "subs").OfType<Dictionary<string, object>>().ToList();
                    foreach (var sub in o.SubOptions)
                    {
                        var ts = theirSubs.FirstOrDefault(x => Json.Str(x, "name") == sub.Name);
                        if (ts == null) continue;
                        sub.Description = Json.Str(ts, "description") ?? "";
                        sub.Image = EchelonPicture(dir, Json.Str(ts, "image"));
                    }
                }
            }
            // library.json lists the mods in the order they were added, not in the load order
            // (a tester, 2026-10-25: "乱序"). The load order is the active profile's: top first,
            // the bottom one deployed last and so winning, as here (proven on 0.37.4: the top mod
            // got patch_0). Mods the profile does not list follow in library.json's order.
            var order = EchelonOrder(state);
            if (order != null)
            {
                var ranked = lib.Mods.Select((x, i) => (Mod: x, Rank: order.IndexOf(theirIds[i]))).ToList();
                lib.Mods = ranked.OrderBy(x => x.Rank < 0 ? int.MaxValue : x.Rank).Select(x => x.Mod).ToList();
            }
            return lib;
        }

        /// <summary>Echelon's state.json, when it is there and of the version read here (2).</summary>
        static Dictionary<string, object> EchelonState(string stateFile)
        {
            try
            {
                if (!File.Exists(stateFile)) return null;
                var s = Store.ReadJson(stateFile) as Dictionary<string, object>;
                return s != null && Json.Long(s, "version") == 2 ? s : null;
            }
            catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException || e is InvalidCastException)
            {
                return null;
            }
        }

        /// <summary>The mod ids of Echelon's active profile, top first; null when there is none.</summary>
        static List<string> EchelonOrder(Dictionary<string, object> state)
        {
            var want = Json.Str(state, "active_profile") ?? Json.Str(state, "deployed_profile");
            var profiles = Json.List(state, "profiles").OfType<Dictionary<string, object>>().ToList();
            var p = profiles.FirstOrDefault(x => Json.Str(x, "id") == want) ?? (profiles.Count == 1 ? profiles[0] : null);
            if (p == null) return null;
            var ids = Json.List(p, "entries").OfType<Dictionary<string, object>>()
                          .Where(e => Json.Str(e, "type") == "mod")      // not the separators
                          .Select(e => Json.Str(e, "id")).Where(x => !string.IsNullOrEmpty(x)).ToList();
            return ids.Count > 0 ? ids : null;
        }

        /// <summary>A picture Echelon names: a file in its images folder (the mod's own picture is
        /// there, seen on 0.37.4), else in its icons folder, else a full path; null when none is
        /// there.</summary>
        static string EchelonPicture(string dir, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
                foreach (var sub in new[] { "images", "icons" })
                {
                    var f = Path.Combine(dir, sub, name);
                    if (File.Exists(f)) return f;
                }
            }
            catch (ArgumentException) { }       // a name with characters no path can hold
            return null;
        }

        /// <summary>Pictures another manager keeps outside the mod's folder, copied into this
        /// program's own pictures folder when the mod is added, so they stay when that manager
        /// is gone: the mod's (found the way a site's picture is, Site.PictureOf) and its
        /// options'. A picture that cannot be copied is left out; the mod comes in all the same.</summary>
        public static void CopyPictures(Mod m)
        {
            string Copy(string from, string name)
            {
                if (string.IsNullOrEmpty(from) || !File.Exists(from)) return null;
                try
                {
                    var into = Path.Combine(Store.Home, "pictures");
                    Directory.CreateDirectory(into);
                    var to = Path.Combine(into, name + Path.GetExtension(from).ToLowerInvariant());
                    File.Copy(from, to, true);
                    return to;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
            }
            Copy(m.ImportPicture, m.Id);
            m.ImportPicture = null;
            int n = 0;
            foreach (var o in m.Layout)
            {
                o.Image = Copy(o.Image, m.Id + "-" + ++n);
                foreach (var s in o.SubOptions) s.Image = Copy(s.Image, m.Id + "-" + ++n);
            }
        }

        /// <summary>HD2 Arsenal's profiles, every one of them (the user, 2026-10-25: all of a
        /// manager's profiles come in, not only the one in use): each read as the import reads
        /// the one in use, so its order turns round when Arsenal gives its first mod the highest
        /// priority.</summary>
        public static ImportedProfiles ArsenalProfiles(string path = null)
        {
            path = path ?? Store.ArsenalFile();
            if (path == null) throw new ModError("HD2 Arsenal's hd2a_data.json was not found", "no_source");
            var d = Store.ReadJson(path) as Dictionary<string, object> ?? new Dictionary<string, object>();
            var list = Json.Obj(d, "modsList") ?? new Dictionary<string, object>();
            var keys = Json.List(d, "profileOrder").OfType<string>().Concat(list.Keys).Distinct().Where(list.ContainsKey).ToList();
            var inUseKey = Json.Str(d, "deployedProfile") ?? Json.Str(d, "selectedProfile");
            var result = new ImportedProfiles { All = new Library { ImportedFrom = "HD2 Arsenal" } };
            var byPath = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                var lib = Ops.ImportArsenal(path, key).Lib;
                var name = Json.Str(Json.Obj(list, key), "label") ?? key;
                var one = new ImportedProfile { Name = name };
                foreach (var m in lib.Mods)
                {
                    var k = Paths.Abs(m.Path).TrimEnd('\\');
                    if (!byPath.TryGetValue(k, out var b)) { byPath[k] = b = m; result.All.Mods.Add(m); }
                    one.Mods.Add((b, m.Enabled, m.Options));
                }
                result.Profiles.Add(one);
                if (key == inUseKey) result.InUse = name;
            }
            return result;
        }

        /// <summary>Echelon's profiles: each one's order and on/off from its own state.json; the
        /// option choices are the ones library.json has, the profile in use's (no other profile's
        /// were seen in its files, 2026-10-25). Null when state.json cannot be read.</summary>
        public static ImportedProfiles EchelonProfiles(string file = null)
        {
            file = file ?? EchelonFile();
            var all = ReadEchelon(file);
            var state = EchelonState(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)), "state.json"));
            if (state == null) return null;
            var byKey = new Dictionary<string, Mod>();
            foreach (var m in all.Mods) if (m.ImportKey != null && !byKey.ContainsKey(m.ImportKey)) byKey[m.ImportKey] = m;
            var result = new ImportedProfiles { All = all };
            var inUseId = Json.Str(state, "active_profile") ?? Json.Str(state, "deployed_profile");
            foreach (var p in Json.List(state, "profiles").OfType<Dictionary<string, object>>())
            {
                var one = new ImportedProfile { Name = Json.Str(p, "name") ?? Json.Str(p, "id") ?? "Echelon" };
                foreach (var e in Json.List(p, "entries").OfType<Dictionary<string, object>>().Where(e => Json.Str(e, "type") == "mod"))
                    if (byKey.TryGetValue(Json.Str(e, "id") ?? "", out var m)) one.Mods.Add((m, Json.Bool(e, "enabled", true), m.Options));
                result.Profiles.Add(one);
                if (Json.Str(p, "id") == inUseId) result.InUse = one.Name;
            }
            return result.Profiles.Count > 0 ? result : null;
        }

        /// <summary>The library's mod an imported one is: the same folder, manifest Guid or name
        /// (as Merge decides), else null.</summary>
        static Mod Match(Library lib, Mod m)
        {
            string GuidOf(string folder)
            {
                try { return Directory.Exists(folder) ? Manifest.Read(folder)?.Guid : null; }
                catch (ModError) { return null; }
            }
            var path = Paths.Abs(m.Path).TrimEnd('\\');
            var hit = lib.Mods.FirstOrDefault(x => string.Equals(Paths.Abs(x.Path).TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            var g = GuidOf(m.Path);
            if (!string.IsNullOrEmpty(g)) hit = lib.Mods.FirstOrDefault(x => string.Equals(GuidOf(x.Path), g, StringComparison.OrdinalIgnoreCase));
            return hit ?? lib.Mods.FirstOrDefault(x => string.Equals(x.Name, m.Name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Another manager's profiles into the library: their mods merged (none twice),
        /// then each profile made one of ours, with its mods, order, on/off and options, and the
        /// one in use there put in use here.</summary>
        public static (List<Mod> Added, List<Mod> Skipped, List<Profile> Made) MergeWithProfiles(Library into, ImportedProfiles imp)
        {
            var (added, skipped) = Merge(into, imp.All);
            var addedSet = new HashSet<Mod>(added);
            var mine = new Dictionary<Mod, Mod>();
            foreach (var m in imp.All.Mods) mine[m] = addedSet.Contains(m) ? m : Match(into, m);
            var made = new List<Profile>();
            foreach (var ip in imp.Profiles)
            {
                var p = new Profile { Name = ip.Name };
                foreach (var (m, on, options) in ip.Mods)
                {
                    if (!mine.TryGetValue(m, out var lm) || lm == null || p.Order.Contains(lm.Id)) continue;
                    p.Order.Add(lm.Id);
                    if (on) p.On.Add(lm.Id);
                    p.Options[lm.Id] = options.Select(o => new OptionChoice
                    {
                        Name = o.Name, Enabled = o.Enabled,
                        Subs = o.Subs.Select(s => new SubChoice { Name = s.Name, Enabled = s.Enabled }).ToList(),
                    }).ToList();
                }
                made.Add(p);
            }
            Profiles.AddImported(into, made, imp.InUse, imp.All.ImportedFrom);
            return (added, skipped, made);
        }

        /// <summary>The managers found, with how many mods each lists.</summary>
        public static List<ImportSource> Found()
        {
            var out_ = new List<ImportSource>();
            if (Store.ArsenalFile() != null)
            {
                try
                {
                    var names = ArsenalProfiles().Profiles.Select(p => p.Name).ToList();
                    out_.Add(new ImportSource { Key = "arsenal", Name = "HD2 Arsenal", Where = Store.ArsenalModsDir() ?? Store.ArsenalFile(), Mods = Ops.ImportArsenal().Lib.Mods.Count,
                                                ProfileNames = names.Count > 1 ? names : new List<string>() });
                }
                // found and not read: said, never left out as if it were not there (the user, 2026-10-26)
                catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException || e is InvalidCastException)
                { out_.Add(ImportSource.Unreadable("arsenal", "HD2 Arsenal", Store.ArsenalFile(), e)); }
            }
            var mm = Hd2mmDir();
            if (mm != null)
            {
                try { out_.Add(new ImportSource { Key = "hd2mm", Name = "HD2ModManager", Where = Path.Combine(mm, "Mods"), Mods = ReadHd2mm(mm).Mods.Count }); }
                catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException || e is InvalidCastException)
                { out_.Add(ImportSource.Unreadable("hd2mm", "HD2ModManager", Path.Combine(mm, "enabled.json"), e)); }
            }
            var ec = EchelonFile();
            if (ec != null)
            {
                try
                {
                    var theirs = ReadEchelon(ec);
                    var where = theirs.Mods.Count > 0 ? Path.GetDirectoryName(theirs.Mods[0].Path.TrimEnd('\\')) ?? ec : ec;
                    var names = EchelonProfiles(ec)?.Profiles.Select(p => p.Name).ToList() ?? new List<string>();
                    out_.Add(new ImportSource { Key = "echelon", Name = "Echelon", Where = where, Mods = theirs.Mods.Count,
                                                ProfileNames = names.Count > 1 ? names : new List<string>() });
                }
                catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException || e is InvalidCastException)
                { out_.Add(ImportSource.Unreadable("echelon", "Echelon", ec, e)); }
            }
            return out_.Where(x => x.Mods > 0 || x.Problem != null).ToList();
        }

        /// <summary>HD2ModManager's list: its Mods\ folders in its enabled.json's order (those it does
        /// not list after them, off), each with its on/off and choices: Toggled[i] is option i on,
        /// Selected[i] the sub-option chosen in it (for folder-name options, Selected[0] the folder).</summary>
        public static Library ReadHd2mm(string dir = null)
        {
            dir = dir ?? Hd2mmDir();
            if (dir == null) throw new ModError("HD2ModManager's folder was not found", "no_source");
            var states = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            var enabledFile = Path.Combine(dir, "enabled.json");
            if (File.Exists(enabledFile))
                foreach (var e in (Store.ReadJson(enabledFile) as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>())
                {
                    var g = Json.Str(e, "Guid");
                    if (string.IsNullOrEmpty(g) || states.ContainsKey(g)) continue;
                    states[g] = e;
                    order.Add(g);
                }
            var mods = new List<(int Rank, Mod Mod)>();
            foreach (var folder in Directory.EnumerateDirectories(Path.Combine(dir, "Mods")).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                Manifest man;
                try { man = Manifest.Read(folder); }
                catch (ModError) { continue; }
                states.TryGetValue(man?.Guid ?? "", out var st);
                var toggled = Json.List(st, "Toggled");
                var selected = Json.List(st, "Selected").Select(x => x is long n ? (int)n : 0).ToList();
                var choices = new List<OptionChoice>();
                for (int i = 0; man != null && i < man.Options.Count; i++)
                {
                    var o = man.Options[i];
                    int pick = man.FolderOptions ? (selected.Count > 0 ? selected[0] : 0) : (i < selected.Count ? selected[i] : 0);
                    choices.Add(new OptionChoice
                    {
                        Name = o.Name,
                        Enabled = man.FolderOptions || i >= toggled.Count || !(toggled[i] is bool b) || b,
                        Subs = o.SubOptions.Select((x, j) => new SubChoice { Name = x.Name, Enabled = j == pick }).ToList(),
                    });
                }
                var mod = new Mod
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = !string.IsNullOrEmpty(man?.Name) ? man.Name : Path.GetFileName(folder),
                    Path = folder, Source = folder, Enabled = Json.Bool(st, "Enabled"), Options = choices,
                };
                int rank = man?.Guid != null && order.Contains(man.Guid) ? order.IndexOf(man.Guid) : int.MaxValue;
                mods.Add((rank, mod));
            }
            var lib = new Library { ImportedFrom = "HD2ModManager" };
            lib.Mods.AddRange(mods.OrderBy(x => x.Rank).Select(x => x.Mod));
            return lib;
        }

        /// <summary>How many pairs of mods from different sources ship the same resources with
        /// different bytes, so one of them is overridden. group says where each mod came from
        /// (by id; a mod not in it was in the library before). Mods from different managers were
        /// never deployed together, so nothing says which should win: the one lower in the list
        /// does, the one imported later (the user, 2026-10-25). 0 when the plan cannot be read.</summary>
        public static int CrossClashes(Library lib, Dictionary<string, int> group)
        {
            List<Deploy.Clash> found;
            try { found = Deploy.Clashes(Deploy.Plan(lib)); }
            catch (Exception e) when (e is ModError || e is IOException || e is UnauthorizedAccessException) { return 0; }
            var groupOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in lib.Mods) groupOf[m.Name] = group.TryGetValue(m.Id ?? "", out var g) ? g : 0;
            var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in found.Where(c => !c.Same))
                for (int i = 0; i < c.Mods.Count; i++)
                    for (int j = i + 1; j < c.Mods.Count; j++)
                    {
                        groupOf.TryGetValue(c.Mods[i], out var a);
                        groupOf.TryGetValue(c.Mods[j], out var b);
                        if (a != b) pairs.Add(string.Compare(c.Mods[i], c.Mods[j], StringComparison.OrdinalIgnoreCase) < 0
                                              ? c.Mods[i] + "\u001f" + c.Mods[j] : c.Mods[j] + "\u001f" + c.Mods[i]);
                    }
            return pairs.Count;
        }

        /// <summary>The mods of from that are not in into yet, added at its bottom in from's order;
        /// the ones already there (same folder, manifest Guid or name) are named in Skipped. A mod
        /// that comes with an id the library already uses is given a new one: another manager's
        /// ids are its own, and two mods with one id confuse everything that goes by id.</summary>
        public static (List<Mod> Added, List<Mod> Skipped) Merge(Library into, Library from)
        {
            string GuidOf(string folder)
            {
                try { return Directory.Exists(folder) ? Manifest.Read(folder)?.Guid : null; }
                catch (ModError) { return null; }
            }
            var ids = new HashSet<string>(into.Mods.Select(m => m.Id ?? ""), StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(into.Mods.Select(m => Paths.Abs(m.Path).TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(into.Mods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            var guids = new HashSet<string>(into.Mods.Select(m => GuidOf(m.Path)).Where(g => !string.IsNullOrEmpty(g)), StringComparer.OrdinalIgnoreCase);
            var added = new List<Mod>();
            var skipped = new List<Mod>();
            foreach (var m in from.Mods)
            {
                var g = GuidOf(m.Path);
                if (paths.Contains(Paths.Abs(m.Path).TrimEnd('\\')) || names.Contains(m.Name) || (g != null && guids.Contains(g))) { skipped.Add(m); continue; }
                if (string.IsNullOrEmpty(m.Id) || !ids.Add(m.Id)) { m.Id = Guid.NewGuid().ToString(); ids.Add(m.Id); }
                into.Mods.Add(m);
                added.Add(m);
                paths.Add(Paths.Abs(m.Path).TrimEnd('\\'));
                names.Add(m.Name);
                if (g != null) guids.Add(g);
            }
            if (added.Count > 0)
                into.ImportedFrom = string.IsNullOrEmpty(into.ImportedFrom) ? from.ImportedFrom
                                  : into.ImportedFrom.Contains(from.ImportedFrom) ? into.ImportedFrom : into.ImportedFrom + "; " + from.ImportedFrom;
            return (added, skipped);
        }
    }
}
