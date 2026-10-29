using System;
using System.Collections.Generic;
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
    }

    /// <summary>Update checks on each mod's own channel (the user, 2026-10-24: "从 GitHub 下下来的，
    /// 就从 GitHub 检查更新"): Nexus Mods through its API and the player's key (files.json and its
    /// file_updates chain: which file replaced which), GitHub through its releases (latest).
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

        /// <summary>Every mod checked (progress: done, all). Mods without an origin first learn the one
        /// HD2 Arsenal recorded, when it has.</summary>
        public static List<UpdateInfo> Check(Library lib, Action<int, int> progress = null)
        {
            if (Origins.FromArsenal(lib) > 0) lib.Save();
            var out_ = new List<UpdateInfo>();
            int i = 0;
            bool busy = false;
#if HAS_NEXUS
            // the game's mods updated in the last month, asked once for the whole check
            Dictionary<long, long> recent = null;
            if (lib.Mods.Any(m => m.Nexus != null))
                try
                {
                    var key = Nexus.Credential();
                    if (key != null) recent = RecentlyUpdated(key);
                }
                catch (ModError e) { busy = e.Kind == "nexus_busy"; }
#endif
            foreach (var m in lib.Mods)
            {
                UpdateInfo u;
#if HAS_NEXUS
                if (!busy && recent != null && m.Nexus != null && Unchanged(m, recent) is UpdateInfo kept) u = kept;
                else
#endif
                // once Nexus Mods says too many requests, it is not asked again in this check:
                // the rest of its mods say so without a request (its rate limit is the player's)
                u = busy && m.Nexus != null
                    ? new UpdateInfo { ModId = m.Id, Name = m.Name, Channel = Origins.Of(m).Channel, Page = Origins.Of(m).Page, Status = "error", Why = "nexus_busy" }
                    : One(m, false);
                Versions.Skipped(m, u);
                busy |= u.Why == "nexus_busy";
                out_.Add(u);
                progress?.Invoke(++i, lib.Mods.Count);
            }
#if HAS_NEXUS
            SaveChecks();
#endif
            return out_;
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
#if HAS_NEXUS
                    if (save) SaveChecks();
#endif
                }
                else if (m.GitHub != null) CheckGitHub(m.GitHub, u);
                else u.Status = m.Page != null ? "no_check" : "no_source";
            }
            catch (ModError e) { u.Status = "error"; u.Why = e.Kind ?? e.Message; }
            return u;
        }

        static void CheckNexus(NexusRef r, UpdateInfo u)
        {
#if !HAS_NEXUS
            u.Status = "no_check";
#else
            var key = Nexus.Credential();
            if (string.IsNullOrEmpty(key)) { u.Status = "error"; u.Why = "no_key"; return; }
            var d = Json.Parse(Nexus.Api("/v1/games/" + r.Game + "/mods/" + r.Mod + "/files.json", key)) as Dictionary<string, object>;
            var files = Json.List(d, "files").OfType<Dictionary<string, object>>().ToList();
            var byId = files.ToDictionary(f => Json.Long(f, "file_id"));
            var next = new Dictionary<long, long>();
            foreach (var x in Json.List(d, "file_updates").OfType<Dictionary<string, object>>())
                next[Json.Long(x, "old_file_id")] = Json.Long(x, "new_file_id");
            u.Current = r.Version;
            Dictionary<string, object> latest = null;
            if (r.File > 0)
            {
                // the chain of replacements from the file the mod is
                long id = r.File;
                var seen = new HashSet<long>();
                while (next.TryGetValue(id, out var newer) && seen.Add(id)) id = newer;
                if (id != r.File && byId.TryGetValue(id, out var f)) latest = f;
                else if (byId.TryGetValue(r.File, out var mine))
                {
                    if (string.IsNullOrEmpty(u.Current)) u.Current = Json.Str(mine, "version");
                    var cat = Json.Str(mine, "category_name") ?? "";
                    if (cat == "OLD_VERSION" || cat == "ARCHIVED") latest = NewestMain(files);
                }
                u.Status = latest != null ? "update" : "current";
            }
            else
            {
                latest = NewestMain(files);
                if (latest == null) u.Status = "current";
                else if (r.Time > 0) u.Status = Json.Long(latest, "uploaded_timestamp") > r.Time + 3600 ? "update" : "current";
                else u.Status = "unknown";
                if (u.Status != "update" && u.Status != "unknown") latest = null;
            }
            if (latest != null)
            {
                u.Latest = Json.Str(latest, "version") ?? Json.Str(latest, "name");
                u.LatestFile = Json.Long(latest, "file_id");
                u.LatestTime = Json.Long(latest, "uploaded_timestamp");
                u.Download = u.LatestFile.ToString();
                u.Page = Nexus.Page(r) + "?tab=files";
            }
            Remember(r, u);
#endif
        }

