using System;
using System.Collections.Generic;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>Keys that bring a plugin's window up over the game (the user, 2026-11-28: some
    /// plugins need one; HD2 Mod Setting GUI has had its own since 10-30, made a service of the
    /// manager for every plugin). A window plugin declares one as a public static string
    /// OverlayKey (a field, a constant or a property): a key by the name HD2 Mod Setting GUI
    /// writes, "page down", "ctrl+shift+f10"; "" for a plugin that can have one but brings none.
    /// The player's choice is kept in gui.json "plugin_keys" {file: key}, "" for none. The window
    /// holds the keys (RegisterHotKey) only while the game runs.</summary>
    public static class PluginKeys
    {
        public const string Member = "OverlayKey";

        /// <summary>The key a plugin brings, normalized; "" for one it can have and does not
        /// bring; null when it declares none (or is not a window plugin: only a window or a tab
        /// can come up over the game).</summary>
        public static string Declared(Type t)
        {
            if (t == null || PluginFiles.Kind(t) != "window") return null;
            object v = null;
            try
            {
                var f = t.GetField(Member);
                if (f != null && f.IsStatic) v = f.GetValue(null);
                else if (t.GetProperty(Member) is System.Reflection.PropertyInfo p && p.GetMethod != null && p.GetMethod.IsStatic) v = p.GetValue(null);
                else return null;
            }
            catch (Exception e) when (e is System.Reflection.TargetInvocationException || e is InvalidOperationException || e is NotSupportedException)
            {
                ManagerLog.Warn("plugin " + t.FullName + " OverlayKey: " + (e.InnerException ?? e).Message);
                return "";
            }
            return v is string s ? Normal(s) : null;
        }

        /// <summary>The key in force for a plugin that declares one: the player's, else the
        /// plugin's own; "" for none.</summary>
        public static string Chosen(string file, string declared)
        {
            var mine = Json.Obj(Store.Load("gui.json"), "plugin_keys");
            return mine != null && mine.TryGetValue(file, out var v) && v is string s ? Normal(s) : declared ?? "";
        }

        /// <summary>The player's key for a plugin ("" none); null forgets it, so the plugin's own
        /// counts again.</summary>
        public static void Choose(string file, string key)
        {
            var d = Store.Load("gui.json");
            var mine = Json.Obj(d, "plugin_keys") ?? new Dictionary<string, object>();
            var keep = mine.Where(kv => !string.Equals(kv.Key, file, StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (key != null) keep[file] = Normal(key);
            d["plugin_keys"] = keep;
            Store.Save("gui.json", d);
        }

        public sealed class Bound
        {
            public string File;
            public Type Type;
            public string Key;          // "" for none
            public string SameAs;       // the file of an earlier plugin with the same key: this one is not held
        }

        /// <summary>Every added plugin that declares a key, in the given order, with the key in
        /// force; a key a plugin before it already has is not held twice (SameAs names that one).</summary>
        public static List<Bound> Plan(IEnumerable<(string File, Type Type)> plugins)
        {
            var list = new List<Bound>();
            var owner = new Dictionary<string, string>();
            foreach (var (file, type) in plugins ?? Enumerable.Empty<(string, Type)>())
            {
                var declared = Declared(type);
                if (declared == null || file == null) continue;
                var b = new Bound { File = file, Type = type, Key = Chosen(file, declared) };
                if (b.Key != "")
                {
                    if (owner.TryGetValue(b.Key, out var first)) b.SameAs = first;
                    else owner[b.Key] = file;
                }
                list.Add(b);
            }
            return list;
        }

        static readonly string[] Mods = { "ctrl", "alt", "shift" };

        /// <summary>The names of the keys that can be one (the window's Hotkey names the same
        /// keys from what is pressed): a letter, a digit, F1 to F24, the number pad, and these.</summary>
        static readonly HashSet<string> Named = new HashSet<string>(
            Enumerable.Range(0, 26).Select(i => ((char)('a' + i)).ToString())
            .Concat(Enumerable.Range(0, 10).Select(i => i.ToString()))
            .Concat(Enumerable.Range(0, 10).Select(i => "numpad " + i))
            .Concat(Enumerable.Range(1, 24).Select(i => "f" + i))
            .Concat(new[]
            {
                "space", "tab", "backspace", "enter", "delete", "insert", "home", "end", "page up", "page down",
                "up", "down", "left", "right", "caps lock", "num lock", "scroll lock", "pause", "print screen",
                "backtick", "minus", "equal", "open bracket", "close bracket", "backslash", "semicolon", "quote",
                "comma", "period", "slash", "numpad *", "numpad +", "numpad -", "numpad .", "numpad /",
            }));

        /// <summary>A key typed on the command line in its one spelling (Normal), or null when it
        /// is not a key: only Ctrl, Alt and Shift before one key of those named.</summary>
        public static string Valid(string key)
        {
            var n = Normal(key);
            if (n == "") return null;
            var last = n.EndsWith("numpad +") ? "numpad +" : n.Substring(n.LastIndexOf('+') + 1);
            // what was typed before the key: Normal leaves out a modifier it does not know ("win")
            var raw = key.Trim().ToLowerInvariant();
            raw = raw.EndsWith("+") && raw.TrimEnd('+').TrimEnd().EndsWith("numpad") ? raw.Substring(0, raw.TrimEnd('+').TrimEnd().Length - "numpad".Length)
                : raw.Contains('+') ? raw.Substring(0, raw.LastIndexOf('+')) : "";
            if (raw.Split('+').Select(m => m.Trim()).Where(m => m != "").Any(m => !Mods.Contains(m))) return null;
            return Named.Contains(last) ? n : null;
        }

        /// <summary>One spelling for one key: lower case, the modifiers in the order Ctrl, Alt,
        /// Shift, no spaces around '+' ("numpad +" keeps its own). "" for none or a blank.</summary>
        public static string Normal(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            var parts = key.Trim().ToLowerInvariant().Split('+').Select(p => p.Trim()).ToList();
            string last;
            if (parts.Count > 1 && parts[parts.Count - 1] == "" && parts[parts.Count - 2].EndsWith("numpad"))
            {
                last = "numpad +";
                parts.RemoveRange(parts.Count - 2, 2);
            }
            else
            {
                last = parts[parts.Count - 1];
                parts.RemoveAt(parts.Count - 1);
            }
            var held = Mods.Where(m => parts.Contains(m));
            return string.Concat(held.Select(m => m + "+")) + last;
        }
    }
}
