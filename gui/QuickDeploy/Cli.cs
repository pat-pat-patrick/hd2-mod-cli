using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hd2Mod;

namespace QuickDeploy
{
    // What `hd2qd state` says, read into plain classes. The window keeps no
    // logic of its own about numbering or deploying: the core (..\..\core) does it
    // all, the window shows it and runs the same commands as hd2qd.exe.
    class FileRow
    {
        public string Name, Src, Action, From;
        public int Companions;
        public long Bytes;
        public bool Linked;
    }

    class SubOption
    {
        public string Name, Description, Icon;
        public bool Enabled;
        public int Files;
        public long Bytes;
    }

    class Option
    {
        public string Name, Description, Icon;
        public bool Enabled;
        public int Files;
        public long Bytes;
        public List<SubOption> Subs = new List<SubOption>();
    }

    /// <summary>A mod's Lua, as the core read it from its archives (LuaMods).</summary>
    class LuaInfo
    {
        /// <summary>"any", "chef" or null; a loader itself: "bml", "chef", "bsl".</summary>
        public string Needs, Loader, LoaderVersion;
        /// <summary>Its own start-up script's kind (bml, chef_stub, chef, bsl, other), else null.</summary>
        public string Boot;
        public List<string> Scripts = new List<string>();
        public List<(string Script, int Line, string Message, string Kind, List<string> Jit, bool LuaJitBytecode)> Broken =
            new List<(string, int, string, string, List<string>, bool)>();
        public string NeedsWhy;
        /// <summary>The mod it translates (its id when installed) and that mod's script name.</summary>
        public string PackOf, PackOfName;
        /// <summary>Its boot only starts itself: a deploy takes it off the start-up slot and Boot
        /// Mod Loader runs it as a guest.</summary>
        public bool Guest;
        /// <summary>Its boot only starts itself, and Chef Shared Loader, in effect, runs it too.</summary>
        public bool ChefRuns;
        /// <summary>Its biggest script that would start, in bytes (Chef Shared Loader reads none over 4 MB).</summary>
        public long Biggest;
        /// <summary>Boot Mod Loader's log of the last session: loaded, failed or no_compile.</summary>
        public string LastState, LastWhy;
        public int LastMs;
    }

    /// <summary>The load order's Lua: whose boot runs, the problems, the last session, the last crash.</summary>
    class LuaCheck
    {
        public string BootOwner, BootKind, Running;
        public List<string> BootMods = new List<string>();
        public List<(string Kind, List<string> Mods, string Detail)> Issues = new List<(string, List<string>, string)>();
        /// <summary>Under each choice of loader (its id, or "none"): the mods that would not start, and why.</summary>
        public Dictionary<string, List<(string Why, List<string> Mods, string Over)>> Outcomes = new Dictionary<string, List<(string, List<string>, string)>>();
        public string SessionLoader;
        public long SessionWhen;
        public int Started = -1, Total = -1;
        public string CrashFile;
        public long CrashWhen, CrashDeployedWhen;
        public List<string> CrashMods = new List<string>();
        /// <summary>Where it crashed (helldivers2.exe+0x664D86), how often of the newest dumps,
        /// whether the game crashes there all the time, what the deploy before it added (null:
        /// not recorded).</summary>
        public string CrashSite;
        public int CrashTimes, CrashRecent;
        public bool CrashCommon;
        public List<string> CrashAdded;
    }

