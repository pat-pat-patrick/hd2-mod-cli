using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Hd2Mod
{
    /// <summary>Versioned, hash-pinned compositions of native configuration mods. Each mod
    /// keeps its switch/options; each target has exactly one validated output. No decryption
    /// keys or executable recipe commands are accepted by this module.</summary>
    public static class NativeConfigs
    {
        const string Recipes = "native-configs.json", State = "native-config-deployed.json";
        internal static Action<string> BeforeReplace { get; set; }
        static readonly Dictionary<string, (long Size, long Time, (uint, ulong)? Id, string Hash)> hashes = new Dictionary<string, (long, long, (uint, ulong)?, string)>(StringComparer.OrdinalIgnoreCase);

        public sealed class Item
        {
            public string Target, Source, Hash, Current, Baseline;
            public List<string> Mods = new List<string>();
            public List<string> ModIds = new List<string>();
            public bool TrackingChanged;
            public bool Changed => Current != Hash;
            public bool Restore => ModIds.Count == 0;
        }

        public sealed class Plan
        {
            public string Game;
            public List<Item> Items = new List<Item>();
            public Dictionary<string, List<string>> ModTargets = new Dictionary<string, List<string>>();
            public string LegacyBefore, LegacyAfter;
            public int Changes => Items.Count(x => x.Changed || x.TrackingChanged);
            public bool LegacyChanged => LegacyBefore != LegacyAfter;
            public int ChangesFor(string id) => Items.Count(x => (x.Changed || x.TrackingChanged) && (x.ModIds.Contains(id) || ModTargets.TryGetValue(id, out var ts) && ts.Contains(x.Target)));

            public void Apply()
            {
                if (Items.Count == 0) return;
                var staged = new List<(Item Item, string Destination, string Temp, string Backup)>();
                var committed = new List<(Item Item, string Destination, string Temp, string Backup)>();
                var oldState = Store.Load(State);
                bool legacyWritten = false;
                string legacy = Paths.Join(Game, "HD2FileMods.state");
                try
                {
                    // Prepare every replacement and rollback copy before changing any target.
                    foreach (var item in Items)
                    {
                        var dst = Under(Paths.Join(Game, "data"), item.Target);
                        if (HashOf(dst, true) != item.Current) throw Refuse(item.Target, "changed during deployment");
                        if (!item.Changed) continue;
                        var temp = dst + ".hd2qd-native-" + Guid.NewGuid().ToString("N");
                        var backup = temp + ".rollback";
                        staged.Add((item, dst, temp, backup));
                        File.Copy(dst, backup);
                        File.Copy(item.Source, temp);
                        if (HashOf(temp) != item.Hash) throw Refuse(item.Target, "composition checksum failed");
                    }
                    if ((File.Exists(legacy) ? File.ReadAllText(legacy) : null) != LegacyBefore)
                        throw Refuse("HD2FileMods.state", "changed during deployment");
                    foreach (var entry in staged)
                    {
                        BeforeReplace?.Invoke(entry.Item.Target);
                        if (HashOf(entry.Destination, true) != entry.Item.Current) throw Refuse(entry.Item.Target, "changed during deployment");
                        Native.Replace(entry.Temp, entry.Destination);
                        committed.Add(entry);
                    }
                    if (LegacyChanged)
                    {
                        ReplaceText(legacy, LegacyAfter ?? "");
                        legacyWritten = true;
                    }
                    var targets = new Dictionary<string, object>(Json.Obj(oldState, "targets") ?? new Dictionary<string, object>());
                    foreach (var item in Items)
                    {
                        if (item.Restore) targets.Remove(item.Target);
                        else targets[item.Target] = new Dictionary<string, object>
                        {
                            ["sha256"] = item.Hash, ["baseline"] = item.Baseline,
                            ["mods"] = item.ModIds.Cast<object>().ToList(),
                        };
                    }
                    Store.Save(State, new Dictionary<string, object> { ["version"] = 1L, ["game_dir"] = Game, ["targets"] = targets });
                }
                catch
                {
                    foreach (var entry in committed.AsEnumerable().Reverse()) Native.Replace(entry.Backup, entry.Destination);
                    if (legacyWritten) ReplaceText(legacy, LegacyBefore ?? "");
                    throw;
                }
                finally
                {
                    foreach (var entry in staged)
                    {
                        if (File.Exists(entry.Temp)) File.Delete(entry.Temp);
                        if (File.Exists(entry.Backup)) File.Delete(entry.Backup);
                    }
                }
            }
        }

        static ModError Refuse(string target, string reason) =>
            new ModError("Native configuration refused: " + target + " (" + reason + "). No unknown configuration is overwritten.", "native_config", target);

        static void ReplaceText(string path, string text)
        {
            var tmp = path + ".hd2qd-native-" + Guid.NewGuid().ToString("N");
            try { File.WriteAllText(tmp, text, new UTF8Encoding(false)); Native.Replace(tmp, path); }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        static string Under(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 ||
                relative.Replace('\\', '/').Split('/').Any(x => x == "." || x == ".." || x.Length == 0))
                throw Refuse(relative ?? "", "unsafe recipe path");
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw Refuse(relative, "recipe escapes its folder");
            return path;
        }

        public static string HashOf(string path, bool verify = false)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) throw Refuse(path, "file is missing");
            var identity = Native.FileId(path);
            lock (hashes)
            {
                if (!verify && identity != null && hashes.TryGetValue(fi.FullName, out var known) && known.Size == fi.Length && known.Time == fi.LastWriteTimeUtc.Ticks && Equals(known.Id, identity)) return known.Hash;
            }
            string hash;
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path)) hash = string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2")));
            lock (hashes) hashes[fi.FullName] = (fi.Length, fi.LastWriteTimeUtc.Ticks, identity, hash);
            return hash;
        }

        static List<Dictionary<string, object>> Targets()
        {
            var recipe = Store.Load(Recipes);
            if (recipe.Count == 0) return new List<Dictionary<string, object>>();
            if (Json.Long(recipe, "version") != 1) throw Refuse(Recipes, "unsupported recipe version");
            var targets = Json.List(recipe, "targets").OfType<Dictionary<string, object>>().ToList();
            if (targets.Select(x => Json.Str(x, "target")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
                throw Refuse(Recipes, "duplicate target");
            foreach (var t in targets)
            {
                var target = Json.Str(t, "target");
                if (target == null || !target.StartsWith("game/", StringComparison.Ordinal) || !target.EndsWith(".dl_bin", StringComparison.Ordinal))
                    throw Refuse(target ?? "", "unsupported target");
                Under(Store.Home, target); // lexical validation only
            }
            return targets;
        }

        static List<Dictionary<string, object>> Features(Dictionary<string, object> target) =>
            Json.List(target, "features").OfType<Dictionary<string, object>>().ToList();

        static Mod FindMod(Library lib, Dictionary<string, object> feature)
        {
            var guid = Json.Str(feature, "manifest_guid");
            var matches = lib.Mods.Where(m => m.Id == Json.Str(feature, "mod_id") ||
                guid != null && string.Equals(Manifest.Of(m)?.Guid, guid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1) throw Refuse(Json.Str(feature, "id"), "multiple copies of the same native mod are installed");
            return matches.SingleOrDefault();
        }

        public static HashSet<string> ManagedIds(Library lib) => new HashSet<string>(Targets().SelectMany(Features).Select(f => FindMod(lib, f)?.Id).Where(id => id != null));

        static string VariantKey(IEnumerable<string> features) => string.Join("+", features.OrderBy(x => x, StringComparer.Ordinal));

        static bool On(Mod mod, Dictionary<string, object> feature)
        {
            if (mod == null || !mod.Enabled) return false;
            var payload = Under(mod.Path, Json.Str(feature, "payload"));
            return Deploy.IncludedFolders(mod).Any(folder => string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.GetDirectoryName(payload), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Remove only hash-pinned file-mod payloads and their obsolete installers.
        /// Unknown payloads for a managed target are refused, never silently dropped.</summary>
        public static void Filter(Library lib, List<Wanted> wanted)
        {
            var recipes = Targets();
            if (recipes.Count == 0) return;
            var suppress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in recipes.SelectMany(Features))
            {
                var mod = FindMod(lib, f);
                if (mod == null) continue;
                foreach (var archive in Json.List(f, "archives").OfType<Dictionary<string, object>>())
                {
                    var p = Under(mod.Path, Json.Str(archive, "path"));
                    if (!wanted.Any(w => string.Equals(w.Src, p, StringComparison.OrdinalIgnoreCase))) continue;
                    if (HashOf(p) != Json.Str(archive, "sha256")) throw Refuse(mod.Name, "mod version changed; its composition recipe must be updated");
                    suppress.Add(p);
                    foreach (var suffix in Deploy.Companions) suppress.Add(p + suffix);
                }
            }
            var ownedTargets = new HashSet<string>(recipes.Select(r => "data/" + Json.Str(r, "target")), StringComparer.OrdinalIgnoreCase);
            var managedNames = new HashSet<string>(recipes.SelectMany(Features).Select(f => FindMod(lib, f)?.Name).Where(n => n != null));
            foreach (var w in wanted.Where(w => Deploy.Patch.IsMatch(w.Name) && !suppress.Contains(w.Src)))
            {
                if (managedNames.Contains(w.Mod)) throw Refuse(w.Mod, "an unregistered archive was added to this native mod");
                foreach (var target in PayloadTargets(w.Src))
                    if (ownedTargets.Contains(target)) throw Refuse(target, "another file mod is not covered by the registered compositions: " + w.Mod);
            }
            wanted.RemoveAll(w => suppress.Contains(w.Src));
        }

        static IEnumerable<string> PayloadTargets(string path)
        {
            if (!Deploy.Types(path).Contains(Deploy.LuaType)) yield break;
            using (var f = File.OpenRead(path))
            foreach (var row in Deploy.Rows(path).Where(r => r.Type == Deploy.LuaType && r.Size >= 18))
            {
                f.Seek((long)row.Off + 8, SeekOrigin.Begin);
                var prefix = new byte[10];
                if (Deploy.Fill(f, prefix) != 10 || Encoding.ASCII.GetString(prefix, 0, 6) != "HD2FM1") continue;
                uint n = BitConverter.ToUInt32(prefix, 6);
                if (n > 65536 || n > row.Size - 18) continue;
                var header = new byte[n];
                if (Deploy.Fill(f, header) != n) continue;
                var target = Encoding.UTF8.GetString(header).Split('\n').FirstOrDefault(x => x.StartsWith("target=", StringComparison.Ordinal));
                if (target != null) yield return target.Substring(7).TrimEnd('\r');
            }
        }

        public static Plan Prepare(Library lib, string game, bool restoreAll = false)
        {
            var plan = new Plan { Game = game };
            var recipes = Targets();
            if (recipes.Count == 0 || game == null) return plan;
            var legacy = Paths.Join(game, "HD2FileMods.state");
            plan.LegacyBefore = File.Exists(legacy) ? File.ReadAllText(legacy) : null;
            plan.LegacyAfter = plan.LegacyBefore;
            var state = Store.Load(State);
            var owned = Json.Obj(state, "targets") ?? new Dictionary<string, object>();
            if (owned.Count > 0 && !string.Equals(Path.GetFullPath(Json.Str(state, "game_dir") ?? ""), Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase))
                throw Refuse(State, "tracked files belong to another game folder");
            foreach (var target in recipes)
            {
                var path = Json.Str(target, "target");
                var features = Features(target);
                var active = new List<Dictionary<string, object>>();
                foreach (var feature in features)
                {
                    var mod = FindMod(lib, feature);
                    var id = mod?.Id ?? Json.Str(feature, "mod_id");
                    if (!plan.ModTargets.TryGetValue(id, out var list)) plan.ModTargets[id] = list = new List<string>();
                    list.Add(path);
                    if (!restoreAll && On(mod, feature)) active.Add(feature);
                }
                bool legacyOwned = (plan.LegacyBefore ?? "").Split('\n').Any(l => l.StartsWith("data/" + path + "|", StringComparison.OrdinalIgnoreCase));
                bool purgingActive = restoreAll && features.Any(f => On(FindMod(lib, f), f));
                if (active.Count == 0 && !owned.ContainsKey(path) && !legacyOwned && !purgingActive) continue;
                var variants = Json.List(target, "variants").OfType<Dictionary<string, object>>().ToList();
                var key = VariantKey(active.Select(f => Json.Str(f, "id")));
                var selected = variants.Where(v => VariantKey(Json.List(v, "features").OfType<string>()) == key).ToList();
                var baseline = variants.Where(v => Json.List(v, "features").Count == 0).ToList();
                if (selected.Count != 1 || baseline.Count != 1) throw Refuse(path, "composition is missing or ambiguous: " + key);
                var source = Json.Str(selected[0], "file");
                var hash = Json.Str(selected[0], "sha256");
                if (source == null || HashOf(source) != hash) throw Refuse(path, "composition file was changed or is missing");
                var baseFile = Json.Str(baseline[0], "file");
                var baseHash = Json.Str(baseline[0], "sha256");
                if (baseFile == null || HashOf(baseFile) != baseHash) throw Refuse(path, "original baseline was changed or is missing");
                var current = HashOf(Under(Paths.Join(game, "data"), path), true);
                if (!variants.Any(v => Json.Str(v, "sha256") == current)) throw Refuse(path, "game version mismatch or external modification");
                var activeIds = active.Select(f => FindMod(lib, f).Id).ToList();
                var prior = Json.Obj(owned, path);
                bool tracking = active.Count == 0 ? owned.ContainsKey(path) : prior == null || Json.Str(prior, "sha256") != hash ||
                    Json.Str(prior, "baseline") != baseHash || !Json.List(prior, "mods").OfType<string>().SequenceEqual(activeIds);
                plan.Items.Add(new Item { Target = path, Source = source, Hash = hash, Current = current, Baseline = baseHash, TrackingChanged = tracking,
                    Mods = active.Select(f => FindMod(lib, f).Name).ToList(),
                    ModIds = activeIds });
            }
            if (plan.LegacyBefore != null)
            {
                var controlled = new HashSet<string>(plan.Items.Select(i => "data/" + i.Target), StringComparer.OrdinalIgnoreCase);
                var lines = plan.LegacyBefore.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Any(l => controlled.Contains(l.Split('|')[0].TrimEnd('\r'))))
                    plan.LegacyAfter = string.Join("\n", lines.Where(l => !controlled.Contains(l.Split('|')[0].TrimEnd('\r'))).Select(l => l.TrimEnd('\r')));
            }
            return plan;
        }
    }
}
