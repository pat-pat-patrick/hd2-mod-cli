using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Hd2Mod
{
    /// <summary>
    /// The integrity check for licensed mods (hd2-mod-license, CONTRACT_sealed_lua section 10).
    /// A licensed mod ships only an inert shell in its public package; the feature Lua is 8
    /// per-account segments the member's licence lets them claim, each carrying that account's
    /// combination so a leaked copy can be traced. This check runs in the manager itself: for a
    /// mod that declares "integrity": 1, it compares the library's files against the creator's
    /// signed manifest and the on-disk segments against their receipt, and when something does
    /// not line up it tells the creator's issuer -- silently, at most once a day per mod and
    /// kind, and never in any log. It is how a shared or altered copy is noticed by the people
    /// who did not bother to change managers.
    ///
    /// It never refuses a deploy and never bans: a report is a lead the creator confirms, not a
    /// verdict. It reads only mods that opt in (the "integrity" declaration, or a Guid in the
    /// roster). The sending, the throttle, the manifest fetch, the accounts and the clock are an
    /// <see cref="IIntegrityEnv"/> seam, so the comparison and the crypto are tested on their own.
    /// </summary>
    public static class Integrity
    {
        // ---- what the check knows about one installed, licensed mod ----

        public sealed class LicensedMod
        {
            public string Guid;                 // manifest Guid, the manifest id for the signed integrity manifest
            public string Issuer;               // hd2_license.json "issuer" host (where the manifest and reports go)
            public string KeyModulusHex;        // hd2_license.json "key": the RSA-2048 modulus, 512 hex digits
            public string Name;                 // manifest Name (as shipped, for the report)
            public string Version;              // the vX.Y.Z token from the manifest Name
            public string SegmentsId;           // hd2_license.json "segments".id, or null when the mod ships no segments
            public int SegmentsCount;           // "segments".count
            public Dictionary<string, string> Files;   // library file path (relative, '/') -> lower-hex SHA-256
        }

        public sealed class Finding
        {
            public string Mod, Issuer, Name, Version, Kind;   // kind: shell | segments | package
            public List<KeyValuePair<string, string>> Files;  // mismatching files: path, hash
            public List<string> Segments;                     // on-disk segment hashes (segments kind)
            public List<string> Accounts;                     // this machine's game accounts
        }

        /// <summary>The side effects the check needs, so the logic above stays pure and testable.
        /// A real build wires this to the issuer over HTTP, to %APPDATA% for the segments, to the
        /// game's save folder for the accounts, and to a small throttle file; the test wires it to
        /// memory.</summary>
        public interface IIntegrityEnv
        {
            long NowUnix();
            /// <summary>The signed integrity manifest text for a mod, fetched at most once a day and
            /// cached; null when there is none or the fetch failed.</summary>
            string Manifest(string issuer, string guid);
            /// <summary>This computer's game account ids (16 hex), for checking a receipt is ours.</summary>
            IEnumerable<string> LocalAccounts();
            /// <summary>The on-disk segment texts seg1..segN and the receipt for one account's claim,
            /// or null segs when the folder is absent. receipt is null when there is no receipt.</summary>
            string[] ReadSegments(string issuer, string id, string version, int count, out string receipt);
            /// <summary>POST the report JSON to the issuer; false on any failure (offline, blocked).
            /// Never throws out: the caller drops a failure silently.</summary>
            bool Post(string url, string json);
            /// <summary>The unix time a (mod, kind) was last reported, or 0.</summary>
            long LastSent(string key);
            void MarkSent(string key, long now);
        }

        public const int ThrottleSeconds = 24 * 3600;   // section 10.4: one of a kind a day

        // ---- the signed-text crypto (mirrors lua/license_check.lua) -------------
        // RSASSA-PKCS1-v1_5 over SHA-256, exponent 65537, over every byte up to and
        // including the newline before the "sig=" line. The key is the modulus only.

        /// <summary>True when `text` ends in a valid "sig=&lt;hex&gt;" line over the bytes before it,
        /// under the RSA-2048 modulus `modulusHex`. A malformed key or signature is just false.</summary>
        public static bool SignatureOk(string text, string modulusHex)
        {
            try
            {
                if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(modulusHex)) return false;
                if (modulusHex.Length != 512 || !IsHex(modulusHex)) return false;    // RSA-2048 only
                int i = text.IndexOf("\nsig=", StringComparison.Ordinal);
                if (i < 0) return false;
                string signed = text.Substring(0, i + 1);                 // up to and including that newline
                string tail = text.Substring(i + 1);                      // "sig=<hex>\n..."
                if (!tail.StartsWith("sig=", StringComparison.Ordinal)) return false;
                string sigHex = tail.Substring(4).TrimEnd('\r', '\n', ' ');
                int nl = sigHex.IndexOf('\n');
                if (nl >= 0) sigHex = sigHex.Substring(0, nl).TrimEnd('\r');
                if (sigHex.Length == 0 || (sigHex.Length % 2) != 0 || !IsHex(sigHex)) return false;
                var p = new RSAParameters { Modulus = FromHex(modulusHex), Exponent = new byte[] { 0x01, 0x00, 0x01 } };
                using (var rsa = RSA.Create())
                {
                    rsa.ImportParameters(p);
                    return rsa.VerifyData(Encoding.UTF8.GetBytes(signed), FromHex(sigHex),
                                          HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch { return false; }
        }

        /// <summary>The field after "key=\n...value\n" convention these texts use: one line
        /// "name=value". Returns null when absent.</summary>
        public static string Field(string text, string name)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith(name + "=", StringComparison.Ordinal))
                    return line.Substring(name.Length + 1);
            }
            return null;
        }

        /// <summary>The signed manifest parsed to version -> (relative path -> sha256), or null when
        /// the text is absent, its signature is bad, or it is not this issuer/mod. Section 7.</summary>
        public static Dictionary<string, Dictionary<string, string>> ParseManifest(
            string text, string issuerHost, string guid, string modulusHex)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (!text.StartsWith("HD2 MOD INTEGRITY 1\n", StringComparison.Ordinal)) return null;
            if (!SignatureOk(text, modulusHex)) return null;
            if (!string.Equals(Field(text, "issuer"), issuerHost, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(Field(text, "mod"), guid, StringComparison.OrdinalIgnoreCase)) return null;
            var byVersion = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (!line.StartsWith("release=", StringComparison.Ordinal)) continue;
                // release=<version> <relative path> <sha256> ; the path may hold spaces ("Ammo Beacon/..."): version to the first space, hash after the last
                var rest = line.Substring("release=".Length);
                int s1 = rest.IndexOf(' ');
                int s2 = rest.LastIndexOf(' ');
                if (s1 <= 0 || s2 <= s1) continue;
                string ver = rest.Substring(0, s1);
                string path = rest.Substring(s1 + 1, s2 - s1 - 1);
                string hash = rest.Substring(s2 + 1);
                if (!IsHex(hash) || hash.Length != 64) continue;
                if (!byVersion.TryGetValue(ver, out var files)) byVersion[ver] = files = new Dictionary<string, string>(StringComparer.Ordinal);
                files[path] = hash.ToLowerInvariant();
            }
            return byVersion;
        }

        // ---- the comparison ------------------------------------------------------

        /// <summary>Every finding for the given licensed mods. No side effects but the env's reads.</summary>
        public static List<Finding> Scan(IEnumerable<LicensedMod> mods, IIntegrityEnv env)
        {
            var findings = new List<Finding>();
            var accounts = (env.LocalAccounts() ?? Enumerable.Empty<string>())
                .Where(a => a != null).Select(a => a.ToLowerInvariant()).Distinct().ToList();
            foreach (var m in mods)
            {
                if (m == null || string.IsNullOrEmpty(m.Guid) || string.IsNullOrEmpty(m.Issuer) || m.Files == null) continue;
                var manifest = ParseManifest(env.Manifest(m.Issuer, m.Guid), m.Issuer, m.Guid, m.KeyModulusHex);

                // shell / package: the library's files against the signed manifest for this version.
                if (manifest != null)
                {
                    if (manifest.TryGetValue(m.Version, out var want))
                    {
                        var diff = new List<KeyValuePair<string, string>>();
                        foreach (var f in m.Files)
                            if (!want.TryGetValue(f.Key, out var h) || !string.Equals(h, f.Value, StringComparison.OrdinalIgnoreCase))
                                diff.Add(new KeyValuePair<string, string>(f.Key, f.Value));
                        foreach (var w in want)        // a file the manifest has that the library dropped
                            if (!m.Files.ContainsKey(w.Key))
                                diff.Add(new KeyValuePair<string, string>(w.Key, ""));
                        if (diff.Count > 0)
                            findings.Add(Make(m, "shell", diff.OrderBy(d => d.Key).Take(64).ToList(), null, accounts));
                    }
                    else
                    {
                        // a package with this mod's Guid but a version the creator never released:
                        // a repackaged/renamed build. Report the files; note the accounts installing it.
                        findings.Add(Make(m, "package", m.Files.OrderBy(d => d.Key).Take(64).ToList(), null, accounts));
                    }
                }

                // segments: the on-disk claim against its receipt (section 5, 6).
                if (!string.IsNullOrEmpty(m.SegmentsId) && m.SegmentsCount > 0)
                {
                    var segProblem = SegmentsProblem(m, env, accounts, out var segHashes);
                    if (segProblem)
                        findings.Add(Make(m, "segments", null, segHashes, accounts));
                }
            }
            return findings;
        }

        /// <summary>True when the on-disk segments do not match a good receipt for an account on
        /// this machine (missing receipt, bad signature, not our account, or a hash that disagrees
        /// -- someone else's segments). segHashes gets the on-disk segment hashes either way.</summary>
        static bool SegmentsProblem(LicensedMod m, IIntegrityEnv env, List<string> accounts, out List<string> segHashes)
        {
            segHashes = new List<string>();
            var segs = env.ReadSegments(m.Issuer, m.SegmentsId, m.Version, m.SegmentsCount, out var receipt);
            if (segs == null) return false;                 // not installed yet: nothing to check, not a problem
            foreach (var s in segs) segHashes.Add(s == null ? "" : Sha256Hex(Encoding.UTF8.GetBytes(s)));
            if (segs.Any(s => s == null)) return true;      // an incomplete claim on disk
            if (string.IsNullOrEmpty(receipt)) return true; // segments with no receipt
            if (!SignatureOk(receipt, m.KeyModulusHex)) return true;
            if (!string.Equals(Field(receipt, "issuer"), m.Issuer, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(Field(receipt, "id"), m.SegmentsId, StringComparison.Ordinal)) return true;
            if (!string.Equals(Field(receipt, "version"), m.Version, StringComparison.Ordinal)) return true;
            var acct = (Field(receipt, "account") ?? "").ToLowerInvariant();
            if (!accounts.Contains(acct)) return true;      // a receipt for an account not on this machine
            var want = Field(receipt, "segments_hash") ?? "";
            var got = Sha256Hex(Encoding.UTF8.GetBytes(string.Concat(segs)));
            if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static Finding Make(LicensedMod m, string kind, List<KeyValuePair<string, string>> files,
                            List<string> segments, List<string> accounts)
        {
            return new Finding
            {
                Mod = m.Guid, Issuer = m.Issuer, Name = m.Name, Version = m.Version, Kind = kind,
                Files = files, Segments = segments, Accounts = accounts,
            };
        }

        // ---- reporting: silent, throttled, offline-dropped, never logged --------

        /// <summary>Send the findings the throttle still allows. Each (mod, kind) goes at most once
        /// a day; a send that fails is dropped with no retry and no trace. Returns how many were
        /// sent (for the tests, not for any log).</summary>
        public static int Report(IEnumerable<Finding> findings, IIntegrityEnv env)
        {
            int sent = 0;
            long now = env.NowUnix();
            foreach (var f in findings)
            {
                string key = f.Mod + ":" + f.Kind;
                long last = env.LastSent(key);
                if (last != 0 && now - last < ThrottleSeconds) continue;   // 0 = never sent -> allowed
                var url = "https://" + f.Issuer + "/integrity/report";
                bool ok;
                try { ok = env.Post(url, ReportJson(f)); } catch { ok = false; }
                if (ok) { env.MarkSent(key, now); sent++; }
            }
            return sent;
        }

        /// <summary>The report body (section 8). Only this mod's hashes, version and the machine's
        /// game accounts; nothing about other mods, files or the computer.</summary>
        public static string ReportJson(Finding f)
        {
            var d = new Dictionary<string, object>
            {
                ["mod"] = f.Mod, ["issuer"] = f.Issuer, ["name"] = f.Name ?? "",
                ["version"] = f.Version ?? "", ["kind"] = f.Kind, ["manager"] = StateDoc.Version,
            };
            if (f.Files != null)
                d["files"] = f.Files.Select(p => (object)new Dictionary<string, object> { ["path"] = p.Key, ["hash"] = p.Value }).ToList();
            if (f.Segments != null) d["segments"] = f.Segments.Cast<object>().ToList();
            if (f.Accounts != null) d["accounts"] = f.Accounts.Cast<object>().ToList();
            return Json.Write(d);
        }

        /// <summary>What the machine would report, for `hd2qd integrity --dry-run`: listed, not
        /// sent, not written. Section 10.4.</summary>
        public static string DryRun(List<Finding> findings)
        {
            if (findings.Count == 0) return "integrity: nothing to report (every licensed mod matches its manifest and receipt).";
            var sb = new StringBuilder();
            sb.AppendLine("integrity: " + findings.Count + " item(s) this computer would report (dry run -- nothing was sent):");
            foreach (var f in findings)
            {
                sb.AppendLine("  " + f.Kind + "  " + (f.Name ?? f.Mod) + "  " + f.Version + "  (" + f.Mod + " -> " + f.Issuer + ")");
                if (f.Files != null)
                    foreach (var p in f.Files.Take(12))
                        sb.AppendLine("      " + (p.Value.Length == 0 ? "[missing] " : "") + p.Key);
                if (f.Segments != null)
                    sb.AppendLine("      segments: " + string.Join(" ", f.Segments.Select(h => h.Length >= 8 ? h.Substring(0, 8) : h)));
                if (f.Accounts != null && f.Accounts.Count > 0)
                    sb.AppendLine("      accounts: " + string.Join(" ", f.Accounts));
            }
            sb.Append("These are leads, not verdicts. Nothing is banned; the creator confirms first.");
            return sb.ToString();
        }

        // ---- helpers ------------------------------------------------------------

        public static string Sha256Hex(byte[] data)
        {
            using (var h = SHA256.Create())
                return ToHex(h.ComputeHash(data));
        }

        static bool IsHex(string s)
        {
            foreach (var c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return s.Length > 0;
        }

        static byte[] FromHex(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b;
        }

        static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
