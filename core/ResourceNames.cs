using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>What a resource is, for the conflicts (the user, 2026-10-25: say which resource
    /// two mods fight over, not only how many). Its type comes from the engine's own type names,
    /// kept here; its name from HD2SDK's friendlynames.txt when the player has HD2SDK installed.
    /// That list has no licence, so it is read where it is and never shipped with this program.</summary>
    public static class ResourceNames
    {
        /// <summary>The engine's resource types, by the hash the archives carry.</summary>
        static readonly Dictionary<ulong, string> types = new Dictionary<ulong, string>
        {
            [0x18dead01056b72e9] = "bones", [0x2a690fd348fe9ac5] = "level", [0x5f7203c8f280dab8] = "physics",
            [0x931e336d7646cc26] = "animation", [0xa486d4045106165c] = "state_machine", [0xa8193123526fad64] = "particles",
            [0xab2f78e885f513c6] = "prefab", [0xcd4238c6a0c69e32] = "texture", [0xe0a48d0be9a7453f] = "unit",
            [0xeac0b497876adedf] = "material", [0x535a7bd3e650d799] = "wwise_bank", [0xaf32095c82f2b070] = "wwise_dep",
            [0x504b55235d21440e] = "wwise_stream", [0x250e0a11ac8e26f8] = "shading_environment_mapping",
            [0x9831ca893b0d087d] = "entity", [0xaa5965f03029fa18] = "bik", [0x9efe0a916aae7880] = "font",
            [0xd7014a50477953e0] = "cloth", [0xfe73c7dcff8a7ca5] = "shading_environment", [0xa14e8dfa2cd117e2] = "lua",
            [0xf7505933166d6755] = "vector_field", [0x92d3ee038eeb610d] = "flow", [0x27862fe24795319c] = "render_config",
            [0x0d972bab10b40fd3] = "strings", [0x3b1fa9e8f6bac374] = "network_config", [0x82645835e6b73232] = "config",
            [0xad9c6d9ed1e5e77a] = "package", [0xd50a8b7e1c82b110] = "wwise_metadata", [0xb277b11fe4a61d37] = "mouse_cursor",
            [0x7910103158fc1de9] = "renderable", [0x9e5c3cc74575aeb5] = "shader_library_group", [0xe5ee32a477239a93] = "shader_library",
            [0x05106b81dcd58a13] = "runtime_font", [0x1d59bd6687db6b33] = "ragdoll_profile", [0x2a0a70acfe476e1d] = "ah_bin",
            [0x57a13425279979d7] = "ik_skeleton", [0x5fdd5fe391076f9f] = "wwise_properties", [0x6592b918e67f082c] = "havok_ai_properties",
            [0x9199bb50b6896f02] = "texture_atlas", [0xb8fd4d2cede20ed7] = "geleta", [0xc4f0f4be7fb0c8d6] = "geometry_group",
            [0xe3f2851035957af5] = "hash_lookup", [0xe985c5f61c169997] = "speedtree", [0xf7a09f8bb35a1d49] = "havok_physics_properties",
            [0xfcaaf813b4d3cc1e] = "camera_shake", [0x5ee65304478f8db5] = "bik2", [0x46bc82aae9ae0565] = "xaml",
        };

        /// <summary>The type's engine name ("unit", "texture"), or its hash when it is not known.</summary>
        public static string Type(ulong type) => types.TryGetValue(type, out var n) ? n : type.ToString("x16");

        /// <summary>Tests only: the name list to read instead of looking for HD2SDK's.</summary>
        public static string ListOverride;

        static Dictionary<ulong, string> names;
        static string namesFrom;
        static DateTime namesChecked;

        /// <summary>The list the Resource names plugin downloads from HD2SDK's own repository
        /// into the settings folder (the user, 2026-11-03: the player fetches it, nothing of it
        /// ships with this program).</summary>
        public static string Downloaded => Store.PathOf(Path.Combine("resource_names", "friendlynames.txt"));

        /// <summary>HD2SDK's friendlynames.txt: in HD2SDK_HASHLISTS when that is set, else the one
        /// the Resource names plugin downloaded, else in the newest Blender's add-on folder. Null
        /// when there is none.</summary>
        public static string ListFile()
        {
            if (ListOverride != null) return File.Exists(ListOverride) ? ListOverride : null;
            var env = Environment.GetEnvironmentVariable("HD2SDK_HASHLISTS");
            if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "friendlynames.txt"))) return Path.Combine(env, "friendlynames.txt");
            if (File.Exists(Downloaded)) return Downloaded;
            try
            {
                var blender = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blender Foundation", "Blender");
                if (!Directory.Exists(blender)) return null;
                return Directory.GetDirectories(blender)
                    .Select(v => Path.Combine(v, "scripts", "addons", "HD2SDK-CommunityEdition", "hashlists", "friendlynames.txt"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        /// <summary>Whether names can be given at all (HD2SDK's list was found).</summary>
        public static bool HaveNames => Load().Count > 0;

        /// <summary>The resource's name in HD2SDK's list ("Laser Sight W/ Flashlight", or a path), else null.</summary>
        public static string Name(ulong id) => Load().TryGetValue(id, out var n) ? n : null;

        /// <summary>The list, read once per file (lines "decimal-id name"); looked for again at
        /// most every two seconds, and read again when another file is found or it changed (a
        /// list just downloaded is used at the next state read).</summary>
        static Dictionary<ulong, string> Load()
        {
            if (names != null && ListOverride == null && DateTime.UtcNow - namesChecked < TimeSpan.FromSeconds(2)) return names;
            var file = ListFile();
            namesChecked = ListOverride != null ? DateTime.MinValue : DateTime.UtcNow;     // a test's list is never kept past it
            string key;
            try { key = file == null ? "" : file + ":" + File.GetLastWriteTimeUtc(file).Ticks; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { key = ""; }
            if (names != null && namesFrom == key) return names;
            var d = new Dictionary<ulong, string>();
            if (file != null)
                try
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        int sp = line.IndexOf(' ');
                        if (sp <= 0) continue;
                        var name = line.Substring(sp + 1).Trim();
                        if (name.Length > 0 && ulong.TryParse(line.Substring(0, sp), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && !d.ContainsKey(id))
                            d[id] = name;
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            names = d;
            namesFrom = key;
            return d;
        }
    }
}