    class ModRow
    {
        public int Index;
        public string Id, Name, Path, Source, Description, Error, Icon;
        /// <summary>The name the mod came with, when the player gave it another one.</summary>
        public string RealName;
        public bool Enabled, Exists, HasManifest, SameDrive, Lua, Loader;
        /// <summary>"bml", "chef", "bsl" for a loader, else null.</summary>
        public string LoaderKind;
        /// <summary>Its Lua scripts, when it has any.</summary>
        public LuaInfo Lu;
        /// <summary>In the profile in use (always, with profiles off).</summary>
        public bool InProfile = true;
        public long NexusMod;
        public string NexusPage;
        /// <summary>Where it comes from, for update checks: "Nexus Mods", "GitHub", another site's
        /// host, or null; its page there; its version as recorded (Nexus version, GitHub tag).</summary>
        public string Channel, Page, Version;
        /// <summary>What its source site says about it, once it has been matched: who made it
        /// and the site's own words.</summary>
        public string SiteAuthor, SiteSummary, SiteVersion;
        /// <summary>The player's own labels for this mod.</summary>
        public List<string> Tags = new List<string>();
        /// <summary>The group the player put it in, else null (Groups: shown only).</summary>
        public string Group;
        /// <summary>The labels the mod's own manifest declares: read only.</summary>
        public List<string> AutoTags = new List<string>();
        /// <summary>The version shown (the recorded one, else one found in a name) and where it was
        /// found: nexus, github, file, name, folder; nulls when none says it.</summary>
        public string ShownVersion, VersionFrom;
        public long DeployBytes, LibraryBytes;
        public List<Option> Options = new List<Option>();
        public List<FileRow> Files = new List<FileRow>();
        /// <summary>Resources another mod ships too: Conflicts, where the bytes differ and
        /// one version wins, and Shared, where they are the very same bytes.</summary>
        public int Conflicts, Shared;
        public int Changes => Files.Count(f => f.Action != "keep");
        public int Chosen => Options.Sum(o => o.Subs.Count == 0 ? (o.Enabled ? 1 : 0) : (o.Enabled ? o.Subs.Count(x => x.Enabled) : 0));
        public int Choices => Options.Sum(o => o.Subs.Count == 0 ? 1 : o.Subs.Count);
        /// <summary>What a click just changed implies (on, or other options: "pending";
        /// off: "off"), shown until the state that follows it arrives.</summary>
        public string Assumed;

        /// <summary>keep, write, link, copy, partial, off or broken</summary>
        public string Status
        {
            get
            {
                if (Assumed != null) return Assumed;
                if (Error != null) return "broken";
                if (!Enabled) return "off";
                if (Changes > 0) return "pending";
                if (Files.Count == 0) return "keep";
                if (Files.All(f => f.Linked)) return "link";
                if (Files.All(f => !f.Linked)) return "copy";
                return "partial";
            }
        }
    }

    /// <summary>A group of mods: a folder in the window, only a name in the library.</summary>
    class GroupRow
    {
        public string Id, Name, Parent;
    }

    class Clash
    {
        public List<string> Mods = new List<string>();
        public int Count;
        public string Winner;
        /// <summary>Every mod ships the very same bytes: shared, not fought over.</summary>
        public bool Same, ManagedBoot;
        /// <summary>The first of its resources: type, id, and name when HD2SDK's list knows it.</summary>
        public List<(string Type, string Id, string Name)> Resources = new List<(string, string, string)>();
    }

    /// <summary>A deploy another program is in the middle of.</summary>
    class BusyBlock
    {
        public int Pid;
        public string Since;
    }

    /// <summary>Whether the game folder is exactly what the library asks for.</summary>
    class CheckBlock
    {
        public bool Ok = true;
        public int Missing, Extra, Changed, Copies, Checked;
        /// <summary>Of those, the ones a deploy would leave alone: the record is wrong about
        /// them, and nothing else the window says about them is true.</summary>
        public int Wrong;
    }

    /// <summary>A folder an older version of a mod was unpacked into, that no mod uses now.</summary>
    class Leftover
    {
        public string Path, Name;
        public long Bytes;
    }

    class State
    {
        public string GameDir, Home, Version, ModsDir;
        public int Kept, Moved, Written, Deleted, FileCount, LinkedFiles, CopiedFiles, Deploys;
        public long KeptBytes, MovedBytes, WrittenBytes, CopiedBytes, DeletedBytes, LinkedBytes, CopyBytes, SavedBytes;
        public bool HasSummary;
        public List<string> Deletes = new List<string>();
        public List<ModRow> Mods = new List<ModRow>();
        /// <summary>Profiles: whether they are on, the one in use, all of them.</summary>
        public bool ProfilesOn;
        /// <summary>The loader in effect (a mod's id), chosen for every profile; null: none.</summary>
        public string LoaderChoice;
        /// <summary>Chef Shared Loader helping the loader in effect with the mods made for it; null: none.</summary>
        public string LoaderHelper;
        public string Profile;
        public List<string> Profiles = new List<string>();
        /// <summary>Every label the library knows, whether or not a mod carries it.</summary>
        public List<string> Tags = new List<string>();
        /// <summary>The player's groups, in the order they were made (Groups).</summary>
        public List<GroupRow> Groups = new List<GroupRow>();
        public List<Clash> Clashes = new List<Clash>();
        /// <summary>Whether resources can be named (HD2SDK's list was found).</summary>
        public bool ResourceNames;
        // (Lua is set again when a read is still waiting for the scripts)
        public LuaCheck Lua;
        /// <summary>Some mods' scripts are not read yet: the Lua facts come with a later read.</summary>
        public bool LuaPending;
        /// <summary>The folders of old versions left in the library folder.</summary>
        public List<Leftover> Leftovers = new List<Leftover>();
        /// <summary>The game folder against the library, as the last state read found it.</summary>
        public CheckBlock Check = new CheckBlock();
        /// <summary>Set while another program is writing to the game folder.</summary>
        public BusyBlock Deploying;
        public int Pending => Moved + Written + Deleted;
    }

