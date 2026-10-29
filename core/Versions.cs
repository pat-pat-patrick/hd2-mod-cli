using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Hd2Mod
{
    /// <summary>A mod with a script keeps the version it had before the newest, for a rollback
    /// (the user, 2026-10-30): script mods are small, change often (fixes, game patches) and
    /// differ in quality, while a mod of resources only rarely needs one. It is a setting, off
    /// unless the player turns it on. Every version is unpacked into a folder of its own, so
    /// keeping one is not deleting its folder. One version is kept: the one before it goes.
    /// The player can also skip a newest version, so the update check stops offering it.</summary>
    public static class Versions
    {
        const string Setting = "keep_previous";

        public static bool KeepOn(Dictionary<string, object> cfg = null) => Json.Bool(cfg ?? Store.Config(), Setting);

        public static void SetKeep(bool on)
        {
            var cfg = Store.Config();
            cfg[Setting] = on;
            Store.Save("config.json", cfg);
        }

        static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>Called just before a mod takes a new version from `now`: with the setting on and
        /// a script in the version it has, that version is kept (and the one kept before goes).
        /// True when it was kept: its folder must then stay.</summary>
        public static bool Keep(Library lib, Dictionary<string, object> cfg, Mod mod, string now)
        {
            if (!KeepOn(cfg) || string.IsNullOrEmpty(mod.Path) || !Directory.Exists(mod.Path)) return false;
            if (string.Equals(Paths.Abs(mod.Path), Paths.Abs(now ?? ""), StringComparison.OrdinalIgnoreCase)) return false;
            if (!StateDoc.HasLua(new DirectoryInfo(mod.Path)).Lua) return false;
            var folder = mod.Path;
            // a folder of the player's own, outside the library folder, may be changed or deleted by
            // them: the version kept is a copy of it in the library folder
            if (!Ops.Ours(folder, cfg)) folder = CopyIn(folder, cfg);
            var older = mod.Previous;
            mod.Previous = new PreviousVersion
            {
                Path = folder, Name = mod.Name, Source = mod.Source, Page = mod.Page, Nexus = mod.Nexus, GitHub = mod.GitHub,
                Version = Origins.VersionOf(mod).Version, Kept = Now(),
            };
            // the one kept before goes, unless it is the very version coming (a rollback, then the
            // same update again: the same archive unpacks into the same folder)
            if (older != null && !string.Equals(Paths.Abs(older.Path ?? ""), Paths.Abs(now ?? ""), StringComparison.OrdinalIgnoreCase))
                Drop(lib, cfg, older.Path);
            return true;
        }

        /// <summary>A folder copied into the library folder under a name the library knows as its own.</summary>
        static string CopyIn(string from, Dictionary<string, object> cfg)
        {
            string digest;
            using (var sha = SHA256.Create())
                digest = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(Paths.Abs(from) + "|" + Now())).Select(b => b.ToString("x2"))).Substring(0, 12);
            var to = Paths.Join(Store.ModsDir(cfg), Path.GetFileName(Paths.Abs(from).TrimEnd('\\')) + "_" + digest);
            Copy(from, to + ".copying");
            Directory.Move(to + ".copying", to);
            return to;
        }

        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Paths.Join(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.EnumerateDirectories(from)) Copy(d, Paths.Join(to, Path.GetFileName(d)));
        }

        /// <summary>A kept folder deleted, when it is the library's own and nothing else uses it.</summary>
        static bool Drop(Library lib, Dictionary<string, object> cfg, string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || !Ops.Ours(folder, cfg)) return false;
            if (Used(lib).Contains(Paths.Abs(folder))) return false;
            return Ops.Drop(folder);
        }

        /// <summary>Every folder the library uses: the mods' and the versions kept.</summary>
        public static HashSet<string> Used(Library lib)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in lib.Mods)
            {
                if (!string.IsNullOrEmpty(m.Path)) used.Add(Paths.Abs(m.Path));
                if (!string.IsNullOrEmpty(m.Previous?.Path)) used.Add(Paths.Abs(m.Previous.Path));
            }
            return used;
        }

        /// <summary>The mod goes back to the version kept, and the one it had becomes the version kept,
        /// so a rollback can be undone the same way. Its place, on/off and choices stay; options the
        /// version does not have go. Nothing in the game changes before the next deploy.</summary>
        public static void Rollback(Library lib, Mod mod)
        {
            var back = mod.Previous ?? throw new ModError(mod.Name + " has no earlier version kept", "no_previous", mod.Name);
            if (!Directory.Exists(back.Path)) throw new ModError("the earlier version's folder is gone: " + back.Path, "gone", back.Path);
            var now = new PreviousVersion
            {
                Path = mod.Path, Name = mod.Name, Source = mod.Source, Page = mod.Page, Nexus = mod.Nexus, GitHub = mod.GitHub,
                Version = Origins.VersionOf(mod).Version, Kept = Now(),
            };
            mod.Path = back.Path;
            mod.Source = back.Source;
            mod.Page = back.Page;
            mod.Nexus = back.Nexus;
            mod.GitHub = back.GitHub;
            // a name the player gave the mod stays; the name it came with is the version's
            if (mod.RealName == null && !string.IsNullOrEmpty(back.Name)) mod.Name = back.Name;
            mod.Options = Manifest.Merged(Manifest.Read(back.Path), mod.Options);
            mod.Previous = now;
        }

        /// <summary>The versions kept deleted (their mods keep the version they have).</summary>
        public static List<(Mod Mod, string Folder, long Bytes)> Forget(Library lib, IEnumerable<Mod> mods)
        {
            var cfg = Store.Config();
            var done = new List<(Mod, string, long)>();
            foreach (var m in mods.Where(m => m.Previous != null).Distinct().ToList())
            {
                var folder = m.Previous.Path;
                long bytes = Directory.Exists(folder) ? StateDoc.LibraryBytes(new DirectoryInfo(folder)) : 0;
                m.Previous = null;
                if (Directory.Exists(folder) && Ops.Ours(folder, cfg) && !Used(lib).Contains(Paths.Abs(folder)) && !Ops.Drop(folder))
                    throw new ModError("the folder could not be deleted: " + folder, "delete_failed", folder);
                done.Add((m, folder, bytes));
            }
            return done;
        }

        // ------------------------------------------------------------ skipping a version

        /// <summary>What names the newest version an update check found: its file on Nexus Mods, its tag on GitHub.</summary>
        public static string KeyOf(UpdateInfo u) =>
            u.LatestFile > 0 ? "nexus:" + u.LatestFile : !string.IsNullOrEmpty(u.Latest) ? (u.Channel == "GitHub" ? "github:" : "version:") + u.Latest : null;

        /// <summary>A check's "update" for the version the player skipped says "skipped"; a newer one is an update again.</summary>
        public static UpdateInfo Skipped(Mod m, UpdateInfo u)
        {
            if (u != null && u.Status == "update" && m.Skip != null && m.Skip == KeyOf(u)) u.Status = "skipped";
            return u;
        }

        public static void Skip(Mod m, UpdateInfo u)
        {
            m.Skip = KeyOf(u) ?? throw new ModError("the check found no version to skip", "no_version");
        }
    }
}
