using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>Large asset protection (0.23.0): a mod whose hd2_license.json says
    /// "payload": {"deploy": "game", "mode": "deploy" | "session"} ships only ciphertext
    /// (protected\*.seal). A plugin (the members' plugin) decrypts it into a stage folder on the
    /// game's drive, host["stage"] = &lt;game&gt;\hd2qd_stage, mirroring the mod's own layout, and a
    /// deploy takes the mod's files from there (Deploy.IncludedFolders). The manager tells a
    /// protected mod by its file alone; the plugin says where its files are and whether they are
    /// ready. Its optional members, called by name like the rest of the contract:
    /// - Dictionary ModSource(modFolder, host): null, not its mod; {folder, mode} when ready;
    ///   {folder: "", mode, why} when not (why in the host's language). Cheap: asked once a mod a
    ///   state read;
    /// - bool PrepareDeploy(host): decrypts what is missing, synchronously (the manager calls it
    ///   off the window's thread before a deploy and before a launch); host["for_session"] true:
    ///   the session mods too;
    /// - void EndSession(host): the session mods' stage deleted (only files with one link left),
    ///   called after the deploy that took them out of the game.
    /// A protected mod that is not ready is withheld: left out of the deploy with a word on its
    /// row, never an error, so the rest of the library still deploys. A deploy target of a plugin
    /// never gets one (PluginHost.Mods): the plaintext stays with the official game.</summary>
    public static class Protected
    {
        public const string DeployMode = "deploy", SessionMode = "session";

        /// <summary>What the plugin said of a protected mod: its files' folder when ready (else null),
        /// its mode, and why not ready: the plugin's words (Why), or the manager's own (Key, a
        /// row note's key).</summary>
        public sealed class Source
        {
            public string Folder, Mode, Why, Key;
            public bool Ready => !string.IsNullOrEmpty(Folder);
            public bool Session => Mode == SessionMode;
        }

        // the mode each mod folder's hd2_license.json declares, by the file's size and time
        static readonly Dictionary<string, (string Sig, string Mode)> declared = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The mode a protected mod declares ("deploy" or "session"); null for a mod that is
        /// not protected (no hd2_license.json, or one without "deploy": "game"). Read once while the
        /// file is unchanged.</summary>
        public static string ModeOf(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            var p = Path.Combine(folder, "hd2_license.json");
            FileInfo fi;
            try { fi = new FileInfo(p); if (!fi.Exists) return null; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException) { return null; }
            var sig = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
            lock (declared) if (declared.TryGetValue(p, out var had) && had.Sig == sig) return had.Mode;
            string mode = null;
            try
            {
                var pay = Json.Obj(Store.ReadJson(p) as Dictionary<string, object>, "payload");
                if (Json.Str(pay, "deploy") == "game") mode = Json.Str(pay, "mode") == SessionMode ? SessionMode : DeployMode;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { }
            lock (declared) declared[p] = (sig, mode);
            return mode;
        }

        // set while a plugin's ModSource runs: what it asks of the host (mods, library) plans
        // again, which would ask it again
        [ThreadStatic] static bool asking;

        /// <summary>Where a protected mod's files are now; null for a mod that is not protected.
        /// The plugin is asked once a mod within a state read (Loaders.Pass).</summary>
        public static Source Of(Mod mod)
        {
            if (mod == null || ModeOf(mod.Path) == null) return null;
            if (Loaders.Peek(mod, "protected", out var had)) return (Source)had;
            var s = Now(mod, out bool keep);
            if (keep) Loaders.Put(mod, "protected", s);
            return s;
        }

        static Source Now(Mod mod, out bool keep)
        {
            keep = true;
            var s = new Source { Mode = ModeOf(mod.Path) };
            if (asking)
            {
                // asked again from inside the plugin's own answer: withheld for that inner plan, and
                // not kept, so the answer the plugin is giving is the one the read keeps
                keep = false;
                s.Key = "protected_not_ready";
                return s;
            }
            asking = true;
            Dictionary<string, object> said;
            try { said = PluginFiles.ModSource(mod.Path, PluginHost.Make()); }
            finally { asking = false; }
            if (said == null) { s.Key = "protected_no_plugin"; return s; }
            var mode = Json.Str(said, "mode");
            if (mode == SessionMode || mode == DeployMode) s.Mode = mode;
            // a session mod is in the game only while a game started from the manager runs; outside
            // one its stage is gone (EndSession), so that is what its row says, not "not ready"
            if (s.Session && !Session.IsOpen()) { s.Key = "protected_session"; return s; }
            var folder = Json.Str(said, "folder");
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                s.Why = Json.Str(said, "why");
                if (string.IsNullOrEmpty(s.Why)) s.Key = "protected_not_ready";
                return s;
            }
            s.Folder = Paths.Norm(folder);
            return s;
        }

        /// <summary>Whether any enabled mod is protected (only then are plugins asked to prepare).</summary>
        public static bool Any(Library lib, bool sessionOnly = false) =>
            lib.Mods.Any(m => m.Enabled && ModeOf(m.Path) is string mode && (!sessionOnly || mode == SessionMode));

        /// <summary>The plugins' PrepareDeploy, before a deploy (forSession: and a session's launch):
        /// whether every plugin that has one said it went well. Nothing is asked when no enabled mod
        /// is protected. Synchronous: never on the window's thread.</summary>
        public static bool Prepare(Library lib, bool forSession)
        {
            if (!Any(lib)) return true;
            var host = PluginHost.Make();
            host["for_session"] = forSession;
            return PluginFiles.PrepareDeploy(host);
        }

        /// <summary>The enabled protected mods a deploy leaves out now, each with why.</summary>
        public static List<(Mod Mod, Source Source)> Withheld(Library lib) =>
            Loaders.DeployOrder(lib).Select(m => (m, Of(m))).Where(x => x.Item2 != null && !x.Item2.Ready).ToList();

        /// <summary>The word on a protected mod's row: why it is not in the game (the plugin's
        /// words, else the manager's by key), or on a plugin's deploy target that it never goes
        /// there. None for a mod that is not protected or is ready.</summary>
        public static Dictionary<string, object> Note(Library lib, Mod mod)
        {
            if (ModeOf(mod.Path) == null) return null;
            if (!Targets.IsOfficial(lib)) return new Dictionary<string, object> { ["colour"] = "Yellow", ["key"] = "protected_target" };
            var s = Of(mod);
            if (s == null || s.Ready) return null;
            var colour = s.Key == "protected_session" ? "Blue" : "Yellow";
            return s.Why != null
                ? new Dictionary<string, object> { ["colour"] = colour, ["text"] = s.Why }
                : new Dictionary<string, object> { ["colour"] = colour, ["key"] = s.Key };
        }
    }

    /// <summary>A game session for the session-mode protected mods: their plaintext is in the game
    /// only while a game started from the manager runs. Kept on disk (session.json), not in one
    /// process: the command line, the window and the plugins' timers each read it. Each file a
    /// deploy puts in for a session is marked in deployed.json ("session": true), so files left
    /// by a manager that was closed mid-game are found at the next start (Leftover).</summary>
    public static class Session
    {
        const string FileName = "session.json";

        /// <summary>Waits between tries of the teardown's deploy; tests replace it.</summary>
        public static Action<TimeSpan> Sleep = System.Threading.Thread.Sleep;

        public static bool IsOpen()
        {
            try { return Json.Bool(Store.Load(FileName), "open"); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { return false; }
        }

        /// <summary>A session started (before the deploy that puts the session mods in).</summary>
        public static void Begin(string game) => Store.Save(FileName, new Dictionary<string, object>
        {
            ["open"] = true, ["since"] = DateTime.UtcNow.Ticks, ["game"] = game, ["pid"] = (long)Deploy.Pid(),
        });

        /// <summary>The session over (before the deploy that takes them out).</summary>
        public static void Close()
        {
            if (!File.Exists(Store.PathOf(FileName))) return;
            Store.Save(FileName, new Dictionary<string, object> { ["open"] = false, ["closed"] = DateTime.UtcNow.Ticks });
        }

        /// <summary>Whether the last deploy put session files in the game.</summary>
        public static bool FilesDeployed()
        {
            try
            {
                var files = Json.Obj(Store.Load("deployed.json"), "files");
                return files != null && files.Values.OfType<Dictionary<string, object>>().Any(r => Json.Bool(r, "session"));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { return false; }
        }

        /// <summary>A session open, or its files still in the game: something to end.</summary>
        public static bool Leftover() => IsOpen() || FilesDeployed();

        /// <summary>The session ended: closed, a deploy without the session mods (tried again on a
        /// file the game has just let go of), then the plugins' EndSession, which deletes their
        /// stage — after the deploy, or the hard links in data\ would keep the plaintext. Done
        /// false (said why) when the game runs again or the deploy kept failing: the session's
        /// files are still marked, and the next start or watch tries again.</summary>
        public static (bool Done, DeployResult Deployed, string Why) End(Library lib, string game, int tries = 5)
        {
            Close();
            DeployResult r = null;
            if (game != null && Directory.Exists(Paths.Join(game, "data")))
                for (int i = 1; ; i++)
                {
                    try { r = Deploy.Run(lib, game); break; }
                    catch (ModError e) when (e.Kind == "game_running" || e.Kind == "deploying")
                    {
                        ManagerLog.Info("session end waits: " + e.Message);
                        return (false, null, e.Kind);
                    }
                    catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && i < tries)
                    {
                        ManagerLog.Info("session end: try " + i + " failed (" + e.Message + "), again");
                        Sleep(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        ManagerLog.Warn("session end: the deploy failed " + tries + " times: " + e.Message);
                        return (false, null, e.Message);
                    }
                }
            PluginFiles.EndSession(PluginHost.Make());
            ManagerLog.Info("session ended" + (r != null ? ": " + r.Deleted + " deleted, " + r.Moved + " moved" : ""));
            return (true, r, null);
        }
    }

    public static partial class PluginFiles
    {
        /// <summary>The first plugin's ModSource that answers for this mod folder (null: none does,
        /// or none has the member). A plugin that throws is logged and passed over.</summary>
        public static Dictionary<string, object> ModSource(string modFolder, Dictionary<string, object> host)
        {
            foreach (var (file, type) in Usable())
            {
                if (type.GetMethod("ModSource") == null) continue;
                try { if (Call(type, "ModSource", modFolder, host) is Dictionary<string, object> d) return d; }
                catch (Exception e) when (!(e is OutOfMemoryException)) { ManagerLog.Warn("plugin " + file + ": ModSource: " + e.Message); }
            }
            return null;
        }

        /// <summary>Each plugin's PrepareDeploy: whether all of them said it went well (a plugin
        /// that throws is logged and counts as not).</summary>
        public static bool PrepareDeploy(Dictionary<string, object> host)
        {
            bool ok = true;
            foreach (var (file, type) in Usable())
            {
                if (type.GetMethod("PrepareDeploy") == null) continue;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try { ok &= !(Call(type, "PrepareDeploy", host) is bool b) || b; }
                catch (Exception e) when (!(e is OutOfMemoryException)) { ok = false; ManagerLog.Warn("plugin " + file + ": PrepareDeploy: " + e); }
                ManagerLog.Info("plugin " + file + ": PrepareDeploy in " + clock.Elapsed.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s");
            }
            return ok;
        }

        /// <summary>Each plugin's EndSession (a plugin that throws is logged).</summary>
        public static void EndSession(Dictionary<string, object> host)
        {
            foreach (var (file, type) in Usable())
            {
                if (type.GetMethod("EndSession") == null) continue;
                try { Call(type, "EndSession", host); }
                catch (Exception e) when (!(e is OutOfMemoryException)) { ManagerLog.Warn("plugin " + file + ": EndSession: " + e); }
            }
        }
    }
}
