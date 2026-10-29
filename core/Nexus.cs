#if HAS_NEXUS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
#endif

namespace Hd2Mod
{
#if HAS_NEXUS
    /// <summary>A Mod Manager Download link from Nexus Mods' site:
    /// nxm://helldivers2/mods/MOD/files/FILE?key=...&amp;expires=...&amp;user_id=...</summary>
    public class NxmLink
    {
        public string Game, Key, Expires, Url;
        public long Mod, File;

        public static NxmLink Parse(string url)
        {
            var m = Regex.Match(url ?? "", @"^nxm://([^/]+)/mods/(\d+)/files/(\d+)(\?.*)?$", RegexOptions.IgnoreCase);
            if (!m.Success) throw new ModError("not a Nexus Mods download link: " + url, "bad_link", url);
            var link = new NxmLink { Url = url, Game = m.Groups[1].Value.ToLowerInvariant(), Mod = long.Parse(m.Groups[2].Value), File = long.Parse(m.Groups[3].Value) };
            foreach (var part in m.Groups[4].Value.TrimStart('?').Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var k = part.Substring(0, eq);
                var v = Uri.UnescapeDataString(part.Substring(eq + 1));
                if (k == "key") link.Key = v;
                else if (k == "expires") link.Expires = v;
            }
            return link;
        }
    }
#endif

    /// <summary>Nexus Mods: the game's name there and a mod's page.</summary>
    public static partial class Nexus
    {
        public const string Game = "helldivers2";

        /// <summary>A mod's page on Nexus Mods.</summary>
        public static string Page(NexusRef r) => "https://www.nexusmods.com/" + r.Game + "/mods/" + r.Mod;
#if HAS_NEXUS

        // Nexus Mods' site button (Mod Manager Download) and API (v1), for the player signed in
        // with OAuth and PKCE (NexusOAuth; Nexus Mods' API policy and their reply, 2026-10-27).
        // Every request names the application and its version, and is made only for what the
        // player started. Only builds made with HAS_NEXUS have any of it.

        /// <summary>The name every request carries. A build anyone else makes from this source
        /// says so, because the name is registered with Nexus Mods for the builds published here
        /// and their log would otherwise show ours for a program we did not make (the user,
        /// 2026-10-25). Our own builds name themselves in a part of their own (private/, kept out
        /// of the repository).</summary>
        public static readonly string AppName = Named();

        static string Named()
        {
            var name = "HD2 Mod Quick Deploy (unofficial build)";
            OurName(ref name);
            return name;
        }

        // What only our own builds do is in private/ (not in the repository), each through one of
        // these; a build without it has none of them, and the compiler leaves their calls out.
        static partial void OurName(ref string name);
        static partial void OtherCredential(ref string credential);
        static partial void Carry(HttpRequestMessage req, string credential);
        static partial void NotAccepted(string credential);
        static partial void AccountOf(string credential, ref (string Name, bool Premium)? account);

        /// <summary>The API's address; tests point it at a local fake.</summary>
        public static string ApiBase = Environment.GetEnvironmentVariable("HD2QD_NEXUS_API") ?? "https://api.nexusmods.com";

        static readonly HttpClient http = MakeClient();

        /// <summary>The client every request goes through, with this program's name and version (NexusOAuth too).</summary>
        internal static HttpClient Http => http;

        static HttpClient MakeClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            c.DefaultRequestHeaders.Add("Application-Name", AppName);
            c.DefaultRequestHeaders.Add("Application-Version", StateDoc.Version);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("HD2ModQuickDeploy/" + StateDoc.Version);
            return c;
        }

        // ------------------------------------------------------------ what requests carry

        /// <summary>What requests are made with: the sign-in (NexusOAuth, "Bearer ..."); in our own
        /// testing builds, until Nexus Mods issues the sign-in id, their testing credential instead.
        /// Null for neither.</summary>
        public static string Credential()
        {
            var signedIn = NexusOAuth.Credential();
            if (signedIn != null) return signedIn;
            string other = null;
            OtherCredential(ref other);
            return other;
        }

        /// <summary>An API request (path from /v1/...) with what requests carry (Credential): the answer's text.</summary>
        public static string Api(string path, string key) => Get(path, key);

