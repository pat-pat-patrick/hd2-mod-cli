using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>
    /// The live wiring for <see cref="Integrity"/>: it reads the library and %APPDATA%, fetches
    /// the signed manifest, and (in the window's background only) sends reports. The command-line
    /// `hd2qd integrity` is dry-run always -- it lists what this computer would report and sends
    /// nothing and writes nothing (CONTRACT_sealed_lua section 10.4). The actual sending lives in
    /// <see cref="Integrity.Report"/>, called by <see cref="RunBackground"/>, which the window
    /// runs once at start and once at the next idle after the library changes.
    ///
    /// Nothing here writes to hd2qd.log or the error report: the check leaves no trace (10.4.7).
    /// </summary>
    public static partial class IntegrityWiring
    {
        static readonly Regex Account = new Regex("(?<![0-9a-fA-F])[0-9a-fA-F]{16}(?![0-9a-fA-F])", RegexOptions.Compiled);

        // the issuer and key as the members' plugin reads them (Protocol.Declaration): a key padded
        // with zeros or spaces still claims there, so it must still be checked here (2026-11-19 audit)
        static readonly Regex KeyHex = new Regex("^[89a-f][0-9a-f]{511}$", RegexOptions.Compiled);
        static readonly Regex Host = new Regex(@"^[a-z0-9.-]+(:\d+)?$", RegexOptions.Compiled);
        static string StateFile => Store.PathOf("integrity_state.json");

        // ---- building the licensed-mod list from the library --------------------

        /// <summary>Every licensed mod in the library: one whose hd2_license.json names an issuer
        /// and a key, which the members' plugin needs to claim its content. Whether its creator
        /// checks it is the issuer's to say, by publishing a signed manifest (Integrity.Scan), never
        /// a field in the mod: an "integrity": 1 here could be deleted and the mod still worked
        /// (the user, 2026-11-19). Robust: any mod that cannot be read is skipped, never thrown.</summary>
        public static List<Integrity.LicensedMod> Licensed(Library lib)
        {
            var mods = new List<Integrity.LicensedMod>();
            foreach (var m in lib.Mods)
            {
                try
                {
                    var lic = ReadJson(Path.Combine(m.Path, "hd2_license.json"));
                    if (lic == null) continue;
                    var issuer = Json.Str(lic, "issuer")?.Trim().ToLowerInvariant();
                    var key = Json.Str(lic, "key")?.Trim().ToLowerInvariant().TrimStart('0');
                    // what the plugin refuses cannot claim anything either: not a licensed mod
                    if (issuer == null || !Host.IsMatch(issuer) || key == null || !KeyHex.IsMatch(key)) continue;
                    var manifest = ReadJson(Path.Combine(m.Path, "manifest.json"));
                    if (manifest == null) continue;
                    var guid = Json.Str(manifest, "Guid");
                    var name = Json.Str(manifest, "Name") ?? m.Name;
                    var seg = Json.Obj(lic, "segments");
                    // the version: "segments".version when declared (a published package's Name
                    // carries none), else the Name's vX.Y.Z; a malformed declaration skips the mod
                    var version = seg != null && seg.ContainsKey("version") ? WholeVersion(Json.Str(seg, "version")) : VersionToken(name);
                    if (string.IsNullOrEmpty(guid) || version == null) continue;
                    var lm = new Integrity.LicensedMod
                    {
                        Guid = guid,
                        Issuer = issuer,
                        KeyModulusHex = key,
                        Name = name,
                        Version = version,
                        SegmentsId = seg != null ? Json.Str(seg, "id") : null,
                        SegmentsCount = seg != null ? (int)Json.Long(seg, "count") : 0,
                        Files = HashTree(m.Path),
                    };
                    mods.Add(lm);
                }
                catch { /* a mod we cannot read is not checked; the manager must never break here */ }
            }
            return mods;
        }

        /// <summary>Relative path ('/') -> lower-hex SHA-256 for the mod's package files. Manager
        /// sidecars (dotfiles) are left out so they are never mistaken for an altered shell.</summary>
        static Dictionary<string, string> HashTree(string root)
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(root)) return files;
            foreach (var full in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (rel.Split('/').Any(p => p.StartsWith("."))) continue;   // .git, .hd2qd markers, etc.
                try { files[rel] = Integrity.Sha256Hex(File.ReadAllBytes(full)); } catch { }
            }
            return files;
        }

        /// <summary>A declared version, when it is a whole tag (it names a folder), else null.</summary>
        internal static string WholeVersion(string v) =>
            v != null && Regex.IsMatch(v, @"^v\d+(?:\.\d+)+\z") ? v : null;

        static string VersionToken(string name)
        {
            var m = Regex.Match(name ?? "", @"v\d+\.\d+\.\d+");
            return m.Success ? m.Value : null;
        }

        static Dictionary<string, object> ReadJson(string path)
        {
            try { return File.Exists(path) ? Json.Parse(File.ReadAllText(path)) as Dictionary<string, object> : null; }
            catch { return null; }
        }

        // ---- the live environment ----------------------------------------------

        public sealed class LiveEnv : Integrity.IIntegrityEnv
        {
            readonly bool persist;      // the window's background run caches and throttles; the CLI dry-run does not
            readonly string appData;    // the roaming AppData the game and the members' plugin use
            public LiveEnv(bool persist, string appData = null)
            {
                this.persist = persist;
                this.appData = appData ?? PluginHost.AppData;
            }

            string LicensesRoot => Path.Combine(appData, "Arrowhead", "Helldivers2", "licenses");

            public long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            public string Manifest(Integrity.LicensedMod mod)
            {
                // section 10.4: fetched at most once a day and cached. The cache is the player's file,
                // so only a manifest that verifies counts, and a time still to come makes it stale
                // (2026-11-19 audit: a junk text dated far ahead switched the check off for good).
                bool bySegments = !string.IsNullOrEmpty(mod.SegmentsId);
                string slot = (bySegments ? "segments:" : "mod:") + (bySegments ? mod.SegmentsId : mod.Guid);
                bool Good(string t) => Integrity.ParseManifest(t, mod.Issuer, mod.KeyModulusHex, out _) != null;
                var state = LoadState();
                var cache = Dict(state, "manifest");
                var row = Dict(cache, slot);
                long at = Json.Long(row, "at");
                string text = Json.Str(row, "text");
                if (text != null && !Good(text)) text = null;
                long now = NowUnix();
                if (text != null && at <= now && now - at < Integrity.ThrottleSeconds) return text;
                string fetched = TryFetch(mod.Issuer, bySegments ? "segments" : "mod", bySegments ? mod.SegmentsId : mod.Guid);
                if (fetched == null || !Good(fetched)) return text;   // offline or none: the cached good one, if any
                if (persist)
                {
                    row["at"] = now; row["text"] = fetched; cache[slot] = row; state["manifest"] = cache;
                    SaveState(state);
                }
                return fetched;
            }

            static string TryFetch(string issuer, string by, string value)
            {
                try
                {
                    var url = "https://" + issuer + "/integrity/manifest?" + by + "=" + Uri.EscapeDataString(value);
                    var r = Nexus.Http.GetAsync(url).GetAwaiter().GetResult();
                    if (!r.IsSuccessStatusCode) return null;
                    return r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch { return null; }
            }

            public IEnumerable<string> LocalAccounts()
            {
                // the game accounts that played on this computer: the game's own save files,
                // <account>_user_settings.config, as the members' plugin reads them. Not the files
                // under licenses\, which hold the receipt being checked: its account was always
                // "ours", so a copied claim was never seen (2026-11-19 audit).
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var saves = Path.Combine(appData, "Arrowhead", "Helldivers2", "saves");
                    if (Directory.Exists(saves))
                        foreach (var f in Directory.GetFiles(saves, "*_user_settings.config"))
                        {
                            var name = Path.GetFileName(f);
                            var id = name.Substring(0, name.IndexOf('_')).ToLowerInvariant();
                            if (Account.IsMatch(id) && id.Length == 16) found.Add(id);
                        }
                }
                catch { }
                return found;
            }

            public string[] ReadSegments(string issuer, string id, string version, int count, out string receipt)
            {
                receipt = null;
                try
                {
                    var dir = Path.Combine(LicensesRoot, issuer.Replace(':', '_'), "segments", id, version);
                    if (!Directory.Exists(dir)) return null;
                    var rp = Path.Combine(dir, "receipt.txt");
                    if (File.Exists(rp)) receipt = File.ReadAllText(rp).Replace("\r", "");
                    // a sealed (C1) claim is its ciphertext: seg<i>.lua exists only while the game runs
                    var ext = receipt != null && receipt.StartsWith(Integrity.SegmentsHeader2, StringComparison.Ordinal) ? ".enc" : ".lua";
                    var segs = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        var p = Path.Combine(dir, "seg" + (i + 1) + ext);
                        segs[i] = File.Exists(p) ? File.ReadAllText(p) : null;
                    }
                    return segs;
                }
                catch { receipt = null; return null; }
            }

            public bool Post(string url, string json)
            {
                try
                {
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var r = Nexus.Http.PostAsync(url, content).GetAwaiter().GetResult();
                    return r.IsSuccessStatusCode;
                }
                catch { return false; }
            }

            public long LastSent(string key) => Json.Long(Dict(LoadState(), "sent"), key);

            public void MarkSent(string key, long now)
            {
                if (!persist) return;
                var state = LoadState();
                var sent = Dict(state, "sent");
                sent[key] = now; state["sent"] = sent;
                SaveState(state);
            }

            Dictionary<string, object> LoadState()
            {
                try { return File.Exists(StateFile) ? Json.Parse(File.ReadAllText(StateFile)) as Dictionary<string, object> ?? New() : New(); }
                catch { return New(); }
            }
            void SaveState(Dictionary<string, object> s)
            {
                try { File.WriteAllText(StateFile, Json.Write(s, true)); } catch { }
            }
            static Dictionary<string, object> New() => new Dictionary<string, object>();
            static Dictionary<string, object> Dict(Dictionary<string, object> d, string k)
            {
                var v = Json.Obj(d, k);
                return v ?? new Dictionary<string, object>();
            }
        }

        // ---- the entry points ---------------------------------------------------

        /// <summary>The window's background check: scan the licensed mods and send what the throttle
        /// allows. Silent; drops offline; writes no log. Call at start and at the next idle after
        /// the library changes (section 10.4.1). Never throws.</summary>
        public static void RunBackground(Library lib)
        {
            try
            {
                var env = new LiveEnv(persist: true);
                Integrity.Report(Integrity.Scan(Licensed(lib), env), env);
            }
            catch { }
        }

        /// <summary>`hd2qd integrity`: list what this computer would report, send nothing, write
        /// nothing (dry run). The flag --dry-run is accepted for symmetry with the docs.</summary>
        public static void Command(TextWriter o)
        {
            var env = new LiveEnv(persist: false);
            var findings = Integrity.Scan(Licensed(Library.Load()), env);
            o.WriteLine(Integrity.DryRun(findings));
        }
    }
}
