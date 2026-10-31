using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>MDL3's native part, installed by the manager (the user, 2026-10-31: a player who
    /// never had MDL drags its zip in and the rest is done, and later versions of MDL3 too). An
    /// MDL3 package carries BUILD.json and Native\&lt;dll&gt;; its own install.ps1 copies the DLL
    /// to %LOCALAPPDATA%\MDL3 beside config.json and install-state.json. That is done here in the
    /// same shape, without running the package's script: only a DLL signed by MDL3's publisher
    /// is copied.
    /// - When: on add or replace while the game is closed, and at every deploy; whenever the
    ///   DLL there is not the package's (a new player, an update, a rollback) or config.json
    ///   names another DLL or game folder.
    /// - Later versions: the package is known by its files, not its version; the publisher by
    ///   the certificate's name, not its thumbprint (certificates are renewed); config.json keeps
    ///   every key it had; the folders made are the ones the package's install.ps1 names, read as
    ///   text.
    /// - What it cannot follow (a second native file, BUILD.json asking for more) it does not
    ///   guess at: it says to run the package's own installer.</summary>
    public static class Mdl3Native
    {
        /// <summary>The name in MDL3's code-signing certificate (Certum, 3.0.0-dev.18 to dev.21).</summary>
        public const string Publisher = "CHIEI INTERNET TECHNOLOGY LLC";
        const string CodeSigning = "1.3.6.1.5.5.7.3.3";
        /// <summary>install.ps1's folders up to dev.21, when its text names none.</summary>
        static readonly string[] KnownFolders =
            { "MDL3/Logs", "MDL/Helldivers2/Mods", "MDL/Helldivers2/Logs", "MDL/Helldivers2/Languages/Options", "CowboyBingus/Helldivers2/Logs", "HD2Runtime/mod_data" };

        /// <summary>%LOCALAPPDATA%; tests replace it.</summary>
        public static Func<string> LocalAppData = () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static string DataDir() => Path.Combine(LocalAppData(), "MDL3");
        /// <summary>Whether a file carries a valid signature of MDL3's publisher; tests replace it.</summary>
        public static Func<string, bool> Signed = SignedByPublisher;
        /// <summary>What the last install or wait in this process came to, for the window to say
        /// in its own words; it takes and clears it.</summary>
        public static Outcome Last;

        public class Package
        {
            public string Folder, Dll, DllName, Version, NativeVersion, Sha256, Installer;
            public List<string> Folders;
            /// <summary>Why it cannot be installed here, else null.</summary>
            public string Problem;
        }

        public class Outcome
        {
            /// <summary>"installed", "waits" (the game runs) or "failed".</summary>
            public string Kind, Version, Detail;
        }

        /// <summary>The MDL3 package in a mod's folder, else null.</summary>
        public static Package Find(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            var buildPath = Path.Combine(folder, "BUILD.json");
            if (!File.Exists(buildPath)) return null;
            Dictionary<string, object> build;
            try { build = Json.Parse(File.ReadAllText(buildPath)) as Dictionary<string, object>; }
            catch (FormatException) { return null; }
            var name = Json.Str(build, "native_path");
            if (build == null || string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name == "..") return null;
            var dll = Path.Combine(folder, "Native", name);
            if (!File.Exists(dll)) return null;
            var p = new Package
            {
                Folder = folder, Dll = dll, DllName = name, Version = Json.Str(build, "version"), NativeVersion = Json.Str(build, "native_version"),
                Sha256 = (Json.Str(build, "native_sha256") ?? "").ToLowerInvariant(),
            };
            var installer = Path.Combine(folder, "install.ps1");
            p.Installer = File.Exists(installer) ? installer : null;
            p.Folders = FoldersOf(p.Installer);
            // install.ps1 (dev.18-dev.21) refuses a package whose BUILD.json does not say false here
            if (!(build.TryGetValue("requires_signature", out var rs) && rs is bool b && !b))
                p.Problem = "its BUILD.json asks for what this manager does not know (requires_signature)";
            else if (Directory.GetFiles(Path.Combine(folder, "Native")).Length != 1)
                p.Problem = "it brings more than one native file";
            else if (p.Sha256.Length != 64 || Sha(dll) != p.Sha256)
                p.Problem = "its DLL is not the one BUILD.json names";
            return p;
        }

        /// <summary>The folders install.ps1 makes, from its text ($folders=@('a/b',...)); the
        /// known ones when it names none. Only plain relative names count.</summary>
        static List<string> FoldersOf(string installer)
        {
            if (installer != null)
            {
                var m = Regex.Match(File.ReadAllText(installer), @"\$folders\s*=\s*@\(([^)]*)\)");
                if (m.Success)
                {
                    var list = Regex.Matches(m.Groups[1].Value, "'([^']*)'").Cast<Match>().Select(x => x.Groups[1].Value).ToList();
                    if (list.Count > 0 && list.All(f => Regex.IsMatch(f, @"^[A-Za-z0-9 _\-]+(/[A-Za-z0-9 _\-]+)*$"))) return list;
                }
            }
            return KnownFolders.ToList();
        }

        /// <summary>The package the deploy uses: the first enabled mod that is one.</summary>
        public static Package Wanted(Library lib)
        {
            foreach (var m in lib.Mods.Where(m => m.Enabled && Profiles.InProfile(lib, m)))
            {
                var p = Find(m.Path);
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>Why the installed native part is not this package's, else null.</summary>
        public static string Need(Package p, string game)
        {
            var target = Path.Combine(DataDir(), p.DllName);
            if (!File.Exists(target)) return "not installed";
            if (Sha(target) != p.Sha256) return "another version";
            var config = ReadJson(Path.Combine(DataDir(), "config.json"));
            if (config == null) return "no settings";
            if (!Same(Json.Str(config, "dll"), target) || !Same(Json.Str(config, "game_dir"), game)) return "settings for another place";
            return null;
        }

        /// <summary>At a deploy: the wanted package's native part installed when it is not;
        /// what was done, else null. Never stops the deploy: a problem is said, not thrown.</summary>
        public static string Ensure(Library lib, string game, bool dryRun = false)
        {
            var p = Wanted(lib);
            if (p == null || Need(p, game) == null) return null;
            if (dryRun) return "would install MDL3's native part " + p.NativeVersion + " (MDL3 " + p.Version + ")";
            return Try(p, game);
        }

        /// <summary>After an add or replace: the same, for that mod.</summary>
        public static string Added(Mod mod, string game)
        {
            if (!mod.Enabled || game == null) return null;
            var p = Find(mod.Path);
            if (p == null) return null;
            if (Need(p, game) == null) return "MDL3's native part is already " + p.NativeVersion + " (MDL3 " + p.Version + ")";
            return Try(p, game);
        }

        static string Try(Package p, string game)
        {
            if (Deploy.GameRunning())
            {
                Last = new Outcome { Kind = "waits", Version = p.Version };
                return "MDL3's native part is installed at the next deploy, with the game closed";
            }
            try
            {
                var said = Install(p, game);
                Last = new Outcome { Kind = "installed", Version = p.Version };
                return said;
            }
            catch (Exception e) when (e is ModError || e is IOException || e is UnauthorizedAccessException)
            {
                Last = new Outcome { Kind = "failed", Version = p.Version, Detail = e.Message };
                return "MDL3's native part was not installed: " + e.Message;
            }
        }

        /// <summary>As install.ps1 does it: the files there backed up, the DLL copied and checked,
        /// config.json (every key kept, dll and game_dir set) and install-state.json written, the
        /// folders made; on a failure the backup comes back.</summary>
        public static string Install(Package p, string game)
        {
            string ByHand(string why) => "MDL3 " + p.Version + ": " + why + ". Run the installer in " + p.Folder + " (INSTALL bat or install.ps1)";
            if (p.Problem != null) throw new ModError(ByHand(p.Problem), "mdl3_manual", p.Folder);
            if (!Signed(p.Dll)) throw new ModError(ByHand("its DLL is not signed by " + Publisher), "mdl3_unsigned", p.Folder);
            var data = DataDir();
            NoLinks(data);
            if (Inside(data, game)) throw new ModError("MDL3's folder " + data + " is inside the game folder", "mdl3_place");
            Directory.CreateDirectory(data);
            var target = Path.Combine(data, p.DllName);
            var configPath = Path.Combine(data, "config.json");
            var statePath = Path.Combine(data, "install-state.json");
            var state = ReadJson(statePath);
            if (state != null && (Json.Str(state, "product") != "MDL3" || !Same(Json.Str(state, "data"), data)))
                throw new ModError(statePath + " belongs to another installation: left as it is", "mdl3_place");
            if (File.Exists(target) && Sha(target) != p.Sha256 && !Tracked(state, target) && !Signed(target))
                throw new ModError("another file is at " + target + ": move it away first", "mdl3_place");

            var backup = Backup(data, new[] { target, configPath, statePath });
            try
            {
                Put(p.Dll, target, data);
                var config = ReadJson(configPath) ?? new Dictionary<string, object>();
                if (!config.ContainsKey("schema")) config["schema"] = 1L;
                config["dll"] = target;
                config["game_dir"] = Path.GetFullPath(game).TrimEnd('\\');
                Write(config, configPath, data);
                Write(new Dictionary<string, object>
                {
                    ["schema"] = 2L, ["product"] = "MDL3", ["version"] = p.Version, ["native_version"] = p.NativeVersion,
                    ["data"] = data, ["game"] = config["game_dir"],
                    ["files"] = new List<object> { new Dictionary<string, object> { ["path"] = target, ["sha256"] = p.Sha256 } },
                    ["installed_utc"] = DateTime.UtcNow.ToString("o"),
                }, statePath, data);
                if (Sha(target) != p.Sha256 || !Signed(target)) throw new ModError("the installed DLL did not check out", "mdl3_failed");
            }
            catch
            {
                Restore(backup);
                throw;
            }
            foreach (var f in p.Folders)
            {
                var dir = f == "MDL3/Logs" ? Path.Combine(data, "Logs") : Path.Combine(LocalAppData(), f.Replace('/', '\\'));
                NoLinks(dir);
                Directory.CreateDirectory(dir);
            }
            return "MDL3's native part " + p.NativeVersion + " (MDL3 " + p.Version + ") installed in " + data;
        }

        static bool Tracked(Dictionary<string, object> state, string target) =>
            File.Exists(target) && (Json.List(state, "files") ?? new List<object>()).OfType<Dictionary<string, object>>()
                .Any(f => Same(Json.Str(f, "path"), target) && Json.Str(f, "sha256") == Sha(target));

        /// <summary>The files there now, in Backups\&lt;time&gt;_&lt;id&gt; with backup.json, as
        /// install.ps1 keeps them.</summary>
        static string Backup(string data, string[] paths)
        {
            var dir = Path.Combine(data, "Backups", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var rows = new List<object>();
            foreach (var path in paths)
            {
                var row = new Dictionary<string, object> { ["path"] = path, ["exists"] = File.Exists(path) };
                if (File.Exists(path))
                {
                    row["sha256"] = Sha(path);
                    File.Copy(path, Path.Combine(dir, Path.GetFileName(path)));
                }
                rows.Add(row);
            }
            File.WriteAllText(Path.Combine(dir, "backup.json"), Json.Write(new Dictionary<string, object> { ["files"] = rows }, true));
            return dir;
        }

        static void Restore(string backup)
        {
            var record = ReadJson(Path.Combine(backup, "backup.json"));
            foreach (var row in (Json.List(record, "files") ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                var path = Json.Str(row, "path");
                if (Json.Bool(row, "exists")) File.Copy(Path.Combine(backup, Path.GetFileName(path)), path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>A file put in place through a copy beside it, checked before it replaces the old one.</summary>
        static void Put(string source, string target, string data)
        {
            var tmp = Path.Combine(data, ".mdl3-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, tmp);
                File.SetAttributes(tmp, FileAttributes.Normal);
                if (Sha(tmp) != Sha(source)) throw new ModError("copying " + Path.GetFileName(source) + " did not check out", "mdl3_failed");
                if (File.Exists(target)) { File.SetAttributes(target, FileAttributes.Normal); File.Delete(target); }
                File.Move(tmp, target);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        static void Write(Dictionary<string, object> v, string path, string data)
        {
            var tmp = Path.Combine(data, ".mdl3-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmp, Json.Write(v, true));
                Put(tmp, path, data);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        static Dictionary<string, object> ReadJson(string path)
        {
            if (!File.Exists(path)) return null;
            try { return Json.Parse(File.ReadAllText(path)) as Dictionary<string, object>; }
            catch (FormatException) { return null; }
        }

        /// <summary>No junction or symbolic link anywhere on the way, as install.ps1 refuses them.</summary>
        static void NoLinks(string path)
        {
            for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))
                    throw new ModError(p + " is a link: MDL3 is installed only in a plain folder", "mdl3_place");
        }

        static bool Same(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        static bool Inside(string path, string folder) =>
            Same(path, folder) || Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        static string Sha(string path)
        {
            using (var f = File.OpenRead(path))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        // Authenticode through WinVerifyTrust (no revocation check, no network), then the
        // signer's name and its code-signing use, as install.ps1 checks them.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TrustFile { public uint Size; public string Path; public IntPtr File, Subject; }

        [StructLayout(LayoutKind.Sequential)]
        struct TrustData
        {
            public uint Size;
            public IntPtr Policy, Sip;
            public uint Ui, Revocation, Choice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr State, Url;
            public uint ProvFlags, UiContext;
            public IntPtr Settings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);

        public static bool SignedByPublisher(string path)
        {
            var file = new TrustFile { Size = (uint)Marshal.SizeOf(typeof(TrustFile)), Path = Path.GetFullPath(path) };
            var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustFile)));
            try
            {
                Marshal.StructureToPtr(file, filePtr, false);
                var data = new TrustData { Size = (uint)Marshal.SizeOf(typeof(TrustData)), Ui = 2, Revocation = 0, Choice = 1, File = filePtr, ProvFlags = 0x1000 };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
                if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) return false;
                var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                if (cert.GetNameInfo(X509NameType.SimpleName, false) != Publisher) return false;
                return cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                    .Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == CodeSigning));
            }
            catch (CryptographicException) { return false; }
            finally
            {
                Marshal.DestroyStructure(filePtr, typeof(TrustFile));
                Marshal.FreeHGlobal(filePtr);
            }
        }
    }
}
