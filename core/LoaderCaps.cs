using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>What each Lua loader can do, in one table (the user, 2026-11-01: what was known of the
    /// loaders was written in several places, and once they disagreed; one table, the rest follows
    /// from it). The deploy (which loaders go to the game beside the one in effect), the checks
    /// (which mods start) and the window's compatibility table all ask this table.
    ///
    /// A loader has a capability from a version on, or when its code carries the feature's marker:
    /// the marker counts where the code can be read (Boot Mod Loader's boot, Chef Shared Loader's
    /// core), so a loader that adds a feature needs no new version here. MDL3's boot is obfuscated,
    /// strings too, so for MDL3 only its version counts. Each row says what it rests on.</summary>
    public static class LoaderCaps
    {
        // ---------------------------------------------------------------- what a mod uses
        /// <summary>Starts the mods declared "-- HD2-Addon: mods/a/b".</summary>
        public const string Addons = "addons";
        /// <summary>Gives Bingus Shared Loader's interface (CowboyBingusModLoader): the mods written for Bingus start.</summary>
        public const string Bingus = "bingus";
        /// <summary>Starts the mods made for Chef Shared Loader (declared, or using its chef interface).</summary>
        public const string ChefMods = "chef_mods";
        /// <summary>Takes the script names only Chef Shared Loader's name rule takes.</summary>
        public const string ChefNames = "chef_names";
        /// <summary>Gives the MDL3 global: its audio, and HD2 Data Hub's data.</summary>
        public const string Mdl3Api = "mdl3_api";

        // ---------------------------------------------------------------- how loaders go together
        /// <summary>Runs the start-up scripts deployed before its own: a mod's own boot is no conflict.</summary>
        public const string RunsBoots = "runs_boots";
        /// <summary>Runs the scripts a deploy marks "-- HD2-Guest-Boot:" (GuestBoots).</summary>
        public const string HostsGuests = "hosts_guests";
        /// <summary>Hands the mods made for Chef to a Chef Shared Loader deployed before it.</summary>
        public const string HandsToChef = "hands_to_chef";
        /// <summary>Takes that hand-off (ChefSharedLoaderHandoff).</summary>
        public const string TakesHandOff = "takes_hand_off";
        /// <summary>Run as another loader's guest, starts no mod and gives its services.</summary>
        public const string QuietGuest = "quiet_guest";

        /// <summary>What mods use, in the order the window names them.</summary>
        public static readonly string[] Services = { Addons, Bingus, ChefMods, Mdl3Api };

        class Row
        {
            public string Kind, Cap, Since, Marker;
        }

        static readonly Row[] Rows =
        {
            // Boot Mod Loader: ours
            new Row { Kind = "bml", Cap = Addons },
            new Row { Kind = "bml", Cap = Bingus },                                                    // since 0.1.0
            // guests proven in game 2026-10-26 (Objective Tracker 0.6.7 under 0.12.0)
            new Row { Kind = "bml", Cap = HostsGuests, Since = "0.12.0", Marker = "HD2%-Guest%-Boot" },
            // hand-off proven in game 2026-10-26 (0.13.1 with Chef 1.2.1)
            new Row { Kind = "bml", Cap = HandsToChef, Since = "0.13.0", Marker = "ChefSharedLoaderHandoff" },

            // Chef Shared Loader: its source read 2026-10-25 (1.2.0), its log 2026-10-26
            new Row { Kind = "chef", Cap = Addons },
            new Row { Kind = "chef", Cap = Bingus },
            new Row { Kind = "chef", Cap = ChefMods },
            new Row { Kind = "chef", Cap = ChefNames },
            new Row { Kind = "chef", Cap = RunsBoots },                                                // its log: "running another mod's boot script too"
            new Row { Kind = "chef", Cap = TakesHandOff, Since = "1.2.0", Marker = "ChefSharedLoaderHandoff" },

            // MDL3: dev.18-dev.21 read; later obfuscated, versions only
            new Row { Kind = "mdl3", Cap = Addons },
            new Row { Kind = "mdl3", Cap = Bingus },
            new Row { Kind = "mdl3", Cap = Mdl3Api },
            // its coordinator starts no mod when CowboyBingusModLoader exists (dev.21's source); assumed later
            new Row { Kind = "mdl3", Cap = QuietGuest },
            // its notes since dev.22 (README_zh.md, read 2026-11-01); not tried in game
            new Row { Kind = "mdl3", Cap = ChefMods, Since = "3.0.0-dev.22" },
            new Row { Kind = "mdl3", Cap = RunsBoots, Since = "3.0.0-dev.22" },

            // Bingus Shared Loader
            new Row { Kind = "bsl", Cap = Addons },
            new Row { Kind = "bsl", Cap = Bingus },

            // Universal Mod Loader: its boot read 2026-11-15 (v1.0.1, v2.0.0-v2.0.3), not tried in game.
            // Every version reads "-- HD2-Addon:" from the patches and sets CowboyBingusModLoader;
            // "-- HD2-Boot:" and "-- HD2-Requires:" (the mods made for Chef) from 2.0.1
            new Row { Kind = "uml", Cap = Addons },
            new Row { Kind = "uml", Cap = Bingus },
            new Row { Kind = "uml", Cap = ChefMods, Since = "2.0.1" },
        };

        /// <summary>The markers to look for in a loader's boot and Chef's core (LuaMods.Look).</summary>
        public static readonly string[] Markers = Rows.Where(r => r.Marker != null).Select(r => r.Marker).Distinct().ToArray();

        /// <summary>A loader's capabilities: its version and the markers its code carries.</summary>
        public static List<string> Of(string kind, string version, IEnumerable<string> marks)
        {
            var have = new HashSet<string>(marks ?? Enumerable.Empty<string>());
            return Rows.Where(r => r.Kind == kind && (r.Since == null || AtLeast(version, r.Since) || (r.Marker != null && have.Contains(r.Marker))))
                       .Select(r => r.Cap).ToList();
        }

        public static bool Has(string kind, string version, IEnumerable<string> marks, string cap) => Of(kind, version, marks).Contains(cap);

        /// <summary>Every capability a loader of this kind has in its latest versions.</summary>
        public static List<string> Best(string kind) => Rows.Where(r => r.Kind == kind).Select(r => r.Cap).ToList();

        /// <summary>The version a kind of loader has a capability from; null when it always has it or never does.</summary>
        public static string Since(string kind, string cap) => Rows.FirstOrDefault(r => r.Kind == kind && r.Cap == cap)?.Since;

        /// <summary>How loader b goes to the game beside loader a in effect, by their capabilities:
        /// "hand_off" (a hands the mods made for Chef to b, which takes them), "guest" (a runs b's
        /// start-up script as a quiet guest, for what b gives and a does not), else null.</summary>
        public static string Beside(ICollection<string> a, ICollection<string> b)
        {
            if (a.Contains(HandsToChef) && !a.Contains(ChefMods) && b.Contains(TakesHandOff) && b.Contains(ChefMods)) return "hand_off";
            if (a.Contains(HostsGuests) && b.Contains(QuietGuest) && b.Contains(Mdl3Api) && !a.Contains(Mdl3Api)) return "guest";
            return null;
        }

        /// <summary>What mods use that b gives and a does not.</summary>
        public static List<string> Lacks(ICollection<string> a, ICollection<string> b) => Services.Where(s => b.Contains(s) && !a.Contains(s)).ToList();

        /// <summary>What a kind of loader is for: the service named when another does it too.</summary>
        public static string Own(string kind) =>
            kind == "bml" || kind == "uml" ? Addons : kind == "chef" ? ChefMods : kind == "mdl3" ? Mdl3Api : kind == "bsl" ? Bingus : null;

        /// <summary>The capabilities a way of going together asks of the loader in effect and of the one beside it.</summary>
        public static (string[] In, string[] Beside) Requires(string how) =>
            how == "hand_off" ? (new[] { HandsToChef }, new[] { TakesHandOff, ChefMods }) : (new[] { HostsGuests }, new[] { QuietGuest, Mdl3Api });

        /// <summary>Two loaders, side by side (the window's compatibility table).</summary>
        public class Verdict
        {
            /// <summary>"together" (one goes to the game beside the other), "covered" (one does what the
            /// other does: one is enough), "one" (one at a time).</summary>
            public string State;
            /// <summary>Together: the kind in effect, the one beside it, and how ("hand_off", "guest").</summary>
            public string In, Beside, How;
            /// <summary>Together only with later versions: the capabilities these loaders lack, by kind.</summary>
            public List<(string Kind, string Cap)> Short = new List<(string, string)>();
            /// <summary>Covered: the kind that also does what the other does; null when both do the same.</summary>
            public string Coverer;
        }

        /// <summary>Whether two loaders, with these capabilities, go together, cover each other, or not.</summary>
        public static Verdict Pair(string ka, ICollection<string> a, string kb, ICollection<string> b)
        {
            foreach (var (x, cx, y, cy) in new[] { (ka, a, kb, b), (kb, b, ka, a) })
                if (Beside(cx, cy) is string how) return new Verdict { State = "together", In = x, Beside = y, How = how };
            // with their latest versions: what the library's lack for it
            foreach (var (x, cx, y, cy) in new[] { (ka, a, kb, b), (kb, b, ka, a) })
                if (Beside(Best(x), Best(y)) is string how)
                {
                    var need = Requires(how);
                    var v = new Verdict { State = "together", In = x, Beside = y, How = how };
                    v.Short.AddRange(need.In.Where(c => !cx.Contains(c)).Select(c => (x, c)));
                    v.Short.AddRange(need.Beside.Where(c => !cy.Contains(c)).Select(c => (y, c)));
                    return v;
                }
            var la = Lacks(a, b);
            var lb = Lacks(b, a);
            if (la.Count == 0 && lb.Count == 0) return new Verdict { State = "covered" };
            if (la.Count == 0) return new Verdict { State = "covered", Coverer = ka };
            if (lb.Count == 0) return new Verdict { State = "covered", Coverer = kb };
            return new Verdict { State = "one" };
        }

        static readonly Regex VersionRx = new Regex(@"^(\d+(?:\.\d+){1,3})(?:-dev\.(\d+))?");

        /// <summary>Whether a version is the floor or later; "-dev.N" comes before the release of the same number.</summary>
        public static bool AtLeast(string version, string floor)
        {
            var a = VersionRx.Match(version ?? "");
            var b = VersionRx.Match(floor);
            if (!a.Success || !Version.TryParse(a.Groups[1].Value, out var va) || !Version.TryParse(b.Groups[1].Value, out var vb)) return false;
            if (va != vb) return va > vb;
            int Dev(Match m) => m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : int.MaxValue;
            return Dev(a) >= Dev(b);
        }
    }
}
