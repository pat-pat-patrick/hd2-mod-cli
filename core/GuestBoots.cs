using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Hd2Mod
{
    /// <summary>A mod that is no loader but brings its own start-up script (`boot`), as Objective
    /// Tracker 0.6.7 does, takes the one slot the game starts from: the load order then decides
    /// whether it or the loader runs. The user (2026-10-26): players must never have to mind that
    /// order, and such a mod is no loader. So a deploy takes its boot off the slot and Boot Mod
    /// Loader runs it as a guest: in the game folder its archive is a copy in which the boot row
    /// has another name and its script, with a first line "-- HD2-Guest-Boot: &lt;mod&gt;", is
    /// appended at the end. Nothing in the library changes.
    ///
    /// Only when the boot that wins without these is Boot Mod Loader 0.12.0 or later, the first
    /// that runs guests: with an older loader, or none, there would be nobody to start it.
    /// Evidence: Objective Tracker 0.6.7 runs Boot Mod Loader's boot this way, and Boot Mod
    /// Loader started 8 of 8 mods under it (proven in game, 2026-10-26); the other way round is
    /// assumed until tried in game.</summary>
    public static class GuestBoots
    {
        public const string Header = "-- HD2-Guest-Boot: ";

        /// <summary>Whether a loader's boot runs guests (LoaderCaps.HostsGuests).</summary>
        public static bool Hosts(string kind, string version, IEnumerable<string> marks) =>
            LoaderCaps.Has(kind, version, marks, LoaderCaps.HostsGuests);

        /// <summary>The boots of the load order, in order (kind as LuaMods names it, with its version
        /// and markers; a script in bytecode cannot be a guest and stays as it is): which of them are
        /// run as guests. A quiet guest's too (LoaderCaps.QuietGuest: MDL3), when a boot comes after
        /// it: it reaches the game before another loader only as its guest (Loaders.Mdl3GuestFor); as
        /// the loader in effect it is last.</summary>
        public static HashSet<int> Guests(IList<(string Kind, string Version, bool Bytecode, List<string> Marks)> boots)
        {
            var guests = new HashSet<int>();
            for (int i = 0; i < boots.Count; i++)
                if ((boots[i].Kind == "other" || (i < boots.Count - 1 && LoaderCaps.Has(boots[i].Kind, boots[i].Version, boots[i].Marks, LoaderCaps.QuietGuest)))
                    && !boots[i].Bytecode) guests.Add(i);
            if (guests.Count == 0) return guests;
            int winner = -1;
            for (int i = 0; i < boots.Count; i++) if (!guests.Contains(i)) winner = i;
            if (winner < 0 || !Hosts(boots[winner].Kind, boots[winner].Version, boots[winner].Marks)) guests.Clear();
            return guests;
        }

        /// <summary>The name a guest goes by: the mod's, on one line.</summary>
        public static string Name(string mod)
        {
            var one = new string((mod ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
            if (one.Length == 0) one = "mod";
            return one.Length > 100 ? one.Substring(0, 100) : one;
        }

        /// <summary>MurmurHash64A, seed 0: Stingray's resource names.</summary>
        public static ulong Murmur64(byte[] data)
        {
            unchecked
            {
                const ulong m = 0xc6a4a7935bd1e995;
                const int r = 47;
                ulong h = m * (ulong)data.Length;
                int off = data.Length / 8 * 8;
                for (int i = 0; i < off; i += 8)
                {
                    ulong k = BitConverter.ToUInt64(data, i);
                    k *= m;
                    k ^= k >> r;
                    k *= m;
                    h ^= k;
                    h *= m;
                }
                int l = data.Length & 7;
                for (int i = l - 1; i >= 1; i--) h ^= (ulong)data[off + i] << (8 * i);
                if (l >= 1)
                {
                    h ^= data[off];
                    h *= m;
                }
                h ^= h >> r;
                h *= m;
                h ^= h >> r;
                return h;
            }
        }

        /// <summary>The boot row's new name: the hash of "guest_boot/&lt;mod&gt;".</summary>
        public static ulong IdOf(string mod) => Murmur64(Encoding.UTF8.GetBytes("guest_boot/" + Name(mod)));

        /// <summary>The archive with its boot off the start-up slot: the boot row renamed and
        /// pointed at its script, appended at the end (16-byte aligned) with the guest line in
        /// front. Every other row and payload is left as it was: rows need not be in order (65 of
        /// 96 deployed archives are not), and nothing is moved. Null when there is no boot in
        /// source text to take.</summary>
        public static byte[] Derive(byte[] archive, string mod)
        {
            if (archive.Length < 0x48 || BitConverter.ToUInt32(archive, 0) != 0xF0000011) return null;
            uint types = BitConverter.ToUInt32(archive, 4), count = BitConverter.ToUInt32(archive, 8);
            long rows = 0x48 + types * 32L;
            if (types > 4096 || count > 200000 || rows + count * 80L > archive.Length) return null;
            for (long i = 0; i < count; i++)
            {
                long row = rows + i * 80;
                if (BitConverter.ToUInt64(archive, (int)row) != LuaMods.BootId || BitConverter.ToUInt64(archive, (int)row + 8) != Deploy.LuaType) continue;
                ulong off = BitConverter.ToUInt64(archive, (int)row + 0x10);
                uint size = BitConverter.ToUInt32(archive, (int)row + 0x38);
                if (size < 8 || off + size > (ulong)archive.Length) return null;
                uint length = BitConverter.ToUInt32(archive, (int)off), kind = BitConverter.ToUInt32(archive, (int)off + 4);
                if (kind != 2 || length > size - 8 || (length > 0 && archive[(int)off + 8] == 0x1B)) return null;   // source only
                var head = Encoding.UTF8.GetBytes(Header + Name(mod) + "\n");
                var text = new byte[head.Length + length];
                head.CopyTo(text, 0);
                Array.Copy(archive, (long)off + 8, text, head.Length, length);
                long at = (archive.Length + 15) / 16 * 16;
                var out_ = new byte[at + 8 + text.Length];
                Array.Copy(archive, out_, archive.Length);
                BitConverter.GetBytes((uint)text.Length).CopyTo(out_, at);
                BitConverter.GetBytes(kind).CopyTo(out_, at + 4);
                text.CopyTo(out_, at + 8);
                BitConverter.GetBytes(IdOf(mod)).CopyTo(out_, row);
                BitConverter.GetBytes((ulong)at).CopyTo(out_, row + 0x10);
                BitConverter.GetBytes((uint)(8 + text.Length)).CopyTo(out_, row + 0x38);
                return out_;
            }
            return null;
        }

        /// <summary>Where the derived copies are kept: on the game's drive, so the game gets a hard
        /// link and not a copy (the user, 2026-10-26), in this program's own mod folder, where no
        /// unpacked mod or leftover is named like it; else in the home.</summary>
        static string Folder
        {
            get
            {
                try
                {
                    var cfg = Store.Config();
                    var game = Store.GameDir(cfg, false);
                    var mods = Store.ModsDir(cfg);
                    if (game != null && Directory.Exists(mods) && Deploy.SameDrive(mods, game)) return Path.Combine(mods, ".guest_boots");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { }
                return Home;
            }
        }

        static string Home => Path.Combine(Store.Home, "guest_boots");

        /// <summary>The derived copy of src, made once for each size and time of src (see
        /// Folder); older copies of the same file are deleted. Null when src
        /// has no boot to take.</summary>
        public static string Derived(string src, string mod)
        {
            var fi = new FileInfo(src);
            string key;
            using (var sha = SHA1.Create())
                key = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(Paths.Abs(src).ToLowerInvariant() + "|" + Name(mod)))
                                       .Take(8).Select(b => b.ToString("x2")));
            var name = key + "_" + fi.Length + "_" + fi.LastWriteTimeUtc.Ticks + ".patch";
            var folder = Folder;
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) return path;
            var derived = Derive(File.ReadAllBytes(src), mod);
            if (derived == null) return null;
            Directory.CreateDirectory(folder);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, derived);
            File.Move(tmp, path);
            foreach (var dir in new[] { Path.GetDirectoryName(path), Home }.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
                foreach (var old in Directory.EnumerateFiles(dir, key + "_*.patch").Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    try { File.Delete(old); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }   // still in the game as a link: next time
            return path;
        }

        // whether an archive holds a boot, by its size and time: asked at every state read
        static readonly Dictionary<string, (string Sig, bool Boot)> holds = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);

        static bool HoldsBoot(string path)
        {
            var fi = new FileInfo(path);
            var sig = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
            lock (holds) if (holds.TryGetValue(path, out var k) && k.Sig == sig) return k.Boot;
            bool boot = Deploy.HoldsLua(path, new[] { LuaMods.BootId });
            lock (holds) holds[path] = (sig, boot);
            return boot;
        }

        /// <summary>The plan with each guest's archive taken from its derived copy. An archive
        /// that cannot be read or derived stays as it is.</summary>
        public static void Apply(List<Wanted> plan)
        {
            var boots = new List<(Wanted W, string Kind, string Version, bool Bytecode, List<string> Marks)>();
            foreach (var w in plan)
            {
                if (!Deploy.Patch.IsMatch(Path.GetFileName(w.Name))) continue;
                try
                {
                    if (!HoldsBoot(w.Src)) continue;
                    var s = LuaMods.Read(w.Src).FirstOrDefault(x => x.Id == LuaMods.BootId);
                    if (s != null) boots.Add((w, s.BootKind, s.BootVersion, s.Bytecode, s.Marks));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            foreach (var i in Guests(boots.Select(b => (b.Kind, b.Version, b.Bytecode, b.Marks)).ToList()))
            {
                var w = boots[i].W;
                try
                {
                    var d = Derived(w.Src, w.Mod);
                    if (d != null) w.Src = d;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
        }
    }
}
