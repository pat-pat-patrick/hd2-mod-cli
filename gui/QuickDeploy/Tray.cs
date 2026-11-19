using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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

        internal static void KeepInTray(bool on)
        {
            CloseToTray = on;
            SetGuiFlag("close_to_tray", on);
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

        /// <summary>After Start, on a start in the notification area.</summary>
        void TrayStarted()
        {
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
                if (trayIcon != null) trayIcon.Visible = false;
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
                trayIcon.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) BringBack(); };
            }
            // its words in the window's language as it is now
            var old = trayIcon.ContextMenuStrip;
            var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
            var open = new Forms.ToolStripMenuItem(Texts.Get("tray_open"));
            open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
            open.Click += (s, e) => BringBack();
            var exit = new Forms.ToolStripMenuItem(Texts.Get("tray_exit"));
            exit.Click += (s, e) => ExitForGood();
            menu.Items.Add(open);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exit);
            trayIcon.ContextMenuStrip = menu;
            old?.Dispose();
            trayIcon.Visible = true;
        }

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
            trayIcon.ContextMenuStrip?.Dispose();
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
