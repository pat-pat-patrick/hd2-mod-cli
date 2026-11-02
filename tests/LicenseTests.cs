using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>The license plugin (plugins\License) through the manager's loader, against an
    /// issuer on this computer that answers as the protocol says.</summary>
    public class LicensePlugin : Fixture
    {
        readonly string appData;
        readonly Issuer issuer;
        readonly Dictionary<string, object> keys;

        public LicensePlugin()
        {
            appData = Path.Combine(Root, "roaming");
            Licenses.AppDataOverride = appData;
            Licenses.DllPath = null;
            keys = (Dictionary<string, object>)Json.Parse(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "license", "test_keys.json")));
            issuer = new Issuer(this);
            SetClock(() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        public override void Dispose()
        {
            issuer.Dispose();
            Licenses.DllPath = null;
            SetClock(() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Licenses.AppDataOverride = null;
            base.Dispose();
        }

        static void SetClock(Func<long> clock) => Licenses.PluginType.GetField("Clock").SetValue(null, clock);

        string KeyN(string name) => Json.Str(Json.Obj(keys, name), "n");

        /// <summary>A mod folder in the library with an hd2_license.json for our issuer.</summary>
        string LicensedMod(string name = "Licensed", string key = "a", string issuerName = null, string text = null)
        {
            var folder = Path.Combine(Root, "library", name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "hd2_license.json"), text ?? Json.Write(new Dictionary<string, object>
            {
                ["protocol"] = 1L, ["issuer"] = issuerName ?? issuer.Name, ["name"] = "Test Author", ["key"] = KeyN(key),
            }));
            return folder;
        }

        void Account(string id, int minutesAgo = 0)
        {
            var saves = Path.Combine(appData, "Arrowhead", "Helldivers2", "saves");
            Directory.CreateDirectory(saves);
            var f = Path.Combine(saves, id + "_user_settings.config");
            File.WriteAllText(f, "language = \"us\"");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-minutesAgo));
        }

        string LicenseFile => Path.Combine(appData, "Arrowhead", "Helldivers2", "licenses", issuer.Name.Replace(':', '_') + ".txt");

        Dictionary<string, object> Decl(string folder) => Licenses.Declaration(folder);

        string SignIn(string folder)
        {
            var begin = Licenses.BeginSignIn(Decl(folder));
            Assert.Null(Json.Str(begin, "error"));
            issuer.Opened(Json.Str(begin, "url"));
            var r = Licenses.FinishSignIn(Decl(folder), Json.Str(begin, "nonce"), TimeSpan.FromSeconds(10), CancellationToken.None);
            return Json.Str(r, "error") ?? "ok";
        }

        // -- loading -----------------------------------------------------------------------

        [Fact]
        public void The_plugin_beside_the_program_is_loaded()
        {
            Assert.True(Licenses.Present);
            Assert.Null(Licenses.Why);
        }

        [Fact]
        public void Without_the_plugin_a_licensed_mod_is_only_marked()
        {
            Licenses.DllPath = Path.Combine(Root, "nowhere", "HD2QD.License.dll");
            Assert.False(Licenses.Present);
            Assert.Equal("missing", Licenses.Why);
            Assert.Equal("no_plugin", Json.Str(Licenses.StateOf(LicensedMod()), "state"));
            Directory.CreateDirectory(Path.Combine(Root, "library", "Free"));
            Assert.Null(Licenses.StateOf(Path.Combine(Root, "library", "Free")));
            var e = Assert.Throws<ModError>(() => Licenses.Accounts());
            Assert.Equal("license_plugin", e.Kind);
        }

        [Fact]
        public void A_file_that_is_not_the_plugin_is_refused_as_broken()
        {
            var fake = Path.Combine(Root, "HD2QD.License.dll");
            File.WriteAllText(fake, "not an assembly");
            Licenses.DllPath = fake;
            Assert.False(Licenses.Present);
            Assert.Equal("broken", Licenses.Why);
        }

        // -- declarations and accounts -----------------------------------------------------

        [Fact]
        public void A_declaration_is_read_and_a_wrong_one_is_said_as_wrong()
        {
            var d = Decl(LicensedMod());
            Assert.Equal(issuer.Name, Json.Str(d, "issuer"));
            Assert.Equal("Test Author", Json.Str(d, "name"));
            Assert.Equal(KeyN("a"), Json.Str(d, "key"));
            Assert.Equal("bad_declaration", Json.Str(Decl(LicensedMod("Short", text: "{\"protocol\":1,\"issuer\":\"x.test\",\"key\":\"abcd\"}")), "error"));
            Assert.Equal("bad_declaration", Json.Str(Decl(LicensedMod("V2", text: "{\"protocol\":2,\"issuer\":\"x.test\",\"key\":\"" + KeyN("a") + "\"}")), "error"));
            Assert.Equal("bad_declaration", Json.Str(Decl(LicensedMod("Path", issuerName: "..\\x")), "error"));
            Assert.Equal("bad_declaration", Json.Str(Decl(LicensedMod("Port", issuerName: "evil.test:8080")), "error"));
            Assert.Equal("bad_declaration", Json.Str(Decl(LicensedMod("Broken", text: "{")), "error"));
            Assert.Equal("bad_declaration", Licenses.StateOf(Path.Combine(Root, "library", "Broken"))["state"]);
        }

        [Fact]
        public void Game_accounts_come_from_the_save_names_most_recent_first()
        {
            Assert.Empty(Licenses.Accounts());
            Account(A, 60);
            Account(B, 1);
            var saves = Path.Combine(appData, "Arrowhead", "Helldivers2", "saves");
            File.WriteAllText(Path.Combine(saves, "user_settings.config"), "");
            File.WriteAllText(Path.Combine(saves, "nothex0000000000_user_settings.config"), "");
            File.WriteAllText(Path.Combine(saves, A + "_user_settings.config.old"), "");
            Assert.Equal(new[] { B, A }, Licenses.Accounts());
        }

        // -- signing in ----------------------------------------------------------------------

        [Fact]
        public void Signing_in_writes_the_license_where_the_mod_reads_it()
        {
            var mod = LicensedMod();
            Assert.Equal("no_account", Json.Str(Licenses.StateOf(mod), "state"));
            Assert.Equal("no_account", Json.Str(Licenses.BeginSignIn(Decl(mod)), "error"));
            Account(A);
            Assert.Equal("none", Json.Str(Licenses.StateOf(mod), "state"));

            issuer.PendingClaims = 2;
            Assert.Equal("ok", SignIn(mod));
            Assert.Equal("/login?n=" + issuer.Nonce + "&acct=" + A, issuer.OpenedPath);
            Assert.True(File.Exists(LicenseFile));
            Assert.StartsWith("HD2 MOD LICENSE 1\nissuer=" + issuer.Name + "\n", File.ReadAllText(LicenseFile));
            var st = Licenses.StateOf(mod);
            Assert.Equal("licensed", Json.Str(st, "state"));
            Assert.False(Json.Bool(st, "renew_due"));
            Assert.Equal(new object[] { A }, Json.List(st, "accounts"));

            var store = File.ReadAllText(Path.Combine(Store.Home, "licenses.json"));
            Assert.DoesNotContain(issuer.RenewToken, store);
            Assert.Contains(KeyN("a"), store);
        }

        [Fact]
        public void The_issuers_refusals_come_back_by_name()
        {
            var mod = LicensedMod();
            Account(A);
            foreach (var why in new[] { "not_member", "account_limit", "denied" })
            {
                issuer.ClaimError = why;
                Assert.Equal(why, SignIn(mod));
            }
            Assert.False(File.Exists(LicenseFile));
        }

        [Fact]
        public void A_license_not_signed_by_the_declared_key_is_not_written()
        {
            var mod = LicensedMod();
            Account(A);
            issuer.SignWith = "b";
            Assert.Equal("bad_license", SignIn(mod));
            Assert.False(File.Exists(LicenseFile));
            Assert.False(File.Exists(Path.Combine(Store.Home, "licenses.json")));
        }

        [Fact]
        public void An_issuer_that_does_not_answer_is_a_network_error()
        {
            var mod = LicensedMod();
            Account(A);
            var begin = Licenses.BeginSignIn(Decl(mod));
            issuer.Dispose();
            var r = Licenses.FinishSignIn(Decl(mod), Json.Str(begin, "nonce"), TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("network", Json.Str(r, "error"));
        }

        [Fact]
        public void Waiting_can_be_cancelled_and_runs_out()
        {
            var mod = LicensedMod();
            Account(A);
            issuer.PendingClaims = int.MaxValue;
            var begin = Licenses.BeginSignIn(Decl(mod));
            issuer.Opened(Json.Str(begin, "url"));
            using (var cancel = new CancellationTokenSource(300))
                Assert.Equal("cancelled", Json.Str(Licenses.FinishSignIn(Decl(mod), Json.Str(begin, "nonce"), TimeSpan.FromSeconds(30), cancel.Token), "error"));
            Assert.Equal("timeout", Json.Str(Licenses.FinishSignIn(Decl(mod), Json.Str(begin, "nonce"), TimeSpan.Zero, CancellationToken.None), "error"));
        }

        // -- states --------------------------------------------------------------------------

        [Fact]
        public void Expiry_another_account_and_a_foreign_license_are_told_apart()
        {
            var mod = LicensedMod();
            Account(A);
            Assert.Equal("ok", SignIn(mod));
            long expires = Json.Long(Licenses.StateOf(mod), "expires");

            SetClock(() => expires - issuer.Days * 86400 / 2 + 1);
            Assert.True(Json.Bool(Licenses.StateOf(mod), "renew_due"));
            SetClock(() => expires);
            Assert.Equal("expired", Json.Str(Licenses.StateOf(mod), "state"));
            SetClock(() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            File.Delete(Path.Combine(appData, "Arrowhead", "Helldivers2", "saves", A + "_user_settings.config"));
            Account(B);
            Assert.Equal("other_account", Json.Str(Licenses.StateOf(mod), "state"));
            Account(A);

            File.WriteAllText(LicenseFile, issuer.License("b", new[] { A }, 30));
            Assert.Equal("invalid", Json.Str(Licenses.StateOf(mod), "state"));
        }

        [Fact]
        public void A_mod_declaring_another_key_for_a_signed_in_issuer_is_flagged()
        {
            var mod = LicensedMod();
            Account(A);
            Assert.Equal("ok", SignIn(mod));
            var other = LicensedMod("Other", key: "b");
            Assert.Equal("key_changed", Json.Str(Licenses.StateOf(other), "state"));
            Assert.Equal("key_changed", Json.Str(Licenses.Renew(Decl(other), true), "error"));
            Assert.Equal("licensed", Json.Str(Licenses.StateOf(mod), "state"));
        }

        // -- renewing and signing out ---------------------------------------------------------

        [Fact]
        public void Renewal_waits_for_half_time_and_follows_the_issuer()
        {
            var mod = LicensedMod();
            Account(A);
            Assert.False(Json.Bool(Licenses.Renew(Decl(mod), false), "renewed"));   // never signed in
            Assert.Equal("ok", SignIn(mod));
            int calls = issuer.RenewCalls;
            Assert.False(Json.Bool(Licenses.Renew(Decl(mod), false), "renewed"));
            Assert.Equal(calls, issuer.RenewCalls);

            var r = Licenses.Renew(Decl(mod), true);
            Assert.True(Json.Bool(r, "renewed"));
            Assert.Equal(calls + 1, issuer.RenewCalls);
            Assert.Equal(issuer.RenewToken, issuer.LastRenewToken);

            issuer.RenewError = "not_member";
            Assert.Equal("not_member", Json.Str(Licenses.Renew(Decl(mod), true), "error"));
            Assert.Equal("licensed", Json.Str(Licenses.StateOf(mod), "state"));      // the old one runs out on its own
        }

        [Fact]
        public void Signing_out_removes_the_license_and_the_token()
        {
            var mod = LicensedMod();
            Account(A);
            Assert.Equal("ok", SignIn(mod));
            Licenses.Forget(issuer.Name);
            Assert.False(File.Exists(LicenseFile));
            Assert.Equal("none", Json.Str(Licenses.StateOf(mod), "state"));
            Assert.False(Json.Bool(Licenses.Renew(Decl(mod), true), "renewed"));
            // and another key is welcome again
            Assert.Equal("none", Json.Str(Licenses.StateOf(LicensedMod("Other", key: "b")), "state"));
        }

        [Fact]
        public void The_state_carries_each_licensed_mods_standing()
        {
            Account(A);
            var mod = LicensedMod();
            var lib = Library.Load();
            lib.Mods.Add(Ops.NewMod(mod, "Licensed"));
            lib.Save();
            var doc = StateDoc.Build(Library.Load(), Game, false);
            var entry = Json.List(doc, "mods").Cast<Dictionary<string, object>>().Single(m => Json.Str(m, "name") == "Licensed");
            Assert.Equal("none", Json.Str(Json.Obj(entry, "license"), "state"));
        }

        // -- an issuer on this computer --------------------------------------------------------

        /// <summary>Answers /claim and /renew as a Worker would, signing with a test key. The
        /// "browser" is the test: Opened() reads the nonce from the login address.</summary>
        sealed class Issuer : IDisposable
        {
            readonly LicensePlugin t;
            readonly TcpListener listener;
            readonly Thread thread;
            volatile bool stopped;
            public readonly string Name;
            public string Nonce, OpenedPath, ClaimError, RenewError, SignWith = "a", LastRenewToken;
            public readonly string RenewToken = new string('7', 64);
            public int PendingClaims, RenewCalls;
            public readonly int Days = 30;

            public Issuer(LicensePlugin t)
            {
                this.t = t;
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Name = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
                thread = new Thread(Serve) { IsBackground = true };
                thread.Start();
            }

            public void Opened(string url)
            {
                var u = new Uri(url);
                OpenedPath = u.PathAndQuery;
                Nonce = u.Query.Split('&')[0].Substring(3);
            }

            public string License(string key, string[] accounts, int days)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var text = "HD2 MOD LICENSE 1\nissuer=" + Name + "\naccounts=" + string.Join(",", accounts) +
                           "\nissued=" + now + "\nexpires=" + (now + days * 86400L) + "\nholder=p:1\n";
                var pkcs8 = Convert.FromBase64String(Json.Str(Json.Obj(t.keys, key), "pkcs8"));
                using (var rsa = new RSACng(CngKey.Import(pkcs8, CngKeyBlobFormat.Pkcs8PrivateBlob)))
                {
                    var sig = rsa.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    return text + "sig=" + string.Concat(sig.Select(b => b.ToString("x2"))) + "\n";
                }
            }

            (int, object) Answer(string path, Dictionary<string, object> body)
            {
                if (path == "/claim")
                {
                    if (Json.Str(body, "n") != Nonce) return (400, new Dictionary<string, object> { ["error"] = "bad_request" });
                    if (PendingClaims > 0) { PendingClaims--; return (404, new Dictionary<string, object> { ["pending"] = true }); }
                    if (ClaimError != null) return (403, new Dictionary<string, object> { ["error"] = ClaimError });
                    return (200, new Dictionary<string, object> { ["license"] = License(SignWith, new[] { A }, Days), ["renew"] = RenewToken });
                }
                if (path == "/renew")
                {
                    RenewCalls++;
                    LastRenewToken = Json.Str(body, "renew");
                    if (RenewError != null) return (403, new Dictionary<string, object> { ["error"] = RenewError });
                    return (200, new Dictionary<string, object> { ["license"] = License(SignWith, new[] { A }, Days) });
                }
                return (404, new Dictionary<string, object> { ["error"] = "not_found" });
            }

            void Serve()
            {
                while (!stopped)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); }
                    catch (SocketException) { return; }
                    catch (ObjectDisposedException) { return; }
                    using (c)
                    using (var s = c.GetStream())
                    {
                        var head = new StringBuilder();
                        while (!head.ToString().EndsWith("\r\n\r\n"))
                        {
                            int b = s.ReadByte();
                            if (b < 0) break;
                            head.Append((char)b);
                        }
                        var lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                        var path = lines[0].Split(' ')[1];
                        int length = lines.Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                          .Select(l => int.Parse(l.Substring(15).Trim())).FirstOrDefault();
                        var buf = new byte[length];
                        for (int got = 0; got < length;) got += s.Read(buf, got, length - got);
                        var (status, answer) = Answer(path, Json.Parse(Encoding.UTF8.GetString(buf)) as Dictionary<string, object>);
                        var bytes = Encoding.UTF8.GetBytes(Json.Write(answer));
                        var reply = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " X\r\nContent-Type: application/json\r\nContent-Length: " +
                                                            bytes.Length + "\r\nConnection: close\r\n\r\n");
                        s.Write(reply, 0, reply.Length);
                        s.Write(bytes, 0, bytes.Length);
                    }
                }
            }

            public void Dispose()
            {
                stopped = true;
                listener.Stop();
            }
        }
    }
}
