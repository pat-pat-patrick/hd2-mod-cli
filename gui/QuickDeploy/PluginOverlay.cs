using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Hd2Mod;

namespace QuickDeploy
{
    // (the user, 2026-11-28: some plugins need a key that brings their window up in the game;
    // "做成公共功能") a plugin's key, while the game runs, brings its tab (or its own window) up
    // over the game, on top and with the keys; the same key again puts it away and gives the
    // game back its focus. As HD2 Mod Setting GUI has done since 10-30 (MainWindow.Overlay.cs
    // there), proven in game: a key of the system's (RegisterHotKey), held only while the game
    // runs, nothing in the game's process, a window over a borderless game window. The keys are
    // the plugins' own (PluginKeys.Declared), the player's choice on each plugin's card.
    public partial class MainWindow
    {
        const int WM_HOTKEY = 0x312, KeyIdBase = 0x5100;
        const uint MOD_NOREPEAT = 0x4000;

        List<PluginKeys.Bound> keyPlan = new List<PluginKeys.Bound>();
        readonly Dictionary<int, PluginKeys.Bound> keysHeld = new Dictionary<int, PluginKeys.Bound>();
        readonly HashSet<string> keyBusy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // another program has it
        DispatcherTimer keyWatch;
        bool keyGameUp;              // the game ran at the last look
        bool keyChoosing;            // a key is being chosen on a card: none held meanwhile
        Window overlaidWin;          // brought up by a key, on top until put away or left
        bool overlaidFromTray;       // the window was in the notification area before
        string overlaidFile;         // the plugin whose key brought it up

        /// <summary>The plugins' keys read again (a plugin added or removed, a key chosen); held
        /// at once when the game runs. Without any key nothing is watched.</summary>
        void ArmPluginKeys()
        {
            List<PluginKeys.Bound> plan;
            try { plan = PluginKeys.Plan(PluginFiles.All().Where(p => p.Type != null).Select(p => (p.File, p.Type))); }
            catch (Exception e) when (e is ModError || e is System.IO.IOException || e is UnauthorizedAccessException) { plan = new List<PluginKeys.Bound>(); }
            ReleasePluginKeys();
            keyPlan = plan;
            ManagerLog.Info("plugin keys: " + (plan.Count == 0 ? "none" : string.Join(", ", plan.Select(b => b.File + " " + (b.Key == "" ? "-" : b.Key) + (b.SameAs != null ? " (same as " + b.SameAs + ")" : "")))));
            bool any = plan.Any(b => b.Key != "" && b.SameAs == null);
            if (!any)
            {
                keyWatch?.Stop();
                keyWatch = null;
                keyGameUp = false;
                return;
            }
            if (keyWatch == null)
            {
                // the game's process looked for every 5 s, as the game watch does; only while a
                // plugin has a key
                keyWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                keyWatch.Tick += (s, e) => KeysForGame();
                keyWatch.Start();
            }
            keyGameUp = false;
            KeysForGame();
        }

        void KeysForGame()
        {
            bool up;
            try { up = Hd2Mod.Deploy.GameRunning(); }
            catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception) { up = false; }
            keyGameUp = up;
            // a key another program had is tried again at the next look
            if (up && !keyChoosing) HoldPluginKeys(); else ReleasePluginKeys();
        }

        void HoldPluginKeys()
        {
            if (keysHeld.Count > 0 && keyBusy.Count == 0) return;
            ReleasePluginKeys();
            var me = new WindowInteropHelper(this).EnsureHandle();
            var busyBefore = new HashSet<string>(keyBusy, StringComparer.OrdinalIgnoreCase);
            keyBusy.Clear();
            int id = KeyIdBase;
            foreach (var b in keyPlan.Where(x => x.Key != "" && x.SameAs == null))
            {
                var h = Hotkey.Parse(b.Key);
                if (h == null) continue;
                if (KeyNative.RegisterHotKey(me, ++id, h.Modifiers | MOD_NOREPEAT, h.VirtualKey)) keysHeld[id] = b;
                else { keyBusy.Add(b.File); Mark("plugin key busy: " + b.File + " " + b.Key); }
            }
            Mark("plugin keys held: " + keysHeld.Count);
            if (!keyBusy.SetEquals(busyBefore) || busyBefore.Count == 0) ManagerLog.Info("plugin keys held: " + keysHeld.Count + (keyBusy.Count > 0 ? ", taken by another program: " + string.Join(", ", keyBusy) : ""));
            if (!keyBusy.SetEquals(busyBefore) && PluginsPage.Visibility == Visibility.Visible) RenderPlugins();
        }

        void ReleasePluginKeys()
        {
            if (keysHeld.Count == 0) return;
            var me = new WindowInteropHelper(this).Handle;
            foreach (var id in keysHeld.Keys) KeyNative.UnregisterHotKey(me, id);
            keysHeld.Clear();
            Mark("plugin keys let go");
        }

