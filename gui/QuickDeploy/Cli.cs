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
        /// <summary>The enabled mods it conflicts with, chosen or not.</summary>
        public List<string> Clashes = new List<string>();
    }

    class Option
    {
        public string Name, Description, Icon;
        public bool Enabled;
        public int Files;
        public long Bytes;
        public List<SubOption> Subs = new List<SubOption>();
        /// <summary>The enabled mods its own folders conflict with, chosen or not.</summary>
        public List<string> Clashes = new List<string>();
    }

    /// <summary>A mod's Lua, as the core read it from its archives (LuaMods).</summary>
    class LuaInfo
    {
        /// <summary>"any", "chef" or null; a loader itself: "bml", "chef", "bsl", "mdl3".</summary>
        public string Needs, Loader, LoaderVersion;
        /// <summary>A loader's capabilities (LoaderCaps); empty for a mod.</summary>
        public List<string> Caps = new List<string>();
        /// <summary>Its own start-up script's kind (bml, chef_stub, chef, bsl, mdl3, other), else null.</summary>
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
        /// <summary>Its boot only starts itself, and MDL3, in effect, runs it too.</summary>
        public bool Mdl3Runs;
        /// <summary>Made for Chef Shared Loader, and MDL3, in effect, runs it in Chef's place.</summary>
        public bool Mdl3Chef;
        /// <summary>Its scripts read MDL3 or HD2Data: MDL3 goes along as Boot Mod Loader's guest.</summary>
        public bool UsesMdl3;
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
        /// <summary>MDL3's native part: the package's mod, "installed" or why not, its version.</summary>
        public string Mdl3Mod, Mdl3State, Mdl3Native, Mdl3Problem;
    }

    /// <summary>A plugin's word on a mod's row, in both languages (PluginFiles.Notes).</summary>
    class Note
    {
        /// <summary>A plugin's words (Text, Tip, in the window's language), or the manager's own by
        /// Key and Args, worded by the window (Texts "note_" + Key).</summary>
        public string Plugin, Text, Tip, Colour, Key;
        public List<object> Args;
        /// <summary>An official plugin the mod needs and that is not added.</summary>
        public bool Needs;
    }

    class ModRow
    {
        public int Index;
        public string Id, Name, Path, Source, Description, Error, Icon;
        /// <summary>The name the mod came with, when the player gave it another one.</summary>
        public string RealName;
        public bool Enabled, Exists, HasManifest, SameDrive, Lua, Loader;
        /// <summary>"bml", "chef", "bsl", "mdl3" for a loader, else null.</summary>
        public string LoaderKind;
        /// <summary>For its author's members (hd2_license.json): where its license stands here
        /// ("licensed", "expired", "none", ... as core\Plugins.cs), else null.</summary>
        /// <summary>What the plugins say of it (PluginFiles.Notes).</summary>
        public List<Note> Notes = new List<Note>();
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
        /// <summary>The version kept for a rollback (Versions): its folder (null: none kept), its
        /// version, when it was replaced (Unix seconds), its size, whether its folder is there.</summary>
        public string PrevPath, PrevVersion;
        public long PrevKept, PrevBytes;
        public bool PrevExists;
        /// <summary>The mod this translation pack was turned off with (it comes on with it), else null.</summary>
        public string OffWith;
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
        /// <summary>In a conflict with any mod, on or off (the conflicts view).</summary>
        public bool InConflict;
        /// <summary>What it replaces, by game item (GameArchives): category and name, most first.</summary>
        public List<(string Category, string Name)> Replaces = new List<(string, string)>();
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
        /// <summary>Whose copy the game has now (the last deploy); null when none is deployed.</summary>
        public string Deployed;
        /// <summary>The conflicts view's sets (every option): "conflict" now, or why not: "options"
        /// (the options chosen do not), "one_on", "none_on"; null for the deploy's own sets.</summary>
        public string Now;
        /// <summary>Every mod ships the very same bytes: shared, not fought over.</summary>
        public bool Same, ManagedBoot;
        /// <summary>The first of its resources: type, id, and name when HD2SDK's list knows it.</summary>
        public List<(string Type, string Id, string Name, List<(string Category, string Name)> Of)> Resources = new List<(string, string, string, List<(string, string)>)>();
        /// <summary>Every resource of it grouped by what it is: type, name, the game items whose
        /// archives hold it, how many, and the first ids; named groups first, the most first.</summary>
        public List<(string Type, string Name, List<(string Category, string Name)> Of, int Count, List<string> Ids)> Groups =
            new List<(string, string, List<(string, string)>, int, List<string>)>();
        /// <summary>The options (and sub-options, else -1) that bring it, by mod (the conflicts view only).</summary>
        public List<(string Mod, int Option, int Sub)> From = new List<(string, int, int)>();
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
        /// <summary>Since the last deploy, another program changed files of this many mods
        /// (their rows say how), and added this many patch files a deploy deletes.</summary>
        public int AlteredMods, Added;
    }

    /// <summary>A folder an older version of a mod was unpacked into, that no mod uses now.</summary>
    class Leftover
    {
        public string Path, Name;
        public long Bytes;
    }

    class State
    {
        /// <summary>The number of the state read it came from (the window's), 0 for the saved list.</summary>
        public int ReadNo;
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
        /// <summary>The loaders going to the game beside the one in effect: MDL3 as Boot Mod Loader's
        /// guest, Chef Shared Loader helping with the mods made for it.</summary>
        public List<string> LoaderHelpers = new List<string>();
        public string Profile;
        public List<string> Profiles = new List<string>();
        /// <summary>Every label the library knows, whether or not a mod carries it.</summary>
        public List<string> Tags = new List<string>();
        /// <summary>The player's groups, in the order they were made (Groups).</summary>
        public List<GroupRow> Groups = new List<GroupRow>();
        public List<Clash> Clashes = new List<Clash>();
        /// <summary>The same over every mod, on or off (the conflicts view).</summary>
        public List<Clash> AllClashes = new List<Clash>();
        /// <summary>The library differs from how the last deploy took it, and can go back to it.</summary>
        public bool CanUndo;
        /// <summary>Whether resources can be named (HD2SDK's list was found).</summary>
        public bool ResourceNames;
        // (Lua is set again when a read is still waiting for the scripts)
        public LuaCheck Lua;
        /// <summary>Some mods' scripts are not read yet: the Lua facts come with a later read.</summary>
        public bool LuaPending;
        /// <summary>The folders of old versions left in the library folder.</summary>
        public List<Leftover> Leftovers = new List<Leftover>();
        /// <summary>Whether mods with a script keep the version they had when a new one comes.</summary>
        public bool KeepPrevious;
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
                ManagerLog.Error("failed: " + ManagerLog.Line(args) + ":\n" + x);
                return new CliResult { Code = 1, Out = o.ToString(), Err = "hd2qd: " + x.GetType().Name + ": " + x.Message };
            }
        }

        /// <summary>A piece of the core called directly, for a result the window words
        /// itself (deploy, purge, relink); its text is the window's own. What it says goes
        /// into the manager's log, as the commands' does.</summary>
        public static CliResult Call(Func<string> work)
        {
            try
            {
                var said = work();
                ManagerLog.Info(said);
                return new CliResult { Code = 0, Out = said };
            }
            catch (ModError e) { ManagerLog.Warn("refused: " + e.Message + (e.Kind != null ? " (" + e.Kind + ")" : "")); return new CliResult { Code = 1, Err = e.Message, Refused = e }; }
            catch (Exception x) { ManagerLog.Error("failed:\n" + x); return new CliResult { Code = 1, Err = x.GetType().Name + ": " + x.Message }; }
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

        /// <summary>A list of game items ({category, name}) in a state entry.</summary>
        static List<(string Category, string Name)> Archives(Dictionary<string, object> d, string key) =>
            List(d, key).OfType<Dictionary<string, object>>().Select(a => (Str(a, "category"), Str(a, "name"))).Where(a => a.Item2 != null).ToList();

        public static State ReadState(string json)
        {
            var d = (Dictionary<string, object>)Json.Parse(json);
            var s = new State { GameDir = Str(d, "game_dir"), Home = Str(d, "home"), Version = Str(d, "version"), ModsDir = Str(d, "mods_dir"),
                                SavedBytes = Long(d, "saved_bytes"), Deploys = Int(d, "deploys"), CanUndo = Bool(d, "can_undo"),
                                ProfilesOn = Bool(d, "profiles_on"), Profile = Str(d, "profile"), LoaderChoice = Str(d, "loader_choice"), LoaderHelpers = List(d, "loader_helpers").OfType<string>().ToList() };
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
                    Wrong = Int(ch, "wrong_n"), Added = Int(ch, "added_n"),
                    AlteredMods = List(ch, "altered").OfType<Dictionary<string, object>>().Select(x => Str(x, "mod")).Where(m => m != null).Distinct().Count(),
                };
            s.KeepPrevious = Bool(d, "keep_previous");
            foreach (var l in List(d, "leftovers").OfType<Dictionary<string, object>>())
                s.Leftovers.Add(new Leftover { Path = Str(l, "path"), Name = Str(l, "name"), Bytes = Long(l, "bytes") });
            foreach (var (key, into) in new[] { ("conflicts", s.Clashes), ("all_conflicts", s.AllClashes) })
                foreach (var c in List(d, key).OfType<Dictionary<string, object>>())
                    into.Add(new Clash
                    {
                        Mods = List(c, "mods").OfType<string>().ToList(), Count = Int(c, "count"), Winner = Str(c, "winner"), Deployed = Str(c, "deployed"), Now = Str(c, "now"), Same = Bool(c, "same"), ManagedBoot = Bool(c, "managed_boot"),
                        Resources = List(c, "resources").OfType<Dictionary<string, object>>().Select(r => (Str(r, "type"), Str(r, "id"), Str(r, "name"), Archives(r, "archives"))).ToList(),
                        From = List(c, "from").OfType<Dictionary<string, object>>().Select(f => (Str(f, "mod"), Int(f, "option"), Int(f, "sub"))).ToList(),
                        Groups = List(c, "groups").OfType<Dictionary<string, object>>()
                            .Select(g => (Str(g, "type"), Str(g, "name"), Archives(g, "archives"), Int(g, "count"), List(g, "ids").OfType<string>().ToList())).ToList(),
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
                if (lc.TryGetValue("mdl3_native", out var nv) && nv is Dictionary<string, object> na)
                {
                    lua.Mdl3Mod = Str(na, "mod");
                    lua.Mdl3State = Str(na, "state");
                    lua.Mdl3Native = Str(na, "native_version");
                    lua.Mdl3Problem = Str(na, "problem");
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
                    Notes = List(m, "notes").OfType<Dictionary<string, object>>().Select(n => new Note
                    {
                        Plugin = Str(n, "plugin"), Text = Str(n, "text"), Tip = Str(n, "tip"), Key = Str(n, "key"), Args = List(n, "args").ToList(),
                        Colour = Str(n, "colour"), Needs = n.TryGetValue("needs", out var nd) && nd is bool nb && nb,
                    }).ToList(),
                    LibraryBytes = Long(m, "library_bytes"),
                };
                foreach (var o in List(m, "options").OfType<Dictionary<string, object>>())
                {
                    var opt = new Option { Name = Str(o, "name"), Description = Str(o, "description"), Enabled = Bool(o, "enabled"),
                                           Icon = Str(o, "icon"), Files = Int(o, "files"), Bytes = Long(o, "bytes"),
                                           Clashes = List(o, "clashes").OfType<string>().ToList() };
                    foreach (var x in List(o, "subs").OfType<Dictionary<string, object>>())
                        opt.Subs.Add(new SubOption { Name = Str(x, "name"), Description = Str(x, "description"), Enabled = Bool(x, "enabled"),
                                                     Icon = Str(x, "icon"), Files = Int(x, "files"), Bytes = Long(x, "bytes"),
                                                     Clashes = List(x, "clashes").OfType<string>().ToList() });
                    row.Options.Add(opt);
                }
                foreach (var t in List(m, "tags")) if (t is string tag) row.Tags.Add(tag);
                row.Replaces = Archives(m, "replaces");
                row.Group = Str(m, "group");
                row.OffWith = Str(m, "off_with");
                var pv = Json.Obj(m, "previous");
                if (pv != null)
                {
                    row.PrevPath = Str(pv, "path"); row.PrevVersion = Str(pv, "version"); row.PrevKept = Long(pv, "kept");
                    row.PrevBytes = Long(pv, "bytes"); row.PrevExists = Bool(pv, "exists");
                }
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
                    lu.Mdl3Runs = Bool(li, "mdl3_runs");
                    lu.Caps = List(li, "caps").OfType<string>().ToList();
                    lu.Mdl3Chef = Bool(li, "mdl3_chef");
                    lu.UsesMdl3 = Bool(li, "uses_mdl3");
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
                row.InConflict = s.AllClashes.Any(c => !c.Same && !c.ManagedBoot && c.Mods.Contains(row.Name));
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
