using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace Hd2Mod
{
    /// <summary>What a mod's own site says about it: the name, who made it, its own words, its
    /// picture and the version on the site. A mod folder says little — often only a folder name
    /// a download gave it — so the site is asked and what it answers is filled in, the way a
    /// reference manager fills a paper in from its DOI (the user, 2026-10-24).</summary>
    public class SiteInfo
    {
        public string Name, Author, Summary, Picture, Version;
        /// <summary>When the site last had a new file, Unix seconds; 0 when it does not say.</summary>
        public long Updated;
    }

    public static class Site
    {
        static readonly HttpClient http = MakeClient();

        static HttpClient MakeClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("HD2ModQuickDeploy/" + StateDoc.Version);
            c.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return c;
        }

        /// <summary>What the mod's source says about it, or null when it has no source that can be
        /// asked (another site's page cannot).</summary>
        public static SiteInfo Describe(Mod mod)
        {
            if (mod.Nexus != null) return FromNexus(mod.Nexus);
            if (mod.GitHub != null) return FromGitHub(mod.GitHub);
            return null;
        }

        /// <summary>The mod's page as anyone sees it, with no sign-in, in every build (Nexus.LookUp;
        /// the user, 2026-11-04).</summary>
        static SiteInfo FromNexus(NexusRef r)
        {
            var m = Nexus.LookUp(r.Mod);
            return new SiteInfo
            {
                Name = m.Name, Author = m.Author ?? m.Uploader, Summary = Clean(m.Summary),
                Picture = m.Picture, Version = m.Version, Updated = m.Updated,
            };
        }

        static SiteInfo FromGitHub(GitHubRef r)
        {
            var d = Json.Parse(Get(Updates.GitHubApi.TrimEnd('/') + "/repos/" + r.Owner + "/" + r.Repo)) as Dictionary<string, object>;
            if (d == null) throw new ModError("GitHub answered nothing about " + r.Owner + "/" + r.Repo, "error");
            var info = new SiteInfo
            {
                Name = Json.Str(d, "name"),
                Author = Json.Str(Json.Obj(d, "owner"), "login") ?? r.Owner,
                Summary = Clean(Json.Str(d, "description")),
                Version = r.Tag,
            };
            // the newest release names the version, and often the mod itself
            try
            {
                var rel = Json.Parse(Get(Updates.GitHubApi.TrimEnd('/') + "/repos/" + r.Owner + "/" + r.Repo + "/releases/latest")) as Dictionary<string, object>;
                if (rel != null)
                {
                    info.Version = Json.Str(rel, "tag_name") ?? info.Version;
                    var when = Json.Str(rel, "published_at");
                    if (DateTime.TryParse(when, System.Globalization.CultureInfo.InvariantCulture,
                                          System.Globalization.DateTimeStyles.AdjustToUniversal, out var t))
                        info.Updated = (long)(t - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                }
            }
            catch (ModError) { }     // a repository without releases still has its name and words
            return info;
        }

        static string Get(string url)
        {
            HttpResponseMessage r;
            try { r = http.GetAsync(url).GetAwaiter().GetResult(); }
            catch (HttpRequestException e) { throw new ModError("cannot be reached: " + e.Message, "offline"); }
            using (r)
            {
                var body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if ((int)r.StatusCode == 404) throw new ModError("no such repository", "missing");
                if ((int)r.StatusCode == 403 || (int)r.StatusCode == 429) throw new ModError("too many requests for now", "busy");
                if (!r.IsSuccessStatusCode) throw new ModError("answered " + (int)r.StatusCode, "error");
                return body;
            }
        }

        static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Replace("<br />", "\n").Replace("<br>", "\n").Trim();

        /// <summary>The picture of a mod, kept beside the library as pictures\&lt;mod id&gt;.&lt;ext&gt;,
        /// so the list can show it when the mod folder has none. The path, or null.</summary>
        public static string KeepPicture(string modId, string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var ext = System.IO.Path.GetExtension(new Uri(url).AbsolutePath);
            if (ext.Length == 0 || ext.Length > 5) ext = ".jpg";
            var into = Paths.Join(Store.Home, "pictures");
            Directory.CreateDirectory(into);
            var path = Paths.Join(into, Safe(modId) + ext);
            try
            {
                var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                if (bytes.Length == 0) return null;
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception e) when (e is HttpRequestException || e is IOException || e is UnauthorizedAccessException || e is UriFormatException) { return null; }
            return path;
        }

        /// <summary>The picture kept for a mod, when there is one.</summary>
        public static string PictureOf(string modId)
        {
            var into = Paths.Join(Store.Home, "pictures");
            if (!Directory.Exists(into)) return null;
            return Directory.EnumerateFiles(into, Safe(modId) + ".*").FirstOrDefault();
        }

        static string Safe(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"[^\w-]+", "_");

        /// <summary>The mod told what its site says: the name it is called there (the name it came
        /// with is kept and the details show it), the version, and the site's own words and
        /// picture. What changed, in the words the command line prints.</summary>
        public static List<string> Fill(Library lib, Mod mod, bool pictures = true)
        {
            var info = Describe(mod);
            var said = new List<string>();
            if (info == null) throw new ModError("this mod has no source to ask: set a Nexus Mods page or a GitHub repository", "no_source");
            // the page's title, unless the player gave the mod a name of their own: that one stays
            // (the user, 2026-10-25)
            if (mod.RealName == null && !string.IsNullOrWhiteSpace(info.Name) && info.Name.Trim() != mod.Name)
            {
                var name = info.Name.Trim();
                if (lib.Mods.Any(x => x != mod && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    name += mod.Nexus != null ? " (Nexus " + mod.Nexus.Mod + ")" : " (" + mod.GitHub?.Repo + ")";
                if (mod.RealName == null) mod.RealName = mod.Name;
                said.Add("name: " + mod.Name + " -> " + name);
                mod.Name = name;
                if (string.Equals(mod.RealName, mod.Name, StringComparison.Ordinal)) mod.RealName = null;
            }
            // The installed version is not the page's to say. The page's newest version was written
            // in as the mod's own when the file was not known, so a mod one or two versions behind
            // read as up to date (the user, 2026-10-25). It is kept as the page's (Site.Version)
            // and shown as that.
            if (!string.IsNullOrWhiteSpace(info.Version)) said.Add("newest on the page: " + info.Version);
            if (pictures && info.Picture != null) KeepPicture(mod.Id, info.Picture);
            if (info.Author != null || info.Summary != null)
            {
                mod.Site = info;
                said.Add("from " + (mod.Nexus != null ? "Nexus Mods" : "GitHub") + ": " + (info.Author ?? "?"));
            }
            return said;
        }
    }
}