        /// <summary>WM_HOTKEY from the window's hook: true when it was one of the plugins' keys.</summary>
        bool PluginKeyPressed(int id)
        {
            if (!keysHeld.TryGetValue(id, out var b)) return false;
            try { ToggleOverlay(b); }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // a plugin's Open that throws is said, not let through: it would end the manager
                ManagerLog.Warn("plugin key " + b.File + ": " + e.Message);
            }
            return true;
        }

        /// <summary>The plugin's window shown in front: its tab in this window shown, or its own
        /// window.</summary>
        bool PluginInFront(PluginKeys.Bound b, IntPtr front)
        {
            if (front == IntPtr.Zero) return false;
            if (front == new WindowInteropHelper(this).Handle)
                return !inTray && WindowState != WindowState.Minimized && string.Equals(TabOf(shownPage)?.File, b.File, StringComparison.OrdinalIgnoreCase);
            var own = PluginWindow(b.Type);
            return own != null && new WindowInteropHelper(own).Handle == front;
        }

        /// <summary>A window of the plugin's own that is shown (a plugin that opens one instead of
        /// a tab); null for none.</summary>
        Window PluginWindow(Type t) =>
            Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != this && w.IsVisible && w.GetType().Assembly == t.Assembly);

        void ToggleOverlay(PluginKeys.Bound b)
        {
            var game = GameWindow();
            var front = KeyNative.GetForegroundWindow();
            // in front: its tab or window, or what its key brought up (a plugin that opens
            // nothing of its own brings the manager)
            bool broughtUp = overlaidWin != null && string.Equals(overlaidFile, b.File, StringComparison.OrdinalIgnoreCase)
                             && new WindowInteropHelper(overlaidWin).Handle == front;
            if (broughtUp || PluginInFront(b, front))
            {
                // away: the game in front again, with the keys; minimized without passing the
                // front on (SW_SHOWMINNOACTIVE), or back to the notification area it came from
                var w = overlaidWin ?? (PluginWindow(b.Type) ?? (Window)this);
                w.Topmost = false;
                if (w == this && overlaidFromTray) PutAway();
                else KeyNative.ShowWindow(new WindowInteropHelper(w).Handle, 7);
                overlaidWin = null;
                overlaidFile = null;
                overlaidFromTray = false;
                if (game != IntPtr.Zero) KeyNative.SetForegroundWindow(game);
                Mark("plugin key: away " + b.File);
                return;
            }
            bool fromTray = inTray;
            // as its card's Open: a tab of this window (shown by it), else its own window
            PluginFiles.Call(b.Type, "Open", this, PluginHostHere());
            var target = PluginWindow(b.Type) ?? (Window)this;
            if (target == this && inTray) BringBack();
            var h = new WindowInteropHelper(target).Handle;
            if (game != IntPtr.Zero && target.WindowState != WindowState.Maximized) OntoGame(h, game);
            if (target.WindowState == WindowState.Minimized) target.WindowState = WindowState.Normal;
            if (overlaidWin != target)
            {
                EventHandler left = null;
                left = (s, e) => { target.Topmost = false; target.Deactivated -= left; if (overlaidWin == target) overlaidWin = null; };
                target.Deactivated += left;
            }
            overlaidWin = target;
            overlaidFile = b.File;
            overlaidFromTray = fromTray && target == this;
            target.Topmost = true;
            target.Activate();
            KeyNative.SetForegroundWindow(h);
            Mark("plugin key: up " + b.File);
        }

        /// <summary>On a window plugin's card: its key, chosen by pressing it (Esc keeps the one
        /// before), cleared with ×; under it why it does nothing when it does nothing (another
        /// program has it, another plugin has the same).</summary>
        FrameworkElement KeyRow(string file, Type type)
        {
            var bound = keyPlan.FirstOrDefault(b => string.Equals(b.File, file, StringComparison.OrdinalIgnoreCase));
            var key = Hotkey.Parse(bound?.Key ?? PluginKeys.Chosen(file, PluginKeys.Declared(type)));
            bool waiting = false;
            var box = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            var line = new DockPanel();
            var btn = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("Ghost"), MinWidth = 150, MinHeight = 32, Padding = new Thickness(12, 0, 12, 0),
                FontFamily = (System.Windows.Media.FontFamily)FindResource("Mono"), VerticalAlignment = VerticalAlignment.Center,
            };
            var clear = new System.Windows.Controls.Button
            {
                Content = "×", Style = (Style)FindResource("Ghost"), MinWidth = 36, MinHeight = 32, Margin = new Thickness(6, 0, 0, 0),
                ToolTip = Texts.Get("plugin_key_clear"), VerticalAlignment = VerticalAlignment.Center,
            };
            void Paint()
            {
                btn.Content = waiting ? Texts.Get("plugin_key_press") : key == null ? Texts.Get("plugin_key_none") : key.Label();
                btn.BorderBrush = waiting ? B("Yellow") : B("Grid");
                clear.Visibility = key == null ? Visibility.Hidden : Visibility.Visible;
            }
            void Stop(Hotkey taken, bool take)
            {
                waiting = false;
                keyChoosing = false;
                if (take)
                {
                    key = taken;
                    PluginKeys.Choose(file, taken?.ToString() ?? "");
                    ArmPluginKeys();
                    RenderPlugins();         // the notes of every card: a key the same as another's
                    return;
                }
                if (keyWatch != null) KeysForGame();
                Paint();
            }
            btn.Click += (s, e) =>
            {
                // the keys let go meanwhile, so that pressing the one held reaches the button
                waiting = true;
                keyChoosing = true;
                ReleasePluginKeys();
                Paint();
                btn.Focus();
            };
            btn.PreviewKeyDown += (s, e) =>
            {
                if (!waiting) return;
                e.Handled = true;
                var k = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                if (k == System.Windows.Input.Key.Escape) { Stop(null, false); return; }
                var h = Hotkey.From(k, System.Windows.Input.Keyboard.Modifiers);
                if (h != null) Stop(h, true);
            };
            btn.LostKeyboardFocus += (s, e) => { if (waiting) Stop(null, false); };
            clear.Click += (s, e) => Stop(null, true);
            Paint();
            var keys = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            keys.Children.Add(btn);
            keys.Children.Add(clear);
            DockPanel.SetDock(keys, Dock.Right);
            line.Children.Add(keys);
            line.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Texts.Get("plugin_key"), Foreground = B("Ink"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            });
            box.Children.Add(line);
            if (Texts.Hints) box.Children.Add(Small(Texts.Get("plugin_key_d"), new Thickness(0, 4, 0, 0)));
            string note = null;
            if (bound?.SameAs != null)
            {
                var other = keyPlan.FirstOrDefault(b => b.File == bound.SameAs);
                note = Texts.F("plugin_key_same", other?.Type != null ? PluginFiles.Title(other.Type, Texts.Lang) ?? other.File : bound.SameAs);
            }
            else if (key != null && keyBusy.Contains(file)) note = Texts.Get("plugin_key_busy");
            if (note != null)
                box.Children.Add(new System.Windows.Controls.TextBlock { Text = note, Foreground = B("Yellow"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            return box;
        }

        /// <summary>The game's main window; IntPtr.Zero when it does not run (or has none yet).</summary>
        static IntPtr GameWindow()
        {
            try
            {
                var h = IntPtr.Zero;
                foreach (var p in Process.GetProcessesByName("helldivers2")) { if (h == IntPtr.Zero) h = p.MainWindowHandle; p.Dispose(); }
                return h;
            }
            catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception) { return IntPtr.Zero; }
        }

        /// <summary>A window off the game's screen brought to the middle of the game, at most as
        /// large as it (HD2 Mod Setting GUI's OntoGame).</summary>
        static void OntoGame(IntPtr me, IntPtr game)
        {
            var p = new KeyNative.Placement { Length = Marshal.SizeOf(typeof(KeyNative.Placement)) };
            if (!KeyNative.GetWindowRect(game, out var g) || !KeyNative.GetWindowPlacement(me, ref p)) return;
            // the placement's rectangle is in the work area's coordinates (they differ from the
            // screen's by a taskbar on the left or the top)
            var mon = new KeyNative.MonitorInfo { Size = Marshal.SizeOf(typeof(KeyNative.MonitorInfo)) };
            if (!KeyNative.GetMonitorInfo(KeyNative.MonitorFromWindow(me, 2), ref mon)) return;
            int dx = mon.Work.Left - mon.Screen.Left, dy = mon.Work.Top - mon.Screen.Top;
            var w = p.Normal;
            int cx = (w.Left + w.Right) / 2 + dx, cy = (w.Top + w.Bottom) / 2 + dy;
            if (cx >= g.Left && cx < g.Right && cy >= g.Top && cy < g.Bottom) return;
            int ww = Math.Min(w.Right - w.Left, g.Right - g.Left), wh = Math.Min(w.Bottom - w.Top, g.Bottom - g.Top);
            var gm = new KeyNative.MonitorInfo { Size = Marshal.SizeOf(typeof(KeyNative.MonitorInfo)) };
            if (!KeyNative.GetMonitorInfo(KeyNative.MonitorFromWindow(game, 2), ref gm)) return;
            int left = g.Left + (g.Right - g.Left - ww) / 2 - (gm.Work.Left - gm.Screen.Left);
            int top = g.Top + (g.Bottom - g.Top - wh) / 2 - (gm.Work.Top - gm.Screen.Top);
            p.Normal = new KeyNative.Rect32 { Left = left, Top = top, Right = left + ww, Bottom = top + wh };
            if (p.ShowCmd != 2) p.ShowCmd = 1;
            KeyNative.SetWindowPlacement(me, ref p);
        }

        static class KeyNative
        {
            public struct Rect32 { public int Left, Top, Right, Bottom; }
            public struct Placement { public int Length, Flags, ShowCmd; public int MinX, MinY, MaxX, MaxY; public Rect32 Normal; }
            public struct MonitorInfo { public int Size; public Rect32 Screen, Work; public int Flags; }
            [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
            [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
            [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
            [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
            [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect32 r);
            [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref Placement p);
            [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr h, ref Placement p);
            [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
            [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref MonitorInfo i);
        }
    }
}
