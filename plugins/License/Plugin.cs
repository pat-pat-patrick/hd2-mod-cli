using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hd2Mod.License
{
    /// <summary>HD2 Mod License, the manager's side (spec: utilities\hd2-mod-license,
    /// spec/protocol.zh-CN.md). A mod that declares an issuer in hd2_license.json checks a
    /// license at load; this signs the player in to that issuer, writes the license where the
    /// mod reads it, and renews it. The manager calls these by reflection (core\Plugins.cs), so
    /// every method takes and gives strings, numbers, bools and the Json shapes only.
    ///
    /// home: the manager's settings folder (licenses.json: per issuer the pinned key and the
    /// renew token, encrypted for this Windows user). appData: the player's roaming AppData,
    /// where the game keeps saves\ and the mods read licenses\.</summary>
    public static class Plugin
    {
        /// <summary>Raised when the calls change shape; the manager refuses a plugin of another Api.</summary>
        public const int Api = 1;

        const string FileName = "hd2_license.json", Header = "HD2 MOD LICENSE 1\n", StoreName = "licenses.json";
        static readonly Regex Issuer = new Regex(@"^(?:[a-z0-9][a-z0-9.-]*|(?:localhost|127\.0\.0\.1):\d{1,5})$");
        static readonly Regex Account = new Regex("^[0-9a-f]{16}$");
        static readonly byte[] Salt = Encoding.UTF8.GetBytes("hd2qd license renew");
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // -- what a mod declares ---------------------------------------------------------

        /// <summary>The mod's hd2_license.json as {issuer, name, key}, or null when it has none.
        /// A declaration that is there but wrong is {error}, so the manager can say so.</summary>
        public static Dictionary<string, object> Declaration(string modFolder)
        {
            var path = Path.Combine(modFolder, FileName);
            if (!File.Exists(path)) return null;
            Dictionary<string, object> d;
            try { d = Json.Parse(File.ReadAllText(path)) as Dictionary<string, object>; }
            catch (Exception e) when (e is FormatException || e is IOException) { return Err("bad_declaration"); }
            if (d == null) return Err("bad_declaration");
            var issuer = Json.Str(d, "issuer")?.Trim().ToLowerInvariant();
            var key = Json.Str(d, "key")?.Trim().ToLowerInvariant().TrimStart('0');
            if (Json.Long(d, "protocol") != 1 || issuer == null || !Issuer.IsMatch(issuer)
                || key == null || !Regex.IsMatch(key, "^[0-9a-f]{500,1024}$"))
                return Err("bad_declaration");
            var name = Json.Str(d, "name")?.Trim();
            return new Dictionary<string, object> { ["issuer"] = issuer, ["name"] = string.IsNullOrEmpty(name) ? issuer : name, ["key"] = key };
        }

        /// <summary>The game accounts on this computer: the ids the game names its settings
        /// files after (saves\&lt;id&gt;_user_settings.config), which Network.peer_id() returns in
        /// game. Most played first.</summary>
        public static List<string> Accounts(string appData)
        {
            var saves = Path.Combine(appData, "Arrowhead", "Helldivers2", "saves");
            if (!Directory.Exists(saves)) return new List<string>();
            return new DirectoryInfo(saves).GetFiles("*_user_settings.config")
                .Select(f => (Id: f.Name.Substring(0, f.Name.IndexOf('_')).ToLowerInvariant(), f.LastWriteTimeUtc))
                .Where(a => Account.IsMatch(a.Id))
                .OrderByDescending(a => a.LastWriteTimeUtc)
                .Select(a => a.Id).Distinct().ToList();
        }

        // -- state -------------------------------------------------------------------------

        /// <summary>Where the issuer's license stands on this computer:
        /// {state, issuer, name, expires?, accounts?, renew_due?, members}. The file holds one
        /// license per member who signed in here (two players sharing a computer); the one for
        /// an account here that runs longest speaks. state is "licensed", "expired", "none" (no
        /// license yet), "other_account" (the licenses name no account here), "invalid" (none is
        /// signed by the declared key), "key_changed" (the declared key is not the one signed in
        /// with: ask the player), or "no_account" (no game account here yet: start the game
        /// once). members: how many members' licenses the file holds.</summary>
        public static Dictionary<string, object> Status(string home, string appData, Dictionary<string, object> decl)
        {
            string issuer = Json.Str(decl, "issuer"), key = Json.Str(decl, "key");
            var r = new Dictionary<string, object> { ["issuer"] = issuer, ["name"] = Json.Str(decl, "name") };
            var pinned = Json.Str(IssuerRecord(home, issuer), "key");
            if (pinned != null && pinned != key) return With(r, "key_changed");
            var mine = Accounts(appData);
            var text = ReadLicense(appData, issuer);
            if (text == null) return With(r, mine.Count == 0 ? "no_account" : "none");
            var all = Licenses(text, issuer, key);
            if (all.Count == 0) return With(r, "invalid");
            r["members"] = (long)all.Count;
            var ours = all.Where(f => Named(f).Intersect(mine).Any()).OrderByDescending(Expires).ToList();
            if (ours.Count == 0) return With(r, mine.Count == 0 ? "no_account" : "other_account");
            var best = ours[0];
            long expires = Expires(best);
            long issued = best.TryGetValue("issued", out var iv) && long.TryParse(iv, out var i) ? i : expires;
            r["expires"] = expires;
            r["accounts"] = Named(best).Cast<object>().ToList();
            long now = Now();
            if (now >= expires) return With(r, "expired");
            r["renew_due"] = now >= issued + (expires - issued) / 2;
            return With(r, "licensed");
        }

        // -- signing in --------------------------------------------------------------------

        /// <summary>Step one: {url, nonce}; the manager opens url in the browser, where the player
        /// signs in to the issuer's members' site. {error: "no_account"} when the game has not
        /// made a settings file here yet.</summary>
        public static Dictionary<string, object> BeginSignIn(string appData, Dictionary<string, object> decl)
        {
            var accounts = Accounts(appData);
            if (accounts.Count == 0) return Err("no_account");
            var nonce = Hex(Random(16));
            var url = Base(Json.Str(decl, "issuer")) + "/login?n=" + nonce + "&acct=" + accounts[0];
            return new Dictionary<string, object> { ["url"] = url, ["nonce"] = nonce };
        }

        /// <summary>Step two: waits for the issuer's answer (it asks every 2 s, up to
        /// timeoutSeconds), checks the license against the declared key, writes it where the
        /// mods read it and keeps the renew token. {ok: true, expires} or {error}: the issuer's
        /// own (not_member, account_limit, denied), "timeout", "cancelled", "network",
        /// "bad_license".</summary>
        public static Dictionary<string, object> FinishSignIn(string home, string appData, Dictionary<string, object> decl,
                                                              string nonce, int timeoutSeconds, CancellationToken cancel)
        {
            string issuer = Json.Str(decl, "issuer"), key = Json.Str(decl, "key");
            var until = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (true)
            {
                if (cancel.IsCancellationRequested) return Err("cancelled");
                var (status, body) = Post(issuer, "/claim", new Dictionary<string, object> { ["n"] = nonce });
                if (status == 200 || status == 403)
                {
                    if (status == 403 || body == null) return Err(Json.Str(body, "error") ?? "network");
                    var text = Json.Str(body, "license");
                    var renew = Json.Str(body, "renew");
                    var f = text == null ? null : Verify(text, issuer, key);
                    if (f == null || renew == null) return Err("bad_license");
                    WriteLicense(appData, issuer, key, text);
                    var rec = IssuerRecord(home, issuer) ?? new Dictionary<string, object>();
                    rec["key"] = key;
                    rec["name"] = Json.Str(decl, "name");
                    var members = Json.Obj(rec, "members") ?? new Dictionary<string, object>();
                    members[Holder(f)] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(renew), Salt, DataProtectionScope.CurrentUser));
                    rec["members"] = members;
                    SaveIssuer(home, issuer, rec);
                    return new Dictionary<string, object> { ["ok"] = true, ["expires"] = long.Parse(f["expires"]) };
                }
                // an issuer out of reach is asked again until the time is up: a dropped
                // connection while the player signs in is not the end of it
                if (status != 404 && status != 0) return Err("network");
                if (DateTime.UtcNow >= until) return Err(status == 0 ? "network" : "timeout");
                if (cancel.WaitHandle.WaitOne(2000)) return Err("cancelled");
            }
        }

        /// <summary>Renews each member's license here when half its time has passed (or always,
        /// with force). {renewed: false} when none was due or nobody signed in here; {renewed:
        /// true, expires} (the latest end) when one or more were; {error} when none could be and
        /// one failed: the issuer's (not_member, sign_in_again, unknown), "network",
        /// "bad_license", "key_changed". A member whose renewal fails keeps their license to its
        /// end; the others are renewed all the same.</summary>
        public static Dictionary<string, object> Renew(string home, string appData, Dictionary<string, object> decl, bool force)
        {
            string issuer = Json.Str(decl, "issuer"), key = Json.Str(decl, "key");
            var rec = IssuerRecord(home, issuer);
            var tokens = RenewTokens(rec);
            if (tokens.Count == 0) return new Dictionary<string, object> { ["renewed"] = false };
            if (Json.Str(rec, "key") != key) return Err("key_changed");
            var text = ReadLicense(appData, issuer);
            var held = text == null ? new List<Dictionary<string, string>>() : Licenses(text, issuer, key);
            long now = Now(), latest = 0;
            string error = null;
            foreach (var (holder, token) in tokens)
            {
                var mine = held.Where(f => Holder(f) == holder).OrderByDescending(Expires).FirstOrDefault();
                if (!force && mine != null && !Due(mine, now)) continue;
                var (status, body) = Post(issuer, "/renew", new Dictionary<string, object> { ["renew"] = token });
                if (status != 200) { error = error ?? (status == 403 ? Json.Str(body, "error") ?? "network" : "network"); continue; }
                var got = Json.Str(body, "license");
                var f = got == null ? null : Verify(got, issuer, key);
                if (f == null) { error = error ?? "bad_license"; continue; }
                WriteLicense(appData, issuer, key, got);
                latest = Math.Max(latest, Expires(f));
                // a token kept before members were told apart: filed under the member it renewed
                if (holder == "")
                {
                    rec = IssuerRecord(home, issuer);
                    var members = Json.Obj(rec, "members") ?? new Dictionary<string, object>();
                    if (!members.ContainsKey(Holder(f))) members[Holder(f)] = rec["renew"];
                    rec["members"] = members;
                    rec.Remove("renew");
                    SaveIssuer(home, issuer, rec);
                }
            }
            if (latest > 0) return new Dictionary<string, object> { ["renewed"] = true, ["expires"] = latest };
            return error != null ? Err(error) : new Dictionary<string, object> { ["renewed"] = false };
        }

        /// <summary>Signs out here: the license file, the renew token and the pinned key go.</summary>
        public static void Forget(string home, string appData, string issuer)
        {
            var path = LicensePath(appData, issuer);
            if (File.Exists(path)) File.Delete(path);
            SaveIssuer(home, issuer, null);
        }

        /// <summary>The issuers signed in on this computer, each as {issuer, name, key}: so the
        /// manager can show a sign-in (and sign it out) when no mod in the library names it.</summary>
        public static List<Dictionary<string, object>> SignedIn(string home)
        {
            var list = new List<Dictionary<string, object>>();
            var all = Json.Obj(LoadStore(home), "issuers");
            if (all == null) return list;
            foreach (var kv in all)
                if (kv.Value is Dictionary<string, object> rec && Json.Str(rec, "key") != null)
                    list.Add(new Dictionary<string, object> { ["issuer"] = kv.Key, ["name"] = Json.Str(rec, "name") ?? kv.Key, ["key"] = Json.Str(rec, "key") });
            return list;
        }

        // -- the license -------------------------------------------------------------------

        /// <summary>Every license in a file signed by key for issuer: the file holds one per
        /// member, each starting with the header at the start of a line.</summary>
        static List<Dictionary<string, string>> Licenses(string text, string issuer, string key)
        {
            var found = new List<Dictionary<string, string>>();
            foreach (var block in Blocks(text))
            {
                var f = Verify(block, issuer, key);
                if (f != null) found.Add(f);
            }
            return found;
        }

        static List<string> Blocks(string text)
        {
            text = text.Replace("\r", "");
            var blocks = new List<string>();
            int at = text.StartsWith(Header, StringComparison.Ordinal) ? 0 : text.IndexOf("\n" + Header, StringComparison.Ordinal);
            while (at >= 0)
            {
                if (text[at] == '\n') at++;
                int next = text.IndexOf("\n" + Header, at, StringComparison.Ordinal);
                blocks.Add(next < 0 ? text.Substring(at) : text.Substring(at, next + 1 - at));
                at = next;
            }
            return blocks;
        }

        static List<string> Named(Dictionary<string, string> f) => f["accounts"].Split(',').ToList();
        static long Expires(Dictionary<string, string> f) => long.Parse(f["expires"]);
        static string Holder(Dictionary<string, string> f) => f.TryGetValue("holder", out var h) ? h : "";

        static bool Due(Dictionary<string, string> f, long now)
        {
            long expires = Expires(f);
            long issued = f.TryGetValue("issued", out var iv) && long.TryParse(iv, out var i) ? i : expires;
            return now >= issued + (expires - issued) / 2;
        }

        /// <summary>The fields of a license signed by key for issuer, else null. Same rules as
        /// license_check.lua: CR dropped, the signature over every byte before "sig=".</summary>
        static Dictionary<string, string> Verify(string text, string issuer, string key)
        {
            text = text.Replace("\r", "");
            int at = text.IndexOf("\nsig=", StringComparison.Ordinal);
            if (!text.StartsWith(Header, StringComparison.Ordinal) || at < 0) return null;
            string signed = text.Substring(0, at + 1), sig = text.Substring(at + 5).Trim();
            var f = new Dictionary<string, string>();
            foreach (var line in signed.Split('\n').Skip(1))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) f[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            if (!f.TryGetValue("issuer", out var iss) || iss != issuer) return null;
            if (!f.ContainsKey("accounts") || !f.TryGetValue("expires", out var exp) || !long.TryParse(exp, out _)) return null;
            var modulus = Bytes(key);
            if (!Regex.IsMatch(sig, "^[0-9a-fA-F]+$") || sig.Length != modulus.Length * 2) return null;
            using (var rsa = new RSACng())
            {
                rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = new byte[] { 1, 0, 1 } });
                if (!rsa.VerifyData(Encoding.UTF8.GetBytes(signed), Bytes(sig), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return null;
            }
            return f;
        }

        static string LicensePath(string appData, string issuer) =>
            Path.Combine(appData, "Arrowhead", "Helldivers2", "licenses", issuer.Replace(':', '_') + ".txt");

        static string ReadLicense(string appData, string issuer)
        {
            var path = LicensePath(appData, issuer);
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (IOException) { return null; }
        }

        /// <summary>A member's new license, in place of their old one: the other members'
        /// licenses that are still good stay (two players sharing a computer). Through a
        /// temporary file, so the game never reads half a license.</summary>
        static void WriteLicense(string appData, string issuer, string key, string text)
        {
            var path = LicensePath(appData, issuer);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var holder = Holder(Verify(text, issuer, key));
            var keep = new StringBuilder(text.Replace("\r", "").TrimEnd('\n') + "\n");
            var old = ReadLicense(appData, issuer);
            if (old != null)
                foreach (var block in Blocks(old))
                {
                    var f = Verify(block, issuer, key);
                    if (f != null && Holder(f) != holder && Expires(f) > Now()) keep.Append(block.TrimEnd('\n') + "\n");
                }
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, keep.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        // -- licenses.json -----------------------------------------------------------------

        static Dictionary<string, object> LoadStore(string home)
        {
            var path = Path.Combine(home, StoreName);
            try { return File.Exists(path) ? Json.Parse(File.ReadAllText(path)) as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>(); }
            catch (FormatException) { return new Dictionary<string, object>(); }
        }

        static Dictionary<string, object> IssuerRecord(string home, string issuer) => Json.Obj(Json.Obj(LoadStore(home), "issuers"), issuer);

        static void SaveIssuer(string home, string issuer, Dictionary<string, object> rec)
        {
            var store = LoadStore(home);
            var all = Json.Obj(store, "issuers") ?? new Dictionary<string, object>();
            if (rec == null) all.Remove(issuer);
            else all[issuer] = rec;
            store["issuers"] = all;
            Directory.CreateDirectory(home);
            var path = Path.Combine(home, StoreName);
            File.WriteAllText(path + ".tmp", Json.Write(store, true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
            else File.Move(path + ".tmp", path);
        }

        /// <summary>(holder, token) for each member signed in here; holder "" for the one token
        /// a sign-in before 2026-11-02 kept for the whole issuer.</summary>
        static List<(string Holder, string Token)> RenewTokens(Dictionary<string, object> rec)
        {
            var list = new List<(string, string)>();
            var members = Json.Obj(rec, "members");
            if (members != null)
                foreach (var kv in members)
                    if (Unprotect(kv.Value as string) is string t) list.Add((kv.Key, t));
            if (Unprotect(Json.Str(rec, "renew")) is string legacy) list.Add(("", legacy));
            return list;
        }

        static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), Salt, DataProtectionScope.CurrentUser)); }
            catch (Exception e) when (e is CryptographicException || e is FormatException) { return null; }
        }

        // -- helpers -----------------------------------------------------------------------

        /// <summary>https, except for a test issuer on this computer.</summary>
        static string Base(string issuer) =>
            (issuer.StartsWith("localhost:") || issuer.StartsWith("127.0.0.1:") ? "http://" : "https://") + issuer;

        /// <summary>(status, body); status 0 when the issuer could not be reached.</summary>
        static (int, Dictionary<string, object>) Post(string issuer, string path, Dictionary<string, object> body)
        {
            try
            {
                using (var content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json"))
                using (var r = Http.PostAsync(Base(issuer) + path, content).GetAwaiter().GetResult())
                {
                    var text = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Dictionary<string, object> d = null;
                    try { d = Json.Parse(text) as Dictionary<string, object>; } catch (FormatException) { }
                    return ((int)r.StatusCode, d);
                }
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException) { return (0, null); }
        }

        /// <summary>Set by tests.</summary>
        public static Func<long> Clock = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        static long Now() => Clock();

        static Dictionary<string, object> Err(string error) => new Dictionary<string, object> { ["error"] = error };

        static Dictionary<string, object> With(Dictionary<string, object> r, string state)
        {
            r["state"] = state;
            return r;
        }

        static byte[] Random(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));

        static byte[] Bytes(string hex)
        {
            if (hex.Length % 2 == 1) hex = "0" + hex;
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }
    }
}
