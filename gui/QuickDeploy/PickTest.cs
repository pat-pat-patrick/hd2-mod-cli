using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Hd2Mod;

namespace QuickDeploy
{
    public partial class MainWindow
    {
        /// <summary>HD2QD_CLICK_TEST=addzips (tests only), with HD2QD_ADD_ZIPS = archive paths split
        /// by '|': 4 s after the start, "add mod > from archives" as its menu item runs it. While
        /// the system's window is open, its title is read and every path is typed into its file
        /// name box as a quoted list, then its Open is pressed by a message (no mouse, no
        /// keyboard, no clipboard). A window that takes one file refuses such a list. Then the
        /// mods the library gained are written to HD2QD_WHEEL_OUT. addfolders: the same with
        /// "add mod > from a folder" (issue 13), the window opened in the folders' parent; the
        /// folder window takes no typed list, so the test outside picks them there (UI Automation).</summary>
        void PickTest()
        {
            // addfolders: the same through "add mod > from a folder" (issue 13)
            var mode = Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST");
            if (mode != "addzips" && mode != "addfolders") return;
            var zips = (Environment.GetEnvironmentVariable("HD2QD_ADD_ZIPS") ?? "").Split('|').Where(x => x.Length > 0).ToList();
            var outPath = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
            var lines = new List<string>();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += async (s, e) =>
            {
                t.Stop();
                var before = new HashSet<string>(Library.Load().Mods.Select(m => m.Id));
                uint ui = GetCurrentThreadId();
                var filler = Task.Run(() =>
                {
                    // the folder window takes no typed list: the test outside picks the folders in it
                    if (mode == "addfolders") return;
                    for (int i = 0; i < 100; i++)
                    {
                        Thread.Sleep(100);
                        IntPtr dlg = IntPtr.Zero;
                        EnumThreadWindows(ui, (h, l) =>
                        {
                            var c = new StringBuilder(64);
                            GetClassName(h, c, 64);
                            if (c.ToString() == "#32770" && IsWindowVisible(h)) { dlg = h; return false; }
                            return true;
                        }, IntPtr.Zero);
                        if (dlg == IntPtr.Zero) continue;
                        var title = new StringBuilder(256);
                        GetWindowText(dlg, title, 256);
                        lines.Add("window: " + title);
                        var box = FindWindowEx(FindWindowEx(FindWindowEx(dlg, IntPtr.Zero, "ComboBoxEx32", null), IntPtr.Zero, "ComboBox", null), IntPtr.Zero, "Edit", null);
                        if (box == IntPtr.Zero) { lines.Add("no file name box"); return; }
                        Thread.Sleep(300);
                        SendMessage(box, 0x000C /* WM_SETTEXT */, IntPtr.Zero, string.Join(" ", zips.Select(z => "\"" + z + "\"")));
                        PostMessage(dlg, 0x0111 /* WM_COMMAND */, (IntPtr)1 /* IDOK */, IntPtr.Zero);
                        return;
                    }
                    lines.Add("no window");
                });
                // what the menu item runs
                var p = mode == "addfolders"
                    ? Pick.Folders(this, Texts.Get("pick_mods_title"), System.IO.Path.GetDirectoryName(zips[0].TrimEnd('\\')))?.ToList()
                    : PickZips();
                await filler;
                lines.Add("chosen: " + (p == null ? "none" : p.Count + ": " + string.Join(" | ", p.Select(System.IO.Path.GetFileName))));
                if (p != null) await DropPaths(p);
                var added = Library.Load().Mods.Where(m => !before.Contains(m.Id)).ToList();
                lines.Add("added " + added.Count + ": " + string.Join(" | ", added.Select(m => m.Name)));
                lines.Add("foot: " + lastDone);
                System.IO.File.WriteAllText(outPath, string.Join("\r\n", lines) + "\r\n");
            };
            t.Start();
        }

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, EnumProc proc, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    }
}
