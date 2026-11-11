using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Mod
{
    /// <summary>A mod from GitHub checked against the repository's releases (the user, 2026-11-12:
    /// MDL3-preview). Neither the tag nor GitHub's "latest" release is trusted: there, tags are
    /// names (Normal, BugFix, AH_SB), two releases share one commit so "latest" is a tie, and
    /// one release carries two mods (BFV Hit and Kill Feedback beside MDL3, listed first), so the
    /// first zip of the latest release was another mod. Instead:
    ///
    ///   1. which archive is this mod: the Guid in the manifest.json inside it (read with HTTP
    ///      ranges, about 64 KB, kept per file), the same as the installed folder's; without one,
    ///      the same file name once its digits are left out; without that, the only archive of
    ///      its release. File names change (the user, 2026-11-12), the Guid does not (MDL3 dev.43,
    ///      47, 52, 57: all b0ae9d35-...).
    ///   2. which is newest: the time GitHub received the file, which no tag or edit changes.
    ///   3. what version: the tag when it holds a number, else the version in the file's name.
    ///
    /// The last 30 releases are read with one request; drafts never count, pre-releases only
    /// when the installed file came from one.</summary>
    public static class GitHubUpdates
    {
        /// <summary>A release's archive.</summary>
        public class Asset
        {
            public string Name, Url, Tag, Page;
            public long Size, Uploaded;
            public bool Prerelease;
            /// <summary>How many archives its release holds.</summary>
            public int InRelease;
        }

        static readonly string[] Archives = { ".zip", ".7z", ".rar" };
        static bool IsArchive(string name) => Archives.Any(e => (name ?? "").EndsWith(e, StringComparison.OrdinalIgnoreCase));

        /// <summary>The repository's archives in its last 30 releases (no drafts), newest first.</summary>
        public static List<Asset> List(GitHubRef r)
        {
            var list = Json.Parse(Updates.Get(Updates.GitHubApi.TrimEnd('/') + "/repos/" + r.Owner + "/" + r.Repo + "/releases?per_page=30")) as List<object>;
            var out_ = new List<Asset>();
            foreach (var rel in (list ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                if (Json.Bool(rel, "draft")) continue;
                var archives = Json.List(rel, "assets").OfType<Dictionary<string, object>>().Where(a => IsArchive(Json.Str(a, "name"))).ToList();
                foreach (var a in archives)
                    out_.Add(new Asset
                    {
                        Name = Json.Str(a, "name"), Url = Json.Str(a, "browser_download_url"), Size = Json.Long(a, "size"),
                        Uploaded = Time(Json.Str(a, "created_at")), Tag = Json.Str(rel, "tag_name"), Page = Json.Str(rel, "html_url"),
                        Prerelease = Json.Bool(rel, "prerelease"), InRelease = archives.Count,
                    });
            }
            return out_.OrderByDescending(a => a.Uploaded).ToList();
        }

        /// <summary>The mod checked: u's Status, Current, Latest, Download, LatestTime, Page,
        /// LatestTag and LatestAsset.</summary>
        public static void Check(Mod m, UpdateInfo u)
        {
            var r = m.GitHub;
            var all = List(r);
            string installedName = r.Asset ?? SourceName(m.Source);
            string guid = null;
            try { guid = m.Path != null && Directory.Exists(m.Path) ? Manifest.Read(m.Path)?.Guid : null; }
            catch (ModError) { }

            // the file installed: by its name (and time, when kept); else the release it came from
            var installed = all.Where(a => installedName != null && string.Equals(a.Name, installedName, StringComparison.OrdinalIgnoreCase))
                               .OrderByDescending(a => a.Uploaded == r.Uploaded).ThenByDescending(a => a.Uploaded).FirstOrDefault();
            if (installed == null && !string.IsNullOrEmpty(r.Tag))
                installed = all.FirstOrDefault(a => string.Equals(a.Tag, r.Tag, StringComparison.OrdinalIgnoreCase) && Mine(a, installedName, guid));
            long since = installed?.Uploaded ?? r.Uploaded;
            bool pre = installed?.Prerelease ?? false;

            var newer = all.FirstOrDefault(a => a.Uploaded > since && (pre || !a.Prerelease) && Mine(a, installedName, guid));
            u.Current = installed != null ? Label(installed.Tag, installed.Name) : (Label(r.Tag, installedName) ?? r.Tag);
            if (newer != null)
            {
                u.Latest = Label(newer.Tag, newer.Name);
                u.Download = newer.Url;
                u.LatestTime = newer.Uploaded;
                u.LatestTag = newer.Tag;
                u.LatestAsset = newer.Name;
                u.Page = newer.Page ?? u.Page;
                // nothing says what is installed: the newest is offered, but not called an update
                u.Status = installed == null && since == 0 && installedName == null && string.IsNullOrEmpty(r.Tag) ? "unknown" : "update";
                return;
            }
            if (installed == null && since == 0)
            {
                // nothing newer than an unknown file: the newest of this mod, if it is the one installed
                var newest = all.FirstOrDefault(a => !a.Prerelease && Mine(a, installedName, guid));
                u.Latest = newest != null ? Label(newest.Tag, newest.Name) : null;
                u.Status = newest == null ? "unknown" : "current";
                if (newest != null) u.Page = newest.Page ?? u.Page;
                return;
            }
            u.Latest = u.Current;
            u.Page = installed?.Page ?? u.Page;
            u.Status = "current";
        }

        /// <summary>Whether an archive is this mod: the same manifest Guid; else the same name with
        /// its digits left out; else the only archive of its release.</summary>
        static bool Mine(Asset a, string installedName, string guid)
        {
            if (guid != null)
            {
                var theirs = GuidOf(a);
                if (theirs != null) return string.Equals(theirs, guid, StringComparison.OrdinalIgnoreCase);
            }
            if (installedName != null && Skeleton(installedName) == Skeleton(a.Name)) return true;
            return a.InRelease == 1;
        }

        /// <summary>A name with its runs of digits left out: what stays the same from version to version.</summary>
        public static string Skeleton(string name) => Regex.Replace((name ?? "").ToLowerInvariant(), @"\d+", "#");

        /// <summary>The version shown: the tag when it holds a number; else the version in the file's
        /// name (MDL3-3.0.0-dev.57-preview-... gives 3.0.0-dev.57); else null.</summary>
        public static string Label(string tag, string file)
        {
            if (!string.IsNullOrWhiteSpace(tag) && tag.Any(char.IsDigit)) return tag.Trim();
            if (string.IsNullOrEmpty(file)) return null;
            var stem = Path.GetFileNameWithoutExtension(file);
            var v = Regex.Match(stem, @"(?<![\dA-Za-z])[vV]?\d+(?:\.\d+)+(?:-[A-Za-z]+\.?\d+)?");
            return v.Success ? v.Value : null;
        }

        /// <summary>The archive a mod was added from, by its file name, when its source is one.</summary>
        static string SourceName(string source)
        {
            if (string.IsNullOrEmpty(source) || source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
            try { var n = Path.GetFileName(source); return IsArchive(n) ? n : null; }
            catch (ArgumentException) { return null; }
        }

        static long Time(string iso) =>
            DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
                ? t.ToUnixTimeSeconds() : 0;

        // ------------------------------------------------------------ the manifest inside a zip on GitHub

        const string CacheFile = "github_assets.json";
        static Dictionary<string, object> cache;
        static readonly object gate = new object();

        /// <summary>The Guid in the manifest.json of a zip on GitHub, or null: none there, not a zip,
        /// or not readable in ranges. Kept by the file's address, size and time, which name one file.</summary>
        static string GuidOf(Asset a)
        {
            if (!a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || a.Size <= 22 || a.Url == null) return null;
            var key = a.Url + "|" + a.Size + "|" + a.Uploaded;
            lock (gate)
            {
                cache = cache ?? Store.Load(CacheFile);
                if (cache.TryGetValue(key, out var known)) return known as string == "" ? null : known as string;
            }
            string guid;
            try { guid = ReadGuid(a.Url, a.Size); }
            catch (ModError) { return null; }                     // offline, refused: asked again next time
            lock (gate)
            {
                cache[key] = guid ?? "";
                try { Store.Save(CacheFile, cache); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            return guid;
        }

        /// <summary>The zip's central directory from its last bytes, then manifest.json alone (at the
        /// top, or in the one folder the zip holds). Null when the zip has none, or the server sends
        /// no ranges.</summary>
        internal static string ReadGuid(string url, long size)
        {
            long from = Math.Max(0, size - 65536);
            var tail = Updates.GetRange(url, from, size - 1);
            if (tail == null) return null;
            int eocd = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
                if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 5 && tail[i + 3] == 6) { eocd = i; break; }
            if (eocd < 0) return null;
            long cdSize = BitConverter.ToUInt32(tail, eocd + 12), cdOff = BitConverter.ToUInt32(tail, eocd + 16);
            byte[] cd;
            if (cdOff >= from && cdOff + cdSize <= size) cd = tail.Skip((int)(cdOff - from)).Take((int)cdSize).ToArray();
            else if (cdSize < 4 << 20) cd = Updates.GetRange(url, cdOff, cdOff + cdSize - 1);
            else return null;
            if (cd == null) return null;

            var found = new List<(string Name, int Method, long Size, long Offset)>();
            for (int i = 0; i + 46 <= cd.Length && BitConverter.ToUInt32(cd, i) == 0x02014b50;)
            {
                int method = BitConverter.ToUInt16(cd, i + 10);
                long csize = BitConverter.ToUInt32(cd, i + 20);
                int nlen = BitConverter.ToUInt16(cd, i + 28), xlen = BitConverter.ToUInt16(cd, i + 30), clen = BitConverter.ToUInt16(cd, i + 32);
                long off = BitConverter.ToUInt32(cd, i + 42);
                var name = Encoding.UTF8.GetString(cd, i + 46, nlen).Replace('\\', '/');
                if (name.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) && name.Count(c => c == '/') <= 1
                    && (name.Length == 13 || name[name.Length - 14] == '/'))
                    found.Add((name, method, csize, off));
                i += 46 + nlen + xlen + clen;
            }
            var m = found.OrderBy(f => f.Name.Count(c => c == '/')).FirstOrDefault();
            if (m.Name == null || m.Size > 1 << 20 || (m.Method != 0 && m.Method != 8)) return null;
            var head = Updates.GetRange(url, m.Offset, m.Offset + 29);
            if (head == null || head.Length < 30) return null;
            long data = m.Offset + 30 + BitConverter.ToUInt16(head, 26) + BitConverter.ToUInt16(head, 28);
            var raw = m.Size == 0 ? new byte[0] : Updates.GetRange(url, data, data + m.Size - 1);
            if (raw == null) return null;
            string text;
            try
            {
                if (m.Method == 0) text = Encoding.UTF8.GetString(raw);
                else using (var z = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress))
                     using (var rd = new StreamReader(z, Encoding.UTF8)) text = rd.ReadToEnd();
            }
            catch (InvalidDataException) { return null; }
            try { return Json.Str(Json.Parse(text.TrimStart('﻿')) as Dictionary<string, object>, "Guid"); }
            catch (FormatException) { return null; }
        }
    }
}
