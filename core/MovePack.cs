using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hd2Mod
{
    /// <summary>A whole setup moved to another computer in one file (the user, 2026-11-25: "一键搬迁
    /// ... 不要再找路径、复制粘贴"): the mods in the library, what the player chose (order, on/off,
    /// options, groups, labels, profiles, targets), the plugins, the mods' pictures, the window's
    /// settings and the mods' own settings files in the game's folder. Restored on the other side
    /// into a mod folder on the game's drive, every path made over to it; a deploy then fills the
    /// game folder.
    ///
    /// Left out on purpose: the deploy record and every cache (they describe this computer's game
    /// folder and are made again), the Nexus Mods sign-in (Windows encrypts it for this user on
    /// this computer: it cannot be read elsewhere), the versions kept for a rollback, and the
    /// plugins' own data folders (a plugin finds its things again on the new computer).
    ///
    /// The file: each part starts with "HD2QDMV1", the package's id (8 bytes) and the part's
    /// number (2 bytes); the parts' data, end to end, is a run of records:
    /// - 'F' a file: its path in the package (2-byte length, UTF-8), size, time (UTC ticks);
    /// - 'C' a piece of the file before it: method (0 stored, 1 deflate), its length before and
    ///   after, CRC-32, the bytes;
    /// - 'E' the end: how many files and bytes.
    /// The first file is move.json (what is in the package). Every piece of up to 4 MB is
    /// compressed on its own, so all the processor's cores compress at once (the user's library
    /// 2026-11-25: 4,694 files, 19.5 GB; deflate's fastest level kept 28% of a sample at
    /// 124 MB/s a core). A part ends before 4 GB, FAT32's limit for a file, the format of most
    /// USB sticks; the next is "NAME.2", "NAME.3", ...</summary>
    public static class MovePack
    {
        public const string Extension = ".hd2qdmove";
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("HD2QDMV1");
        const int Format = 1;
        const int Piece = 4 << 20;

        /// <summary>Where a part ends: below FAT32's 4 GiB, with room to spare. Tests make it small.</summary>
        public static long PartLimit = (4L << 30) - (16L << 20);

        /// <summary>Tests: split at PartLimit on any drive. Otherwise only a FAT32 drive gets parts:
        /// on NTFS and exFAT the package is one file, as the player asked.</summary>
        public static bool SplitAnywhere;

        /// <summary>Whether a folder's drive is FAT32 (or FAT); a drive that cannot be asked (a
        /// network share) is taken as one, so the parts fit wherever they are copied.</summary>
        static bool Fat(string dir)
        {
            try
            {
                var root = Path.GetPathRoot(dir);
                if (root.StartsWith(@"\\")) return true;
                var f = new DriveInfo(root).DriveFormat ?? "";
                return f.StartsWith("FAT", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is UnauthorizedAccessException) { return true; }
        }

        /// <summary>Tests: the folder of the mods' own settings files, instead of the game's.</summary>
        public static string GameSettingsOverride;

        static string GameSettings => GameSettingsOverride ?? LuaMods.GameFolder;

        /// <summary>What is said of a package before it is restored.</summary>
        public sealed class Info
        {
            public int Mods, Files, Parts;
            public long ModBytes, Bytes, Created;
            public string Program;
            /// <summary>The package's folder names and the paths they had on the computer it came from.</summary>
            public Dictionary<string, string> Folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public sealed class Packed
        {
            public int Mods, Files;
            public long Bytes, Size;
            public double Seconds;
            public List<string> Parts = new List<string>();
            /// <summary>Mods whose folder was not found: in the list, without their files.</summary>
            public List<string> Missing = new List<string>();
        }

        public sealed class Restored
        {
            public int Mods, Files, Settings;
            public long Bytes;
            public double Seconds;
            public string Into, Game;
            /// <summary>The package's plugins, held in Held and not added: a plugin runs as this
            /// program does, and a package may come from anyone. The window adds them as it adds
            /// any plugin file (an official release at once, by its SHA-256; another one asked).</summary>
            public List<string> Plugins = new List<string>();
        }

        /// <summary>A file name for a package made today, in the program's language.</summary>
        public static string DefaultName(string word) => word + " " + DateTime.Now.ToString("yyyy-MM-dd") + Extension;

        // ------------------------------------------------------------------ packing

        sealed class Entry
        {
            public string Name, Src;
            public byte[] Data;
            public long Size, Time;
        }

        /// <summary>The library and its mods into one package at file (and file.2, ... when it
        /// passes PartLimit). progress: bytes read so far, and in all. Written under .part names
        /// and renamed when whole: a package cut short leaves nothing behind.</summary>
        public static Packed Export(string file, Action<long, long> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            file = Paths.Abs(file);
            if (!file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) file += Extension;
            var lib = Library.Load();
            var result = new Packed { Mods = lib.Mods.Count };
            var entries = new List<Entry>();
            var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // package name -> path here
            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);     // path here -> package name
            long modBytes = 0;

            foreach (var m in lib.Mods)
            {
                var p = Paths.AbsOrEmpty(m.Path).TrimEnd('\\');
                if (p.Length == 0) continue;                               // a mod a plugin keeps whole: no files of ours
                if (named.ContainsKey(p)) continue;
                if (!Directory.Exists(p)) { result.Missing.Add(m.Name); continue; }
                if (Path.GetPathRoot(p).TrimEnd('\\').Equals(p, StringComparison.OrdinalIgnoreCase))
                    throw new ModError("a mod's folder is a whole drive: " + p, "move_drive_root", p);
                var name = Unique(Path.GetFileName(p), folders);
                folders[name] = p;
                named[p] = name;
                foreach (var (full, rel) in Walk(p))
                {
                    var fi = new FileInfo(full);
                    entries.Add(new Entry { Name = "mods/" + name + "/" + rel, Src = full, Size = fi.Length, Time = fi.LastWriteTimeUtc.Ticks });
                    modBytes += fi.Length;
                }
            }

            // what the player chose, and the window's settings
            foreach (var n in new[] { "library.json", "config.json", "gui.json" })
                AddFile(entries, Store.PathOf(n), "settings/" + n);
            var plugins = Path.Combine(Store.Home, "plugins");
            if (Directory.Exists(plugins))
                foreach (var f in Directory.GetFiles(plugins, "HD2QD.*.dll")) AddFile(entries, f, "settings/plugins/" + Path.GetFileName(f));
            var pictures = Path.Combine(Store.Home, "pictures");
            if (Directory.Exists(pictures))
                foreach (var f in Directory.GetFiles(pictures)) AddFile(entries, f, "settings/pictures/" + Path.GetFileName(f));
            // the mods' own settings, where the game keeps its logs
            if (Directory.Exists(GameSettings))
                foreach (var f in Directory.GetFiles(GameSettings).Where(f => IsModSetting(Path.GetFileName(f))))
                    AddFile(entries, f, "game/" + Path.GetFileName(f));

            long all = entries.Sum(e => e.Size);
            var manifest = new Dictionary<string, object>
            {
                ["format"] = (long)Format,
                ["program"] = StateDoc.Version,
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["mods"] = (long)lib.Mods.Count,
                ["mod_bytes"] = modBytes,
                ["files"] = (long)entries.Count + 1,
                ["bytes"] = all,
                ["folders"] = folders.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            };
            var head = Encoding.UTF8.GetBytes(Json.Write(manifest, true));
            entries.Insert(0, new Entry { Name = "move.json", Data = head, Size = head.Length, Time = DateTime.UtcNow.Ticks });
            all += head.Length;

            var dir = Path.GetDirectoryName(file);
            Directory.CreateDirectory(dir);
            var writer = new PartWriter(file, SplitAnywhere || Fat(dir) ? PartLimit : long.MaxValue);
            try
            {
                long done = 0;
                progress?.Invoke(0, all);
                Pipeline(Pieces(entries, cancel), Pack, batch =>
                {
                    foreach (var it in batch)
                    {
                        if (it.Header != null) WriteHeader(writer, it.Header);
                        if (it.HasPiece)
                        {
                            WritePiece(writer, it);
                            done += it.RawLen;
                        }
                        Give(it);
                    }
                    progress?.Invoke(done, all);
                }, cancel);
                var end = new byte[1 + 4 + 8];
                end[0] = (byte)'E';
                BitConverter.GetBytes(entries.Count).CopyTo(end, 1);
                BitConverter.GetBytes(all).CopyTo(end, 5);
                writer.Write(end, 0, end.Length);
                writer.Finish();
            }
            catch
            {
                writer.Abandon();
                throw;
            }
            result.Parts.AddRange(writer.Finals);
            result.Files = entries.Count;
            result.Bytes = all;
            result.Size = writer.Finals.Sum(p => new FileInfo(p).Length);
            result.Seconds = clock.Elapsed.TotalSeconds;
            return result;
        }

        /// <summary>The settings files of the mods, by their names: the settings window's
        /// NAME_settings.txt, profiles and lists, .ini files, and Chef's per-mod data. Not the
        /// game's own files, logs, dumps or what a probe left there.</summary>
        public static bool IsModSetting(string name)
        {
            var n = name.ToLowerInvariant();
            return n.EndsWith("_settings.txt") || n.EndsWith("_profiles.txt") || n.EndsWith("_lists.txt") || n.EndsWith(".ini")
                   || (n.StartsWith("chef_data_") && n.EndsWith(".lua"));
        }

        static void AddFile(List<Entry> entries, string path, string name)
        {
            if (!File.Exists(path)) return;
            var fi = new FileInfo(path);
            entries.Add(new Entry { Name = name, Src = path, Size = fi.Length, Time = fi.LastWriteTimeUtc.Ticks });
        }

        static string Unique(string name, IDictionary<string, string> taken)
        {
            if (string.IsNullOrEmpty(name)) name = "mod";
            var n = name;
            for (int k = 2; taken.ContainsKey(n); k++) n = name + " (" + k + ")";
            return n;
        }

        /// <summary>Every file under a folder, with its path below it ("a/b.txt"); folders that are
        /// links elsewhere are not followed (a loop, or a whole other drive).</summary>
        static IEnumerable<(string Full, string Rel)> Walk(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                foreach (var f in Directory.GetFiles(d).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    yield return (f, f.Substring(root.Length + 1).Replace('\\', '/'));
                foreach (var s in Directory.GetDirectories(d).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                    if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) == 0) stack.Push(s);
            }
        }

        // ------------------------------------------------------------------ pieces, packed and unpacked

        sealed class Item
        {
            public Entry Header;
            public bool HasPiece, Deflated;
            public byte[] Raw, Out;
            public int RawLen, OutLen;
            public uint Crc;
        }

        static readonly ConcurrentBag<byte[]> pool = new ConcurrentBag<byte[]>();
        const int Big = Piece + Piece / 64 + 4096;

        static byte[] Take(int size)
        {
            if (size > Piece / 2 && pool.TryTake(out var b)) return b;
            return new byte[size > Piece / 2 ? Big : size];
        }

        static void Give(Item it)
        {
            foreach (var b in new[] { it.Raw, it.Out })
                if (b != null && b.Length == Big && pool.Count < 64) pool.Add(b);
            it.Raw = it.Out = null;
        }

        /// <summary>The files read in pieces of up to 4 MB, in order; each file's header rides on
        /// its first piece (a file of no bytes has a header alone).</summary>
        static IEnumerable<Item> Pieces(List<Entry> entries, CancellationToken cancel)
        {
            foreach (var e in entries)
            {
                cancel.ThrowIfCancellationRequested();
                if (e.Size == 0) { yield return new Item { Header = e }; continue; }
                if (e.Data != null)
                {
                    var raw = Take(e.Data.Length);
                    Buffer.BlockCopy(e.Data, 0, raw, 0, e.Data.Length);
                    yield return new Item { Header = e, HasPiece = true, Raw = raw, RawLen = e.Data.Length };
                    continue;
                }
                FileStream s;
                try { s = new FileStream(e.Src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }
                catch (Exception x) when (x is IOException || x is UnauthorizedAccessException)
                {
                    throw new ModError("cannot read " + e.Src + ": " + x.Message, "move_read", e.Src);
                }
                using (s)
                {
                    long left = e.Size;
                    bool first = true;
                    while (left > 0)
                    {
                        int want = (int)Math.Min(left, Piece);
                        var raw = Take(want);
                        int got = 0;
                        while (got < want)
                        {
                            int n = s.Read(raw, got, want - got);
                            if (n == 0) throw new ModError("a file changed while it was packed: " + e.Src, "move_changed", e.Src);
                            got += n;
                        }
                        left -= want;
                        yield return new Item { Header = first ? e : null, HasPiece = true, Raw = raw, RawLen = want };
                        first = false;
                    }
                }
            }
        }

        static void Pack(Item it)
        {
            if (!it.HasPiece) return;
            it.Crc = Crc32.Of(it.Raw, 0, it.RawLen);
            it.Out = null;
            if (it.RawLen < 128) return;
            var into = Take(it.RawLen + it.RawLen / 64 + 4096);
            try
            {
                var ms = new MemoryStream(into, 0, into.Length, true);
                using (var d = new DeflateStream(ms, CompressionLevel.Fastest, true)) d.Write(it.Raw, 0, it.RawLen);
                if (ms.Position < it.RawLen) { it.Out = into; it.OutLen = (int)ms.Position; it.Deflated = true; return; }
            }
            catch (NotSupportedException) { }       // more than it came in: stored as it is
            if (into.Length == Big && pool.Count < 64) pool.Add(into);
        }

        static void Unpack(Item it)
        {
            if (!it.HasPiece) return;
            if (it.Deflated)
            {
                var raw = Take(it.RawLen);
                try
                {
                    using (var d = new DeflateStream(new MemoryStream(it.Out, 0, it.OutLen), CompressionMode.Decompress))
                    {
                        int got = 0;
                        while (got < it.RawLen)
                        {
                            int n = d.Read(raw, got, it.RawLen - got);
                            if (n == 0) break;
                            got += n;
                        }
                        if (got != it.RawLen) throw new ModError("the package is damaged (a piece is short)", "move_damaged");
                    }
                }
                catch (InvalidDataException) { throw new ModError("the package is damaged (a piece cannot be unpacked)", "move_damaged"); }
                if (it.Out.Length == Big && pool.Count < 64) pool.Add(it.Out);
                it.Out = null;
                it.Raw = raw;
            }
            else
            {
                it.Raw = it.Out;
                it.Out = null;
            }
            if (Crc32.Of(it.Raw, 0, it.RawLen) != it.Crc) throw new ModError("the package is damaged (a checksum does not match)", "move_damaged");
        }

        /// <summary>Items in batches of about a piece a core, worked on every core while the next
        /// batch is read and the one before is handed on (written), in order. At most three
        /// batches are held: some 300 MB with 12 cores.</summary>
        static void Pipeline(IEnumerable<Item> items, Action<Item> work, Action<List<Item>> hand, CancellationToken cancel)
        {
            int cores = Math.Max(1, Environment.ProcessorCount);
            long budget = (long)Piece * cores;
            var options = new ParallelOptions { MaxDegreeOfParallelism = cores, CancellationToken = cancel };
            Task Start(List<Item> b) => b.Count == 0 ? Task.CompletedTask : Task.Run(() => Parallel.ForEach(b, options, work));
            void Wait(Task t)
            {
                try { t.Wait(); }
                catch (AggregateException x) { throw Unwrap(x); }
            }
            using (var e = items.GetEnumerator())
            {
                List<Item> Next()
                {
                    var b = new List<Item>();
                    long bytes = 0;
                    while (bytes < budget && b.Count < 4096 && e.MoveNext())
                    {
                        b.Add(e.Current);
                        bytes += Math.Max(e.Current.RawLen, e.Current.OutLen) + 256;
                    }
                    return b;
                }
                var batch = Next();
                var working = Start(batch);
                while (batch.Count > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    List<Item> next;
                    try { next = Next(); }
                    catch { try { working.Wait(); } catch (AggregateException) { } throw; }
                    Wait(working);
                    var nextWorking = Start(next);
                    try { hand(batch); }
                    catch { try { nextWorking.Wait(); } catch (AggregateException) { } throw; }
                    batch = next;
                    working = nextWorking;
                }
            }
        }

        static Exception Unwrap(AggregateException x)
        {
            var f = x.Flatten().InnerExceptions;
            return f.FirstOrDefault(i => i is ModError) ?? f.FirstOrDefault(i => i is OperationCanceledException) ?? f.FirstOrDefault() ?? x;
        }

        static void WriteHeader(Stream w, Entry e)
        {
            var name = Encoding.UTF8.GetBytes(e.Name);
            if (name.Length > ushort.MaxValue) throw new ModError("a path is too long to pack: " + e.Name, "move_path");
            var b = new byte[1 + 2 + name.Length + 16 + 4];
            b[0] = (byte)'F';
            BitConverter.GetBytes((ushort)name.Length).CopyTo(b, 1);
            name.CopyTo(b, 3);
            BitConverter.GetBytes(e.Size).CopyTo(b, 3 + name.Length);
            BitConverter.GetBytes(e.Time).CopyTo(b, 11 + name.Length);
            // the header's own checksum: a name changed on the way would put a file somewhere else
            BitConverter.GetBytes(Crc32.Of(b, 0, b.Length - 4)).CopyTo(b, b.Length - 4);
            w.Write(b, 0, b.Length);
        }

        static void WritePiece(Stream w, Item it)
        {
            var b = new byte[1 + 1 + 4 + 4 + 4];
            b[0] = (byte)'C';
            b[1] = (byte)(it.Deflated ? 1 : 0);
            BitConverter.GetBytes(it.RawLen).CopyTo(b, 2);
            BitConverter.GetBytes(it.Deflated ? it.OutLen : it.RawLen).CopyTo(b, 6);
            BitConverter.GetBytes(it.Crc).CopyTo(b, 10);
            w.Write(b, 0, b.Length);
            if (it.Deflated) w.Write(it.Out, 0, it.OutLen);
            else w.Write(it.Raw, 0, it.RawLen);
        }

        // ------------------------------------------------------------------ reading

        /// <summary>The records of a package, in order: each file's header with its first piece,
        /// still packed. Reads whole records only; throws on anything that is not one.</summary>
        static IEnumerable<Item> Records(Stream s, Action<long> read, Action<int, long> end)
        {
            var r = new BinaryReader(s, Encoding.UTF8);
            Entry pending = null;
            long left = 0;
            while (true)
            {
                int kind = r.ReadByte();
                {
                    switch (kind)
                    {
                        case 'F':
                        {
                            if (left > 0) throw Damaged();
                            int n = r.ReadUInt16();
                            var head = new byte[1 + 2 + n + 16];
                            head[0] = (byte)'F';
                            BitConverter.GetBytes((ushort)n).CopyTo(head, 1);
                            var rest = r.ReadBytes(n + 16);
                            if (rest.Length != n + 16) throw Damaged();
                            rest.CopyTo(head, 3);
                            if (r.ReadUInt32() != Crc32.Of(head, 0, head.Length)) throw Damaged();
                            var e = new Entry { Name = Encoding.UTF8.GetString(head, 3, n), Size = BitConverter.ToInt64(head, 3 + n), Time = BitConverter.ToInt64(head, 11 + n) };
                            if (e.Size < 0) throw Damaged();
                            if (e.Size == 0) { yield return new Item { Header = e }; continue; }
                            pending = e;
                            left = e.Size;
                            continue;
                        }
                        case 'C':
                        {
                            bool deflated = r.ReadByte() == 1;
                            int raw = r.ReadInt32(), stored = r.ReadInt32();
                            uint crc = r.ReadUInt32();
                            if (raw <= 0 || raw > Piece || stored <= 0 || stored > Big || raw > left) throw Damaged();
                            var buf = Take(stored);
                            int got = 0;
                            while (got < stored)
                            {
                                int k = s.Read(buf, got, stored - got);
                                if (k == 0) throw new EndOfStreamException();
                                got += k;
                            }
                            read?.Invoke(raw);
                            left -= raw;
                            var it = new Item { Header = pending, HasPiece = true, Deflated = deflated, Out = buf, OutLen = stored, RawLen = raw, Crc = crc };
                            pending = null;
                            yield return it;
                            continue;
                        }
                        case 'E':
                            if (left > 0) throw Damaged();
                            end?.Invoke(r.ReadInt32(), r.ReadInt64());
                            yield break;
                        default:
                            throw Damaged();
                    }
                }
            }
        }

        static ModError Damaged() => new ModError("the package is damaged (an unknown record)", "move_damaged");

        /// <summary>What a package holds, from its first file alone.</summary>
        public static Info Read(string file)
        {
            using (var s = new PartReader(Paths.Abs(file)))
            {
                foreach (var it in Records(s, null, null))
                {
                    if (it.Header?.Name != "move.json" || !it.HasPiece) break;
                    Unpack(it);
                    var d = Json.Parse(Encoding.UTF8.GetString(it.Raw, 0, it.RawLen)) as Dictionary<string, object>;
                    if (d == null) break;
                    if (Json.Long(d, "format") > Format)
                        throw new ModError("this package was made by a newer version (" + Json.Str(d, "program") + "): update this program first", "move_newer", Json.Str(d, "program"));
                    var info = new Info
                    {
                        Mods = (int)Json.Long(d, "mods"), Files = (int)Json.Long(d, "files"), ModBytes = Json.Long(d, "mod_bytes"),
                        Bytes = Json.Long(d, "bytes"), Created = Json.Long(d, "created"), Program = Json.Str(d, "program"),
                        Parts = PartReader.Count(Paths.Abs(file)),
                    };
                    foreach (var kv in Json.Obj(d, "folders") ?? new Dictionary<string, object>())
                        if (kv.Value is string p) info.Folders[kv.Key] = p;
                    return info;
                }
            }
            throw new ModError("not a migration package of this program: " + file, "move_not_pack", file);
        }

        // ------------------------------------------------------------------ restoring

        /// <summary>A package put in place on this computer: its mods into a mod folder on the
        /// game's drive (into, else "HD2 Mods" there), its list and settings into the settings
        /// folder with every path made over, its plugins and the mods' settings files beside
        /// them. A library that has mods is replaced only with replace (its mods' folders stay on
        /// the disk; library.json.bak keeps the list). Nothing is changed in the library until
        /// every file is out and checked; a package that fails takes away the folders it made.
        /// The game folder is not touched: a deploy does that.</summary>
        public static Restored Restore(string file, string game = null, string into = null, bool replace = false,
                                       Action<long, long> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            file = Paths.Abs(file);
            var info = Read(file);
            var had = Library.Load();
            if (had.Mods.Count > 0 && !replace)
                throw new ModError("the library already has " + had.Mods.Count + " mods: --replace replaces it (their folders stay on the disk)",
                                   "move_not_empty", had.Mods.Count.ToString());
            var g = !string.IsNullOrEmpty(game) ? Ops.GameFolderOf(game) : Store.GameDir(null, false) ?? Ops.FindGame();
            if (g == null)
                throw new ModError(!string.IsNullOrEmpty(game) ? "not Helldivers 2's folder (it has no data folder): " + game
                                                               : "the game folder was not found: pass --game", "no_game");
            g = Paths.Abs(g);
            into = Paths.Abs(!string.IsNullOrEmpty(into) ? into : Path.Combine(Path.GetPathRoot(g), "HD2 Mods"));
            if (Path.GetPathRoot(into).TrimEnd('\\').Equals(into.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new ModError("a whole drive cannot be a mod folder: " + into, "drive_root", into);
            long free = new DriveInfo(Path.GetPathRoot(into)).AvailableFreeSpace;
            long need = info.ModBytes + (64L << 20);
            if (free < need)
                throw new ModError("not enough space on " + Path.GetPathRoot(into) + ": " + Mb(need) + " needed, " + Mb(free) + " free",
                                   "move_space", need + "|" + free + "|" + Path.GetPathRoot(into));

            // each of the package's folders gets a folder of its own, never one that is there already
            Directory.CreateDirectory(into);
            var where = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in info.Folders.Keys)
            {
                var n = name;
                for (int k = 2; taken.Contains(n) || Directory.Exists(Path.Combine(into, n)) || File.Exists(Path.Combine(into, n)); k++) n = name + " (" + k + ")";
                taken.Add(n);
                where[name] = Path.Combine(into, n);
            }

            var result = new Restored { Into = into, Game = g, Mods = info.Mods };
            var made = new List<string>();            // folders and files made, taken away again on a failure
            var kept = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);   // the settings, rewritten at the end
            var pluginDir = Held;
            if (Directory.Exists(pluginDir)) Directory.Delete(pluginDir, true);
            var pictureDir = Path.Combine(Store.Home, "pictures");
            var later = new List<(string Tmp, string Final)>();   // plugins and settings files, put in place once all is well

            FileStream outFile = null;
            MemoryStream outMemory = null;
            Entry current = null;
            long written = 0, done = 0;
            int files = 0;
            string OutPath(string name, out bool memory)
            {
                memory = false;
                if (name.Contains("..") || name.StartsWith("/") || name.Contains(":") || name.Contains("\\")) throw new ModError("the package names a path outside its folders: " + name, "move_damaged");
                if (name == "move.json") return null;
                if (name.StartsWith("settings/") && name.IndexOf('/', 9) < 0)
                {
                    var n = name.Substring(9);
                    if (n == "library.json" || n == "config.json" || n == "gui.json") { memory = true; return n; }
                    return null;
                }
                if (name.StartsWith("settings/plugins/")) return Staged(Path.Combine(pluginDir, name.Substring(17)), true);
                if (name.StartsWith("settings/pictures/")) return Staged(Path.Combine(pictureDir, name.Substring(18)), false);
                if (name.StartsWith("game/")) return Staged(Path.Combine(GameSettings, name.Substring(5)), false);
                if (name.StartsWith("mods/"))
                {
                    int slash = name.IndexOf('/', 5);
                    if (slash < 0) return null;
                    if (!where.TryGetValue(name.Substring(5, slash - 5), out var dir)) return null;
                    var path = Path.Combine(dir, name.Substring(slash + 1).Replace('/', '\\'));
                    if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); made.Add(dir); }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    return path;
                }
                return null;                          // something a newer version packs: left out
            }
            string Staged(string final, bool plugin)
            {
                if (plugin) result.Plugins.Add(final);
                Directory.CreateDirectory(Path.GetDirectoryName(final));
                var tmp = final + ".moving";
                later.Add((tmp, final));
                made.Add(tmp);
                return tmp;
            }
            void Close()
            {
                if (current == null) return;
                if (outFile != null)
                {
                    outFile.Dispose();
                    try { File.SetLastWriteTimeUtc(outFile.Name, new DateTime(current.Time, DateTimeKind.Utc)); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { }
                    outFile = null;
                }
                if (outMemory != null) { kept[KeyOf(current.Name)] = outMemory.ToArray(); outMemory = null; }
                files++;
                current = null;
            }
            void Open(Entry e)
            {
                Close();
                current = e;
                written = 0;
                var path = OutPath(e.Name, out bool memory);
                if (memory) outMemory = new MemoryStream();
                else if (path != null)
                {
                    if (path.StartsWith(into, StringComparison.OrdinalIgnoreCase)) result.Files++;
                    outFile = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                }
                if (e.Size == 0) Close();
            }

            int endFiles = -1;
            try
            {
                using (var s = new PartReader(file))
                {
                    progress?.Invoke(0, info.Bytes);
                    Pipeline(Records(s, null, (n, b) => endFiles = n), Unpack, batch =>
                    {
                        foreach (var it in batch)
                        {
                            if (it.Header != null) Open(it.Header);
                            if (it.HasPiece)
                            {
                                if (current == null) throw Damaged();
                                if (outFile != null) outFile.Write(it.Raw, 0, it.RawLen);
                                else outMemory?.Write(it.Raw, 0, it.RawLen);
                                written += it.RawLen;
                                done += it.RawLen;
                                if (written == current.Size) Close();
                            }
                            Give(it);
                        }
                        progress?.Invoke(done, info.Bytes);
                    }, cancel);
                }
                if (current != null || endFiles != files) throw new ModError("the package ends too soon: a part is missing or was cut short", "move_short");
                if (!kept.ContainsKey("library.json")) throw new ModError("the package has no library in it", "move_damaged");

                // everything is out and checked: the settings now
                foreach (var (tmp, final) in later)
                {
                    if (File.Exists(final)) File.Delete(final);
                    File.Move(tmp, final);
                    if (!final.StartsWith(pluginDir, StringComparison.OrdinalIgnoreCase) && !final.StartsWith(pictureDir, StringComparison.OrdinalIgnoreCase)) result.Settings++;
                }
                later.Clear();
                var olds = info.Folders.ToDictionary(kv => kv.Key, kv => kv.Value.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);
                string Moved(string p)
                {
                    if (string.IsNullOrEmpty(p)) return p;
                    var a = p.TrimEnd('\\');
                    foreach (var kv in olds)
                    {
                        if (!where.TryGetValue(kv.Key, out var now)) continue;
                        if (a.Equals(kv.Value, StringComparison.OrdinalIgnoreCase)) return now;
                        if (a.StartsWith(kv.Value + "\\", StringComparison.OrdinalIgnoreCase)) return now + a.Substring(kv.Value.Length);
                    }
                    return p;
                }
                var libDoc = Json.Parse(Encoding.UTF8.GetString(kept["library.json"])) as Dictionary<string, object>
                             ?? throw new ModError("the package's library cannot be read", "move_damaged");
                foreach (var m in Json.List(libDoc, "mods").OfType<Dictionary<string, object>>())
                {
                    if (Json.Str(m, "path") is string p) m["path"] = Moved(p);
                    if (Json.Str(m, "source") is string src && !src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) m["source"] = Moved(src);
                    m.Remove("previous");
                }
                var cfg = kept.TryGetValue("config.json", out var c) ? Json.Parse(Encoding.UTF8.GetString(c)) as Dictionary<string, object> : null;
                cfg = cfg ?? new Dictionary<string, object>();
                cfg["game_dir"] = g;
                cfg["mods_dir"] = into;
                cfg["library_folders"] = new List<object>();
                var gui = Store.Load("gui.json");
                if (kept.TryGetValue("gui.json", out var gb) && Json.Parse(Encoding.UTF8.GetString(gb)) is Dictionary<string, object> theirs)
                    foreach (var kv in theirs)
                        if (!ThisComputer.Contains(kv.Key)) gui[kv.Key] = kv.Value;

                Store.Save("config.json", cfg);
                Store.Save("gui.json", gui);
                Store.Save("library.json", libDoc);
                made.Clear();
            }
            catch
            {
                outFile?.Dispose();
                foreach (var m in made.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (Directory.Exists(m)) Directory.Delete(m, true);
                        else if (File.Exists(m)) File.Delete(m);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
                throw;
            }
            result.Bytes = done;
            result.Seconds = clock.Elapsed.TotalSeconds;
            return result;
        }

        /// <summary>Where a restored package's plugins wait to be added (never loaded from there).</summary>
        public static string Held => Path.Combine(Store.Home, "plugins_moved");

        /// <summary>The window's settings that belong to the computer they were made on: where the
        /// window stood, the drives the player chose to keep mods on, the last crash seen.</summary>
        static readonly HashSet<string> ThisComputer = new HashSet<string> { "window", "drive_ok", "list_offset", "crash_seen" };

        static string KeyOf(string name) => name.Substring(name.LastIndexOf('/') + 1);

        static string Mb(long b) => (b >> 20) + " MB";

        // ------------------------------------------------------------------ parts

        /// <summary>Writes the package's data across parts of at most limit bytes, each under
        /// a .part name until Finish renames them.</summary>
        sealed class PartWriter : Stream
        {
            readonly string first;
            readonly byte[] id = new byte[8];
            readonly List<string> temps = new List<string>();
            public readonly List<string> Finals = new List<string>();
            FileStream now;
            long room;

            readonly long limit;

            public PartWriter(string file, long limit)
            {
                first = file;
                this.limit = limit;
                new Random().NextBytes(id);
            }

            public static string NameOf(string first, int k) => k == 1 ? first : first + "." + k;

            void Next()
            {
                now?.Dispose();
                int k = temps.Count + 1;
                var tmp = NameOf(first, k) + ".part";
                temps.Add(tmp);
                now = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                now.Write(Magic, 0, Magic.Length);
                now.Write(id, 0, id.Length);
                now.Write(BitConverter.GetBytes((ushort)k), 0, 2);
                room = limit - 18;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                while (count > 0)
                {
                    if (now == null || room == 0) Next();
                    int n = (int)Math.Min(count, room);
                    try { now.Write(buffer, offset, n); }
                    catch (IOException e) when ((e.HResult & 0xFFFF) == 112 || (e.HResult & 0xFFFF) == 39)
                    {
                        throw new ModError("the drive is full: " + Path.GetPathRoot(first), "move_disk_full", Path.GetPathRoot(first));
                    }
                    room -= n; offset += n; count -= n;
                }
            }

            public void Finish()
            {
                try { now?.Flush(true); }
                catch (IOException e) when ((e.HResult & 0xFFFF) == 112 || (e.HResult & 0xFFFF) == 39)
                {
                    throw new ModError("the drive is full: " + Path.GetPathRoot(first), "move_disk_full", Path.GetPathRoot(first));
                }
                now?.Dispose();
                now = null;
                // parts of an older package of the same name, after this one's last, go: they would not be read
                // but would look as if they belonged
                for (int k = temps.Count + 1; File.Exists(NameOf(first, k)); k++) File.Delete(NameOf(first, k));
                for (int k = 1; k <= temps.Count; k++)
                {
                    var final = NameOf(first, k);
                    if (File.Exists(final)) File.Delete(final);
                    File.Move(temps[k - 1], final);
                    Finals.Add(final);
                }
                temps.Clear();
            }

            public void Abandon()
            {
                try { now?.Dispose(); } catch (IOException) { }
                now = null;
                foreach (var t in temps)
                    try { File.Delete(t); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                temps.Clear();
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => now?.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) now?.Dispose(); base.Dispose(disposing); }
        }

        /// <summary>Reads a package's parts end to end, checking each belongs to the first.</summary>
        sealed class PartReader : Stream
        {
            readonly string first;
            byte[] id;
            int k;
            FileStream now;

            public PartReader(string file)
            {
                first = file;
                if (!File.Exists(file)) throw new ModError("no such file: " + file, "move_no_file", file);
                Open(1);
            }

            /// <summary>How many parts lie beside the first that belong to it.</summary>
            public static int Count(string first)
            {
                byte[] id = null;
                int k = 1;
                for (; File.Exists(PartWriter.NameOf(first, k)); k++)
                {
                    var head = new byte[18];
                    using (var s = new FileStream(PartWriter.NameOf(first, k), FileMode.Open, FileAccess.Read, FileShare.Read))
                        if (s.Read(head, 0, 18) != 18) break;
                    var mine = head.Skip(8).Take(8).ToArray();
                    if (id == null) id = mine;
                    else if (!id.SequenceEqual(mine) || BitConverter.ToUInt16(head, 16) != k) break;
                }
                return k - 1;
            }

            void Open(int part)
            {
                var name = PartWriter.NameOf(first, part);
                if (!File.Exists(name))
                    throw new ModError("part " + part + " of the package is missing: " + Path.GetFileName(name) + " must lie beside " + Path.GetFileName(first),
                                       "move_part_missing", name);
                now?.Dispose();
                now = new FileStream(name, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                var head = new byte[18];
                int got = 0;
                while (got < 18) { int n = now.Read(head, got, 18 - got); if (n == 0) break; got += n; }
                if (got < 18 || !head.Take(8).SequenceEqual(Magic))
                    throw new ModError("not a migration package of this program: " + name, "move_not_pack", name);
                var mine = head.Skip(8).Take(8).ToArray();
                if (id == null) id = mine;
                else if (!id.SequenceEqual(mine) || BitConverter.ToUInt16(head, 16) != part)
                    throw new ModError(Path.GetFileName(name) + " belongs to another package", "move_part_other", name);
                k = part;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                while (true)
                {
                    int n = now.Read(buffer, offset, count);
                    if (n > 0 || count == 0) return n;
                    // only a package cut short is read past its end record: the next part is missing
                    // (Open says so), or the last one was not copied whole
                    if (!File.Exists(PartWriter.NameOf(first, k + 1)) && Count(first) == k)
                        throw new ModError("the package ends too soon: " + Path.GetFileName(PartWriter.NameOf(first, k)) +
                                           " was not copied whole, or " + Path.GetFileName(PartWriter.NameOf(first, k + 1)) + " is missing",
                                           "move_short", PartWriter.NameOf(first, k + 1));
                    Open(k + 1);
                }
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) now?.Dispose(); base.Dispose(disposing); }
        }
    }

    /// <summary>CRC-32 (IEEE, as zip), eight bytes a step.</summary>
    static class Crc32
    {
        static readonly uint[] T = Make();

        static uint[] Make()
        {
            var t = new uint[8 * 256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            for (int i = 0; i < 256; i++)
                for (int s = 1; s < 8; s++)
                    t[s * 256 + i] = (t[(s - 1) * 256 + i] >> 8) ^ t[t[(s - 1) * 256 + i] & 0xFF];
            return t;
        }

        public static uint Of(byte[] b, int off, int len)
        {
            uint c = 0xFFFFFFFFu;
            int i = off, end = off + len;
            while (end - i >= 8)
            {
                uint a = c ^ (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24);
                uint d = (uint)(b[i + 4] | b[i + 5] << 8 | b[i + 6] << 16 | b[i + 7] << 24);
                c = T[7 * 256 + (a & 0xFF)] ^ T[6 * 256 + ((a >> 8) & 0xFF)] ^ T[5 * 256 + ((a >> 16) & 0xFF)] ^ T[4 * 256 + (a >> 24)]
                  ^ T[3 * 256 + (d & 0xFF)] ^ T[2 * 256 + ((d >> 8) & 0xFF)] ^ T[1 * 256 + ((d >> 16) & 0xFF)] ^ T[d >> 24];
                i += 8;
            }
            while (i < end) c = T[(c ^ b[i++]) & 0xFF] ^ (c >> 8);
            return ~c;
        }
    }
}
