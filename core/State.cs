using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Mod
{
    public static class StateDoc
    {
        public const string Version = "0.11.0";
        /// <summary>How many of a conflict's resources the state names; the count says how many there are.</summary>
        const int Shown = 30;

        /// <summary>Patch files (not companions) and bytes (with companions) in folders, not recursive.
        /// The sizes come with the folder's listing: asking each file for its own took 60 ms of
        /// every state read (the user, 2026-10-26: the window's start was slow).</summary>
        static (long Files, long Bytes) FolderStats(IEnumerable<string> folders)
        {
            long files = 0, size = 0;
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var fi in new DirectoryInfo(folder).EnumerateFiles()) sizes[fi.Name] = fi.Length;
                foreach (var kv in sizes)
                {
                    if (!Deploy.Patch.IsMatch(kv.Key)) continue;
                    files++;
                    size += kv.Value;
                    foreach (var c in Deploy.Companions) if (sizes.TryGetValue(kv.Key + c, out var cs)) size += cs;
                }
            }
            return (files, size);
        }

        /// <summary>An image for a mod or an option: the manifest's path, else icon.png in its
        /// folder or its first included folder.</summary>
        /// <summary>The names a mod's own picture goes by, besides the one its manifest gives:
        /// mods are packed by hand and the file is called all sorts of things (the user's tester,
        /// 2026-10-25: a mod with a picture showed none here).</summary>
        static readonly string[] PictureNames = { "icon", "cover", "preview", "thumbnail", "thumb", "image", "logo" };
        static readonly string[] PictureKinds = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

        /// <summary>A deploy another program is in the middle of, for the window's own line.</summary>
        static Dictionary<string, object> Deploying()
        {
            var busy = Deploy.Running();
            return busy == null ? null : new Dictionary<string, object>
            {
                ["pid"] = (long)busy.Pid, ["since"] = busy.Since.ToLocalTime().ToString("HH:mm:ss"), ["game"] = busy.Game,
            };
        }

        /// <summary>Whether the game folder is exactly what the library asks for, for the
        /// window's own line: names and file ids only, no bytes read.</summary>
        static Dictionary<string, object> CheckBlock(Library lib, string game, Diff plan)
        {
            var c = Deploy.Check(lib, game, plan);
            return new Dictionary<string, object>
            {
                ["ok"] = c.Ok, ["checked"] = (long)c.Checked, ["copies"] = (long)c.Copies,
                ["missing"] = c.Missing.Take(40).Cast<object>().ToList(),
                ["extra"] = c.Extra.Take(40).Cast<object>().ToList(),
                ["changed"] = c.Changed.Take(40).Cast<object>().ToList(),
                ["missing_n"] = (long)c.Missing.Count, ["extra_n"] = (long)c.Extra.Count, ["changed_n"] = (long)c.Changed.Count,
                ["wrong"] = c.Wrong.Take(40).Cast<object>().ToList(), ["wrong_n"] = (long)c.Wrong.Count,
            };
        }

        static string Picture(string folder, string rel, List<string> includes)
        {
            // what the manifest says, whatever it is called
            if (!string.IsNullOrEmpty(rel))
            {
                var said = Paths.Norm(Paths.Join(folder, rel));
                if (File.Exists(said)) return said;
            }
            // an option's own folder first when there is one, then the mod's folder
            var where = new List<string>();
            if (includes != null && includes.Count > 0) where.Add(Paths.Join(folder, includes[0]));
            else where.Add(folder);
            foreach (var dir in where)
            {
                var found = InFolder(dir);
                if (found != null) return found;
            }
            // one step down, for a mod packed as a single folder inside its own
            if (includes == null || includes.Count == 0)
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(8))
                    {
                        var found = InFolder(sub);
                        if (found != null) return found;
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return null;
        }

        /// <summary>A picture in one folder: one of the usual names first, else the only image
        /// there when there is just one.</summary>
        static string InFolder(string dir)
        {
            List<string> images;
            try
            {
                if (!Directory.Exists(dir)) return null;
                images = Directory.EnumerateFiles(dir)
                    .Where(f => PictureKinds.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
            if (images.Count == 0) return null;
            foreach (var name in PictureNames)
            {
                var hit = images.FirstOrDefault(f => string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return Paths.Norm(hit);
            }
            return images.Count == 1 ? Paths.Norm(images[0]) : null;
        }

        // a folder's answer, kept while its patch files (names, sizes, times) stay the same:
        // opening every archive on each state read added 0.1 s to it
        static readonly Dictionary<string, (string Sig, bool Lua, bool Loader)> luaKnown =
            new Dictionary<string, (string, bool, bool)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether any patch archive under a mod's folder (any option) holds a Lua script,
        /// and whether any of them replaces a script a loader replaces. Only an archive that holds
        /// Lua at all is read row by row.</summary>
        static long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

        /// <summary>The mods' Lua before a deploy (LuaMods): each mod's facts go into its own
        /// entry as "lua_info"; the load order's into the block returned.</summary>
        static Dictionary<string, object> LuaBlock(Library lib, List<object> mods)
        {
            List<ModLua> all;
            int missing;
            try { all = LuaMods.Of(lib, LuaMods.Lazy, out missing); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
            // some archives not read yet (the window reads them in the background): nothing
            // half known is said
            if (missing > 0) return new Dictionary<string, object> { ["pending"] = true };
            var (owner, boots, running, issues) = LuaMods.Check(lib, all);
            var outcomes = LuaMods.Outcomes(lib, all);
            var session = LuaMods.LastSession();
            var byId = all.ToDictionary(x => x.Mod.Id);
            foreach (var entry in mods.OfType<Dictionary<string, object>>())
            {
                if (!byId.TryGetValue((string)entry["id"], out var ml)) continue;
                var addons = ml.Addons.ToList();
                var last = session?.Mods.LastOrDefault(x => addons.Contains(x.Name));
                entry["lua_info"] = new Dictionary<string, object>
                {
                    ["needs"] = ml.Needs, ["needs_why"] = ml.NeedsWhy, ["loader"] = ml.Loader, ["loader_version"] = ml.LoaderVersion, ["boot"] = ml.Boot?.BootKind,
                    ["scripts"] = addons.Cast<object>().ToList(),
                    ["broken"] = ml.Broken.Select(b => (object)new Dictionary<string, object>
                    {
                        ["script"] = b.Name ?? (b.Id == LuaMods.BootId ? "boot" : b.Id.ToString("x16")),
                        ["line"] = (long)(b.Error?.Line ?? 0), ["message"] = b.Error?.Message, ["error_kind"] = b.ErrorKind,
                        ["jit"] = b.Jit.Cast<object>().ToList(), ["luajit_bytecode"] = b.LuaJitBytecode,
                    }).ToList(),
                    ["pack_of"] = ml.PackOf?.Id, ["pack_of_name"] = ml.PackOfName, ["guest"] = ml.Guest, ["helper"] = ml.Helper, ["chef_runs"] = ml.ChefRuns,
                    // its biggest script that would start (Chef Shared Loader reads none over 4 MB)
                    ["biggest"] = ml.Scripts.Where(s => s.Header == "Addon" || s.Header == "Boot").Select(s => s.Size).DefaultIfEmpty(0).Max(),
                    ["last"] = last == null ? null : new Dictionary<string, object> { ["state"] = last.State, ["ms"] = (long)last.Ms, ["why"] = last.Why },
                };
            }
            Dictionary<string, object> crash = null;
            var newest = LuaMods.Crashes(1).FirstOrDefault();
            if (newest.File != null)
            {
                var then = LuaMods.DeployedAt(newest.When);
                var (site, times, of) = LuaMods.SiteCount(newest.File);
                var added = LuaMods.AddedAt(newest.When);
                crash = new Dictionary<string, object>
                {
                    ["file"] = newest.File, ["when"] = Unix(newest.When),
                    ["deployed_when"] = then == null ? (object)null : Unix(then.Value.When),
                    ["mods"] = (then?.Mods ?? new List<string>()).Cast<object>().ToList(),
                    // where it happened, and whether the game crashes there all the time
                    ["site"] = site, ["site_times"] = (long)times, ["recent"] = (long)of,
                    ["common"] = site != null && times >= LuaMods.Common,
                    ["added"] = added?.Cast<object>().ToList(),
                };
            }
            return new Dictionary<string, object>
            {
                ["boot_owner"] = owner?.Mod.Id, ["boot_kind"] = owner?.Boot.BootKind, ["running"] = running,
                ["boot_mods"] = boots.Select(b => (object)b.Mod.Id).ToList(),
                ["issues"] = issues.Select(i => (object)new Dictionary<string, object>
                {
                    ["kind"] = i.Kind, ["mods"] = i.Mods.Select(m => (object)m.Id).ToList(), ["detail"] = i.Detail,
                }).ToList(),
                // under each choice of loader ("none": no loader), the mods that would not start
                ["outcomes"] = outcomes.ToDictionary(c => c.Choice?.Id ?? Loaders.None, c => (object)c.Outcomes.Select(o => (object)new Dictionary<string, object>
                {
                    ["why"] = o.Why, ["mods"] = o.Mods.Select(m => (object)m.Id).ToList(), ["over"] = o.Over?.Id,
                }).ToList()),
                ["session"] = session == null ? null : new Dictionary<string, object>
                {
                    ["loader"] = session.Loader, ["when"] = session.When == default(DateTime) ? 0L : Unix(session.When),
                    ["started"] = (long)session.StartedCount, ["total"] = (long)session.Total,
                },
                ["crash"] = crash,
            };
        }

        static (bool Lua, bool Loader) HasLua(DirectoryInfo folder)
        {
            var patches = folder.EnumerateFiles("*", SearchOption.AllDirectories).Where(f => Deploy.Patch.IsMatch(f.Name)).ToList();
            var sig = string.Join("|", patches.Select(f => f.FullName + ":" + f.Length + ":" + f.LastWriteTimeUtc.Ticks));
            lock (luaKnown)
                if (luaKnown.TryGetValue(folder.FullName, out var known) && known.Sig == sig) return (known.Lua, known.Loader);
            bool lua = false, loader = false;
            foreach (var f in patches)
            {
                try
                {
                    if (!Deploy.Types(f.FullName).Contains(Deploy.LuaType)) continue;
                    lua = true;
                    if (loader || !Deploy.HoldsLua(f.FullName, Deploy.LoaderScripts)) continue;
                    loader = true;
                    break;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            lock (luaKnown) luaKnown[folder.FullName] = (sig, lua, loader);
            return (lua, loader);
        }

        /// <summary>Every file's size under a folder, from the listing itself (no file is
        /// opened or looked up again: 1224 files took 50 ms that way, now a few).</summary>
        public static long LibraryBytes(DirectoryInfo folder)
        {
            long n = 0;
            foreach (var f in folder.EnumerateFiles()) n += f.Length;
            foreach (var d in folder.EnumerateDirectories())
                if ((d.Attributes & FileAttributes.ReparsePoint) == 0) n += LibraryBytes(d);
            return n;
        }

        static string Or(string a, string b) => string.IsNullOrEmpty(a) ? b : a;

        /// <summary>Everything a window needs to show, in one piece: each mod with its options
        /// and its patch files and what a deploy would do to each, the files a deploy would
        /// delete, the totals (files and bytes), the hard links in the game folder, and the
        /// conflicts.</summary>
        public static Dictionary<string, object> Build(Library lib, string game, bool verify = false)
        {
            var errors = new Dictionary<string, string>();
            var d = game != null ? Deploy.Compute(lib, game, verify, errors) : null;
            var data = game != null ? Paths.Join(game, "data") : null;
            var action = new Dictionary<string, (string What, string From)>();
            if (d != null)
            {
                foreach (var w in d.Keep) action[w.Name] = ("keep", null);
                foreach (var (a, b) in d.Moves) action[b] = ("move", a);
                foreach (var w in d.Writes) action[w.Name] = ("write", null);
            }
            var files = new Dictionary<string, List<Dictionary<string, object>>>();
            foreach (var w in d?.All ?? new List<Wanted>())
            {
                if (!Deploy.Patch.IsMatch(w.Name)) continue;
                if (!action.TryGetValue(w.Name, out var act)) act = ("write", null);
                var companions = Deploy.Companions.Where(c => File.Exists(w.Src + c)).ToList();
                bool linked = act.What == "keep" && Native.SameFile(Paths.Join(data, w.Name), w.Src) == true;
                if (!files.TryGetValue(w.Mod, out var mine)) files[w.Mod] = mine = new List<Dictionary<string, object>>();
                mine.Add(new Dictionary<string, object>
                {
                    ["name"] = w.Name, ["src"] = w.Src, ["action"] = act.What, ["from"] = act.From, ["companions"] = (long)companions.Count,
                    ["bytes"] = Deploy.SizeOrZero(w.Src) + companions.Sum(c => Deploy.SizeOrZero(w.Src + c)), ["linked"] = linked,
                });
            }
            var clashes = new List<object>();
            if (d != null)
            {
                // the same mods are one entry, and the resources they ship identically another:
                // those are shared, not fought over
                var pairs = new List<(List<string> Mods, bool Same, bool Boot, long Count, List<Deploy.Clash> Some)>();
                foreach (var c in Deploy.Clashes(d.All))
                {
                    bool boot = c.Type == Deploy.LuaType && c.Id == LuaMods.BootId;
                    int i = pairs.FindIndex(p => p.Same == c.Same && p.Boot == boot && p.Mods.SequenceEqual(c.Mods));
                    if (i < 0) pairs.Add((c.Mods, c.Same, boot, 1, new List<Deploy.Clash> { c }));
                    else
                    {
                        if (pairs[i].Some.Count < Shown) pairs[i].Some.Add(c);
                        pairs[i] = (pairs[i].Mods, pairs[i].Same, pairs[i].Boot, pairs[i].Count + 1, pairs[i].Some);
                    }
                }
                // which resources they are (the user, 2026-10-25): the first ones of each pair,
                // with the type and, when HD2SDK's list is installed, the name
                foreach (var p in pairs.OrderByDescending(p => p.Count))      // stable, as Python's sorted
                    clashes.Add(new Dictionary<string, object>
                    {
                        ["mods"] = p.Mods.Cast<object>().ToList(), ["count"] = p.Count, ["winner"] = p.Mods[p.Mods.Count - 1],
                        ["same"] = p.Same,
                        ["resources"] = p.Some.Select(c => (object)new Dictionary<string, object>
                        {
                            ["type"] = ResourceNames.Type(c.Type), ["id"] = c.Id.ToString("x16"), ["name"] = ResourceNames.Name(c.Id),
                        }).ToList(),
                    });
            }
            Dictionary<string, object> summary = null;
            if (d != null)
            {
                long Sz(IEnumerable<Wanted> items) => items.Sum(w => Deploy.SizeOrZero(w.Src));
                var byName = new Dictionary<string, Wanted>();
                foreach (var w in d.All) byName[w.Name] = w;
                summary = new Dictionary<string, object>
                {
                    ["kept"] = (long)d.Kept, ["moved"] = (long)d.Moves.Count, ["written"] = (long)d.Writes.Count,
                    ["deleted"] = (long)d.Deleted, ["files"] = (long)d.All.Count,
                    ["kept_bytes"] = Sz(d.Keep), ["moved_bytes"] = Sz(d.Moves.Select(m => byName[m.To])),
                    ["written_bytes"] = Sz(d.Writes),
                    ["copied_bytes"] = Sz(d.Writes.Where(w => !Deploy.SameDrive(w.Src, data))),
                    ["deleted_bytes"] = d.Deletes.Concat(d.Replaced).Sum(f => Deploy.SizeOrZero(Paths.Join(data, f))),
                };
            }
            long lf = 0, lb = 0, cf = 0, cb = 0;
            if (data != null && Directory.Exists(data))
                foreach (var f in Deploy.List(data))
                {
                    if (!Deploy.Managed.IsMatch(f)) continue;
                    var p = Paths.Join(data, f);
                    long size = Deploy.SizeOrZero(p);
                    if (Native.Links(p) > 1) { lf++; lb += size; }
                    else { cf++; cb += size; }
                }
            var links = new Dictionary<string, object> { ["files"] = lf, ["bytes"] = lb, ["copies"] = cf, ["copy_bytes"] = cb };
            var record = Store.Load("deployed.json");
            var mods = new List<object>();
            int index = 0;
            foreach (var m in lib.Mods)
            {
                index++;
                bool exists = Directory.Exists(m.Path);
                Manifest manifest = null;
                if (exists)
                {
                    try { manifest = Manifest.Of(m); }
                    catch (ModError e) { if (!errors.ContainsKey(m.Name)) errors[m.Name] = e.Message; }
                }
                var options = new List<object>();
                if (manifest != null)
                {
                    var chosen = Manifest.Merged(manifest, m.Options);
                    for (int i = 0; i < manifest.Options.Count; i++)
                    {
                        var o = manifest.Options[i];
                        var c = chosen[i];
                        var (n, b) = FolderStats(o.Include.Select(x => Paths.Join(m.Path, x)));
                        var subs = new List<object>();
                        for (int j = 0; j < o.SubOptions.Count && j < c.Subs.Count; j++)
                        {
                            var s = o.SubOptions[j];
                            var (sn, sb) = FolderStats(s.Include.Select(y => Paths.Join(m.Path, y)));
                            subs.Add(new Dictionary<string, object>
                            {
                                ["name"] = c.Subs[j].Name, ["description"] = s.Description, ["enabled"] = c.Subs[j].Enabled,
                                ["files"] = sn, ["bytes"] = sb, ["icon"] = Picture(m.Path, Or(s.IconPath, s.Image), s.Include),
                            });
                        }
                        options.Add(new Dictionary<string, object>
                        {
                            ["name"] = c.Name, ["description"] = o.Description, ["enabled"] = c.Enabled,
                            ["files"] = n, ["bytes"] = b, ["subs"] = subs,
                            ["icon"] = o.Include.Count > 0 ? Picture(m.Path, Or(o.IconPath, o.Image), o.Include) : null,
                        });
                    }
                }
                files.TryGetValue(m.Name, out var own);
                own = own ?? new List<Dictionary<string, object>>();
                errors.TryGetValue(m.Name, out var error);
                var scripts = exists ? HasLua(new DirectoryInfo(m.Path)) : (Lua: false, Loader: false);
                var loaderKind = exists ? Loaders.KindOf(m) : null;
                mods.Add(new Dictionary<string, object>
                {
                    ["index"] = (long)index, ["id"] = m.Id, ["name"] = m.Name, ["real_name"] = m.RealName, ["enabled"] = m.Enabled, ["path"] = m.Path,
                    ["in_profile"] = Profiles.InProfile(lib, m),
                    ["source"] = m.Source, ["exists"] = exists, ["manifest"] = manifest != null,
                    ["description"] = Or(manifest?.Description, m.Description) ?? "", ["options"] = options,
                    ["icon"] = (exists ? Picture(m.Path, manifest?.IconPath, null) : null) ?? Site.PictureOf(m.Id),
                    ["same_drive"] = data != null && exists && Deploy.SameDrive(m.Path, data),
                    ["files"] = own.Cast<object>().ToList(), ["deploy_bytes"] = own.Sum(f => (long)f["bytes"]),
                    ["library_bytes"] = exists ? LibraryBytes(new DirectoryInfo(m.Path)) : 0L,
                    ["lua"] = scripts.Lua, ["loader"] = loaderKind != null, ["loader_kind"] = loaderKind,
                    ["nexus_mod"] = m.Nexus?.Mod ?? 0L, ["nexus_page"] = m.Nexus != null ? Nexus.Page(m.Nexus) : null,
                    ["channel"] = Origins.Of(m).Channel, ["page"] = Origins.Of(m).Page, ["version"] = m.Nexus?.Version ?? m.GitHub?.Tag,
                    ["shown_version"] = Origins.VersionOf(m).Version, ["version_from"] = Origins.VersionOf(m).From,
                    ["site_author"] = m.Site?.Author, ["site_summary"] = m.Site?.Summary, ["site_version"] = m.Site?.Version,
                    ["tags"] = m.Tags.Cast<object>().ToList(), ["group"] = m.Group,
                    ["auto_tags"] = (manifest?.Tags ?? new List<string>()).Cast<object>().ToList(),
                    ["error"] = error,
                });
            }
            var deletes = d != null ? d.Deletes.Concat(d.Replaced).ToList() : new List<string>();
            deletes.Sort(string.CompareOrdinal);
            var luaCheck = LuaBlock(lib, mods);
            // A coordinated start-up slot is still described, but is not an unresolved
            // resource conflict. Keep it separate from real conflicts between these mods.
            if (luaCheck != null && !Json.Bool(luaCheck, "pending"))
            {
                var owner = Json.Str(luaCheck, "boot_owner");
                var entries = mods.OfType<Dictionary<string, object>>().ToList();
                foreach (var c in clashes.OfType<Dictionary<string, object>>())
                {
                    var resources = Json.List(c, "resources").OfType<Dictionary<string, object>>().ToList();
                    if (resources.Count != 1 || Json.Str(resources[0], "type") != "lua" ||
                        Json.Str(resources[0], "id") != LuaMods.BootId.ToString("x16")) continue;
                    var names = Json.List(c, "mods").OfType<string>().ToList();
                    bool coordinated = owner != null && names.Count > 1 && names.All(name =>
                    {
                        var m = entries.FirstOrDefault(x => Json.Str(x, "name") == name);
                        if (m == null) return false;
                        if (Json.Str(m, "id") == owner) return true;
                        var info = Json.Obj(m, "lua_info");
                        return info != null && (Json.Bool(info, "chef_runs") || Json.Bool(info, "guest") || Json.Bool(info, "helper"));
                    });
                    if (coordinated) c["managed_boot"] = true;
                }
            }
            return new Dictionary<string, object>
            {
                ["tool"] = "hd2-mod-cli", ["version"] = Version, ["game_dir"] = game, ["home"] = Store.Home, ["mods_dir"] = Store.ModsDir(),
                ["summary"] = summary, ["links"] = links, ["saved_bytes"] = Json.Long(record, "saved_bytes"),
                ["deploys"] = Json.Long(record, "deploys"), ["deletes"] = deletes.Cast<object>().ToList(),
                ["mods"] = mods, ["conflicts"] = clashes, ["resource_names"] = ResourceNames.HaveNames, ["tags"] = lib.Tags.Cast<object>().ToList(),
                // the player's groups (Groups): shown only, in the order they were made
                ["groups"] = lib.Groups.Select(g => (object)new Dictionary<string, object> { ["id"] = g.Id, ["name"] = g.Name, ["parent"] = g.Parent }).ToList(),
                ["lua_check"] = luaCheck,
                // profiles: whether they are on, the one in use, all of them (Profiles)
                ["profiles_on"] = lib.ProfilesOn, ["profile"] = lib.ProfilesOn ? lib.Active : null,
                // the loader in effect, chosen for every profile alike (Loaders); null: none
                ["loader_choice"] = Loaders.Chosen(lib)?.Id,
                // Chef Shared Loader helping it with the mods made for Chef only; null: not
                ["loader_helper"] = Loaders.Helper(lib)?.Id,
                ["profiles"] = lib.Profiles.Select(p => (object)p.Name).ToList(),
                ["deploying"] = Deploying(),
                ["check"] = CheckBlock(lib, game, d),
                ["leftovers"] = Ops.Leftovers(lib, null).Select(d => (object)new Dictionary<string, object>
                {
                    ["path"] = d, ["name"] = System.IO.Path.GetFileName(d), ["bytes"] = LibraryBytes(new DirectoryInfo(d)),
                }).ToList(),
            };
        }
    }
}
