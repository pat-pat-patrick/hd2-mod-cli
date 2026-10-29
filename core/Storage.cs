using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>A folder that holds mods, as Steam's library folders hold games.</summary>
    public class LibraryFolder
    {
        public string Path, Drive;
        /// <summary>Where new mods go (added zips, downloads, mods moved here by default).</summary>
        public bool IsDefault;
        /// <summary>Added by the player (else listed because mods are in it).</summary>
        public bool Registered;
        /// <summary>On the game's drive: its mods are deployed as hard links.</summary>
        public bool GameDrive;
        public bool InAppData;
        public long Free, Total, Bytes;
        public List<Mod> Mods = new List<Mod>();
    }

    /// <summary>The mod library folders (the user, 2026-10-24: "像 Steam 设置里设置游戏库一样"): the
    /// ones added, the default (where new mods go), and every folder the library's mods are in;
    /// moving mods between them, as Steam moves an install folder.</summary>
    /// <summary>What a library folder holds that the library does not have yet.</summary>
    public class FolderFinds
    {
        /// <summary>Other managers' lists kept here: imported, their order, on/off and options come too.</summary>
        public List<ImportSource> Managers = new List<ImportSource>();
        /// <summary>Mod folders here that no manager lists and the library does not have.</summary>
        public List<string> Loose = new List<string>();
        /// <summary>The folder is this manager's store (Importers.StoreOf), and its list was not found
        /// where it usually is, or lists none of these mods: nothing is offered loose, the player is
        /// asked for the list file. Null otherwise.</summary>
        public string NeedsConfig;
        /// <summary>Lists found and not read (ImportSource.Problem): said, so a list read wrong is
        /// never taken for no list at all.</summary>
        public List<ImportSource> Problems = new List<ImportSource>();
    }

    public static class Storage
    {
        public static List<string> Registered() =>
            Json.List(Store.Config(), "library_folders").OfType<string>().Where(s => s.Length > 0).ToList();

        static void SaveRegistered(Dictionary<string, object> cfg, List<string> list)
        {
            cfg["library_folders"] = list.Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToList();
            Store.Save("config.json", cfg);
        }

        public static string AddFolder(string path)
        {
            var p = Paths.Abs(path);
            if (System.IO.Path.GetPathRoot(p).TrimEnd('\\') == p.TrimEnd('\\')) throw new ModError("a whole drive cannot be a mod folder: " + p, "drive_root", p);
            Directory.CreateDirectory(p);
            var cfg = Store.Config();
            var list = Json.List(cfg, "library_folders").OfType<string>().ToList();
            list.Add(p);
            SaveRegistered(cfg, list);
            return p;
        }

        /// <summary>A library folder looked into, the way Steam looks into a library folder it is
        /// given (the user, 2026-10-25: "添加库文件夹，可以检测各个 mod 管理器的"). The mods other
        /// managers keep there come in through those managers' own lists, so their order, on/off and
        /// options come too; any other mod folder in it is offered as it stands. Our own unpacked
        /// folders no mod uses (older versions) are the Storage page's leftovers, not mods to add.
        /// Only reads.</summary>
        public static FolderFinds Look(string folder, Library lib)
        {
            var root = Paths.Abs(folder).TrimEnd('\\');
            string Key(string p) => Paths.Abs(p ?? "").TrimEnd('\\');
            bool Inside(string p)
            {
                var x = Key(p);
                return string.Equals(x, root, StringComparison.OrdinalIgnoreCase) || x.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
            }
            bool Readable(Exception e) => e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException || e is InvalidCastException;
            var have = new HashSet<string>(lib.Mods.Select(m => Key(m.Path)), StringComparer.OrdinalIgnoreCase);
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);      // every folder a manager lists
            var listed = new HashSet<string>();                                       // the managers whose list has mods here
            var finds = new FolderFinds();
            void Offer(string key, string name, string where, string dir, Library theirs, bool all)
            {
                foreach (var m in theirs.Mods) claimed.Add(Key(m.Path));
                if (all || theirs.Mods.Any(m => Inside(m.Path))) listed.Add(key);
                // the mods an import would add: the same rule as the import (folder, Guid or name)
                var mine = new Library { Mods = new List<Mod>(lib.Mods) };
                theirs.Mods = theirs.Mods.Where(m => all || Inside(m.Path)).ToList();
                int n = Importers.Merge(mine, theirs).Added.Count;
                if (n > 0) finds.Managers.Add(new ImportSource { Key = key, Name = name, Where = where, Dir = dir, Mods = n });
            }
            try { if (Store.ArsenalFile() != null) Offer("arsenal", "HD2 Arsenal", root, null, Ops.ImportArsenal().Lib, false); }
            catch (Exception e) when (Readable(e)) { finds.Problems.Add(ImportSource.Unreadable("arsenal", "HD2 Arsenal", Store.ArsenalFile(), e)); }
            // HD2ModManager: the folder given is its storage (or the program's folder, which names
            // it: then all of its mods, wherever that is), or its usual storage is in the folder
            var usual = Importers.Hd2mmDir();
            var named = Importers.Hd2mmStorage(root);
            foreach (var dir in new[] { named, usual }.Where(d => d != null).Distinct(StringComparer.OrdinalIgnoreCase))
                try
                {
                    bool isUsual = usual != null && string.Equals(Key(dir), Key(usual), StringComparison.OrdinalIgnoreCase);
                    Offer("hd2mm", "HD2ModManager", Path.Combine(dir, "Mods"), isUsual ? null : dir, Importers.ReadHd2mm(dir), dir == named);
                }
                catch (Exception e) when (Readable(e)) { finds.Problems.Add(ImportSource.Unreadable("hd2mm", "HD2ModManager", Path.Combine(dir, "enabled.json"), e)); }
            try { if (Importers.EchelonFile() != null) Offer("echelon", "Echelon", root, null, Importers.ReadEchelon(), false); }
            catch (Exception e) when (Readable(e)) { finds.Problems.Add(ImportSource.Unreadable("echelon", "Echelon", Importers.EchelonFile(), e)); }
            // a manager's store whose list was not found: imported through that list or not at all
            // (the user, 2026-10-26: without it there is no order, no switches, no options)
            var store = Importers.StoreOf(root);
            if (store != null && !listed.Contains(store))
            {
                // its list was found and could not be read: that is said, not a file to look for
                if (!finds.Problems.Any(p => p.Key == store)) finds.NeedsConfig = store;
                return finds;
            }
            var cfg = Store.Config();
            List<string> subs;
            try { subs = Directory.EnumerateDirectories(root).ToList(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { subs = new List<string>(); }
            foreach (var sub in subs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var k = Key(sub);
                if (have.Contains(k) || claimed.Contains(k)) continue;
                if (claimed.Any(c => c.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase))) continue;   // a manager's own store
                if (Ops.Ours(k, cfg)) continue;                    // an older version this program unpacked
                if (HasPatch(k, 3)) finds.Loose.Add(k);
            }
            return finds;
        }

        /// <summary>Whether a folder holds a patch file, at most depth folders down.</summary>
        static bool HasPatch(string dir, int depth)
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (info.EnumerateFiles().Any(f => Deploy.Patch.IsMatch(f.Name))) return true;
                return depth > 0 && info.EnumerateDirectories().Any(d => HasPatch(d.FullName, depth - 1));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        /// <summary>Mod folders added to the library where they are, at its bottom: the ones Look
        /// found loose. One whose name the library has already gets the folder's name after it.</summary>
        public static List<Mod> AddLoose(Library lib, IEnumerable<string> folders)
        {
            var added = new List<Mod>();
            var paths = new HashSet<string>(lib.Mods.Select(m => Paths.Abs(m.Path ?? "").TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
            foreach (var f in folders)
            {
                if (!paths.Add(Paths.Abs(f).TrimEnd('\\'))) continue;
                var mod = Ops.NewMod(f);
                if (lib.Mods.Any(m => m.Name == mod.Name)) mod.Name += " (" + Path.GetFileName(f.TrimEnd('\\')) + ")";
                lib.Mods.Add(mod);
                added.Add(mod);
            }
            return added;
        }

        /// <summary>A library folder that is somewhere else now (moved by hand, or another drive
        /// letter): the list, the default and every mod in it follow, each one that is at the new
        /// place too (the user, 2026-10-25). Nothing on disk is moved. The library is the caller's
        /// to save.</summary>
        public static (int Followed, int Missing) ChangeFolder(Library lib, string from, string to)
        {
            var a = Paths.Abs(from).TrimEnd('\\');
            var b = Paths.Abs(to).TrimEnd('\\');
            if (!Directory.Exists(b)) throw new ModError("no such folder: " + b, "no_folder", b);
            if (System.IO.Path.GetPathRoot(b).TrimEnd('\\') == b) throw new ModError("a whole drive cannot be a mod folder: " + b, "drive_root", b);
            int followed = 0, missing = 0;
            foreach (var m in lib.Mods)
            {
                var p = Paths.Abs(m.Path ?? "").TrimEnd('\\');
                if (!string.Equals(p, a, StringComparison.OrdinalIgnoreCase) && !p.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                var q = b + p.Substring(a.Length);
                if (Directory.Exists(q)) { m.Path = q; followed++; }
                else missing++;
            }
            // the versions kept for a rollback follow too, without being counted as mods
            foreach (var m in lib.Mods.Where(m => m.Previous != null))
            {
                var p = Paths.Abs(m.Previous.Path ?? "").TrimEnd('\\');
                if (!p.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                var q = b + p.Substring(a.Length);
                if (Directory.Exists(q)) m.Previous.Path = q;
            }
            var cfg = Store.Config();
            var list = Json.List(cfg, "library_folders").OfType<string>()
                           .Select(x => string.Equals(Paths.Abs(x).TrimEnd('\\'), a, StringComparison.OrdinalIgnoreCase) ? b : x).ToList();
            if (!list.Contains(b, StringComparer.OrdinalIgnoreCase)) list.Add(b);
            if (string.Equals(Paths.Abs(Json.Str(cfg, "mods_dir") ?? "").TrimEnd('\\'), a, StringComparison.OrdinalIgnoreCase)) cfg["mods_dir"] = b;
            SaveRegistered(cfg, list);
            return (followed, missing);
        }

        /// <summary>The folder off the list (its files stay). The default stays: choose another first.</summary>
        public static void RemoveFolder(string path)
        {
            var p = Paths.Abs(path).TrimEnd('\\');
            if (string.Equals(p, Paths.Abs(Store.ModsDir()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new ModError("the default mod folder stays on the list: make another one the default first", "is_default", p);
            var cfg = Store.Config();
            SaveRegistered(cfg, Json.List(cfg, "library_folders").OfType<string>().Where(x => !string.Equals(Paths.Abs(x).TrimEnd('\\'), p, StringComparison.OrdinalIgnoreCase)).ToList());
        }

        public static void SetDefault(string path)
        {
            var p = Paths.Abs(path);
            Directory.CreateDirectory(p);
            var cfg = Store.Config();
            cfg["mods_dir"] = p;
            var list = Json.List(cfg, "library_folders").OfType<string>().ToList();
            list.Add(p);
            SaveRegistered(cfg, list);
        }

        static string Key(string p) => Paths.Abs(p).TrimEnd('\\').ToLowerInvariant();

        /// <summary>Every mod folder: the default first, then the ones added, then the folders the
        /// library's mods are in (a mod belongs to the folder its own folder is in).</summary>
        public static List<LibraryFolder> Folders(Library lib, string game)
        {
            var seen = new Dictionary<string, LibraryFolder>();
            var order = new List<LibraryFolder>();
            string data = game != null ? Paths.Join(game, "data") : null;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            LibraryFolder Get(string path)
            {
                var k = Key(path);
                if (seen.TryGetValue(k, out var f)) return f;
                var abs = Paths.Abs(path);
                f = new LibraryFolder
                {
                    Path = abs, Drive = System.IO.Path.GetPathRoot(abs),
                    InAppData = abs.StartsWith(appData, StringComparison.OrdinalIgnoreCase) || abs.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase),
                };
                try
                {
                    var d = new DriveInfo(f.Drive);
                    if (d.IsReady) { f.Free = d.AvailableFreeSpace; f.Total = d.TotalSize; }
                }
                catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException) { }
                // the drive the game is on: compared by volume when both exist, else by drive letter
                f.GameDrive = data != null && (Directory.Exists(abs) ? Deploy.SameDrive(abs, data)
                                                                      : string.Equals(f.Drive, System.IO.Path.GetPathRoot(Paths.Abs(game)), StringComparison.OrdinalIgnoreCase));
                seen[k] = f;
                order.Add(f);
                return f;
            }
            Get(Store.ModsDir()).IsDefault = true;
            foreach (var r in Registered()) Get(r).Registered = true;
            foreach (var m in lib.Mods)
            {
                if (string.IsNullOrEmpty(m.Path)) continue;
                var parent = System.IO.Path.GetDirectoryName(Paths.Abs(m.Path).TrimEnd('\\'));
                if (parent == null) continue;
                var f = Get(parent);
                f.Mods.Add(m);
                if (Directory.Exists(m.Path)) f.Bytes += StateDoc.LibraryBytes(new DirectoryInfo(m.Path));
            }
            return order;
        }

        /// <summary>Mods moved into another mod folder, as Steam moves an install folder: each one's
        /// folder is copied there (a name already taken gets " (2)"), the library points at the copy,
        /// and the old folder is deleted unless keep (another manager may still use it). Refused,
        /// before anything is copied, for a folder that is not the mod's own.</summary>
        public static List<(Mod Mod, string From, string To)> Move(Library lib, IList<Mod> mods, string target, bool keep, string game)
        {
            var into = Paths.Abs(target);
            var plan = new List<(Mod Mod, string From, string To)>();
            foreach (var m in mods)
            {
                var from = Ops.OwnFolder(lib, m, game, "moved");
                if (!Directory.Exists(from)) throw new ModError("its folder is gone: " + from, "gone", from);
                if (string.Equals(System.IO.Path.GetDirectoryName(from.TrimEnd('\\')), into.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                var name = System.IO.Path.GetFileName(from.TrimEnd('\\'));
                var to = Paths.Join(into, name);
                for (int n = 2; Directory.Exists(to) || File.Exists(to) || plan.Any(x => string.Equals(x.To, to, StringComparison.OrdinalIgnoreCase)); n++)
                    to = Paths.Join(into, name + " (" + n + ")");
                plan.Add((m, from, to));
            }
            Directory.CreateDirectory(into);
            var done = new List<(Mod Mod, string From, string To)>();
            foreach (var (m, from, to) in plan)
            {
                var tmp = to + ".moving";
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                CopyTree(from, tmp);
                Directory.Move(tmp, to);
                m.Path = to;
                lib.Save();                                  // each one as it is done: a stop halfway loses nothing
                done.Add((m, from, to));
                if (!keep)
                {
                    try { DeleteTree(from); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        throw new ModError("moved to " + to + ", but the old folder could not be deleted (" + e.Message + "): " + from, "delete_failed", from);
                    }
                }
            }
            return done;
        }

        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Paths.Join(to, System.IO.Path.GetFileName(f)));
            foreach (var d in Directory.EnumerateDirectories(from)) CopyTree(d, Paths.Join(to, System.IO.Path.GetFileName(d)));
        }

        static void DeleteTree(string folder)
        {
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
                if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(folder, true);
        }
    }
}
