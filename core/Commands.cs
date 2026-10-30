using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>The command line: hd2qd COMMAND [ARGS]. The window runs the same commands
    /// in its own process.</summary>
    public static partial class Commands
    {
        class Spec
        {
            public string Help;
            public string[] Positionals = new string[0];
            /// <summary>The last positional takes one or more values.</summary>
            public bool Many;
            public string[] Flags = new string[0], Values = new string[0];
            /// <summary>At most one of these may be given.</summary>
            public string[] OneOf = new string[0];
            public Action<Args, TextWriter> Run;
        }

        class Args
        {
            public Dictionary<string, string> Pos = new Dictionary<string, string>();
            public List<string> Rest = new List<string>();
            public HashSet<string> Flags = new HashSet<string>();
            public Dictionary<string, string> Values = new Dictionary<string, string>();
            public bool Flag(string f) => Flags.Contains(f);
            public string Value(string v) => Values.TryGetValue(v, out var x) ? x : null;
        }

        class UsageError : Exception
        {
            public UsageError(string m) : base(m) { }
        }

        static readonly List<KeyValuePair<string, Spec>> Specs = new List<KeyValuePair<string, Spec>>
        {
            Cmd("init", new Spec { Help = "set the game folder (found from HD2 Arsenal or Steam when left out)",
                                   Values = new[] { "--game", "--mods-dir" }, Run = Init }),
            Cmd("import-arsenal", new Spec { Help = "add HD2 Arsenal's mods, order, on/off and options to the library (read only; mods already in it stay; --replace: the library becomes HD2 Arsenal's; --profiles: every profile, each one of ours)",
                                             Values = new[] { "--file", "--profile" }, Flags = new[] { "--replace", "--profiles" }, Run = ImportArsenal }),
            Cmd("import-hd2mm", new Spec { Help = "add HD2ModManager's mods, order, on/off and options to the library (read only; mods already in it stay)",
                                           Values = new[] { "--dir" }, Flags = new[] { "--replace" }, Run = ImportHd2mm }),
            Cmd("import-echelon", new Spec { Help = "add Echelon's mods, order, on/off and options to the library (read only; mods already in it stay; --profiles: every profile, each one of ours)",
                                             Values = new[] { "--file" }, Flags = new[] { "--replace", "--profiles" }, Run = ImportEchelon }),
            Cmd("folders", new Spec { Help = "the mod folders: the default (where new mods go), the ones added, and those mods are in", Run = FoldersCmd }),
            Cmd("folder-add", new Spec { Help = "add a library folder, and say which other managers' lists and which mods not in the library it holds (--import: bring them in)",
                                         Positionals = new[] { "path" }, Flags = new[] { "--import" }, Run = FolderAdd }),
            Cmd("folder-path", new Spec { Help = "a library folder is somewhere else now: the list, the default and the mods in it follow (nothing on disk moves)",
                                          Positionals = new[] { "path", "to" }, Run = FolderPath }),
            Cmd("add-loose", new Spec { Help = "add the mod folders in a library folder that no other manager lists and the library does not have, where they are",
                                        Positionals = new[] { "path" }, Run = AddLoose }),
            Cmd("folder-remove", new Spec { Help = "take a mod folder off the list (its files stay)", Positionals = new[] { "path" },
                                            Run = (a, o) => { Storage.RemoveFolder(a.Pos["path"]); o.WriteLine("off the list: " + a.Pos["path"]); } }),
            Cmd("folder-default", new Spec { Help = "make a mod folder the default: added zips and downloads go there", Positionals = new[] { "path" },
                                             Run = (a, o) => { Storage.SetDefault(a.Pos["path"]); o.WriteLine("default: " + Store.ModsDir()); } }),
            Cmd("relocate", new Spec { Help = "move mods into another mod folder (--keep: copy, the old folders stay); the game's copies of them become hard links",
                                       Positionals = new[] { "mods" }, Many = true, Values = new[] { "--into" }, Flags = new[] { "--keep" }, Run = RelocateCmd }),
            Cmd("list", new Spec { Help = "the load order (the last wins)", Flags = new[] { "--files", "--paths" }, Run = List }),
            Cmd("add", new Spec { Help = "add a mod at the bottom: a zip (unpacked once) or a folder (used where it is)",
                                  Positionals = new[] { "path" }, Values = new[] { "--name" }, Flags = new[] { "--copy", "--whole" }, Run = Add }),
            Cmd("replace", new Spec { Help = "a new version of a mod: same place in the order, same options " +
                                             "(the folder the old version was unpacked into is deleted; --keep-old keeps it)",
                                      Positionals = new[] { "mod", "path" }, Values = new[] { "--name" },
                                      Flags = new[] { "--copy", "--keep-old" }, Run = Replace }),
            Cmd("remove", new Spec { Help = "take a mod out of the library", Positionals = new[] { "mod" }, Run = Remove }),
            Cmd("rename", new Spec { Help = "call a mod something else in the library (its folder and files are left alone)",
                                     Positionals = new[] { "mod", "name" }, Run = Rename }),
            Cmd("match", new Spec { Help = "ask a mod's source what it is called and fill that in (name, version, author, picture)",
                                    Positionals = new[] { "mod" }, Flags = new[] { "--all" }, Run = Match }),
            Cmd("tags", new Spec { Help = "the player's own labels, with how many mods each one is on", Run = TagList }),
            Cmd("tag", new Spec { Help = "put a label on a mod: tag MOD NAME...", Positionals = new[] { "mod" }, Many = true, Run = Tag }),
            Cmd("untag", new Spec { Help = "take a label off a mod (--all: every label)", Positionals = new[] { "mod" },
                                    Many = true, Flags = new[] { "--all" }, Run = Untag }),
            Cmd("tag-many", new Spec { Help = "put one label on several mods: tag-many NAME MOD...",
                                       Positionals = new[] { "name" }, Many = true, Run = TagMany }),
            Cmd("tag-new", new Spec { Help = "make a label, before any mod carries it", Positionals = new[] { "name" }, Run = TagNew }),
            Cmd("tag-rename", new Spec { Help = "rename a label everywhere it is used", Positionals = new[] { "name", "to" }, Run = TagRename }),
            Cmd("tag-delete", new Spec { Help = "take a label off every mod", Positionals = new[] { "name" }, Run = TagDelete }),
            Cmd("groups", new Spec { Help = "the player's groups, up to " + Groups.Depth + " levels, with their mods (shown only: no file moves, the deploy is the same)", Run = GroupList }),
            Cmd("group-new", new Spec { Help = "make a group, at the top or --in another (a group is a name or a path such as Looks/Armour)",
                                        Positionals = new[] { "name" }, Values = new[] { "--in" }, Run = GroupNew }),
            Cmd("group-rename", new Spec { Help = "rename a group", Positionals = new[] { "group", "name" }, Run = GroupRename }),
            Cmd("group-move", new Spec { Help = "move a group, with what it holds, --in another or to the --top",
                                         Positionals = new[] { "group" }, Values = new[] { "--in" }, Flags = new[] { "--top" },
                                         OneOf = new[] { "--in", "--top" }, Run = GroupMove }),
            Cmd("group-delete", new Spec { Help = "delete a group: its mods and groups go up into the group around it", Positionals = new[] { "group" }, Run = GroupDelete }),
            Cmd("group-put", new Spec { Help = "put mods --in a group, or back to the --top (no group)",
                                        Positionals = new[] { "mods" }, Many = true, Values = new[] { "--in" }, Flags = new[] { "--top" },
                                        OneOf = new[] { "--in", "--top" }, Run = GroupPut }),
#if HAS_NEXUS
            Cmd("nexus", new Spec { Help = "download a Nexus Mods link (nxm://...) and put it in the library (a mod from it already there gets the new version)",
                                    Positionals = new[] { "link" }, Run = NexusCmd }),
            Cmd("nexus-login", new Spec { Help = "sign in to Nexus Mods: your browser opens Nexus Mods' page, where you allow this program (OAuth with PKCE)",
                                          Run = NexusLoginCmd }),
            Cmd("nexus-logout", new Spec { Help = "sign out of Nexus Mods here (the access can also be taken back on Nexus Mods)", Run = NexusLogoutCmd }),
            Cmd("nexus-info", new Spec { Help = "a mod on Nexus Mods by its number (16662, N站16662) or its page: its name, author, version, page, and whether the library has it (read only; no sign-in needed)",
                                         Positionals = new[] { "number" }, Run = NexusInfoCmd }),
            Cmd("check-updates", new Spec { Help = "check every mod for a newer version on its own channel (Nexus Mods when signed in, GitHub releases)", Run = CheckUpdatesCmd }),
            Cmd("update", new Spec { Help = "download a mod's newer version from GitHub (or Nexus Mods, premium accounts) and put it in the library", Positionals = new[] { "mod" }, Run = UpdateCmd }),
#else
            Cmd("check-updates", new Spec { Help = "check every mod from GitHub for a newer release", Run = CheckUpdatesCmd }),
            Cmd("update", new Spec { Help = "download a mod's newer release from GitHub and put it in the library", Positionals = new[] { "mod" }, Run = UpdateCmd }),
#endif
            Cmd("source", new Spec { Help = "set where a mod comes from: a Nexus Mods page, a GitHub repository, or another page (--clear: none)",
                                     Positionals = new[] { "mod" }, Many = true, Flags = new[] { "--clear" }, Run = SourceCmd }),
            Cmd("delete", new Spec { Help = "delete mods from the disk for good: out of the library and the game (one deploy), their folders deleted",
                                     Positionals = new[] { "mods" }, Many = true, Flags = new[] { "--yes" }, Run = DeleteCmd }),
            Cmd("enable", new Spec { Help = "turn mods on", Positionals = new[] { "mods" }, Many = true, Run = (a, o) => Switch(a, o, true) }),
            Cmd("disable", new Spec { Help = "turn mods off", Positionals = new[] { "mods" }, Many = true, Run = (a, o) => Switch(a, o, false) }),
            Cmd("move", new Spec { Help = "move mods in the load order; several move as one block, in the order they have", Positionals = new[] { "mods" }, Many = true,
                                   Flags = new[] { "--top", "--bottom" }, Values = new[] { "--to", "--before", "--after" },
                                   OneOf = new[] { "--top", "--bottom", "--to", "--before", "--after" }, Run = Move }),
            Cmd("options", new Spec { Help = "a mod's options and sub-options", Positionals = new[] { "mod" }, Run = Options }),
            Cmd("set", new Spec { Help = "turn an option on or off, or pick one of its sub-options (one at a time): OPTION, OPTION/SUB, or 2.3",
                                  Positionals = new[] { "mod", "option", "value" }, Flags = new[] { "--only" }, Run = Set }),
            Cmd("profiles", new Spec { Help = "the profiles: each its own mods, load order, on/off and options; * marks the one in use", Run = ProfilesCmd }),
            Cmd("profiles-on", new Spec { Help = "turn profiles on: the first time, a profile Default of every mod", Run = (a, o) => Profiling(o, true) }),
            Cmd("profiles-off", new Spec { Help = "turn profiles off: one list of every mod again (the profiles are kept)", Run = (a, o) => Profiling(o, false) }),
            Cmd("profile-new", new Spec { Help = "a new profile, a copy of the one in use, which stays in use (--empty: no mods)", Positionals = new[] { "name" }, Flags = new[] { "--empty" }, Run = ProfileNew }),
            Cmd("profile-add", new Spec { Help = "put mods of the library into the profile in use, at its bottom, on", Positionals = new[] { "mods" }, Many = true, Run = (a, o) => ProfileMembers(a, o, true) }),
            Cmd("profile-remove", new Spec { Help = "take mods out of the profile in use (they stay in the library)", Positionals = new[] { "mods" }, Many = true, Run = (a, o) => ProfileMembers(a, o, false) }),
            Cmd("profile-use", new Spec { Help = "switch to a profile and deploy it (--no-deploy: switch only)", Positionals = new[] { "name" }, Flags = new[] { "--no-deploy" }, Run = ProfileUse }),
            Cmd("profile-rename", new Spec { Help = "rename a profile", Positionals = new[] { "name", "to" }, Run = ProfileRename }),
            Cmd("profile-delete", new Spec { Help = "delete a profile (not the one in use); the mods stay in the library", Positionals = new[] { "name" }, Run = ProfileDelete }),
            Cmd("deploy", new Spec { Help = "make the game folder match the load order, touching only what differs",
                                     Flags = new[] { "--dry-run", "--copy", "--verify" }, Run = DeployCmd }),
            Cmd("status", new Spec { Help = "what a deploy would change", Flags = new[] { "--verify" }, Run = Status }),
            Cmd("relink", new Spec { Help = "make the copies in the game folder hard links to the library (same drive): " +
                                            "their space comes back, nothing is written",
                                     Flags = new[] { "--dry-run", "--verify" }, Run = Relink }),
            Cmd("keep-previous-on", new Spec { Help = "from now on, a mod with a script keeps the version it had when a new one comes, for a rollback", Run = (a, o) => KeepPrevious(o, true) }),
            Cmd("keep-previous-off", new Spec { Help = "stop keeping earlier versions (the ones kept stay until deleted)", Run = (a, o) => KeepPrevious(o, false) }),
            Cmd("previous", new Spec { Help = "the earlier versions kept for a rollback (--delete with mods, or --delete --all, deletes them; the mods keep the version they have)",
                                       Many = true, Flags = new[] { "--delete", "--all" }, Run = PreviousCmd }),
            Cmd("rollback", new Spec { Help = "a mod goes back to the version kept, and the one it had is kept instead (deploy to bring the game up to date)",
                                       Positionals = new[] { "mod" }, Run = RollbackCmd }),
            Cmd("skip", new Spec { Help = "the update check stops offering a mod's newest version, until a newer one comes (--clear: offer it again)",
                                   Positionals = new[] { "mod" }, Flags = new[] { "--clear" }, Run = SkipCmd }),
            Cmd("leftovers", new Spec { Help = "the folders of old versions this program unpacked that no mod uses any more " +
                                               "(--delete with folders, or --delete --all, deletes them; the game is untouched)",
                                        Many = true, Flags = new[] { "--delete", "--all" }, Run = LeftoverCmd }),
            Cmd("loader", new Spec { Help = "the loader in effect, for every profile: a loader of the library (name or id), or none; " +
                                            "a deploy puts it after every mod and leaves the other loaders out",
                                     Positionals = new[] { "which" }, Run = LoaderCmd }),
            Cmd("lua", new Spec { Help = "the mods' Lua before a deploy: whose start-up script (boot) runs, which loader that is, what each mod needs, what the game's Lua cannot run, the last session and the last crash (read only)",
                                  Run = LuaCmd }),
            Cmd("lua-syntax", new Spec { Help = "whether Lua files compile on the game's Lua (PUC 5.1, no LuaJIT): OK, or the line and why",
                                         Positionals = new[] { "files" }, Many = true, Run = LuaSyntaxCmd }),
            Cmd("self-update", new Spec { Help = "whether a newer version of this program is on GitHub (read only; the official build counts releases, the development build pre-releases too)",
                                          Run = SelfUpdateCmd }),
            Cmd("check", new Spec { Help = "whether the game folder is exactly what the library asks for: what is missing, " +
                                           "what is there that no mod wants (another manager's, or an interrupted deploy), and " +
                                           "what is not the file the library holds (--deep reads the bytes of the copies)",
                                    Flags = new[] { "--deep" }, Run = CheckCmd }),
            Cmd("purge", new Spec { Help = "delete every patch file from the game: vanilla", Flags = new[] { "--yes" }, Run = Purge }),
            Cmd("which", new Spec { Help = "which mod a patch file or archive comes from", Positionals = new[] { "what" }, Run = Which }),
            Cmd("state", new Spec { Help = "everything about the library and the game folder, as JSON (for a window)",
                                    Flags = new[] { "--verify" }, Run = State }),
            Cmd("report", new Spec { Help = "a zip to send with a question: the state, the deploy history, Boot Mod Loader's log and the crash dumps' names (never the dumps, never the sign-in; your user folder written as %USERPROFILE%); on the desktop unless --out",
                                     Values = new[] { "--out" }, Run = ReportCmd }),
            Cmd("conflicts", new Spec { Help = "resources that more than one mod ships (the last in the order wins)",
                                        Flags = new[] { "-v", "--verbose" }, Run = Conflicts }),
        };

        static KeyValuePair<string, Spec> Cmd(string name, Spec s) => new KeyValuePair<string, Spec>(name, s);

        // the commands only our own builds have, from their part in private/ (not in the repository)
        static Commands() => OurCommands(Specs);
        static partial void OurCommands(List<KeyValuePair<string, Spec>> specs);

        static string Usage() =>
            "usage: hd2qd [--version] {" + string.Join(",", Specs.Select(s => s.Key)) + "} ...";

        static string Help()
        {
            var lines = new List<string>
            {
                Usage(), "",
                "A command-line mod manager for Helldivers 2. Deploys incrementally: only the files that change are touched.", "",
                "commands:",
            };
            foreach (var kv in Specs) lines.Add("  " + kv.Key.PadRight(16) + kv.Value.Help);
            return string.Join(Environment.NewLine, lines);
        }

        static string CommandHelp(string name, Spec s)
        {
            var parts = new List<string> { "usage: hd2qd " + name };
            parts.AddRange(s.Flags.Select(f => "[" + f + "]"));
            parts.AddRange(s.Values.Select(v => "[" + v + " " + v.TrimStart('-').Replace('-', '_').ToUpperInvariant() + "]"));
            parts.AddRange(s.Positionals.Select((p, i) => s.Many && i == s.Positionals.Length - 1 ? p + " [" + p + " ...]" : p));
            return string.Join(" ", parts) + Environment.NewLine + Environment.NewLine + s.Help;
        }

        /// <summary>Runs one command line; 0 done, 1 refused (a message on err), 2 a wrong command line.</summary>
        public static int Run(string[] argv, TextWriter output, TextWriter err) => Run(argv, output, err, out _);

        /// <summary>The commands that only read: everything else writes the library, the deploy
        /// record or the game folder.</summary>
        static readonly HashSet<string> ReadOnly = new HashSet<string>
        {
            "folders", "list", "tags", "groups", "status", "check", "which", "state", "conflicts", "profiles", "report", "nexus-info",
        };

        static bool Writes(string command, Args a) =>
            !ReadOnly.Contains(command) && !(command == "leftovers" && !a.Flag("--delete"));

        /// <summary>The same; refused is the refusal, for the window to say in its own words.</summary>
        public static int Run(string[] argv, TextWriter output, TextWriter err, out ModError refused)
        {
            refused = null;
            if (argv.Length == 0) { err.WriteLine(Usage()); err.WriteLine("hd2qd: error: a command is required"); return 2; }
            if (argv[0] == "--version") { output.WriteLine("hd2-mod-cli " + StateDoc.Version); return 0; }
            if (argv[0] == "-h" || argv[0] == "--help") { output.WriteLine(Help()); return 0; }
            var spec = Specs.FirstOrDefault(s => s.Key == argv[0]).Value;
            if (spec == null)
            {
                err.WriteLine(Usage());
                err.WriteLine("hd2qd: error: no command " + Ops.Repr(argv[0]));
                return 2;
            }
            Args a;
            try
            {
                if (argv.Skip(1).Any(x => x == "-h" || x == "--help")) { output.WriteLine(CommandHelp(argv[0], spec)); return 0; }
                a = Parse(spec, argv.Skip(1).ToArray());
            }
            catch (UsageError e)
            {
                err.WriteLine(CommandHelp(argv[0], spec).Split('\n')[0].TrimEnd());
                err.WriteLine("hd2qd " + argv[0] + ": error: " + e.Message);
                return 2;
            }
            try
            {
                if (Writes(argv[0], a))
                {
                    var elsewhere = Store.Redirected();
                    if (elsewhere != null)
                        throw new ModError("this was started from inside a packaged app, and Windows sends its writes to " +
                                           "AppData to that app's own copy (" + elsewhere + "), which no other program reads: " +
                                           "the library would split in two. Run it from your own terminal or the window.", "redirected");
                }
                spec.Run(a, output);
                return 0;
            }
            catch (ModError e)
            {
                err.WriteLine("hd2qd: " + e.Message);
                refused = e;
                return 1;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception
                                      || e is FormatException || e is InvalidDataException)
            {
                err.WriteLine("hd2qd: " + e.Message);
                return 1;
            }
        }

        static Args Parse(Spec spec, string[] argv)
        {
            var a = new Args();
            var pos = new List<string>();
            bool onlyPositionals = false;
            for (int i = 0; i < argv.Length; i++)
            {
                var x = argv[i];
                if (!onlyPositionals && x == "--") { onlyPositionals = true; continue; }
                if (!onlyPositionals && x.StartsWith("-") && x.Length > 1 && !IsNumber(x))
                {
                    string name = x, inline = null;
                    int eq = x.IndexOf('=');
                    if (x.StartsWith("--") && eq > 0) { name = x.Substring(0, eq); inline = x.Substring(eq + 1); }
                    if (spec.Flags.Contains(name) && inline == null) a.Flags.Add(name == "-v" ? "--verbose" : name);
                    else if (spec.Values.Contains(name))
                    {
                        if (inline == null)
                        {
                            if (i + 1 >= argv.Length) throw new UsageError("argument " + name + ": expected one argument");
                            inline = argv[++i];
                        }
                        a.Values[name] = inline;
                    }
                    else throw new UsageError("unrecognized arguments: " + x);
                    continue;
                }
                pos.Add(x);
            }
            var given = spec.OneOf.Where(o => a.Flags.Contains(o) || a.Values.ContainsKey(o)).ToList();
            if (given.Count > 1) throw new UsageError("argument " + given[1] + ": not allowed with argument " + given[0]);
            int need = spec.Positionals.Length;
            if (pos.Count < need || (!spec.Many && pos.Count > need))
            {
                if (pos.Count > need) throw new UsageError("unrecognized arguments: " + string.Join(" ", pos.Skip(need)));
                throw new UsageError("the following arguments are required: " + string.Join(", ", spec.Positionals.Skip(pos.Count)));
            }
            for (int i = 0; i < need; i++) a.Pos[spec.Positionals[i]] = pos[i];
            if (spec.Many) a.Rest = pos.Skip(need - 1).ToList();
            if (a.Values.TryGetValue("--to", out var to) && !int.TryParse(to, out _))
                throw new UsageError("argument --to: invalid int value: " + Ops.Repr(to));
            if (a.Pos.TryGetValue("value", out var v) && v != "on" && v != "off")
                throw new UsageError("argument value: invalid choice: " + Ops.Repr(v) + " (choose from 'on', 'off')");
            return a;
        }

        static bool IsNumber(string x) => x.Length > 1 && x.Skip(1).All(char.IsDigit);

        // ------------------------------------------------------------ the commands

        static void Init(Args a, TextWriter o)
        {
            var cfg = Store.Config();
            // --game may name the game folder, its data or its bin; a saved folder that is
            // gone is looked for again
            var given = a.Value("--game");
            var g = !string.IsNullOrEmpty(given) ? Ops.GameFolderOf(given) : Ops.GameFolderOf(Json.Str(cfg, "game_dir")) ?? Ops.FindGame();
            if (g == null)
                throw new ModError(!string.IsNullOrEmpty(given)
                    ? "not Helldivers 2's folder (it has no data folder): " + given
                    : "the game folder was not found: pass --game \"<...>\\steamapps\\common\\Helldivers 2\"", "no_game");
            cfg["game_dir"] = Paths.Abs(g);
            if (!string.IsNullOrEmpty(a.Value("--mods-dir"))) cfg["mods_dir"] = Paths.Abs(a.Value("--mods-dir"));
            Store.Save("config.json", cfg);
            o.WriteLine("game: " + cfg["game_dir"]);
            o.WriteLine("library folder for zips: " + Store.ModsDir(cfg));
            o.WriteLine("settings: " + Store.Home);
        }

        static void ImportArsenal(Args a, TextWriter o)
        {
            if (a.Flag("--profiles"))
            {
                ImportProfiles(Importers.ArsenalProfiles(a.Value("--file")), a.Flag("--replace"), "HD2 Arsenal", o);
                return;
            }
            var (theirs, g) = Ops.ImportArsenal(a.Value("--file"), a.Value("--profile"));
            Import(theirs, a.Flag("--replace"), "HD2 Arsenal", o,
                   Ops.ArsenalTopWins(a.Value("--file"))
                       ? "HD2 Arsenal gives its first mod the highest priority: the list came in the other way round, so the same mods win as there."
                       : null);
            var cfg = Store.Config();
            if (!string.IsNullOrEmpty(g) && string.IsNullOrEmpty(Json.Str(cfg, "game_dir")))
            {
                cfg["game_dir"] = g;
                Store.Save("config.json", cfg);
            }
        }

        /// <summary>A library folder added, and what it holds told (the user, 2026-10-25: as Steam
        /// finds the games in a library folder it is given).</summary>
        static void FolderAdd(Args a, TextWriter o)
        {
            var p = Storage.AddFolder(a.Pos["path"]);
            o.WriteLine("added: " + p);
            var finds = Storage.Look(p, Library.Load());
            foreach (var x in finds.Problems)
                o.WriteLine("in it: " + x.Name + "'s mods, but its list (" + x.Where + ") cannot be read: " + x.Problem);
            if (finds.NeedsConfig != null)
            {
                var (name, file, usual) = Importers.ConfigOf(finds.NeedsConfig);
                o.WriteLine("in it: " + name + "'s mods, but its list (" + file + ") was not found where it usually is (" + usual +
                            "): hd2qd import-" + finds.NeedsConfig + (finds.NeedsConfig == "hd2mm" ? " --dir <its folder>" : " --file <its " + file + ">"));
                return;
            }
            foreach (var m in finds.Managers)
            {
                if (!a.Flag("--import"))
                {
                    o.WriteLine("in it: " + m.Name + "'s list, " + m.Mods + " mods not in the library: hd2qd import-" + m.Key +
                                (m.Dir != null ? " --dir " + Ops.Repr(m.Dir) : ""));
                    continue;
                }
                var args = new Args();
                if (m.Dir != null) args.Values["--dir"] = m.Dir;
                if (m.Key == "arsenal") ImportArsenal(args, o);
                else if (m.Key == "hd2mm") ImportHd2mm(args, o);
                else if (m.Key == "echelon") ImportEchelon(args, o);
            }
            if (finds.Loose.Count == 0) return;
            if (!a.Flag("--import")) { o.WriteLine("in it: " + finds.Loose.Count + " mod folders no manager lists: hd2qd add-loose " + Ops.Repr(p)); return; }
            AddLoose(new Args { Pos = { ["path"] = p } }, o);
        }

        static void FolderPath(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var (followed, missing) = Storage.ChangeFolder(lib, a.Pos["path"], a.Pos["to"]);
            lib.Save();
            o.WriteLine(followed + " mods now in " + Paths.Abs(a.Pos["to"]) + (missing > 0 ? "; " + missing + " not found there, left where they were" : ""));
        }

        static void AddLoose(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var added = Storage.AddLoose(lib, Storage.Look(a.Pos["path"], lib).Loose);
            lib.Save();
            foreach (var m in added) o.WriteLine("added " + m.Name + "  (" + m.Path + ")");
            o.WriteLine(added.Count + " mod folders added where they are");
        }

        static void ImportHd2mm(Args a, TextWriter o) => Import(Importers.ReadHd2mm(a.Value("--dir")), a.Flag("--replace"), "HD2ModManager", o);

        static void ImportEchelon(Args a, TextWriter o)
        {
            var profiles = a.Flag("--profiles") ? Importers.EchelonProfiles(a.Value("--file")) : null;
            if (profiles != null) ImportProfiles(profiles, a.Flag("--replace"), "Echelon", o);
            else Import(Importers.ReadEchelon(a.Value("--file")), a.Flag("--replace"), "Echelon", o);
        }

        /// <summary>Another manager's profiles, every one, into the library (the user, 2026-10-25).
        /// A library that had mods and no profiles keeps them as a profile Default first.</summary>
        static void ImportProfiles(ImportedProfiles imp, bool replace, string who, TextWriter o)
        {
            var lib = replace ? new Library() : Library.Load();
            bool hadMods = lib.Mods.Count > 0;
            if (hadMods && !lib.ProfilesOn) Profiles.TurnOn(lib);
            var (added, skipped, made) = Importers.MergeWithProfiles(lib, imp);
            foreach (var m in added) Importers.CopyPictures(m);
            lib.Save();
            o.WriteLine(added.Count + " mods added from " + who + ", " + skipped.Count + " already in the library. " + who + "'s files were only read.");
            foreach (var p in made)
                o.WriteLine((string.Equals(p.Name, lib.Active, StringComparison.CurrentCultureIgnoreCase) ? "* " : "  ") + "profile " + p.Name +
                            "  (" + p.Order.Count + " mods, " + p.On.Count + " on)");
            int cross = hadMods && added.Count > 0 ? Importers.CrossClashes(lib, added.ToDictionary(m => m.Id, m => 1)) : 0;
            if (cross > 0)
                o.WriteLine(cross + (cross == 1 ? " conflict" : " conflicts") + " between mods from " + who +
                            " and mods already in the library: the one lower in the list, from " + who + ", wins. See them with: hd2qd conflicts");
            o.WriteLine("From now on deploy with hd2qd only: a deploy from " + who + " would undo this one's.");
        }

        /// <summary>Another manager's list into the library: added (the mods not in it yet), or, with
        /// replace, instead of it.</summary>
        static void Import(Library theirs, bool replace, string who, TextWriter o, string note = null)
        {
            var lib = replace ? new Library() : Library.Load();
            bool hadMods = lib.Mods.Count > 0;
            var (added, skipped) = Importers.Merge(lib, theirs);
            foreach (var m in added) Importers.CopyPictures(m);
            lib.Save();
            o.WriteLine(added.Count + " mods added from " + who + " (" + added.Count(m => m.Enabled) + " on), " + skipped.Count +
                        " already in the library. " + who + "'s files were only read.");
            if (note != null && added.Count > 0) o.WriteLine(note);
            // mods from another manager meet the ones already here for the first time
            int cross = hadMods && added.Count > 0 ? Importers.CrossClashes(lib, added.ToDictionary(m => m.Id, m => 1)) : 0;
            if (cross > 0)
                o.WriteLine(cross + (cross == 1 ? " conflict" : " conflicts") + " between mods from " + who +
                            " and mods already in the library: the one lower in the list, from " + who + ", wins. See them with: hd2qd conflicts");
            o.WriteLine("From now on deploy with hd2qd only: a deploy from " + who + " would undo this one's.");
        }

        static void FoldersCmd(Args a, TextWriter o)
        {
            foreach (var f in Storage.Folders(Library.Load(), Store.GameDir(null, false)))
                o.WriteLine((f.IsDefault ? "* " : "  ") + f.Path + "  [" + f.Mods.Count + " mods, " + f.Bytes + " bytes; drive " + f.Drive + " " + f.Free + " free" +
                            (f.GameDrive ? "; the game's drive: hard links" : "; another drive than the game: copies") + (f.InAppData ? "; in AppData" : "") + "]");
        }

        static void RelocateCmd(Args a, TextWriter o)
        {
            var to = a.Value("--into");
            if (string.IsNullOrEmpty(to)) throw new ModError("say where: --into FOLDER");
            var lib = Library.Load();
            var mods = a.Rest.Select(r => Ops.Pick(lib, r)).Distinct().ToList();
            var g = Store.GameDir(null, false);
            var moved = Storage.Move(lib, mods, to, a.Flag("--keep"), g);
            foreach (var (m, from, dest) in moved) o.WriteLine((a.Flag("--keep") ? "copied " : "moved ") + m.Name + ": " + from + " -> " + dest);
            if (moved.Count > 0 && g != null)
            {
                try
                {
                    var r = Deploy.Relink(Library.Load(), g);
                    if (r.Linked > 0) o.WriteLine(r.Linked + " copies in the game are hard links now: " + r.Bytes + " bytes came back");
                }
                catch (ModError e) { o.WriteLine("the game's copies stay copies for now (" + e.Message + "): run  hd2qd relink  later"); }
            }
        }

        static void List(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var files = new Dictionary<string, List<string>>();
            if (a.Flag("--files"))
                foreach (var w in Deploy.Plan(lib))
                {
                    if (!files.TryGetValue(w.Mod, out var l)) files[w.Mod] = l = new List<string>();
                    l.Add(w.Name);
                }
            int i = 0;
            foreach (var m in lib.Mods)
            {
                o.WriteLine((++i).ToString().PadLeft(3) + " " + (m.Enabled ? "[x]" : "[ ]") + " " + m.Name);
                if (a.Flag("--paths")) o.WriteLine("        " + m.Path);
                if (files.TryGetValue(m.Name, out var mine))
                    foreach (var f in mine.Where(f => Deploy.Patch.IsMatch(f))) o.WriteLine("        " + f);
            }
        }

        static void Add(Args a, TextWriter o)
        {
            var cfg = Store.Config();
            var lib = Library.Load();
            var path = a.Pos["path"];
            // a folder of mods (another manager's store, as HD2 Arsenal's): taken as one mod it became
            // one entry holding all of them (players, 2026-10-26); it is a library folder
            if (Directory.Exists(path) && !a.Flag("--whole"))
            {
                var held = Ops.ModsInside(path);
                if (held >= 2)
                    throw new ModError(Ops.Repr(path) + " holds " + held + " mods, each in a folder of its own: add it as a library folder  (hd2qd folder-add)" +
                                       ", or as one mod with --whole", "library_folder", Paths.Abs(path));
            }
            var folder = Ops.SourceFolder(path, cfg, a.Flag("--copy"));
            var mod = Ops.NewMod(folder, a.Value("--name"), Paths.Abs(path));
            // A stable manifest identity survives versioned names and the player's rename.
            // --name explicitly requests a separate entry (for example a variant).
            var identity = Manifest.Read(folder)?.Guid;
            if (a.Value("--name") == null && !string.IsNullOrWhiteSpace(identity))
            {
                var matches = lib.Mods.Where(m =>
                {
                    try { return string.Equals(Manifest.Read(m.Path)?.Guid, identity, StringComparison.OrdinalIgnoreCase); }
                    catch (Exception e) when (e is ModError || e is IOException || e is UnauthorizedAccessException) { return false; }
                }).ToList();
                if (matches.Count > 1)
                    throw new ModError("Several library entries have this manifest identity; select the entry to replace explicitly.");
                if (matches.Count == 1)
                {
                    a.Pos["mod"] = matches[0].Id;
                    Replace(a, o);
                    return;
                }
            }
            if (File.Exists(path)) Origins.FromFile(mod, path);               // a download: where it came from
            if (lib.Mods.Any(m => m.Name == mod.Name))
                throw new ModError("a mod named " + Ops.Repr(mod.Name) + " is in the library: use  hd2qd replace  or --name", "duplicate", mod.Name);
            lib.Mods.Add(mod);
            lib.Save();
            o.WriteLine("added " + lib.Mods.Count + " " + mod.Name + "  (" + folder + ")");
        }

        static void Replace(Args a, TextWriter o)
        {
            var cfg = Store.Config();
            var lib = Library.Load();
            var mod = Ops.Pick(lib, a.Pos["mod"]);
            var had = mod.Path;
            var folder = Ops.SourceFolder(a.Pos["path"], cfg, a.Flag("--copy"));
            var manifest = Manifest.Read(folder);
            bool kept = Versions.Keep(lib, cfg, mod, folder);
            mod.Options = Manifest.Merged(manifest, mod.Options);
            mod.Path = folder;
            mod.Source = Paths.Abs(a.Pos["path"]);
            if (File.Exists(a.Pos["path"]))
            {
                var fresh = new Mod();
                Origins.FromFile(fresh, a.Pos["path"]);
                if (fresh.Nexus != null || fresh.GitHub != null || fresh.Page != null) { mod.Nexus = fresh.Nexus; mod.GitHub = fresh.GitHub; mod.Page = fresh.Page; }
            }
            if (!string.IsNullOrEmpty(a.Value("--name"))) mod.Name = a.Value("--name");
            else if (!string.IsNullOrEmpty(manifest?.Name))
            {
                // a name the player gave the mod stays; only the name it came with is the new
                // version's (the user, 2026-10-25: a replace put "Boot Mod Loader v0.9.0" over
                // the player's "Boot Mod Loader" and left the old version as the name it came with)
                if (mod.RealName != null) mod.RealName = string.Equals(manifest.Name, mod.Name, StringComparison.Ordinal) ? null : manifest.Name;
                else mod.Name = manifest.Name;
            }
            lib.Save();
            o.WriteLine("replaced: " + mod.Name + "  (" + folder + "), same place and options");
            if (kept) o.WriteLine("the version before is kept: hd2qd rollback " + Ops.Repr(mod.Name) + " goes back to it");
            else if (!a.Flag("--keep-old")) DropOld(lib, cfg, had, folder, o);
        }

        /// <summary>The folder the old version was unpacked into, once the library no longer
        /// points at it: an armour pack leaves hundreds of megabytes behind otherwise (the user,
        /// 2026-10-25). Only a folder this program unpacked itself, and only when no other mod
        /// uses it.</summary>
        static void DropOld(Library lib, Dictionary<string, object> cfg, string had, string now, TextWriter o)
        {
            if (string.IsNullOrEmpty(had) || string.Equals(Paths.Abs(had), Paths.Abs(now), StringComparison.OrdinalIgnoreCase)) return;
            if (Versions.Used(lib).Contains(Paths.Abs(had))) return;
            if (!Ops.Ours(had, cfg)) return;
            if (Ops.Drop(had)) o.WriteLine("the old version's folder is gone: " + had);
        }

        static void KeepPrevious(TextWriter o, bool on)
        {
            Versions.SetKeep(on);
            o.WriteLine(on ? "mods with a script now keep the version they had when a new one comes" : "earlier versions are no longer kept (the ones kept stay)");
        }

        static void PreviousCmd(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var kept = lib.Mods.Where(m => m.Previous != null).ToList();
            if (!a.Flag("--delete"))
            {
                if (kept.Count == 0) { o.WriteLine("no earlier version is kept"); return; }
                foreach (var m in kept)
                {
                    long bytes = Directory.Exists(m.Previous.Path) ? StateDoc.LibraryBytes(new DirectoryInfo(m.Previous.Path)) : 0;
                    o.WriteLine(m.Name + "  " + (m.Previous.Version ?? "?") + "  " + bytes + " bytes  kept " +
                                DateTimeOffset.FromUnixTimeSeconds(m.Previous.Kept).LocalDateTime.ToString("yyyy-MM-dd HH:mm") + "  " + m.Previous.Path);
                }
                return;
            }
            if (a.Rest.Count == 0 && !a.Flag("--all")) throw new ModError("name the mods whose earlier version goes, or pass --all");
            var go = a.Flag("--all") ? kept : a.Rest.Select(r => Ops.Pick(lib, r)).Distinct().ToList();
            var done = Versions.Forget(lib, go);
            lib.Save();
            foreach (var (m, folder, bytes) in done) o.WriteLine("deleted the earlier version of " + m.Name + " (" + bytes + " bytes): " + folder);
        }

        static void RollbackCmd(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var mod = Ops.Pick(lib, a.Pos["mod"]);
            Versions.Rollback(lib, mod);
            lib.Save();
            o.WriteLine("back to " + mod.Name + " " + (Origins.VersionOf(mod).Version ?? "") + "  (" + mod.Path + "); " +
                        (mod.Previous.Version ?? "the version it had") + " is kept instead. Deploy to bring the game up to date");
        }

        static void SkipCmd(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var mod = Ops.Pick(lib, a.Pos["mod"]);
            if (a.Flag("--clear")) { mod.Skip = null; lib.Save(); o.WriteLine(mod.Name + ": every newer version is offered again"); return; }
            mod.Skip = null;
            var u = Updates.One(mod);
            if (u.Status != "update") throw new ModError(mod.Name + " has no newer version to skip (" + u.Status + ")", "no_update", mod.Name);
            Versions.Skip(mod, u);
            lib.Save();
            o.WriteLine(mod.Name + ": " + u.Latest + " is skipped; a newer one is offered again");
        }

        static void LeftoverCmd(Args a, TextWriter o)
        {
            var cfg = Store.Config();
            var lib = Library.Load();
            var left = Ops.Leftovers(lib, cfg);
            if (!a.Flag("--delete"))
            {
                if (left.Count == 0) { o.WriteLine("no folders of old versions are left over"); return; }
                foreach (var d in left) o.WriteLine(StateDoc.LibraryBytes(new DirectoryInfo(d)).ToString().PadLeft(12) + " bytes  " + d);
                return;
            }
            var named = a.Rest.Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).ToList();
            if (named.Count == 0 && !a.Flag("--all")) throw new ModError("name the folders to delete, or pass --all");
            var go = named.Count > 0
                ? left.Where(d => named.Any(n => string.Equals(Paths.Abs(d), Paths.Abs(n), StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(System.IO.Path.GetFileName(d), n, StringComparison.OrdinalIgnoreCase))).ToList()
                : left;
            if (go.Count == 0) throw new ModError("no folder of an old version goes by that name");
            long freed = 0;
            int n = 0;
            foreach (var d in go)
            {
                var size = StateDoc.LibraryBytes(new DirectoryInfo(d));
                if (!Ops.Drop(d)) { o.WriteLine("cannot delete (in use?): " + d); continue; }
                freed += size;
                n++;
            }
            o.WriteLine("deleted " + n + " folder" + (n == 1 ? "" : "s") + ": " + freed + " bytes came back; the game is untouched");
        }

        /// <summary>A name of the player's own for a mod (the user, 2026-10-24). The name it came
        /// with is kept, so the details can say both; renaming it back to that name drops it.</summary>
        static void Rename(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var mod = Ops.Pick(lib, a.Pos["mod"]);
            var name = (a.Pos["name"] ?? "").Trim();
            if (name.Length == 0) throw new ModError("a mod needs a name", "no_name");
            if (lib.Mods.Any(x => x != mod && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ModError("another mod is already called " + Ops.Repr(name), "name_taken");
            var was = mod.Name;
            if (string.Equals(name, was, StringComparison.Ordinal)) { o.WriteLine("already called " + Ops.Repr(name)); return; }
            if (mod.RealName == null) mod.RealName = was;
            mod.Name = name;
            if (string.Equals(mod.RealName, name, StringComparison.Ordinal)) mod.RealName = null;
            lib.Save();
            o.WriteLine("renamed " + Ops.Repr(was) + " to " + Ops.Repr(name));
        }

        /// <summary>A mod matched against its own source, the way a reference manager fills a paper
        /// in (the user, 2026-10-24). --all does every mod that has a source worth asking.</summary>
        static void Match(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var mods = a.Flag("--all") ? lib.Mods.Where(m => m.Nexus != null || m.GitHub != null).ToList()
                                       : new List<Mod> { Ops.Pick(lib, a.Pos["mod"]) };
            int done = 0;
            foreach (var mod in mods)
            {
                var was = mod.Name;
                try
                {
                    var said = Site.Fill(lib, mod);
                    done++;
                    o.WriteLine(was + ": " + (said.Count == 0 ? "already as the site has it" : string.Join("; ", said)));
                }
                catch (ModError e)
                {
                    if (mods.Count == 1) throw;
                    o.WriteLine(was + ": " + e.Message);
                }
            }
            if (done > 0) lib.Save();
        }

        // ------------------------------------------------------------ the player's own labels

        static List<string> Names(Args a) =>
            a.Rest.Skip(1).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).ToList();

        static void TagNew(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var name = (a.Pos["name"] ?? "").Trim();
            if (name.Length == 0) throw new ModError("a label needs a name", "no_name");
            if (!lib.Tags.Any(t => string.Equals(t, name, StringComparison.CurrentCultureIgnoreCase)) &&
                !lib.Mods.Any(m => m.Tags.Any(t => string.Equals(t, name, StringComparison.CurrentCultureIgnoreCase))))
            {
                lib.Tags.Add(name);
                lib.Save();
                o.WriteLine("new label: " + name);
                return;
            }
            o.WriteLine("already a label: " + name);
        }

        static void TagList(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var counts = lib.Tags.Select(t => (Name: t, N: 0)).ToList();
            foreach (var mod in lib.Mods)
                foreach (var t in mod.Tags)
                {
                    int i = counts.FindIndex(x => string.Equals(x.Name, t, StringComparison.OrdinalIgnoreCase));
                    if (i < 0) counts.Add((t, 1));
                    else counts[i] = (counts[i].Name, counts[i].N + 1);
                }
            foreach (var (name, n) in counts.OrderByDescending(x => x.N).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                o.WriteLine(n.ToString().PadLeft(4) + "  " + name);
            if (counts.Count == 0) o.WriteLine("no labels yet");
        }

        static void Tag(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var mod = Ops.Pick(lib, a.Rest[0]);
            var names = Names(a);
            if (names.Count == 0) { o.WriteLine(mod.Name + ": " + (mod.Tags.Count == 0 ? "no labels" : string.Join(", ", mod.Tags))); return; }
            foreach (var name in names)
            {
                if (!mod.Tags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase))) mod.Tags.Add(name);
                if (!lib.Tags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase))) lib.Tags.Add(name);
            }
            lib.Save();
            o.WriteLine(mod.Name + ": " + string.Join(", ", mod.Tags));
        }

        /// <summary>One label onto several mods in one call, so a library can be labelled in
        /// one go instead of mod by mod (the user's tester, 2026-10-25).</summary>
        static void TagMany(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var name = (a.Pos["name"] ?? "").Trim();
            if (name.Length == 0) throw new ModError("a label needs a name");
            var mods = a.Rest.Skip(1).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Select(x => Ops.Pick(lib, x)).ToList();
            if (mods.Count == 0) throw new ModError("tag-many needs at least one mod");
            foreach (var mod in mods)
                if (!mod.Tags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase))) mod.Tags.Add(name);
            if (!lib.Tags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase))) lib.Tags.Add(name);
            lib.Save();
            o.WriteLine(name + ": " + string.Join(", ", mods.Select(m => m.Name)));
        }

        static void Untag(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var mod = Ops.Pick(lib, a.Rest[0]);
            if (a.Flag("--all")) mod.Tags.Clear();
            else foreach (var name in Names(a)) mod.Tags.RemoveAll(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
            lib.Save();
            o.WriteLine(mod.Name + ": " + (mod.Tags.Count == 0 ? "no labels" : string.Join(", ", mod.Tags)));
        }

        static void TagRename(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var was = a.Pos["name"];
            var now = (a.Pos["to"] ?? "").Trim();
            if (now.Length == 0) throw new ModError("a label needs a name", "no_name");
            int n = 0;
            foreach (var mod in lib.Mods)
                for (int i = 0; i < mod.Tags.Count; i++)
                    if (string.Equals(mod.Tags[i], was, StringComparison.OrdinalIgnoreCase))
                    {
                        mod.Tags[i] = now;
                        n++;
                    }
            for (int i = 0; i < lib.Tags.Count; i++)
                if (string.Equals(lib.Tags[i], was, StringComparison.OrdinalIgnoreCase)) lib.Tags[i] = now;
            lib.Tags = lib.Tags.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var mod in lib.Mods)
            {
                var seen = new List<string>();
                foreach (var t in mod.Tags) if (!seen.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) seen.Add(t);
                mod.Tags.Clear();
                mod.Tags.AddRange(seen);
            }
            lib.Save();
            o.WriteLine("renamed the label on " + n + " mods: " + was + " -> " + now);
        }

        static void TagDelete(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var name = a.Pos["name"];
            int n = lib.Mods.Count(m => m.Tags.RemoveAll(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)) > 0);
            lib.Tags.RemoveAll(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
            lib.Save();
            o.WriteLine("took the label off " + n + " mods: " + name);
        }

        // ---------------------------------------------------------------- groups (Groups)

        static void GroupList(Args a, TextWriter o)
        {
            var lib = Library.Load();
            void Show(string parent, int depth)
            {
                foreach (var g in lib.Groups.Where(g => g.Parent == parent))
                {
                    int n = lib.Mods.Count(m => m.Group != null && (m.Group == g.Id || Groups.Inside(lib, m.Group, g.Id)));
                    o.WriteLine(new string(' ', depth * 2) + g.Name + "/  (" + n + (n == 1 ? " mod" : " mods") + ")");
                    Show(g.Id, depth + 1);
                    foreach (var m in lib.Mods.Where(m => m.Group == g.Id)) o.WriteLine(new string(' ', depth * 2 + 2) + m.Name);
                }
            }
            Show(null, 0);
            var loose = lib.Mods.Where(m => m.Group == null).ToList();
            if (lib.Groups.Count == 0) o.WriteLine("no groups yet");
            else if (loose.Count > 0) o.WriteLine("in no group: " + string.Join("; ", loose.Select(m => m.Name)));
        }

        static void GroupNew(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var into = a.Value("--in") != null ? Groups.Pick(lib, a.Value("--in")).Id : null;
            var g = Groups.New(lib, a.Pos["name"], into);
            lib.Save();
            o.WriteLine("new group: " + Groups.PathText(lib, g.Id) + "  (" + g.Id + ")");
        }

        static void GroupRename(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var g = Groups.Pick(lib, a.Pos["group"]);
            var was = Groups.PathText(lib, g.Id);
            Groups.Rename(lib, g, a.Pos["name"]);
            lib.Save();
            o.WriteLine("renamed: " + was + " -> " + Groups.PathText(lib, g.Id));
        }

        static void GroupMove(Args a, TextWriter o)
        {
            if (a.Value("--in") == null && !a.Flag("--top")) throw new ModError("say where: --in a group, or --top");
            var lib = Library.Load();
            var g = Groups.Pick(lib, a.Pos["group"]);
            Groups.Move(lib, g, a.Flag("--top") ? null : Groups.Pick(lib, a.Value("--in")).Id);
            lib.Save();
            o.WriteLine("moved: " + Groups.PathText(lib, g.Id));
        }

        static void GroupDelete(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var g = Groups.Pick(lib, a.Pos["group"]);
            var was = Groups.PathText(lib, g.Id);
            var up = g.Parent != null ? Groups.PathText(lib, g.Parent) : null;
            Groups.Delete(lib, g);
            lib.Save();
            o.WriteLine("deleted the group " + was + "; what it held is now " + (up != null ? "in " + up : "at the top"));
        }

        /// <summary>Mods into a group: the window's drag onto a folder. Nothing moves on disk.</summary>
        static void GroupPut(Args a, TextWriter o)
        {
            if (a.Value("--in") == null && !a.Flag("--top")) throw new ModError("say where: --in a group, or --top");
            var lib = Library.Load();
            var to = a.Flag("--top") ? null : Groups.Pick(lib, a.Value("--in"));
            var mods = a.Rest.Select(r => Ops.Pick(lib, r)).Distinct().ToList();
            foreach (var m in mods) m.Group = to?.Id;
            lib.Save();
            o.WriteLine((to != null ? Groups.PathText(lib, to.Id) : "no group") + ": " + string.Join("; ", mods.Select(m => m.Name)));
        }

        static void Remove(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var mod = Ops.Pick(lib, a.Pos["mod"]);
            lib.Mods.Remove(mod);
            lib.Save();
            o.WriteLine("removed " + mod.Name + " (its files stay where they are; deploy to take it out of the game)");
        }

