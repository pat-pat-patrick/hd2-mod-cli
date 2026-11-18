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
        const string Acct = "011000010fbb262c";
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
            public string Manifest(string issuer, string guid) => ManifestText;
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
