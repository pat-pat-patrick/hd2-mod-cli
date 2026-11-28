using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;

namespace QuickDeploy
{
    /// <summary>A key that brings a plugin's window up over the game (PluginKeys): Ctrl, Alt and
    /// Shift, any of them, and one key by the name HD2 Mod Setting GUI writes ("page down",
    /// "ctrl+shift+f10"). Taken from that program's Hotkey and KeyNames (the same author, MIT),
    /// keyboard keys only: RegisterHotKey takes no mouse button.</summary>
    public sealed class Hotkey
    {
        public bool Ctrl, Alt, Shift;
        public string Key = "";

        static readonly Dictionary<Key, string> Names = new Dictionary<Key, string>
        {
            [System.Windows.Input.Key.Space] = "space", [System.Windows.Input.Key.Tab] = "tab", [System.Windows.Input.Key.Back] = "backspace",
            [System.Windows.Input.Key.Enter] = "enter", [System.Windows.Input.Key.Delete] = "delete", [System.Windows.Input.Key.Insert] = "insert",
            [System.Windows.Input.Key.Home] = "home", [System.Windows.Input.Key.End] = "end",
            [System.Windows.Input.Key.PageUp] = "page up", [System.Windows.Input.Key.PageDown] = "page down",
            [System.Windows.Input.Key.Up] = "up", [System.Windows.Input.Key.Down] = "down", [System.Windows.Input.Key.Left] = "left", [System.Windows.Input.Key.Right] = "right",
            [System.Windows.Input.Key.CapsLock] = "caps lock", [System.Windows.Input.Key.NumLock] = "num lock", [System.Windows.Input.Key.Scroll] = "scroll lock",
            [System.Windows.Input.Key.Pause] = "pause", [System.Windows.Input.Key.PrintScreen] = "print screen",
            [System.Windows.Input.Key.Oem3] = "backtick", [System.Windows.Input.Key.OemMinus] = "minus", [System.Windows.Input.Key.OemPlus] = "equal",
            [System.Windows.Input.Key.Oem4] = "open bracket", [System.Windows.Input.Key.Oem6] = "close bracket", [System.Windows.Input.Key.Oem5] = "backslash",
            [System.Windows.Input.Key.Oem1] = "semicolon", [System.Windows.Input.Key.Oem7] = "quote", [System.Windows.Input.Key.OemComma] = "comma",
            [System.Windows.Input.Key.OemPeriod] = "period", [System.Windows.Input.Key.Oem2] = "slash",
            [System.Windows.Input.Key.Multiply] = "numpad *", [System.Windows.Input.Key.Add] = "numpad +", [System.Windows.Input.Key.Subtract] = "numpad -",
            [System.Windows.Input.Key.Decimal] = "numpad .", [System.Windows.Input.Key.Divide] = "numpad /",
        };

        /// <summary>The name of a key pressed; null for one that cannot be a key of its own
        /// (a modifier, a key with no name here).</summary>
        public static string NameOf(Key k)
        {
            if (k >= System.Windows.Input.Key.A && k <= System.Windows.Input.Key.Z) return ((char)('a' + (k - System.Windows.Input.Key.A))).ToString();
            if (k >= System.Windows.Input.Key.D0 && k <= System.Windows.Input.Key.D9) return ((char)('0' + (k - System.Windows.Input.Key.D0))).ToString();
            if (k >= System.Windows.Input.Key.NumPad0 && k <= System.Windows.Input.Key.NumPad9) return "numpad " + (k - System.Windows.Input.Key.NumPad0);
            if (k >= System.Windows.Input.Key.F1 && k <= System.Windows.Input.Key.F24) return "f" + (k - System.Windows.Input.Key.F1 + 1).ToString(CultureInfo.InvariantCulture);
            return Names.TryGetValue(k, out var n) ? n : null;
        }

        static readonly Dictionary<string, Key> ByName = Enum.GetValues(typeof(Key)).Cast<Key>()
            .Select(k => (k, n: NameOf(k))).Where(x => x.n != null)
            .GroupBy(x => x.n).ToDictionary(g => g.Key, g => g.First().k);

        /// <summary>null for "" or a name it does not know.</summary>
        public static Hotkey Parse(string text)
        {
            var n = Hd2Mod.PluginKeys.Normal(text);
            if (n == "") return null;
            var h = new Hotkey();
            var last = n.EndsWith("numpad +") ? "numpad +" : n.Substring(n.LastIndexOf('+') + 1);
            var head = n.Substring(0, n.Length - last.Length).TrimEnd('+');
            foreach (var m in head == "" ? new string[0] : head.Split('+'))
                switch (m)
                {
                    case "ctrl": h.Ctrl = true; break;
                    case "alt": h.Alt = true; break;
                    case "shift": h.Shift = true; break;
                    default: return null;
                }
            h.Key = last;
            return ByName.ContainsKey(h.Key) ? h : null;
        }

        /// <summary>From a key pressed with the modifiers held; null for a key it cannot take.</summary>
        public static Hotkey From(Key k, ModifierKeys mods)
        {
            var name = NameOf(k);
            if (name == null) return null;
            return new Hotkey
            {
                Key = name, Ctrl = (mods & ModifierKeys.Control) != 0,
                Alt = (mods & ModifierKeys.Alt) != 0, Shift = (mods & ModifierKeys.Shift) != 0,
            };
        }

        public override string ToString() =>
            (Ctrl ? "ctrl+" : "") + (Alt ? "alt+" : "") + (Shift ? "shift+" : "") + Key;

        static readonly Dictionary<string, string> Signs = new Dictionary<string, string>
        {
            ["backtick"] = "`", ["minus"] = "-", ["equal"] = "=", ["open bracket"] = "[", ["close bracket"] = "]",
            ["backslash"] = "\\", ["semicolon"] = ";", ["quote"] = "'", ["comma"] = ",", ["period"] = ".", ["slash"] = "/",
            ["up"] = "↑", ["down"] = "↓", ["left"] = "←", ["right"] = "→",
        };

        /// <summary>As shown: "Ctrl + F10", "Page Down", "Num 1".</summary>
        public string Label()
        {
            string key;
            if (Signs.TryGetValue(Key, out var sign)) key = sign;
            else if (Key.StartsWith("numpad ")) key = "Num " + Key.Substring(7).ToUpperInvariant();
            else if (Key.Length <= 3) key = Key.ToUpperInvariant();
            else key = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Key);
            return string.Join(" + ", new[] { Ctrl ? "Ctrl" : null, Alt ? "Alt" : null, Shift ? "Shift" : null, key }.Where(s => s != null));
        }

        /// <summary>For RegisterHotKey: MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, and the virtual key.</summary>
        public uint Modifiers => (Alt ? 1u : 0) | (Ctrl ? 2u : 0) | (Shift ? 4u : 0);
        public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(ByName[Key]);
    }
}
