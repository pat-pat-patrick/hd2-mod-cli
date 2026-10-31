using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Hd2Mod
{
    /// <summary>A refusal with a message for the player ("hd2qd: ..." on the command line).</summary>
    public class ModError : Exception
    {
        /// <summary>For the window, which says it in its own words: "no_game", "duplicate"
        /// (Arg: the mod's name), "maybe_update" (Arg: the library mod's id, a line break, the
        /// path added), else null.</summary>
        public readonly string Kind, Arg;

        public ModError(string message, string kind = null, string arg = null) : base(message)
        {
            Kind = kind;
            Arg = arg;
        }
    }

    /// <summary>The settings folder: config.json, library.json, deployed.json.</summary>
    public static class Store
    {
        /// <summary>Set by tests; else HD2MOD_HOME, else %USERPROFILE%\.hd2-mod-cli. Not AppData: a
        /// program started by a packaged app -- a desktop AI assistant, which is how a command
        /// line is mostly run now -- has a folder it makes under AppData put in that app's own
        /// copy, and the library splits in two without an error (the user, 2026-10-25). The rest
        /// of the user's folder is left alone.</summary>
        public static string HomeOverride;

        public static string Home => HomeOverride ?? Environment.GetEnvironmentVariable("HD2MOD_HOME") ??
                                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hd2-mod-cli");

        public static string PathOf(string name) => Path.Combine(Home, name);

        /// <summary>Where this process's writes to the home really land, when Windows sends them
        /// somewhere else; null when they land where they say. A packaged app -- the Claude
        /// desktop app, for one -- and the programs it starts have their writes to AppData put
        /// in the package's own LocalCache, while every other program goes on reading the real
        /// AppData. Nothing fails and both sides look normal: a library written that way is a
        /// second library nobody else can see, and a deploy made from it rearranges the real
        /// game folder after a list the player never had (the user, 2026-10-25). A test can say
        /// otherwise.</summary>
        public static Func<string> Redirected = FindRedirect;

        static bool redirectKnown;
        static string redirectSeen;

        /// <summary>Writes a probe into the home and looks for it in every package's LocalCache:
        /// found there, the writes are being sent there. Once a process: the answer does not
        /// change while it runs. Only for a home under AppData; one elsewhere is not moved.</summary>
        static string FindRedirect()
        {
            if (redirectKnown) return redirectSeen;
            redirectKnown = true;
            string home, appdata, packages;
            try
            {
                home = Path.GetFullPath(Home).TrimEnd('\\');
                appdata = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)).TrimEnd('\\');
                packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return null; }
            if (!home.StartsWith(appdata + "\\", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(packages)) return null;
            var rel = home.Substring(appdata.Length + 1);
            var name = ".where-" + Guid.NewGuid().ToString("N");
            var probe = Path.Combine(home, name);
            try
            {
                Directory.CreateDirectory(home);
                File.WriteAllText(probe, "");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
            try
            {
                foreach (var pkg in Directory.EnumerateDirectories(packages))
                {
                    var there = Path.Combine(pkg, "LocalCache", "Roaming", rel);
                    if (!File.Exists(Path.Combine(there, name))) continue;
                    redirectSeen = there;
                    // the probe made that folder: take it away again if nothing else is in it,
                    // so it does not stand in front of the real one
                    try
                    {
                        File.Delete(Path.Combine(there, name));
                        if (!Directory.EnumerateFileSystemEntries(there).Any()) Directory.Delete(there);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                    return redirectSeen;
                }
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
            finally
            {
                try { if (File.Exists(probe)) File.Delete(probe); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
        }

        public static Dictionary<string, object> Load(string name)
        {
            var p = PathOf(name);
            if (!File.Exists(p)) return new Dictionary<string, object>();
            return ReadJson(p) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        public static object ReadJson(string path) => Json.Parse(File.ReadAllText(path, Encoding.UTF8));

        /// <summary>Written whole through a temporary file, then put in place.</summary>
        /// <summary>A settings file written whole: into a temporary name first, then put in place
        /// in one step, so a crash or a power cut cannot leave half a file. The version it replaces
        /// is kept beside it as .bak — the mods themselves can be downloaded again, but what the
        /// player chose (which mods, their order, their options, their labels) cannot, and it is a
        /// few kilobytes (the user, 2026-10-24). To go back, rename the .bak over the file.</summary>
        public static void Save(string name, object data)
        {
            Directory.CreateDirectory(Home);
            var p = PathOf(name);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, Json.Write(data, true) + "\n", new UTF8Encoding(false));
            if (!File.Exists(p)) { File.Move(tmp, p); return; }
            try { File.Replace(tmp, p, p + ".bak"); }
            catch (IOException) { File.Replace(tmp, p, null); }          // the backup is a comfort, never a reason to fail
            catch (UnauthorizedAccessException) { File.Replace(tmp, p, null); }
        }

        public static Dictionary<string, object> Config() => Load("config.json");

        public static string GameDir(Dictionary<string, object> cfg = null, bool required = true)
        {
            cfg = cfg ?? Config();
            var g = Json.Str(cfg, "game_dir");
            if (!string.IsNullOrEmpty(g) && Directory.Exists(Path.Combine(g, "data"))) return g;
            if (required) throw new ModError("no game folder: run  hd2qd init --game \"<...>\\steamapps\\common\\Helldivers 2\"", "no_game");
            return null;
        }

        /// <summary>The mod folder, where zips are unpacked and downloads go: the one set (init
        /// --mods-dir, or the Storage page's Make default); else HD2 Arsenal's mod folder, where the
        /// player's mods are already; else HD2 Mods on the game's drive (hard links). Never
        /// AppData (the user, 2026-10-24).</summary>
        public static string ModsDir(Dictionary<string, object> cfg = null)
        {
            cfg = cfg ?? Config();
            var m = Json.Str(cfg, "mods_dir");
            if (!string.IsNullOrEmpty(m)) return m;
            var arsenal = ArsenalModsDir();
            if (arsenal != null) return arsenal;
            var g = GameDir(cfg, false);
            if (g != null) return Path.Combine(Path.GetPathRoot(g), "HD2 Mods");
            return Path.Combine(Home, "mods");
        }

        static (string File, DateTime When, string Dir) arsenalSeen;

        /// <summary>HD2 Arsenal's mod folder (its userModsDir), when it is there.</summary>
        public static string ArsenalModsDir()
        {
            var f = ArsenalFile();
            if (f == null) return null;
            var when = File.GetLastWriteTimeUtc(f);
            if (arsenalSeen.File == f && arsenalSeen.When == when) return arsenalSeen.Dir;
            string dir = null;
            try { dir = Json.Str(ReadJson(f) as Dictionary<string, object>, "userModsDir"); }
            catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException) { }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = null;
            arsenalSeen = (f, when, dir);
            return dir;
        }

        public static string ArsenalFile()
        {
            var b = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            var p = string.IsNullOrEmpty(b) ? null : Path.Combine(b, "hd2arsenal", "hd2a_data.json");
            return p != null && File.Exists(p) ? p : null;
        }
    }

    /// <summary>The Windows calls .NET Framework does not have.</summary>
    public static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string newName, string existing, IntPtr security);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string from, string to, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
                                                                           uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInfo info);

        [StructLayout(LayoutKind.Sequential)]
        struct FileInfo
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        /// <summary>A file's named stream as text (path:name, e.g. Zone.Identifier, where Windows
        /// keeps a download's address), else null.</summary>
        public static string ReadStream(string path, string name)
        {
            // GENERIC_READ, share read/write/delete, OPEN_EXISTING
            using (var h = CreateFile(path + ":" + name, 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                using (var fs = new FileStream(h, FileAccess.Read))
                using (var r = new StreamReader(fs, Encoding.UTF8, true))
                    return r.ReadToEnd();
            }
        }

        /// <summary>A hard link at newName to existing; false when the system refuses
        /// (another drive, a file system without links).</summary>
        public static bool Link(string existing, string newName) => CreateHardLink(newName, existing, IntPtr.Zero);

        /// <summary>from renamed over to, in one step.</summary>
        public static void Replace(string from, string to)
        {
            if (!MoveFileEx(from, to, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "cannot replace " + to);
        }

        /// <summary>A file's or folder's volume, index and number of names, as Python's
        /// os.stat gives them (st_dev, st_ino, st_nlink); false when it cannot be opened.</summary>
        static bool Identity(string path, out uint volume, out ulong index, out uint links)
        {
            volume = 0; index = 0; links = 0;
            // no access asked, every sharing allowed, folders too (backup semantics)
            using (var h = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (h.IsInvalid || !GetFileInformationByHandle(h, out var fi)) return false;
                volume = fi.VolumeSerial;
                index = ((ulong)fi.IndexHigh << 32) | fi.IndexLow;
                links = fi.Links;
                return true;
            }
        }

        /// <summary>Whether two paths are one file (a hard link), as Python's samefile;
        /// null when either cannot be opened (samefile raises).</summary>
        public static bool? SameFile(string a, string b)
        {
            if (!Identity(a, out var va, out var ia, out _) || !Identity(b, out var vb, out var ib, out _)) return null;
            return va == vb && ia == ib;
        }

        /// <summary>A file's identity (volume, index): equal for two names of one file; null
        /// when it cannot be opened.</summary>
        public static (uint, ulong)? FileId(string path) =>
            Identity(path, out var v, out var i, out _) ? (v, i) : ((uint, ulong)?)null;

        /// <summary>How many names the file has (more than one: a hard link).</summary>
        public static int Links(string path) => Identity(path, out _, out _, out var n) ? (int)n : 0;

        /// <summary>Whether two paths are on one volume (st_dev); false when either cannot be opened.</summary>
        public static bool SameDrive(string a, string b) =>
            Identity(a, out var va, out _, out _) && Identity(b, out var vb, out _, out _) && va == vb;
    }
}
