using System;
using System.Collections.Generic;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>Deploy targets plugins offer (Targets): another copy of the game a plugin deploys
    /// the library to, such as HellForge's. The manager knows no target by name. A plugin offers
    /// one with these optional members, called by name like the rest of the contract:
    /// - Dictionary Target(host): {id, title (in host lang), found, why, running, lua}; null: none.
    ///   lua is the Lua that target's game runs: "luajit" (LuaJIT 2.1 with ffi, bit and jit, as the
    ///   official game had before its 2026-10-20 update), absent for the official game's own;
    ///   loaders lists the kinds of Lua loader that game runs ("bml", "chef", "bsl", "mdl3", "uml"), absent: any;
    /// - Dictionary TargetStatus(host): {pending (long), running (bool), said, mods}: cheap, read with
    ///   the window's state; mods is {mod id: "link" | "copy" | "converted" | "pending"} for the enabled mods;
    /// - Dictionary TargetDeploy(host): {said, refused: [text], log}: host["mods"] is the list in use
    ///   (every enabled mod's files, options chosen); host["target"] says which target is in use;
    ///   host["progress"], an Action&lt;string&gt;, says what a long step does while it runs;
    /// - string TargetPurge(host): what this manager put into the target, taken out;
    /// - void TargetLaunch(Window owner, host): the game of that target started (the window only);
    /// - bool TargetClaims(folder, host): the folder is that target's own kind of mod (added, it is
    ///   for that target only, Mod.For).
    /// A plugin that throws is reported, never the manager's own failure.</summary>
    public static class PluginTargets
    {
        public sealed class Offer
        {
            public string File, Id, Title, Why;
            /// <summary>The Lua the target's game runs: "luajit", or null for the official game's.</summary>
            public string Lua;
            /// <summary>The kinds of Lua loader the target's game runs, or null for any.</summary>
            public List<string> Loaders;
            public bool Found, Running;
            public Type Type;
        }

        /// <summary>Every target a plugin offers now (a plugin that fails offers none).</summary>
        public static List<Offer> All(Dictionary<string, object> host)
        {
            var offers = new List<Offer>();
            foreach (var (file, type, why) in PluginFiles.All())
            {
                if (type == null || type.GetMethod("Target") == null) continue;
                try
                {
                    if (!(PluginFiles.Call(type, "Target", host) is Dictionary<string, object> d)) continue;
                    var id = Json.Str(d, "id");
                    if (string.IsNullOrEmpty(id) || id == Targets.Official || offers.Any(o => o.Id == id)) continue;
                    offers.Add(new Offer
                    {
                        File = file, Type = type, Id = id, Title = Json.Str(d, "title") ?? id, Why = Json.Str(d, "why"),
                        Found = Json.Bool(d, "found"), Running = Json.Bool(d, "running"), Lua = Json.Str(d, "lua"),
                        Loaders = d.TryGetValue("loaders", out var ls) && ls is List<object> l ? l.OfType<string>().ToList() : null,
                    });
                }
                catch (Exception e) { ManagerLog.Warn("plugin " + file + ": Target: " + e.Message); }
            }
            return offers;
        }

        public static Offer Of(string id, Dictionary<string, object> host) =>
            All(host).FirstOrDefault(o => o.Id == id) ?? throw new ModError("no plugin offers the deploy target " + Ops.Repr(id), "no_target", id);

        static Dictionary<string, object> Ask(Offer o, string method, Dictionary<string, object> host)
        {
            if (o.Type.GetMethod(method) == null) throw new ModError("the plugin " + o.File + " cannot " + method, "target_cannot", method);
            return Guarded(o, method, () => PluginFiles.Call(o.Type, method, host)) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        /// <summary>A plugin's call whose failure is a refusal: what the plugin throws (its own
        /// exception types, as HellForge's "a session is running") becomes a ModError
        /// "plugin_failed" with the plugin's message, so the command line ends with exit code 1 and
        /// the window says the message. Before (2026-11-12), a plugin's exception left
        /// Commands.Run unhandled and ended hd2qd.exe (.NET Runtime event 1026). The whole
        /// exception, with its stack, goes to the log.</summary>
        static object Guarded(Offer o, string method, Func<object> call)
        {
            try { return call(); }
            catch (Exception e) when (!(e is ModError) && !(e is OutOfMemoryException))
            {
                ManagerLog.Warn("plugin " + o.File + ": " + method + ": " + e);
                throw new ModError(e.Message, "plugin_failed", o.File);
            }
        }

        /// <summary>The target a plugin claims a mod folder for (its own kind of mod, as
        /// HellForge's hellforge-mod.json): its optional bool TargetClaims(folder, host); null:
        /// none does.</summary>
        public static string Claim(string folder, Dictionary<string, object> host)
        {
            foreach (var o in All(host))
            {
                if (o.Type.GetMethod("TargetClaims") == null) continue;
                try { if (PluginFiles.Call(o.Type, "TargetClaims", folder, host) is bool b && b) return o.Id; }
                catch (Exception e) { ManagerLog.Warn("plugin " + o.File + ": TargetClaims: " + e.Message); }
            }
            return null;
        }

        public static Dictionary<string, object> Status(Offer o, Dictionary<string, object> host)
        {
            if (o.Type.GetMethod("TargetStatus") == null) return new Dictionary<string, object>();
            try { return Ask(o, "TargetStatus", host); }
            catch (Exception e) { ManagerLog.Warn("plugin " + o.File + ": TargetStatus: " + e.Message); return new Dictionary<string, object> { ["said"] = e.Message }; }
        }

        /// <summary>A deploy to the target, logged with its time and the steps its plugin names
        /// (TargetDeploy's optional "log"), so a slow one says where the time went (the user,
        /// 2026-11-12: "HellForge 部署怎么用时这么久").</summary>
        public static Dictionary<string, object> Deploy(Offer o, Dictionary<string, object> host)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var r = Ask(o, "TargetDeploy", host);
                ManagerLog.Info("deploy to " + o.Id + " in " + clock.Elapsed.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s: " +
                                (Json.Str(r, "said") ?? "") + (Json.Str(r, "log") is string l && l.Length > 0 ? " (" + l + ")" : ""));
                return r;
            }
            catch (Exception e)
            {
                ManagerLog.Warn("deploy to " + o.Id + " failed after " + clock.Elapsed.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s: " + e.Message);
                throw;
            }
        }

        public static string Purge(Offer o, Dictionary<string, object> host)
        {
            if (o.Type.GetMethod("TargetPurge") == null) throw new ModError("the plugin " + o.File + " cannot purge", "target_cannot", "TargetPurge");
            return Guarded(o, "TargetPurge", () => PluginFiles.Call(o.Type, "TargetPurge", host)) as string ?? "";
        }
    }
}