        static string Get(string path, string key)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, ApiBase.TrimEnd('/') + path))
            {
                // the sign-in's access token
                if (key.StartsWith("Bearer ", StringComparison.Ordinal))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Substring(7));
                else Carry(req, key);
                HttpResponseMessage r;
                try { r = http.SendAsync(req).GetAwaiter().GetResult(); }
                catch (HttpRequestException e) { throw new ModError("Nexus Mods cannot be reached: " + e.Message, "nexus_offline"); }
                using (r)
                {
                    var body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    switch ((int)r.StatusCode)
                    {
                        case 200: return body;
                        case 401:
                            if (!key.StartsWith("Bearer ", StringComparison.Ordinal)) NotAccepted(key);
                            throw new ModError("Nexus Mods does not accept the sign-in: sign in again", "nexus_login");
                        case 403: throw new ModError("Nexus Mods refused the download (an old link, or one for a premium account): " + body, "nexus_refused");
                        case 404: throw new ModError("Nexus Mods has no such mod or file", "nexus_missing");
                        case 429: throw new ModError("too many requests to Nexus Mods: wait a while", "nexus_busy");
                        default: throw new ModError("Nexus Mods answered " + (int)r.StatusCode + ": " + body, "nexus_error");
                    }
                }
            }
        }

        /// <summary>The account requests are made for: its name and whether it is premium.</summary>
        public static (string Name, bool Premium) Validate(string key)
        {
            if (key.StartsWith("Bearer ", StringComparison.Ordinal)) return NexusOAuth.Account();      // in the token itself
            (string Name, bool Premium)? account = null;
            AccountOf(key, ref account);
            if (account != null) return account.Value;
            throw new ModError("not signed in to Nexus Mods", "nexus_login");
        }

        // ------------------------------------------------------------ download and install

        static string Safe(string name) => Regex.Replace(name ?? "", @"[^\w .()\[\]-]+", "_").Trim();

        /// <summary>The file a link names, downloaded into the mod folder (Store.ModsDir)
        /// (progress: bytes so far, all bytes or 0 when unknown).</summary>
        public static (string File, string ModName) Download(NxmLink link, string key, Action<long, long> progress = null)
        {
            string g = link.Game, path = "/v1/games/" + g + "/mods/" + link.Mod;
            var info = Json.Parse(Get(path + "/files/" + link.File + ".json", key)) as Dictionary<string, object>;
            var modInfo = Json.Parse(Get(path + ".json", key)) as Dictionary<string, object>;
            var query = link.Key != null ? "?key=" + Uri.EscapeDataString(link.Key) + "&expires=" + Uri.EscapeDataString(link.Expires ?? "") : "";
            var links = Json.Parse(Get(path + "/files/" + link.File + "/download_link.json" + query, key)) as List<object>;
            var uri = links?.OfType<Dictionary<string, object>>().Select(x => Json.Str(x, "URI")).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            if (uri == null) throw new ModError("Nexus Mods gave no download address", "nexus_error");

            var name = Safe(Json.Str(info, "file_name"));
            if (name.Length == 0) name = Safe(Path.GetFileName(new Uri(uri).AbsolutePath));
            if (name.Length == 0) name = link.Mod + "-" + link.File + ".zip";
            var folder = Store.ModsDir();
            Directory.CreateDirectory(folder);
            var dest = Paths.Join(folder, name);
            var tmp = dest + ".part";
            HttpResponseMessage r;
            try { r = http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult(); }
            catch (HttpRequestException e) { throw new ModError("the download did not start: " + e.Message, "nexus_offline"); }
            using (r)
            {
                if (!r.IsSuccessStatusCode) throw new ModError("the download was refused: " + (int)r.StatusCode, "nexus_error");
                long total = r.Content.Headers.ContentLength ?? 0, done = 0;
                using (var src = r.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    var buf = new byte[1 << 16];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        done += n;
                        progress?.Invoke(done, total);
                    }
                }
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            LastFile = (Json.Str(info, "version"), Json.Long(info, "uploaded_timestamp"));
            return (dest, Json.Str(modInfo, "name"));
        }

        /// <summary>The version and upload time of the file Download fetched last.</summary>
        static (string Version, long Time) LastFile;

        /// <summary>A link's file downloaded and put in the library: a mod already there from the
        /// same Nexus mod gets it as its new version (its place, on/off and options kept);
        /// another is added at the bottom, on.</summary>
        public static (Mod Mod, bool Replaced) Install(NxmLink link, string key, Action<long, long> progress = null)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long bytes = 0;
            try
            {
                var r = InstallNow(link, key, (done, total) => { bytes = done; progress?.Invoke(done, total); });
                Remember(link, r.Mod.Name, r.Replaced ? "replaced" : "added", bytes, clock.Elapsed.TotalSeconds, null);
                return r;
            }
            catch (ModError e)
            {
                if (e.Kind != "other_game") Remember(link, null, "failed", bytes, clock.Elapsed.TotalSeconds, e.Kind ?? e.Message);
                throw;
            }
        }

        /// <summary>Every download so far, newest first (all of them, as Vortex keeps them: the user, 2026-10-25): time, Nexus mod and file,
        /// the mod's name, result (added, replaced, failed), bytes, seconds, why it failed.</summary>
        public static List<Dictionary<string, object>> History() =>
            Json.List(Store.Load("nexus.json"), "history").OfType<Dictionary<string, object>>().ToList();

        static void Remember(NxmLink link, string name, string result, long bytes, double seconds, string why)
        {
            var d = Store.Load("nexus.json");
            var h = Json.List(d, "history");
            h.Insert(0, new Dictionary<string, object>
            {
                ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                ["game"] = link.Game, ["mod"] = link.Mod, ["file"] = link.File, ["name"] = name, ["result"] = result,
                ["bytes"] = bytes, ["seconds"] = Math.Round(seconds, 2), ["why"] = why,
            });
            d["history"] = h;
            Store.Save("nexus.json", d);
        }


        static (Mod Mod, bool Replaced) InstallNow(NxmLink link, string key, Action<long, long> progress)
        {
            if (link.Game != Game) throw new ModError("this link is for another game: " + link.Game, "other_game", link.Game);
            if (string.IsNullOrEmpty(key)) throw new ModError("not signed in to Nexus Mods", "no_key");
            var (file, modName) = Download(link, key, progress);
            var cfg = Store.Config();
            var folder = Ops.SourceFolder(file, cfg);
            // unpacked beside it in the mod folder: the archive goes, only the mod's folder stays
            var page = Page(new NexusRef { Game = link.Game, Mod = link.Mod });
            try { File.Delete(file); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            var lib = Library.Load();
            var manifest = Manifest.Read(folder);
            var had = lib.Mods.FirstOrDefault(m => m.Nexus != null && m.Nexus.Game == link.Game && m.Nexus.Mod == link.Mod);
            var nexus = new NexusRef { Game = link.Game, Mod = link.Mod, File = link.File, Version = LastFile.Version, Time = LastFile.Time };
            if (had != null)
            {
                Versions.Keep(lib, cfg, had, folder);
                had.Options = Manifest.Merged(manifest, had.Options);
                had.Path = folder;
                had.Source = page;
                had.Nexus = nexus;
                if (!string.IsNullOrEmpty(manifest?.Name)) had.Name = manifest.Name;
                lib.Save();
                return (had, true);
            }
            var name = !string.IsNullOrEmpty(manifest?.Name) ? manifest.Name : !string.IsNullOrEmpty(modName) ? modName : null;
            if (name != null && lib.Mods.Any(m => m.Name == name)) name += " (Nexus " + link.Mod + ")";
            var mod = Ops.NewMod(folder, name, page);
            mod.Nexus = nexus;
            lib.Mods.Add(mod);
            lib.Save();
            return (mod, false);
        }

        // ------------------------------------------------------------ the site's button

        /// <summary>Where Windows looks up nxm:// for this user; tests use a key of their own.</summary>
        public static string Classes = @"Software\Classes\nxm";

        /// <summary>The program Windows gives nxm:// links to now (its command), else null.</summary>
        public static string Handler()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Classes + @"\shell\open\command"))
                return k?.GetValue("") as string;
        }

        /// <summary>Whether the links go to this program: the registered program has this one's
        /// file name and is there (a copy elsewhere, a new build, counts; a moved one does not).</summary>
        public static bool IsOurs(string exe)
        {
            var m = Regex.Match(Handler() ?? "", "^\\s*\"([^\"]+)\"|^\\s*(\\S+)");
            var path = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return path.Length > 0 && File.Exists(path)
                && string.Equals(Path.GetFileName(path), Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>nxm:// links to exe from now on (for this Windows user); the program that had
        /// them is remembered, to give them back and to pass it other games' links.</summary>
        public static void TakeOver(string exe)
        {
            var before = Handler();
            if (before != null && before.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0)
            {
                var d = Store.Load("nexus.json");
                d["previous_handler"] = before;
                Store.Save("nexus.json", d);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(Classes))
            {
                k.SetValue("", "URL:NXM Protocol");
                k.SetValue("URL Protocol", "");
            }
            using (var k = Registry.CurrentUser.CreateSubKey(Classes + @"\DefaultIcon")) k.SetValue("", "\"" + exe + "\",0");
            using (var k = Registry.CurrentUser.CreateSubKey(Classes + @"\shell\open\command")) k.SetValue("", "\"" + exe + "\" \"%1\"");
        }

        /// <summary>The program that had nxm:// links before, else null.</summary>
        public static string Previous() => Json.Str(Store.Load("nexus.json"), "previous_handler");

        /// <summary>nxm:// links back to the program that had them before.</summary>
        public static void GiveBack()
        {
            var before = Previous();
            if (string.IsNullOrEmpty(before)) throw new ModError("no program had the links before", "no_previous");
            using (var k = Registry.CurrentUser.CreateSubKey(Classes + @"\shell\open\command")) k.SetValue("", before);
            var exe = Regex.Match(before, "^\"([^\"]+)\"|^(\\S+)").Groups.Cast<Group>().Skip(1).FirstOrDefault(x => x.Success)?.Value;
            if (exe != null)
                using (var k = Registry.CurrentUser.CreateSubKey(Classes + @"\DefaultIcon")) k.SetValue("", "\"" + exe + "\",0");
        }

        /// <summary>A link handed on to the program that had them before (another game's link).</summary>
        public static bool PassOn(string url)
        {
            var before = Previous();
            if (string.IsNullOrEmpty(before)) return false;
            var m = Regex.Match(before, "^\\s*(\"[^\"]+\"|\\S+)\\s*(.*)$");
            if (!m.Success) return false;
            var exe = m.Groups[1].Value.Trim('"');
            var args = m.Groups[2].Value.Replace("%1", url);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false });
            return true;
        }
#endif
    }
}
