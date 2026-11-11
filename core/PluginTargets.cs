using System;
using System.Collections.Generic;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>Deploy targets plugins offer (Targets): another copy of the game a plugin deploys
    /// the library to, such as HellForge's. The manager knows no target by name. A plugin offers
    /// one with these optional members, called by name like the rest of the contract:
    /// - Dictionary Target(host): {id, title (in host lang), found, why, running}; null: none;
    /// - Dictionary TargetStatus(host): {pending (long), running (bool), said}: cheap, read with the
    ///   window's state;
    /// - Dictionary TargetDeploy(host): {said, refused: [text]}: host["mods"] is the list in use
    ///   (every enabled mod's files, options chosen); host["target"] says which target is in use;
    /// - string TargetPurge(host): what this manager put into the target, taken out;
    /// - void TargetLaunch(Window owner, host): the game of that target started (the window only).
    /// A plugin that throws is reported, never the manager's own failure.</summary>
    public static class PluginTargets
    {
        public sealed class Offer
        {
            public string File, Id, Title, Why;
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
                        Found = Json.Bool(d, "found"), Running = Json.Bool(d, "running"),
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
            return PluginFiles.Call(o.Type, method, host) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        public static Dictionary<string, object> Status(Offer o, Dictionary<string, object> host)
        {
            if (o.Type.GetMethod("TargetStatus") == null) return new Dictionary<string, object>();
            try { return Ask(o, "TargetStatus", host); }
            catch (Exception e) { ManagerLog.Warn("plugin " + o.File + ": TargetStatus: " + e.Message); return new Dictionary<string, object> { ["said"] = e.Message }; }
        }

        public static Dictionary<string, object> Deploy(Offer o, Dictionary<string, object> host) => Ask(o, "TargetDeploy", host);

        public static string Purge(Offer o, Dictionary<string, object> host)
        {
            if (o.Type.GetMethod("TargetPurge") == null) throw new ModError("the plugin " + o.File + " cannot purge", "target_cannot", "TargetPurge");
            return PluginFiles.Call(o.Type, "TargetPurge", host) as string ?? "";
        }
    }
}