#if HAS_NEXUS
        static void NexusCmd(Args a, TextWriter o)
        {
            var link = NxmLink.Parse(a.Pos["link"]);
            var (mod, replaced) = Nexus.Install(link, Nexus.Credential());
            o.WriteLine((replaced ? "new version: " : "added: ") + mod.Name + "  (" + mod.Path + ")");
        }
#endif

#if HAS_NEXUS

        static void NexusInfoCmd(Args a, TextWriter o)
        {
            var n = Nexus.ModNumber(a.Pos["number"]);
            if (n == null) throw new ModError("not a Nexus Mods number or page: " + a.Pos["number"], "not_number", a.Pos["number"]);
            var m = Nexus.LookUp(n.Value);
            o.WriteLine((m.Name ?? "?") + "  (Nexus " + m.Mod + ")");
            var by = new List<string>();
            if (m.Author != null) by.Add("by " + m.Author + (m.Uploader != null && m.Uploader != m.Author ? " (uploaded by " + m.Uploader + ")" : ""));
            if (m.Version != null) by.Add("version " + m.Version);
            if (m.Updated > 0) by.Add("updated " + DateTimeOffset.FromUnixTimeSeconds(m.Updated).LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            if (by.Count > 0) o.WriteLine("  " + string.Join(" · ", by));
            o.WriteLine("  " + m.Downloads.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " downloads · " + m.Endorsements.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " endorsements" +
                        (m.Adult ? " · adult content" : "") + (m.Available ? "" : " · not available: " + m.StatusWords));
            o.WriteLine("  page:  " + m.Page);
            o.WriteLine("  files: " + m.FilesPage);
            if (m.Summary != null) o.WriteLine("  " + m.Summary.Replace("\n", "\n  "));
            var had = Library.Load().Mods.FirstOrDefault(x => x.Nexus != null && x.Nexus.Game == Nexus.Game && x.Nexus.Mod == m.Mod);
            o.WriteLine(had != null ? "in the library: " + had.Name : "not in the library");
        }

        static void NexusLoginCmd(Args a, TextWriter o)
        {
            o.WriteLine("your browser opens Nexus Mods: allow HD2 Mod Quick Deploy there");
            var (name, premium) = NexusOAuth.Login(url => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }),
                                                   TimeSpan.FromMinutes(5));
            o.WriteLine("signed in as " + name + " (" + (premium ? "premium" : "free") + " account)");
        }

        static void NexusLogoutCmd(Args a, TextWriter o)
        {
            NexusOAuth.Logout();
            o.WriteLine("signed out of Nexus Mods here; the access can be taken back on Nexus Mods as well");
        }
