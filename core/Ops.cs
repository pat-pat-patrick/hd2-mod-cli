using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    public static class Ops
    {
        /// <summary>A name as Python's %r writes it, near enough for a message.</summary>
        public static string Repr(string s) => "'" + s + "'";

        static bool IsDigits(string s) => s.Length > 0 && s.All(c => c >= '0' && c <= '9');

        /// <summary>The game folder from HD2 Arsenal's settings or Steam's library list, if either knows it.</summary>
        public static string FindGame()
        {
            var arsenal = Store.ArsenalFile();
            if (arsenal != null)
            {
                try
                {
                    var g = Json.Str(Store.ReadJson(arsenal) as Dictionary<string, object>, "userGameDir");
                    if (!string.IsNullOrEmpty(g) && Directory.Exists(Paths.Join(g, "data"))) return g;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { }
            }
            var roots = new List<string>();
            foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            {
                var v = Environment.GetEnvironmentVariable(env);
                if (!string.IsNullOrEmpty(v)) roots.Add(Paths.Join(v, "Steam"));
            }
            foreach (var drive in "CDEFGHIJ")                   // Steam installed on another drive
                foreach (var rel in new[] { @"Program Files (x86)\Steam", @"Program Files\Steam", "Steam", "SteamLibrary" })
                    roots.Add(drive + ":\\" + rel);
            foreach (var steam in roots)
            {
                var vdf = Paths.Join(Paths.Join(steam, "steamapps"), "libraryfolders.vdf");
                var libs = new List<string> { steam };
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf, Encoding.UTF8), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                foreach (var lib in libs)
                {
                    var g = Paths.Join(Paths.Join(Paths.Join(lib, "steamapps"), "common"), "Helldivers 2");
                    if (Directory.Exists(Paths.Join(g, "data"))) return g;
                }
            }
            return null;
        }

        /// <summary>The game folder for a folder a player chose: the game folder itself, its
        /// data or its bin (the one with helldivers2.exe); null when it is none of them.</summary>
        public static string GameFolderOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var p = Paths.Norm(path.Trim());
            var name = System.IO.Path.GetFileName(p);
            if (string.Equals(name, "data", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase))
            {
                var up = System.IO.Path.GetDirectoryName(p);
                if (up != null && Directory.Exists(Paths.Join(up, "data"))) return up;
            }
            return Directory.Exists(Paths.Join(p, "data")) ? p : null;
        }

        public static Mod NewMod(string folder, string name = null, string source = null)
        {
            var manifest = Manifest.Read(folder);
            return new Mod
            {
                Id = Guid.NewGuid().ToString(),
                Name = !string.IsNullOrEmpty(name) ? name
                     : !string.IsNullOrEmpty(manifest?.Name) ? manifest.Name
                     : System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')),
                Path = Paths.Abs(folder), Source = source ?? Paths.Abs(folder), Enabled = true,
                Options = manifest?.Defaults() ?? new List<OptionChoice>(),
            };
        }

        /// <summary>A zip unpacked into the library folder (once per zip content).</summary>
        public static string Unpack(string zipPath, Dictionary<string, object> cfg)
        {
            string digest;
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(zipPath))
                digest = string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2"))).Substring(0, 12);
            var stem = Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(zipPath), @"[^\w .()-]+", "_").Trim();
            var dest = Paths.Join(Store.ModsDir(cfg), stem + "_" + digest);
            if (Directory.Exists(dest)) return dest;
            var tmp = dest + ".unpacking";
            if (Directory.Exists(tmp)) { try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            var root = Paths.Norm(tmp);
            if (!IsZip(zipPath)) ExtractOther(zipPath, tmp);
            else using (var z = ZipFile.OpenRead(zipPath))
            {
                var targets = new List<(ZipArchiveEntry Entry, string Target)>();
                foreach (var e in z.Entries)
                {
                    var target = Paths.Norm(Paths.Join(tmp, e.FullName));
                    if (!target.StartsWith(root + "\\", StringComparison.Ordinal) && target != root)
                        throw new ModError(zipPath + ": a path leaves the folder: " + e.FullName);
                    targets.Add((e, target));
                }
                Directory.CreateDirectory(tmp);
                foreach (var (e, target) in targets)
                {
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                }
            }
            // a zip with one folder at the top that holds the manifest: that folder is the mod
            var entries = Deploy.List(tmp).ToList();
            if (entries.Count == 1 && Directory.Exists(Paths.Join(tmp, entries[0])) && !File.Exists(Paths.Join(tmp, "manifest.json")))
            {
                Directory.Move(Paths.Join(tmp, entries[0]), dest);
                try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            else Directory.Move(tmp, dest);
            return dest;
        }

        /// <summary>A 7z, rar or tar archive (Nexus files often are) unpacked by Windows's own
        /// tar.exe (bsdtar on libarchive: 7z and rar read since Windows 10 1803 / 11). It keeps
        /// every path inside the folder (no "..", no absolute paths without -P).</summary>
        static void ExtractOther(string archive, string into)
        {
            var tar = Paths.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            if (!File.Exists(tar)) throw new ModError("only zips can be unpacked here (Windows's tar.exe is missing): " + archive, "not_zip", archive);
            Directory.CreateDirectory(into);
            var psi = new System.Diagnostics.ProcessStartInfo(tar, "-xf \"" + archive + "\" -C \"" + into + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    try { Directory.Delete(into, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    throw new ModError("cannot unpack " + archive + ": " + err.Result.Trim(), "not_zip", archive);
                }
            }
        }

        static bool IsArchive(string path) =>
            File.Exists(path) && (IsZip(path) || new[] { ".7z", ".rar", ".tar", ".gz", ".xz" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()));

        static bool IsZip(string path)
        {
            if (!File.Exists(path)) return false;
            // a zip starts "PK": anything else is not opened as one, as .NET looks for a zip's end
            // record by reading the whole file backwards -- 40 s for a 300 MB 7z, twice per
            // download, all of it at 100% (the user, 2026-10-30)
            var head = new byte[4];
            using (var f = File.OpenRead(path))
                if (f.Read(head, 0, 4) < 4 || head[0] != 'P' || head[1] != 'K') return false;
            try
            {
                using (ZipFile.OpenRead(path)) return true;
            }
            catch (InvalidDataException) { return false; }
        }

        public static string SourceFolder(string path, Dictionary<string, object> cfg, bool copy = false)
        {
            if (Directory.Exists(path))
            {
                if (!copy) return Paths.Abs(path);
                var dest = Paths.Join(Store.ModsDir(cfg), System.IO.Path.GetFileName(Paths.Abs(path)));
                if (File.Exists(dest) || Directory.Exists(dest)) throw new ModError("already in the library folder: " + dest);
                CopyTree(path, dest);
                return dest;
            }
            if (IsArchive(path)) return Unpack(path, cfg);
            throw new ModError("not a folder or an archive (zip, 7z, rar): " + path);
        }

        /// <summary>A folder this program unpacked a zip into: the zip's name and the first 12
        /// characters of its SHA-256, as Unpack writes it. A folder added as it stands, or one of
        /// another manager's (HD2 Arsenal's are "..._AR123456"), never matches -- the library
        /// folder is often HD2 Arsenal's own, and nothing in it but our own unpacked folders may
        /// ever be deleted.</summary>
        static readonly Regex Unpacked = new Regex(@"_[0-9a-f]{12}$");

        /// <summary>Whether this folder is one this program unpacked, directly in the library
        /// folder, and a real folder rather than a link to somewhere else.</summary>
        public static bool Ours(string folder, Dictionary<string, object> cfg)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            string full, mods;
            try
            {
                full = Paths.Abs(folder);
                mods = Paths.Abs(Store.ModsDir(cfg));
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return false; }
            if (!string.Equals(System.IO.Path.GetDirectoryName(full), mods, StringComparison.OrdinalIgnoreCase)) return false;
            if (!Unpacked.IsMatch(System.IO.Path.GetFileName(full))) return false;
            try
            {
                var info = new DirectoryInfo(full);
                return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        /// <summary>The folders this program unpacked that no mod in the library uses any more:
        /// what a mod's older versions leave behind, which is nothing for a small mod and
        /// hundreds of megabytes for an armour pack (the user, 2026-10-25).</summary>
        public static List<string> Leftovers(Library lib, Dictionary<string, object> cfg)
        {
            var mods = Store.ModsDir(cfg);
            if (!Directory.Exists(mods)) return new List<string>();
            // the versions kept for a rollback are in use too (Versions)
            var used = Versions.Used(lib);
            var out_ = new List<string>();
            foreach (var d in Directory.EnumerateDirectories(mods))
                if (Ours(d, cfg) && !used.Contains(Paths.Abs(d))) out_.Add(Paths.Abs(d));
            out_.Sort(StringComparer.CurrentCultureIgnoreCase);
            return out_;
        }

        /// <summary>Deletes one of those folders. The mod's files in the game folder are hard
        /// links to it, so they keep the data and the game is untouched; the next deploy writes
        /// them again from the version the library now holds.</summary>
        public static bool Drop(string folder)
        {
            try { Directory.Delete(folder, true); return true; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Paths.Join(to, System.IO.Path.GetFileName(f)));
            foreach (var d in Directory.EnumerateDirectories(from)) CopyTree(d, Paths.Join(to, System.IO.Path.GetFileName(d)));
        }

        static bool Within(string inner, string outer) =>
            inner.StartsWith(outer.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        static bool SamePath(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        /// <summary>A mod off the disk for good (the user, 2026-10-24: "能把 mod 文件真实从硬盘上删除"):
        /// out of the library, out of the game (a deploy), then its folder deleted, not to the
        /// Recycle Bin. Refused before anything changes when the game runs, or when the folder
        /// is not the mod's own (a drive, the game or settings folders or one above them, or a
        /// folder another mod in the library uses). The zip it was added from is kept.</summary>
        /// <summary>A mod's folder when it is its own: not a drive, the game, settings or a library
        /// folder or one above them, and no other mod in the library uses it or a folder in it.</summary>
        public static string OwnFolder(Library lib, Mod mod, string game, string doing = "deleted")
        {
            var folder = Paths.Abs(mod.Path);
            var root = System.IO.Path.GetPathRoot(folder) ?? "";
            var guarded = new[] { game, Store.Home, Store.ModsDir() }.Concat(Storage.Registered())
                .Where(x => !string.IsNullOrEmpty(x)).Select(Paths.Abs).ToList();
            if (SamePath(folder, root) || guarded.Any(g => SamePath(folder, g) || Within(g, folder)) || (game != null && Within(folder, Paths.Abs(game))))
                throw new ModError("not a mod's own folder, so it is not " + doing + ": " + folder, "protected", folder);
            var others = lib.Mods.Where(m => m != mod && !string.IsNullOrEmpty(m.Path)).Select(m => Paths.Abs(m.Path));
            if (others.Any(o => SamePath(o, folder) || Within(o, folder) || Within(folder, o)))
                throw new ModError("other mods in the library use this folder, so it is not " + doing + ": " + folder, "shared", folder);
            return folder;
        }

        public static (DeployResult Deployed, string Folder, long Bytes) DeleteMod(Library lib, Mod mod, string game)
        {
            var folder = OwnFolder(lib, mod, game);
            if (Deploy.GameRunning()) throw new ModError("the game is running: close it first", "game_running");

            long bytes = 0;
            if (Directory.Exists(folder))
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)) bytes += f.Length;
            lib.Mods.Remove(mod);
            lib.Save();
            var r = Deploy.Run(lib, game);
            try
            {
                if (Directory.Exists(folder))
                {
                    foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
                        if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new ModError("out of the library and the game, but its folder could not be deleted (" + e.Message + "): " + folder,
                                   "delete_failed", folder);
            }
            // the version kept for a rollback goes with it
            try { Versions.Forget(lib, new[] { mod }); } catch (ModError) { }
            return (r, folder, bytes);
        }

        /// <summary>Several mods deleted from the disk at once (the Storage page's ticked mods; the
        /// user, 2026-10-25: a page for seeing what takes space is where it gets deleted). Every
        /// folder is checked before anything changes, one deploy takes them out of the game, then
        /// the folders go. A folder that cannot be deleted is named; the others still go.</summary>
        public static (DeployResult Deployed, long Bytes, List<string> Failed) DeleteMods(Library lib, IList<Mod> mods, string game)
        {
            var go = mods.Distinct().Select(m => (Mod: m, Folder: OwnFolder(lib, m, game))).ToList();
            if (Deploy.GameRunning()) throw new ModError("the game is running: close it first", "game_running");
            long bytes = 0;
            foreach (var x in go)
                if (Directory.Exists(x.Folder))
                    foreach (var f in new DirectoryInfo(x.Folder).EnumerateFiles("*", SearchOption.AllDirectories)) bytes += f.Length;
            foreach (var x in go) lib.Mods.Remove(x.Mod);
            lib.Save();
            var r = Deploy.Run(lib, game);
            var failed = new List<string>();
            foreach (var x in go)
                try
                {
                    if (!Directory.Exists(x.Folder)) continue;
                    foreach (var f in new DirectoryInfo(x.Folder).EnumerateFiles("*", SearchOption.AllDirectories))
                        if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                    Directory.Delete(x.Folder, true);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { failed.Add(x.Folder); }
            // the versions kept for a rollback go with them
            foreach (var x in go)
                try { Versions.Forget(lib, new[] { x.Mod }); } catch (ModError e) { failed.Add(e.Arg ?? x.Mod.Name); }
            return (r, bytes, failed);
        }

        /// <summary>A mod by its number in `list` (from 1), its id, or a unique part of its name.</summary>
        public static Mod Pick(Library lib, string reference)
        {
            var mods = lib.Mods;
            if (IsDigits(reference) && int.TryParse(reference, out var n) && n >= 1 && n <= mods.Count) return mods[n - 1];
            var exact = mods.Where(m => string.Equals(m.Name, reference, StringComparison.OrdinalIgnoreCase) || m.Id == reference).ToList();
            if (exact.Count == 1) return exact[0];
            var part = mods.Where(m => m.Name.IndexOf(reference, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (part.Count == 1) return part[0];
            if (part.Count == 0) throw new ModError("no mod matches " + Repr(reference));
            throw new ModError(Repr(reference) + " matches " + part.Count + " mods: " + string.Join("; ", part.Select(m => m.Name)));
        }

        /// <summary>Whether HD2 Arsenal is set to "将第一个模组设置为最高优先级" (setTopPriority):
        /// its top mod wins, and an import reverses its list (ImportArsenal).</summary>
        public static bool ArsenalTopWins(string path = null)
        {
            path = path ?? Store.ArsenalFile();
            if (path == null) return false;
            try { return Truthy(Store.ReadJson(path) as Dictionary<string, object> ?? new Dictionary<string, object>(), "setTopPriority", false); }
            catch (Exception e) when (e is ModError || e is System.IO.IOException || e is FormatException || e is UnauthorizedAccessException) { return false; }
        }

        /// <summary>HD2 Arsenal's deployed (else selected) profile, read only: names,
        /// folders, order, on/off and option choices; and its game folder.</summary>
        /// <summary>How many mods a folder holds one level down, when it is no mod itself: no
        /// manifest.json and no patch file at its top, and subfolders that each have one of them at
        /// theirs. 0 for a mod folder.</summary>
        public static int ModsInside(string folder)
        {
            try
            {
                if (File.Exists(System.IO.Path.Combine(folder, "manifest.json"))) return 0;
                if (Deploy.List(folder).Any(f => Deploy.Patch.IsMatch(f))) return 0;
                return Directory.EnumerateDirectories(folder).Count(sub =>
                    File.Exists(System.IO.Path.Combine(sub, "manifest.json")) || Deploy.List(sub).Any(f => Deploy.Patch.IsMatch(f)));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return 0; }
        }

        public static (Library Lib, string Game) ImportArsenal(string path = null, string profile = null)
        {
            path = path ?? Store.ArsenalFile();
            if (path == null) throw new ModError("HD2 Arsenal's hd2a_data.json was not found");
            var d = Store.ReadJson(path) as Dictionary<string, object> ?? new Dictionary<string, object>();
            var key = First(profile, Json.Str(d, "deployedProfile"), Json.Str(d, "selectedProfile"), "default");
            var prof = Json.Obj(Json.Obj(d, "modsList"), key);
            if (prof == null) throw new ModError("no profile " + Repr(key) + " in " + path);
            // Arsenal 0.37 (librarySystemVersion 1) keeps every mod once in modsLibrary; a profile
            // only names each by uuid, with its own on/off and choices (optionsConfig). Before, each
            // profile entry held the mod itself. Read as the old shape, the new one gave no mods,
            // and Arsenal was not found at all (a player's file, 2026-10-26)
            var library = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in Json.List(d, "modsLibrary").OfType<Dictionary<string, object>>())
            {
                var u = Json.Str(x, "uuid");
                if (u != null && !library.ContainsKey(u)) library[u] = x;
            }
            var lib = new Library();
            foreach (var entry in Json.List(prof, "mods").OfType<Dictionary<string, object>>())
            {
                if (Json.Str(entry, "type") == "separator") continue;          // a heading in Arsenal's list, no mod
                var m = entry;
                if (Json.Str(entry, "path") == null && Json.Str(entry, "uuid") is string id && library.TryGetValue(id, out var kept)) m = kept;
                var options = new List<OptionChoice>();
                var chosen = entry.ContainsKey("optionsConfig") ? Json.List(entry, "optionsConfig") : Json.List(m, "options");
                foreach (var o in chosen.OfType<Dictionary<string, object>>())
                    options.Add(new OptionChoice
                    {
                        Name = Json.Str(o, "name") ?? "", Enabled = Truthy(o, "enabled", true),
                        Subs = Json.List(o, "suboptions").OfType<Dictionary<string, object>>()
                                   .Select(s => new SubChoice { Name = Json.Str(s, "name") ?? "", Enabled = Truthy(s, "enabled", false) }).ToList(),
                    });
                var p = Json.Str(m, "path");
                // an entry without a folder is no mod to bring (one in the user's Default Profile, 2026-10-25)
                if (p == null) continue;
                lib.Mods.Add(new Mod
                {
                    Id = First(Json.Str(m, "uuid"), Guid.NewGuid().ToString()),
                    Name = First(Json.Str(m, "label"), System.IO.Path.GetFileName(p)),
                    Path = p, Source = p, Enabled = Truthy(entry, "enabled", true), Options = options,
                    Nexus = ArsenalNexus(Json.Obj(m, "nexusData")), Tags = ArsenalTags(m),
                });
            }
            // Arsenal's "将第一个模组设置为最高优先级" (setTopPriority) keeps its list as shown and
            // deploys it from the bottom up, so the top one wins. Here the last one wins, so the
            // list comes in the other way round (proven 2026-10-25: Arsenal deployed 278 files
            // with it on; the list reversed matched all of them, the list as stored did not).
            if (Truthy(d, "setTopPriority", false)) lib.Mods.Reverse();
            // entries, and not one of them read: a shape this program does not know, said as such
            // (the user, 2026-10-26: a list read as empty was hidden, as if there were none)
            int entries = Json.List(prof, "mods").OfType<Dictionary<string, object>>().Count(x => Json.Str(x, "type") != "separator");
            if (entries > 0 && lib.Mods.Count == 0)
                throw new ModError("HD2 Arsenal's list has " + entries + " mods, and none of them could be read: its format may have changed" +
                                   (d.ContainsKey("librarySystemVersion") ? " (librarySystemVersion " + Json.Long(d, "librarySystemVersion") + ")" : ""), "bad_format", path);
            lib.ImportedFrom = "HD2 Arsenal: " + (Json.Str(prof, "label") ?? key);
            return (lib, Json.Str(d, "userGameDir"));
        }

        /// <summary>HD2 Arsenal's tags, as labels. It keeps them under lowercase keys ("misc",
        /// "armor", "vfx") and shows "Misc", "Armor", "VFX" (the user, 2026-10-25, from its
        /// screen): a word of three letters or fewer is taken as an abbreviation.</summary>
        static List<string> ArsenalTags(Dictionary<string, object> m)
        {
            string Shown(string key) => string.Join(" ", key.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Length <= 3 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w.Substring(1)));
            var tags = new List<string>();
            foreach (var t in Json.List(m, "tags"))
                if (t is string s && s.Trim().Length > 0)
                {
                    var name = Shown(s.Trim());
                    if (!tags.Contains(name, StringComparer.CurrentCultureIgnoreCase)) tags.Add(name);
                }
            return tags;
        }

        /// <summary>HD2 Arsenal's nexusData {modId, fileId, version, updateTimestamp} (ids as strings).</summary>
        static NexusRef ArsenalNexus(Dictionary<string, object> nd)
        {
            if (nd == null) return null;
            long Num(string k) => nd.TryGetValue(k, out var v) && long.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), out var n) ? n : 0;
            var mod = Num("modId");
            if (mod <= 0) return null;
            return new NexusRef { Game = Nexus.Game, Mod = mod, File = Num("fileId"), Version = Json.Str(nd, "version"), Time = Num("updateTimestamp") };
        }

        static string First(params string[] xs) => xs.FirstOrDefault(x => !string.IsNullOrEmpty(x));

        /// <summary>Python's bool(x.get(k, fallback)).</summary>
        static bool Truthy(Dictionary<string, object> d, string k, bool fallback)
        {
            if (!d.TryGetValue(k, out var v)) return fallback;
            switch (v)
            {
                case null: return false;
                case bool b: return b;
                case long n: return n != 0;
                case double x: return x != 0;
                case string s: return s.Length > 0;
                default: return true;
            }
        }

        /// <summary>An option by number (from 1) or name; 'option/sub-option' or '2.3' for a sub-option.</summary>
        public static (OptionChoice Option, SubChoice Sub) OptionAt(List<OptionChoice> chosen, string reference)
        {
            if (Regex.IsMatch(reference, @"^\d+\.\d+$"))
            {
                int dot = reference.IndexOf('.');
                reference = reference.Substring(0, dot) + "/" + reference.Substring(dot + 1);
            }
            int slash = reference.IndexOf('/');
            string head = slash < 0 ? reference : reference.Substring(0, slash);
            string tail = slash < 0 ? "" : reference.Substring(slash + 1);
            var o = Find(chosen, x => x.Name, head);
            return (o, tail.Length > 0 ? Find(o.Subs, x => x.Name, tail) : null);
        }

        static T Find<T>(List<T> items, Func<T, string> name, string r)
        {
            if (IsDigits(r) && int.TryParse(r, out var n) && n >= 1 && n <= items.Count) return items[n - 1];
            var hits = items.Where(x => string.Equals(name(x), r, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) hits = items.Where(x => name(x).IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hits.Count != 1) throw new ModError(hits.Count + " options match " + Repr(r));
            return hits[0];
        }
    }
}
