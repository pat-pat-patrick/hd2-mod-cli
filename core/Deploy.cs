using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>One file the load order needs: its name in data\, where it comes from, whose it is.</summary>
    public class Wanted
    {
        public string Name, Src, Mod;
    }

    /// <summary>What a deploy would do.</summary>
    public class Diff
    {
        public NativeConfigs.Plan Native;
        public List<Wanted> All = new List<Wanted>(), Keep = new List<Wanted>(), Writes = new List<Wanted>();
        public List<(string From, string To)> Moves = new List<(string, string)>();
        /// <summary>Files no longer wanted; Replaced: spare files on a wanted name that is rewritten.</summary>
        public List<string> Deletes = new List<string>(), Replaced = new List<string>();
        public int Kept => Keep.Count;
        public int Deleted => Deletes.Count + Replaced.Count;
    }

    public class DeployResult
    {
        public int NativeWritten;
        public int Kept, Moved, Written, Deleted, Files, Linked, Copied;
        public long CopiedBytes, SavedBytes;
    }

    public class RelinkResult
    {
        public int Linked, Candidates;
        public long Bytes;
    }

    public static class Deploy
    {
        public static readonly Regex Patch = new Regex(@"^([0-9a-f]{16})\.patch_(\d+)$");
        public static readonly string[] Companions = { ".gpu_resources", ".stream" };
        public static readonly Regex Managed = new Regex(@"^[0-9a-f]{16}\.patch_\d+(\.gpu_resources|\.stream)?$");
        const string TmpSuffix = ".hd2mod-move";

        /// <summary>Whether helldivers2.exe runs; tests replace it.</summary>
        public static Func<bool> GameRunning = () => Process.GetProcessesByName("helldivers2").Length > 0;

        /// <summary>Whether two paths are on one volume (a hard link is possible); tests replace it.</summary>
        public static Func<string, string, bool> SameDrive = Native.SameDrive;

        /// <summary>The names in a folder (files and folders), as os.listdir.</summary>
        public static IEnumerable<string> List(string folder) =>
            Directory.EnumerateFileSystemEntries(folder).Select(System.IO.Path.GetFileName);

        static IEnumerable<string> Files(string folder) => Directory.EnumerateFiles(folder).Select(System.IO.Path.GetFileName);

        /// <summary>The folders a mod deploys, in order: the root without options; else each
        /// enabled option's folders, then its enabled sub-options' folders.</summary>
        public static List<string> IncludedFolders(Mod mod)
        {
            var folder = mod.Path;
            var manifest = Manifest.Of(mod);
            var options = manifest?.Options ?? new List<ManifestOption>();
            if (options.Count == 0)
            {
                if (List(folder).Any(f => Patch.IsMatch(f))) return new List<string> { folder };
                // no manifest and nothing at the top: every folder that holds patch files
                var found = new List<string>();
                Walk(folder, found);
                return found;
            }
            var chosen = Manifest.Merged(manifest, mod.Options);
            var out_ = new List<string>();
            for (int i = 0; i < options.Count; i++)
            {
                var o = options[i];
                var c = chosen[i];
                if (!c.Enabled) continue;
                foreach (var inc in o.Include) out_.Add(Paths.Norm(Paths.Join(folder, inc)));
                for (int j = 0; j < o.SubOptions.Count && j < c.Subs.Count; j++)
                    if (c.Subs[j].Enabled)
                        foreach (var inc in o.SubOptions[j].Include) out_.Add(Paths.Norm(Paths.Join(folder, inc)));
            }
            return out_;
        }

        /// <summary>os.walk with its folders sorted: a folder, then each subfolder in name order.</summary>
        static void Walk(string folder, List<string> found)
        {
            if (Files(folder).Any(f => Patch.IsMatch(f))) found.Add(folder);
            var dirs = Directory.EnumerateDirectories(folder).Select(System.IO.Path.GetFileName).ToList();
            dirs.Sort(string.CompareOrdinal);
            foreach (var d in dirs) Walk(Paths.Join(folder, d), found);
        }

        /// <summary>The files the load order needs, every archive numbered from 0. With
        /// errors, a mod that cannot be read is noted there by name and left out; without,
        /// it stops the command.</summary>
        public static List<Wanted> Plan(Library lib, Dictionary<string, string> errors = null)
        {
            var counters = new Dictionary<string, int>();
            var out_ = new List<Wanted>();
            // the loader in effect last, the other loaders not at all (Loaders)
            foreach (var mod in Loaders.DeployOrder(lib))
            {
                string problem = null;
                List<string> folders = null;
                if (!Directory.Exists(mod.Path)) problem = "its folder is gone: " + mod.Path;
                else
                {
                    try
                    {
                        folders = IncludedFolders(mod);
                        var missing = folders.FirstOrDefault(f => !Directory.Exists(f));
                        if (missing != null) problem = "an included folder is missing: " + missing;
                    }
                    catch (ModError e) { problem = e.Message; }
                }
                if (problem != null)
                {
                    if (errors == null) throw new ModError(mod.Name + ": " + problem);
                    errors[mod.Name] = problem;
                    continue;
                }
                foreach (var folder in folders)
                {
                    var mains = new List<(long N, string Archive, string File)>();
                    foreach (var f in List(folder))
                    {
                        var m = Patch.Match(f);
                        if (m.Success) mains.Add((long.Parse(m.Groups[2].Value), m.Groups[1].Value, f));
                    }
                    mains.Sort((a, b) =>
                    {
                        int c = a.N.CompareTo(b.N);
                        if (c == 0) c = string.CompareOrdinal(a.Archive, b.Archive);
                        return c != 0 ? c : string.CompareOrdinal(a.File, b.File);
                    });
                    foreach (var (_, archive, f) in mains)
                    {
                        counters.TryGetValue(archive, out var n);
                        counters[archive] = n + 1;
                        var target = archive + ".patch_" + n;
                        out_.Add(new Wanted { Name = target, Src = Paths.Join(folder, f), Mod = mod.Name });
                        foreach (var ext in Companions)
                        {
                            var src = Paths.Join(folder, f + ext);
                            if (File.Exists(src)) out_.Add(new Wanted { Name = target + ext, Src = src, Mod = mod.Name });
                        }
                    }
                }
            }
            // a boot that only starts its own mod goes to the game off the start-up slot, for
            // Boot Mod Loader to run (GuestBoots; the user, 2026-10-26)
            NativeConfigs.Filter(lib, out_);
            // Filtering legacy installers can leave holes before a HUD or the loader.
            // Number the surviving mains and their companions together, preserving order.
            counters.Clear();
            var names = new Dictionary<string, string>();
            foreach (var w in out_)
            {
                var match = Patch.Match(w.Name);
                if (!match.Success) continue;
                var archive = match.Groups[1].Value;
                counters.TryGetValue(archive, out var n);
                counters[archive] = n + 1;
                names[w.Name] = archive + ".patch_" + n;
            }
            foreach (var w in out_)
            {
                var suffix = Companions.FirstOrDefault(ext => w.Name.EndsWith(ext, StringComparison.Ordinal)) ?? "";
                var main = w.Name.Substring(0, w.Name.Length - suffix.Length);
                if (names.TryGetValue(main, out var name)) w.Name = name + suffix;
            }
            GuestBoots.Apply(out_);
            return out_;
        }

        // ------------------------------------------------------------ deploying

        static readonly long Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        /// <summary>[size, mtime in ns], as Python's os.stat gives them on Windows.</summary>
        public static List<object> Stamp(string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new FileNotFoundException("no such file", path);
            return new List<object> { fi.Length, (fi.LastWriteTimeUtc.Ticks - Epoch) * 100 };
        }

        static bool SameStamp(object recorded, List<object> s) =>
            recorded is List<object> r && r.Count == 2 && r[0] is long a && r[1] is long b && a == (long)s[0] && b == (long)s[1];

        public static long Size(string path) => new FileInfo(path).Length;

        public static long SizeOrZero(string path)
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : 0;
        }

        // answers of SameContent, by both files' identity, size and time, kept in same.json:
        // a mod's option variants often ship one identical 146 MB .gpu_resources each, and
        // after a mod is turned off every state read compared them again (1 GB, 500 ms)
        static Dictionary<string, object> sameKnown;
        static string sameHome;
        static bool sameDirty;

        static string Key(string path)
        {
            var id = Native.FileId(path);
            if (id == null) return null;
            var s = Stamp(path);
            return id.Value.Item1.ToString("x") + ":" + id.Value.Item2.ToString("x") + ":" + s[0] + ":" + s[1];
        }

        static bool SameContentKnown(string a, string b, bool verify)
        {
            if (Size(a) != Size(b)) return false;
            if (verify || Size(a) < 1 << 20) return SameContent(a, b);  // small: reading is as cheap
            string ka = Key(a), kb = Key(b);
            if (ka == null || kb == null) return SameContent(a, b);
            var key = string.CompareOrdinal(ka, kb) < 0 ? ka + " " + kb : kb + " " + ka;
            if (sameKnown == null || sameHome != Store.Home)
            {
                sameHome = Store.Home;
                try { sameKnown = Store.Load("same.json"); }
                catch (FormatException) { sameKnown = new Dictionary<string, object>(); }
                sameDirty = false;
            }
            if (sameKnown.TryGetValue(key, out var v) && v is bool known) return known;
            bool same = SameContent(a, b);
            sameKnown[key] = same;
            sameDirty = true;
            return same;
        }

        /// <summary>same.json written when it learned something; answers about files that
        /// are gone are dropped then.</summary>
        static void SaveKnown()
        {
            if (!sameDirty || sameKnown == null) return;
            sameDirty = false;
            if (sameKnown.Count > 2000)
                foreach (var k in sameKnown.Keys.Take(sameKnown.Count - 1000).ToList()) sameKnown.Remove(k);
            Store.Save("same.json", sameKnown);
        }

        static bool SameContent(string a, string b)
        {
            if (Size(a) != Size(b)) return false;
            // 4 KB buffers first, 1 MB ones only once the starts match: two archives of one
            // size differ in their headers, and two 1 MB buffers for each of 308 such pairs
            // made turning one mod off (170 renames) take 550 ms
            using (var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
            using (var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
            {
                var x = new byte[4096];
                var y = new byte[4096];
                while (true)
                {
                    int nx = Fill(fa, x), ny = Fill(fb, y);
                    if (nx != ny) return false;
                    if (nx == 0) return true;
                    for (int i = 0; i < nx; i++) if (x[i] != y[i]) return false;
                    if (x.Length < 1 << 20)
                    {
                        x = new byte[1 << 20];
                        y = new byte[1 << 20];
                    }
                }
            }
        }

        internal static int Fill(Stream s, byte[] buf) => Fill(s, buf, buf.Length);

        internal static int Fill(Stream s, byte[] buf, int want)
        {
            int n = 0;
            while (n < want)
            {
                int r = s.Read(buf, n, want - n);
                if (r == 0) break;
                n += r;
            }
            return n;
        }

        /// <summary>Whether the file in the game folder already is src: the same file (a
        /// hard link); else what the last deploy recorded for it, when neither file changed
        /// since; else the same bytes. Size and time alone are not enough: a mod's option
        /// variants are often built in the same second with the same size.</summary>
        static bool Holds(string diskPath, string src, Dictionary<string, object> record, bool verify)
        {
            var same = Native.SameFile(diskPath, src);
            if (same == null) return false;
            if (same == true) return true;
            if (!verify && Recorded(diskPath, src, record)) return true;
            return SameContentKnown(diskPath, src, verify);
        }

        /// <summary>Whether the last deploy recorded diskPath as src, and neither file changed since.</summary>
        static bool Recorded(string diskPath, string src, Dictionary<string, object> record)
        {
            if (record == null || Json.Str(record, "src") != src) return false;
            var disk = Stamp(diskPath);
            var source = Stamp(src);
            return (long)disk[0] == (long)source[0]
                && record.TryGetValue("disk", out var rd) && SameStamp(rd, disk)
                && record.TryGetValue("source", out var rs) && SameStamp(rs, source);
        }

        static Dictionary<string, object> Record(Dictionary<string, object> files, string name) =>
            files != null && files.TryGetValue(name, out var r) ? r as Dictionary<string, object> : null;

        /// <summary>A hard link to src at dst, else a copy (another drive, or --copy).</summary>
        static bool Place(string src, string dst, bool copy)
        {
            if (!copy && Native.Link(src, dst)) return true;
            File.Copy(src, dst);
            return false;
        }

        static string Data(string game) => Paths.Join(game, "data");

        public static Diff Compute(Library lib, string game, bool verify = false, Dictionary<string, string> errors = null)
        {
            var data = Data(game);
            var d = new Diff { All = Plan(lib, errors), Native = NativeConfigs.Prepare(lib, game) };
            var record = Json.Obj(Store.Load("deployed.json"), "files") ?? new Dictionary<string, object>();
            var onDisk = new HashSet<string>(Files(data).Where(f => Managed.IsMatch(f)));
            var byName = new HashSet<string>(d.All.Select(w => w.Name));

            var todo = new List<Wanted>();
            foreach (var w in d.All)
            {
                var p = Paths.Join(data, w.Name);
                if (onDisk.Contains(w.Name) && Holds(p, w.Src, Record(record, w.Name), verify)) d.Keep.Add(w);
                else todo.Add(w);
            }
            // files on disk that are not where they are wanted: candidates to move instead of
            // rewrite, when one holds the same bytes (looked for among those of the same size)
            var kept = new HashSet<string>(d.Keep.Select(w => w.Name));
            var spare = onDisk.Where(f => !kept.Contains(f)).ToList();
            spare.Sort(string.CompareOrdinal);
            var movable = new Dictionary<long, List<string>>();
            foreach (var f in spare)
            {
                var size = Size(Paths.Join(data, f));
                if (!movable.TryGetValue(size, out var l)) movable[size] = l = new List<string>();
                l.Add(f);
            }
            // the cheap answers first, bytes last: a spare file that is the same file (a hard
            // link), else one the last deploy recorded as it, else one with the same bytes.
            // Reading bytes first made turning one mod off (170 renames) take 550 ms.
            var ids = new Dictionary<(uint, ulong), List<string>>();
            foreach (var f in spare)
            {
                var id = Native.FileId(Paths.Join(data, f));
                if (id == null) continue;
                if (!ids.TryGetValue(id.Value, out var l)) ids[id.Value] = l = new List<string>();
                l.Add(f);
            }
            var taken = new HashSet<string>();
            foreach (var w in todo)
            {
                string found = null;
                var sid = Native.FileId(w.Src);
                if (sid != null && ids.TryGetValue(sid.Value, out var linked)) found = linked.FirstOrDefault(f => !taken.Contains(f));
                if (found == null && movable.TryGetValue(Size(w.Src), out var same))
                {
                    if (!verify) found = same.FirstOrDefault(f => !taken.Contains(f) && Recorded(Paths.Join(data, f), w.Src, Record(record, f)));
                    if (found == null) found = same.FirstOrDefault(f => !taken.Contains(f) && SameContentKnown(Paths.Join(data, f), w.Src, verify));
                }
                if (found != null)
                {
                    taken.Add(found);
                    d.Moves.Add((found, w.Name));
                }
                else d.Writes.Add(w);
            }
            var movedFrom = new HashSet<string>(d.Moves.Select(m => m.From));
            d.Deletes = spare.Where(f => !movedFrom.Contains(f) && !byName.Contains(f)).ToList();
            // a spare file that sits on a wanted name and moves nowhere is replaced: deleted first
            d.Replaced = spare.Where(f => !movedFrom.Contains(f) && byName.Contains(f)).ToList();
            SaveKnown();
            return d;
        }

        static Dictionary<string, object> RecordOf(Wanted w, string disk) => new Dictionary<string, object>
        {
            ["src"] = w.Src, ["mod"] = w.Mod, ["disk"] = Stamp(disk), ["source"] = Stamp(w.Src),
        };

        public static DeployResult Run(Library lib, string game, bool dryRun = false, bool copy = false, bool verify = false,
                                       Action<string> log = null)
        {
            var data = Data(game);
            Diff d;
            Loaders.ReadAll = true;               // every archive read: a helper is not left out
            try { d = Compute(lib, game, verify); }
            finally { Loaders.ReadAll = false; }
            var r = new DeployResult { Kept = d.Kept, Moved = d.Moves.Count, Written = d.Writes.Count, Deleted = d.Deleted, Files = d.All.Count, NativeWritten = d.Native.Changes };
            if (dryRun)
            {
                if (log != null)
                {
                    foreach (var item in d.Native.Items.Where(i => i.Changed || i.TrackingChanged)) log("  native " + (item.Restore ? "restore " : "compose ") + item.Target + " <- " + string.Join(" + ", item.Mods));
                    foreach (var (a, b) in d.Moves) log("  move   " + a + " -> " + b);
                    foreach (var w in d.Writes) log("  write  " + w.Name + "  <- " + w.Src + " (" + w.Mod + ")");
                    foreach (var f in d.Deletes.Concat(d.Replaced)) log("  delete " + f);
                }
                return r;
            }
            if ((d.Moves.Count > 0 || d.Writes.Count > 0 || d.Deleted > 0 || d.Native.Changes > 0 || d.Native.LegacyChanged) && GameRunning())
                throw new ModError("the game is running: close it first", "game_running");
            NotWhileBusy();
            using (new Marked(game))
            {
            foreach (var f in d.Deletes.Concat(d.Replaced)) File.Delete(Paths.Join(data, f));
            foreach (var (a, _) in d.Moves) File.Move(Paths.Join(data, a), Paths.Join(data, a + TmpSuffix));
            foreach (var (a, b) in d.Moves) File.Move(Paths.Join(data, a + TmpSuffix), Paths.Join(data, b));
            foreach (var w in d.Writes)
            {
                var dst = Paths.Join(data, w.Name);
                if (File.Exists(dst)) File.Delete(dst);
                if (Place(w.Src, dst, copy)) r.Linked++;
                else
                {
                    r.Copied++;
                    r.CopiedBytes += Size(w.Src);
                }
            }
            d.Native.Apply();
            // what a manager that purges and copies everything would have written, less what
            // this deploy copied: the running total shown as "saved"
            long total = d.All.Sum(w => Size(w.Src));
            var before = Store.Load("deployed.json");
            r.SavedBytes = Json.Long(before, "saved_bytes") + Math.Max(0, total - r.CopiedBytes);
            // what each file is, with both files' size and time, so the next deploy can tell
            // an unchanged file without reading it
            var files = new Dictionary<string, object>();
            foreach (var w in d.All) files[w.Name] = RecordOf(w, Paths.Join(data, w.Name));
            Store.Save("deployed.json", new Dictionary<string, object>
            {
                ["game_dir"] = game, ["saved_bytes"] = r.SavedBytes, ["deploys"] = Json.Long(before, "deploys") + 1, ["files"] = files,
            });
            // when, and which mods: after a crash, what was in the game then (the user, 2026-10-26)
            try { LuaMods.Record(d.All.Select(w => w.Mod)); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            return r;
        }

        /// <summary>Files that are already right but are copies (another manager's, or
        /// --copy) made hard links to the library, where both are on one drive: no data is
        /// written, the copy's space comes back. Each is linked under a temporary name and
        /// renamed over the copy, so the game folder never misses a file.</summary>
        public static RelinkResult Relink(Library lib, string game, bool dryRun = false, bool verify = false)
        {
            var data = Data(game);
            var d = Compute(lib, game, verify);
            var todo = new List<Wanted>();
            foreach (var w in d.Keep)
            {
                var same = Native.SameFile(Paths.Join(data, w.Name), w.Src);
                if (same != false) continue;
                if (SameDrive(w.Src, data)) todo.Add(w);
            }
            var r = new RelinkResult { Candidates = todo.Count, Bytes = todo.Sum(w => Size(w.Src)) };
            if (dryRun || todo.Count == 0) return r;
            if (GameRunning()) throw new ModError("the game is running: close it first", "game_running");
            NotWhileBusy();
            using (new Marked(game))
            {
            var record = Store.Load("deployed.json");
            var files = Json.Obj(record, "files");
            if (files == null) record["files"] = files = new Dictionary<string, object>();
            foreach (var w in todo)
            {
                var dst = Paths.Join(data, w.Name);
                var tmp = dst + ".hd2mod-link";
                if (File.Exists(tmp)) File.Delete(tmp);
                if (!Native.Link(w.Src, tmp))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "cannot link " + tmp);
                Native.Replace(tmp, dst);
                files[w.Name] = RecordOf(w, dst);
                r.Linked++;
            }
            // the rest of the wanted files, as a deploy records them, if this is the first
            // record -- but only the ones that really are that file. A file under the right name
            // can be another mod's: numbering moves everything after a mod that was added or
            // taken out. Recording one of those says a mod is deployed when it is not (the user,
            // 2026-10-25: the window said the loader was deployed while the game ran the old one).
            foreach (var w in d.All)
            {
                if (files.ContainsKey(w.Name)) continue;
                var dst = Paths.Join(data, w.Name);
                if (File.Exists(dst) && Size(dst) == Size(w.Src) && SameContentKnown(dst, w.Src, false))
                    files[w.Name] = RecordOf(w, dst);
            }
            record["game_dir"] = game;
            Store.Save("deployed.json", record);
            }
            return r;
        }

        /// <summary>What the game folder holds that the library does not account for. A patch
        /// file left behind is the hardest kind of mistake to find: the game's own integrity
        /// check does not know these files and never removes them, so a player who later moves
        /// to another manager sees mods fight and has no way to see why (HS历史学家 on bilibili,
        /// 2026-10-25). Reading no bytes: names, sizes and the file id, which tells a hard link
        /// to the library from anything else. 65 ms on a library of 284 files.</summary>
        public class CheckResult
        {
            /// <summary>Wanted, and not in the game folder.</summary>
            public List<string> Missing = new List<string>();
            /// <summary>In the game folder, and not wanted: another manager's, or what an
            /// interrupted deploy left. A deploy deletes these.</summary>
            public List<string> Extra = new List<string>();
            /// <summary>Under the right name, and not the file the library holds.</summary>
            public List<string> Changed = new List<string>();
            /// <summary>Right, but a copy rather than a hard link: nothing is wrong with it,
            /// and relink gives the space back.</summary>
            public int Copies;
            public int Checked;
            /// <summary>The files a deploy would leave alone, that are not what it thinks they
            /// are. These are the ones worth saying out loud: everything else a deploy is on its
            /// way to put right, and the window already counts that as work to do. A file here
            /// means the deploy record is wrong about it, which is how a mod can sit in the
            /// library looking deployed while the game runs something else (the user,
            /// 2026-10-25).</summary>
            public List<string> Wrong = new List<string>();
            public bool Ok => Missing.Count == 0 && Extra.Count == 0 && Changed.Count == 0;
            /// <summary>Nothing the deploy is wrong about.</summary>
            public bool Trustworthy => Wrong.Count == 0;
        }

        /// <summary>Compares the game folder with the library. deep reads the bytes of the files
        /// that are copies; without it a copy of the same size is taken to be right.</summary>
        public static CheckResult Check(Library lib, string game, bool deep = false) =>
            Check(lib, game, game == null ? null : Compute(lib, game, false, new Dictionary<string, string>()), deep);

        /// <summary>The same against a plan that has already been worked out.</summary>
        public static CheckResult Check(Library lib, string game, Diff plan, bool deep = false)
        {
            var r = new CheckResult();
            if (game == null) return r;
            var data = Data(game);
            if (!Directory.Exists(data)) return r;
            var native = plan?.Native ?? NativeConfigs.Prepare(lib, game);
            r.Checked += native.Items.Count;
            r.Changed.AddRange(native.Items.Where(i => i.Changed).Select(i => "data/" + i.Target));
            var want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in Plan(lib, new Dictionary<string, string>())) want[w.Name] = w.Src;
            var onDisk = new HashSet<string>(Files(data).Where(f => Managed.IsMatch(f)), StringComparer.OrdinalIgnoreCase);
            foreach (var name in want.Keys.Where(n => !onDisk.Contains(n)).OrderBy(n => n, StringComparer.Ordinal)) r.Missing.Add(name);
            foreach (var name in onDisk.Where(n => !want.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal)) r.Extra.Add(name);
            foreach (var name in want.Keys.Where(onDisk.Contains).OrderBy(n => n, StringComparer.Ordinal))
            {
                var dst = Paths.Join(data, name);
                var src = want[name];
                r.Checked++;
                if (!File.Exists(src)) continue;                  // the mod's own folder is gone: its row says so
                if (Native.SameFile(dst, src) == true) continue;  // one file under two names
                if (Size(dst) != Size(src)) { r.Changed.Add(name); continue; }
                if (deep && !SameContent(dst, src)) { r.Changed.Add(name); continue; }
                r.Copies++;
            }
            // what the plan is already on its way to fix is not worth saying: only the files it
            // means to leave alone have to be right
            if (plan != null)
            {
                var keep = new HashSet<string>(plan.Keep.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var f in r.Missing.Concat(r.Changed))
                    if (keep.Contains(f)) r.Wrong.Add(f);
                r.Wrong.Sort(StringComparer.Ordinal);
            }
            return r;
        }

        /// <summary>A deploy another program is in the middle of. While one runs, the game
        /// folder is half renumbered, and anything read from it is about a state that is on its
        /// way somewhere: a window that reads it then shows a frightening number of changes that
        /// are nobody's fault (the user, 2026-10-25).</summary>
        public class Busy
        {
            public int Pid;
            public DateTime Since;
            public string Game;
        }

        /// <summary>This process's id; a test can say otherwise.</summary>
        public static Func<int> Pid = () => System.Diagnostics.Process.GetCurrentProcess().Id;

        /// <summary>A deploy some other program is running now: null when there is none, when
        /// the program that wrote the mark is gone, or when the mark is older than a quarter of
        /// an hour (it was killed before it could take it away).</summary>
        public static Busy Running()
        {
            Dictionary<string, object> d;
            try { d = Store.Load("deploying.json"); }
            catch (FormatException) { return null; }
            if (d == null || d.Count == 0) return null;
            var pid = (int)Json.Long(d, "pid");
            if (pid == 0 || pid == Pid()) return null;
            var since = new DateTime(Json.Long(d, "since"), DateTimeKind.Utc);
            if (DateTime.UtcNow - since > TimeSpan.FromMinutes(15)) return null;
            try { System.Diagnostics.Process.GetProcessById(pid); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            return new Busy { Pid = pid, Since = since, Game = Json.Str(d, "game") };
        }

        /// <summary>Marks the game folder as being written to, until it is disposed.</summary>
        class Marked : IDisposable
        {
            public Marked(string game)
            {
                try
                {
                    Store.Save("deploying.json", new Dictionary<string, object>
                    {
                        ["pid"] = (long)Pid(), ["since"] = DateTime.UtcNow.Ticks, ["game"] = game,
                    });
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }

            public void Dispose()
            {
                try { File.Delete(Store.PathOf("deploying.json")); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
        }

        /// <summary>Refuses while another program is writing to the game folder: two at once
        /// would each undo the other.</summary>
        static void NotWhileBusy()
        {
            var busy = Running();
            if (busy != null)
                throw new ModError("another program is deploying (process " + busy.Pid + "): wait for it to finish", "deploying");
        }

        public static int Purge(string game)
        {
            var native = NativeConfigs.Prepare(Library.Load(), game, true);
            var data = Data(game);
            var files = Files(data).Where(f => Managed.IsMatch(f)).ToList();
            files.Sort(string.CompareOrdinal);
            if ((files.Count > 0 || native.Changes > 0 || native.LegacyChanged) && GameRunning()) throw new ModError("the game is running: close it first", "game_running");
            NotWhileBusy();
            using (new Marked(game))
            {
                native.Apply();
                foreach (var f in files) File.Delete(Paths.Join(data, f));
            }
            var before = Store.Load("deployed.json");
            Store.Save("deployed.json", new Dictionary<string, object>
            {
                ["game_dir"] = game, ["saved_bytes"] = Json.Long(before, "saved_bytes"), ["deploys"] = Json.Long(before, "deploys"),
                ["files"] = new Dictionary<string, object>(),
            });
            return files.Count;
        }

        // ------------------------------------------------------------ resources and conflicts

        /// <summary>One resource's row in a patch archive: where its bytes are and how many,
        /// in the archive itself, in its .stream and in its .gpu_resources.</summary>
        public struct Res
        {
            public ulong Type, Id, Off, StreamOff, GpuOff;
            public uint Size, StreamSize, GpuSize;
            public (ulong, ulong) Key => (Type, Id);
        }

        /// <summary>Every resource in a patch archive: a 72-byte header with the type and file
        /// counts at 4 and 8, 32-byte type rows, then 80-byte file rows — the id and the type
        /// first, then the three offsets (archive, stream, gpu), two numbers this reader does
        /// not use, then the three sizes. Read on every mod in the library 2026-10-24: each
        /// row's offset and size stay inside its file.</summary>
        public static List<Res> Rows(string path)
        {
            var out_ = new List<Res>();
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var head = new byte[0x48];
                if (Fill(f, head) < 0x48 || BitConverter.ToUInt32(head, 0) != 0xF0000011) return out_;
                uint types = BitConverter.ToUInt32(head, 4), count = BitConverter.ToUInt32(head, 8);
                if (types > 4096 || count > 200000) return out_;
                f.Seek(0x48 + types * 32L, SeekOrigin.Begin);
                var rows = new byte[count * 80];
                int n = Fill(f, rows);
                for (int i = 0; i + 80 <= n; i += 80)
                    out_.Add(new Res
                    {
                        Id = BitConverter.ToUInt64(rows, i),
                        Type = BitConverter.ToUInt64(rows, i + 8),
                        Off = BitConverter.ToUInt64(rows, i + 0x10),
                        StreamOff = BitConverter.ToUInt64(rows, i + 0x18),
                        GpuOff = BitConverter.ToUInt64(rows, i + 0x20),
                        Size = BitConverter.ToUInt32(rows, i + 0x38),
                        StreamSize = BitConverter.ToUInt32(rows, i + 0x3C),
                        GpuSize = BitConverter.ToUInt32(rows, i + 0x40),
                    });
            }
            return out_;
        }

        /// <summary>(type, id) of every resource in a patch archive.</summary>
        public static List<(ulong Type, ulong Id)> Resources(string path) =>
            Rows(path).Select(r => r.Key).ToList();

        /// <summary>Stingray's resource type of a Lua script (murmur64 of "lua").</summary>
        public const ulong LuaType = 0xA14E8DFA2CD117E2;

        /// <summary>The scripts a mod replaces when it brings its own loader: "boot", the script
        /// the engine starts from (murmur64 of "boot"), and the script the community's shared
        /// loaders ship. Read over the user's 75 mods 2026-10-24: only Boot Mod Loader, Bingus
        /// Shared Loader, MDL and two mods that carry a loader of their own hold one of them.
        /// Two mods holding the same one cannot both have their way: the last to load wins.</summary>
        public static readonly ulong[] LoaderScripts = { 0xF476DF93691895FA, 0x7251FDD9BB62480A };

        /// <summary>Whether a patch archive holds any of these Lua resources. The row table is
        /// read in pieces and the reading stops at the first one; no resource itself is read.</summary>
        public static bool HoldsLua(string path, IReadOnlyList<ulong> ids)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024))
            {
                var head = new byte[0x48];
                if (Fill(f, head) < 0x48 || BitConverter.ToUInt32(head, 0) != 0xF0000011) return false;
                uint types = BitConverter.ToUInt32(head, 4), count = BitConverter.ToUInt32(head, 8);
                if (types > 4096 || count > 200000) return false;
                f.Seek(0x48 + types * 32L, SeekOrigin.Begin);
                var piece = new byte[80 * 256];
                for (long left = count; left > 0; )
                {
                    int want = (int)Math.Min(left, 256) * 80;
                    int n = Fill(f, piece, want);
                    for (int i = 0; i + 80 <= n; i += 80)
                        if (BitConverter.ToUInt64(piece, i + 8) == LuaType)
                        {
                            var id = BitConverter.ToUInt64(piece, i);
                            for (int k = 0; k < ids.Count; k++) if (ids[k] == id) return true;
                        }
                    if (n < want) break;
                    left -= n / 80;
                }
            }
            return false;
        }

        /// <summary>The resource types a patch archive holds, from its type table alone (32-byte
        /// rows after the 72-byte header, the type at +8): no file row is read. Checked on
        /// 68 mods 2026-10-24: the 40 with scripts have LuaType, sound / texture / UI mods not.</summary>
        public static HashSet<ulong> Types(string path)
        {
            var out_ = new HashSet<ulong>();
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
            {
                var head = new byte[0x48];
                if (Fill(f, head) < 0x48 || BitConverter.ToUInt32(head, 0) != 0xF0000011) return out_;
                uint types = BitConverter.ToUInt32(head, 4);
                if (types > 4096) return out_;
                var rows = new byte[types * 32];
                int n = Fill(f, rows);
                for (int i = 0; i + 32 <= n; i += 32) out_.Add(BitConverter.ToUInt64(rows, i + 8));
            }
            return out_;
        }

        /// <summary>A resource more than one mod ships, with its mods in load order (the last
        /// one wins). Same: all of them ship the very same bytes, so whichever wins the game
        /// gets the same resource — worth saying, but not a conflict (the user, 2026-10-24).</summary>
        public class Clash
        {
            public ulong Type, Id;
            public List<string> Mods = new List<string>();
            public bool Same;
        }

        /// <summary>Resources that more than one mod ships, in the order they were first seen.
        /// Only those resources' bytes are read, nothing else: the archives are read once for
        /// their rows, then once more for the few resources two mods have in common.</summary>
        public static List<Clash> Clashes(List<Wanted> wanted)
        {
            var seen = new Dictionary<(ulong, ulong), List<string>>();
            var from = new Dictionary<(ulong, ulong), List<string>>();     // the archives it is in
            var order = new List<(ulong, ulong)>();
            foreach (var w in wanted)
            {
                if (!Patch.IsMatch(w.Name)) continue;
                foreach (var key in Resources(w.Src).Distinct())
                {
                    if (!seen.TryGetValue(key, out var mods))
                    {
                        seen[key] = mods = new List<string>();
                        from[key] = new List<string>();
                        order.Add(key);
                    }
                    if (!mods.Contains(w.Mod)) mods.Add(w.Mod);
                    from[key].Add(w.Src);
                }
            }
            var shared = order.Where(k => seen[k].Count > 1).ToList();
            var same = Identical(shared, from);
            return shared.Select(k => new Clash { Type = k.Item1, Id = k.Item2, Mods = seen[k], Same = same.Contains(k) }).ToList();
        }

        /// <summary>Of the resources two mods have in common, the ones whose bytes are the same
        /// in every archive that ships them. A resource whose bytes cannot all be read is left
        /// out: unread bytes are never called the same.</summary>
        static HashSet<(ulong, ulong)> Identical(List<(ulong, ulong)> shared, Dictionary<(ulong, ulong), List<string>> from)
        {
            var out_ = new HashSet<(ulong, ulong)>();
            if (shared.Count == 0) return out_;
            var want = new HashSet<(ulong, ulong)>(shared);
            var hashes = new Dictionary<(ulong, ulong), List<string>>();
            foreach (var archive in shared.SelectMany(k => from[k]).Distinct())
                foreach (var kv in Hashes(archive, want))
                {
                    if (!hashes.TryGetValue(kv.Key, out var list)) hashes[kv.Key] = list = new List<string>();
                    list.Add(kv.Value);
                }
            foreach (var k in shared)
                if (hashes.TryGetValue(k, out var list) && list.Count == from[k].Count && list.All(h => h == list[0]))
                    out_.Add(k);
            return out_;
        }

        /// <summary>A hash of each wanted resource's bytes in one patch archive: its slice of the
        /// archive, of the .stream and of the .gpu_resources beside it. A resource whose bytes do
        /// not fit in those files is left out.</summary>
        static Dictionary<(ulong, ulong), string> Hashes(string archive, HashSet<(ulong, ulong)> want)
        {
            var out_ = new Dictionary<(ulong, ulong), string>();
            List<Res> mine;
            try { mine = Rows(archive).Where(r => want.Contains(r.Key)).ToList(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return out_; }
            if (mine.Count == 0) return out_;
            try
            {
                using (var a = Reading(archive))
                using (var s = Reading(archive + ".stream"))
                using (var g = Reading(archive + ".gpu_resources"))
                    foreach (var r in mine)
                    {
                        using (var h = System.Security.Cryptography.SHA256.Create())
                        {
                            if (Feed(h, a, r.Off, r.Size) && Feed(h, s, r.StreamOff, r.StreamSize) && Feed(h, g, r.GpuOff, r.GpuSize))
                            {
                                h.TransformFinalBlock(new byte[0], 0, 0);
                                out_[r.Key] = BitConverter.ToString(h.Hash);
                            }
                        }
                    }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return out_;
        }

        static FileStream Reading(string path) =>
            File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024) : null;

        /// <summary>One slice of a file into the hash; false when the file does not hold it.</summary>
        static bool Feed(System.Security.Cryptography.HashAlgorithm h, FileStream f, ulong off, uint size)
        {
            if (size == 0) return true;
            if (f == null || (ulong)f.Length < off + size) return false;
            f.Seek((long)off, SeekOrigin.Begin);
            var buf = new byte[64 * 1024];
            long left = size;
            while (left > 0)
            {
                int n = f.Read(buf, 0, (int)Math.Min(left, buf.Length));
                if (n <= 0) return false;
                h.TransformBlock(buf, 0, n, null, 0);
                left -= n;
            }
            return true;
        }
    }

    /// <summary>Paths written the way Python's os.path writes them on Windows, so the files
    /// it recorded still match.</summary>
    public static class Paths
    {
        /// <summary>os.path.join for two parts.</summary>
        public static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (b.Length > 0 && (b[0] == '\\' || b[0] == '/')) return Drive(a) + b;
            if (b.Length >= 2 && b[1] == ':') return b;
            char last = a[a.Length - 1];
            return last == '\\' || last == '/' || (a.Length == 2 && a[1] == ':') ? a + b : a + "\\" + b;
        }

        static string Drive(string p)
        {
            if (p.Length >= 2 && p[1] == ':') return p.Substring(0, 2);
            var unc = Unc(p.Replace('/', '\\'));
            return unc ?? "";
        }

        /// <summary>\\server\share of a UNC path, else null.</summary>
        static string Unc(string p)
        {
            if (p.Length < 3 || p[0] != '\\' || p[1] != '\\' || p[2] == '\\') return null;
            int a = p.IndexOf('\\', 2);
            if (a < 0) return null;
            int b = p.IndexOf('\\', a + 1);
            if (b == a + 1) return null;
            return b < 0 ? p : p.Substring(0, b);
        }

        /// <summary>os.path.normpath: / to \, no empty, . or resolvable .. parts, no trailing \.</summary>
        public static string Norm(string path)
        {
            if (path.StartsWith(@"\\.\") || path.StartsWith(@"\\?\")) return path;
            path = path.Replace('/', '\\');
            string prefix;
            if (path.Length >= 2 && path[1] == ':') prefix = path.Substring(0, 2);
            else prefix = Unc(path) ?? "";
            path = path.Substring(prefix.Length);
            if (path.StartsWith("\\"))
            {
                prefix += "\\";
                path = path.TrimStart('\\');
            }
            var comps = path.Split('\\').ToList();
            int i = 0;
            while (i < comps.Count)
            {
                if (comps[i].Length == 0 || comps[i] == ".") comps.RemoveAt(i);
                else if (comps[i] == "..")
                {
                    if (i > 0 && comps[i - 1] != "..") { comps.RemoveRange(i - 1, 2); i--; }
                    else if (i == 0 && prefix.EndsWith("\\")) comps.RemoveAt(i);
                    else i++;
                }
                else i++;
            }
            if (prefix.Length == 0 && comps.Count == 0) comps.Add(".");
            return prefix + string.Join("\\", comps);
        }

        /// <summary>os.path.abspath.</summary>
        public static string Abs(string path) => Norm(System.IO.Path.IsPathRooted(path) && Drive(path).Length > 0 ? path : System.IO.Path.GetFullPath(path));
    }
}
