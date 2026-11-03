#if HAS_NEXUS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>A mod on Nexus Mods as its page shows it, looked up by its number.</summary>
    public class NexusModInfo
    {
        public long Mod;
        public string Name, Author, Uploader, Summary, Picture, Version;
        /// <summary>"published" when anyone can download it; else why not, as Nexus Mods says it.</summary>
        public string Status;
        /// <summary>When the page last changed, Unix seconds; 0 when it does not say.</summary>
        public long Updated;
        public long Downloads, Endorsements;
        public bool Adult;
        public string Page => Nexus.Page(new NexusRef { Game = Nexus.Game, Mod = Mod });
        /// <summary>The page's Files tab, where Mod Manager Download is.</summary>
        public string FilesPage => Page + "?tab=files";
        public bool Available => Status == null || string.Equals(Status, "published", StringComparison.OrdinalIgnoreCase);

        /// <summary>Why it cannot be downloaded, in words. What Nexus Mods answers for Helldivers 2's
        /// mods (2026-10-29): published, not_published, hidden, removed, wastebinned, under_moderation.</summary>
        public string StatusWords
        {
            get
            {
                switch (Status)
                {
                    case "not_published": return "not published yet";
                    case "hidden": return "hidden by its author";
                    case "removed": return "removed";
                    case "wastebinned": return "deleted";
                    case "under_moderation": return "under moderation";
                    default: return Status;
                }
            }
        }
    }

    // Mods are passed around by their number alone in Chinese-speaking groups ("N站16662"; the user,
    // 2026-10-29): the number, or a page, looked up on Nexus Mods without signing in.
    public static partial class Nexus
    {
        static readonly Regex PageLink = new Regex(@"nexusmods\.com/([\w-]+)/mods/(\d+)", RegexOptions.IgnoreCase);
        // a number on its own, with a few words or signs around it but no other digits
        static readonly Regex NumberAlone = new Regex(@"^\D{0,12}?(\d{1,8})\D{0,6}$");

        /// <summary>The mod number in what was typed or pasted: a number ("16662", "N站16662",
        /// "#16662") or a page of this game's mods; null when there is none. Another game's page
        /// is refused.</summary>
        public static long? ModNumber(string text)
        {
            text = (text ?? "").Trim();
            var p = PageLink.Match(text);
            if (p.Success)
            {
                if (!string.Equals(p.Groups[1].Value, Game, StringComparison.OrdinalIgnoreCase))
                    throw new ModError("this page is for another game: " + p.Groups[1].Value, "other_game", p.Groups[1].Value);
                return long.Parse(p.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            var n = NumberAlone.Match(text);
            return n.Success && long.TryParse(n.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : (long?)null;
        }

        const string ModQuery =
            "query Mod($ids: [CompositeDomainWithIdInput!]!) { legacyModsByDomain(ids: $ids) { nodes { " +
            "modId name summary version author pictureUrl updatedAt endorsements downloads adultContent status uploader { name } } } }";

        /// <summary>What the mod's page says. Asked without signing in; when that way fails for
        /// another reason than the mod not being there, and the player is signed in, the v1 API
        /// is asked instead.</summary>
        public static NexusModInfo LookUp(long mod)
        {
            try { return FromGraphQL(mod); }
            catch (ModError e) when (e.Kind == "nexus_error" && Credential() is string key)
            {
                return FromV1(mod, key);
            }
        }

        static NexusModInfo FromGraphQL(long mod)
        {
            var data = PostGraphQL(ModQuery, new Dictionary<string, object>
            {
                ["ids"] = new List<object> { new Dictionary<string, object> { ["gameDomain"] = Game, ["modId"] = mod } },
            });
            var node = Json.List(Json.Obj(data, "legacyModsByDomain"), "nodes").OfType<Dictionary<string, object>>().FirstOrDefault();
            if (node == null) throw new ModError("Nexus Mods has no Helldivers 2 mod numbered " + mod, "nexus_missing", mod.ToString(CultureInfo.InvariantCulture));
            return new NexusModInfo
            {
                Mod = Json.Long(node, "modId") is long id && id > 0 ? id : mod,
                Name = Json.Str(node, "name"), Author = Json.Str(node, "author"), Uploader = Json.Str(Json.Obj(node, "uploader"), "name"),
                Summary = Words(Json.Str(node, "summary")), Picture = Json.Str(node, "pictureUrl"), Version = Json.Str(node, "version"),
                Status = Json.Str(node, "status"), Updated = Unix(Json.Str(node, "updatedAt")),
                Downloads = Json.Long(node, "downloads"), Endorsements = Json.Long(node, "endorsements"), Adult = Json.Bool(node, "adultContent"),
            };
        }

        static NexusModInfo FromV1(long mod, string key)
        {
            var d = Json.Parse(Get("/v1/games/" + Game + "/mods/" + mod + ".json", key)) as Dictionary<string, object>;
            if (d == null) throw new ModError("Nexus Mods answered nothing about mod " + mod, "nexus_error");
            return new NexusModInfo
            {
                Mod = mod, Name = Json.Str(d, "name"), Author = Json.Str(d, "author"), Uploader = Json.Str(d, "uploaded_by"),
                Summary = Words(Json.Str(d, "summary")), Picture = Json.Str(d, "picture_url"), Version = Json.Str(d, "version"),
                Status = Json.Str(d, "status"), Updated = Json.Long(d, "updated_timestamp"),
                Downloads = Json.Long(d, "mod_downloads"), Endorsements = Json.Long(d, "endorsement_count"), Adult = Json.Bool(d, "contains_adult_content"),
            };
        }

        static string Words(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Replace("<br />", "\n").Replace("<br>", "\n").Trim();

        static long Unix(string iso) =>
            DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeSeconds() : 0;
    }
}
#endif
