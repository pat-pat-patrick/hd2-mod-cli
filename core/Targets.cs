using System;
using System.Collections.Generic;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>Where the library deploys to: the official game ("official", the game folder), or a
    /// target a plugin offers (HellForge's own copy of the game, "hellforge"; the user, 2026-11-11:
    /// "我需要在管理器能正常管理 HellForge 的 MOD"). The library is one; each target keeps its own
    /// list as it stands: the order, which mods are on, each one's options, and the profiles with
    /// the one in use. Switching keeps the one left and puts the other in place, so the window, the
    /// conflicts and every command work on the target in use and know nothing of targets.
    ///
    /// The first switch to a target starts it from the list in use. A mod added to the library
    /// while another target is in use comes in at the end, off.</summary>
    public static class Targets
    {
        public const string Official = "official";

        /// <summary>One target's list.</summary>
        public class State
        {
            public List<string> Order = new List<string>();
            public HashSet<string> On = new HashSet<string>();
            public Dictionary<string, List<OptionChoice>> Options = new Dictionary<string, List<OptionChoice>>();
            public bool ProfilesOn;
            public string Active;
            public List<Profile> Profiles = new List<Profile>();
        }

        public static string Current(Library lib) => string.IsNullOrEmpty(lib.Target) ? Official : lib.Target;

        public static bool IsOfficial(Library lib) => Current(lib) == Official;

        /// <summary>The list as it stands, every mod of the library.</summary>
        public static State Capture(Library lib)
        {
            if (lib.ProfilesOn) Profiles.InUse(lib);                 // the one in use as it stands
            return new State
            {
                Order = lib.Mods.Select(m => m.Id).ToList(),
                On = new HashSet<string>(lib.Mods.Where(m => m.Enabled).Select(m => m.Id)),
                Options = lib.Mods.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => Copy(g.First().Options)),
                ProfilesOn = lib.ProfilesOn, Active = lib.Active,
                Profiles = lib.Profiles.Select(Copy).ToList(),
            };
        }

        /// <summary>A target's list put in place: its mods in its order with their on/off and
        /// options, mods it does not know after them, off; its profiles and the one in use.</summary>
        public static void Apply(Library lib, State s)
        {
            var byId = new Dictionary<string, Mod>();
            foreach (var m in lib.Mods) if (!byId.ContainsKey(m.Id ?? "")) byId[m.Id ?? ""] = m;
            var known = s.Order.Where(byId.ContainsKey).Distinct().Select(id => byId[id]).ToList();
            var seen = new HashSet<Mod>(known);
            foreach (var m in known)
            {
                m.Enabled = s.On.Contains(m.Id);
                if (s.Options.TryGetValue(m.Id, out var o)) m.Options = Copy(o);
                m.OffWith = null;
            }
            var rest = lib.Mods.Where(m => !seen.Contains(m)).ToList();
            foreach (var m in rest) { m.Enabled = false; m.OffWith = null; }
            lib.Mods = known.Concat(rest).ToList();
            lib.Profiles = s.Profiles.Select(Copy).ToList();
            lib.Active = s.Active;
            var inUse = lib.Profiles.FirstOrDefault(p => string.Equals(p.Name, s.Active, StringComparison.CurrentCultureIgnoreCase));
            lib.ProfilesOn = s.ProfilesOn && inUse != null;
            lib.Members = lib.ProfilesOn ? new HashSet<string>(inUse.Order) : null;
        }

        /// <summary>Whether a mod belongs to the target in use (Mod.For).</summary>
        public static bool Belongs(Library lib, Mod m) => m.For == null || m.For == Current(lib);

        /// <summary>Mods for another target off; of a pair, only one on.</summary>
        public static void Enforce(Library lib)
        {
            foreach (var m in lib.Mods.Where(m => m.Enabled && !Belongs(lib, m))) m.Enabled = false;
            var byId = lib.Mods.GroupBy(m => m.Id ?? "").ToDictionary(g => g.Key, g => g.First());
            foreach (var m in lib.Mods.Where(m => m.Enabled && m.Pair != null))
                if (byId.TryGetValue(m.Pair, out var other) && other.Enabled && other != m) other.Enabled = false;
        }

        /// <summary>The pair of two library mods: the same mod, one build for each target. The one
        /// that was for both becomes the other's counterpart: with forNew "official", the plugin's
        /// target (the only one offered); else "official".</summary>
        public static void MakePair(Library lib, Mod old, Mod made, string forNew, IList<string> offered)
        {
            made.For = forNew == Official ? Official : forNew;
            if (old.For == null || old.For == made.For)
                old.For = forNew == Official ? (offered.Count == 1 ? offered[0] : null) : Official;
            if (old.Pair != null && old.Pair != made.Id)
                foreach (var x in lib.Mods.Where(x => x.Id == old.Pair)) x.Pair = null;
            old.Pair = made.Id;
            made.Pair = old.Id;
            Enforce(lib);
        }

        /// <summary>Another target put in use; the one left keeps its list. The caller saves the library.</summary>
        public static void Switch(Library lib, string to)
        {
            to = string.IsNullOrEmpty(to) ? Official : to;
            var from = Current(lib);
            if (from == to) return;
            lib.TargetStates[from] = Capture(lib);
            if (lib.TargetStates.TryGetValue(to, out var s)) Apply(lib, s);
            lib.TargetStates.Remove(to);                             // the list in use is the library itself
            lib.Target = to == Official ? null : to;
            Enforce(lib);
        }

        static List<OptionChoice> Copy(List<OptionChoice> options) =>
            options.Select(o => new OptionChoice
            {
                Name = o.Name, Enabled = o.Enabled,
                Subs = o.Subs.Select(x => new SubChoice { Name = x.Name, Enabled = x.Enabled }).ToList(),
            }).ToList();

        static Profile Copy(Profile p) => new Profile
        {
            Name = p.Name, Order = p.Order.ToList(), On = new HashSet<string>(p.On),
            Options = p.Options.ToDictionary(kv => kv.Key, kv => Copy(kv.Value)),
        };

        // ---------------------------------------------------------------- library.json

        public static Dictionary<string, object> ToJson(State s)
        {
            var d = new Dictionary<string, object>
            {
                ["order"] = s.Order.Cast<object>().ToList(),
                ["on"] = s.Order.Where(s.On.Contains).Cast<object>().ToList(),
                ["options"] = s.Options.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (object)Library.OptionsJson(kv.Value)),
            };
            if (s.Profiles.Count > 0)
            {
                d["profiles_on"] = s.ProfilesOn;
                d["active_profile"] = s.Active;
                d["profiles"] = s.Profiles.Select(p => (object)new Dictionary<string, object>
                {
                    ["name"] = p.Name, ["order"] = p.Order.Cast<object>().ToList(), ["on"] = p.Order.Where(p.On.Contains).Cast<object>().ToList(),
                    ["options"] = p.Options.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (object)Library.OptionsJson(kv.Value)),
                }).ToList();
            }
            return d;
        }

        public static State FromJson(Dictionary<string, object> d)
        {
            var s = new State { ProfilesOn = Json.Bool(d, "profiles_on"), Active = Json.Str(d, "active_profile") };
            s.Order.AddRange(Json.List(d, "order").OfType<string>());
            foreach (var id in Json.List(d, "on").OfType<string>()) s.On.Add(id);
            foreach (var kv in Json.Obj(d, "options") ?? new Dictionary<string, object>())
                if (kv.Value is List<object> list) s.Options[kv.Key] = Library.ReadOptionsJson(list);
            foreach (var p in Json.List(d, "profiles").OfType<Dictionary<string, object>>())
            {
                var profile = new Profile { Name = Json.Str(p, "name") ?? "" };
                profile.Order.AddRange(Json.List(p, "order").OfType<string>());
                foreach (var id in Json.List(p, "on").OfType<string>()) profile.On.Add(id);
                foreach (var kv in Json.Obj(p, "options") ?? new Dictionary<string, object>())
                    if (kv.Value is List<object> list) profile.Options[kv.Key] = Library.ReadOptionsJson(list);
                if (profile.Name.Length > 0) s.Profiles.Add(profile);
            }
            return s;
        }
    }
}
