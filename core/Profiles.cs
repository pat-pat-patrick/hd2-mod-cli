using System;
using System.Collections.Generic;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>A profile: the mods put into it (out of the library's), their load order, which of
    /// them are on, and each one's options (the user, 2026-10-25: one profile may hold 3 mods,
    /// another 10, of the library's 50; switching a profile is often switching which mod wins).</summary>
    public class Profile
    {
        public string Name;
        /// <summary>The profile's mods by id, in load order: the last one wins.</summary>
        public List<string> Order = new List<string>();
        public HashSet<string> On = new HashSet<string>();
        public Dictionary<string, List<OptionChoice>> Options = new Dictionary<string, List<OptionChoice>>();
    }

    /// <summary>Profiles, a switch of their own (the user, 2026-10-25: a player who needs none
    /// keeps one list). Off: the library is the one list, as before. On: the library holds every
    /// mod, the profile in use holds some of them, and only those can be on and deployed. The
    /// library's own list is always the profile in use (its mods first, in its order), so
    /// deploying and conflicts need to know nothing of profiles.</summary>
    public static class Profiles
    {
        /// <summary>The name of the profile made when profiles are first turned on: every mod.</summary>
        public const string First = "Default";

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);

        /// <summary>Whether a mod is in the profile in use (always, with profiles off).</summary>
        public static bool InProfile(Library lib, Mod m) => !lib.ProfilesOn || lib.Members == null || lib.Members.Contains(m.Id);

        /// <summary>The profile in use as the library stands.</summary>
        public static Profile Capture(Library lib, string name)
        {
            var mine = lib.Mods.Where(m => InProfile(lib, m)).ToList();
            return new Profile
            {
                Name = name,
                Order = mine.Select(m => m.Id).ToList(),
                On = new HashSet<string>(mine.Where(m => m.Enabled).Select(m => m.Id)),
                Options = mine.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => Copy(g.First().Options)),
            };
        }

        static List<OptionChoice> Copy(List<OptionChoice> options) =>
            options.Select(o => new OptionChoice
            {
                Name = o.Name, Enabled = o.Enabled,
                Subs = o.Subs.Select(s => new SubChoice { Name = s.Name, Enabled = s.Enabled }).ToList(),
            }).ToList();

        static void NeedOn(Library lib)
        {
            if (!lib.ProfilesOn) throw new ModError("profiles are off: hd2qd profiles-on", "profiles_off");
        }

        /// <summary>The profile in use, its record brought up to date from the library.</summary>
        public static Profile InUse(Library lib)
        {
            NeedOn(lib);
            int i = lib.Profiles.FindIndex(p => Same(p.Name, lib.Active));
            if (i < 0) { i = 0; lib.Active = lib.Profiles[0].Name; }
            lib.Profiles[i] = Capture(lib, lib.Profiles[i].Name);
            return lib.Profiles[i];
        }

        public static Profile Find(Library lib, string name)
        {
            InUse(lib);
            return lib.Profiles.FirstOrDefault(p => Same(p.Name, name?.Trim()))
                ?? throw new ModError("no profile named " + Ops.Repr(name), "no_profile", name);
        }

        /// <summary>Profiles on: the first time, a profile of every mod as it stands; after that,
        /// the profile in use as it was kept.</summary>
        public static void TurnOn(Library lib)
        {
            if (lib.ProfilesOn) return;
            lib.ProfilesOn = true;
            if (lib.Profiles.Count == 0)
            {
                lib.Members = new HashSet<string>(lib.Mods.Select(m => m.Id));
                lib.Profiles.Add(Capture(lib, First));
                lib.Active = First;
                return;
            }
            var to = lib.Profiles.FirstOrDefault(p => Same(p.Name, lib.Active)) ?? lib.Profiles[0];
            var everIn = new HashSet<string>(lib.Profiles.SelectMany(p => p.Order));
            var added = lib.Mods.Where(m => !everIn.Contains(m.Id)).ToList();      // added while they were off
            var was = added.ToDictionary(m => m, m => m.Enabled);
            Apply(lib, to);
            // a mod no profile has heard of goes into the one in use, as it was
            foreach (var m in added)
            {
                lib.Members.Add(m.Id);
                lib.Mods.Remove(m);
                lib.Mods.Insert(lib.Mods.FindLastIndex(x => lib.Members.Contains(x.Id) && x != m) + 1, m);
                m.Enabled = was[m];
            }
        }

        /// <summary>Profiles off: one list again, every mod in it, each as it stands. The profiles
        /// are kept for when they are turned on again.</summary>
        public static void TurnOff(Library lib)
        {
            if (!lib.ProfilesOn) return;
            InUse(lib);
            lib.ProfilesOn = false;
            lib.Members = null;
        }

        /// <summary>A new profile: a copy of the one in use (its mods, order, on/off and options),
        /// or with empty, none of them. The one in use stays in use.</summary>
        public static Profile Make(Library lib, string name, bool empty = false)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ModError("a profile needs a name", "no_name");
            InUse(lib);
            if (lib.Profiles.Any(p => Same(p.Name, name))) throw new ModError("a profile named " + Ops.Repr(name) + " exists already", "duplicate", name);
            var made = empty ? new Profile { Name = name } : Capture(lib, name);
            lib.Profiles.Add(made);
            return made;
        }

        /// <summary>Another profile put in place of the one in use, which keeps how it stands. The
        /// caller saves the library and deploys.</summary>
        public static Profile Use(Library lib, string name)
        {
            var to = Find(lib, name);
            if (Same(to.Name, lib.Active)) return to;
            Apply(lib, to);
            return to;
        }

        /// <summary>The library's list made a profile's: its mods first, in its order, with its
        /// on/off and options; every other mod after them, off.</summary>
        static void Apply(Library lib, Profile to)
        {
            var byId = new Dictionary<string, Mod>();
            foreach (var m in lib.Mods) if (!byId.ContainsKey(m.Id ?? "")) byId[m.Id ?? ""] = m;
            var mine = to.Order.Where(byId.ContainsKey).Distinct().Select(id => byId[id]).ToList();
            var seen = new HashSet<Mod>(mine);
            // a pack turned off with its mod comes back with that mod in this profile only
            foreach (var m in lib.Mods) m.OffWith = null;
            foreach (var m in mine)
            {
                m.Enabled = to.On.Contains(m.Id);
                if (to.Options.TryGetValue(m.Id, out var o)) m.Options = Copy(o);
            }
            var rest = lib.Mods.Where(m => !seen.Contains(m)).ToList();
            foreach (var m in rest) m.Enabled = false;
            lib.Mods = mine.Concat(rest).ToList();
            lib.Members = new HashSet<string>(mine.Select(m => m.Id));
            lib.Active = to.Name;
        }

        /// <summary>Mods of the library put into the profile in use, at its bottom, on.</summary>
        public static int Add(Library lib, IEnumerable<Mod> mods)
        {
            InUse(lib);
            int n = 0;
            foreach (var m in mods.Distinct())
            {
                if (lib.Members.Contains(m.Id)) continue;
                lib.Members.Add(m.Id);
                lib.Mods.Remove(m);
                int last = lib.Mods.FindLastIndex(x => lib.Members.Contains(x.Id));
                lib.Mods.Insert(last + 1, m);
                m.Enabled = true;
                n++;
            }
            return n;
        }

        /// <summary>Mods taken out of the profile in use; they stay in the library.</summary>
        public static int Remove(Library lib, IEnumerable<Mod> mods)
        {
            InUse(lib);
            int n = 0;
            foreach (var m in mods.Distinct())
            {
                if (!lib.Members.Remove(m.Id)) continue;
                m.Enabled = false;
                lib.Mods.Remove(m);
                lib.Mods.Add(m);
                n++;
            }
            return n;
        }

        /// <summary>Profiles imported from another manager added, their names made unique. With
        /// profiles on already, the one in use stays in use (an import never changes the game on
        /// its own); else the one named inUse (else the first) is put in use.</summary>
        public static List<Profile> AddImported(Library lib, List<Profile> made, string inUse, string from = null)
        {
            if (made.Count == 0) return made;
            bool keep = lib.ProfilesOn;
            if (keep) InUse(lib);
            foreach (var p in made)
            {
                var name = p.Name.Trim().Length > 0 ? p.Name.Trim() : "Profile";
                // a name the library has already: the manager's after it, then a number
                var unique = name;
                if (from != null && lib.Profiles.Any(x => Same(x.Name, unique))) unique = name + " (" + from + ")";
                for (int n = 2; lib.Profiles.Any(x => Same(x.Name, unique)); n++) unique = name + " (" + (from != null ? from + " " : "") + n + ")";
                if (Same(p.Name, inUse)) inUse = unique;
                p.Name = unique;
                lib.Profiles.Add(p);
            }
            lib.ProfilesOn = true;
            if (!keep) Apply(lib, made.FirstOrDefault(x => Same(x.Name, inUse)) ?? made[0]);
            // the mods the import brought are outside the profile in use: off, as every such mod is
            else foreach (var m in lib.Mods) if (!lib.Members.Contains(m.Id)) m.Enabled = false;
            lib.KnowAll();
            return made;
        }

        public static void Rename(Library lib, string name, string to)
        {
            var p = Find(lib, name);
            to = (to ?? "").Trim();
            if (to.Length == 0) throw new ModError("a profile needs a name", "no_name");
            if (!Same(p.Name, to) && lib.Profiles.Any(x => Same(x.Name, to))) throw new ModError("a profile named " + Ops.Repr(to) + " exists already", "duplicate", to);
            if (Same(p.Name, lib.Active)) lib.Active = to;
            p.Name = to;
        }

        /// <summary>A profile gone; never the one in use (switch to another first). Its mods stay in the library.</summary>
        public static void Delete(Library lib, string name)
        {
            var p = Find(lib, name);
            if (Same(p.Name, lib.Active)) throw new ModError("the profile in use cannot be deleted: switch to another first", "in_use", p.Name);
            lib.Profiles.Remove(p);
        }
    }
}
