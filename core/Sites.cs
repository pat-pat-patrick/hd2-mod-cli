using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Hd2Mod
{
    /// <summary>What a mod's page offered when this copy was taken (Sites): the version, the site's
    /// file, when that file came (or, for a browser's download, when it was downloaded), and for a
    /// direct link the server's ETag and length. Null fields are not known.</summary>
    public class PageRef
    {
        /// <summary>Name and Label: the file's name and the site's label for it (GameBanana: "SA-8
        /// NO HELM"), which find its line on the page once the file itself is gone (an author's
        /// new upload often takes the old one's place).</summary>
        public string Version, File, ETag, Name, Label;
        public long Time, Size;
    }

    /// <summary>A file a mod's page offers.</summary>
    public class SiteFile
    {
        public string Id, Name, Label, Version, Url, Md5;
        public long Time, Size;
    }

    /// <summary>What a mod's page says now.</summary>
    public class SiteOffer
    {
        public string Site, Id, Page, Name, Author, Summary, Picture, Version;
        /// <summary>The site as the window names it (Sites.Look): "GameBanana", a direct link's host.</summary>
        public string Shown;
        /// <summary>When the mod last had a new file, Unix seconds (0: not said).</summary>
        public long Time;
        /// <summary>The files a player could take: empty where the site needs a sign-in (AyakaMods)
        /// or the author asked mod managers not to download (ModWorkshop).</summary>
        public List<SiteFile> Files = new List<SiteFile>();
        /// <summary>Why there are no files to download here, when there are none.</summary>
        public string NoFiles;
        /// <summary>What the author wrote of each version (ModWorkshop's changelog), newest first.</summary>
        public List<string> Changelog = new List<string>();
        public string ETag;
        public long Size;
    }

    /// <summary>Update checks and downloads for the sites besides Nexus Mods and GitHub (issue 26,
    /// the user, 2026-11-22: "这些可以都加上"), each asked the way it can be asked without a key:
    ///
    /// - AyakaMods: no API; each mod page carries a JSON-LD SoftwareApplication with the version
    ///   (softwareVersion, free text: one page says 2026-10-19) and dateModified. Downloads need a
    ///   sign-in, so an update opens the page. Behind Cloudflare: one request at a time, two
    ///   seconds apart, our own User-Agent; a challenge is never solved, only reported.
    /// - GameBanana: apiv11/Mod/{id}, keyless; every file has its MD5 and a public link
    ///   (gamebanana.com/dl/{file}).
    /// - ModWorkshop: api.modworkshop.net/mods/{id}, keyless (90 requests a minute); bumped_at is
    ///   the last real update (updated_at also moves for edits); a mod whose author turned mod
    ///   managers off (disable_mod_managers) or whose download is a link elsewhere opens the page.
    /// - A direct link to an archive: the server's ETag, Last-Modified and length.
    ///
    /// The idea of reading AyakaMods' JSON-LD comes from DemocracyDefenderModManager
    /// (katsyk, Apache-2.0); this is our own code.</summary>
    public static class Sites
    {
        public const string Ayaka = "AyakaMods", Banana = "GameBanana", Workshop = "ModWorkshop", Direct = "direct";

        /// <summary>The sites' addresses; tests point them at a local server.</summary>
        public static string AyakaBase = "https://ayakamods.com", BananaBase = "https://gamebanana.com", WorkshopApi = "https://api.modworkshop.net";

        static readonly Regex AyakaPage = new Regex(@"^https?://(?:www\.)?ayakamods\.com/mods/(?:[^/?#]*?\.)?(\d{1,12})(?:[/?#]|$)", RegexOptions.IgnoreCase);
        static readonly Regex BananaPage = new Regex(@"^https?://(?:www\.)?gamebanana\.com/mods/(?:download/)?(\d{1,12})(?:[/?#]|$)", RegexOptions.IgnoreCase);
        static readonly Regex WorkshopPage = new Regex(@"^https?://(?:www\.)?modworkshop\.net/mod/(\d{1,12})(?:[/?#]|$)", RegexOptions.IgnoreCase);
        static readonly string[] Archives = { ".zip", ".7z", ".rar" };

        static bool IsArchive(string name) => Archives.Any(e => (name ?? "").EndsWith(e, StringComparison.OrdinalIgnoreCase));

        /// <summary>Which site an address is and the mod's id there: (Ayaka|GameBanana|ModWorkshop,
        /// id), (direct, the address) for an https address of an archive that is none of them,
        /// else (null, null).</summary>
        public static (string Site, string Id) Of(string url)
        {
            url = (url ?? "").Trim();
            Match m;
            if ((m = AyakaPage.Match(url)).Success) return (Ayaka, m.Groups[1].Value);
            if ((m = BananaPage.Match(url)).Success) return (Banana, m.Groups[1].Value);
            if ((m = WorkshopPage.Match(url)).Success) return (Workshop, m.Groups[1].Value);
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && IsArchive(u.AbsolutePath)
                && u.Host.IndexOf("nexusmods.com", StringComparison.OrdinalIgnoreCase) < 0 && u.Host.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) < 0)
                return (Direct, url);
            return (null, null);
        }

        /// <summary>The channel's name for a page of one of these sites: the site's name, or for a
        /// direct link its host; null for another page.</summary>
        public static string Channel(string url)
        {
            var (site, _) = Of(url);
            if (site == null) return null;
            return site == Direct ? new Uri(url).Host : site;
        }

        /// <summary>The mod's page as the site writes it (the real site's address, whatever a test
        /// points the requests at).</summary>
        public static string PageOf(string site, string id) =>
            site == Ayaka ? "https://ayakamods.com/mods/" + id + "/" : site == Banana ? "https://gamebanana.com/mods/" + id
            : site == Workshop ? "https://modworkshop.net/mod/" + id : id;

        // ------------------------------------------------------------ asking politely

        static readonly HttpClient http = MakeClient();

        static HttpClient MakeClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
            { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("HD2ModQuickDeploy/" + StateDoc.Version);
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
            return c;
        }

        /// <summary>The least time between two requests to one site: AyakaMods sits behind Cloudflare
        /// (two seconds, as DDMM keeps); ModWorkshop allows 90 a minute.</summary>
        public static Dictionary<string, TimeSpan> Gap = new Dictionary<string, TimeSpan>
        {
            [Ayaka] = TimeSpan.FromSeconds(2), [Workshop] = TimeSpan.FromMilliseconds(700), [Banana] = TimeSpan.FromMilliseconds(500), [Direct] = TimeSpan.Zero,
        };
        /// <summary>The wait before the one retry after 403/429/503 without Retry-After; a longer
        /// Retry-After than RetryMax is not waited out.</summary>
        public static TimeSpan RetryWait = TimeSpan.FromSeconds(5), RetryMax = TimeSpan.FromSeconds(30);

        static readonly Dictionary<string, object> gates = new Dictionary<string, object>();
        static readonly Dictionary<string, DateTime> last = new Dictionary<string, DateTime>();
        /// <summary>The sites that turned us away in this check: asked no more until the next one.</summary>
        static readonly Dictionary<string, string> refusing = new Dictionary<string, string>();

        /// <summary>A new whole check: a site that refused in the last one is asked again.</summary>
        public static void BeginCheck() { lock (refusing) refusing.Clear(); }

        static object GateOf(string site) { lock (gates) { if (!gates.TryGetValue(site, out var g)) gates[site] = g = new object(); return g; } }

        class Answer { public string Body, Url; public System.Net.Http.Headers.HttpResponseHeaders Headers; public System.Net.Http.Headers.HttpContentHeaders Content; }

        /// <summary>One request to a site, through its gate: one at a time, Gap apart, one retry after
        /// a refusal that says to wait; a Cloudflare challenge is never retried.</summary>
        static Answer Ask(string site, string url, string accept, HttpMethod method = null)
        {
            lock (refusing) if (refusing.TryGetValue(site, out var why)) throw new ModError(site + " is not answering automated checks now", why);
            lock (GateOf(site))
            {
                bool retried = false;
                while (true)
                {
                    if (last.TryGetValue(site, out var at))
                    {
                        var wait = Gap[site] - (DateTime.UtcNow - at);
                        if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                    }
                    var q = new HttpRequestMessage(method ?? HttpMethod.Get, url);
                    q.Headers.Accept.ParseAdd(accept);
                    HttpResponseMessage r;
                    try { r = http.SendAsync(q).GetAwaiter().GetResult(); }
                    catch (HttpRequestException e) { last[site] = DateTime.UtcNow; throw new ModError(site + " cannot be reached: " + e.Message, "offline"); }
                    catch (System.Threading.Tasks.TaskCanceledException e) { last[site] = DateTime.UtcNow; throw new ModError(site + " did not answer in time: " + e.Message, "offline"); }
                    last[site] = DateTime.UtcNow;
                    using (r)
                    {
                        int code = (int)r.StatusCode;
                        var body = method == HttpMethod.Head ? "" : r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (r.IsSuccessStatusCode)
                            return new Answer { Body = body, Url = r.RequestMessage?.RequestUri?.ToString() ?? url, Headers = r.Headers, Content = r.Content.Headers };
                        if (code == 404 || code == 410) throw new ModError(site + ": the mod's page is not there (removed or moved)", "site_missing");
                        bool challenge = r.Headers.TryGetValues("cf-mitigated", out var cf) && cf.Any(v => v.Trim().Equals("challenge", StringComparison.OrdinalIgnoreCase))
                                         || Challenge(body);
                        if (challenge) { Refuse(site, "site_refuses"); throw new ModError(site + " is blocking automated checks now; its page opens in a browser", "site_refuses"); }
                        if (code == 403 || code == 429 || code == 503)
                        {
                            var after = r.Headers.RetryAfter?.Delta;
                            var why = code == 403 ? "site_refuses" : "site_busy";
                            if (retried || (after ?? RetryWait) > RetryMax) { Refuse(site, why); throw new ModError(site + " answered " + code + ": try again later", why); }
                            retried = true;
                            Thread.Sleep(after ?? RetryWait);
                            continue;
                        }
                        throw new ModError(site + " answered " + code, "site_error");
                    }
                }
            }
        }

        static void Refuse(string site, string why) { lock (refusing) refusing[site] = why; }

        /// <summary>A Cloudflare challenge page ("Just a moment...") rather than the site's own.</summary>
        public static bool Challenge(string html) =>
            html != null && (html.Contains("/cdn-cgi/challenge-platform/") || html.Contains("window._cf_chl_opt") || html.Contains("cf-chl-"));

        // ------------------------------------------------------------ what each site says

        /// <summary>What the page at url says now (Of decides which site); null for a page of none
        /// of these sites.</summary>
        public static SiteOffer Read(string url)
        {
            var (site, id) = Of(url);
            switch (site)
            {
                case Ayaka: return ReadAyaka(id);
                case Banana: return ReadBanana(id);
                case Workshop: return ReadWorkshop(id);
                case Direct: return ReadDirect(id);
                default: return null;
            }
        }

        static SiteOffer ReadAyaka(string id)
        {
            var a = Ask(Ayaka, AyakaBase + "/mods/" + id + "/", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
            var o = ParseAyaka(a.Body) ?? throw new ModError("AyakaMods: the page does not say the mod's version", "site_error");
            o.Id = id;
            o.Page = a.Url != null && AyakaPage.IsMatch(a.Url) ? a.Url : PageOf(Ayaka, id);
            o.NoFiles = "sign_in";
            return o;
        }

        static readonly Regex LdJson = new Regex(@"<script[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        /// <summary>The SoftwareApplication in a page's JSON-LD, else null.</summary>
        public static SiteOffer ParseAyaka(string html)
        {
            foreach (Match m in LdJson.Matches(html ?? ""))
            {
                object v;
                try { v = Json.Parse(m.Groups[1].Value.Trim()); }
                catch (FormatException) { continue; }
                var app = FindApp(v);
                if (app == null) continue;
                var author = Json.Obj(app, "author");
                var image = app.TryGetValue("image", out var im) ? im as string ?? Json.Str(im as Dictionary<string, object>, "url")
                            ?? (im as List<object>)?.OfType<string>().FirstOrDefault() : null;
                return new SiteOffer
                {
                    Site = Ayaka, Name = Decoded(Json.Str(app, "name")), Version = Json.Str(app, "softwareVersion")?.Trim() is string sv && sv.Length > 0 ? sv : null,
                    Time = Unix(Json.Str(app, "dateModified")), Author = Json.Str(author, "name") ?? Json.Str(app, "author"),
                    Summary = Decoded(Json.Str(app, "description")), Picture = image,
                };
            }
            return null;
        }

        static Dictionary<string, object> FindApp(object v)
        {
            if (v is Dictionary<string, object> d)
            {
                var type = d.TryGetValue("@type", out var t) ? t : null;
                if (type as string == "SoftwareApplication" || (type as List<object>)?.Contains("SoftwareApplication") == true) return d;
                return FindApp(Json.List(d, "@graph"));
            }
            if (v is List<object> list)
                foreach (var x in list) if (FindApp(x) is Dictionary<string, object> found) return found;
            return null;
        }

        const string BananaFields = "_idRow,_sName,_sVersion,_tsDateUpdated,_aFiles,_bIsPrivate,_bIsTrashed,_bIsWithheld,_aSubmitter,_aPreviewMedia,_sDescription,_sProfileUrl";

        static SiteOffer ReadBanana(string id)
        {
            var a = Ask(Banana, BananaBase + "/apiv11/Mod/" + id + "?_csvProperties=" + BananaFields, "application/json");
            var o = ParseBanana(a.Body);
            o.Id = id;
            o.Page = PageOf(Banana, id);
            return o;
        }

        /// <summary>A GameBanana mod: its files that can be taken (not archived, an archive, not
        /// flagged by its virus scan), each by its public link.</summary>
        public static SiteOffer ParseBanana(string json)
        {
            var d = Json.Parse(json) as Dictionary<string, object> ?? throw new ModError("GameBanana answered nothing", "site_error");
            if (Json.Bool(d, "_bIsPrivate") || Json.Bool(d, "_bIsTrashed") || Json.Bool(d, "_bIsWithheld"))
                throw new ModError("GameBanana: the mod is private, withheld or deleted", "site_missing");
            var o = new SiteOffer
            {
                Site = Banana, Name = Json.Str(d, "_sName"), Version = Trimmed(Json.Str(d, "_sVersion")), Time = Json.Long(d, "_tsDateUpdated"),
                Author = Json.Str(Json.Obj(d, "_aSubmitter"), "_sName"), Summary = Trimmed(Json.Str(d, "_sDescription")),
            };
            var image = Json.List(Json.Obj(d, "_aPreviewMedia"), "_aImages").OfType<Dictionary<string, object>>().FirstOrDefault();
            if (image != null && Json.Str(image, "_sBaseUrl") is string pb)
                o.Picture = pb + "/" + (Json.Str(image, "_sFile530") ?? Json.Str(image, "_sFile"));
            foreach (var f in Json.List(d, "_aFiles").OfType<Dictionary<string, object>>())
            {
                var name = Json.Str(f, "_sFile");
                var av = Json.Str(f, "_sAvResult");
                if (Json.Bool(f, "_bIsArchived") || !IsArchive(name) || (!string.IsNullOrEmpty(av) && av != "clean")) continue;
                var fid = Json.Long(f, "_idRow").ToString(CultureInfo.InvariantCulture);
                o.Files.Add(new SiteFile
                {
                    Id = fid, Name = name, Label = Trimmed(Json.Str(f, "_sDescription")), Version = Trimmed(Json.Str(f, "_sVersion")) ?? o.Version,
                    Time = Json.Long(f, "_tsDateAdded"), Size = Json.Long(f, "_nFilesize"), Md5 = Json.Str(f, "_sMd5Checksum"),
                    // always the site's own link, whatever the answer says
                    Url = BananaBase + "/dl/" + fid,
                });
            }
            if (o.Files.Count > 0) o.Time = Math.Max(o.Time, o.Files.Max(f => f.Time));
            o.Files = o.Files.OrderByDescending(f => f.Time).ToList();
            return o;
        }

        static SiteOffer ReadWorkshop(string id)
        {
            var a = Ask(Workshop, WorkshopApi + "/mods/" + id, "application/json");
            var o = ParseWorkshop(a.Body, out int count);
            o.Id = id;
            o.Page = PageOf(Workshop, id);
            // the mod's other files only when it has more than its main download
            if (count > 1 && o.NoFiles == null)
                o.Files = ParseWorkshopFiles(Ask(Workshop, WorkshopApi + "/mods/" + id + "/files", "application/json").Body, o.Version);
            return o;
        }

        /// <summary>A ModWorkshop mod and its main download; how many files it has.</summary>
        public static SiteOffer ParseWorkshop(string json, out int files)
        {
            var d = Json.Parse(json) as Dictionary<string, object> ?? throw new ModError("ModWorkshop answered nothing", "site_error");
            var vis = Json.Str(d, "visibility");
            if (Json.Bool(d, "suspended") || (vis != null && vis != "public" && vis != "unlisted"))
                throw new ModError("ModWorkshop: the mod is suspended or private", "site_missing");
            var o = new SiteOffer
            {
                Site = Workshop, Name = Json.Str(d, "name"), Version = Trimmed(Json.Str(d, "version")), Time = Unix(Json.Str(d, "bumped_at")),
                Author = Json.Str(Json.Obj(d, "user"), "name"), Summary = Trimmed(Json.Str(d, "short_desc")),
            };
            var thumb = Json.Obj(d, "thumbnail");
            var tf = Json.Str(thumb, "file");
            // WPF draws no WebP: a picture only in a kind it draws
            if (tf != null && Regex.IsMatch(tf, @"\.(png|jpe?g|gif|bmp)$", RegexOptions.IgnoreCase)) o.Picture = "https://storage.modworkshop.net/mods/images/" + tf;
            o.Changelog = (Json.Str(d, "changelog") ?? "").Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && x.Trim('!', '-', '=', '*').Length > 0).ToList();
            files = (int)Json.Long(d, "files_count");
            if (Json.Bool(d, "disable_mod_managers")) o.NoFiles = "author_off";
            else if (Json.Str(d, "download_type") == "link") o.NoFiles = "elsewhere";
            else if (Json.Obj(d, "download") is Dictionary<string, object> dl && WorkshopFile(dl, o.Version) is SiteFile f) o.Files.Add(f);
            return o;
        }

        static List<SiteFile> ParseWorkshopFiles(string json, string version)
        {
            var d = Json.Parse(json) as Dictionary<string, object>;
            return Json.List(d, "data").OfType<Dictionary<string, object>>().Select(f => WorkshopFile(f, version)).Where(f => f != null)
                       .OrderByDescending(f => f.Time).ToList();
        }

        static SiteFile WorkshopFile(Dictionary<string, object> f, string version)
        {
            var file = Json.Str(f, "file");
            var type = Json.Str(f, "type");
            if (file == null || !(IsArchive(file) || (type != null && new[] { "zip", "7z", "rar" }.Contains(type.ToLowerInvariant())))) return null;
            var id = Json.Long(f, "id").ToString(CultureInfo.InvariantCulture);
            var label = Trimmed(Json.Str(f, "label")) ?? Trimmed(Json.Str(f, "name"));
            return new SiteFile
            {
                Id = id, Label = label,
                // the name the site downloads it as: its label, with the stored file's extension
                Name = (label != null ? Regex.Replace(label, @"[\\/:*?""<>|]+", "_") : id) + Path.GetExtension(file),
                Version = Trimmed(Json.Str(f, "version")) ?? version, Time = Unix(Json.Str(f, "created_at")), Size = Json.Long(f, "size"),
                // the site's own download (it counts the download for the author, as its button does)
                Url = WorkshopApi + "/files/" + id + "/download",
            };
        }

        static SiteOffer ReadDirect(string url)
        {
            Answer a;
            try { a = Ask(Direct, url, "*/*", HttpMethod.Head); }
            catch (ModError e) when (e.Kind == "site_error") { a = null; }
            var o = new SiteOffer { Site = Direct, Id = url, Page = url };
            if (a != null)
            {
                o.ETag = a.Headers.ETag?.Tag;
                o.Time = a.Content.LastModified.HasValue ? a.Content.LastModified.Value.ToUnixTimeSeconds() : 0;
                o.Size = a.Content.ContentLength ?? 0;
            }
            var name = Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath));
            o.Name = Path.GetFileNameWithoutExtension(name);
            o.Version = o.Time > 0 ? Date(o.Time) : null;
            o.Files.Add(new SiteFile { Id = url, Name = name, Url = url, Time = o.Time, Size = o.Size, Version = o.Version });
            return o;
        }

        static string Decoded(string s) => s == null ? null : WebUtility.HtmlDecode(s).Trim();

        static string Trimmed(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        internal static long Unix(string iso) =>
            !string.IsNullOrEmpty(iso) && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeSeconds() : 0;

        /// <summary>A time as a version, for a page that writes none: the day, in UTC.</summary>
        public static string Date(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

        // ------------------------------------------------------------ the check

        /// <summary>Slack between a page's time and a download's: the author's upload and the
        /// player's download a minute apart are the same version.</summary>
        const long Slack = 60;

        /// <summary>The mod checked against its page: u's Status, Current, Latest, LatestTime,
        /// Download, LatestAsset, LatestTag (the site's file id), Page, Why and Changes.</summary>
        public static void Check(Mod m, UpdateInfo u)
        {
            var o = Read(m.Page);
            if (o == null) { u.Status = "no_check"; return; }
            Decide(m, o, u);
        }

        /// <summary>The decision, apart from the asking (tests give it an offer).</summary>
        public static void Decide(Mod m, SiteOffer o, UpdateInfo u)
        {
            var got = m.PageGot;
            u.Page = o.Page ?? u.Page;
            string archive = ArchiveName(m);

            if (o.Site == Direct)
            {
                var f = o.Files.FirstOrDefault();
                u.Latest = o.Version ?? (o.ETag != null ? o.ETag.Trim('"', 'W', '/') : null);
                u.LatestTime = o.Time;
                u.Download = f?.Url;
                u.LatestAsset = f?.Name;
                u.LatestTag = o.ETag;
                u.Current = got?.Version;
                bool? changed = got == null ? (bool?)null
                    : got.ETag != null && o.ETag != null ? got.ETag != o.ETag
                    : got.Time > 0 && o.Time > 0 ? o.Time > got.Time + Slack
                    : got.Size > 0 && o.Size > 0 ? got.Size != o.Size
                    : (bool?)null;
                u.Status = changed == true ? "update" : changed == false ? "current" : "unknown";
                // the same date both sides, another file (its ETag): "a newer file", not "X -> X"
                if (u.Status == "update" && u.Current == u.Latest) u.Current = null;
                return;
            }

            // the file installed: the site's file id recorded, its MD5, its name
            var installed = o.Files.FirstOrDefault(f => got?.File != null && f.Id == got.File)
                         ?? o.Files.FirstOrDefault(f => m.ArchiveMd5 != null && string.Equals(f.Md5, m.ArchiveMd5, StringComparison.OrdinalIgnoreCase))
                         ?? o.Files.FirstOrDefault(f => archive != null && string.Equals(f.Name, archive, StringComparison.OrdinalIgnoreCase));
            // this mod's files: its line (the same label or name) when the installed one is known,
            // else the only file the page has
            var line = installed ?? (got?.Name != null || got?.Label != null ? new SiteFile { Id = got.File, Name = got.Name, Label = got.Label } : null);
            var mine = line != null ? o.Files.Where(f => SameLine(f, line)).ToList()
                     : archive != null && o.Files.Count(f => Skeleton(f.Name) == Skeleton(archive)) == 1 ? o.Files.Where(f => Skeleton(f.Name) == Skeleton(archive)).ToList()
                     : o.Files.Count == 1 ? o.Files.ToList() : new List<SiteFile>();
            var newest = mine.OrderByDescending(f => f.Time).FirstOrDefault();
            long since = installed?.Time > 0 ? installed.Time : got?.Time ?? 0;
            long latestTime = newest?.Time > 0 ? newest.Time : o.Time;
            string latest = newest?.Version ?? o.Version ?? (latestTime > 0 ? Date(latestTime) : null);
            string own = installed?.Version ?? got?.Version;

            u.Current = own ?? Origins.VersionOf(m).Version;
            u.Latest = latest;
            u.LatestTime = latestTime;
            u.Download = newest?.Url;
            u.LatestAsset = newest?.Name;
            u.LatestTag = newest?.Id;
            u.LatestLabel = newest?.Label;

            bool? newer;
            if (newest != null && installed != null) newer = newest.Id != installed.Id && newest.Time > installed.Time;
            else if (own != null && o.Version != null && newest == null)
                newer = !Same(own, o.Version) && (since == 0 || latestTime == 0 || latestTime > since);
            else if (since > 0 && latestTime > 0) newer = latestTime > since + Slack;
            else newer = null;
            // a version the page writes that is the one the folder or its name says is current
            if (newer != false && latest != null && Origins.VersionOf(m).Version is string mineV && Same(mineV, latest)) newer = false;

            u.Status = newer == true ? "update" : newer == false ? "current" : latest != null ? "unknown" : "current";
            if (u.Status == "current") { u.Latest = u.Latest ?? u.Current; u.Download = null; }
            if (u.Status != "current" && u.Download == null)
                u.Why = o.NoFiles != null ? "page_only_" + o.NoFiles : o.Files.Count > 1 ? "page_only_several" : null;
            if (u.Status == "update" && o.Changelog.Count > 0)
            {
                // the changelog's lines above the one of the installed version
                var c = new Change { Version = latest };
                foreach (var said in o.Changelog)
                {
                    if (u.Current != null && Regex.IsMatch(said, @"(?<![\w.])v?" + Regex.Escape(u.Current.TrimStart('v', 'V')) + @"(?![\w.])", RegexOptions.IgnoreCase)) break;
                    if (c.Lines.Count == 20) break;
                    c.Lines.Add(said);
                }
                if (c.Lines.Count > 0) u.Changes.Add(c);
            }
        }

        static bool Same(string a, string b) => string.Equals((a ?? "").Trim().TrimStart('v', 'V'), (b ?? "").Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

        /// <summary>Two files of one line: the same label, or the same name once the site's random
        /// suffix (GameBanana: _8430d) and the digits are left out.</summary>
        static bool SameLine(SiteFile a, SiteFile b) =>
            (a.Id != null && a.Id == b.Id) || (a.Label != null && b.Label != null && string.Equals(a.Label, b.Label, StringComparison.OrdinalIgnoreCase)) || (a.Name != null && b.Name != null && Skeleton(a.Name) == Skeleton(b.Name));

        public static string Skeleton(string name)
        {
            var stem = Path.GetFileNameWithoutExtension(name ?? "");
            stem = Regex.Replace(stem, @"_[0-9a-f]{5}$", "", RegexOptions.IgnoreCase);
            return GitHubUpdates.Skeleton(stem);
        }

        /// <summary>The archive the mod was added from, by its file name, when it was one.</summary>
        static string ArchiveName(Mod m)
        {
            if (string.IsNullOrEmpty(m.Source) || m.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
            try { var n = Path.GetFileName(m.Source); return IsArchive(n) ? n : null; }
            catch (ArgumentException) { return null; }
        }

        // ------------------------------------------------------------ downloads

        /// <summary>A file downloaded into folder, whole or not at all, with the Zone.Identifier a
        /// browser writes (the page it came from, the address it came from) and what the page said
        /// of it (PageRef), so adding it records where it came from as a drop of a browser's
        /// download does. The file's path.</summary>
        public static string Download(string url, string page, PageRef said, string folder, Action<long, long> progress = null)
        {
            Directory.CreateDirectory(folder);
            HttpResponseMessage r;
            var (site, _) = Of(page);
            var q = new HttpRequestMessage(HttpMethod.Get, url);
            try { r = http.SendAsync(q, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult(); }
            catch (HttpRequestException e) { throw new ModError("the download did not start: " + e.Message, "offline"); }
            using (r)
            {
                if (!r.IsSuccessStatusCode) throw new ModError("the download was refused: " + (int)r.StatusCode, "download_refused");
                var final = r.RequestMessage?.RequestUri?.ToString() ?? url;
                var name = FileName(r, final, url);
                if (!IsArchive(name)) throw new ModError("the link is not a zip, 7z or rar: " + name, "not_archive", name);
                var dest = Free(Paths.Join(folder, name));
                var tmp = dest + ".part";
                long total = r.Content.Headers.ContentLength ?? 0, done = 0;
                try
                {
                    using (var src = r.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    {
                        var buf = new byte[1 << 16];
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0) { dst.Write(buf, 0, n); done += n; progress?.Invoke(done, total); }
                    }
                }
                catch (Exception e) when (e is IOException || e is HttpRequestException || e is WebException)
                {
                    TryDelete(tmp);
                    throw new ModError("the download broke off at " + done + " of " + total + " bytes: " + e.Message, "download_cut");
                }
                if (total > 0 && done != total) { TryDelete(tmp); throw new ModError("the download broke off at " + done + " of " + total + " bytes", "download_cut"); }
                File.Move(tmp, dest);
                said = said ?? new PageRef();
                if (site == Direct)
                {
                    said.ETag = said.ETag ?? r.Headers.ETag?.Tag;
                    if (said.Time == 0 && r.Content.Headers.LastModified.HasValue) said.Time = r.Content.Headers.LastModified.Value.ToUnixTimeSeconds();
                    if (said.Size == 0) said.Size = done;
                    if (said.Version == null && said.Time > 0) said.Version = Date(said.Time);
                }
                // HostUrl: the address asked for (a GitHub asset's says its release; where it went on to does not)
                Native.WriteStream(dest, "Zone.Identifier", Zone(url, page ?? url, said));
                return dest;
            }
        }

        /// <summary>Zone.Identifier as a browser writes it, with what the page said in keys of our
        /// own (Windows reads only ZoneId).</summary>
        public static string Zone(string host, string referrer, PageRef said)
        {
            var s = new StringBuilder("[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=" + referrer + "\r\nHostUrl=" + host + "\r\n");
            if (said != null)
            {
                if (said.Version != null) s.Append("Hd2qdVersion=" + said.Version.Replace("\r", " ").Replace("\n", " ") + "\r\n");
                if (said.File != null) s.Append("Hd2qdFile=" + said.File + "\r\n");
                if (said.Time > 0) s.Append("Hd2qdTime=" + said.Time + "\r\n");
                if (said.ETag != null) s.Append("Hd2qdETag=" + said.ETag + "\r\n");
                if (said.Name != null) s.Append("Hd2qdName=" + Line1(said.Name) + "\r\n");
                if (said.Label != null) s.Append("Hd2qdLabel=" + Line1(said.Label) + "\r\n");
                if (said.Size > 0) s.Append("Hd2qdSize=" + said.Size + "\r\n");
            }
            return s.ToString();
        }

        static string Line1(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

        /// <summary>What a download's Zone.Identifier says the page offered (Zone), else null.</summary>
        public static PageRef FromZone(string zone)
        {
            string F(string key) { var m = Regex.Match(zone ?? "", "^" + key + @"=(.+)$", RegexOptions.Multiline); return m.Success ? m.Groups[1].Value.Trim() : null; }
            var p = new PageRef { Version = F("Hd2qdVersion"), File = F("Hd2qdFile"), ETag = F("Hd2qdETag"), Name = F("Hd2qdName"), Label = F("Hd2qdLabel") };
            long.TryParse(F("Hd2qdTime"), out p.Time);
            long.TryParse(F("Hd2qdSize"), out p.Size);
            return p.Version == null && p.File == null && p.ETag == null && p.Time == 0 && p.Size == 0 ? null : p;
        }

        static string FileName(HttpResponseMessage r, string final, string asked)
        {
            var cd = r.Content.Headers.ContentDisposition;
            var name = (cd?.FileNameStar ?? cd?.FileName)?.Trim('"');
            // ModWorkshop's storage sends filename="Mr%20Pizza%20Portable%20Hellbomb.zip"
            if (name != null && Regex.IsMatch(name, "%[0-9A-Fa-f]{2}")) name = Uri.UnescapeDataString(name);
            if (string.IsNullOrWhiteSpace(name))
            {
                // ModWorkshop's storage: ...?filename=Mr%20Pizza%20Portable%20Hellbomb.zip
                var fq = Regex.Match(new Uri(final).Query, @"(?:^|[?&])filename=([^&]+)");
                name = fq.Success ? Uri.UnescapeDataString(fq.Groups[1].Value.Replace('+', ' ')) : Uri.UnescapeDataString(Path.GetFileName(new Uri(final).AbsolutePath));
                if (!IsArchive(name)) name = Uri.UnescapeDataString(Path.GetFileName(new Uri(asked).AbsolutePath));
            }
            name = Regex.Replace(Path.GetFileName(name), @"[\\/:*?""<>|]+", "_").Trim();
            return name.Length == 0 ? "download.zip" : name;
        }

        static string Free(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int n = 2; ; n++) { var p = Path.Combine(dir, stem + " (" + n + ")" + ext); if (!File.Exists(p)) return p; }
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>A mod's newer version from its page downloaded and put in its place (its place
        /// in the order, on/off and options kept), as ApplyGitHub does for a release.</summary>
        public static Mod Apply(string modId, UpdateInfo u, Action<long, long> progress = null)
        {
            if (string.IsNullOrEmpty(u.Download)) throw new ModError("the page offers no file to download here: it opens in a browser", "page_only");
            var said = new PageRef { Version = u.Latest, File = u.LatestTag, Time = u.LatestTime, Name = u.LatestAsset, Label = u.LatestLabel };
            var lib0 = Library.Load();
            var page = lib0.Mods.FirstOrDefault(m => m.Id == modId)?.Page ?? u.Page;
            if (Of(page).Site == Direct) said = new PageRef();       // the server's own, from the download
            var file = Download(u.Download, page, said, Store.ModsDir(), progress);
            try
            {
                var lib = Library.Load();
                var mod = lib.Mods.FirstOrDefault(m => m.Id == modId) ?? throw new ModError("not in the library any more", "gone");
                var unpacked = Ops.SourceFolder(file, Store.Config());
                var manifest = Manifest.Read(unpacked);
                var md5 = Fingerprints.OfArchive(file);
                var fresh = new Mod();
                Origins.FromFile(fresh, file);
                Versions.Keep(lib, Store.Config(), mod, unpacked);
                mod.Options = Manifest.Merged(manifest, mod.Options);
                mod.Path = unpacked;
                mod.Source = page;
                mod.ArchiveMd5 = md5;
                mod.PageGot = fresh.PageGot;
                if (mod.RealName == null && !string.IsNullOrEmpty(manifest?.Name)) mod.Name = manifest.Name;
                lib.Save();
                return mod;
            }
            finally { TryDelete(file); }
        }

        /// <summary>The files a link offers to add: a page of one of these sites, a GitHub repository
        /// or release, a direct link. Empty with Why for a page whose files need its own site
        /// (AyakaMods, Nexus Mods: a sign-in) or that offers none.</summary>
        public static (string Page, string Site, List<SiteFile> Files, string Why) Offered(string url)
        {
            var o = Look(url);
            return (o.Page, o.Shown, o.Files, o.Files.Count == 0 ? o.NoFiles ?? "no_files" : null);
        }

        /// <summary>What a link's page says and the files it offers to add, for the download page's
        /// look-up (the user, 2026-11-23: one box for a Nexus number and a link, a link's mod shown
        /// as a Nexus number's is): Shown is the site as the window names it; NoFiles says why
        /// there is nothing to download here (sign_in for AyakaMods and Nexus Mods, unknown_page
        /// for a page of no site this program knows). A Nexus Mods page is not read here: the
        /// window looks its number up (Nexus.LookUp).</summary>
        public static SiteOffer Look(string url)
        {
            url = (url ?? "").Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) throw new ModError("not an address: " + url, "bad_url");
            var probe = new Mod();
            Origins.FromUrl(probe, url);
            if (probe.Nexus != null) return new SiteOffer { Site = "nexus", Shown = "Nexus Mods", Page = Nexus.Page(probe.Nexus), NoFiles = "sign_in" };
            if (probe.GitHub != null)
            {
                var all = GitHubUpdates.List(probe.GitHub);
                var tag = probe.GitHub.Tag;
                var pick = tag != null ? all.Where(a => string.Equals(a.Tag, tag, StringComparison.OrdinalIgnoreCase)).ToList()
                                       : all.Where(a => !a.Prerelease).GroupBy(a => a.Tag).Select(g => g.ToList()).FirstOrDefault() ?? new List<GitHubUpdates.Asset>();
                if (probe.GitHub.Asset != null) pick = pick.Where(a => string.Equals(a.Name, probe.GitHub.Asset, StringComparison.OrdinalIgnoreCase)).ToList();
                var files = pick.Select(a => new SiteFile { Id = a.Name, Name = a.Name, Version = GitHubUpdates.Label(a.Tag, a.Name), Url = a.Url, Time = a.Uploaded, Size = a.Size }).ToList();
                return new SiteOffer
                {
                    Site = "github", Shown = "GitHub", Name = probe.GitHub.Repo, Author = probe.GitHub.Owner,
                    Page = pick.FirstOrDefault()?.Page ?? "https://github.com/" + probe.GitHub.Owner + "/" + probe.GitHub.Repo + "/releases",
                    Version = pick.FirstOrDefault()?.Tag, Time = files.Count > 0 ? files.Max(f => f.Time) : 0,
                    Files = files, NoFiles = files.Count == 0 ? "no_files" : null,
                };
            }
            var (site, _) = Of(url);
            if (site == null) return new SiteOffer { Page = url, NoFiles = "unknown_page" };
            var o = Read(url);
            o.Shown = site == Direct ? new Uri(url).Host : site;
            if (o.Files.Count == 0 && o.NoFiles == null) o.NoFiles = "no_files";
            return o;
        }

        /// <summary>One file a link offers, downloaded into the mod folder with its origin written as
        /// a browser's download carries it: adding the file (the window's drop, hd2qd add) records
        /// the page. The file's path; the caller adds it, then deletes it.</summary>
        public static string Fetch(string page, SiteFile f, Action<long, long> progress = null)
        {
            var (site, _) = Of(page);
            var said = site == Direct ? new PageRef() : new PageRef { Version = f.Version, File = site == null ? null : f.Id, Time = f.Time, Name = f.Name, Label = f.Label };
            // a GitHub asset: its address says the release, which FromFile reads from HostUrl
            return Download(f.Url, page, said, Store.ModsDir(), progress);
        }
    }
}
