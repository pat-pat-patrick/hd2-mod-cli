using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Hd2Mod
{
    /// <summary>A mod added from an archive with no Nexus Mods record (downloaded by hand, renamed,
    /// passed on) is known by the archive's MD5: Nexus Mods' public API (v2) says which mod and
    /// which file of it has those exact bytes, with no key or sign-in (the user, 2026-11-04). The
    /// MD5 is taken when the archive is added (Mod.ArchiveMd5); a mod added before that has it
    /// taken from its archive at the next update check, when the archive is still where it was
    /// added from. Asked only in an update check the player started.</summary>
    public static class Fingerprints
    {
        /// <summary>A file's MD5, in lower-case hex.</summary>
        public static string Md5(string file)
        {
            using (var md5 = MD5.Create())
            using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                return string.Concat(md5.ComputeHash(s).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        /// <summary>The MD5 of the archive a mod is added from, else null (a folder, or a file that
        /// cannot be read).</summary>
        public static string OfArchive(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try { return Md5(path); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        /// <summary>A mod the archive's fingerprint can tell more about: no source at all, or a
        /// Nexus Mods mod whose file is not known (its version then only guessed from a name).</summary>
        static bool Wants(Mod m) => (m.Nexus == null && m.GitHub == null && m.Page == null) || (m.Nexus != null && m.Nexus.File <= 0);

        /// <summary>The mods to ask about, by id, with their archive's MD5 and file name. The MD5 of
        /// a mod added before MD5s were kept is taken now from the archive it came from, when that
        /// is still there, and kept in the library (the library is changed, not saved).</summary>
        public static Dictionary<string, (string Md5, string Name)> Wanted(Library lib, string onlyId = null)
        {
            var out_ = new Dictionary<string, (string, string)>();
            foreach (var m in lib.Mods.Where(x => Wants(x) && (onlyId == null || x.Id == onlyId)))
            {
                if (m.ArchiveMd5 == null) m.ArchiveMd5 = OfArchive(m.Source);
                if (m.ArchiveMd5 != null) out_[m.Id] = (m.ArchiveMd5, Path.GetFileName(m.Source ?? ""));
            }
            return out_;
        }

        const string HashesQuery = "query Hashes($md5s: [String!]!) { fileHashes(md5s: $md5s) { md5 fileName gameId modFile { fileId modId version date } } }";

        /// <summary>What Nexus Mods has for each MD5 (fifty a request), in this game only: the mod and
        /// the file. Several files can share the bytes: the one of the same name, else the newest of
        /// one mod; bytes in more than one mod (a copy uploaded again) tell nothing and are left out.</summary>
        public static Dictionary<string, NexusRef> Look(IEnumerable<(string Md5, string Name)> archives)
        {
            var all = archives.GroupBy(x => x.Md5).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            var out_ = new Dictionary<string, NexusRef>(StringComparer.OrdinalIgnoreCase);
            if (all.Count == 0) return out_;
            var game = Updates.GameId(Nexus.Game);
            var md5s = all.Keys.ToList();
            for (int at = 0; at < md5s.Count; at += 50)
            {
                var d = Nexus.PostGraphQL(HashesQuery, new Dictionary<string, object> { ["md5s"] = md5s.Skip(at).Take(50).Cast<object>().ToList() });
                var found = Json.List(d, "fileHashes").OfType<Dictionary<string, object>>()
                    .Where(h => Json.Long(h, "gameId") == game && Json.Obj(h, "modFile") != null);
                foreach (var g in found.GroupBy(h => (Json.Str(h, "md5") ?? "").ToLowerInvariant()))
                {
                    if (!all.TryGetValue(g.Key, out var name)) continue;
                    var named = g.Where(h => string.Equals(Json.Str(h, "fileName"), name, StringComparison.OrdinalIgnoreCase)).ToList();
                    var pick = named.Count == 1 ? named[0]
                             : g.Select(h => Json.Long(Json.Obj(h, "modFile"), "modId")).Distinct().Count() == 1
                               ? g.OrderByDescending(h => Json.Long(Json.Obj(h, "modFile"), "date")).First() : null;
                    if (pick == null) continue;
                    var f = Json.Obj(pick, "modFile");
                    out_[g.Key] = new NexusRef { Game = Nexus.Game, Mod = Json.Long(f, "modId"), File = Json.Long(f, "fileId"), Version = Json.Str(f, "version"), Time = Json.Long(f, "date") };
                }
            }
            return out_;
        }

        /// <summary>The answers put in the library: the MD5s taken now are kept, and each mod Nexus
        /// Mods knows gets its mod and file. A mod already recorded as another Nexus mod keeps that
        /// record (its download said so). How many mods learnt their source.</summary>
        public static int Apply(Library lib, Dictionary<string, (string Md5, string Name)> wanted, Dictionary<string, NexusRef> found)
        {
            int n = 0;
            foreach (var kv in wanted)
            {
                var m = lib.Mods.FirstOrDefault(x => x.Id == kv.Key);
                if (m == null || !Wants(m)) continue;
                if (m.ArchiveMd5 == null && string.Equals(Path.GetFileName(m.Source ?? ""), kv.Value.Name, StringComparison.OrdinalIgnoreCase)) m.ArchiveMd5 = kv.Value.Md5;
                if (!string.Equals(m.ArchiveMd5, kv.Value.Md5, StringComparison.OrdinalIgnoreCase)) continue;      // replaced meanwhile
                if (!found.TryGetValue(kv.Value.Md5, out var r)) continue;
                if (m.Nexus != null && m.Nexus.Mod != r.Mod) continue;
                m.Nexus = new NexusRef { Game = r.Game, Mod = r.Mod, File = r.File, Version = r.Version, Time = r.Time };
                n++;
            }
            return n;
        }

        /// <summary>Wanted, Look and Apply on one library, for the command line and the tests; the
        /// window runs Look outside its queue. A failed request learns nothing (the update check says
        /// why for itself).</summary>
        public static int Learn(Library lib)
        {
            var wanted = Wanted(lib);
            Dictionary<string, NexusRef> found;
            try { found = Look(wanted.Values); }
            catch (ModError) { found = new Dictionary<string, NexusRef>(); }
            return Apply(lib, wanted, found);
        }
    }
}
