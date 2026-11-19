using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>The licensed-mod integrity check (core/Integrity.cs, CONTRACT_sealed_lua section 10):
    /// it verifies the creator's signed manifest and the segment receipt with a real RSA-2048 key,
    /// flags a shell/segments/package mismatch, and reports silently at most once a day per mod and
    /// kind, dropping an offline send. The live wiring (files, %APPDATA%, HTTP) is the IIntegrityEnv
    /// seam; here it is in memory, so the crypto and the comparison are tested on their own.</summary>
    public class IntegrityTests
    {
        const string Guid = "1f5366a3-557d-40ad-a821-553220c8588d";
        const string Issuer = "lic.test";
        const string Version = "v0.35.0";
        const string Acct = "0f1e2d3c4b5a6978";   // made up (never a real player's id: this repository is public)
        const string Other = "aaaa1111bbbb2222";

        static readonly RSA Key = MakeKey();
        static readonly string Modulus = ToHex(Key.ExportParameters(false).Modulus);

        static RSA MakeKey() => new RSACryptoServiceProvider(2048);   // a real RSA-2048 key (RSA.Create() defaults to 1024 on net48)

        static string Sign(string text)   // the issuer's way: RSASSA-PKCS1 SHA-256 over the bytes, then sig=
        {
            var sig = Key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return text + "sig=" + ToHex(sig) + "\n";
        }

        static string ToHex(byte[] b) { var s = new StringBuilder(); foreach (var x in b) s.Append(x.ToString("x2")); return s.ToString(); }
        static string Sha(string s) => Integrity.Sha256Hex(Encoding.UTF8.GetBytes(s));

        // a two-file shell at Version
        static readonly (string Path, string Body)[] Shell =
        {
            ("manifest.json", "{\"Name\":\"Super Beacon v0.35.0\"}"),
            ("Ammo Beacon/9ba626afa44a3aa3.patch_0", "the shell bytes"),
        };

        static string Manifest(IEnumerable<(string Path, string Body)> files, string version = Version, string mod = Guid)
        {
            var t = "HD2 MOD INTEGRITY 1\nissuer=" + Issuer + "\nmod=" + mod + "\n";
            foreach (var f in files) t += "release=" + version + " " + f.Path + " " + Sha(f.Body) + "\n";
            t += "issued=1000\n";
            return Sign(t);
        }

        static Integrity.LicensedMod Mod(IEnumerable<(string Path, string Body)> files, int segCount = 0)
        {
            return new Integrity.LicensedMod
            {
                Guid = Guid, Issuer = Issuer, KeyModulusHex = Modulus, Name = "Super Beacon " + Version,
                Version = Version, SegmentsId = segCount > 0 ? "super-beacon" : null, SegmentsCount = segCount,
                Files = files.ToDictionary(f => f.Path, f => Sha(f.Body), StringComparer.Ordinal),
            };
        }

        static string Receipt(string[] segs, string account = Acct, string version = Version, string id = "super-beacon")
        {
            var t = "HD2 MOD SEGMENTS 1\nissuer=" + Issuer + "\nid=" + id + "\nversion=" + version
                + "\naccount=" + account + "\nsegments_hash=" + Sha(string.Concat(segs)) + "\nissued=1000\n";
            return Sign(t);
        }

        /// <summary>A sealed (C1) claim's receipt: sealed_hash over the ciphertext blobs on disk.</summary>
        static string Receipt2(string[] blobs, string account = Acct, string version = Version, string id = "super-beacon")
        {
            var t = "HD2 MOD SEGMENTS 2\nissuer=" + Issuer + "\nid=" + id + "\nversion=" + version + "\naccount=" + account
                + "\nalg=A256CBC\nsegments_hash=" + Sha("plain") + "\nsealed_hash=" + Sha(string.Concat(blobs)) + "\nissued=1000\n";
            return Sign(t);
        }

        sealed class FakeEnv : Integrity.IIntegrityEnv
        {
            public long Now = 2000;
            public string ManifestText;
            public List<string> Accounts = new List<string> { Acct };
            public string[] Segs; public string ReceiptText;
            public bool Online = true;
            public readonly List<string> Posted = new List<string>();
            public readonly Dictionary<string, long> Sent = new Dictionary<string, long>();

            public long NowUnix() => Now;
            public string Manifest(Integrity.LicensedMod mod) => ManifestText;
            public IEnumerable<string> LocalAccounts() => Accounts;
            public string[] ReadSegments(string issuer, string id, string version, int count, out string receipt)
            { receipt = ReceiptText; return Segs; }
            public bool Post(string url, string json) { if (!Online) return false; Posted.Add(json); return true; }
            public long LastSent(string key) => Sent.TryGetValue(key, out var v) ? v : 0;
            public void MarkSent(string key, long now) => Sent[key] = now;
        }

        [Fact]
        public void SignatureVerifiesAndRejectsTampering()
        {
            var text = Manifest(Shell);
            Assert.True(Integrity.SignatureOk(text, Modulus));
            Assert.False(Integrity.SignatureOk(text.Replace("issuer=lic.test", "issuer=lic.evil"), Modulus));  // tampered signed body
            Assert.False(Integrity.SignatureOk(text.Substring(0, text.Length - 5) + "00\n", Modulus));   // mangled sig
            Assert.False(Integrity.SignatureOk(text, new string('0', 512)));                             // wrong key
            Assert.False(Integrity.SignatureOk(text, "abc"));                                            // not RSA-2048
        }

        [Fact]
        public void AMatchingModReportsNothing()
        {
            var env = new FakeEnv { ManifestText = Manifest(Shell), Segs = new[] { "s1", "s2" } };
            env.ReceiptText = Receipt(env.Segs);
            var mod = Mod(Shell, segCount: 2);
            var findings = Integrity.Scan(new[] { mod }, env);
            Assert.Empty(findings);
        }

        [Fact]
        public void AnAlteredShellFileIsReported()
        {
            var env = new FakeEnv { ManifestText = Manifest(Shell) };
            var tampered = new[] { Shell[0], ("Ammo Beacon/9ba626afa44a3aa3.patch_0", "ALTERED") };
            var findings = Integrity.Scan(new[] { Mod(tampered) }, env);
            Assert.Single(findings);
            Assert.Equal("shell", findings[0].Kind);
            Assert.Contains(findings[0].Files, f => f.Key == "Ammo Beacon/9ba626afa44a3aa3.patch_0");
        }

        [Fact]
        public void AnUnknownVersionOfTheSameGuidIsAPackage()
        {
            var env = new FakeEnv { ManifestText = Manifest(Shell, version: "v9.9.9") };   // manifest has no v0.35.0
            var findings = Integrity.Scan(new[] { Mod(Shell) }, env);
            Assert.Single(findings);
            Assert.Equal("package", findings[0].Kind);
        }

        [Fact]
        public void SomeoneElsesSegmentsAreReported()
        {
            // a good shell, but the on-disk segments carry a receipt for an account not on this machine
            var env = new FakeEnv { ManifestText = Manifest(Shell), Accounts = new List<string> { Other } };
            env.Segs = new[] { "s1", "s2" };
            env.ReceiptText = Receipt(env.Segs, account: Acct);          // Acct is not in Accounts (= Other)
            var findings = Integrity.Scan(new[] { Mod(Shell, segCount: 2) }, env);
            Assert.Single(findings);
            Assert.Equal("segments", findings[0].Kind);
            Assert.Equal(new[] { Sha("s1"), Sha("s2") }, findings[0].Segments.ToArray());
        }

        [Fact]
        public void SegmentsThatDoNotMatchTheirReceiptAreReported()
        {
            var env = new FakeEnv { ManifestText = Manifest(Shell) };
            env.Segs = new[] { "s1", "DIFFERENT" };
            env.ReceiptText = Receipt(new[] { "s1", "s2" });            // hash is of s1+s2, not s1+DIFFERENT
            var findings = Integrity.Scan(new[] { Mod(Shell, segCount: 2) }, env);
            Assert.Single(findings);
            Assert.Equal("segments", findings[0].Kind);
        }

        [Fact]
        public void NoSegmentsOnDiskIsNotAProblem()
        {
            var env = new FakeEnv { ManifestText = Manifest(Shell), Segs = null };
            var findings = Integrity.Scan(new[] { Mod(Shell, segCount: 2) }, env);
            Assert.Empty(findings);
        }

        /// <summary>The creator's choice is the issuer's manifest: with none, even someone else's
        /// segments on disk are not checked or sent (2026-11-19).</summary>
        [Fact]
        public void NothingIsCheckedWhenTheIssuerPublishesNoManifest()
        {
            var segs = new[] { "s1", "s2" };
            var env = new FakeEnv { ManifestText = null, Segs = segs, ReceiptText = Receipt(segs, account: Other) };
            Assert.Empty(Integrity.Scan(new[] { Mod(Shell, segCount: 2) }, env));
            env.ManifestText = Manifest(Shell);                       // the same files, the check turned on
            Assert.Single(Integrity.Scan(new[] { Mod(Shell, segCount: 2) }, env), f => f.Kind == "segments");
        }

        /// <summary>A licensed mod is checked whatever its hd2_license.json says of "integrity":
        /// deleting that line took it out of the check while the mod still worked (the user,
        /// 2026-11-19). A folder with no issuer or key is no licensed mod.</summary>
        [Fact]
        public void TheIntegrityLineInTheModDoesNotDecide()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hd2qd-integrity-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var lib = new Library();
                void Add(string folder, string license)
                {
                    var dir = System.IO.Path.Combine(root, folder);
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "manifest.json"), "{\"Guid\":\"" + Guid + "\",\"Name\":\"Super Beacon v1.0.0\"}");
                    if (license != null) System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "hd2_license.json"), license);
                    lib.Mods.Add(new Mod { Id = folder, Name = folder, Path = dir });
                }
                string Lic(string extra) => "{\"protocol\":1,\"issuer\":\"" + Issuer + "\",\"key\":\"" + Modulus + "\"" + extra + "}";
                Add("on", Lic(",\"integrity\":1"));
                Add("deleted", Lic(""));                       // the line taken out
                Add("zero", Lic(",\"integrity\":0"));          // or set to 0
                Add("nokey", "{\"protocol\":1,\"issuer\":\"" + Issuer + "\"}");
                Add("plain", null);                            // no licence at all
                var got = IntegrityWiring.Licensed(lib).Count;
                Assert.Equal(3, got);
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
        }

        /// <summary>A member's own sealed (C1) claim holds only ciphertext between sessions: it is
        /// checked against sealed_hash and is not reported. Before (2026-11-19 audit) every honest C1
        /// member read as "incomplete", and a copied claim was never "someone else's".</summary>
        [Fact]
        public void OurSealedClaimIsNotReportedAndSomeoneElsesIsWithItsReceipt()
        {
            var blobs = new[] { "QUJD", "REVG" };
            var env = new FakeEnv { ManifestText = Manifest(Shell), Segs = blobs, ReceiptText = Receipt2(blobs) };
            Assert.Empty(Integrity.Scan(new[] { Mod(Shell, segCount: 2) }, env));
            env.ReceiptText = Receipt2(blobs, account: Other);           // a claim made for another account
            var f = Assert.Single(Integrity.Scan(new[] { Mod(Shell, segCount: 2) }, env));
            Assert.Equal("segments", f.Kind);
            Assert.Equal(env.ReceiptText, f.Receipt);                    // the issuer can verify whose claim it is
            Assert.Contains("\"receipt\":", Integrity.ReportJson(f));
            env.ReceiptText = Receipt2(new[] { "QUJD", "XXXX" });        // ciphertext that is not what was signed
            Assert.Single(Integrity.Scan(new[] { Mod(Shell, segCount: 2) }, env), x => x.Kind == "segments");
        }

        /// <summary>The manifest is found by the licence's segments id; one signed for another Guid
        /// than manifest.json names means the Guid was edited, which used to make the lookup miss
        /// and the check go quiet (2026-11-19 audit).</summary>
        [Fact]
        public void AnEditedGuidIsAPackage()
        {
            var env = new FakeEnv { ManifestText = Manifest(Shell) };
            var mod = Mod(Shell, segCount: 2);
            mod.Guid = "00000000-0000-0000-0000-000000000000";
            Assert.Single(Integrity.Scan(new[] { mod }, env), f => f.Kind == "package");
        }

        /// <summary>A last-sent time still to come does not silence a mod (2026-11-19 audit).</summary>
        [Fact]
        public void ASentTimeStillToComeDoesNotThrottle()
        {
            var env = new FakeEnv { ManifestText = Manifest(Shell, version: "v9.9.9") };
            var findings = Integrity.Scan(new[] { Mod(Shell) }, env);
            env.Sent[Guid + ":package"] = env.Now + 999999;
            Assert.Equal(1, Integrity.Report(findings, env));
            Assert.Equal(0, Integrity.Report(findings, env));             // and the real throttle still holds
        }

        /// <summary>The issuer and key are read as the members' plugin reads them: a key padded with
        /// zeros or spaces, which still claims there, is still checked here (2026-11-19 audit).</summary>
        [Fact]
        public void APaddedKeyIsStillALicensedMod()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hd2qd-integrity-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var dir = System.IO.Path.Combine(root, "padded");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "manifest.json"), "{\"Guid\":\"" + Guid + "\",\"Name\":\"Super Beacon v1.0.0\"}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "hd2_license.json"),
                    "{\"protocol\":1,\"issuer\":\" LIC.test \",\"key\":\" 00" + Modulus.ToUpperInvariant() + " \"}");
                var lib = new Library();
                lib.Mods.Add(new Mod { Id = "padded", Name = "padded", Path = dir });
                var m = Assert.Single(IntegrityWiring.Licensed(lib));
                Assert.Equal(Issuer, m.Issuer);
                Assert.Equal(Modulus, m.KeyModulusHex);
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
        }

        /// <summary>The live environment: the accounts are the game's own save files, never the
        /// receipt being checked; a sealed claim is read as its ciphertext.</summary>
        [Fact]
        public void TheLiveEnvironmentReadsSavesAndCiphertext()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hd2qd-integrity-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var hd2 = System.IO.Path.Combine(root, "Arrowhead", "Helldivers2");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(hd2, "saves"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(hd2, "saves", Acct + "_user_settings.config"), "x");
                var dir = System.IO.Path.Combine(hd2, "licenses", Issuer, "segments", "super-beacon", Version);
                System.IO.Directory.CreateDirectory(dir);
                var blobs = new[] { "QUJD", "REVG" };
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "receipt.txt"), Receipt2(blobs, account: Other));
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "seg1.enc"), blobs[0]);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "seg2.enc"), blobs[1]);
                var env = new IntegrityWiring.LiveEnv(false, root);
                Assert.Equal(new[] { Acct }, env.LocalAccounts().ToArray());  // not Other, though the receipt names it
                // the game drops leading zeros from the name: 0a1b2c3d4e5f6071 is saved as a1b2c3d4e5f6071_...
                System.IO.File.WriteAllText(System.IO.Path.Combine(hd2, "saves", "a1b2c3d4e5f6071_user_settings.config"), "x");
                Assert.Contains("0a1b2c3d4e5f6071", env.LocalAccounts());
                var segs = env.ReadSegments(Issuer, "super-beacon", Version, 2, out var receipt);
                Assert.Equal(blobs, segs);
                Assert.StartsWith("HD2 MOD SEGMENTS 2", receipt);
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void ABadManifestSignatureIsTreatedAsNoManifest()
        {
            var env = new FakeEnv { ManifestText = Manifest(Shell).Replace("issued=1000", "issued=1001") };  // breaks the sig
            var findings = Integrity.Scan(new[] { Mod(Shell) }, env);
            Assert.Empty(findings);   // no manifest to compare against -> no shell/package finding
        }

        [Fact]
        public void TheReportCarriesOnlyThisModAndNoOtherData()
        {
            var env = new FakeEnv { ManifestText = Manifest(Shell, version: "v9.9.9") };
            var f = Integrity.Scan(new[] { Mod(Shell) }, env)[0];
            var json = Integrity.ReportJson(f);
            var d = (Dictionary<string, object>)Json.Parse(json);
            Assert.Equal(Guid, d["mod"]);
            Assert.Equal(Issuer, d["issuer"]);
            Assert.Equal("package", d["kind"]);
            Assert.Equal(StateDoc.Version, d["manager"]);
            Assert.False(d.ContainsKey("computer"));
            Assert.False(d.ContainsKey("other_mods"));
        }

        [Fact]
        public void ReportSendsOncePerDayAndDropsOffline()
        {
            var env = new FakeEnv { ManifestText = Manifest(Shell, version: "v9.9.9") };
            var findings = Integrity.Scan(new[] { Mod(Shell) }, env);

            // offline: nothing is sent, nothing is marked, so it is retried next time
            env.Online = false;
            Assert.Equal(0, Integrity.Report(findings, env));
            Assert.Empty(env.Sent);

            // online: sent once and marked
            env.Online = true;
            Assert.Equal(1, Integrity.Report(findings, env));
            Assert.Single(env.Posted);

            // within a day: the throttle holds it back
            env.Now += 3600;
            Assert.Equal(0, Integrity.Report(findings, env));
            Assert.Single(env.Posted);

            // a day later: sent again
            env.Now += Integrity.ThrottleSeconds;
            Assert.Equal(1, Integrity.Report(findings, env));
            Assert.Equal(2, env.Posted.Count);
        }

        [Fact]
        public void DryRunListsFindingsAndSaysNothingWasSent()
        {
            var env = new FakeEnv { ManifestText = Manifest(Shell, version: "v9.9.9") };
            var text = Integrity.DryRun(Integrity.Scan(new[] { Mod(Shell) }, env));
            Assert.Contains("package", text);
            Assert.Contains("nothing was sent", text);
            Assert.Equal("integrity: nothing to report (every licensed mod matches its manifest and receipt).",
                         Integrity.DryRun(new List<Integrity.Finding>()));
        }

        /// <summary>(11-19) The version comes from hd2_license.json "segments".version when the mod
        /// declares it, since a published package's manifest Name carries no version; else from
        /// the Name. A malformed declaration (it names a folder) skips the mod, never falls back.</summary>
        [Fact]
        public void TheVersionIsTheDeclaredOneElseTheName()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hd2qd-integrity-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var lib = new Library();
                void Add(string folder, string name, string versionJson)
                {
                    var dir = System.IO.Path.Combine(root, folder);
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "manifest.json"), "{\"Guid\":\"" + Guid + "\",\"Name\":\"" + name + "\"}");
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "hd2_license.json"),
                        "{\"protocol\":1,\"issuer\":\"" + Issuer + "\",\"key\":\"" + Modulus + "\",\"integrity\":1,"
                        + "\"segments\":{\"id\":\"super-beacon\",\"count\":8" + (versionJson == null ? "" : ",\"version\":" + versionJson) + "}}");
                    lib.Mods.Add(new Mod { Id = folder, Name = name, Path = dir });
                }
                Add("pub", "Super Beacon", "\"v0.35.0\"");                  // published: only the declaration has it
                Add("both", "Super Beacon v9.9.9", "\"v1.0.1\"");            // the declaration wins
                Add("named", "Super Beacon v1.2.3", null);                   // no declaration: the Name
                Add("none", "Super Beacon", null);                           // neither: not checked
                Add("bad", "Super Beacon v2.0.0", "\"v2.0.0/../x\"");        // malformed: not checked, no fallback
                Add("notext", "Super Beacon v2.0.0", "35");
                var got = IntegrityWiring.Licensed(lib).Select(m => m.Version).ToList();
                Assert.Equal(new[] { "v0.35.0", "v1.0.1", "v1.2.3" }, got);
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
        }
    }
}