    class CliResult
    {
        public int Code;
        public string Out, Err;
        /// <summary>The refusal, when the command refused (its Kind for the window's own words).</summary>
        public ModError Refused;
        public bool Ok => Code == 0;
        public string Message => (string.IsNullOrWhiteSpace(Err) ? Out : Err).Trim();
    }

    static class Cli
    {
        public static string LibraryFile => Store.PathOf("library.json");

        /// <summary>Runs one hd2qd command in this process; blocks: call it off the UI thread.</summary>
        public static CliResult Run(params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            try
            {
                int code = Commands.Run(args, o, e, out var refused);
                return new CliResult { Code = code, Out = o.ToString(), Err = e.ToString(), Refused = refused };
            }
            catch (Exception x)
            {
                return new CliResult { Code = 1, Out = o.ToString(), Err = "hd2qd: " + x.GetType().Name + ": " + x.Message };
            }
        }

        /// <summary>A piece of the core called directly, for a result the window words
        /// itself (deploy, purge, relink); its text is the window's own.</summary>
        public static CliResult Call(Func<string> work)
        {
            try { return new CliResult { Code = 0, Out = work() }; }
            catch (ModError e) { return new CliResult { Code = 1, Err = e.Message, Refused = e }; }
            catch (Exception x) { return new CliResult { Code = 1, Err = x.GetType().Name + ": " + x.Message }; }
        }

        /// <summary>One argument for a Windows command line (the C runtime's rules).</summary>
        public static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
                else sb.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        // ---------------------------------------------------------------- reading `state`