#endif

        static string LoaderName(string kind, ModLua ml) =>
            kind == "bml" ? "Boot Mod Loader" + (ml?.Boot?.BootVersion != null ? " " + ml.Boot.BootVersion : "")
            : kind == "chef" ? "Chef Shared Loader" : kind == "chef_stub" ? "Chef Shared Loader's start-up stub"
            : kind == "bsl" ? "Bingus Shared Loader" : kind == "mdl3" ? "MDL3" + (ml?.Boot?.BootVersion != null ? " " + ml.Boot.BootVersion : "")
            : kind == "other" ? (ml != null ? ml.Mod.Name + "'s own start-up script" : "another start-up script")
            : "none (the game's own)";

        static void LuaCmd(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var all = LuaMods.Of(lib);
            var (owner, boots, running, issues) = LuaMods.Check(lib, all);
            o.WriteLine("start-up script (boot): " + (owner == null ? "the game's own" : owner.Mod.Name + " (" + LoaderName(owner.Boot.BootKind, owner) + ")"));
            foreach (var b in boots.Where(b => b != owner)) o.WriteLine("  not run: " + b.Mod.Name + " (" + LoaderName(b.Boot.BootKind, b) + ")");
            o.WriteLine("loader running: " + (running == null ? "none" : LoaderName(running, owner)));
            if (issues.Count > 0)
            {
                o.WriteLine("problems:");
                foreach (var i in issues)
                {
                    var names = string.Join(", ", i.Mods.Select(m => m.Name));
                    switch (i.Kind)
                    {
                        case "boot_conflict": o.WriteLine("  " + i.Mods.Count + " mods carry a start-up script; only " + i.Mods[0].Name + "'s runs: " + names); break;
                        case "two_loaders": o.WriteLine("  two loaders are on; only the one whose start-up script runs works: " + names); break;
                        case "no_loader": o.WriteLine("  no loader runs, so these mods do not start: " + string.Join(", ", i.Mods.Skip(i.Detail == "other" ? 1 : 0).Select(m => m.Name))); break;
                        case "needs_chef": o.WriteLine("  these run only on Chef Shared Loader, which is not the loader running: " + names); break;
                        case "same_name": o.WriteLine("  " + i.Detail + " is in " + names + "; only " + i.Mods[0].Name + "'s runs"); break;
                        case "cannot_run": o.WriteLine("  the game's Lua (5.1, no LuaJIT) cannot run " + names); break;
                        case "too_big_for_chef": o.WriteLine("  " + names + " will not start: Chef Shared Loader reads no script over 4 MB at present"); break;
                        case "pack_alone": o.WriteLine("  " + i.Mods[0].Name + " translates " + i.Detail + ", which is not installed"); break;
                    }
                }
            }
            // what would not start under each loader, the one in effect first; nothing for no loader
            // (no Lua mod starts, as everyone knows: the user, 2026-10-26)
            var choices = LuaMods.Outcomes(lib, all).Where(x => x.Choice != null).ToList();
            if (choices.Count > 0)
            {
                o.WriteLine("under each loader:");
                var inEffect = Loaders.Chosen(lib)?.Id;
                foreach (var (choice, outs) in choices.OrderByDescending(x => x.Choice?.Id == inEffect))
                {
                    var head = "  " + choice.Name + (choice.Id == inEffect ? " (in effect)" : "") + ": ";
                    if (outs.Count == 0) { o.WriteLine(head + "every Lua mod starts"); continue; }
                    foreach (var x in outs)
                    {
                        var why = x.Why == "no_loader" ? "no loader" : x.Why == "needs_chef" ? "Chef Shared Loader only"
                                : x.Why == "too_big_for_chef" ? "script over Chef Shared Loader's 4 MB limit" : "start-up script replaced by " + x.Over?.Name + "'s";
                        o.WriteLine(head + "will not start (" + why + "): " + string.Join(", ", x.Mods.Select(m => m.Name)));
                    }
                }
            }
            // the loaders the library has, never every loader there is (the user, 2026-10-26)
            var chosenId = Loaders.Chosen(lib)?.Id;
            string Have(params string[] kinds)
            {
                var have = all.Where(x => x.Loader != null && kinds.Contains(x.Loader))
                              .Select(x => LoaderName(x.Loader, x) + (x.Mod.Id == chosenId ? "" : x.Helper ? " (helping)" : " (not used)")).Distinct().ToList();
                return have.Count > 0 ? "in the library: " + string.Join(", ", have) : "none in the library";
            }
            o.WriteLine("Lua mods:");
            foreach (var ml in all)
            {
                var what = new List<string>();
                if (ml.Loader != null) what.Add("loader: " + LoaderName(ml.Loader == "chef" ? "chef" : ml.Loader, ml));
                if (ml.Needs == "any") what.Add("needs a loader (" + Have("bml", "chef", "bsl", "mdl3") + ")");
                if (ml.Needs == "chef") what.Add("needs Chef Shared Loader" + (ml.NeedsWhy == "declared" ? ", as the mod declares" : ml.NeedsWhy == "api" ? ": its script calls the chef interface that loader provides" : "") + " (" + Have("chef") + ")");
                if (ml.PackOfName != null) what.Add("translation pack of " + (ml.PackOf?.Name ?? ml.PackOfName));
                foreach (var s in ml.Broken)
                {
                    var name = s.Name ?? (s.Id == LuaMods.BootId ? "boot" : s.Id.ToString("x16"));
                    if (s.Error != null) what.Add("cannot run: " + name + " line " + s.Error.Value.Line + ": " + s.Error.Value.Message +
                                                  (s.ErrorKind != null && s.ErrorKind != "syntax" ? " (" + s.ErrorKind.Replace('_', ' ') + ")" : ""));
                    else if (s.LuaJitBytecode) what.Add("cannot run: " + name + " is code compiled for LuaJIT");
                    else what.Add("cannot run: " + name + " uses " + string.Join(", ", s.Jit.Select(j => j == "ffi" ? "FFI" : j)) +
                                  " (LuaJIT's; the game's Lua 5.1 does not provide it at present, since the 2026-10-20 update)");
                }
                if (what.Count == 0) continue;
                o.WriteLine("  " + (ml.Mod.Enabled ? "[x] " : "[ ] ") + ml.Mod.Name + ": " + string.Join("; ", what));
            }
            var last = LuaMods.LastSession();
            if (last != null)
            {
                o.WriteLine("last session (" + last.Loader + ", " + last.When.ToString("yyyy-MM-dd HH:mm") + "): " +
                            (last.StartedCount >= 0 ? "started " + last.StartedCount + " of " + last.Total : "did not finish starting"));
                foreach (var s in last.Mods.Where(s => s.State != "loaded"))
                    o.WriteLine("  " + s.Name + ": " + (s.State == "failed" ? "FAILED: " : "does not compile: ") + s.Why.Split('\n')[0]);
            }
            var crash = LuaMods.Crashes(1).FirstOrDefault();
            if (crash.File != null)
            {
                var (site, times, of) = LuaMods.SiteCount(crash.File);
                var added = LuaMods.AddedAt(crash.When);
                o.WriteLine("last crash: " + crash.When.ToString("yyyy-MM-dd HH:mm:ss") + " (" + crash.File + ")" +
                            (site == null ? "" : " at " + site + ", " + times + " of the last " + of + " crashes there" +
                                                 (times >= LuaMods.Common ? " (a place the game crashes at often)" : "")) +
                            (added == null ? "" : added.Count == 0 ? "; the deploy before it added no mod" : "; new in the deploy before it: " + string.Join(", ", added)));
            }
        }

        static void LoaderCmd(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var m = Loaders.Choose(lib, a.Pos["which"]);
            o.WriteLine(m == null ? "loader: none" : "loader: " + m.Name + " (deploy to put it in the game)");
        }

        static void LuaSyntaxCmd(Args a, TextWriter o)
        {
            foreach (var f in a.Rest)
            {
                byte[] bytes;                           // as the game gets them: a BOM stays
                try { bytes = File.ReadAllBytes(f); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { o.WriteLine("UNREADABLE\t" + f + "\t" + e.Message); continue; }
                var r = LuaSyntax.Check(bytes);
                o.WriteLine(r == null ? "OK\t" + f : "ERROR\t" + f + "\t" + r.Value.Line + "\t" + r.Value.Message.Replace("\r", " ").Replace("\n", " ").Replace("\t", " "));
            }
        }

        static void SelfUpdateCmd(Args a, TextWriter o)
        {
            o.WriteLine("hd2-mod-cli " + StateDoc.Version + (SelfUpdate.Official ? " (official build)" : " (development build: pre-releases count too)"));
            var u = SelfUpdate.Check();
            if (u.Latest == null) o.WriteLine("no release on GitHub yet");
            else if (u.Newer) o.WriteLine(u.Latest + " is out" + (u.Prerelease ? " (pre-release)" : "") + ": " + u.Page);
            else o.WriteLine("up to date (newest: " + u.Latest + ")");
        }

        static void CheckUpdatesCmd(Args a, TextWriter o)
        {
            foreach (var u in Updates.Check(Library.Load()))
                o.WriteLine(u.Status.PadRight(9) + " " + u.Name + (u.Channel != null ? "  [" + u.Channel + "]" : "") +
                            (u.Status == "update" ? "  " + (u.Current ?? "?") + " -> " + (u.Latest ?? "?") + "  " + u.Page : "") +
                            (u.Why != null ? "  (" + u.Why + ")" : ""));
        }

        static void UpdateCmd(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var mod = Ops.Pick(lib, a.Pos["mod"]);
            var u = Updates.Check(new Library { Mods = { mod } }).First();
            if (u.Status != "update") throw new ModError(mod.Name + ": no newer version found (" + u.Status + (u.Why != null ? ", " + u.Why : "") + ")", "no_update");
            if (mod.GitHub != null) { o.WriteLine("updated: " + Updates.ApplyGitHub(mod.Id, u).Name + " " + u.Latest); return; }
#if HAS_NEXUS
            var link = new NxmLink { Game = mod.Nexus.Game, Mod = mod.Nexus.Mod, File = u.LatestFile };
            var (m, _) = Nexus.Install(link, Nexus.Credential());
            o.WriteLine("updated: " + m.Name + " " + u.Latest);
#else
            throw new ModError(mod.Name + ": no newer version found", "no_update");
#endif
        }

        static void SourceCmd(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var mod = Ops.Pick(lib, a.Rest[0]);
            if (a.Flag("--clear")) { mod.Nexus = null; mod.GitHub = null; mod.Page = null; }
            else if (a.Rest.Count < 2 || !Origins.FromUrl(mod, a.Rest[1])) throw new ModError("give an address: a Nexus Mods page or a GitHub repository", "bad_url");
            lib.Save();
            var (channel, page) = Origins.Of(mod);
            o.WriteLine(mod.Name + ": " + (channel == null ? "no source" : channel + "  " + page));
        }

        static void DeleteCmd(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var mods = a.Rest.Select(x => Ops.Pick(lib, x)).Distinct().ToList();
            var g = Store.GameDir();
            if (!a.Flag("--yes"))
                throw new ModError("this deletes " + string.Join(", ", mods.Select(m => Paths.Abs(m.Path))) + " for good and takes " +
                                   string.Join(", ", mods.Select(m => m.Name)) + " out of the game: add --yes");
            if (mods.Count == 1)
            {
                var (r, folder, bytes) = Ops.DeleteMod(lib, mods[0], g);
                o.WriteLine("deleted " + mods[0].Name + ": out of the library; deploy kept " + r.Kept + ", moved " + r.Moved + ", deleted " + r.Deleted +
                            "; folder " + folder + " deleted (" + bytes + " bytes)");
                return;
            }
            var (rs, all, failed) = Ops.DeleteMods(lib, mods, g);
            o.WriteLine("deleted " + (mods.Count - failed.Count) + " mods: out of the library; deploy kept " + rs.Kept + ", moved " + rs.Moved +
                        ", deleted " + rs.Deleted + "; " + all + " bytes");
            foreach (var f in failed) o.WriteLine("out of the library and the game, but its folder could not be deleted: " + f);
        }

        static void Switch(Args a, TextWriter o, bool on)
        {
            var lib = Library.Load();
            var mods = a.Rest.Select(r => Ops.Pick(lib, r)).ToList();
            foreach (var mod in mods)
            {
                mod.Enabled = on;
                mod.OffWith = null;
                o.WriteLine((on ? "on " : "off") + " " + mod.Name);
            }
            // a mod turned off takes its translation packs with it, and turned on again brings
            // back the ones it took (the user, 2026-10-30)
            if (!on)
                foreach (var p in LuaMods.Of(lib).Where(x => x.Mod.Enabled && x.PackOf != null && mods.Contains(x.PackOf) && !mods.Contains(x.Mod)))
                {
                    p.Mod.Enabled = false;
                    p.Mod.OffWith = p.PackOf.Id;
                    o.WriteLine("off " + p.Mod.Name + " (translation pack of " + p.PackOf.Name + ")");
                }
            else
                foreach (var p in lib.Mods.Where(x => !x.Enabled && x.OffWith != null && mods.Any(m => m.Id == x.OffWith)))
                {
                    o.WriteLine("on  " + p.Name + " (translation pack of " + mods.First(m => m.Id == p.OffWith).Name + ")");
                    p.Enabled = true;
                    p.OffWith = null;
                }
            lib.Save();
        }

        static void Move(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var mods = lib.Mods;
            // several mods move as one block, in the order they have (the user, 2026-10-25: a
            // drag of the mods taken together); every one is found before anything moves
            var block = a.Rest.Select(r => Ops.Pick(lib, r)).Distinct().OrderBy(m => mods.IndexOf(m)).ToList();
            string before = a.Value("--before"), after = a.Value("--after");
            Mod other = null;
            if (!string.IsNullOrEmpty(before) || !string.IsNullOrEmpty(after))
            {
                other = Ops.Pick(lib, !string.IsNullOrEmpty(before) ? before : after);
                if (block.Contains(other))
                    throw new ModError(block.Count == 1 ? "a mod cannot move next to itself" : "the mods cannot move next to one of themselves");
            }
            int to = a.Value("--to") != null ? int.Parse(a.Value("--to")) : 0;
            if (!a.Flag("--top") && !a.Flag("--bottom") && other == null && to == 0)
                throw new ModError("say where: --top, --bottom, --to N, --before MOD or --after MOD");
            foreach (var m in block) mods.Remove(m);
            int at = a.Flag("--top") ? 0
                : a.Flag("--bottom") ? mods.Count
                : !string.IsNullOrEmpty(before) ? mods.IndexOf(other)
                : !string.IsNullOrEmpty(after) ? mods.IndexOf(other) + 1
                : Math.Max(0, Math.Min(mods.Count, to - 1));
            mods.InsertRange(at, block);
            lib.Save();
            foreach (var m in block) o.WriteLine((mods.IndexOf(m) + 1) + " " + m.Name);
        }

        static void PrintOptions(Mod mod, TextWriter o)
        {
            var chosen = Manifest.Merged(Manifest.Of(mod), mod.Options);
            if (chosen.Count == 0)
            {
                o.WriteLine(mod.Name + " has no options");
                return;
            }
            for (int i = 0; i < chosen.Count; i++)
            {
                var c = chosen[i];
                o.WriteLine((i + 1).ToString().PadLeft(2) + " " + (c.Enabled ? "[x]" : "[ ]") + " " + c.Name);
                for (int j = 0; j < c.Subs.Count; j++)
                    o.WriteLine("     " + (i + 1).ToString().PadLeft(2) + "." + (j + 1) + " " + (c.Subs[j].Enabled ? "(*)" : "( )") + " " + c.Subs[j].Name);
            }
        }

        static void Options(Args a, TextWriter o) => PrintOptions(Ops.Pick(Library.Load(), a.Pos["mod"]), o);

        static void Set(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var mod = Ops.Pick(lib, a.Pos["mod"]);
            var chosen = Manifest.Merged(Manifest.Of(mod), mod.Options);
            var (opt, sub) = Ops.OptionAt(chosen, a.Pos["option"]);
            bool value = a.Pos["value"] == "on";
            // a sub-option is one of a kind: they replace the same static resources, so one is
            // chosen and choosing another drops it (the user, 2026-10-27). --only is what "on"
            // always does now, and is still taken
            if (sub == null) opt.Enabled = value;
            else if (value) foreach (var x in opt.Subs) x.Enabled = x == sub;
            else if (sub.Enabled && opt.Subs.Count(x => x.Enabled) == 1)
                throw new ModError(opt.Name + " keeps one choice: pick another, or turn " + opt.Name + " off", "pick_one", opt.Name);
            else sub.Enabled = false;
            mod.Options = chosen;
            lib.Save();
            PrintOptions(Ops.Pick(Library.Load(), a.Pos["mod"]), o);
        }

        static void ProfilesCmd(Args a, TextWriter o)
        {
            var lib = Library.Load();
            if (!lib.ProfilesOn) { o.WriteLine("profiles are off: one list of " + lib.Mods.Count + " mods (hd2qd profiles-on)"); return; }
            Profiles.InUse(lib);
            var ids = new HashSet<string>(lib.Mods.Select(m => m.Id));
            foreach (var p in lib.Profiles)
            {
                bool cur = string.Equals(p.Name, lib.Active, StringComparison.CurrentCultureIgnoreCase);
                var mine = p.Order.Where(ids.Contains).ToList();
                o.WriteLine((cur ? "* " : "  ") + p.Name + "  (" + mine.Count + " mods, " + mine.Count(p.On.Contains) + " on)");
            }
            o.WriteLine(lib.Mods.Count + " mods in the library");
        }

        static void Profiling(TextWriter o, bool on)
        {
            var lib = Library.Load();
            if (on) Profiles.TurnOn(lib); else Profiles.TurnOff(lib);
            lib.Save();
            o.WriteLine(on ? "profiles on; in use: " + lib.Active : "profiles off: one list of " + lib.Mods.Count + " mods");
        }

        static void ProfileMembers(Args a, TextWriter o, bool add)
        {
            var lib = Library.Load();
            var mods = a.Rest.Select(r => Ops.Pick(lib, r)).ToList();
            int n = add ? Profiles.Add(lib, mods) : Profiles.Remove(lib, mods);
            lib.Save();
            o.WriteLine(n + " mods " + (add ? "put into " : "taken out of ") + Ops.Repr(lib.Active) + (add ? "" : " (still in the library)"));
        }

        static void ProfileNew(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var p = Profiles.Make(lib, a.Pos["name"], a.Flag("--empty"));
            lib.Save();
            o.WriteLine("made " + Ops.Repr(p.Name) + ", a copy of " + Ops.Repr(lib.Active) + " (still in use): hd2qd profile-use " + Ops.Repr(p.Name));
        }

        static void ProfileUse(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var was = Profiles.InUse(lib).Name;
            var p = Profiles.Use(lib, a.Pos["name"]);
            lib.Save();
            o.WriteLine("in use: " + p.Name + (string.Equals(was, p.Name, StringComparison.CurrentCultureIgnoreCase) ? " (already)" : " (was " + was + ")"));
            if (!a.Flag("--no-deploy")) DeployCmd(new Args(), o);
        }

        static void ProfileRename(Args a, TextWriter o)
        {
            var lib = Library.Load();
            Profiles.Rename(lib, a.Pos["name"], a.Pos["to"]);
            lib.Save();
            o.WriteLine("renamed " + Ops.Repr(a.Pos["name"]) + " to " + Ops.Repr(a.Pos["to"].Trim()));
        }

        static void ProfileDelete(Args a, TextWriter o)
        {
            var lib = Library.Load();
            Profiles.Delete(lib, a.Pos["name"]);
            lib.Save();
            o.WriteLine("deleted the profile " + Ops.Repr(a.Pos["name"]) + "; its mods stay in the library");
        }

        static void DeployCmd(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var g = Store.GameDir();
            bool dry = a.Flag("--dry-run");
            var s = Deploy.Run(lib, g, dry, a.Flag("--copy"), a.Flag("--verify"), o.WriteLine);
            if (dry)
                o.WriteLine("would keep " + s.Kept + ", move " + s.Moved + ", write " + s.Written + ", delete " + s.Deleted + " of " + s.Files + " files");
            else
            {
                o.WriteLine("kept " + s.Kept + ", moved " + s.Moved + ", wrote " + s.Written + " (" + s.Linked + " linked, " + s.Copied +
                            " copied), deleted " + s.Deleted + "; " + s.Files + " files in the game");
                // what was meant to be written is not always what is there: a deploy that was
                // interrupted, an antivirus, another manager (the user, 2026-10-25)
                var c = Deploy.Check(lib, g);
                if (!c.Ok)
                    o.WriteLine("but the game folder is still not what the library asks for: " + c.Missing.Count + " missing, " +
                                c.Extra.Count + " that no mod wants, " + c.Changed.Count + " that differ (hd2qd check)");
            }
        }

        static void CheckCmd(Args a, TextWriter o)
        {
            var lib = Library.Load();
            var g = Store.GameDir();
            var c = Deploy.Check(lib, g, a.Flag("--deep"));
            o.WriteLine(c.Checked + " files checked, " + c.Copies + " of them copies rather than hard links");
            foreach (var f in c.Missing) o.WriteLine("  missing  " + f);
            foreach (var f in c.Extra) o.WriteLine("  spare    " + f);
            foreach (var f in c.Changed) o.WriteLine("  differs  " + f);
            if (c.Ok) { o.WriteLine("the game folder is exactly what the library asks for"); return; }
            o.WriteLine(c.Missing.Count + " missing, " + c.Extra.Count + " that no mod wants, " + c.Changed.Count + " that differ");
            if (c.Wrong.Count > 0)
                o.WriteLine(c.Wrong.Count + " of them are files a deploy would leave alone: the deploy record is wrong about these");
            else
                o.WriteLine("a deploy is on its way to put all of this right: none of it is a file a deploy would leave alone");
            throw new ModError("the game folder does not match the library: a deploy puts it right", "not_matching");
        }

        static void Status(Args a, TextWriter o)
        {
            a.Flags.Add("--dry-run");
            DeployCmd(a, o);
        }

        static void Relink(Args a, TextWriter o)
        {
            var r = Deploy.Relink(Library.Load(), Store.GameDir(), a.Flag("--dry-run"), a.Flag("--verify"));
            if (a.Flag("--dry-run")) o.WriteLine("would link " + r.Candidates + " copies (" + r.Bytes + " bytes come back)");
            else o.WriteLine("linked " + r.Linked + " copies: " + r.Bytes + " bytes came back, nothing was written");
        }

        static void Purge(Args a, TextWriter o)
        {
            var g = Store.GameDir();
            if (!a.Flag("--yes")) throw new ModError("this deletes every patch file in " + Paths.Join(g, "data") + ": add --yes");
            o.WriteLine("deleted " + Deploy.Purge(g) + " files: the game is vanilla");
        }

        static void Which(Args a, TextWriter o)
        {
            var what = a.Pos["what"];
            var hits = Deploy.Plan(Library.Load()).Where(w => w.Name.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                              w.Mod.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            foreach (var w in hits.Where(w => Deploy.Patch.IsMatch(w.Name)))
                o.WriteLine(w.Name.PadRight(28) + " " + w.Mod.PadRight(30) + " " + w.Src);
            if (hits.Count == 0) o.WriteLine("nothing matches " + Ops.Repr(what));
        }

        static void Conflicts(Args a, TextWriter o)
        {
            var found = Deploy.Clashes(Deploy.Plan(Library.Load()));
            var pairs = new List<(List<string> Mods, bool Same, List<(ulong Type, ulong Id)> Keys)>();
            foreach (var c in found)
            {
                int i = pairs.FindIndex(p => p.Same == c.Same && p.Mods.SequenceEqual(c.Mods));
                if (i < 0) pairs.Add((c.Mods, c.Same, new List<(ulong, ulong)> { (c.Type, c.Id) }));
                else pairs[i].Keys.Add((c.Type, c.Id));
            }
            foreach (var (mods, same, keys) in pairs.OrderByDescending(p => p.Keys.Count))
            {
                o.WriteLine(keys.Count + " resource(s) in " + string.Join(" / ", mods) + " -- " +
                            (same ? "the very same bytes, nothing is overridden" : mods[mods.Count - 1] + " wins"));
                if (a.Flag("--verbose"))
                    foreach (var (t, id) in keys.Take(50))
                        o.WriteLine("      " + ResourceNames.Type(t).PadRight(14) + " " + id.ToString("x16") +
                                    (ResourceNames.Name(id) is string n ? "  " + n : ""));
            }
            if (found.Count == 0) o.WriteLine("no resource is shipped by two mods");
        }

        static void State(Args a, TextWriter o) =>
            o.WriteLine(Json.Write(StateDoc.Build(Library.Load(), Store.GameDir(null, false), a.Flag("--verify")), true));

        static void ReportCmd(Args a, TextWriter o)
        {
            var (path, summary) = Report.Write(Library.Load(), Store.GameDir(null, false), a.Value("--out"));
            o.WriteLine("report written: " + path);
            o.Write(summary);
        }
    }
}
