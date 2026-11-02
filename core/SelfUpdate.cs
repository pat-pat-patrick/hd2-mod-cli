using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>What the last check of this program's own releases found.</summary>
    public class SelfUpdateInfo
    {
        /// <summary>The newest release's tag and page, or null when none counts for this build.</summary>
        public string Latest, Page;
        public bool Prerelease;
        /// <summary>Whether it is newer than the version running.</summary>
        public bool Newer;
        /// <summary>When it was checked (Unix seconds); 0: never.</summary>
        public long CheckedAt;
        /// <summary>What each release newer than the one running says, newest first: its tag,
        /// page and text (GitHub's release body, in both languages: SelfUpdate.NotesIn).</summary>
        public List<(string Tag, string Page, string Body)> Notes = new List<(string, string, string)>();
    }

    /// <summary>This program's own updates, from its GitHub releases (the user, 2026-10-25). The
    /// development build is published there as pre-releases, the official build as releases: the
    /// official build counts only releases, so it is never offered a build without Nexus Mods;
    /// the development build counts both. Read only: nothing is downloaded, the release page is
    /// opened for the player. The last result is kept, so the window can show it before (or
    /// without) going online; the automatic check runs at most once a day.</summary>
    public static class SelfUpdate
    {
        public const string Repo = "pat-pat-patrick/hd2-mod-cli";
        const string File = "self_update.json";
        const long Day = 24 * 3600;

#if HAS_NEXUS
        public const bool Official = true;
#else
        public const bool Official = false;
#endif

        /// <summary>A tag's version as numbers: "v0.9.0", "0.9", "v1.2.3-dev" -> [0,9,0]...; null when it has none.</summary>
        public static int[] Numbers(string tag)
        {
            var m = Regex.Match(tag ?? "", @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) return null;
            return Enumerable.Range(1, 3).Select(i => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0).ToArray();
        }

        public static int Compare(int[] a, int[] b)
        {
            for (int i = 0; i < 3; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return 0;
        }

        /// <summary>The newest release that counts for this build, out of GitHub's list of them.</summary>
        public static SelfUpdateInfo Pick(List<Dictionary<string, object>> releases, string running, bool official)
        {
            var now = Numbers(running);
            var counted = releases
                .Where(r => !Json.Bool(r, "draft", false) && (!official || !Json.Bool(r, "prerelease", false)))
                .Select(r => (R: r, N: Numbers(Json.Str(r, "tag_name"))))
                .Where(x => x.N != null)
                .OrderByDescending(x => x.N[0]).ThenByDescending(x => x.N[1]).ThenByDescending(x => x.N[2])
                .ToList();
            if (counted.Count == 0) return new SelfUpdateInfo();
            var best = counted[0];
            return new SelfUpdateInfo
            {
                Latest = Json.Str(best.R, "tag_name"), Page = Json.Str(best.R, "html_url"),
                Prerelease = Json.Bool(best.R, "prerelease", false),
                Newer = now != null && Compare(best.N, now) > 0,
                // every version the player would get by updating, so the window can say all of it
                Notes = now == null ? new List<(string, string, string)>() : counted.Where(x => Compare(x.N, now) > 0)
                    .Select(x => (Json.Str(x.R, "tag_name"), Json.Str(x.R, "html_url"), Json.Str(x.R, "body") ?? "")).ToList(),
            };
        }

        /// <summary>A release text in one language. A text in both carries a line &lt;!-- zh --&gt;
        /// before its Chinese and &lt;!-- en --&gt; before its English; what comes before the first
        /// of them is for both. A text without them is the same in both languages (Same: true).</summary>
        public static (string Text, bool Same) NotesIn(string body, bool zh)
        {
            body = (body ?? "").Replace("\r\n", "\n");
            var parts = Regex.Split(body, @"^[ \t]*<!--\s*(zh|en)\s*-->[ \t]*$", RegexOptions.Multiline);
            if (parts.Length == 1) return (body.Trim(), true);
            var sb = new StringBuilder(parts[0]);
            bool any = false;
            // each part starts with the marker line's own end
            string Part(int i) => parts[i + 1].StartsWith("\n") ? parts[i + 1].Substring(1) : parts[i + 1];
            for (int i = 1; i + 1 < parts.Length; i += 2)
                if ((parts[i] == "zh") == zh) { sb.Append(Part(i)); any = true; }
            if (!any)
                for (int i = 1; i + 1 < parts.Length; i += 2) sb.Append(Part(i));
            return (sb.ToString().Trim(), !any);
        }

        /// <summary>Whether the window should show the notes of the newest version by itself: it
        /// is newer than the one running, and its notes have not been shown yet (once a version,
        /// the user, 2026-11-02).</summary>
        public static bool NotesDue(SelfUpdateInfo u) => u.Newer && u.Latest != null && Json.Str(Store.Load(File), "notes_shown") != u.Latest;

        public static void NotesShown(string tag)
        {
            var d = Store.Load(File);
            d["notes_shown"] = tag;
            Store.Save(File, d);
        }

        /// <summary>GitHub asked now; the result kept.</summary>
        public static SelfUpdateInfo Check()
        {
            var list = Json.Parse(Updates.Get(Updates.GitHubApi.TrimEnd('/') + "/repos/" + Repo + "/releases?per_page=30")) as List<object>;
            var found = Pick((list ?? new List<object>()).OfType<Dictionary<string, object>>().ToList(), StateDoc.Version, Official);
            found.CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var d = Store.Load(File);
            d["checked_at"] = found.CheckedAt;
            d["latest"] = found.Latest;
            d["page"] = found.Page;
            d["prerelease"] = found.Prerelease;
            d["notes"] = found.Notes.Select(n => (object)new Dictionary<string, object> { ["tag"] = n.Tag, ["page"] = n.Page, ["body"] = n.Body }).ToList();
            Store.Save(File, d);
            return found;
        }

        /// <summary>The last result, compared again with the version running now: once the new
        /// version is the one running, it is not newer any more. A pre-release a development
        /// build found in the same folder is nothing to the official build.</summary>
        public static SelfUpdateInfo Last()
        {
            var d = Store.Load(File);
            var latest = Json.Str(d, "latest");
            var pre = Json.Bool(d, "prerelease", false);
            if (Official && pre) latest = null;
            var n = Numbers(latest);
            var now = Numbers(StateDoc.Version);
            return new SelfUpdateInfo
            {
                Latest = latest, Page = latest == null ? null : Json.Str(d, "page"), Prerelease = latest != null && pre,
                CheckedAt = Official && pre ? 0 : Json.Long(d, "checked_at"),
                Newer = n != null && now != null && Compare(n, now) > 0,
                // the ones still newer than the version running now
                Notes = latest == null || now == null ? new List<(string, string, string)>()
                    : (Json.List(d, "notes") ?? new List<object>()).OfType<Dictionary<string, object>>()
                        .Where(x => Numbers(Json.Str(x, "tag")) is int[] t && Compare(t, now) > 0)
                        .Select(x => (Json.Str(x, "tag"), Json.Str(x, "page"), Json.Str(x, "body") ?? "")).ToList(),
            };
        }

        /// <summary>Whether to check by itself (on unless turned off).</summary>
        public static bool Auto
        {
            get => Json.Bool(Store.Load(File), "auto", true);
            set { var d = Store.Load(File); d["auto"] = value; Store.Save(File, d); }
        }

        /// <summary>Whether the automatic check is due: on, and not checked in the last day.</summary>
        public static bool Due() => Auto && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Last().CheckedAt >= Day;
    }
}
