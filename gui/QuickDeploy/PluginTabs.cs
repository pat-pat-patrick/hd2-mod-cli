using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace QuickDeploy
{
    // Window plugins as tabs of this window, not windows of their own (the user, 2026-11-26:
    // players opened three to five plugin windows, which piled up). The tabs are a browser's: a
    // row at the window's top, the manager the first tab and each open plugin one more, shown
    // only while a plugin is open; the window's caption buttons move into it. A plugin's tab
    // takes the whole window under the row, the manager's title bar and footer hidden. A plugin
    // asks for one through host["tab"]; a plugin that does not know it still opens host["window"].
    public partial class MainWindow
    {
        sealed class PluginTab
        {
            public string Key;                  // its page's name for ShowPage: "tab:1", "tab:2", ...
            public string Title;
            public FrameworkElement Content;    // the plugin's own
            public Border Page;                 // the content, in TabArea
            public Action Closed;               // the plugin's, once the tab is closed
            public ImageSource Icon;            // the plugin's own (PluginIconFrame), else null
            public string File;                 // the plugin's file (HD2QD.*.dll), else null: what ReopenTabs opens
        }

        readonly List<PluginTab> tabs = new List<PluginTab>();
        int tabCount;
        // the widest a plugin's page is drawn: the widest of the plugins' own windows (the
        // settings window's 1180), its margins inside
        const double TabPageWidth = 1200;
        // the manager's page shown last: its tab and a tab closed with none left go back to it
        string managerPage = "deploy";
        // where the strip last put the caption buttons, and the drag height it set
        bool captionInStrip;
        double captionHeight = 72;           // MainWindow.xaml's

        PluginTab TabOf(string page) => tabs.FirstOrDefault(t => t.Key == page);

        /// <summary>The strip shown while a plugin is open, the caption buttons in it; the
        /// manager's tab shown or one plugin's; and the part of the window's top that drags it:
        /// the strip, with the manager's title bar under it while that is on show.</summary>
        void FitTabs()
        {
            bool strip = tabs.Count > 0;
            bool manager = TabOf(shownPage) == null;
            TabStrip.Visibility = strip ? Visibility.Visible : Visibility.Collapsed;
            ManagerView.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
            TabArea.Visibility = manager ? Visibility.Collapsed : Visibility.Visible;
            if (strip != captionInStrip)
            {
                captionInStrip = strip;
                var from = strip ? HeadDock : StripDock;
                var to = strip ? StripDock : HeadDock;
                // docked right in this order, before the rest: the close button the rightmost,
                // and the tabs never push them out of the window
                var caption = new UIElement[] { CloseBtn, MaxBtn, MinBtn };
                foreach (var b in caption) from.Children.Remove(b);
                for (int i = 0; i < caption.Length; i++) to.Children.Insert(i, caption[i]);
                UpdateLayout();
                FitHeader();
            }
            double h = (strip ? 40 : 0) + (manager ? 72 : 0);
            if (Math.Abs(h - captionHeight) > 0.5)
            {
                captionHeight = h;
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
                if (chrome != null)
                {
                    if (chrome.IsFrozen) { chrome = (System.Windows.Shell.WindowChrome)chrome.Clone(); System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome); }
                    chrome.CaptionHeight = h;
                }
            }
        }

        /// <summary>host["tab"](title, content, closed): the content as a tab of this window,
        /// shown at once; closed is called once the tab is closed (its X, Ctrl+W, the program
        /// ending). The same content again shows its tab. Returns "show" and "close", the two
        /// things the plugin can do with it.</summary>
        Func<string, FrameworkElement, Action, Dictionary<string, object>> TabMaker() => (title, content, closed) =>
        {
            var t = tabs.FirstOrDefault(x => x.Content == content);
            if (t == null)
            {
                var (file, type) = PluginOf(closed);
                t = new PluginTab { Key = "tab:" + (++tabCount), Title = title, Content = content, Closed = closed, File = file,
                                    Icon = type == null ? null : PluginIconFrame(type) };
                // a column in the middle as wide as the plugins' own windows were (980 to 1180),
                // not the whole window (the user, 2026-11-28: "太分散到两边了")
                t.Page = new Border { Child = content, Visibility = Visibility.Collapsed, MaxWidth = TabPageWidth };
                TabArea.Children.Add(t.Page);
                tabs.Add(t);
            }
            ShowTab(t);
            var mine = t;
            return new Dictionary<string, object>
            {
                ["show"] = (Action)(() => { if (tabs.Contains(mine)) ShowTab(mine); }),
                ["close"] = (Action)(() => CloseTab(mine)),
            };
        };

        /// <summary>The plugin that asked for a tab, its file and class: the plugin whose assembly
        /// wrote its closed (host["tab"] does not say who asks: a plugin opens it from its Open,
        /// or from its launch target, each with a host of its own). (null, null) when none.</summary>
        static (string File, Type Type) PluginOf(Action closed)
        {
            var asm = closed?.Method.DeclaringType?.Assembly;
            if (asm == null) return (null, null);
            var p = Hd2Mod.PluginFiles.All().FirstOrDefault(x => x.Type != null && x.Type.Assembly == asm);
            return (p.File, p.Type);
        }

        // while ReopenTabs opens the tabs of the last start: the window is not brought forward
        // (a start in the notification area stays there)
        bool reopening;

        /// <summary>Whether the plugin tabs open when the program ended open again at the next
        /// start (a setting; the user, 2026-11-26). Off at first: a plugin opened at the start
        /// costs the start its reading.</summary>
        static bool ReopenTabsOn
        {
            get
            {
                try { return Hd2Mod.Json.Bool(Hd2Mod.Store.Load("gui.json"), "reopen_tabs", false); }
                catch (Exception e) when (e is System.IO.IOException || e is FormatException || e is UnauthorizedAccessException) { return false; }
            }
            set
            {
                var d = Hd2Mod.Store.Load("gui.json");
                d["reopen_tabs"] = value;
                Hd2Mod.Store.Save("gui.json", d);
            }
        }

        /// <summary>The open tabs' plugins, in order, and the one on show ("" for the manager's),
        /// kept in gui.json as the program ends, whether the setting is on or not: turned on
        /// later, it has them.</summary>
        void SaveTabs()
        {
            try
            {
                var d = Hd2Mod.Store.Load("gui.json");
                d["open_tabs"] = tabs.Where(t => t.File != null).Select(t => (object)t.File).ToList();
                d["open_tab_shown"] = TabOf(shownPage)?.File ?? "";
                Hd2Mod.Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is System.IO.IOException || e is FormatException || e is UnauthorizedAccessException)
            {
                Hd2Mod.ManagerLog.Warn("plugin tabs: not kept: " + e.Message);
            }
        }

        /// <summary>At the start, with the setting on: the plugins whose tabs were open when the
        /// program ended, opened again in their order, and the tab that was on show shown. A
        /// plugin since removed, or no game folder (a plugin needs one), opens nothing.</summary>
        void ReopenTabs()
        {
            if (!ReopenTabsOn || Hd2Mod.Store.GameDir(null, false) == null) return;
            List<string> files;
            string shown;
            try
            {
                var d = Hd2Mod.Store.Load("gui.json");
                files = Hd2Mod.Json.List(d, "open_tabs").OfType<string>().ToList();
                shown = d.TryGetValue("open_tab_shown", out var s) ? s as string : null;
            }
            catch (Exception e) when (e is System.IO.IOException || e is FormatException || e is UnauthorizedAccessException) { return; }
            if (files.Count == 0) return;
            var all = Hd2Mod.PluginFiles.All();
            var page = shownPage;
            reopening = true;
            try
            {
                foreach (var file in files)
                {
                    var type = all.FirstOrDefault(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase)).Type;
                    if (type == null || Hd2Mod.PluginFiles.Kind(type) != "window") continue;
                    try { Hd2Mod.PluginFiles.Call(type, "Open", this, PluginHostHere()); }
                    catch (Exception x) when (!(x is OutOfMemoryException)) { Hd2Mod.ManagerLog.Warn("plugin tabs: " + file + ": " + (x.InnerException ?? x).Message); }
                }
            }
            finally { reopening = false; }
            var on = tabs.FirstOrDefault(t => !string.IsNullOrEmpty(shown) && string.Equals(t.File, shown, StringComparison.OrdinalIgnoreCase));
            ShowPage(on != null ? on.Key : page);
        }

        void ShowTab(PluginTab t)
        {
            if (reopening) { ShowPage(t.Key); return; }
            if (inTray) BringBack();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            ShowPage(t.Key);
        }

        void CloseTab(PluginTab t)
        {
            int i = tabs.IndexOf(t);
            if (i < 0) return;
            tabs.RemoveAt(i);
            TabArea.Children.Remove(t.Page);
            t.Page.Child = null;
            if (pageBack == t.Key) pageBack = "deploy";
            // the tab on show: the one beside it, else the manager's page it came from
            if (shownPage == t.Key)
                ShowPage(tabs.Count > 0 ? tabs[Math.Min(i, tabs.Count - 1)].Key : managerPage);
            else { FitTabs(); PaintTabs(); }
            try { t.Closed?.Invoke(); }
            catch (Exception e) when (!(e is OutOfMemoryException)) { Hd2Mod.ManagerLog.Warn("plugin tab " + t.Title + ": closed: " + e.Message); }
        }

        /// <summary>Every tab closed, as the program ends: each plugin finishes what it holds.</summary>
        void CloseTabs()
        {
            foreach (var t in tabs.ToList())
                try { t.Closed?.Invoke(); }
                catch (Exception e) when (!(e is OutOfMemoryException)) { Hd2Mod.ManagerLog.Warn("plugin tab " + t.Title + ": closed: " + e.Message); }
            tabs.Clear();
        }

        /// <summary>The row of tabs: the manager's, then one per open plugin, the one on show
        /// lit; drawn while a plugin is open.</summary>
        void PaintTabs()
        {
            // drawn again only when the row changes: ShowPage calls this on every page, the
            // manager's own pages included
            var row = string.Join("|", tabs.Select(x => x.Key + "=" + x.Title)) + "#" + (TabOf(shownPage)?.Key ?? "") + "#" + Texts.Lang;
            if (row == paintedRow) return;
            paintedRow = row;
            TabRow.Children.Clear();
            if (tabs.Count == 0) return;
            bool home = TabOf(shownPage) == null;
            // the manager's own: its mark and name, no close (the program's own is the window's)
            var mark = new Image
            {
                Source = Mark20, Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
            var homeRow = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(mark, Dock.Left);
            homeRow.Children.Add(mark);
            var homeText = TabText(Texts.Get("tab_manager"), home);
            homeRow.Children.Add(homeText);
            var homeHead = TabHead(homeRow, home, new Thickness(16, 0, 16, 0));
            homeHead.Tag = new HeadParts { On = home, Pad = homeHead.Padding, Text = homeText };
            homeHead.MouseLeftButtonUp += (s, e) => ShowPage(managerPage);
            TabRow.Children.Add(homeHead);
            foreach (var t in tabs)
            {
                bool on = shownPage == t.Key;
                var line = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
                var x = new Button
                {
                    Style = (Style)FindResource("Caption"), Width = 28, Height = 28, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(0),
                    ToolTip = Texts.Get("tab_close"), Cursor = Cursors.Hand, Focusable = false,
                    Content = new Path { Data = Geometry.Parse("M0,0 L8,8 M8,0 L0,8"), StrokeThickness = 1.2, Stroke = B(on ? "Ink" : "Muted") },
                };
                var mine = t;
                x.Click += (s, e) => { CloseTab(mine); e.Handled = true; };
                DockPanel.SetDock(x, Dock.Right);
                line.Children.Add(x);
                // its own icon as on its pinned button, else the plugins button's glyph
                UIElement icon = t.Icon != null
                    ? new Image { Source = t.Icon, Width = 20, Height = 20 }
                    : (UIElement)new TextBlock { Text = "\uEA86", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Foreground = B("Muted"), Width = 20, TextAlignment = TextAlignment.Center };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                ((FrameworkElement)icon).Margin = new Thickness(0, 0, 8, 0);
                ((FrameworkElement)icon).VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(icon, Dock.Left);
                line.Children.Add(icon);
                var text = TabText(t.Title, on);
                line.Children.Add(text);
                var head = TabHead(line, on, new Thickness(16, 0, 6, 0));
                head.Tag = new HeadParts { On = on, Pad = head.Padding, Text = text, Close = x, Icon = icon };
                head.MouseLeftButtonUp += (s, e) => ShowTab(mine);
                // the middle button closes it, as a browser's tab
                head.MouseUp += (s, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(mine); };
                TabRow.Children.Add(head);
            }
            if (!stripWatched)
            {
                StripDock.SizeChanged += (s, e) => FitTabWidths();
                stripWatched = true;
            }
            FitTabWidths();
        }

        // every tab as wide as a browser's, whatever its title (the user, 2026-11-26: the manager's
        // tab, the one used most, was as narrow as its word); narrower, all alike, only when they
        // no longer fit. Below TabFull the tabs not on show lose their close button (Ctrl+W and
        // the middle button still close them), below TabIconOnly their titles too, as a
        // browser's: never cut off at the window's edge, where they could not be reached
        const double TabWidth = 240, TabFull = 112, TabIconOnly = 72, TabLeast = 44;
        // the strip's room the tabs leave free, to take the window by
        const double DragRoom = 96;
        bool stripWatched;
        // what PaintTabs last drew: the tabs, the one on show and the language
        string paintedRow;

        // the parts of a tab's head that FitTabWidths changes with its width
        sealed class HeadParts
        {
            public bool On;
            public Thickness Pad;
            public TextBlock Text;
            public Button Close;        // null on the manager's
            public UIElement Icon;      // null on the manager's
        }

        static ImageSource mark20;
        // the window's mark on the manager's tab, decoded once
        static ImageSource Mark20
        {
            get
            {
                if (mark20 == null)
                {
                    var b = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png"));
                    b.Freeze();
                    mark20 = b;
                }
                return mark20;
            }
        }

        void FitTabWidths()
        {
            int n = TabRow.Children.Count;
            if (n == 0) return;
            // the caption buttons are 46 wide each (App.xaml's Caption)
            double room = StripDock.ActualWidth - TabRow.Margin.Left - 3 * 46 - DragRoom;
            double w = room <= 0 ? TabWidth : Math.Max(TabLeast, Math.Min(TabWidth, Math.Floor(room / n)));
            foreach (FrameworkElement head in TabRow.Children)
            {
                head.Width = w;
                if (!(head.Tag is HeadParts p) || !(head is Border b)) continue;
                bool close = p.Close != null && (w >= TabFull || (p.On && w >= TabIconOnly));
                if (p.Close != null) p.Close.Visibility = close ? Visibility.Visible : Visibility.Collapsed;
                p.Text.Visibility = w >= TabIconOnly ? Visibility.Visible : Visibility.Collapsed;
                b.ToolTip = w >= TabIconOnly ? null : p.Text.Text;      // the title, when only the icon shows
                // the tab on show, narrow, keeps its title and its close button, not its icon
                if (p.Icon != null) p.Icon.Visibility = p.On && close && w < TabFull ? Visibility.Collapsed : Visibility.Visible;
                b.Padding = w >= TabFull ? p.Pad : new Thickness(w >= TabIconOnly ? 10 : Math.Max(0, Math.Floor((w - 20) / 2)), 0, close ? 4 : 8, 0);
            }
        }

        static TextBlock TabText(string text, bool on) => new TextBlock
        {
            Text = text, FontSize = 13, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = B(on ? "Ink" : "Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };

        /// <summary>One tab's head: the one on show in the title bar's ground, a yellow line on
        /// top and joined to the page under it; the others on the strip's, a line between them.
        /// The strip drags the window, so a head takes the mouse itself.</summary>
        static Border TabHead(UIElement child, bool on, Thickness padding)
        {
            var head = new Border
            {
                Child = child, Padding = padding, Cursor = Cursors.Hand,
                Background = on ? B("Ground700") : Brushes.Transparent,
                BorderBrush = on ? B("Yellow") : B("Grid"), BorderThickness = on ? new Thickness(0, 2, 0, 0) : new Thickness(0, 0, 1, 0),
                Margin = on ? new Thickness(0, 0, 0, -1) : new Thickness(0),
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(head, true);
            return head;
        }

        /// <summary>Ctrl+W closes the tab on show; Ctrl+Tab and Ctrl+Shift+Tab go through the
        /// tabs, the manager's page counted as the first. On KeyDown, as it comes back up: a
        /// control inside a plugin that uses the key itself (its own tabs) has it first.</summary>
        void TabKeys(object sender, KeyEventArgs e)
        {
            if (e.Handled || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || tabs.Count == 0) return;
            if (e.Key == Key.W && TabOf(shownPage) is PluginTab t) { CloseTab(t); e.Handled = true; }
            else if (e.Key == Key.Tab)
            {
                var all = new List<string> { managerPage };
                all.AddRange(tabs.Select(x => x.Key));
                int i = all.IndexOf(shownPage);
                if (i < 0) i = 0;
                int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
                var next = all[(i + step + all.Count) % all.Count];
                if (TabOf(next) is PluginTab n) ShowTab(n); else ShowPage(next);
                e.Handled = true;
            }
        }
    }
}