        public static State ReadState(string json)
        {
            var d = (Dictionary<string, object>)Json.Parse(json);
            var s = new State { GameDir = Str(d, "game_dir"), Home = Str(d, "home"), Version = Str(d, "version"), ModsDir = Str(d, "mods_dir"),
                                SavedBytes = Long(d, "saved_bytes"), Deploys = Int(d, "deploys"),
                                ProfilesOn = Bool(d, "profiles_on"), Profile = Str(d, "profile"), LoaderChoice = Str(d, "loader_choice"), LoaderHelper = Str(d, "loader_helper") };
            if (d.TryGetValue("profiles", out var pl) && pl is List<object> names) s.Profiles = names.OfType<string>().ToList();
            if (d.TryGetValue("summary", out var sm) && sm is Dictionary<string, object> sum)
            {
                s.HasSummary = true;
                s.Kept = Int(sum, "kept"); s.Moved = Int(sum, "moved"); s.Written = Int(sum, "written");
                s.Deleted = Int(sum, "deleted"); s.FileCount = Int(sum, "files");
                s.KeptBytes = Long(sum, "kept_bytes"); s.MovedBytes = Long(sum, "moved_bytes"); s.WrittenBytes = Long(sum, "written_bytes");
                s.CopiedBytes = Long(sum, "copied_bytes"); s.DeletedBytes = Long(sum, "deleted_bytes");
            }
            if (d.TryGetValue("links", out var lk) && lk is Dictionary<string, object> links)
            {
                s.LinkedFiles = Int(links, "files"); s.LinkedBytes = Long(links, "bytes");
                s.CopiedFiles = Int(links, "copies"); s.CopyBytes = Long(links, "copy_bytes");
            }
            foreach (var x in List(d, "deletes")) if (x is string f) s.Deletes.Add(f);
            foreach (var t in List(d, "tags")) if (t is string tag) s.Tags.Add(tag);
            foreach (var g in List(d, "groups").OfType<Dictionary<string, object>>())
                s.Groups.Add(new GroupRow { Id = Str(g, "id"), Name = Str(g, "name"), Parent = Str(g, "parent") });
            var bs = Json.Obj(d, "deploying");
            if (bs != null) s.Deploying = new BusyBlock { Pid = Int(bs, "pid"), Since = Str(bs, "since") };
            var ch = Json.Obj(d, "check");
            if (ch != null)
                s.Check = new CheckBlock
                {
                    Ok = Bool(ch, "ok"), Missing = Int(ch, "missing_n"), Extra = Int(ch, "extra_n"),
                    Changed = Int(ch, "changed_n"), Copies = Int(ch, "copies"), Checked = Int(ch, "checked"),
                    Wrong = Int(ch, "wrong_n"),
                };
            foreach (var l in List(d, "leftovers").OfType<Dictionary<string, object>>())
                s.Leftovers.Add(new Leftover { Path = Str(l, "path"), Name = Str(l, "name"), Bytes = Long(l, "bytes") });
            foreach (var c in List(d, "conflicts").OfType<Dictionary<string, object>>())
                s.Clashes.Add(new Clash
                {
                    Mods = List(c, "mods").OfType<string>().ToList(), Count = Int(c, "count"), Winner = Str(c, "winner"), Same = Bool(c, "same"), ManagedBoot = Bool(c, "managed_boot"),
                    Resources = List(c, "resources").OfType<Dictionary<string, object>>().Select(r => (Str(r, "type"), Str(r, "id"), Str(r, "name"))).ToList(),
                });
            s.ResourceNames = Bool(d, "resource_names");
            if (d.TryGetValue("lua_check", out var lcp) && lcp is Dictionary<string, object> lcq && Bool(lcq, "pending")) s.LuaPending = true;
            else if (d.TryGetValue("lua_check", out var lcv) && lcv is Dictionary<string, object> lc)
            {
                var lua = new LuaCheck { BootOwner = Str(lc, "boot_owner"), BootKind = Str(lc, "boot_kind"), Running = Str(lc, "running") };
                foreach (var id in List(lc, "boot_mods").OfType<string>()) lua.BootMods.Add(id);
                foreach (var i in List(lc, "issues").OfType<Dictionary<string, object>>())
                    lua.Issues.Add((Str(i, "kind"), List(i, "mods").OfType<string>().ToList(), Str(i, "detail")));
                if (lc.TryGetValue("outcomes", out var ov) && ov is Dictionary<string, object> outs)
                    foreach (var kv in outs)
                        lua.Outcomes[kv.Key] = (kv.Value as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>()
                            .Select(o => (Str(o, "why"), List(o, "mods").OfType<string>().ToList(), Str(o, "over"))).ToList();
                if (lc.TryGetValue("session", out var sv) && sv is Dictionary<string, object> se)
                {
                    lua.SessionLoader = Str(se, "loader");
                    lua.SessionWhen = Long(se, "when");
                    lua.Started = Int(se, "started");
                    lua.Total = Int(se, "total");
                }
                if (lc.TryGetValue("crash", out var cv) && cv is Dictionary<string, object> cr)
                {
                    lua.CrashFile = Str(cr, "file");
                    lua.CrashWhen = Long(cr, "when");
                    lua.CrashDeployedWhen = Long(cr, "deployed_when");
                    lua.CrashMods = List(cr, "mods").OfType<string>().ToList();
                    lua.CrashSite = Str(cr, "site");
                    lua.CrashTimes = Int(cr, "site_times");
                    lua.CrashRecent = Int(cr, "recent");
                    lua.CrashCommon = Bool(cr, "common");
                    lua.CrashAdded = cr.TryGetValue("added", out var ad) && ad is List<object> al ? al.OfType<string>().ToList() : null;
                }
                s.Lua = lua;
            }
            foreach (var m in List(d, "mods").OfType<Dictionary<string, object>>())
            {
                var row = new ModRow
                {
                    Index = Int(m, "index"), Id = Str(m, "id"), Name = Str(m, "name"), RealName = Str(m, "real_name"), Path = Str(m, "path"),
                    Source = Str(m, "source"), Description = Str(m, "description"), Error = Str(m, "error"),
                    Enabled = Bool(m, "enabled"), Exists = Bool(m, "exists"), HasManifest = Bool(m, "manifest"),
                    InProfile = !m.ContainsKey("in_profile") || Bool(m, "in_profile"),
                    Icon = Str(m, "icon"), SameDrive = Bool(m, "same_drive"), Lua = Bool(m, "lua"), Loader = Bool(m, "loader"), LoaderKind = Str(m, "loader_kind"), NexusMod = Long(m, "nexus_mod"), NexusPage = Str(m, "nexus_page"),
                    Channel = Str(m, "channel"), Page = Str(m, "page"), Version = Str(m, "version"),
                    SiteAuthor = Str(m, "site_author"), SiteSummary = Str(m, "site_summary"), SiteVersion = Str(m, "site_version"),
                    ShownVersion = Str(m, "shown_version"), VersionFrom = Str(m, "version_from"), DeployBytes = Long(m, "deploy_bytes"),
                    LibraryBytes = Long(m, "library_bytes"),
                };
                foreach (var o in List(m, "options").OfType<Dictionary<string, object>>())
                {
                    var opt = new Option { Name = Str(o, "name"), Description = Str(o, "description"), Enabled = Bool(o, "enabled"),
                                           Icon = Str(o, "icon"), Files = Int(o, "files"), Bytes = Long(o, "bytes") };
                    foreach (var x in List(o, "subs").OfType<Dictionary<string, object>>())
                        opt.Subs.Add(new SubOption { Name = Str(x, "name"), Description = Str(x, "description"), Enabled = Bool(x, "enabled"),
                                                     Icon = Str(x, "icon"), Files = Int(x, "files"), Bytes = Long(x, "bytes") });
                    row.Options.Add(opt);
                }
                foreach (var t in List(m, "tags")) if (t is string tag) row.Tags.Add(tag);
                row.Group = Str(m, "group");
                foreach (var t in List(m, "auto_tags")) if (t is string tag) row.AutoTags.Add(tag);
                foreach (var f in List(m, "files").OfType<Dictionary<string, object>>())
                    row.Files.Add(new FileRow { Name = Str(f, "name"), Src = Str(f, "src"), Action = Str(f, "action"), From = Str(f, "from"),
                                                Companions = Int(f, "companions"), Bytes = Long(f, "bytes"), Linked = Bool(f, "linked") });
                if (m.TryGetValue("lua_info", out var liv) && liv is Dictionary<string, object> li)
                {
                    var lu = new LuaInfo
                    {
                        Needs = Str(li, "needs"), Loader = Str(li, "loader"), LoaderVersion = Str(li, "loader_version"), Boot = Str(li, "boot"),
                        Scripts = List(li, "scripts").OfType<string>().ToList(), PackOf = Str(li, "pack_of"), PackOfName = Str(li, "pack_of_name"),
                    };
                    foreach (var b in List(li, "broken").OfType<Dictionary<string, object>>())
                        lu.Broken.Add((Str(b, "script"), Int(b, "line"), Str(b, "message"), Str(b, "error_kind"), List(b, "jit").OfType<string>().ToList(), Bool(b, "luajit_bytecode")));
                    lu.NeedsWhy = Str(li, "needs_why");
                    lu.Guest = Bool(li, "guest");
                    lu.ChefRuns = Bool(li, "chef_runs");
                    lu.Biggest = Long(li, "biggest");
                    if (li.TryGetValue("last", out var lv) && lv is Dictionary<string, object> last)
                    {
                        lu.LastState = Str(last, "state");
                        lu.LastWhy = Str(last, "why");
                        lu.LastMs = Int(last, "ms");
                    }
                    row.Lu = lu;
                }
                row.Conflicts = s.Clashes.Where(c => !c.Same && !c.ManagedBoot && c.Mods.Contains(row.Name)).Sum(c => c.Count);
                row.Shared = s.Clashes.Where(c => c.Same && c.Mods.Contains(row.Name)).Sum(c => c.Count);
                s.Mods.Add(row);
            }
            return s;
        }

        static string Str(Dictionary<string, object> d, string k) => Json.Str(d, k);
        static bool Bool(Dictionary<string, object> d, string k) => Json.Bool(d, k);
        static int Int(Dictionary<string, object> d, string k) => (int)Json.Long(d, k);
        static long Long(Dictionary<string, object> d, string k) => Json.Long(d, k);
        static IEnumerable<object> List(Dictionary<string, object> d, string k) =>
            d.TryGetValue(k, out var v) && v is List<object> l ? l : Enumerable.Empty<object>();
    }
}
