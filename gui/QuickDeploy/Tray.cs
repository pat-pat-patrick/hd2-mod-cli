using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Hd2Mod;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace QuickDeploy
{
    /// <summary>The window closed to the notification area: the program keeps running (its
    /// plugins too: a members' plugin unlocks its content when the game starts, which needs the
    /// manager running then), with an icon that opens the window again. While it is away the
    /// window reads nothing: the folder watcher's reads wait until it is opened, and what the
    /// last read left behind is collected and handed back to the system.</summary>
    public partial class MainWindow
    {
        Forms.NotifyIcon trayIcon;
        /// <summary>Hidden in the notification area, still running.</summary>
        bool inTray;
        /// <summary>Started with --tray: the window is made and laid out unseen, then put away.</summary>
        bool startInTray;
        /// <summary>The window closes for good (the icon's Exit, --exit, Windows signing out):
        /// closing does not put it away then.</summary>
        internal static bool Leaving;

        /// <summary>Whether closing the window leaves the program running in the notification area
        /// (gui.json "close_to_tray"; on by default: the manager is wanted running while playing).</summary>
        internal static bool CloseToTray = GuiFlag("close_to_tray", true);

        static bool GuiFlag(string key, bool otherwise)
        {
            try { return Json.Bool(Store.Load("gui.json"), key, otherwise); }
            catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException) { return otherwise; }
        }

        static void SetGuiFlag(string key, bool on)
        {
            try
            {
                var d = Store.Load("gui.json");
                d[key] = on;
                Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { }
        }

        /// <summary>The setting turned on or off: the icon comes or goes with it at once (it stays
        /// while the window is put away, whatever the setting).</summary>
        void KeepInTray(bool on)
        {
            CloseToTray = on;
            SetGuiFlag("close_to_tray", on);
            if (on) ShowTrayIcon();
            else if (!inTray && trayIcon != null) { trayIcon.Visible = false; Mark("icon hidden"); }
        }

        /// <summary>Made for a start in the notification area: shown cloaked (DWM draws nothing of
        /// it, the taskbar has no button), so it is laid out and its start runs as on any start,
        /// then put away once loaded (TrayStarted).</summary>
        void StartCloaked()
        {
            startInTray = true;
            cloakedStart = true;
            ShowActivated = false;
            ShowInTaskbar = false;
        }

        void Cloak(bool on)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int v = on ? 1 : 0;
            try { Mark("cloak " + v + ": hr 0x" + DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref v, sizeof(int)).ToString("x")); }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { }
        }

        /// <summary>After Start: the icon while the setting is on, and on a start in the
        /// notification area the window put away.</summary>
        void TrayStarted()
        {
            if (CloseToTray) ShowTrayIcon();
            if (!startInTray) return;
            startInTray = false;
            // Loaded comes inside Show, before the window has its handle: put away once Show is
            // over (measured 2026-11-19: a Hide in Loaded was undone by Show itself). It stays
            // cloaked and without a taskbar button until it is first shown (BringBack).
            Dispatcher.BeginInvoke(new Action(PutAway), DispatcherPriority.Loaded);
        }

        /// <summary>Started in the notification area and not shown yet: cloaked from the moment it
        /// has a window handle (OnSourceInitialized, which comes AFTER Loaded on this window:
        /// measured 2026-11-19, 306 ms against 650 ms).</summary>
        bool cloakedStart;

        /// <summary>The window closed with the setting on: put away, still running.</summary>
        void CloseToNotificationArea()
        {
            SaveWhere();
            SaveList();
            SavePlace();
            CloseLoaderInfo();
            PutAway();
            TellOnce();
        }

        void PutAway()
        {
            inTray = true;
            Mark("put away");
            Hide();
            beat.Stop();
            settle.Stop();
            ShowTrayIcon();
            Lighten();
            // and once more a minute later: what the start still does in the background (pages
            // warmed, scripts read, plugins started) touched its pages again after the first trim
            // (measured 2026-11-19: 167 MB working set at 45 s after a start in the tray)
            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            later.Tick += (s, e) => { later.Stop(); Lighten(); };
            later.Start();
        }

        /// <summary>The window back from the notification area (the icon, the menu, another start
        /// of the program, a Nexus link); the state read again when its files changed meanwhile.</summary>
        void BringBack()
        {
            if (inTray)
            {
                inTray = false;
                Mark("brought back");
                // the icon stays while the setting is on (the user, 2026-11-19: shown all the time,
                // so the program is seen to be running and the X is known not to end it)
                if (trayIcon != null && !CloseToTray) { trayIcon.Visible = false; Mark("icon hidden"); }
                if (cloakedStart)
                {
                    cloakedStart = false;
                    ShowInTaskbar = true;
                    ShowActivated = true;
                    Cloak(false);
                }
                Show();
                beat.Start();
                if (Written() != readStamp) Refresh();
            }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>The program ends: the icon's Exit, or --exit from another start.</summary>
        void ExitForGood()
        {
            Leaving = true;
            Mark("exit");
            Close();
        }

        /// <summary>Once the window is idle after it is put away: one full collection, the large
        /// object heap compacted, then the pages it no longer touches handed back to the system
        /// (they come back as the window is used again). Called again after a read that ends
        /// while it is away.</summary>
        void Lighten()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!inTray || refreshing || warming) return;    // the read or scan still on calls this again
                var clock = Stopwatch.StartNew();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException) { }
                Mark("put away: collected and trimmed in " + clock.ElapsedMilliseconds + " ms");
            }), DispatcherPriority.ApplicationIdle);
        }

        void ShowTrayIcon()
        {
            if (trayIcon == null)
            {
                trayIcon = new Forms.NotifyIcon { Icon = TrayIconImage(), Text = "HD2 Mod Quick Deploy" };
                trayIcon.MouseClick += (s, e) =>
                {
                    if (e.Button == Forms.MouseButtons.Left) BringBack();
                    else if (e.Button == Forms.MouseButtons.Right) OpenTrayMenu(null);
                };
            }
            trayIcon.Visible = true;
            Mark("icon shown");
        }

        System.Windows.Controls.ContextMenu trayMenu;
        // the parts of the open menu that follow the state while it stays open (a target switch)
        System.Windows.Controls.TextBlock trayStatus;
        System.Windows.Shapes.Ellipse trayDot;
        System.Windows.Controls.MenuItem trayDeploy, trayLaunch;
        System.Windows.Controls.Primitives.UniformGrid traySwitch;

        /// <summary>The icon's menu, in the window's own menu style (the user, 2026-11-19: the
        /// system's two-line menu was too bare): what the manager is doing now (the window's bottom
        /// line), open, launch, deploy, the profile in use, settings, exit. Built anew on each
        /// right-click, so it says what is true then. At the pointer, its bottom-left corner
        /// there, as menus of the notification area open; at a point given (tests only). The
        /// point is in screen PIXELS: with no placement target WPF reads the rectangle in device
        /// units (measured 2026-11-19 at 2x: a point given in DIPs opened the menu halfway up).</summary>
        void OpenTrayMenu(Point? atPixels)
        {
            if (trayMenu != null && trayMenu.IsOpen) { trayMenu.IsOpen = false; return; }
            bool running = Hd2Mod.Deploy.GameRunning();
            bool ready = st != null && !busy;
            // one width while it is open: the status line changes during a target switch, and the
            // menu grew under the pointer (measured 2026-11-19); 300 of words + the rows' margins
            var menu = new System.Windows.Controls.ContextMenu { Width = 334 };

            // what is true now: the bottom line of the window, its colour on the dot
            var head = new System.Windows.Controls.StackPanel { MaxWidth = 300 };
            head.Children.Add(new System.Windows.Controls.TextBlock { Text = Texts.Get("title"), FontWeight = FontWeights.Bold, FontSize = 14, Foreground = B("Ink") });
            var said = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var dot = trayDot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = running ? B("Blue") : FootDot.Fill, Margin = new Thickness(0, 2.5, 8, 0), VerticalAlignment = VerticalAlignment.Top };   // centred on the first line's letters (measured 11-19: 5 px low at 2x)
            System.Windows.Controls.DockPanel.SetDock(dot, System.Windows.Controls.Dock.Left);
            said.Children.Add(dot);
            said.Children.Add(trayStatus = new System.Windows.Controls.TextBlock
            {
                Text = running ? Texts.Get("tray_game_running") : FootText.Text, FontSize = 12, LineHeight = 16,
                Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap,
            });
            head.Children.Add(said);
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = head, IsHitTestVisible = false, Focusable = false });
            menu.Items.Add(new System.Windows.Controls.Separator());

            System.Windows.Controls.MenuItem Item(string text, Action act, bool enabled = true, bool strong = false)
            {
                var it = new System.Windows.Controls.MenuItem { Header = text, IsEnabled = enabled };
                if (strong) it.FontWeight = FontWeights.SemiBold;
                it.Click += (s, e) => act();
                menu.Items.Add(it);
                return it;
            }
            Item(Texts.Get("tray_open"), BringBack, strong: true);
            // the game of the target in use: the official one, or a plugin target's session
            bool busyGame = st != null && (st.OnOfficial ? running : st.TargetRunning);
            trayLaunch = Item(Texts.Get("launch"), TrayLaunch, ready && !busyGame && switchingTo == null);
            int pending = st?.Pending ?? 0;
            trayDeploy = Item(pending > 0 ? Texts.F("deploy_n", pending) : Texts.Get("deploy"), () => { BringBack(); Deploy_Click(null, null); },
                              ready && pending > 0 && !running && switchingTo == null);

            // the deploy target, official or a plugin's (HellForge), as the window's switch above the
            // library offers them and only when it shows (a plugin offers one); switching is the
            // window's own, which deploys nothing (the user, 2026-11-19)
            if (st != null)
            {
                var offers = st.TargetOffers.Where(o => o.Found || o.Id == st.Target).ToList();
                if (offers.Count > 0 || !st.OnOfficial)
                {
                    var all = new List<(string Id, string Title, string Tip)> { ("official", Texts.Get("target_official"), Texts.Get("target_official_tip")) };
                    all.AddRange(offers.Select(o => (o.Id, o.Title, o.Found ? Texts.F("target_tip", o.Title) : o.Why)));
                    if (!st.OnOfficial && all.All(a => a.Id != st.Target)) all.Add((st.Target, st.Target, Texts.F("target_no_plugin", st.Target)));
                    var current = all.First(a => a.Id == (switchingTo ?? st.Target) || (switchingTo == null && st.OnOfficial && a.Id == "official"));
                    // the window's own switch, in one row: one click changes the target, nothing to
                    // open first (the user, 2026-11-19: a submenu was "太麻烦了")
                    var row = traySwitch = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = all.Count };
                    foreach (var (id, title, tip) in all)
                    {
                        var rb = new System.Windows.Controls.RadioButton
                        {
                            Content = new System.Windows.Controls.TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis },
                            Style = (Style)FindResource("Segment"), GroupName = "tray_target", IsChecked = id == current.Id, ToolTip = tip, Tag = id,
                            HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 34, IsEnabled = ready && switchingTo == null,
                        };
                        // the menu stays open: the new target lit at once and the status line saying
                        // the switch, then what is true of the new target (UpdateTrayMenu)
                        rb.Click += (s, e) => { if (id != current.Id) SwitchTarget(id, title); };
                        row.Children.Add(rb);
                    }
                    var frame = new System.Windows.Controls.Border
                    {
                        BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Child = row, ToolTip = Texts.Get("target_switch_tip"),
                    };
                    menu.Items.Add(new System.Windows.Controls.MenuItem { Header = frame, Template = BareRow(), Focusable = false });
                }
            }

            // the profile in use, and the others to change to (profiles on, more than one)
            if (st != null && st.ProfilesOn && st.Profiles.Count > 1)
            {
                var profiles = new System.Windows.Controls.MenuItem
                {
                    Header = Texts.F("tray_profile", ProfileName(st.Profile)), IsEnabled = ready,
                    Style = (Style)FindResource("SubMenuItem"),
                };
                foreach (var name in st.Profiles)
                {
                    var to = name;
                    var it = new System.Windows.Controls.MenuItem { Header = ProfileName(name), IsChecked = name == st.Profile };
                    it.Click += async (s, e) =>
                    {
                        if (to == st.Profile) return;
                        viewLibrary = false;
                        await Exec("profile-use", to);
                        Refresh();
                    };
                    profiles.Items.Add(it);
                }
                menu.Items.Add(profiles);
            }

            // the plugins pinned to the title bar, as there: icon and name (the user, 2026-11-19)
            var pinned = Pinned().ToList();
            if (pinned.Count > 0)
            {
                bool game = Store.GameDir(null, false) != null;
                menu.Items.Add(new System.Windows.Controls.Separator());
                foreach (var (type, title, icon) in pinned)
                {
                    var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                    if (icon is FrameworkElement fe) { fe.Width = fe.Height = 16; fe.Margin = new Thickness(0, 0, 10, 2); }   // its centre on the letters' (measured 11-19: 2 px low at 2x)
                    row.Children.Add(icon);
                    row.Children.Add(new System.Windows.Controls.TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
                    var it = new System.Windows.Controls.MenuItem { Header = row, IsEnabled = game, ToolTip = game ? null : Texts.Get("plugin_no_game") };
                    System.Windows.Controls.ToolTipService.SetShowOnDisabled(it, true);
                    var t = type;
                    it.Click += (s, e) => OpenPlugin(t);
                    menu.Items.Add(it);
                }
            }

            menu.Items.Add(new System.Windows.Controls.Separator());
            Item(Texts.Get("settings"), () => { BringBack(); if (SettingsPage.Visibility != Visibility.Visible) Settings_Click(null, null); });
            Item(Texts.Get("tray_exit"), ExitForGood);

            // placed above the point, left edges together; below it when there is no room above
            var p = atPixels ?? Pointer();
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.PlacementRectangle = new Rect(p, new Size(0, 0));
            // the menu takes the foreground, so a click anywhere else closes it
            menu.Opened += (s, e) =>
            {
                if (PresentationSource.FromVisual(menu) is HwndSource src) SetForegroundWindow(src.Handle);
            };
            menu.Closed += (s, e) =>
            {
                if (trayMenu != menu) return;
                trayMenu = null;
                trayStatus = null; trayDot = null; trayDeploy = trayLaunch = null; traySwitch = null;
            };
            trayMenu = menu;
            menu.IsOpen = true;
        }

        /// <summary>HD2QD_CLICK_TEST=traymenu (pictures only): the menu opened in the middle of the
        /// window's screen and drawn into a PNG once laid out, with its size and the place it
        /// opened at written beside it (file.txt).</summary>
        void TrayMenuShot(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            // the middle of the work area's bottom edge, where the taskbar meets it, in pixels
            var area = SystemParameters.WorkArea;
            var toPx = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var at = toPx.Transform(new Point(area.Left + area.Width / 2, area.Bottom - 4));
            OpenTrayMenu(at);
            var menu = trayMenu;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (menu == null) return;
                var dpi = VisualTreeHelper.GetDpi(menu);
                SavePng(menu, file);
                var top = menu.PointToScreen(new Point(0, 0));
                var bottom = menu.PointToScreen(new Point(menu.ActualWidth, menu.ActualHeight));
                File.WriteAllText(file + ".txt", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "asked at {0:0},{1:0} px; menu {2:0}x{3:0} dip; on screen px {4:0},{5:0} to {6:0},{7:0}; dpi scale {8}",
                    at.X, at.Y, menu.ActualWidth, menu.ActualHeight, top.X, top.Y, bottom.X, bottom.Y, dpi.DpiScaleX)
                    + "\r\n" + MenuRows(menu.Items, ""));
            }), DispatcherPriority.ContextIdle);
        }

        /// <summary>An element drawn into a PNG at its screen's scale (pictures only).</summary>
        static void SavePng(FrameworkElement el, string file)
        {
            var dpi = VisualTreeHelper.GetDpi(el);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(el.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(el);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var f = File.Create(file)) enc.Save(f);
        }

        /// <summary>Each row of a menu and its submenus as text (pictures only: a submenu's rows are
        /// not in the picture of the closed menu): enabled or not, checked or not.</summary>
        static string MenuRows(System.Windows.Controls.ItemCollection items, string indent)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var o in items)
            {
                if (!(o is System.Windows.Controls.MenuItem mi)) { sb.Append(indent).Append("----\r\n"); continue; }
                if (mi.Header is System.Windows.Controls.Border bd && bd.Child is System.Windows.Controls.Panel seg)
                {
                    sb.Append(indent).Append("switch: ").Append(string.Join(" | ", seg.Children.OfType<System.Windows.Controls.RadioButton>()
                        .Select(r => (r.IsChecked == true ? "[" : "") + ((r.Content as System.Windows.Controls.TextBlock)?.Text ?? "") + (r.IsChecked == true ? "]" : "")
                                     + (r.IsEnabled ? "" : " (off)")))).Append("\r\n");
                    continue;
                }
                string words = mi.Header is string h ? h
                    : string.Join(" ", ((mi.Header as System.Windows.Controls.Panel)?.Children.OfType<System.Windows.Controls.TextBlock>() ?? Enumerable.Empty<System.Windows.Controls.TextBlock>()).Select(t => t.Text));
                sb.Append(indent).Append(mi.IsChecked ? "[x] " : "    ").Append(words).Append(mi.IsEnabled ? "" : "  (off)").Append("\r\n");
                if (mi.Items.Count > 0) sb.Append(MenuRows(mi.Items, indent + "      "));
            }
            return sb.ToString();
        }

        /// <summary>The open menu brought up to date: during a target switch, the switch said and
        /// nothing else to click; once the new target's state is drawn, its status, deploy count and
        /// launch. Nothing when the menu is closed.</summary>
        void UpdateTrayMenu()
        {
            Mark("tray menu update: " + (trayMenu == null ? "no menu" : trayMenu.IsOpen ? "open" : "closed") + (trayStatus == null ? ", no status" : "") + ", switching " + (switchingTo ?? "-"));
            if (trayMenu == null || !trayMenu.IsOpen || trayStatus == null) return;
            bool switching = switchingTo != null, ready = st != null && !busy && !switching;
            bool running = Hd2Mod.Deploy.GameRunning();
            bool busyGame = st != null && (st.OnOfficial ? running : st.TargetRunning);
            trayStatus.Text = switching ? Texts.F("target_switching", switchingTitle) : running ? Texts.Get("tray_game_running") : FootText.Text;
            trayDot.Fill = switching || running ? B("Blue") : FootDot.Fill;
            int pending = st?.Pending ?? 0;
            if (trayDeploy != null)
            {
                trayDeploy.Header = pending > 0 && !switching ? Texts.F("deploy_n", pending) : Texts.Get("deploy");
                trayDeploy.IsEnabled = ready && pending > 0 && !running;
            }
            if (trayLaunch != null) trayLaunch.IsEnabled = ready && !busyGame;
            if (traySwitch != null && st != null)
            {
                // the target chosen lit at once, then the one in use: from the state, not the click
                string lit = switchingTo ?? (st.OnOfficial ? "official" : st.Target);
                foreach (var rb in traySwitch.Children.OfType<System.Windows.Controls.RadioButton>())
                {
                    rb.IsEnabled = ready;
                    rb.IsChecked = rb.Tag as string == lit;
                }
            }
        }

        /// <summary>A menu row that is only its content, at the rows' margins: no highlight of its
        /// own, so the controls in it (the target switch) answer the pointer themselves.</summary>
        static System.Windows.Controls.ControlTemplate BareRow()
        {
            var t = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.MenuItem));
            var f = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            f.SetValue(System.Windows.Controls.ContentPresenter.ContentSourceProperty, "Header");
            f.SetValue(FrameworkElement.MarginProperty, new Thickness(16, 6, 16, 6));
            t.VisualTree = f;
            return t;
        }

        /// <summary>Every element of type T under root, in the visual tree (tests only).</summary>
        static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t) yield return t;
                foreach (var d in FindAll<T>(c)) yield return d;
            }
        }

        /// <summary>The pointer on the screen, in pixels.</summary>
        static Point Pointer()
        {
            GetCursorPos(out var pt);
            return new Point(pt.X, pt.Y);
        }

        /// <summary>Launch from the icon's menu: straight away when nothing needs asking (all
        /// deployed; the official game found, or the plugin target found and no session of it running);
        /// otherwise with the window open, where the launch asks.</summary>
        void TrayLaunch()
        {
            bool quiet = st != null && st.Pending == 0
                && (st.OnOfficial ? st.GameDir != null : st.TargetOffered && st.TargetFound && !st.TargetRunning);
            if (!quiet) BringBack();
            Launch_Click(null, null);
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

        static System.Drawing.Icon TrayIconImage()
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            using (var s = res.Stream) return new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
        }

        /// <summary>The first time the window is closed to the notification area, a word on where it
        /// went and how to quit; never again (gui.json "tray_told").</summary>
        void TellOnce()
        {
            if (GuiFlag("tray_told", false) || trayIcon == null) return;
            SetGuiFlag("tray_told", true);
            trayIcon.ShowBalloonTip(8000, Texts.Get("tray_told_title"), Texts.Get("tray_told"), Forms.ToolTipIcon.None);
        }

        void DisposeTrayIcon()
        {
            if (trayIcon == null) return;
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayIcon = null;
        }

        const int DWMWA_CLOAK = 13;
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
    }

    /// <summary>Starting with Windows: a value under the user's Run key that starts this program
    /// in the notification area (--tray). Only the current user's key; nothing for other users.
    /// HD2QD_RUN_KEY (tests only) puts it under another key of the current user.</summary>
    static class Autostart
    {
        const string Name = "HD2 Mod Quick Deploy";
        static string KeyPath => Environment.GetEnvironmentVariable("HD2QD_RUN_KEY") ?? @"Software\Microsoft\Windows\CurrentVersion\Run";

        static string Command => "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --tray";

        /// <summary>Whether the value is there (for any copy of this program).</summary>
        public static bool On
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(KeyPath)) return k?.GetValue(Name) is string; }
                catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is IOException) { return false; }
            }
        }

        /// <summary>The value written for this copy of the program, or taken away. False when the
        /// key could not be written.</summary>
        public static bool Set(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (on) k.SetValue(Name, Command);
                    else if (k.GetValue(Name) != null) k.DeleteValue(Name);
                }
                return true;
            }
            catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is IOException) { return false; }
        }
    }
}