#if HAS_NEXUS
        // ------------------------------------------------------------ fewer requests to Nexus Mods

        // A check asks Nexus Mods once which of the game's mods were updated in the last month; a mod
        // whose last full check is newer than its last update there, and less than a month old, keeps
        // that check's answer without a request of its own (the user, 2026-10-27: a check of 150 mods
        // was 150 requests). The answers are kept in nexus_checks.json, by the file the mod is.

        /// <summary>The time, in Unix seconds; tests move it on.</summary>
        public static Func<long> Now = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>A check this old is done in full again: updated.json's "1m" reaches a month back.</summary>
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

        /// <summary>The mod ids updated in the last month, with when.</summary>
        static Dictionary<long, long> RecentlyUpdated(string key)
        {
            var list = Json.Parse(Nexus.Api("/v1/games/" + Nexus.Game + "/mods/updated.json?period=1m", key)) as List<object>;
            var out_ = new Dictionary<long, long>();
            foreach (var x in (list ?? new List<object>()).OfType<Dictionary<string, object>>())
                out_[Json.Long(x, "mod_id")] = Math.Max(Json.Long(x, "latest_file_update"), Json.Long(x, "latest_mod_activity"));
            return out_;
        }

        static void Remember(NexusRef r, UpdateInfo u)
        {
            if (u.Status != "update" && u.Status != "current" && u.Status != "unknown") return;
            Checks()[CheckKey(r)] = new Dictionary<string, object>
            {
                ["checked_at"] = Now(), ["status"] = u.Status, ["current"] = u.Current, ["latest"] = u.Latest,
                ["download"] = u.Download, ["latest_file"] = u.LatestFile, ["latest_time"] = u.LatestTime, ["page"] = u.Page,
            };
        }

        /// <summary>The last full check's answer, when Nexus Mods lists no update of the mod since and the
        /// check is less than a month old; else null (a request is needed).</summary>
        static UpdateInfo Unchanged(Mod m, Dictionary<long, long> recent)
        {
            var d = Json.Obj(Checks(), CheckKey(m.Nexus));
            if (d == null) return null;
            var at = Json.Long(d, "checked_at");
            if (Now() - at > Month) return null;
            if (recent.TryGetValue(m.Nexus.Mod, out var changed) && changed >= at - 3600) return null;
            var (channel, page) = Origins.Of(m);
            return new UpdateInfo
            {
                ModId = m.Id, Name = m.Name, Channel = channel, Page = Json.Str(d, "page") ?? page, Status = Json.Str(d, "status"),
                Current = Json.Str(d, "current"), Latest = Json.Str(d, "latest"), Download = Json.Str(d, "download"),
                LatestFile = Json.Long(d, "latest_file"), LatestTime = Json.Long(d, "latest_time"),
            };
        }
#endif

        static Dictionary<string, object> NewestMain(List<Dictionary<string, object>> files) =>
            files.Where(f => (Json.Str(f, "category_name") ?? "") == "MAIN").OrderByDescending(f => Json.Long(f, "uploaded_timestamp")).FirstOrDefault();

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
            mod.GitHub = new GitHubRef { Owner = mod.GitHub?.Owner, Repo = mod.GitHub?.Repo, Tag = u.Latest };
            if (!string.IsNullOrEmpty(manifest?.Name)) mod.Name = manifest.Name;
            lib.Save();
            return mod;
        }
    }
}
