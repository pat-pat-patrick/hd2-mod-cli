using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    public class SubChoice
    {
        public string Name;
        public bool Enabled;
    }

    /// <summary>A mod's choice for one of its manifest's options, kept by name.</summary>
    public class OptionChoice
    {
        public string Name;
        public bool Enabled = true;
        public List<SubChoice> Subs = new List<SubChoice>();
    }

    public class Mod
    {
        public string Id, Name, Path, Source;
        /// <summary>The name the mod came with, kept from the first time it was renamed by hand
        /// (null while Name is still that name).</summary>
        public string RealName;
        public bool Enabled = true;
        public List<OptionChoice> Options = new List<OptionChoice>();
        /// <summary>Where it came from on Nexus Mods (a download through the site's button), else null.</summary>
        public NexusRef Nexus;
        /// <summary>The GitHub release it came from, else null.</summary>
        public GitHubRef GitHub;
        /// <summary>Another site's page it came from (no update check there), else null.</summary>
        public string Page;
        /// <summary>What its site says about it, when it has been asked (Site.Fill).</summary>
        public SiteInfo Site;
        /// <summary>The player's own labels for this mod, in the order they were given.</summary>
        public List<string> Tags = new List<string>();
        /// <summary>The group the player put it in (Groups), else null: shown only, nothing moves.</summary>
        public string Group;
        /// <summary>The mod's options as the manager it came from describes them, for a folder
        /// with no manifest.json of its own: Echelon keeps its copy as numbered set folders, so
        /// the options only exist in its list (the user, 2026-10-24).</summary>
        public List<ManifestOption> Layout = new List<ManifestOption>();
        /// <summary>What the manager it came from says about it, for a folder with no
        /// manifest.json of its own to say it, else null.</summary>
        public string Description;
        /// <summary>A picture of the mod that the manager it came from keeps outside the mod's
        /// folder, to be copied in when the mod is added (Importers.CopyPictures); not kept.</summary>
        public string ImportPicture;
        /// <summary>The mod's id in the manager it is imported from, while it is imported; not kept.</summary>
        public string ImportKey;
        /// <summary>The version it had before the newest came, kept for a rollback (Versions), else null.</summary>
        public PreviousVersion Previous;
        /// <summary>The newest version the player chose to skip ("nexus:FILE" or "github:TAG"), else null:
        /// an update check says "skipped" for that one and "update" again for a newer one.</summary>
        public string Skip;
    }

    /// <summary>A mod's version before the newest, as it was: its folder and where it came from
    /// (Versions). Its folder is in the library folder, so a mod with a script can go back to it.</summary>
    public class PreviousVersion
    {
        public string Path, Name, Source, Page, Version;
        public NexusRef Nexus;
        public GitHubRef GitHub;
        /// <summary>When it was replaced, in Unix seconds.</summary>
        public long Kept;
    }

    /// <summary>Where a mod came from on Nexus Mods: its mod, the file it is (0 when unknown), that
    /// file's version and upload time (Unix seconds, 0 when unknown).</summary>
    public class NexusRef
    {
        public string Game, Version;
        public long Mod, File, Time;
    }

    /// <summary>Where a mod came from on GitHub: a repository's release.</summary>
    public class GitHubRef
    {
        public string Owner, Repo, Tag;
    }

    /// <summary>The load order (top to bottom; the last one wins) in library.json.</summary>
    public class Library
    {
        public List<Mod> Mods = new List<Mod>();
        /// <summary>The player's labels, kept whether or not a mod carries one: a label made
        /// before its mods are chosen has to outlive the window (the user, 2026-10-24).</summary>
        public List<string> Tags = new List<string>();
        /// <summary>The player's groups, up to Groups.Depth levels, in the order they were made.</summary>
        public List<ModGroup> Groups = new List<ModGroup>();
        public string ImportedFrom;
        /// <summary>Profiles (Profiles): whether they are on, the profiles, the one in use (whose
        /// order, on/off and options are the list's own), and its mods. Known is every mod id the
        /// library had when last saved: one it did not have was just added, and goes into the
        /// profile in use, wherever it was added from.</summary>
        public bool ProfilesOn;
        public List<Profile> Profiles = new List<Profile>();
        public string Active;
        public HashSet<string> Members;
        HashSet<string> known;

        /// <summary>Every mod the library has now counts as known: mods an import put into chosen
        /// profiles must not also go into the profile in use when the library is saved.</summary>
        public void KnowAll() => known = new HashSet<string>(Mods.Select(m => m.Id));

        public static Library Load()
        {
            var d = Store.Load("library.json");
            var lib = new Library { ImportedFrom = Json.Str(d, "imported_from"), Active = Json.Str(d, "active_profile"), ProfilesOn = Json.Bool(d, "profiles_on") };
            foreach (var p in Json.List(d, "profiles").OfType<Dictionary<string, object>>())
            {
                var profile = new Profile { Name = Json.Str(p, "name") ?? "" };
                profile.Order.AddRange(Json.List(p, "order").OfType<string>());
                foreach (var id in Json.List(p, "on").OfType<string>()) profile.On.Add(id);
                foreach (var kv in Json.Obj(p, "options") ?? new Dictionary<string, object>())
                    if (kv.Value is List<object> list) profile.Options[kv.Key] = ReadOptions(list);
                if (profile.Name.Length > 0) lib.Profiles.Add(profile);
            }
            var inUse = lib.Profiles.FirstOrDefault(p => string.Equals(p.Name, lib.Active, StringComparison.CurrentCultureIgnoreCase));
            if (lib.ProfilesOn && inUse != null) lib.Members = new HashSet<string>(inUse.Order);
            else lib.ProfilesOn = false;
            foreach (var t in Json.List(d, "tags"))
                if (t is string tag && tag.Trim().Length > 0 && !lib.Tags.Any(x => string.Equals(x, tag.Trim(), StringComparison.CurrentCultureIgnoreCase)))
                    lib.Tags.Add(tag.Trim());
            foreach (var m in Json.List(d, "mods").OfType<Dictionary<string, object>>())
            {
                var mod = new Mod
                {
                    Id = Json.Str(m, "id"), Name = Json.Str(m, "name"), Path = Json.Str(m, "path"),
                    Source = Json.Str(m, "source"), Enabled = Json.Bool(m, "enabled", true),
                    RealName = Json.Str(m, "real_name"),
                };
                mod.Options = ReadOptions(Json.List(m, "options"));
                mod.Nexus = ReadNexus(Json.Obj(m, "nexus"));
                mod.GitHub = ReadGitHub(Json.Obj(m, "github"));
                mod.Page = Json.Str(m, "page");
                mod.Skip = Json.Str(m, "skip");
                var pv = Json.Obj(m, "previous");
                if (pv != null && !string.IsNullOrEmpty(Json.Str(pv, "path")))
                    mod.Previous = new PreviousVersion
                    {
                        Path = Json.Str(pv, "path"), Name = Json.Str(pv, "name"), Source = Json.Str(pv, "source"), Page = Json.Str(pv, "page"),
                        Version = Json.Str(pv, "version"), Nexus = ReadNexus(Json.Obj(pv, "nexus")), GitHub = ReadGitHub(Json.Obj(pv, "github")),
                        Kept = Json.Long(pv, "kept"),
                    };
                foreach (var t in Json.List(m, "tags")) if (t is string tag && tag.Trim().Length > 0) mod.Tags.Add(tag.Trim());
                mod.Group = Json.Str(m, "group");
                mod.Description = Json.Str(m, "description");
                foreach (var o in Json.List(m, "layout").OfType<Dictionary<string, object>>())
                    mod.Layout.Add(new ManifestOption
                    {
                        Name = Json.Str(o, "name") ?? "",
                        Description = Json.Str(o, "description") ?? "", Image = Json.Str(o, "image"),
                        Include = Json.List(o, "include").OfType<string>().ToList(),
                        SubOptions = Json.List(o, "subs").OfType<Dictionary<string, object>>().Select(x => new ManifestOption
                        {
                            Name = Json.Str(x, "name") ?? "",
                            Description = Json.Str(x, "description") ?? "", Image = Json.Str(x, "image"),
                            Include = Json.List(x, "include").OfType<string>().ToList(),
                        }).ToList(),
                    });
                var si = Json.Obj(m, "site");
                if (si != null) mod.Site = new SiteInfo
                {
                    Name = Json.Str(si, "name"), Author = Json.Str(si, "author"), Summary = Json.Str(si, "summary"),
                    Picture = Json.Str(si, "picture"), Version = Json.Str(si, "version"), Updated = Json.Long(si, "updated"),
                };
                lib.Mods.Add(mod);
            }
            foreach (var g in Json.List(d, "groups").OfType<Dictionary<string, object>>())
                lib.Groups.Add(new ModGroup { Id = Json.Str(g, "id"), Name = (Json.Str(g, "name") ?? "").Trim(), Parent = Json.Str(g, "parent") });
            Hd2Mod.Groups.Tidy(lib);
            var was = Json.List(d, "known").OfType<string>().ToList();
            lib.known = new HashSet<string>(was.Count > 0 ? was : lib.Mods.Select(m => m.Id));
            return lib;
        }

        static NexusRef ReadNexus(Dictionary<string, object> nx) => nx == null ? null :
            new NexusRef { Game = Json.Str(nx, "game"), Mod = Json.Long(nx, "mod"), File = Json.Long(nx, "file"), Version = Json.Str(nx, "version"), Time = Json.Long(nx, "time") };

        static GitHubRef ReadGitHub(Dictionary<string, object> gh) => gh == null ? null :
            new GitHubRef { Owner = Json.Str(gh, "owner"), Repo = Json.Str(gh, "repo"), Tag = Json.Str(gh, "tag") };

        static Dictionary<string, object> NexusJson(NexusRef n) => new Dictionary<string, object>
        {
            ["game"] = n.Game, ["mod"] = n.Mod, ["file"] = n.File, ["version"] = n.Version, ["time"] = n.Time,
        };

        static Dictionary<string, object> GitHubJson(GitHubRef g) => new Dictionary<string, object> { ["owner"] = g.Owner, ["repo"] = g.Repo, ["tag"] = g.Tag };

        static List<OptionChoice> ReadOptions(List<object> list)
        {
            var out_ = new List<OptionChoice>();
            foreach (var o in list.OfType<Dictionary<string, object>>())
            {
                var c = new OptionChoice { Name = Json.Str(o, "name") ?? "", Enabled = Json.Bool(o, "enabled", true) };
                foreach (var s in Json.List(o, "subs").OfType<Dictionary<string, object>>())
                    c.Subs.Add(new SubChoice { Name = Json.Str(s, "name") ?? "", Enabled = Json.Bool(s, "enabled") });
                out_.Add(c);
            }
            return out_;
        }

        public void Save()
        {
            var mods = new List<object>();
            foreach (var m in Mods)
            {
                var entry = new Dictionary<string, object>
                {
                    ["id"] = m.Id, ["name"] = m.Name, ["path"] = m.Path, ["source"] = m.Source, ["enabled"] = m.Enabled,
                    ["options"] = OptionsJson(m.Options),
                };
                if (m.Nexus != null) entry["nexus"] = NexusJson(m.Nexus);
                if (m.GitHub != null) entry["github"] = GitHubJson(m.GitHub);
                if (m.Skip != null) entry["skip"] = m.Skip;
                if (m.Previous != null)
                {
                    var pv = new Dictionary<string, object>
                    {
                        ["path"] = m.Previous.Path, ["name"] = m.Previous.Name, ["source"] = m.Previous.Source, ["page"] = m.Previous.Page,
                        ["version"] = m.Previous.Version, ["kept"] = m.Previous.Kept,
                    };
                    if (m.Previous.Nexus != null) pv["nexus"] = NexusJson(m.Previous.Nexus);
                    if (m.Previous.GitHub != null) pv["github"] = GitHubJson(m.Previous.GitHub);
                    entry["previous"] = pv;
                }
                if (m.RealName != null) entry["real_name"] = m.RealName;
                if (m.Tags.Count > 0) entry["tags"] = m.Tags.Cast<object>().ToList();
                if (m.Group != null) entry["group"] = m.Group;
                if (!string.IsNullOrEmpty(m.Description)) entry["description"] = m.Description;
                Dictionary<string, object> Option(ManifestOption o)
                {
                    var x = new Dictionary<string, object> { ["name"] = o.Name, ["include"] = o.Include.Cast<object>().ToList() };
                    if (!string.IsNullOrEmpty(o.Description)) x["description"] = o.Description;
                    if (!string.IsNullOrEmpty(o.Image)) x["image"] = o.Image;
                    return x;
                }
                if (m.Layout.Count > 0)
                    entry["layout"] = m.Layout.Select(o =>
                    {
                        var x = Option(o);
                        x["subs"] = o.SubOptions.Select(y => (object)Option(y)).ToList();
                        return (object)x;
                    }).ToList();
                if (m.Site != null) entry["site"] = new Dictionary<string, object>
                {
                    ["name"] = m.Site.Name, ["author"] = m.Site.Author, ["summary"] = m.Site.Summary,
                    ["picture"] = m.Site.Picture, ["version"] = m.Site.Version, ["updated"] = m.Site.Updated,
                };
                if (m.Page != null) entry["page"] = m.Page;
                mods.Add(entry);
            }
            var d = new Dictionary<string, object> { ["version"] = 1L, ["mods"] = mods };
            // every label the mods carry, plus the ones made but not given to a mod yet
            var labels = new List<string>(Tags);
            foreach (var m in Mods)
                foreach (var t in m.Tags)
                    if (!labels.Any(x => string.Equals(x, t, StringComparison.CurrentCultureIgnoreCase))) labels.Add(t);
            if (labels.Count > 0) d["tags"] = labels.Cast<object>().ToList();
            if (Groups.Count > 0)
                d["groups"] = Groups.Select(g =>
                {
                    var x = new Dictionary<string, object> { ["id"] = g.Id, ["name"] = g.Name };
                    if (g.Parent != null) x["parent"] = g.Parent;
                    return (object)x;
                }).ToList();
            if (ImportedFrom != null) d["imported_from"] = ImportedFrom;
            if (Profiles.Count > 0)
            {
                if (ProfilesOn && Members != null)
                {
                    // a mod new to the library goes into the profile in use; one outside it is off
                    foreach (var m in Mods)
                        if (known != null && !known.Contains(m.Id)) Members.Add(m.Id);
                    foreach (var m in Mods.Where(m => !Members.Contains(m.Id))) m.Enabled = false;
                    // the one in use as the list stands now
                    int i = Profiles.FindIndex(p => string.Equals(p.Name, Active, StringComparison.CurrentCultureIgnoreCase));
                    if (i >= 0) Profiles[i] = Hd2Mod.Profiles.Capture(this, Profiles[i].Name);
                }
                d["profiles_on"] = ProfilesOn;
                d["active_profile"] = Active;
                d["profiles"] = Profiles.Select(p => (object)new Dictionary<string, object>
                {
                    ["name"] = p.Name,
                    ["order"] = p.Order.Cast<object>().ToList(),
                    ["on"] = p.Order.Where(p.On.Contains).Cast<object>().ToList(),
                    ["options"] = p.Options.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (object)OptionsJson(kv.Value)),
                }).ToList();
            }
            d["known"] = Mods.Select(m => (object)m.Id).ToList();
            known = new HashSet<string>(Mods.Select(m => m.Id));
            Store.Save("library.json", d);
        }

        public static List<object> OptionsJson(List<OptionChoice> options) =>
            options.Select(o => (object)new Dictionary<string, object>
            {
                ["name"] = o.Name, ["enabled"] = o.Enabled,
                ["subs"] = o.Subs.Select(s => (object)new Dictionary<string, object> { ["name"] = s.Name, ["enabled"] = s.Enabled }).ToList(),
            }).ToList();
    }

    public class ManifestOption
    {
        public string Name = "", Description = "", IconPath, Image;
        public List<string> Include = new List<string>();
        public List<ManifestOption> SubOptions = new List<ManifestOption>();
    }

    public class Manifest
    {
        public string Name, Description = "", IconPath, Guid;
        /// <summary>What the author calls this mod: "Tags" (or "Categories") in manifest.json.
        /// They belong to the mod, so the library shows them but never changes them.</summary>
        public List<string> Tags = new List<string>();
        /// <summary>The options were folder names (HD2ModManager's first manifests): one group, pick one.</summary>
        public bool FolderOptions;
        public List<ManifestOption> Options = new List<ManifestOption>();

        static readonly string[] Keys = { "Version", "Guid", "Name", "Description", "IconPath", "Options", "SubOptions", "Include", "Image", "Tags", "Categories" };

        /// <summary>The managers' spelling of a key, whatever case the author used.</summary>
        static string Canon(string k) => Keys.FirstOrDefault(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase)) ?? k;

        static object Canonical(object x)
        {
            if (x is Dictionary<string, object> d)
            {
                var n = new Dictionary<string, object>();
                foreach (var kv in d) n[Canon(kv.Key)] = Canonical(kv.Value);
                return n;
            }
            if (x is List<object> l) return l.Select(Canonical).ToList();
            return x;
        }

        /// <summary>A mod's manifest.json, read as leniently as the managers do: a BOM,
        /// // comments and trailing commas are accepted. null without one.</summary>
        /// <summary>The manifest a mod is deployed by: its folder's own, else the layout the
        /// manager it came from gave us (a folder with no manifest.json).</summary>
        public static Manifest Of(Mod mod)
        {
            var man = Read(mod.Path);
            if (man != null && man.Options.Count > 0) return man;
            if (mod.Layout.Count == 0) return man;
            return new Manifest
            {
                Name = man?.Name ?? mod.Name, Description = man?.Description ?? "", IconPath = man?.IconPath,
                Guid = man?.Guid, Options = mod.Layout,
            };
        }

        // each manifest.json read once while it is unchanged: the 65 of a library took 57 ms of
        // every state read (the user, 2026-10-26). What is kept is the parsed text, never a
        // Manifest, which a caller may change
        static readonly Dictionary<string, (string Stamp, object Raw, string Error)> parsed =
            new Dictionary<string, (string, object, string)>(StringComparer.OrdinalIgnoreCase);

        public static Manifest Read(string folder)
        {
            var p = System.IO.Path.Combine(folder, "manifest.json");
            var fi = new FileInfo(p);
            if (!fi.Exists) return null;
            var stamp = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
            (string Stamp, object Raw, string Error) known;
            lock (parsed) parsed.TryGetValue(p, out known);
            if (known.Stamp != stamp)
            {
                try { known = (stamp, Parse(File.ReadAllText(p, Encoding.UTF8)), null); }
                catch (FormatException e) { known = (stamp, null, e.Message); }
                lock (parsed) parsed[p] = known;
            }
            if (known.Error != null) throw new ModError(folder + ": manifest.json cannot be read: " + known.Error);
            return FromRaw(known.Raw);
        }

        /// <summary>A manifest's text as JSON: strict first, then with // comments and trailing commas forgiven.</summary>
        static object Parse(string text)
        {
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            try { return Json.Parse(text); }
            catch (FormatException)
            {
                var loose = Regex.Replace(text, @"^\s*//[^\n]*$", "", RegexOptions.Multiline);
                loose = Regex.Replace(loose, @",(\s*[}\]])", "$1");
                return Json.Parse(loose);
            }
        }

        static Manifest FromRaw(object raw)
        {
            var d = Canonical(raw) as Dictionary<string, object> ?? new Dictionary<string, object>();
            var m = new Manifest { Name = Json.Str(d, "Name"), Description = Json.Str(d, "Description") ?? "", IconPath = Json.Str(d, "IconPath"), Guid = Json.Str(d, "Guid") };
            foreach (var t in Json.List(d, "Tags").Concat(Json.List(d, "Categories")))
                if (t is string tag && tag.Trim().Length > 0 && !m.Tags.Any(x => string.Equals(x, tag.Trim(), StringComparison.CurrentCultureIgnoreCase)))
                    m.Tags.Add(tag.Trim());
            var options = Json.List(d, "Options");
            if (options.Count > 0 && options.All(o => o is string))
            {
                // HD2ModManager's first manifests: "Options" names folders, one of them loads
                // (its enabled.json keeps the one chosen in Selected[0])
                m.FolderOptions = true;
                m.Options.Add(new ManifestOption
                {
                    Name = "Options",
                    SubOptions = options.Cast<string>().Select(s => new ManifestOption { Name = s, Include = new List<string> { s } }).ToList(),
                });
            }
            else m.Options = ReadOptions(options);
            return m;
        }

        static List<ManifestOption> ReadOptions(List<object> list)
        {
            var out_ = new List<ManifestOption>();
            foreach (var o in list.OfType<Dictionary<string, object>>())
            {
                out_.Add(new ManifestOption
                {
                    Name = Json.Str(o, "Name") ?? "", Description = Json.Str(o, "Description") ?? "",
                    IconPath = Json.Str(o, "IconPath"), Image = Json.Str(o, "Image"),
                    Include = Json.List(o, "Include").OfType<string>().ToList(),
                    SubOptions = ReadOptions(Json.List(o, "SubOptions")),
                });
            }
            return out_;
        }

        /// <summary>Every option on and the first sub-option of each, as HD2 Arsenal starts a mod.</summary>
        public List<OptionChoice> Defaults() =>
            Options.Select(o => new OptionChoice
            {
                Name = o.Name, Enabled = true,
                Subs = o.SubOptions.Select((s, i) => new SubChoice { Name = s.Name, Enabled = i == 0 }).ToList(),
            }).ToList();

        /// <summary>The manifest's options with the choices saved for them (by name); new
        /// options take the defaults.</summary>
        public static List<OptionChoice> Merged(Manifest manifest, List<OptionChoice> chosen)
        {
            if (manifest == null) return new List<OptionChoice>();
            var saved = new Dictionary<string, OptionChoice>();
            foreach (var o in chosen ?? new List<OptionChoice>()) saved[o.Name] = o;
            var out_ = manifest.Defaults();
            foreach (var d in out_)
            {
                if (!saved.TryGetValue(d.Name, out var s)) continue;
                d.Enabled = s.Enabled;
                var subs = new Dictionary<string, bool>();
                foreach (var x in s.Subs) subs[x.Name] = x.Enabled;
                if (d.Subs.Any(x => subs.ContainsKey(x.Name)))
                    foreach (var x in d.Subs) x.Enabled = subs.TryGetValue(x.Name, out var on) && on;
                // one sub-option at most: they replace the same resources (the user, 2026-10-27);
                // a choice of several, saved by an older window's Ctrl+click, keeps its first
                bool first = true;
                foreach (var x in d.Subs.Where(x => x.Enabled)) { if (!first) x.Enabled = false; first = false; }
            }
            return out_;
        }
    }
}
