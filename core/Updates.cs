using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace Hd2Mod
{
    /// <summary>One mod's update check.</summary>
    public class UpdateInfo
    {
        public string ModId, Name, Channel, Page;
        /// <summary>"update" (newer there), "skipped" (newer there, but the player skipped that one),
        /// "current", "unknown" (the mod's own version is not known), "no_source", "no_check" (a site
        /// without a check), "error".</summary>
        public string Status;
        public string Current, Latest, Why;
        /// <summary>GitHub: the release asset to download; Nexus: the newest file's id.</summary>
        public string Download;
        public long LatestFile, LatestTime;
        /// <summary>Nexus Mods: what each version since the installed one changed, newest first, as
        /// its author wrote it on the page (the user, 2026-11-04); empty when the page says nothing.</summary>
        public List<Change> Changes = new List<Change>();
    }

    /// <summary>One version's changes: its version and the author's lines.</summary>
    public class Change
    {
        public string Version;
        public List<string> Lines = new List<string>();
    }

    /// <summary>Update checks on each mod's own channel (the user, 2026-10-24: "从 GitHub 下下来的，
    /// 就从 GitHub 检查更新"): Nexus Mods through its public API (v2: the files, their groups and
    /// categories), with no key, in every build; GitHub through its releases (latest).
    /// Read only; nothing is downloaded unless the player asks.</summary>
    public static class Updates
    {
        public static string GitHubApi = Environment.GetEnvironmentVariable("HD2QD_GITHUB_API") ?? "https://api.github.com";

        static readonly HttpClient http = MakeClient();

        static HttpClient MakeClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("HD2ModQuickDeploy/" + StateDoc.Version);
            c.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return c;
        }

        internal static string Get(string url)
        {
            HttpResponseMessage r;
            try { r = http.GetAsync(url).GetAwaiter().GetResult(); }
            catch (HttpRequestException e) { throw new ModError("cannot be reached: " + e.Message, "offline"); }
            using (r)
            {
                var body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if ((int)r.StatusCode == 404) throw new ModError("no such repository or no release", "missing");
                if ((int)r.StatusCode == 403 || (int)r.StatusCode == 429) throw new ModError("too many requests for now", "busy");
                if (!r.IsSuccessStatusCode) throw new ModError("answered " + (int)r.StatusCode, "error");
                return body;
            }
        }

        /// <summary>A file, as bytes (a release's asset: GitHub sends it on to its storage).</summary>
        internal static byte[] GetBytes(string url)
        {
            var q = new HttpRequestMessage(HttpMethod.Get, url);
            q.Headers.Accept.Clear();
            q.Headers.Accept.ParseAdd("application/octet-stream");
            HttpResponseMessage r;
            try { r = http.SendAsync(q).GetAwaiter().GetResult(); }
            catch (HttpRequestException e) { throw new ModError("cannot be reached: " + e.Message, "offline"); }
            using (r)
            {
                if ((int)r.StatusCode == 404) throw new ModError("no such file: " + url, "missing");
                if ((int)r.StatusCode == 403 || (int)r.StatusCode == 429) throw new ModError("too many requests for now", "busy");
                if (!r.IsSuccessStatusCode) throw new ModError("answered " + (int)r.StatusCode, "error");
                return r.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
        }

        /// <summary>Every mod checked (progress: done, all). Mods without an origin first learn it
        /// (Learn), unless the caller did already.</summary>
        public static List<UpdateInfo> Check(Library lib, Action<int, int> progress = null, bool learn = true)
        {
            if (learn && Learn(lib, out _)) lib.Save();
            var out_ = new List<UpdateInfo>();
            int i = 0;
            bool busy = false;
            // when each Nexus mod was last updated there, asked once for the whole check
            Dictionary<long, long> recent = null;
            if (lib.Mods.Any(m => m.Nexus != null))
                try { recent = UpdatedAt(lib.Mods.Where(m => m.Nexus != null).Select(m => m.Nexus)); }
                catch (ModError e) { busy = e.Kind == "nexus_busy"; }
            foreach (var m in lib.Mods)
            {
                UpdateInfo u;
                if (!busy && recent != null && m.Nexus != null && Unchanged(m, recent) is UpdateInfo kept) u = kept;
                else
                // once Nexus Mods says too many requests, it is not asked again in this check:
                // the rest of its mods say so without a request
                u = busy && m.Nexus != null
                    ? new UpdateInfo { ModId = m.Id, Name = m.Name, Channel = Origins.Of(m).Channel, Page = Origins.Of(m).Page, Status = "error", Why = "nexus_busy" }
                    : One(m, false);
                Versions.Skipped(m, u);
                busy |= u.Why == "nexus_busy";
                out_.Add(u);
                progress?.Invoke(++i, lib.Mods.Count);
            }
            SaveChecks();
            return out_;
        }

        /// <summary>Origins learnt before a check: the one HD2 Arsenal recorded, then what Nexus Mods
        /// knows the archive's bytes as (Fingerprints). Whether the library changed (an MD5 taken
        /// counts), and how many mods learnt their source from the fingerprint.</summary>
        public static bool Learn(Library lib, out int known)
        {
            int arsenal = Origins.FromArsenal(lib);
            var had = lib.Mods.Count(m => m.ArchiveMd5 != null);
            known = Fingerprints.Learn(lib);
            return arsenal > 0 || known > 0 || lib.Mods.Count(m => m.ArchiveMd5 != null) != had;
        }

        /// <summary>One mod checked on its own channel. A check that cannot be made is a
        /// result too: "no_source", "no_check", or "error" with its reason in Why.</summary>
        public static UpdateInfo One(Mod m) => Versions.Skipped(m, One(m, true));

        static UpdateInfo One(Mod m, bool save)
        {
            var (channel, page) = Origins.Of(m);
            var u = new UpdateInfo { ModId = m.Id, Name = m.Name, Channel = channel, Page = page };
            try
            {
                if (m.Nexus != null)
                {
                    CheckNexus(m.Nexus, u);
                    if (save) SaveChecks();
                }
                else if (m.GitHub != null) CheckGitHub(m.GitHub, u);
                else u.Status = m.Page != null ? "no_check" : "no_source";
            }
            catch (ModError e) { u.Status = "error"; u.Why = e.Kind ?? e.Message; }
            return u;
        }

        // ------------------------------------------------------------ Nexus Mods, without a key

        // Nexus Mods' public GraphQL API (v2) answers what a mod's page shows to anyone, with no key
        // and no sign-in, so every build checks Nexus mods (the user, 2026-11-03). A file's updates
        // share its groupId; the files' categories say which are main and which are old.

        const string FilesQuery = "query Files($mod: ID!, $game: ID!) { modFiles(modId: $mod, gameId: $game) { fileId name version category date groupId changelogText } }";
        const string UpdatedQuery = "query Updated($ids: [CompositeDomainWithIdInput!]!) { legacyModsByDomain(ids: $ids) { nodes { modId updatedAt } } }";
        const string GameQuery = "query Game($domain: String) { game(domainName: $domain) { id } }";

        static readonly Dictionary<string, long> gameIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A game's number on Nexus Mods (helldivers2: 6119), asked once.</summary>
        internal static long GameId(string domain)
        {
            lock (gameIds)
                if (gameIds.TryGetValue(domain, out var known)) return known;
            var id = Json.Long(Json.Obj(Nexus.PostGraphQL(GameQuery, new Dictionary<string, object> { ["domain"] = domain }), "game"), "id");
            if (id <= 0) throw new ModError("Nexus Mods has no game " + domain, "nexus_missing");
            lock (gameIds) gameIds[domain] = id;
            return id;
        }

        /// <summary>A mod's files on its page: fileId, name, version, category, date, groupId, changelogText.</summary>
        static List<Dictionary<string, object>> Files(NexusRef r)
        {
            var d = Nexus.PostGraphQL(FilesQuery, new Dictionary<string, object>
            {
                ["mod"] = r.Mod.ToString(CultureInfo.InvariantCulture), ["game"] = GameId(r.Game).ToString(CultureInfo.InvariantCulture),
            });
            return Json.List(d, "modFiles").OfType<Dictionary<string, object>>().ToList();
        }

        /// <summary>When each mod was last updated on Nexus Mods (Unix seconds), fifty mods a request.</summary>
        static Dictionary<long, long> UpdatedAt(IEnumerable<NexusRef> refs)
        {
            var out_ = new Dictionary<long, long>();
            var all = refs.GroupBy(r => (r.Game, r.Mod)).Select(g => g.First()).ToList();
            for (int at = 0; at < all.Count; at += 50)
            {
                var ids = all.Skip(at).Take(50).Select(r => (object)new Dictionary<string, object> { ["gameDomain"] = r.Game, ["modId"] = r.Mod }).ToList();
                var d = Nexus.PostGraphQL(UpdatedQuery, new Dictionary<string, object> { ["ids"] = ids });
                foreach (var n in Json.List(Json.Obj(d, "legacyModsByDomain"), "nodes").OfType<Dictionary<string, object>>())
                    out_[Json.Long(n, "modId")] = Unix(Json.Str(n, "updatedAt"));
            }
            return out_;
        }

        static long Unix(string iso) =>
            DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeSeconds() : 0;

        static void CheckNexus(NexusRef r, UpdateInfo u)
        {
            var files = Files(r);
            u.Current = r.Version;
            Dictionary<string, object> latest = null;
            // the installed file's upload time, the start of what changed since
            long since = r.Time;
            if (r.File > 0)
            {
                var mine = files.FirstOrDefault(f => Json.Long(f, "fileId") == r.File);
                if (mine != null) since = Json.Long(mine, "date");
                if (mine != null)
                {
                    if (string.IsNullOrEmpty(u.Current)) u.Current = Json.Str(mine, "version");
                    // the newest file of the mod's own line (its updates share the group), else, when
                    // the file itself is now old or archived, the page's newest main file
                    var group = Json.Long(mine, "groupId");
                    var newest = group == 0 ? null : files.Where(f => Json.Long(f, "groupId") == group).OrderByDescending(f => Json.Long(f, "date")).FirstOrDefault();
                    if (newest != null && Json.Long(newest, "fileId") != r.File && Json.Long(newest, "date") > Json.Long(mine, "date")) latest = newest;
                    else
                    {
                        var cat = Json.Str(mine, "category") ?? "";
                        if (cat == "OLD_VERSION" || cat == "ARCHIVED") latest = NewestMain(files);
                    }
                }
                // the file is no longer on the page: the page's newest main file
                else latest = NewestMain(files);
                if (latest != null && Json.Long(latest, "fileId") == r.File) latest = null;
                u.Status = latest != null ? "update" : "current";
            }
            else
            {
                latest = NewestMain(files);
                if (latest == null) u.Status = "current";
                else if (r.Time > 0) u.Status = Json.Long(latest, "date") > r.Time + 3600 ? "update" : "current";
                else u.Status = "unknown";
                if (u.Status != "update" && u.Status != "unknown") latest = null;
            }
            if (latest != null)
            {
                u.Latest = Json.Str(latest, "version") ?? Json.Str(latest, "name");
                u.LatestFile = Json.Long(latest, "fileId");
                u.LatestTime = Json.Long(latest, "date");
                u.Download = u.LatestFile.ToString(CultureInfo.InvariantCulture);
                u.Page = Nexus.Page(r) + "?tab=files";
                u.Changes = ChangesSince(files, latest, since);
            }
            Remember(r, u);
        }

        /// <summary>What the versions after the installed one, up to the newest, changed: the files of
        /// the newest one's line uploaded after the installed file, newest first, each version once.
        /// A line is every file that shares a group or a name (without an archive's extension) with
        /// the newest, or with a file that does, and so on: on the page the versions of one file are
        /// often in groups of their own and named with or without ".zip" (Nexus mod 8073,
        /// 2026-11-04: eight files, five groups, two names, every one linked); an optional file has
        /// a name and group of its own. Only the newest's own when the installed file's time is not
        /// known.</summary>
        static List<Change> ChangesSince(List<Dictionary<string, object>> files, Dictionary<string, object> latest, long since)
        {
            long until = Json.Long(latest, "date");
            string NameOf(Dictionary<string, object> f) =>
                System.Text.RegularExpressions.Regex.Replace((Json.Str(f, "name") ?? "").Trim(), @"\.(zip|7z|rar)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).ToLowerInvariant();
            var line = new HashSet<Dictionary<string, object>> { latest };
            var groups = new HashSet<long>();
            var names = new HashSet<string>();
            for (bool grew = true; grew;)
            {
                foreach (var f in line)
                {
                    if (Json.Long(f, "groupId") != 0) groups.Add(Json.Long(f, "groupId"));
                    if (NameOf(f).Length > 0) names.Add(NameOf(f));
                }
                grew = false;
                foreach (var f in files)
                    if (!line.Contains(f) && (groups.Contains(Json.Long(f, "groupId")) || names.Contains(NameOf(f)))) { line.Add(f); grew = true; }
            }
            var shown = since <= 0 ? new List<Dictionary<string, object>> { latest }
                      : line.Where(f => f == latest || (Json.Long(f, "date") > since && Json.Long(f, "date") <= until)).ToList();
            var out_ = new List<Change>();
            foreach (var f in shown.OrderByDescending(f => Json.Long(f, "date")))
            {
                var lines = Json.List(f, "changelogText").OfType<string>().Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                var version = Json.Str(f, "version") ?? Json.Str(f, "name") ?? "";
                // one version uploaded twice (an old and an archived copy) says it once
                if (lines.Count == 0 || out_.Any(c => c.Version == version && c.Lines.SequenceEqual(lines))) continue;
                out_.Add(new Change { Version = version, Lines = lines });
            }
            return out_;
        }

        // ------------------------------------------------------------ fewer requests to Nexus Mods

        // A check asks Nexus Mods once when each of the library's mods was last updated there; a mod
        // whose last full check is newer than that, and less than a month old, keeps that check's
        // answer without a request of its own (the user, 2026-10-27: a check of 150 mods was 150
        // requests). The answers are kept in nexus_checks.json, by the file the mod is.

        /// <summary>The time, in Unix seconds; tests move it on.</summary>
        public static Func<long> Now = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>A check this old is done in full again.</summary>
        const long Month = 27L * 24 * 3600;

        // the answers, read once for the settings folder they belong to (another library, another file)
        static Dictionary<string, object> checks;
        static string checksFrom;

        static Dictionary<string, object> Checks()
        {
            var from = Store.PathOf("nexus_checks.json");
            if (checks == null || checksFrom != from)
            {
                checks = Json.Obj(Store.Load("nexus_checks.json"), "mods") ?? new Dictionary<string, object>();
                checksFrom = from;
            }
            return checks;
        }

        static void SaveChecks()
        {
            if (checks == null || checksFrom != Store.PathOf("nexus_checks.json")) return;
            try { Store.Save("nexus_checks.json", new Dictionary<string, object> { ["mods"] = checks }); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        static string CheckKey(NexusRef r) => r.Game + "/" + r.Mod + "/" + r.File + "/" + r.Version + "/" + r.Time;

        static void Remember(NexusRef r, UpdateInfo u)
        {
            if (u.Status != "update" && u.Status != "current" && u.Status != "unknown") return;
            Checks()[CheckKey(r)] = new Dictionary<string, object>
            {
                ["checked_at"] = Now(), ["status"] = u.Status, ["current"] = u.Current, ["latest"] = u.Latest,
                ["download"] = u.Download, ["latest_file"] = u.LatestFile, ["latest_time"] = u.LatestTime, ["page"] = u.Page,
                ["changes"] = u.Changes.Select(c => (object)new Dictionary<string, object> { ["version"] = c.Version, ["lines"] = c.Lines.Cast<object>().ToList() }).ToList(),
            };
        }

        /// <summary>The last full check's answer, when Nexus Mods says the mod was not updated since
        /// and the check is less than a month old; else null (a request is needed).</summary>
        static UpdateInfo Unchanged(Mod m, Dictionary<long, long> recent)
        {
            var d = Json.Obj(Checks(), CheckKey(m.Nexus));
            if (d == null) return null;
            var at = Json.Long(d, "checked_at");
            if (Now() - at > Month) return null;
            // an update kept from before the changes were kept is asked again, for them
            if (Json.Str(d, "status") == "update" && !d.ContainsKey("changes")) return null;
            if (!recent.TryGetValue(m.Nexus.Mod, out var changed) || changed >= at - 3600) return null;
            var (channel, page) = Origins.Of(m);
            return new UpdateInfo
            {
                ModId = m.Id, Name = m.Name, Channel = channel, Page = Json.Str(d, "page") ?? page, Status = Json.Str(d, "status"),
                Current = Json.Str(d, "current"), Latest = Json.Str(d, "latest"), Download = Json.Str(d, "download"),
                LatestFile = Json.Long(d, "latest_file"), LatestTime = Json.Long(d, "latest_time"),
                Changes = Json.List(d, "changes").OfType<Dictionary<string, object>>()
                    .Select(c => new Change { Version = Json.Str(c, "version"), Lines = Json.List(c, "lines").OfType<string>().ToList() }).ToList(),
            };
        }

        static Dictionary<string, object> NewestMain(List<Dictionary<string, object>> files) =>
            files.Where(f => (Json.Str(f, "category") ?? "") == "MAIN").OrderByDescending(f => Json.Long(f, "date")).FirstOrDefault();

        static void CheckGitHub(GitHubRef r, UpdateInfo u)
        {
            var d = Json.Parse(Get(GitHubApi.TrimEnd('/') + "/repos/" + r.Owner + "/" + r.Repo + "/releases/latest")) as Dictionary<string, object>;
            var tag = Json.Str(d, "tag_name");
            u.Current = r.Tag;
            u.Latest = tag;
            u.Page = Json.Str(d, "html_url") ?? u.Page;
            u.Download = Json.List(d, "assets").OfType<Dictionary<string, object>>()
                .Select(a => Json.Str(a, "browser_download_url"))
                .FirstOrDefault(x => x != null && new[] { ".zip", ".7z", ".rar" }.Any(e => x.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
            u.Status = string.IsNullOrEmpty(r.Tag) ? "unknown" : string.Equals(r.Tag, tag, StringComparison.OrdinalIgnoreCase) ? "current" : "update";
        }

        /// <summary>A GitHub release's archive downloaded into the mod folder and put in the library as
        /// the mod's new version (its place, on/off and options kept); the archive is not kept.</summary>
        public static Mod ApplyGitHub(string modId, UpdateInfo u)
        {
            if (string.IsNullOrEmpty(u.Download)) throw new ModError("the release has no zip, 7z or rar to download", "no_asset");
            var folder = Store.ModsDir();
            Directory.CreateDirectory(folder);
            var name = Path.GetFileName(new Uri(u.Download).AbsolutePath);
            var file = Paths.Join(folder, Uri.UnescapeDataString(name));
            HttpResponseMessage r;
            try { r = http.GetAsync(u.Download).GetAwaiter().GetResult(); }
            catch (HttpRequestException e) { throw new ModError("the download did not start: " + e.Message, "offline"); }
            using (r)
            {
                if (!r.IsSuccessStatusCode) throw new ModError("the download was refused: " + (int)r.StatusCode, "error");
                using (var dst = new FileStream(file, FileMode.Create, FileAccess.Write)) r.Content.CopyToAsync(dst).GetAwaiter().GetResult();
            }
            var lib = Library.Load();
            var mod = lib.Mods.FirstOrDefault(m => m.Id == modId) ?? throw new ModError("not in the library any more", "gone");
            var unpacked = Ops.SourceFolder(file, Store.Config());
            try { File.Delete(file); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            var manifest = Manifest.Read(unpacked);
            Versions.Keep(lib, Store.Config(), mod, unpacked);
            mod.Options = Manifest.Merged(manifest, mod.Options);
            mod.Path = unpacked;
            mod.Source = u.Page;
            mod.ArchiveMd5 = null;
            mod.GitHub = new GitHubRef { Owner = mod.GitHub?.Owner, Repo = mod.GitHub?.Repo, Tag = u.Latest };
            if (!string.IsNullOrEmpty(manifest?.Name)) mod.Name = manifest.Name;
            lib.Save();
            return mod;
        }
    }
}
