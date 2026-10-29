using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hd2Mod;
using IOPath = System.IO.Path;
using Path = System.Windows.Shapes.Path;

namespace QuickDeploy
{
    /// <summary>The design board's Manager (gui\design\Manager.dc.html), in HD2 Mod
    /// Setting GUI's colours: everything shown comes from `hd2qd state`, every
    /// change is an hd2qd command, run in this process.</summary>
    public partial class MainWindow : Window
    {
        State st;
        string selectedId, filter = "all";
        readonly HashSet<string> open = new HashSet<string>();
        // more than one mod taken at once, with Ctrl or Shift (the user, 2026-10-24); the one
        // the details show is selectedId, which is also where a Shift range starts
        readonly HashSet<string> picked = new HashSet<string>();
        bool busy, overMax, acrylic, descOpen, checking;
        string lastDone;
        /// <summary>The loader panel's i button: it opens the details (LoaderInfo).</summary>
        FrameworkElement loaderInfoBtn;
        /// <summary>The details, while open: the left column is drawn anew on every state, and the popup
        /// follows the new i button (it jumped to the column's corner when its button left: the user, 2026-10-26).</summary>
        System.Windows.Controls.Primitives.Popup loaderPop;
        // the last update check, by mod id (kept while the window is open)
        readonly Dictionary<string, UpdateInfo> updates = new Dictionary<string, UpdateInfo>();
        string startWith = Arg("--select");
        readonly string startSearch = Arg("--search");
        string startPage = Arg("--page");

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
        FileSystemWatcher watcher;
        readonly DispatcherTimer settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        DateTime quietUntil = DateTime.MinValue;
        Point dragStart;
        ModRow dragFrom;
        Border dropRow;

        // a line drawing for a mod or an option without a picture (the board's sketch icons)
        const string GlyphMod = "M3 5h18v14H3z M7 9h10 M7 13h6";
        // the row's update mark, in its three states: check this mod (an arrow coming round
        // again), a newer version to install (down), nothing to do (a tick)
        const string GlyphCheck = "M20.5 12a8.5 8.5 0 1 1-2.6-6.1 M20.5 3.5v4.2h-4.2";
        const string GlyphDown = "M12 3.5 V18.5 M5.5 12 L12 18.5 L18.5 12";
        const string GlyphTick = "M4.5 12.5 L9.75 18 L19.5 6.5";
        const string GlyphPart = "M12 3v11 M7 9l5 5 5-5 M4 20h16";
        const string GlyphTop = "M5 4h14 M12 20V9 M7 14l5-5 5 5";
        const string GlyphBottom = "M5 20h14 M12 4v11 M7 10l5 5 5-5";

        /// <summary>Whether this build has the download page.</summary>
#if !HAS_NEXUS
        const bool HasNexus = false;
#else
        const bool HasNexus = true;
#endif

        static Brush B(string key) => (Brush)Application.Current.Resources[key];
        FontFamily Mono => (FontFamily)FindResource("Mono");

        public MainWindow()
        {
            Mark("window made");
            InitializeComponent();
            Mark("window laid out");
            Words();
            if (startSearch != null) SearchBox.Text = startSearch;
            StateChanged += (s, e) => FitMaximized();
            PreviewMouseWheel += Wheel;
            settle.Tick += (s, e) =>
            {
                if (dragRow != null) return;            // a row in hand: the list stays as it is
                if (busy || DateTime.Now <= quietUntil) return;   // not now: the timer tries again
                settle.Stop();
                Refresh();
            };
            // a row dragged to another place follows the pointer until the button is let go
            ListArea.PreviewMouseMove += (s, e) => { if (dragRow != null) { DragAt(e.GetPosition(ListArea)); e.Handled = true; } };
            ListArea.PreviewMouseLeftButtonUp += (s, e) => { if (dragRow != null) { EndDrag(true); e.Handled = true; } };
            // the switch under the pointer loses the mouse to the list when a drag starts, and
            // that bubbles here too: only the list's own loss ends the drag
            ListArea.LostMouseCapture += (s, e) => { if (!ReferenceEquals(Mouse.Captured, ListArea)) EndDrag(false); };
            PreviewKeyDown += (s, e) => { if (dragRow != null && e.Key == Key.Escape) { EndDrag(false); e.Handled = true; } };
            // the loader panel's details close like a menu (LoaderInfo)
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && loaderPop != null && loaderPop.IsOpen) { CloseLoaderInfo(); e.Handled = true; } };
            // Esc leaves the settings page for the page it was opened from
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && !e.Handled && SettingsPage.Visibility == Visibility.Visible) { ShowPage(pageBack); e.Handled = true; } };
            PreviewTextInput += TypeToSearch;
            PreviewMouseDown += (s, e) =>
            {
                pressClosedInfo = loaderPop != null && loaderPop.IsOpen;
                CloseLoaderInfo();
            };
            Deactivated += (s, e) => CloseLoaderInfo();
            LocationChanged += (s, e) => CloseLoaderInfo();
            SizeChanged += (s, e) => CloseLoaderInfo();
            edgeScroll.Tick += (s, e) =>
            {
                if (dragRow == null) { edgeScroll.Stop(); return; }
                ListScroll.ScrollToVerticalOffset(ListScroll.VerticalOffset + edgeBy);
                DragAt(Mouse.GetPosition(ListArea));
            };
            Loaded += (s, e) => Start();
        }

        /// <summary>The window's fixed words, in its language.</summary>
        void Words()
        {
            TitleText.Text = Texts.Get("title");
            StatLinksLabel.Text = Texts.Get("stat_links");
            AddBtn.Content = Texts.Get("add");
            UpdatesBtn.Content = Texts.Get(checking ? "up_checking_btn" : "up_check");
            RefreshBtn.Content = Texts.Get("refresh");
            DeployBtn.Content = Texts.Get("deploy");
            LaunchBtn.Content = Texts.Get("launch");
            ListHint.Text = Texts.Get(groupView ? "g_hint" : Texts.TopWins ? "list_hint_top" : "list_hint");
            ViewGroups.Content = Texts.Get("view_groups");
            ViewOrder.Content = Texts.Get("view_order");
            PageDeploy.Content = Texts.Get("page_deploy");
            PageDownload.Content = Texts.Get("page_download");
            PageStorage.Content = Texts.Get("page_storage");
            PageTags.Content = Texts.Get("page_tags");
            PageDownload.Visibility = HasNexus ? Visibility.Visible : Visibility.Collapsed;
            SearchHint.Text = Texts.Get("search_hint");
            SettingsBtn.ToolTip = Texts.Get("settings");
            FilterWords();
            ListHint.Visibility = Texts.Hints ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The whole window in the other language at once, kept for the next start.</summary>
        void Language(bool zh)
        {
            if (zh == Texts.Zh) { Words(); return; }
            Texts.Choose(zh);
            Words();
            rows.Clear();                       // every row's words, built again
            List.Children.Clear();
            lastDone = null;
            if (st != null) Render();
#if HAS_NEXUS
            if (DownloadPage.Visibility == Visibility.Visible) RenderDownloads();
#endif
            if (StoragePage.Visibility == Visibility.Visible) RenderStorage();
            if (SettingsPage.Visibility == Visibility.Visible) RenderSettings();
        }

        // ---------------------------------------------------------------- start, state, commands

        /// <summary>HD2QD_TIMING=file (measuring only): the milliseconds since the process started
        /// at each step of the start, so a slow start can be taken apart.</summary>
        static readonly string timing = Environment.GetEnvironmentVariable("HD2QD_TIMING");

        static void Mark(string step)
        {
            if (timing == null) return;
            var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            try { File.AppendAllText(timing, string.Format("{0,6:0} ms  {1}\r\n", ms, step)); } catch (IOException) { }
        }

        void Start()
        {
            Mark("window loaded");
            LoadView();
            HookGroupDrag();
            LuaMods.Lazy = true;                       // the list first; the mods' scripts after (WarmLua)
            Watch();
            LoadFilters();
            ShowSaved();
            Refresh();
            ClickTest();
            GroupTest();
            PreviewTest();
            DragTest();
            Listen();
            SelfUpdateAtStart();
#if HAS_NEXUS
            if (App.StartLink != null) Dispatcher.BeginInvoke(new Action(() => Arrived(App.StartLink)), DispatcherPriority.ApplicationIdle);
#endif
        }

        /// <summary>The red dot on the settings icon, from the last check of this program's own
        /// releases: shown while that version is newer than the one running.</summary>
        void ShowUpdateDot()
        {
            var u = SelfUpdate.Last();
            UpdateDot.Visibility = u.Newer ? Visibility.Visible : Visibility.Collapsed;
            SettingsBtn.ToolTip = u.Newer ? Texts.F("self_tip", u.Latest) : null;
        }

        /// <summary>The last result at once, then, when the automatic check is on and due (once a
        /// day), GitHub asked in the background. A check that fails says nothing here: the next
        /// start tries again, and Settings says why.</summary>
        async void SelfUpdateAtStart()
        {
            ShowUpdateDot();
            if (!SelfUpdate.Due()) return;
            try { await Task.Run(() => SelfUpdate.Check()); }
            catch (Exception e) when (e is ModError || e is IOException || e is UnauthorizedAccessException || e is TaskCanceledException) { return; }
            ShowUpdateDot();
        }

        // the switch of each row on screen, for the click test
        readonly Dictionary<string, CheckBox> switches = new Dictionary<string, CheckBox>();

        /// <summary>HD2QD_CLICK_TEST=N (tests only): 3 s after the start, the switch of mod
        /// N is clicked the way a click does it, so a test outside can time what follows.</summary>
        void ClickTest()
        {
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "storage")
            {
                // "storage": the mod folders with a couple of mods ticked, for their picture
                var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t2.Tick += (s, e) =>
                {
                    t2.Stop();
                    ShowPage("storage");
                    foreach (var m in (st?.Mods ?? new List<ModRow>()).Take(2)) ticked.Add(m.Id);
                    RenderStorage();
                };
                t2.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "old")
            {
                // "old": the storage page scrolled to the folders of old versions, two ticked
                var t3 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t3.Tick += (s, e) =>
                {
                    t3.Stop();
                    ShowPage("storage");
                    foreach (var l in (st?.Leftovers ?? new List<Leftover>()).Take(2)) oldPick.Add(l.Path);
                    RenderStorage();
                    Dispatcher.BeginInvoke(new Action(() => StoragePage.ScrollToEnd()), DispatcherPriority.Loaded);
                };
                t3.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "sync")
            {
                // "sync": writes what the window is showing, once a second, so a test outside can
                // watch a change made on the command line arrive here
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var t8 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                t8.Tick += (s, e) =>
                {
                    if (path == null || st == null) return;
                    try
                    {
                        File.WriteAllText(path, "mods=" + st.Mods.Count +
                                                "\r\non=" + st.Mods.Count(m => m.Enabled) +
                                                "\r\nfirst=" + (st.Mods.Count > 0 ? st.Mods[0].Name + "|" + st.Mods[0].Enabled : "-") +
                                                "\r\nhome=" + Store.Home + "\r\n");
                    }
                    catch (IOException) { }
                };
                t8.Start();
                // and at twelve seconds it turns the first mod the other way, the way a click
                // does, so the test can look for that in the library file afterwards
                var t9 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                t9.Tick += (s, e) =>
                {
                    t9.Stop();
                    var m = st?.Mods.FirstOrDefault();
                    if (m == null) return;
                    Change(m.Id, c => c.Enabled = !c.Enabled, m.Enabled ? "disable" : "enable", m.Id);
                };
                t9.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "typesearch")
            {
                // "typesearch": a row of the list focused, then "12" and "a" typed as text arrives
                // from the keyboard; a lone space after; what the search holds goes to HD2QD_WHEEL_OUT
                var t17 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t17.Tick += (s, e) =>
                {
                    t17.Stop();
                    var row = List.Children.Count > 2 ? List.Children[2] as UIElement : List;
                    row.Focusable = true;
                    row.Focus();
                    var before = Keyboard.FocusedElement?.GetType().Name;
                    // a lone space first: it must stay where it is
                    TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, Keyboard.FocusedElement, " "));
                    var afterSpace = "[" + SearchBox.Text + "] focused " + SearchBox.IsKeyboardFocused;
                    foreach (var text in new[] { "12", "a", " " })
                        TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, Keyboard.FocusedElement, text));
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path != null)
                        File.WriteAllText(path, "focus before " + before + "\nafter a lone space " + afterSpace + "\nsearch [" + SearchBox.Text + "]\nfocused " + SearchBox.IsKeyboardFocused +
                                          "\ncaret " + SearchBox.CaretIndex + "\n");
                };
                t17.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "fling")
            {
                // "fling": six turns down 40 ms apart on a row of the list, as a fast wheel does; then
                // whether the button shows, and where, go to HD2QD_WHEEL_OUT
                var t16 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t16.Tick += (s, e) =>
                {
                    t16.Stop();
                    if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                    Height = 640;           // a short library still has more than a page to jump over
                    UpdateLayout();
                    ListScroll.ScrollToTop();
                    UpdateLayout();
                    var row = List.Children.Count > 2 ? List.Children[2] as UIElement : List;
                    int n = 0, at = Environment.TickCount;
                    var turn = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                    turn.Tick += (s2, e2) =>
                    {
                        row.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, at + 40 * n, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                        if (++n < 6) return;
                        turn.Stop();
                        // then two turns on the button itself: the list still moves (the user, 2026-10-27).
                        // A library too short to call it up gets it shown here, for that turn alone
                        bool came = flingBtn != null;
                        if (!came) { ListScroll.ScrollToTop(); UpdateLayout(); ShowFling(-1, new Point(200, 120)); UpdateLayout(); }
                        double before = ListScroll.VerticalOffset;
                        var onBtn = (flingBtn?.Child as Panel)?.Children.OfType<TextBlock>().FirstOrDefault();
                        for (int k = 0; k < 2 && onBtn != null; k++)
                            onBtn.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, at + 40 * (n + k), -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                        UpdateLayout();
                        var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                        if (path != null)
                            File.WriteAllText(path, "shown " + came + "\ndir " + flingDir +
                                              "\nat " + (flingBtn == null ? "-" : Canvas.GetLeft(flingBtn) + "," + Canvas.GetTop(flingBtn)) +
                                              "\noffset " + before + "\nafter_button " + ListScroll.VerticalOffset + "\n");
                    };
                    turn.Start();
                };
                t16.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "loadercopy")
            {
                // "loadercopy": the details open, a line in them selected and its copy menu opened, as a
                // right click does; whether the details stay open goes to HD2QD_WHEEL_OUT
                var t14 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t14.Tick += (s, e) =>
                {
                    t14.Stop();
                    if (loaderInfoBtn == null) return;
                    LoaderInfo(loaderInfoBtn);
                    var boxes = new List<TextBox>();
                    void Walk(DependencyObject d)
                    {
                        if (d is TextBox tb) boxes.Add(tb);
                        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
                    }
                    loaderPop.Child.UpdateLayout();
                    Walk(loaderPop.Child);
                    var line = boxes.Last();
                    line.Focus();
                    line.Select(0, 4);
                    line.ContextMenu.PlacementTarget = line;
                    line.ContextMenu.IsOpen = true;
                    var t15 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    t15.Tick += (s2, e2) =>
                    {
                        t15.Stop();
                        var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                        if (path != null)
                            File.WriteAllText(path, "lines " + boxes.Count + "\npopup " + loaderPop.IsOpen + "\nmenu " + line.ContextMenu.IsOpen +
                                                    "\nselected " + line.SelectedText + "\nfirst " + boxes[0].Text + "\n");
                    };
                    t15.Start();
                };
                t14.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "loaderswitch")
            {
                // "loaderswitch": the loader in effect chosen again, as a click on a ring does (the
                // choice does not change), for the picture of the switching line
                var t13 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t13.Tick += (s, e) => { t13.Stop(); ChooseLoader(st?.Mods.FirstOrDefault(x => x.Id == st.LoaderChoice)); };
                t13.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "loaderinfo")
            {
                // "loaderinfo": the loader panel's details open, for their picture
                var t12 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t12.Tick += (s, e) =>
                {
                    t12.Stop();
                    if (loaderInfoBtn != null) LoaderInfo(loaderInfoBtn);
                    var again = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    again.Tick += (s2, e2) => { again.Stop(); RenderNav(); };
                    again.Start();
                };
                t12.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "detail_end")
            {
                // "detail_end": the chosen mod's details scrolled to the bottom, for their picture
                var t11 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t11.Tick += (s, e) => { t11.Stop(); DetailScroll.ScrollToEnd(); };
                t11.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "copy")
            {
                // "copy": the chosen mod's copyable texts, copied the three ways a player does it
                // (all with Ctrl+C, the menu with nothing selected, the menu with a part selected),
                // and a turn of the mouse wheel over one, which must still scroll the details
                var t10 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                t10.Tick += (s, e) =>
                {
                    t10.Stop();
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path == null) return;
                    var found = new List<TextBox>();
                    void Walk(DependencyObject o)
                    {
                        if (o is TextBox tb && tb.Style == FindResource("Copyable")) found.Add(tb);
                        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++) Walk(VisualTreeHelper.GetChild(o, i));
                    }
                    Walk(Detail);
                    string Clip() { try { return Clipboard.GetText(); } catch (Exception) { return "?"; } }
                    void ByMenu(TextBox tb) { var item = (MenuItem)tb.ContextMenu.Items[0]; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
                    var lines = new List<string> { "found=" + found.Count };
                    foreach (var tb in found)
                    {
                        tb.SelectAll();
                        ApplicationCommands.Copy.Execute(null, tb);
                        string all = Clip();
                        tb.Select(0, 0);
                        ByMenu(tb);
                        string menu = Clip();
                        tb.Select(0, Math.Min(4, tb.Text.Length));
                        ByMenu(tb);
                        string part = Clip();
                        tb.Select(0, 0);
                        lines.Add("text=" + tb.Text.Replace("\n", "\\n") + "\r\n ctrl_c=" + (all == tb.Text) + " menu_all=" + (menu == tb.Text) + " menu_part=" + (part == tb.Text.Substring(0, Math.Min(4, tb.Text.Length))));
                    }
                    var target = found.LastOrDefault();
                    double before = DetailScroll.VerticalOffset;
                    if (target != null)
                        target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.MouseWheelEvent });
                    DetailScroll.UpdateLayout();
                    lines.Add("wheel_scrolled=" + (DetailScroll.VerticalOffset > before) + " (" + before + " -> " + DetailScroll.VerticalOffset + ")");
                    try { File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n"); } catch (IOException) { }
                };
                t10.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "where")
            {
                // "where": writes down where the list came back to, 5 s after the start
                var t7 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                t7.Tick += (s, e) =>
                {
                    t7.Stop();
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path == null) return;
                    try
                    {
                        File.WriteAllText(path, "offset=" + ListScroll.VerticalOffset +
                                                "\r\nheight=" + ListScroll.ScrollableHeight + "\r\n");
                    }
                    catch (IOException) { }
                    ListScroll.ScrollToVerticalOffset(2600);   // a different place, to see it saved
                };
                t7.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "wheel")
            {
                // "wheel": the tag filter menu opened and left open. A test outside turns the
                // wheel over the list; this writes down afterwards whether the menu closed and
                // where the list scrolled to.
                var t5 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t5.Tick += (s, e) =>
                {
                    t5.Stop();
                    PreviewMouseWheel += (s3, e3) => wheelSeen++;
                    // HD2QD_WHEEL_NOMENU: the same test without a menu at all, as a control
                    if (Environment.GetEnvironmentVariable("HD2QD_WHEEL_NOMENU") == null)
                        TagFilterBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, TagFilterBtn));
                    var t55 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    t55.Tick += (s4, e4) =>
                    {
                        t55.Stop();
                        var root = lastMenu == null ? null : PresentationSource.FromVisual(lastMenu) as HwndSource;
                        if (root?.RootVisual is UIElement rv)
                            rv.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                            {
                                RoutedEvent = UIElement.PreviewMouseWheelEvent,
                            });
                    };
                    t55.Start();
                    var t6 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    t6.Tick += (s2, e2) =>
                    {
                        t6.Stop();
                        var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                        if (path == null) return;
                        try
                        {
                            File.WriteAllText(path, "menu_open=" + (lastMenu != null && lastMenu.IsOpen) +
                                                    "\r\noffset=" + ListScroll.VerticalOffset +
                                                    "\r\nheight=" + ListScroll.ScrollableHeight +
                                                    "\r\nwheel_seen=" + wheelSeen +
                                                    "\r\nfocus_is_mine=" + (GetFocus() == new System.Windows.Interop.WindowInteropHelper(this).Handle) +
                                                    "\r\nforeground_is_mine=" + (GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(this).Handle) + "\r\n");
                        }
                        catch (IOException) { }
                    };
                    t6.Start();
                };
                t5.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "pick")
            {
                // "pick": three rows taken together, the third one the one shown
                var t4 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t4.Tick += (s, e) =>
                {
                    t4.Stop();
                    var three = (st?.Mods ?? new List<ModRow>()).Where(Shown).Take(3).ToList();
                    foreach (var x in three) picked.Add(x.Id);
                    if (three.Count > 0) selectedId = three[three.Count - 1].Id;
                    RenderList();
                    RenderDetail();
                };
                t4.Start();
                return;
            }
            // tests only: what a row or the details hold, walked through the logical tree
            IEnumerable<DependencyObject> All(DependencyObject root)
            {
                yield return root;
                foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                    foreach (var d in All(c)) yield return d;
            }
            IEnumerable<Button> FindButtons(DependencyObject root) => All(root).OfType<Button>();
            IEnumerable<string> Texts_(DependencyObject root) => All(root).OfType<TextBlock>().Select(t => t.Text);
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "checkone")
            {
                // "checkone": the update mark on the third row clicked while the first is shown;
                // writes down which mod the details show before and after
                var k = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                k.Tick += (s, e) =>
                {
                    k.Stop();
                    var shown = st.Mods.Where(Shown).ToList();
                    if (shown.Count < 3) return;
                    Current(shown[0].Id);
                    RenderList();
                    RenderDetail();
                    var before = selectedId == shown[0].Id ? "first" : selectedId;
                    var row = rows[shown[2].Id].Row;
                    var mark = FindButtons(row).FirstOrDefault(b => b.Content is Path p && p.Width == 18);
                    mark?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, mark));
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    var title = Detail.Children.OfType<FrameworkElement>().SelectMany(Texts_).FirstOrDefault(t => t == shown[2].Name);
                    if (path != null) File.WriteAllText(path, "before=" + before + "\r\nafter=" + (selectedId == shown[2].Id ? "third" : selectedId) +
                                                               "\r\nmark_found=" + (mark != null) + "\r\ndetails_name=" + (title ?? "-") + "\r\n");
                };
                k.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "tagmenu")
            {
                // "tagmenu": a label made from the first row's menu, then the second row's menu
                // opened the way a right click opens it. Writes down whether that menu has the new
                // label before it opens (the menu the row was drawn with) and once it has opened.
                var k = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                k.Tick += (s, e) =>
                {
                    k.Stop();
                    var a = st?.Mods.ElementAtOrDefault(0);
                    var b = st?.Mods.ElementAtOrDefault(1);
                    if (a == null || b == null) return;
                    const string label = "新建测试标签";
                    PutTag(new List<ModRow> { a }, label, true);
                    var k2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    k2.Tick += (s2, e2) =>
                    {
                        k2.Stop();
                        if (!rows.TryGetValue(b.Id, out var r)) return;
                        bool Has(ContextMenu cm) =>
                            cm != null && cm.Items.OfType<MenuItem>().Any(x => x.Items.OfType<MenuItem>().Any(y => (y.Header as string) == label));
                        bool before = Has(r.Row.ContextMenu);
                        // what a right click does first: the opening event, then the menu read off the row
                        var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                            new object[] { r.Row, true, 0.0, 0.0 }, null);
                        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
                        r.Row.RaiseEvent(args);
                        bool after = Has(r.Row.ContextMenu);
                        // and the mods taken together: three taken and drawn, then a fourth added,
                        // which draws only the fourth row again
                        var four = st.Mods.Where(Shown).Take(4).ToList();
                        picked.Clear();
                        foreach (var x in four.Take(3)) picked.Add(x.Id);
                        RenderList();
                        picked.Add(four[3].Id);
                        RenderList();
                        var r0 = rows[four[0].Id];
                        string Header(ContextMenu cm) => cm?.Items.OfType<MenuItem>().FirstOrDefault()?.Header as string ?? "";
                        var stale = Header(r0.Row.ContextMenu);
                        var args2 = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                            new object[] { r0.Row, true, 0.0, 0.0 }, null);
                        args2.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
                        r0.Row.RaiseEvent(args2);
                        var fresh = Header(r0.Row.ContextMenu);
                        picked.Clear();
                        RenderList();
                        var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                        if (path != null) File.WriteAllText(path, "before_open=" + before + "\r\nafter_open=" + after +
                                                                   "\r\nmany_before_open=" + stale + "\r\nmany_after_open=" + fresh + "\r\n");
                        r.Row.ContextMenu.PlacementTarget = r.Row;
                        r.Row.ContextMenu.IsOpen = true;
                        var sub = r.Row.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(x => x.Items.Count > 0);
                        if (sub != null) Dispatcher.BeginInvoke(new Action(() => sub.IsSubmenuOpen = true), DispatcherPriority.Loaded);
                    };
                    k2.Start();
                };
                k.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "manymenu")
            {
                // "manymenu": the first three rows taken together and their menu open, for its picture
                var k = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                k.Tick += (s, e) =>
                {
                    k.Stop();
                    var three = Seen().Take(3).ToList();
                    if (three.Count < 2) return;
                    foreach (var x in three) picked.Add(x.Id);
                    RenderList();
                    UpdateLayout();
                    if (!rows.TryGetValue(three[0].Id, out var r)) return;
                    r.Row.ContextMenu = Menu(three[0]);
                    r.Row.ContextMenu.PlacementTarget = r.Row;
                    r.Row.ContextMenu.IsOpen = true;
                };
                k.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "menu")
            {
                // "menu": a row's menu with the labels open under it, for its picture
                var k = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                k.Tick += (s, e) =>
                {
                    k.Stop();
                    var first = st?.Mods.FirstOrDefault();
                    if (first == null || !rows.TryGetValue(first.Id, out var r)) return;
                    r.Row.ContextMenu.PlacementTarget = r.Row;
                    r.Row.ContextMenu.IsOpen = true;
                    var sub = r.Row.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(x => x.Items.Count > 0);
                    if (sub != null) Dispatcher.BeginInvoke(new Action(() => sub.IsSubmenuOpen = true), DispatcherPriority.Loaded);
                };
                k.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "filter")
            {
                // "filter": one choice taken and the menu open, for its picture
                var f = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                f.Tick += (s, e) =>
                {
                    f.Stop();
                    fromFilter.Add("nexus");
                    kindFilter.Add("loader");
                    AfterFilter();
                    Filter_Click(FilterBtn, new RoutedEventArgs());
                };
                f.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "tags")
            {
                // "tags": the labels page drawn again and again, as clicking about does
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    ShowPage("tags");
                    tagHunt.Text = "x";
                    tagHunt.Text = "";
                    RenderTags();
                    RenderTags();
                    // a label taken, two mods ticked in the add box: the whole page at work
                    tagSel = st?.Tags.FirstOrDefault() ?? st?.Mods.SelectMany(m => m.Tags).FirstOrDefault();
                    tagAuto = false;
                    RenderTags();
                    if (tagSel != null)
                        foreach (var m in (st?.Mods ?? new List<ModRow>()).Where(x => !Carries(x, tagSel, false)).Take(2))
                            tagPick.Add(m.Id);
                    RenderTags();
                    Dispatcher.BeginInvoke(new Action(() => TagsPage.ScrollToEnd()), DispatcherPriority.Loaded);
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "profilemenu" || Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "libview")
            {
                // "profilemenu": the menu at the list's title left open; "libview": the mod library on show
                bool menu = Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "profilemenu";
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    if (menu) ProfileBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, ProfileBtn));
                    else
                    {
                        viewLibrary = true; RenderNav(); RenderList(); RenderDetail();
                        // where the profile's own mods end
                        Dispatcher.BeginInvoke(new Action(() => ListScroll.ScrollToVerticalOffset(4 * 73)), DispatcherPriority.Loaded);
                    }
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "dltime")
            {
                // "dltime": how long the download page takes to draw, written down
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    ShowPage("download");
                    UpdateLayout();
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    var said = "download page drawn in " + clock.ElapsedMilliseconds + " ms";
                    var list = Downloads.Children.OfType<Border>().Select(b => b.Child).OfType<ScrollViewer>().FirstOrDefault(v => v.MaxHeight == 470);
                    if (list != null)
                    {
                        int Rows() => ((Panel)list.Content).Children.Count;
                        said += "\nrows at first: " + Rows();
                        for (int i = 0; i < 30; i++) { list.ScrollToEnd(); UpdateLayout(); }
                        said += "\nrows after scrolling to the end: " + Rows();
                    }
                    if (path != null) try { File.WriteAllText(path, said); } catch (IOException) { }
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "menutoggle")
            {
                // "menutoggle": the labels menu opened, then closed by the press on its own button
                // and that button's click at once; then a click later. Writes down what was open.
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    var said = new List<string>();
                    void Click() => TagFilterBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, TagFilterBtn));
                    Click();
                    var first = lastMenu;
                    said.Add("after the first click: open " + (first?.IsOpen == true));
                    var g2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                    g2.Tick += (s2, e2) =>
                    {
                        g2.Stop();
                        first.IsOpen = false;          // the press outside closes it
                        Click();                       // and the same click lands on the button
                        said.Add("a click on its button while open: open " + (lastMenu?.IsOpen == true) + ", a new menu " + !ReferenceEquals(lastMenu, first));
                        var g3 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                        g3.Tick += (s3, e3) =>
                        {
                            g3.Stop();
                            Click();
                            said.Add("a click later: open " + (lastMenu?.IsOpen == true));
                            var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                            if (path != null) try { File.WriteAllLines(path, said); } catch (IOException) { }
                        };
                        g3.Start();
                    };
                    g2.Start();
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "addlib")
            {
                // "addlib": HD2QD_ADDLIB (a folder) added as a library folder, as the button does,
                // and its question left open for a picture
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) => { g.Stop(); ShowPage("storage"); AddLibraryFolder(Environment.GetEnvironmentVariable("HD2QD_ADDLIB")); };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "listtop")
            {
                // "listtop": the list scrolled back to its top after the start, for the README's
                // picture (--select opens a mod and scrolls it to the top of the list)
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                g.Tick += (s, e) => { g.Stop(); ListScroll.ScrollToVerticalOffset(0); };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "topwins")
            {
                // "topwins": the list shown with the winner at the top. Writes down the rows' order,
                // where a row dropped just below the top row would land, and the row menu's moves.
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    Texts.ShowTopWins(true);
                    rows.Clear();
                    List.Children.Clear();
                    Words();
                    Render();
                    UpdateLayout();
                    var said = new List<string>();
                    var seen = Seen().ToList();
                    said.Add("rows, top first: " + string.Join(", ", seen.Take(3).Select(x => x.Index)) + " ... " + seen.Last().Index + " of " + st.Mods.Count);
                    var dragged = seen[2];
                    var onto = rows[seen[0].Id].Row;
                    double top = onto.TranslatePoint(new Point(0, 0), ListArea).Y;
                    BeginDrag(dragged, rows[dragged.Id].Row, new Point(10, top + onto.ActualHeight * 0.75), 5, false);
                    said.Add("row " + dragged.Index + " dropped just below the top row (" + seen[0].Index + "): lands at " + (dropAnchor == null ? 0 : Moved(st.Mods, dragBlock, dropAnchor, dropAfter).IndexOf(dragged) + 1));
                    EndDrag(false);
                    said.Add("row menu: " + string.Join(" | ", Menu(seen[0]).Items.OfType<MenuItem>().Select(x => x.Header as string).Where(x => x != null)));
                    said.Add("hint: " + ListHint.Text);
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path != null) try { File.WriteAllLines(path, said); } catch (IOException) { }
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "exclude")
            {
                // "exclude": the labels menu opened, Alt pressed and let go on it as the keyboard
                // sends it, then an Alt+click on Probe. Writes down whether the menu stayed open,
                // what the list shows, and leaves the menu open for its picture.
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    var said = new List<string>();
                    int Rows() => (st?.Mods ?? new List<ModRow>()).Count(Shown);
                    int Probes() => (st?.Mods ?? new List<ModRow>()).Where(Shown).Count(m => m.Tags.Any(t => string.Equals(t, "Probe", StringComparison.CurrentCultureIgnoreCase)));
                    said.Add("before: shown " + Rows() + ", of them Probe " + Probes());
                    TagFilterBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, TagFilterBtn));
                    var g2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    g2.Tick += (s2, e2) =>
                    {
                        g2.Stop();
                        var hwnd = (PresentationSource.FromVisual(lastMenu) as HwndSource)?.Handle ?? IntPtr.Zero;
                        PostMessage(hwnd, 0x0104, (IntPtr)0x12, (IntPtr)0x20380001);              // WM_SYSKEYDOWN Alt
                        PostMessage(hwnd, 0x0105, (IntPtr)0x12, unchecked((IntPtr)(int)0xC0380001)); // WM_SYSKEYUP Alt
                        var g3 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                        g3.Tick += (s3, e3) =>
                        {
                            g3.Stop();
                            said.Add("menu open after Alt: " + (lastMenu?.IsOpen == true));
                            var probe = lastMenu?.Items.OfType<MenuItem>().FirstOrDefault(x => (x.Header as string ?? "").StartsWith("Probe "));
                            if (probe == null) said.Add("no Probe label in the menu");
                            else
                            {
                                altForTest = true;
                                probe.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, probe));
                                altForTest = false;
                                said.Add("after Alt+click: shown " + Rows() + ", of them Probe " + Probes() + ", item tag " + probe.Tag);
                                said.Add("menu open after the click: " + (lastMenu?.IsOpen == true));
                            }
                            var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                            if (path != null) try { File.WriteAllLines(path, said); } catch (IOException) { }
                        };
                        g3.Start();
                    };
                    g2.Start();
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "nestwheel")
            {
                // "nestwheel": the wheel over the tags page's add list. Writes down where the list
                // and the page are after one turn, a long turn, the next turn, and a turn over the
                // list with nothing in it to scroll.
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    Height = 640;
                    ShowPage("tags");
                    tagSel = st?.Tags.FirstOrDefault() ?? st?.Mods.SelectMany(m => m.Tags).FirstOrDefault();
                    tagAuto = false;
                    RenderTags();
                    UpdateLayout();
                    var inner = tagHits?.Children.OfType<ScrollViewer>().FirstOrDefault();
                    var said = new List<string>();
                    if (inner == null) said.Add("no add list");
                    else
                    {
                        int t = Environment.TickCount;
                        void Turn(int after)
                        {
                            t += after;
                            var under = (inner.Content as Panel)?.Children.OfType<UIElement>().FirstOrDefault() ?? inner;
                            under.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, t, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                            UpdateLayout();
                        }
                        void Say(string what) =>
                            said.Add(what + ": list " + inner.VerticalOffset + "/" + inner.ScrollableHeight +
                                     " page " + TagsPage.VerticalOffset + "/" + TagsPage.ScrollableHeight);
                        TagsPage.ScrollToVerticalOffset(0); inner.ScrollToVerticalOffset(0); UpdateLayout();
                        Say("start");
                        Turn(1000); Say("one turn");
                        // one notch from the end, then the wheel kept turning, a notch each 50 ms
                        inner.ScrollToVerticalOffset(inner.ScrollableHeight - 48); UpdateLayout();
                        Turn(1000); Say("a turn to the end");
                        for (int i = 0; i < 4; i++) Turn(50);
                        Say("0.2 s more (held)");
                        for (int i = 0; i < 4; i++) Turn(50);
                        Say("0.4 s more (the page)");
                        TagsPage.ScrollToVerticalOffset(0); UpdateLayout();
                        Turn(600); Say("next turn, after a rest");
                        // the page turning, then a list comes under the pointer: the list takes over at once
                        inner.ScrollToVerticalOffset(0); UpdateLayout();
                        var pageBefore = TagsPage.VerticalOffset;
                        Turn(30); Say("still the same turn, the list under the pointer (the list moves)");
                        inner.MaxHeight = double.PositiveInfinity;
                        TagsPage.ScrollToVerticalOffset(0); UpdateLayout();
                        Say("no scroll on the list");
                        Turn(600); Say("a turn over it");
                    }
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path != null) try { File.WriteAllLines(path, said); } catch (IOException) { }
                };
                g.Start();
                return;
            }
            var settingsTest = Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST");
            if (settingsTest == "settings" || settingsTest == "settings_end" || settingsTest == "settings_esc")
            {
                // "settings": the settings page, so a test outside can take its picture;
                // "settings_end": that page scrolled to its end; "settings_esc": that page, then Esc
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    SettingsBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, SettingsBtn));
                    if (settingsTest == "settings_end")
                        Dispatcher.BeginInvoke(new Action(() => SettingsPage.ScrollToEnd()), DispatcherPriority.Loaded);
                    if (settingsTest == "settings_esc")
                        Dispatcher.BeginInvoke(new Action(() => RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent })), DispatcherPriority.Loaded);
                };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "conflict")
            {
                // "conflict": the list of mods in conflict, the first one's details on show
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) => { g.Stop(); filter = "conflict"; RenderNav(); FollowList(); RenderList(); };
                g.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "updates")
            {
                // "updates": the update check's button, the same way
                var u = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                u.Tick += (s, e) => { u.Stop(); UpdatesBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, UpdatesBtn)); };
                u.Start();
                return;
            }
            if (!int.TryParse(Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST"), out var n)) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                var m = st?.Mods.FirstOrDefault(x => x.Index == n);
                if (m == null || !switches.TryGetValue(m.Id, out var sw)) return;
                sw.IsChecked = sw.IsChecked != true;
                sw.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, sw));
            };
            t.Start();
        }

        /// <summary>HD2QD_DRAG_TEST=N,DY (tests only): 4 s after the start, mod N's row is taken
        /// up and carried DY pixels down, and left hanging there — the picture a test outside
        /// takes then shows what a hand would see mid-drag. Nothing is moved: the drag is never
        /// let go.</summary>
        void DragTest()
        {
            var said = (Environment.GetEnvironmentVariable("HD2QD_DRAG_TEST") ?? "").Split(',');
            if (said.Length != 2 || !int.TryParse(said[0], out var n) || !double.TryParse(said[1], out var dy)) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                var m = st?.Mods.FirstOrDefault(x => x.Index == n);
                if (m == null || !rows.TryGetValue(m.Id, out var r)) return;
                // HD2QD_DRAG_PICK=2,4: those places taken together first
                var pick = (Environment.GetEnvironmentVariable("HD2QD_DRAG_PICK") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in pick)
                    if (int.TryParse(p, out var k) && st.Mods.FirstOrDefault(x => x.Index == k) is ModRow px) picked.Add(px.Id);
                if (picked.Count > 0) { RenderList(); UpdateLayout(); }
                if (!rows.TryGetValue(m.Id, out r)) return;          // a row drawn again for the taken ones is a new row
                var top = r.Row.TranslatePoint(new Point(0, 0), ListArea).Y;
                BeginDrag(m, r.Row, new Point(200, top + 20 + dy), 20, false);   // no mouse to hold it
                // HD2QD_DRAG_DROP=1: let go a second later
                if (Environment.GetEnvironmentVariable("HD2QD_DRAG_DROP") == "1")
                {
                    var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    t2.Tick += (s2, e2) => { t2.Stop(); EndDrag(true); };
                    t2.Start();
                }
            };
            t.Start();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveWhere();
            SaveList();
            base.OnClosing(e);
        }

        /// <summary>The library and the deploy record, watched: a change from the
        /// command line shows here half a second later.</summary>
        void Watch()
        {
            var home = IOPath.GetDirectoryName(Cli.LibraryFile);
            try { Directory.CreateDirectory(home); }               // a first start: nothing saved yet
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }
            watcher = new FileSystemWatcher(home, "*.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            // only the files that hold the state: the caches the state read writes itself
            // (crash_sites, lua_scan, same) and the window's own files never start another read
            FileSystemEventHandler h = (s, e) =>
            {
                if (!StateFiles.Contains(IOPath.GetFileName(e.FullPath ?? ""))) return;
                Dispatcher.BeginInvoke(new Action(() => { settle.Stop(); settle.Start(); }));
            };
            watcher.Changed += h;
            watcher.Created += h;
            watcher.Renamed += (s, e) => h(s, e);
            watcher.Deleted += h;
            // it dies silently on an overflow or when the folder is taken away: without this a
            // window left open would never see another change (the user, 2026-10-25)
            watcher.Error += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                try { watcher.Dispose(); } catch (Exception x) when (x is IOException || x is ObjectDisposedException) { }
                watcher = null;
                Watch();
                settle.Stop();
                settle.Start();
            }));
            watcher.EnableRaisingEvents = true;
            again.Tick += (s, e) => Refresh();
            // and whatever the watcher still misses: the library and the deploy record are
            // looked at every few seconds, which is two file reads and nothing else
            beatSaw = Written();
            beat.Tick += (s, e) =>
            {
                var now = Written();
                if (now == beatSaw) return;
                beatSaw = now;
                if (!busy && DateTime.Now > quietUntil) Refresh();
            };
            beat.Start();
        }

        readonly DispatcherTimer beat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        string beatSaw;

        /// <summary>The files whose change is a change of the state the window shows.</summary>
        static readonly HashSet<string> StateFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "library.json", "deployed.json", "deploying.json", "config.json",
#if HAS_NEXUS
            "nexus.json",
#endif
        };

        /// <summary>The size and time of the library and the deploy record, as one string.</summary>
        static string Written()
        {
            var out_ = new System.Text.StringBuilder();
            foreach (var name in new[] { "library.json", "deployed.json" })
                try
                {
                    var f = new FileInfo(Store.PathOf(name));
                    out_.Append(f.Exists ? f.Length + ":" + f.LastWriteTimeUtc.Ticks : "-").Append(" ");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { out_.Append("? "); }
            return out_.ToString();
        }

        // commands and state reads run one at a time, in the order they were asked for
        readonly System.Threading.SemaphoreSlim gate = new System.Threading.SemaphoreSlim(1, 1);
        // quick changes (a switch, an option, the order) asked for and not yet done
        int waiting;
        bool refreshing, refreshAgain;

        async Task<T> Queued<T>(Func<T> work)
        {
            await gate.WaitAsync();
            try { return await Task.Run(work); }
            finally { gate.Release(); }
        }

        /// <summary>The state read again in the background and shown without holding the
        /// window; a state read while a click is still waiting is not shown (the next one is).</summary>
        async void Refresh()
        {
            if (refreshing) { refreshAgain = true; return; }
            refreshing = true;
            if (st == null) Foot(Texts.Get("reading"), "Muted");
            try
            {
                do
                {
                    refreshAgain = false;
                    Mark("state read: start");
                    var r = await Queued(() => Cli.Run("state"));
                    Mark("state read: done");
                    if (!r.Ok)
                    {
                        // the list on show was read before this one failed. It is not the library
                        // any more, so it is taken away rather than left standing there looking
                        // current, and the reading goes on until it works (the user, 2026-10-25)
                        Lost(Last(r.Message));
                        return;
                    }
                    Found();
                    // a quick change is still on its way: what was just read is already out of
                    // date, so read it again rather than drop it and show nothing (the user,
                    // 2026-10-25: another session deployed and the window never caught up)
                    if (waiting > 0) { refreshAgain = true; await Task.Delay(250); continue; }
                    State read;
                    try { read = Cli.ReadState(r.Out); }
                    catch (FormatException e) { Foot(Texts.F("failed", e.Message), "Yellow"); return; }
                    // the scripts not read yet: what was known of them stays on show until they are
                    if (read.LuaPending && st?.Lua != null)
                    {
                        read.Lua = st.Lua;
                        var had = st.Mods.Where(x => x.Lu != null).ToDictionary(x => x.Id, x => x.Lu);
                        foreach (var x in read.Mods) if (had.TryGetValue(x.Id, out var lu)) x.Lu = lu;
                    }
                    st = read;
                    stateJson = r.Out;
                    fromSaved = false;
                    Mark("state parsed");
                    Render();
                    Mark("rendered");
                    if (!st.LuaPending) AdjoinPacks();
                    if (timing != null) Dispatcher.BeginInvoke(new Action(() => Mark("drawn")), DispatcherPriority.ContextIdle);
                    if (st.LuaPending) WarmLua();
                } while (refreshAgain);
            }
            finally { refreshing = false; }
            if (!setupDone)
            {
                setupDone = true;
                Setup();
            }
        }

        bool setupDone;
        bool warming;

        // the packs moved this run: each once, so nothing can go round in a loop
        readonly HashSet<string> adjoined = new HashSet<string>();

        /// <summary>A translation pack goes right after the mod it translates in the load order
        /// (the user, 2026-10-26: "直接调整和原来的mod挨着", a deploy is quick). After, not before:
        /// that is how ActionCam简体中文 ran in game (BML 0.11.0, 2026-10-25). One move a read; the
        /// read the move brings does the next.</summary>
        void AdjoinPacks()
        {
            if (st == null || busy || fromSaved) return;
            foreach (var p in st.Mods.Where(x => x.Lu?.PackOf != null))
            {
                var of = ById(p.Lu.PackOf);
                if (of == null || p.Index == of.Index + 1) continue;
                if (st.ProfilesOn && p.InProfile != of.InProfile) continue;      // not in one profile: not one place
                if (!adjoined.Add(p.Id)) continue;
                lastDone = Texts.F("pack_moved", p.Name, of.Name);   // the bottom line says it after the read
                Run("move", p.Id, "--after", of.Id);
                return;
            }
        }

        // the last state read, kept when the window closes; the list it showed, drawn at once at
        // the next start while the library and the game folder are read again (the user,
        // 2026-10-26: "以前我都没看过读取中"). Until that read comes, nothing is deployed from it.
        string stateJson;
        bool fromSaved;

        static string SavedPath => Store.PathOf("window_state.json");

        void ShowSaved()
        {
            try
            {
                if (!File.Exists(SavedPath)) return;
                var saved = Cli.ReadState(File.ReadAllText(SavedPath, System.Text.Encoding.UTF8));
                // another version's list, or another library's: not shown
                if (saved.Version != StateDoc.Version || !string.Equals(saved.Home, Store.Home, StringComparison.OrdinalIgnoreCase)) return;
                st = saved;
                fromSaved = true;
                Mark("saved list read");
                Render();
                Mark("saved list rendered");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException || e is InvalidCastException
                                      || e is KeyNotFoundException || e is NullReferenceException) { st = null; fromSaved = false; }
        }

        void SaveList()
        {
            if (stateJson == null) return;
            try
            {
                var tmp = SavedPath + ".tmp";
                File.WriteAllText(tmp, stateJson, new System.Text.UTF8Encoding(false));
                if (File.Exists(SavedPath)) File.Replace(tmp, SavedPath, null); else File.Move(tmp, SavedPath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>The mods' scripts read in the background, once, then the state read again:
        /// the list never waits for them (the user, 2026-10-26).</summary>
        async void WarmLua()
        {
            if (warming) return;
            warming = true;
            try { await Task.Run(() => LuaMods.Warm(Library.Load())); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ModError) { }
            finally { warming = false; }
            Refresh();
        }

        /// <summary>The first start, in the window and never on a command line: the game
        /// folder (found from HD2 Arsenal's or Steam's settings, else chosen), then, with
        /// an empty library, HD2 Arsenal's mod list if it has one.</summary>
        async void Setup()
        {
            bool changed = false;
            if (st.GameDir == null)
            {
                var r = await Exec("init");
                if (!r.Ok)
                {
                    Dialog.Tell(this, Texts.Get("game_ask"));
                    var g = PickGame();
                    if (g != null) await Exec("init", "--game", g);
                }
                changed = true;
            }
            if (changed) Refresh();
            // an empty library: the lists of the managers found (HD2 Arsenal, HD2ModManager)
            if (st.Mods.Count == 0) ImportFromManagers(true);
            NoPurgeTip();
            // the versions and sources HD2 Arsenal recorded for its mods, learnt once per start
            await Queued(() =>
            {
                try
                {
                    var lib = Library.Load();
                    if (Origins.FromArsenal(lib) > 0) lib.Save();
                }
                catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException) { }
                return 0;
            });
        }

        /// <summary>A version as shown: "v1.2" for one that starts with a digit, else as written.</summary>
        static string V(string v) => v.Length > 0 && char.IsDigit(v[0]) ? "v" + v : v;

        static string VersionTip(ModRow m) => m.ShownVersion == null ? Texts.Get("ver_none") : Texts.F("ver_tip", Texts.Get("ver_from_" + m.VersionFrom));

        /// <summary>Once, on the first start (the user, 2026-10-24): installing mods never needs a
        /// purge; deploying does it all, only what changed. Remembered in gui.json.</summary>
        void NoPurgeTip()
        {
            Dictionary<string, object> d;
            try { d = Store.Load("gui.json"); }
            catch (Exception e) when (e is IOException || e is FormatException) { d = new Dictionary<string, object>(); }
            if (Json.Bool(d, "tip_no_purge")) return;
            Dialog.Tell(this, Texts.Get("tip_no_purge"));
            d["tip_no_purge"] = true;
            try { Store.Save("gui.json", d); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>The game folder chosen in a folder dialog: the game folder, its data
        /// or its bin; asked again when it is none of them; null when cancelled.</summary>
        string PickGame()
        {
            while (true)
            {
                var chosen = Pick.Folder(this, Texts.Get("game_pick_title"), st?.GameDir);
                if (chosen == null) return null;
                var g = Ops.GameFolderOf(chosen);
                if (g != null) return g;
                Dialog.Tell(this, Texts.F("not_game", chosen));
            }
        }

        void ChooseGame()
        {
            var g = PickGame();
            if (g != null) Run("init", "--game", g);
            else Refresh();
        }

        /// <summary>A big change (deploy, purge, add, ...): the window held meanwhile.</summary>
        async Task<CliResult> Exec(Func<CliResult> work)
        {
            busy = true;
            SetEnabled(false);
            Foot(Texts.Get("working"), "Blue");
            var r = await Queued(work);
            quietUntil = DateTime.Now.AddSeconds(1.5);
            busy = false;
            SetEnabled(true);
            return r;
        }

        Task<CliResult> Exec(params string[] args) => Exec(() => Cli.Run(args));

        /// <summary>One hd2qd command, then the state again.</summary>
        void Run(params string[] args) => _ = Act(() => Cli.Run(args), false);

        /// <summary>Several commands, each waited for before the next: Act turns away a command
        /// while another is running, so firing them in a loop ran only the first.</summary>
        async void RunMany(List<string[]> commands)
        {
            foreach (var args in commands)
                if (!await Act(() => Cli.Run(args), false)) return;
        }

        /// <summary>A change, then the state again. done: its text (the window's own) goes
        /// to the footer with the time it took. A refusal is said in the window's own words
        /// when it has them, never as a command to type.</summary>
        async Task<bool> Act(Func<CliResult> work, bool done)
        {
            if (busy) return false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var r = await Exec(work);
            clock.Stop();
            if (Refused(r)) return false;
            if (done && r.Ok) lastDone = Texts.F("took", Last(r.Out), Seconds(clock.Elapsed.TotalSeconds));
            Refresh();
            return r.Ok;
        }

        /// <summary>A quick change (a switch, an option, the order): shown at once from
        /// what it implies, done in the background in order, the window never held; the
        /// rows it did not change stay as they are.</summary>
        async void Change(string id, Action<ModRow> local, params string[] args)
        {
            var m = st?.Mods.FirstOrDefault(x => x.Id == id);
            if (m == null || busy) return;
            local(m);
            m.Assumed = m.Enabled ? "pending" : "off";
            RenderList();
            if (id == selectedId) RenderDetail();
            waiting++;
            var r = await Queued(() => Cli.Run(args));
            waiting--;
            quietUntil = DateTime.Now.AddSeconds(1.5);
            Refused(r);
            Refresh();
        }

        /// <summary>Says a refusal; true when it took over (the game folder is asked for).</summary>
        bool Refused(CliResult r)
        {
            if (r.Ok) return false;
#if HAS_NEXUS
            bool said = false;
            OurRefusal(r, ref said);
            if (said) return false;
#endif
            switch (r.Refused?.Kind)
            {
                case "no_game":
                    Dialog.Tell(this, Texts.Get("game_ask"));
                    ChooseGame();
                    return true;
                case "duplicate": Dialog.Tell(this, Texts.F("add_dupe", r.Refused.Arg)); break;
                // a folder of mods, not a mod: offered as a library folder, whose mods come in one by
                // one, through their manager's list when it has one (players, 2026-10-26)
                case "library_folder":
                    if (Dialog.Ask(this, Texts.F("add_library_folder", r.Refused.Arg), Texts.Get("add_library_folder_do")))
                        AddLibraryFolder(r.Refused.Arg);
                    return true;
                case "game_running": Dialog.Tell(this, Texts.Get("game_running")); break;
                case "protected": Dialog.Tell(this, Texts.F("del_protected", r.Refused.Arg)); break;
                case "shared": Dialog.Tell(this, Texts.F("del_shared", r.Refused.Arg)); break;
                case "delete_failed": Dialog.Tell(this, Texts.F("delete_failed", r.Refused.Arg)); break;
#if HAS_NEXUS
                case "nexus_login": Dialog.Tell(this, Texts.F("nx_sign_in_failed", r.Message)); break;
                case "nexus_refused": Dialog.Tell(this, Texts.Get("nx_refused")); break;
                case "nexus_offline": Dialog.Tell(this, Texts.Get("nx_offline")); break;
                case "nexus_busy": Dialog.Tell(this, Texts.Get("nx_busy")); break;
#endif
                case "not_zip": Dialog.Tell(this, Texts.F("nx_not_zip", r.Refused.Arg)); break;
                case "group_exists": Dialog.Tell(this, Texts.F("g_exists", r.Refused.Arg)); break;
                case "group_depth": Dialog.Tell(this, Texts.Get("g_depth")); break;
                case "group_inside": Dialog.Tell(this, Texts.Get("g_inside")); break;
                case "pick_one": Dialog.Tell(this, Texts.F("pick_one", r.Refused.Arg)); break;
                default: Dialog.Tell(this, Texts.F("failed", r.Message)); break;
            }
            return false;
        }

        static string Seconds(double s) =>
            s.ToString(s < 10 ? "0.00" : "0.0", System.Globalization.CultureInfo.InvariantCulture);

        static string Last(string text)
        {
            var lines = (text ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
        }

        void SetEnabled(bool on)
        {
            Body.IsEnabled = on;
            DeployBtn.IsEnabled = LaunchBtn.IsEnabled = on;
        }

        /// <summary>The library could not be read: what is on show is from before, so it goes
        /// away and the reading keeps trying by itself.</summary>
        void Lost(string why)
        {
            st = null;
            List.Children.Clear();
            rows.Clear();
            Detail.Children.Clear();
            LostTitle.Text = Texts.Get("lost");
            LostWhy.Text = why;
            LostBox.Visibility = Visibility.Visible;
            Foot(Texts.F("failed", why), "Yellow");
            again.Start();
        }

        void Found()
        {
            again.Stop();
            LostBox.Visibility = Visibility.Collapsed;
        }

        // while the library cannot be read, it is tried again until it can
        readonly DispatcherTimer again = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

        /// <summary>The bottom line: what was just done, else whether the game folder matches the library.</summary>
        void FootState()
        {
            if (st == null) return;
            if (st.GameDir == null) Foot(Texts.Get("no_game"), "Yellow");
            // another program is writing to the game folder: what was just read is a state on
            // its way somewhere, and its numbers are nobody's fault (the user, 2026-10-25)
            else if (st.Deploying != null) Foot(Texts.F("deploy_busy", st.Deploying.Since), "Yellow");
            // files a deploy would leave alone that are not what it thinks they are: an
            // interrupted deploy, another manager, an antivirus. Said before anything else,
            // because nothing else here is true while it holds. What a deploy is on its way to
            // put right is not said here: the Deploy button already counts that (the user,
            // 2026-10-25: a library waiting to be deployed is not a fault)
            else if (st.Check.Wrong > 0) Foot(Texts.F("mismatch", st.Check.Wrong), "Yellow");
            else if (lastDone != null) Foot(Texts.F("done", lastDone), "Blue");
            else if (st.Pending == 0) Foot(Texts.Get("synced"), "Blue");
            else Foot(Texts.F("pending", st.Written, st.Moved, st.Deleted, Texts.Size(st.CopiedBytes)), "Yellow");
            lastDone = null;
        }

        // after a launch, the bottom line says the game runs while it does and goes back to the
        // library's state when it closes, or when no game appeared within three minutes (the user,
        // 2026-10-26: "正在通过 Steam 启动游戏" was still there after the game had quit). Asked every
        // 5 s while the window watches, which is only after a launch
        DispatcherTimer gameWatch;

        void WatchGame()
        {
            gameWatch?.Stop();
            bool seen = false;
            var started = DateTime.UtcNow;
            gameWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            gameWatch.Tick += (s, e) =>
            {
                bool running = Hd2Mod.Deploy.GameRunning();
                if (running && !seen)
                {
                    seen = true;
                    Foot(Texts.Get("game_running"), "Blue");
                }
                else if (!running && (seen || DateTime.UtcNow - started > TimeSpan.FromMinutes(3)))
                {
                    gameWatch.Stop();
                    gameWatch = null;
                    FootState();
                }
            };
            gameWatch.Start();
        }

        void Foot(string text, string colour)
        {
            if (FootText.ContextMenu == null) Selectable.Menu(FootText, () => FootText.Text);
            FootText.Text = text;
            FootText.Foreground = B(colour == "Muted" ? "Ink" : colour);
            FootDot.Fill = B(colour);
        }

        // ---------------------------------------------------------------- the whole window

        // what the filter beside the search box keeps, by kind; each kind empty = everything.
        // Within one kind any of them will do; a mod must pass every kind that has something.
        readonly HashSet<string> tagFilter = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        // the labels left out: a mod carrying any of them is not shown, whatever else it carries
        readonly HashSet<string> tagExclude = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        readonly HashSet<string> fromFilter = new HashSet<string>();
        readonly HashSet<string> kindFilter = new HashSet<string>();

        bool Shown(ModRow m) => InScope(m) && InFilter(m) && Matches(m) && Labelled(m) && FromOne(m) && OfKind(m);

        // the mod library shown instead of the profile in use (profiles on)
        bool viewLibrary;

        /// <summary>In what the list shows: the profile in use, or with the library on show (or
        /// profiles off) every mod.</summary>
        /// <summary>A mod's number as the list shows it: the loaders, which are not in it, are not
        /// counted (they go last in a deploy whatever their place).</summary>
        int Num(ModRow m) => m.Index - (st?.Mods.Count(x => x.Loader && x.Index < m.Index) ?? 0);

        // a loader is not in the list: the panel on the left holds the loaders (the user, 2026-10-26)
        bool InScope(ModRow m) => st == null || (!m.Loader && (!st.ProfilesOn || viewLibrary || m.InProfile));

        /// <summary>A profile's name as shown: the first one's in the window's language.</summary>
        static string ProfileName(string name) => name == Profiles.First ? Texts.Get("pf_default") : name;

        /// <summary>The mods the list shows, top row first: the load order, or the other way round
        /// when the mod that wins is shown at the top (Texts.TopWins).</summary>
        IEnumerable<ModRow> Seen()
        {
            var shown = st.Mods.Where(Shown).ToList();
            // a translation pack is a line under the mod it translates, not a row of its own,
            // while that mod is in the list (the user, 2026-10-26)
            var ids = new HashSet<string>(shown.Select(x => x.Id));
            shown = shown.Where(x => x.Lu?.PackOf == null || !ids.Contains(x.Lu.PackOf)).ToList();
            if (Texts.TopWins) shown.Reverse();
            return shown;
        }

        /// <summary>The translation packs of a mod, in load order.</summary>
        List<ModRow> PacksOf(ModRow m) => st.Mods.Where(x => x.Lu?.PackOf == m.Id && InScope(x)).ToList();

        // ---------------------------------------------------------------- the mods' Lua (LuaMods)

        /// <summary>A loader's name as players know it, with its version.</summary>
        static string LoaderLabel(string kind, string version)
        {
            switch (kind)
            {
                case "bml": return "Boot Mod Loader" + (version != null ? " " + version : "");
                case "chef": case "chef_stub": return "Chef Shared Loader" + (version != null ? " " + version : "");
                case "bsl": return "Bingus Shared Loader" + (version != null ? " " + version : "");
                default: return null;
            }
        }

        ModRow ById(string id) => id == null ? null : st.Mods.FirstOrDefault(x => x.Id == id);

        /// <summary>The library's loaders of these kinds, by name and version; one that is off says so.</summary>
        string[] LibraryLoaders(string[] kinds) =>
            st.Mods.Where(x => x.Lu?.Loader != null && kinds.Contains(x.Lu.Loader))
                   .Select(x => LoaderLabel(x.Lu.Loader, x.Lu.LoaderVersion) + (x.Id == st.LoaderChoice ? "" : x.Id == st.LoaderHelper ? Texts.Get("lua_helping_suffix") : Texts.Get("lua_off_suffix")))
                   .Distinct().ToArray();

        /// <summary>The loader running now, by name; null when none.</summary>
        string RunningLabel()
        {
            var lua = st?.Lua;
            if (lua == null || lua.Running == null || lua.Running == "other") return null;
            var owner = ById(lua.BootOwner);
            var version = st.Mods.FirstOrDefault(x => x.Lu?.Loader == lua.Running && x.Enabled)?.Lu.LoaderVersion ?? owner?.Lu?.LoaderVersion;
            return LoaderLabel(lua.Running, version);
        }

        /// <summary>The issues a mod is in, from the load order's check.</summary>
        IEnumerable<(string Kind, List<string> Mods, string Detail)> IssuesOf(ModRow m) =>
            st?.Lua == null ? Enumerable.Empty<(string, List<string>, string)>() : st.Lua.Issues.Where(i => i.Mods.Contains(m.Id));

        /// <summary>Why an enabled mod's scripts will not run, as the row's tag; null when they run.</summary>
        (string Text, string Colour)? NoRun(ModRow m)
        {
            if (m.Lu == null || !m.Enabled) return null;
            if (m.Lu.Broken.Count > 0) return (Texts.Get("lua_tag_broken"), "Red");
            foreach (var i in IssuesOf(m))
            {
                bool first = i.Mods.Count > 0 && i.Mods[0] == m.Id;
                if (i.Kind == "no_loader" && m.Lu.Needs != null) return (Texts.Get("lua_tag_nostart"), "Yellow");
                if (i.Kind == "needs_chef") return (Texts.Get("lua_tag_nostart"), "Yellow");
                if (i.Kind == "same_name" && !first) return (Texts.Get("lua_tag_nostart"), "Yellow");
                if (i.Kind == "boot_conflict" && !first) return (Texts.Get("lua_tag_boot_off"), "Yellow");
                if (i.Kind == "pack_alone" && first) return (Texts.Get("lua_tag_pack_off"), "Yellow");
            }
            if (m.Lu.LastState == "failed" || m.Lu.LastState == "no_compile") return (Texts.Get("lua_tag_failed"), "Red");
            return null;
        }

        static string When(long unix) => unix <= 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("MM-dd HH:mm");

        /// <summary>The details' card about a mod's Lua: what starts it, whether it will run,
        /// and why not; null for a mod without scripts.</summary>
        UIElement LuaCard(ModRow m)
        {
            var lu = m.Lu;
            if (lu == null || st.Lua == null) return null;
            var box = new StackPanel();
            box.Children.Add(Label("lua_head", new Thickness(0, 0, 0, 6)));
            void Line(string text, string colour = "Ink", bool strong = false) => box.Children.Add(new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, Foreground = B(colour), Margin = new Thickness(0, 2, 0, 2),
                FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            });
            // a line with a name or an error in it: it can be copied (the user, 2026-10-26); whole,
            // when the screen shows only its first line, is what right-click -> Copy takes
            void CopyLine(string text, string colour, string whole = null)
            {
                var t = Selectable.Text(text, 14, colour);
                t.Margin = new Thickness(0, 2, 0, 2);
                if (whole != null) Selectable.Menu(t, () => t.SelectionLength > 0 ? t.SelectedText : whole);
                box.Children.Add(t);
            }
            var running = RunningLabel();
            var owner = ById(st.Lua.BootOwner);
            // a loader, or a mod with a boot of its own: whose start-up script the game runs
            if (lu.Loader != null) Line(Texts.F("lua_loader", LoaderLabel(lu.Loader, lu.LoaderVersion)), "Ink", true);
            // a boot that only starts its own mod: Boot Mod Loader runs it, off the slot
            if (lu.Guest)
            {
                var host = ById(st.Lua.BootOwner);
                Line(Texts.F("lua_guest", host != null ? LoaderLabel("bml", host.Lu?.LoaderVersion) : "Boot Mod Loader"), "Muted");
            }
            else if (lu.ChefRuns) Line(Texts.Get("lua_chef_runs"), "Muted");
            else if (lu.Boot != null)
            {
                if (lu.Loader == null) Line(Texts.Get("lua_own_boot"), "Ink", true);
                if (!m.Enabled) { }
                else if (owner?.Id == m.Id) Line(Texts.Get("lua_boot_runs"), "Blue");
                // the loader in effect always goes last (Loaders): no place in the list changes that,
                // so there is no button to move this one after it any more
                else if (owner != null) Line(Texts.F("lua_boot_off", owner.Name), "Yellow");
            }
            // what starts it: only the loaders the library has, never a list of every loader
            // there is (the user, 2026-10-26: others may come)
            if (lu.Needs != null)
            {
                var able = LibraryLoaders(lu.Needs == "chef" ? new[] { "chef" } : new[] { "bml", "chef", "bsl" });
                if (lu.Needs == "chef")
                    Line(Texts.F((lu.NeedsWhy == "declared" ? "lua_needs_chef_declared" : lu.NeedsWhy == "api" ? "lua_needs_chef_api" : able.Length > 0 ? "lua_needs_chef_have" : "lua_needs_chef")
                                 + (able.Length > 0 ? "" : "_none"),
                                 string.Join("、", able)), "Muted");
                else Line(able.Length > 0 ? Texts.F("lua_needs_any_have", string.Join("、", able)) : Texts.Get("lua_needs_any_none"), "Muted");
                if (m.Enabled)
                {
                    var issues = IssuesOf(m).ToList();
                    if (issues.Any(i => i.Kind == "no_loader"))
                        Line(st.Lua.Running == "other" && owner != null ? Texts.F("lua_other_boot", owner.Name) : Texts.Get("lua_no_loader"), "Yellow");
                    else if (issues.Any(i => i.Kind == "needs_chef"))
                        Line(Texts.F("lua_wrong_loader", running ?? "-"), "Yellow");
                    // started by Chef Shared Loader, which reads no script over 4 MB (2026-10-26)
                    if (issues.Any(i => i.Kind == "too_big_for_chef")) Line(Texts.F("lua_too_big", Texts.Size(lu.Biggest)), "Yellow");
                    // the loader in effect is not repeated here: the card on the left says it (the user, 2026-10-26);
                    // a script name another mod declares too is its own card below, where it is chosen
                }
            }
            // its scripts by the names the loader's log and Mod Lag Watchdog print (mods/author/name),
            // whole, so a name from a log can be matched to the mod (the user, 2026-10-26). Each name
            // once: a script the game's Lua cannot run says why beneath its name, then one short
            // sentence on just what they use: the game does not provide it at present, nor do the
            // library's loaders (no update dates or Lua versions)
            var names = lu.Scripts.Concat(lu.Broken.Select(b => b.Script)).Distinct().ToList();
            foreach (var name in names)
            {
                var dp = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var dot = new TextBlock { Text = "· ", FontSize = 12, Foreground = B("Ink") };
                DockPanel.SetDock(dot, Dock.Left);
                dp.Children.Add(dot);
                var col = new StackPanel();
                col.Children.Add(Selectable.Text(name, 12));
                foreach (var b in lu.Broken.Where(x => x.Script == name))
                {
                    string why;
                    if (b.Message != null)
                        why = b.Kind != null && b.Kind != "syntax" && Texts.Has("lua_kind_" + b.Kind) ? Texts.F("lua_kind_" + b.Kind, b.Script, b.Line)
                            : Texts.F("lua_broken_line", b.Script, b.Line, b.Message);
                    else if (b.LuaJitBytecode) why = Texts.F("lua_kind_bytecode", b.Script);
                    else why = Texts.F("lua_broken_uses", b.Script, string.Join(Texts.Zh ? "、" : ", ", b.Jit.Select(j => Texts.Get("lua_lib_" + j))));
                    var tb = new DockPanel();
                    var cannot = new TextBlock { Text = Texts.Get("lua_broken") + (Texts.Zh ? "" : " "), FontSize = 12, Foreground = B("Red"), FontWeight = FontWeights.SemiBold };
                    DockPanel.SetDock(cannot, Dock.Left);
                    tb.Children.Add(cannot);
                    tb.Children.Add(Selectable.Text(why, 12));
                    // a line from the script's name down to its reason, so the reason is not read
                    // as the next script's (the user, 2026-10-26)
                    var reason = new DockPanel { Margin = new Thickness(0, 1, 0, 0) };
                    var hook = new Border
                    {
                        // the same gap above (below the name) as on the right (before the reason):
                        // 4 px each, measured (the user, 2026-10-26: -5 touched the name); the
                        // corner at the middle of the reason's first line
                        Width = 8, Height = 9, BorderBrush = B("Muted"), BorderThickness = new Thickness(1, 0, 0, 1),
                        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3, -1, 4, 0),
                    };
                    DockPanel.SetDock(hook, Dock.Left);
                    reason.Children.Add(hook);
                    reason.Children.Add(tb);
                    col.Children.Add(reason);
                }
                dp.Children.Add(col);
                box.Children.Add(dp);
            }
            if (lu.Broken.Count > 0)
            {
                var used = lu.Broken.SelectMany(b => b.Jit).Distinct().ToList();
                int code = lu.Broken.Where(b => b.Message != null).Select(b => b.Script).Distinct().Count();
                bool bytecode = lu.Broken.Any(b => b.Message == null && b.LuaJitBytecode);
                string sep = Texts.Zh ? "、" : ", ";
                // what the game lacks, as clauses of one sentence: only what the scripts use
                var clauses = new List<string>();
                if (code > 0) clauses.Add(Texts.Get(code > 1 ? "lua_clause_code_n" : "lua_clause_code"));
                if (bytecode) clauses.Add(Texts.Get("lua_clause_bytecode"));
                if (used.Count > 0) clauses.Add(Texts.F("lua_clause_libs", string.Join(sep, used.Select(j => Texts.Get("lua_lib_" + j)))));
                string lacks = Texts.Zh ? string.Join("，", clauses)
                             : clauses.Count == 1 ? clauses[0] : string.Join(", ", clauses.Take(clauses.Count - 1)) + " and " + clauses.Last();
                var basis = new List<string>();
                // each loader in the library, as far as it is known what it gives, only for the libraries
                var known = st.Mods.Where(x => x.Lu?.Loader == "bml" || x.Lu?.Loader == "chef").Select(x => LoaderLabel(x.Lu.Loader, x.Lu.LoaderVersion)).Distinct().ToList();
                basis.Add(used.Count > 0 && known.Count > 0 ? Texts.F("lua_basis_loaders", lacks, string.Join(sep, known)) : Texts.F("lua_basis", lacks));
                if (used.Count > 0)
                {
                    foreach (var x in st.Mods.Where(x => x.Lu?.Loader == "bsl"))
                        if (used.Contains("ffi")) basis.Add(Texts.F("lua_basis_bsl", LoaderLabel("bsl", x.Lu.LoaderVersion)));
                    foreach (var x in st.Mods.Where(x => x.Lu?.Boot == "other"))
                        basis.Add(Texts.F("lua_basis_unknown", x.Name));
                }
                box.Children.Add(Small(string.Join(Texts.Zh ? "" : " ", basis), new Thickness(0, 6, 0, 0)));   // Chinese sentences take no space between them
            }
            // a translation pack, and the packs of a mod
            if (lu.PackOfName != null)
            {
                var of = ById(lu.PackOf);
                if (of == null) CopyLine(Texts.F("lua_pack_missing", lu.PackOfName), m.Enabled ? "Yellow" : "Muted");
                else if (!of.Enabled) Line(Texts.F("lua_pack_off", of.Name), m.Enabled ? "Yellow" : "Muted");
                else Line(Texts.F("lua_pack", of.Name), "Muted");
            }
            var packs = PacksOf(m);
            if (packs.Count > 0) Line(Texts.F("lua_packs", string.Join(", ", packs.Select(p => p.Name))), "Muted");
            // the last session, as Boot Mod Loader logged it
            if (lu.LastState != null)
            {
                var when = When(st.Lua.SessionWhen);
                if (lu.LastState == "loaded") Line(Texts.F("lua_last_loaded", when, lu.LastMs), "Muted");
                else
                {
                    var key = lu.LastState == "failed" ? "lua_last_failed" : "lua_last_nocompile";
                    CopyLine(Texts.F(key, when, (lu.LastWhy ?? "").Split('\n')[0]), "Red", Texts.F(key, when, (lu.LastWhy ?? "").TrimEnd()));
                }
            }
            if (box.Children.Count <= 1) return null;
            return Panel(box, new Thickness(0, 16, 0, 0));
        }

        /// <summary>The left column's card on the load order's Lua: the loader in effect, what
        /// will not run, the last session (the user, 2026-10-26).</summary>
        void LuaSide()
        {
            var lua = st.Lua;
            if (lua == null) return;
            var loaders = st.Mods.Where(x => x.Loader).ToList();
            // no loader in the library: no panel at all (the user, 2026-10-26). No crash card either: it said
            // nothing a player could act on ("没有一点用"); the facts stay in hd2qd lua and the state for a
            // crash analysis of its own later
            if (loaders.Count == 0)
            {
                if (loaderPop != null) loaderPop.IsOpen = false;
                return;
            }
            var card = new StackPanel();
            {
                var running = RunningLabel();
                // the loaders, one chosen for every profile; the manager puts it in the game last and
                // leaves the others out (the user, 2026-10-26: not a list position for the player to
                // think about). A name opens its details.
                // the title, and an i button that opens the details (the user, 2026-10-26: only the states
                // on the panel itself, "点一下再显示这些详细信息")
                var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                {
                    // the i drawn as a dot and a stem, its ink centred in the ring (a font's "i" sat high: the user, 2026-10-26)
                    var glyph = new Canvas { Width = 13.6, Height = 13.6 };
                    var dot = new Rectangle { Width = 1.8, Height = 1.8, Fill = B("Muted") };
                    Canvas.SetLeft(dot, 5.9); Canvas.SetTop(dot, 2.9);
                    var stem = new Rectangle { Width = 1.8, Height = 5.4, Fill = B("Muted") };
                    Canvas.SetLeft(stem, 5.9); Canvas.SetTop(stem, 5.3);
                    glyph.Children.Add(dot);
                    glyph.Children.Add(stem);
                    var info = new Border
                    {
                        Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.2),
                        BorderBrush = B("Muted"), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.Get("lua_side_info"), Child = glyph,
                    };
                    info.MouseLeftButtonUp += (s, e) => LoaderInfo(info);
                    loaderInfoBtn = info;
                    // the details open: the new state in them, and placed by the new button once it is laid
                    // out (it stays where it is until then; the new button is where the old one was)
                    if (loaderPop != null && loaderPop.IsOpen)
                    {
                        var pop = loaderPop;
                        pop.Child = LoaderInfoBox();
                        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (pop.IsOpen) PlaceBy(pop, info); }));
                    }
                    DockPanel.SetDock(info, Dock.Right);
                    head.Children.Add(info);
                }
                var title = Label("lua_side_head", new Thickness(0));
                title.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(title);
                card.Children.Add(head);
                foreach (var x in loaders) card.Children.Add(LoaderChoice(x, x.Id == st.LoaderChoice));
                card.Children.Add(LoaderChoice(null, st.LoaderChoice == null));
                void Say(string text) => card.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = B("Yellow"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
                foreach (var g in lua.Issues.GroupBy(i => i.Kind))
                {
                    var first = g.First();
                    switch (g.Key)
                    {
                        // what depends on the loader is said under each choice (LoaderChoice), not here
                        // a script name two mods declare: said in those mods' details, where it can be
                        // settled (the user, 2026-10-27)
                        case "cannot_run": Say(Texts.F("lua_side_broken", g.Count())); break;
                        case "pack_alone": Say(Texts.F("lua_side_pack", g.Count())); break;
                    }
                }
                if (lua.Started >= 0)
                    card.Children.Add(Small(Texts.F("lua_side_session", When(lua.SessionWhen), lua.Started, lua.Total), new Thickness(0, 8, 0, 0)));
                if (Texts.Hints && lua.Issues.Count > 0) card.Children.Add(Small(Texts.Get("lua_side_note"), new Thickness(0, 6, 0, 0)));
            }
            Nav.Children.Add(new Border { Height = 12 });
            Nav.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(12), Child = card });
        }

        /// <summary>One line of the loader panel: a round switch to choose it (null: no loader), and
        /// its name, which opens its details.</summary>
        UIElement LoaderChoice(ModRow x, bool on)
        {
            var line = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var ring = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1.5),
                BorderBrush = B(on ? "Blue" : "Muted"), Background = Brushes.Transparent, Margin = new Thickness(0, 3, 8, 0),
                VerticalAlignment = VerticalAlignment.Top, Cursor = on ? Cursors.Arrow : Cursors.Hand, ToolTip = on ? null : Texts.Get("lua_side_pick"),
                Child = on ? new Ellipse { Width = 6, Height = 6, Fill = B("Blue"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null,
            };
            if (!on)
                ring.MouseLeftButtonUp += (s, e) => ChooseLoader(x);
            DockPanel.SetDock(ring, Dock.Left);
            line.Children.Add(ring);
            var words = new StackPanel();
            if (x == null) words.Children.Add(new TextBlock { Text = Texts.Get("lua_side_nothing"), Foreground = B(on ? "Ink" : "Muted"), TextWrapping = TextWrapping.Wrap });
            else
            {
                var label = LoaderLabel(x.LoaderKind, x.Lu?.LoaderVersion) ?? x.Name;
                var name = new TextBlock
                {
                    Text = label, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Cursor = Cursors.Hand,
                    Foreground = B(x.Id == selectedId ? "Blue" : on ? "Ink" : "Muted"),
                };
                var id = x.Id;
                name.MouseLeftButtonUp += (s, e) => { selectedId = id; RenderDetail(); RenderNav(); };
                words.Children.Add(name);
                bool helping = !on && x.Id == st.LoaderHelper;
                words.Children.Add(new TextBlock { Text = Texts.Get(on && loaderSwitching ? "lua_side_switching" : on ? "lua_side_on" : helping ? "lua_side_helping" : "lua_side_off"), FontSize = 12,
                                                   Foreground = B(on || helping ? "Blue" : "Muted"), TextWrapping = TextWrapping.Wrap });
            }
            line.Children.Add(words);
            return line;
        }

        /// <summary>The loader panel's details, from its i button, in a box of their own beside it (the
        /// user, 2026-10-26: the panel shows only the states; "只放在详细信息里"): under every loader, the one
        /// in effect too, the same question, which mods would not start and why. Nothing for no loader: no
        /// Lua mod starts, as everyone knows. Closes like a menu: a click elsewhere, or Esc.</summary>
        void LoaderInfo(FrameworkElement at)
        {
            if (loaderPop != null && loaderPop.IsOpen) { CloseLoaderInfo(); return; }     // the i again: closed
            if (pressClosedInfo) { pressClosedInfo = false; return; }                         // this click's press closed it
            // placed on the screen, not on the button: the left column is drawn anew on every state, and a
            // popup on a button that leaves jumped to the column's corner (the user, 2026-10-26)
            var pop = new System.Windows.Controls.Primitives.Popup
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute,
                // closed by the window (CloseLoaderInfo), not by the popup itself: its own rule closed it
                // when the copy menu of a line in it opened
                StaysOpen = true, AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.None,
                Child = LoaderInfoBox(),
            };
            pop.KeyDown += (s, e) => { if (e.Key == Key.Escape) pop.IsOpen = false; };
            PlaceBy(pop, at);
            loaderPop = pop;
            pop.IsOpen = true;
            pop.Child.Focus();
        }

        /// <summary>The press of the click under way closed the details (the i pressed again).</summary>
        bool pressClosedInfo;

        /// <summary>The details closed like a menu: a click anywhere in the window, Esc, another window, or
        /// the window moved (they are placed on the screen).</summary>
        void CloseLoaderInfo()
        {
            if (loaderPop == null || !loaderPop.IsOpen) return;
            loaderPop.IsOpen = false;
        }

        /// <summary>The details beside the i button: 8 to its right, their tops level.</summary>
        void PlaceBy(System.Windows.Controls.Primitives.Popup pop, FrameworkElement at)
        {
            var source = PresentationSource.FromVisual(at);
            if (source?.CompositionTarget == null) return;
            var p = source.CompositionTarget.TransformFromDevice.Transform(at.PointToScreen(new Point(at.ActualWidth + 8, 0)));
            pop.HorizontalOffset = p.X;
            pop.VerticalOffset = p.Y;
        }

        /// <summary>What the details say (LoaderInfo).</summary>
        FrameworkElement LoaderInfoBox()
        {
            var box = new StackPanel();
            var lua = st.Mods.Any(y => y.Enabled && !y.Loader && y.Lu?.Needs != null);
            bool firstOne = true;
            var sep = Texts.Zh ? "、" : ", ";
            // every line can be selected and copied (the user, 2026-10-26)
            void Say(string text, string colour)
            {
                var t = Selectable.Text(text, 12, colour);
                t.Margin = new Thickness(0, 1, 0, 0);
                box.Children.Add(t);
            }
            foreach (var x in st.Mods.Where(y => y.Loader))
            {
                bool on = x.Id == st.LoaderChoice;
                var name = Selectable.Text(LoaderLabel(x.LoaderKind, x.Lu?.LoaderVersion) ?? x.Name, FontSize, on ? "Ink" : "Muted");
                name.FontWeight = FontWeights.SemiBold;
                name.Margin = new Thickness(0, firstOne ? 0 : 10, 0, 1);
                box.Children.Add(name);
                firstOne = false;
                // helping Boot Mod Loader: the mods it starts, by name (the panel says only "helping")
                if (!on && x.Id == st.LoaderHelper)
                {
                    var helped = st.Mods.Where(y => y.Enabled && !y.Loader && y.Lu?.Needs == "chef").Select(y => y.Name).ToList();
                    var chosen = ById(st.LoaderChoice);
                    Say(Texts.F("lua_info_helping", chosen != null ? LoaderLabel(chosen.LoaderKind, chosen.Lu?.LoaderVersion) ?? chosen.Name : "?",
                                string.Join(sep, helped)), "Blue");
                }
                if (!st.Lua.Outcomes.TryGetValue(x.Id, out var outs)) continue;
                if (outs.Count == 0 && lua) Say(Texts.Get(on ? "lua_side_all_start" : "lua_side_all_start_if"), "Muted");
                foreach (var o in outs)
                {
                    var over = ById(o.Over);
                    var why = Texts.F("lua_why_" + o.Why, over == null ? "?" : over.Loader ? LoaderLabel(over.LoaderKind, over.Lu?.LoaderVersion) ?? over.Name : over.Name);
                    Say(Texts.F(on ? "lua_side_wont" : "lua_side_wont_if", why, string.Join(sep, o.Mods.Select(id => ById(id)?.Name ?? id))), "Yellow");
                }
            }
            var border = new Border
            {
                Background = B("Ground700"), BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Padding = new Thickness(12),
                Width = 340, Child = box, Focusable = true,
            };
            // a popup is outside the window's tree: it takes the window's font only when given it
            TextElement.SetFontFamily(border, FontFamily);
            TextElement.SetFontSize(border, FontSize);
            return border;
        }

        /// <summary>A loader chosen in the panel (null: none): shown at once, the ring moved and the line
        /// saying it is switching, while the choice is saved and the state read again in the background
        /// (the user, 2026-10-26: the panel waited for the whole check before a click showed).</summary>
        async void ChooseLoader(ModRow x)
        {
            if (st == null || busy || loaderSwitching) return;
            st.LoaderChoice = x?.Id;
            // Chef helps only Boot Mod Loader; whether it still does, the state says
            if (x?.LoaderKind != "bml") st.LoaderHelper = null;
            loaderSwitching = true;
            RenderNav();
            waiting++;
            var r = await Queued(() => Cli.Run("loader", x == null ? "none" : x.Id));
            waiting--;
            loaderSwitching = false;
            if (r.Ok) lastDone = x == null ? Texts.Get("loader_none_chosen") : Texts.F("loader_chosen", x.Name);
            Refused(r);
            Refresh();
        }

        /// <summary>A loader choice being saved (ChooseLoader).</summary>
        bool loaderSwitching;

        /// <summary>Any one of the labels the filter holds (or no filter at all), and none it
        /// leaves out.</summary>
        bool Labelled(ModRow m) =>
            (tagFilter.Count == 0 || m.Tags.Any(t => tagFilter.Contains(t))) && !m.Tags.Any(t => tagExclude.Contains(t));

        /// <summary>Where the mod comes from: nexus, github, or none recorded.</summary>
        static string FromOf(ModRow m) => m.Channel == "Nexus Mods" ? "nexus" : m.Channel == "GitHub" ? "github" : "none";

        bool FromOne(ModRow m) => fromFilter.Count == 0 || fromFilter.Contains(FromOf(m));

        /// <summary>What the mod is: a loader, another Lua mod, or files only.</summary>
        static string KindOf(ModRow m) => m.Loader ? "loader" : m.Lua ? "lua" : "asset";

        bool OfKind(ModRow m) => kindFilter.Count == 0 || kindFilter.Contains(KindOf(m));

        string[] searchWords = new string[0];
        TextBlock noMatch;

        /// <summary>Every word typed is somewhere in the mod's metadata (any case): its name,
        /// the author's description, its options' and sub-options' names and descriptions,
        /// its folder and source, its patch files (archive ids), its Nexus mod id, "lua".</summary>
        bool Matches(ModRow m)
        {
            if (searchWords.Length == 0) return true;
            var hay = new System.Text.StringBuilder();
            void Add(string x) { if (!string.IsNullOrEmpty(x)) hay.Append(x).Append('\n'); }
            Add(m.Name); Add(m.Description); Add(m.Path); Add(m.Source); Add(m.Error);
            foreach (var o in m.Options)
            {
                Add(o.Name); Add(o.Description);
                foreach (var x in o.Subs) { Add(x.Name); Add(x.Description); }
            }
            foreach (var f in m.Files) { Add(f.Name); Add(f.Src); }
            if (m.Lua) Add("lua");
            if (m.Lu != null) foreach (var s in m.Lu.Scripts) Add(s);      // a script's name from a log finds its mod
            if (m.Loader) Add("loader");
            foreach (var t in m.Tags) Add(t);
            if (m.NexusMod > 0) { Add("nexus"); Add(m.NexusMod.ToString()); Add(m.NexusPage); }
            Add(m.Channel); Add(m.Page); Add(m.Version); Add(m.ShownVersion);
            var text = hay.ToString();
            return searchWords.All(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        void Search_Changed(object sender, TextChangedEventArgs e)
        {
            searchWords = SearchBox.Text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchClear.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (st == null) return;
            FollowList();
            RenderList();
        }

        /// <summary>The details follow what the list shows: the chosen mod while it is in the
        /// list, else the first one in it. For the search, and for the list on the left (the
        /// user, 2026-10-25: 冲突 showed a mod in no conflict).</summary>
        void FollowList()
        {
            var first = Seen().FirstOrDefault();
            if (first != null && !st.Mods.Any(m => m.Id == selectedId && Shown(m)))
            {
                selectedId = first.Id;
                RenderDetail();
            }
        }

        /// <summary>Text typed on the mod list with no text box to take it goes to the search: no
        /// click on the box first (the user, 2026-10-26). Added to what the box holds. Not with
        /// Ctrl, Alt or Win (shortcuts), not a lone space (it presses the focused button), not
        /// while a row is dragged or another page is on show.</summary>
        void TypeToSearch(object sender, TextCompositionEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl) || string.IsNullOrWhiteSpace(e.Text)) return;
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
            if (OnShow() != ListScroll || !SearchBox.IsVisible || dragRow != null) return;
            // a box one can type in has the keys already (the search itself, a name being changed);
            // a line of copyable text does not
            if (Keyboard.FocusedElement is TextBox tb && !tb.IsReadOnly) return;
            if (Keyboard.FocusedElement is PasswordBox) return;
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text.Length;
            SearchBox.SelectedText = e.Text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }

        void SearchClear_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        }

        bool InFilter(ModRow m)
        {
            switch (filter)
            {
                case "pending": return m.Enabled && m.Changes > 0;
                case "options": return m.Options.Count > 0;
                case "off": return !m.Enabled;
                case "conflict": return m.Conflicts > 0;
                case "broken": return m.Error != null;
                case "lua": return m.Lua;
                case "updates": return HasUpdate(m);
                case "norun": return NoRun(m) != null;
                default: return true;
            }
        }

        void Render()
        {
            // labels remembered from the last time that no mod carries any more filter nothing
            if (!fromSaved && !filtersPruned)
            {
                filtersPruned = true;
                var known = new HashSet<string>(st.Tags.Concat(st.Mods.SelectMany(x => x.Tags)), StringComparer.CurrentCultureIgnoreCase);
                tagFilter.RemoveWhere(t => !known.Contains(t));
                tagExclude.RemoveWhere(t => !known.Contains(t));
            }
            int on = st.Mods.Count(m => m.Enabled);
            // only the missing game folder is said under the title: the counts were noise there (the user, 2026-10-25)
            SummaryText.Text = st.GameDir == null ? Texts.Get("no_game") : "";
            SummaryText.Visibility = st.GameDir == null ? Visibility.Visible : Visibility.Collapsed;
            StatLinks.Text = Texts.F("stat_links_v", Texts.N(st.LinkedFiles), Texts.Size(st.LinkedBytes));
            DeployBtn.Content = st.Pending > 0 ? Texts.F("deploy_n", st.Pending) : Texts.Get("deploy");
            DeployBtn.IsEnabled = st.Deploying == null && !fromSaved;   // two at once would undo each other; a saved list is not the library yet
            // what was just done comes first (a download leaves changes to deploy: the Deploy
            // button counts them); else whether the game folder matches
            FootState();
            if (startWith != null)
            {
                // --select TEXT: the first mod whose name holds it, chosen and open
                var first = st.Mods.FirstOrDefault(m => m.Name.IndexOf(startWith, StringComparison.OrdinalIgnoreCase) >= 0);
                if (first != null)
                {
                    selectedId = first.Id;
                    open.Add(first.Id);
                    if (groupView) RevealSelected();
                    // scrolled to, once the list is laid out
                    Dispatcher.BeginInvoke(new Action(() => { if (rows.TryGetValue(first.Id, out var r)) r.Row.BringIntoView(); }), DispatcherPriority.Loaded);
                }
                startWith = null;
            }
            // the chosen mod stays while it is in the list; else the first the list shows
            WhereItWas();
            // in the groups a group may be chosen instead of a mod (GroupView)
            if (!(groupView && selectedId == null && groupPath.Count > 0) && !st.Mods.Any(m => m.Id == selectedId && (searchWords.Length == 0 || Shown(m))))
                selectedId = groupView ? null : (Seen().FirstOrDefault() ?? st.Mods.FirstOrDefault())?.Id;
            Mark("render: top");
            RenderNav();
            Mark("render: left list");
            FilterWords();
            RenderList();
            Mark("render: rows");
            RenderDetail();
            Mark("render: details");
#if HAS_NEXUS
            if (DownloadPage.Visibility == Visibility.Visible) RenderDownloads();
#endif
            if (StoragePage.Visibility == Visibility.Visible) RenderStorage();
            if (startPage == "download" || startPage == "storage" || startPage == "tags") ShowPage(startPage);
            startPage = null;
        }

        // ---------------------------------------------------------------- the filters and the target

        void RenderNav()
        {
            Nav.Children.Clear();
            Nav.Children.Add(Label("library", new Thickness(8, 0, 0, 4)));
            if (filter == "lua") filter = "all";
            var scope = st.Mods.Where(InScope).ToList();
            var defs = new[]
            {
                // what needs doing something, counted in what the list shows (the profile in use,
                // or the library); what a mod is (Lua and the rest) is the Filter's (the user, 2026-10-25)
                ("all", scope.Count), ("pending", scope.Count(m => m.Enabled && m.Changes > 0)),
                ("updates", scope.Count(HasUpdate)),
                ("off", scope.Count(m => !m.Enabled)),
                ("conflict", scope.Count(m => m.Conflicts > 0)), ("broken", scope.Count(m => m.Error != null)),
                ("norun", scope.Count(m => NoRun(m) != null)),
            };
            foreach (var (key, n) in defs)
            {
                if ((key == "broken" || key == "updates" || key == "norun") && n == 0 && filter != key) continue;
                bool cur = filter == key, hot = (key == "pending" || key == "broken" || key == "updates") && n > 0;
                var btn = new Button { Cursor = Cursors.Hand, FocusVisualStyle = null, Margin = new Thickness(0, 2, 0, 2) };
                var row = new Border
                {
                    MinHeight = 44, Padding = new Thickness(12, 0, 12, 0), BorderThickness = new Thickness(2, 0, 0, 0),
                    BorderBrush = cur ? B("Yellow") : Brushes.Transparent, Background = cur ? B("Ground700") : Brushes.Transparent,
                };
                var dp = new DockPanel { LastChildFill = false };
                var badge = new Border
                {
                    Background = hot ? B("Yellow") : B("Grid"), Padding = new Thickness(6, 0, 6, 0), MinWidth = 22, VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = n.ToString(), FontFamily = Mono, FontSize = 11, Foreground = hot ? B("TapeInk") : B("Muted"), HorizontalAlignment = HorizontalAlignment.Center },
                };
                DockPanel.SetDock(badge, Dock.Right);
                dp.Children.Add(badge);
                dp.Children.Add(new TextBlock { Text = Texts.Get("f_" + key), FontWeight = FontWeights.SemiBold, Foreground = cur ? B("Yellow") : B("Ink"), VerticalAlignment = VerticalAlignment.Center });
                row.Child = dp;
                btn.Template = Plain();
                btn.Content = row;
                btn.Click += (s, e) => { filter = key; RenderNav(); FollowList(); RenderList(); };
                Nav.Children.Add(btn);
            }

            // the deploy target, without a heading: its state says what it is (the user, 2026-10-25)
            Nav.Children.Add(new Border { Height = 24 });
            var card = new StackPanel();
            int far = st.Mods.Count(m => m.Exists && !m.SameDrive);
            string state = far == 0 ? "target_link" : far == st.Mods.Count(m => m.Exists) ? "target_copy" : "target_mixed";
            // the dot, then the title in the width that is left: in a horizontal StackPanel it had no
            // width to wrap in, and a long title was cut ("Some mods on another d", 2026-10-26)
            var head = new DockPanel();
            var dot = new Ellipse { Width = 8, Height = 8, Fill = B(far == 0 ? "Blue" : "Yellow"), Margin = new Thickness(0, 3.5, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(dot, Dock.Left);
            head.Children.Add(dot);
            head.Children.Add(new TextBlock { Text = Texts.Get(state), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(head);
            // no folders here: the Storage page shows them and changes them (the user, 2026-10-25)
            // only shown here: a "Change…" in this card read as changing the mod library too (the
            // user, 2026-10-25); the game folder is changed on the Storage page. With none set yet
            // the card offers to choose one.
            if (st.GameDir == null)
            {
                var pickGame = new Button
                {
                    Content = Texts.Get("game_pick"), Style = (Style)FindResource("Ghost"),
                    MinHeight = 32, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                pickGame.Click += (s, e) => ChooseGame();
                card.Children.Add(pickGame);
            }
            if (Texts.Hints) card.Children.Add(Small(Texts.Get("target_note"), new Thickness(0, 8, 0, 0)));
            // copies in the game folder (another manager's) that can be hard links
            var copies = st.Mods.Where(m => m.Enabled && m.SameDrive).SelectMany(m => m.Files).Where(f => f.Action == "keep" && !f.Linked).ToList();
            if (copies.Count > 0)
            {
                long bytes = copies.Sum(f => f.Bytes);
                card.Children.Add(new TextBlock { Text = Texts.F("copies", Texts.N(copies.Count), Texts.Size(bytes)), FontWeight = FontWeights.SemiBold, Foreground = B("Yellow"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
                if (Texts.Hints) card.Children.Add(Small(Texts.Get("copies_note"), new Thickness(0, 4, 0, 0)));
                // its words wrap: "Make hard links · free 1.5 MB" is wider than the card
                var relink = new Button
                {
                    Content = new TextBlock { Text = Texts.F("relink", Texts.Size(bytes)), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                    Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                relink.Click += (s, e) => _ = Act(() => Cli.Call(() =>
                {
                    var r = Hd2Mod.Deploy.Relink(Library.Load(), Store.GameDir());
                    return Texts.F("relinked", Texts.N(r.Linked), Texts.Size(r.Bytes));
                }), true);
                card.Children.Add(relink);
            }
            // the purge, out of the way: a deploy never needs one first
            card.Children.Add(new Border { Height = 1, Background = B("Grid"), Margin = new Thickness(0, 14, 0, 10) });
            if (Texts.Hints) card.Children.Add(Small(Texts.Get("purge_note"), new Thickness(0, 0, 0, 0)));
            var purge = new Button
            {
                Content = new TextBlock { Text = Texts.Get("purge_long"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            purge.Click += Purge_Click;
            card.Children.Add(purge);
            Nav.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(12), Child = card });
            LuaSide();
        }

        // ---------------------------------------------------------------- Nexus Mods downloads

        static string Exe => System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

#if HAS_NEXUS
        // the download now going: what it is and how far (shown on the download page)
        string downloading;
        TextBlock progressText;
#endif

        /// <summary>A row of a list on a faint band every other row, so a name on the left and its
        /// number on the right pair up at a glance (the user, 2026-10-25). In a card the band runs
        /// to the card's edges; in a list that scrolls, to the list's.</summary>
        Border Band(UIElement row, int index, bool toCardEdges = true) => new Border
        {
            Background = index % 2 == 1 ? B("Band") : Brushes.Transparent,
            Margin = toCardEdges ? new Thickness(-16, 0, -16, 0) : new Thickness(0),
            Padding = toCardEdges ? new Thickness(16, 0, 16, 0) : new Thickness(8, 0, 8, 0),
            Child = row,
        };

        Border Card(UIElement child) =>
            new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(16), Child = child };

        TextBlock Heading(string text) => new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap };

#if HAS_NEXUS
        /// <summary>The download page: the site's Mod Manager Download button (who gets it, take
        /// it or give it back), the Nexus Mods account, the download going now, the downloads so far, and
        /// the mods in the library that came from Nexus Mods.</summary>
        void RenderDownloads()
        {
            Downloads.Children.Clear();
            Downloads.Children.Add(new TextBlock { Text = Texts.Get("dl_title"), FontSize = 28, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            Downloads.Children.Add(Texts.Hints ? Small(Texts.Get("dl_intro"), new Thickness(0, 0, 0, 20)) : new Border { Height = 12 });

            // the button and the key, side by side
            var top = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            top.ColumnDefinitions.Add(new ColumnDefinition());

            bool ours = Nexus.IsOurs(Exe);
            var button = new StackPanel();
            var head = new DockPanel();            // the words wrap beside the dot
            var dot = new Ellipse { Width = 10, Height = 10, Fill = B(ours ? "Blue" : "Muted"), Margin = new Thickness(0, 7, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(dot, Dock.Left);
            head.Children.Add(dot);
            head.Children.Add(Heading(Texts.Get(ours ? "nx_ours" : "nx_not_ours")));
            button.Children.Add(head);
            var other = ours ? null : Program(Nexus.Handler());
            button.Children.Add(Small(ours ? Texts.Get("nx_ours_n") : other != null ? Texts.F("nx_other_n", other) : Texts.Get("nx_none_n"), new Thickness(0, 8, 0, 0)));
            var before = Program(Nexus.Previous());
            var take = new Button
            {
                Content = ours ? (before != null ? Texts.F("nx_give_back", before) : Texts.Get("nx_off")) : Texts.Get("nx_take"),
                Style = (Style)FindResource(ours ? "Ghost" : "Primary"), MinHeight = 40, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = !ours || before != null,
            };
            take.Click += (s, e) =>
            {
                try
                {
                    if (ours) Nexus.GiveBack();
                    else Nexus.TakeOver(Exe);
                }
                catch (Exception x) when (x is ModError || x is UnauthorizedAccessException || x is System.Security.SecurityException) { Dialog.Tell(this, Texts.F("failed", x.Message)); }
                RenderDownloads();
            };
            button.Children.Add(take);
            var c1 = Card(button);
            top.Children.Add(c1);

            var keyPanel = new StackPanel();
            bool drawn = false;
            if (NexusOAuth.ClientId == null) OurConnectCard(keyPanel, ref drawn);
            if (!drawn) SignInCard(keyPanel);          // with no sign-in id in this build, it says so
            var c2 = Card(keyPanel);
            Grid.SetColumn(c2, 2);
            top.Children.Add(c2);
            Downloads.Children.Add(top);

            // a mod by its number, as players pass it around (the user, 2026-10-29: "N站16662")
            Downloads.Children.Add(Card(FindByNumber(ours)));
            Downloads.Children.Add(new Border { Height = 20 });

            // where downloads go: the default mod folder, chosen on the storage page
            var where = new DockPanel();
            var change = new Button { Content = Texts.Get("dl_where_change"), Style = (Style)FindResource("Ghost"), MinHeight = 36, VerticalAlignment = VerticalAlignment.Center };
            change.Click += (s, e) => ShowPage("storage");
            DockPanel.SetDock(change, Dock.Right);
            where.Children.Add(change);
            var wordsWhere = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            wordsWhere.Children.Add(Heading(Texts.Get("dl_where")));
            wordsWhere.Children.Add(Code(Store.ModsDir()));
            wordsWhere.Children.Add(Small(Texts.Get("dl_where_n"), new Thickness(0, 4, 0, 0)));
            where.Children.Add(wordsWhere);
            Downloads.Children.Add(Card(where));
            Downloads.Children.Add(new Border { Height = 20 });

            // the download going now
            if (downloading != null)
            {
                var now = new StackPanel();
                now.Children.Add(Heading(Texts.F("dl_now", downloading)));
                progressText = new TextBlock { FontFamily = Mono, FontSize = 13, Foreground = B("Blue"), Margin = new Thickness(0, 8, 0, 0) };
                now.Children.Add(progressText);
                Downloads.Children.Add(Card(now));
                Downloads.Children.Add(new Border { Height = 20 });
            }

            // the downloads so far
            // every one kept, in a list about ten rows high that scrolls: all of them in the page
            // took the page over (the user, 2026-10-25). The wheel moves on from its end as any
            // list inside a page does (Wheel).
            var history = Nexus.History();
            Downloads.Children.Add(new TextBlock
            {
                Text = history.Count > 0 ? Texts.F("dl_history_n", history.Count) : Texts.Get("dl_history"),
                FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"), Margin = new Thickness(0, 0, 0, 8),
            });
            if (history.Count == 0) Downloads.Children.Add(Small(Texts.Get("dl_history_none"), new Thickness(0, 0, 0, 20)));
            else
            {
                var table = new StackPanel();
                int historyBand = 0;
                // built as the list scrolls, forty rows at a time: five hundred rows at once took
                // 0.94 s, and the page is drawn again on every refresh
                void AddRow(Dictionary<string, object> h)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
                    // the page it was downloaded from, at the file itself (the user, 2026-10-25)
                    long hMod = Json.Long(h, "mod"), hFile = Json.Long(h, "file");
                    if (hMod > 0)
                    {
                        var url = Nexus.Page(new NexusRef { Game = Json.Str(h, "game") ?? Nexus.Game, Mod = hMod }) +
                                  "?tab=files" + (hFile > 0 ? "&file_id=" + hFile : "");
                        var page = new Button { Content = Texts.Get("dl_file_page"), Style = (Style)FindResource("Ghost"), MinHeight = 30, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                        page.Click += (s, e) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        DockPanel.SetDock(page, Dock.Right);
                        row.Children.Add(page);
                    }
                    string result = Json.Str(h, "result");
                    var tag = new Border
                    {
                        Width = 92, Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center,
                        Background = B(result == "failed" ? "Red" : result == "replaced" ? "Blue" : "Yellow"),
                        Child = new TextBlock { Text = Texts.Get("dl_" + result), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = B(result == "failed" ? "Ink" : "TapeInk"), HorizontalAlignment = HorizontalAlignment.Center },
                    };
                    DockPanel.SetDock(tag, Dock.Left);
                    row.Children.Add(tag);
                    var when = new TextBlock { Text = Json.Str(h, "time"), FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), Width = 150, VerticalAlignment = VerticalAlignment.Center };
                    DockPanel.SetDock(when, Dock.Left);
                    row.Children.Add(when);
                    var numbers = new TextBlock
                    {
                        Text = result == "failed" ? "" : Texts.Size(Json.Long(h, "bytes")) + " · " + Texts.F("dl_seconds", Json.Str(h, "seconds") ?? Convert.ToString(h["seconds"], System.Globalization.CultureInfo.InvariantCulture)),
                        FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                    };
                    DockPanel.SetDock(numbers, Dock.Right);
                    row.Children.Add(numbers);
                    var name = Json.Str(h, "name") ?? Texts.F("dl_unnamed", Json.Long(h, "mod"), Json.Long(h, "file"));
                    var what = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var nameBox = Selectable.Text(name, 14);
                    nameBox.FontWeight = FontWeights.SemiBold;
                    what.Children.Add(nameBox);
                    if (result == "failed") what.Children.Add(Selectable.Text(Why(Json.Str(h, "why")), 12, "Yellow"));
                    row.Children.Add(what);
                    table.Children.Add(Band(row, historyBand++, false));
                }
                const int Step = 40;
                int built = 0;
                void More() { foreach (var h in history.Skip(built).Take(Step)) AddRow(h); built = Math.Min(history.Count, built + Step); }
                More();
                table.Margin = new Thickness(0, 0, 12, 0);      // clear of the scroll bar
                var scroll = new ScrollViewer
                {
                    Content = table, MaxHeight = 470, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
                };
                scroll.ScrollChanged += (s, e) =>
                {
                    if (built < history.Count && scroll.VerticalOffset + scroll.ViewportHeight > scroll.ExtentHeight - 300) More();
                };
                Downloads.Children.Add(Card(scroll));
                Downloads.Children.Add(new Border { Height = 20 });
            }

            // the mods that came from Nexus Mods
            var fromNexus = st?.Mods.Where(m => m.NexusPage != null).ToList() ?? new List<ModRow>();
            var nexusHead = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            if (fromNexus.Count > 0)
            {
                var how = SortBy(downloadSort, k => { downloadSort = k; RenderDownloads(); },
                                 ("order", Texts.Get("sort_order")), ("name", Texts.Get("sort_name")));
                DockPanel.SetDock(how, Dock.Right);
                nexusHead.Children.Add(how);
            }
            nexusHead.Children.Add(new TextBlock { Text = Texts.Get("dl_from_nexus"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center });
            Downloads.Children.Add(nexusHead);
            if (fromNexus.Count == 0) Downloads.Children.Add(Small(Texts.Get("dl_from_nexus_none"), new Thickness(0, 0, 0, 0)));
            else
            {
                var list = new StackPanel();
                int nexusBand = 0;
                foreach (var m in downloadSort == "name" ? fromNexus.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList() : fromNexus)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
                    var page = m.NexusPage;
                    var open = new Button { Content = Texts.Get("dl_open_page"), Style = (Style)FindResource("Ghost"), MinHeight = 32 };
                    open.Click += (s, e) => Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
                    DockPanel.SetDock(open, Dock.Right);
                    row.Children.Add(open);
                    var pic = Picture(m.Icon, GlyphMod, 44, m.Enabled ? "Blue" : "Muted");
                    pic.Margin = new Thickness(0, 0, 14, 0);
                    DockPanel.SetDock(pic, Dock.Left);
                    row.Children.Add(pic);
                    var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var nameBox = Selectable.Text(m.Name, 14);
                    nameBox.FontWeight = FontWeights.SemiBold;
                    words.Children.Add(nameBox);
                    words.Children.Add(new TextBlock { Text = Texts.F("dl_mod_line", Num(m), m.NexusMod, Texts.Get(m.Enabled ? "dl_on" : "dl_off_word")), FontFamily = Mono, FontSize = 12, Foreground = B("Muted") });
                    row.Children.Add(words);
                    list.Children.Add(Band(row, nexusBand++));
                }
                Downloads.Children.Add(Card(list));
            }
        }

        // the look-up by number: what the box held, what was found or why nothing was, while
        // the page is drawn anew
        string findTyped, findSaid;
        NexusModInfo found;
        bool finding;

        /// <summary>The download page's look-up: a Nexus Mods number or page typed or pasted, what
        /// the mod's page says, and the way to its Files tab, where Mod Manager Download is.</summary>
        UIElement FindByNumber(bool ours)
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading(Texts.Get("dl_find")));
            if (Texts.Hints) panel.Children.Add(Small(Texts.Get("dl_find_n"), new Thickness(0, 4, 0, 0)));
            var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var go = new Button { Content = Texts.Get("dl_find_do"), Style = (Style)FindResource("Primary"), MinHeight = 40, Margin = new Thickness(8, 0, 0, 0), IsEnabled = !finding };
            DockPanel.SetDock(go, Dock.Right);
            row.Children.Add(go);
            var box = new TextBox { Style = (Style)FindResource("Input"), Tag = Texts.Get("dl_find_ph"), Text = findTyped ?? "" };
            row.Children.Add(box);
            panel.Children.Add(row);

            async void Find()
            {
                if (finding) return;
                findTyped = box.Text;
                found = null;
                long? n;
                try { n = Nexus.ModNumber(box.Text); }
                catch (ModError e) { findSaid = Texts.F("dl_find_other", e.Arg); RenderDownloads(); return; }
                if (n == null) { findSaid = Texts.Get("dl_find_none"); RenderDownloads(); return; }
                finding = true;
                findSaid = null;
                RenderDownloads();
                NexusModInfo got = null;
                var r = await Task.Run(() => Cli.Call(() => { got = Nexus.LookUp(n.Value); return ""; }));
                finding = false;
                var kind = r.Refused?.Kind;
                if (r.Ok) found = got;
                else if (kind == "nexus_missing") findSaid = Texts.F("dl_find_missing", n.Value);
                else findSaid = kind != null && Why(kind) != kind ? Why(kind) : Texts.F("failed", r.Message);
                RenderDownloads();
            }
            go.Click += (s, e) => Find();
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; Find(); } };

            if (finding) panel.Children.Add(Small(Texts.Get("dl_find_busy"), new Thickness(0, 12, 0, 0)));
            else if (findSaid != null)
                panel.Children.Add(new TextBlock { Text = findSaid, Foreground = B("Yellow"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
            else if (found != null) panel.Children.Add(Found(found, ours));
            return panel;
        }

        /// <summary>What a mod's page says: its picture (none for adult content), name, author,
        /// version, numbers and words, its address, whether the library has it, and the buttons
        /// to its page and its Files tab.</summary>
        UIElement Found(NexusModInfo m, bool ours)
        {
            var card = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
            if (!m.Adult && Uri.TryCreate(m.Picture ?? "", UriKind.Absolute, out var pic) && pic.Scheme == Uri.UriSchemeHttps)
            {
                var frame = new Border { Width = 192, Height = 108, Margin = new Thickness(0, 0, 16, 0), Background = B("Ground700"), VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = pic;
                    bmp.DecodePixelWidth = 384;
                    bmp.EndInit();
                    frame.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                }
                catch (Exception e) when (e is NotSupportedException || e is IOException || e is InvalidOperationException) { }
                DockPanel.SetDock(frame, Dock.Left);
                card.Children.Add(frame);
            }
            var words = new StackPanel();
            words.Children.Add(Heading((m.Name ?? "?") + "  ·  Nexus " + m.Mod));
            var meta = new List<string>();
            if (m.Author != null) meta.Add(Texts.F("dl_find_by", m.Author));
            if (m.Version != null) meta.Add(Texts.F("dl_find_version", m.Version));
            if (m.Updated > 0) meta.Add(Texts.F("dl_find_updated", DateTimeOffset.FromUnixTimeSeconds(m.Updated).LocalDateTime.ToString("yyyy-MM-dd")));
            meta.Add(Texts.F("dl_find_stats", m.Downloads.ToString("N0"), m.Endorsements.ToString("N0")));
            if (m.Adult) meta.Add(Texts.Get("dl_find_adult"));
            words.Children.Add(Small(string.Join("  ·  ", meta), new Thickness(0, 4, 0, 0)));
            if (!m.Available)
                words.Children.Add(new TextBlock { Text = Texts.F("dl_find_status", Texts.Has("nx_st_" + m.Status) ? Texts.Get("nx_st_" + m.Status) : m.Status), Foreground = B("Yellow"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            if (m.Summary != null)
                words.Children.Add(new TextBlock { Text = m.Summary, Foreground = B("Ink"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            words.Children.Add(Code(m.Page));
            var have = st?.Mods.FirstOrDefault(x => x.NexusMod == m.Mod);
            if (have != null)
                words.Children.Add(new TextBlock { Text = Texts.F("dl_find_have", have.Name), Foreground = B("Blue"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var files = new Button { Content = Texts.Get("dl_find_files"), Style = (Style)FindResource("Primary"), MinHeight = 36, IsEnabled = m.Available };
            files.Click += (s, e) => Process.Start(new ProcessStartInfo(m.FilesPage) { UseShellExecute = true });
            btns.Children.Add(files);
            var page = new Button { Content = Texts.Get("dl_find_page"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
            page.Click += (s, e) => Process.Start(new ProcessStartInfo(m.Page) { UseShellExecute = true });
            btns.Children.Add(page);
            words.Children.Add(btns);
            // Mod Manager Download there comes back here only while the button is ours
            if (m.Available) words.Children.Add(Small(Texts.Get(ours ? "dl_find_files_n" : "dl_find_take"), new Thickness(0, 6, 0, 0)));
            card.Children.Add(words);
            return card;
        }
#endif

        /// <summary>Why a download failed, in the window's words.</summary>
        static string Why(string kind)
        {
#if HAS_NEXUS
            string why = null;
            OurWhy(kind, ref why);
            if (why != null) return why;
#endif
            switch (kind)
            {
#if HAS_NEXUS
                case "no_key": return Texts.Get("up_no_key");
                case "nexus_refused": return Texts.Get("nx_refused");
                case "nexus_offline": return Texts.Get("nx_offline");
                case "nexus_busy": return Texts.Get("nx_busy");
                case "nexus_missing": return Texts.Get("dl_missing");
#endif
                case "not_zip": return Texts.Get("dl_not_zip");
                default: return kind ?? "";
            }
        }

        // the page the settings page was opened from: Esc and the gear go back to it
        string pageBack = "deploy";

        /// <summary>One of the pages: "deploy", "download", "storage", "tags", or "settings" (the
        /// gear; a page of its own since 2026-10-27, the window was taller than the main one).</summary>
        void ShowPage(string page)
        {
            if (page == "settings" && SettingsPage.Visibility != Visibility.Visible)
                pageBack = StoragePage.IsVisible ? "storage" : TagsPage.IsVisible ? "tags" : DownloadPage.IsVisible ? "download" : "deploy";
            PageDeploy.IsChecked = page == "deploy";
            PageDownload.IsChecked = page == "download";
            PageStorage.IsChecked = page == "storage";
            PageTags.IsChecked = page == "tags";
            Body.Visibility = page == "deploy" ? Visibility.Visible : Visibility.Collapsed;
            DownloadPage.Visibility = page == "download" ? Visibility.Visible : Visibility.Collapsed;
            StoragePage.Visibility = page == "storage" ? Visibility.Visible : Visibility.Collapsed;
            TagsPage.Visibility = page == "tags" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
            // the gear lit while its page is on show, as the page buttons are
            SettingsBtn.Background = page == "settings" ? B("Yellow") : Brushes.Transparent;
            SettingsBtn.BorderBrush = page == "settings" ? B("Yellow") : B("Grid");
            GearPath.Fill = page == "settings" ? B("TapeInk") : B("Ink");
#if HAS_NEXUS
            if (page == "download") RenderDownloads();
#endif
            if (page == "storage") RenderStorage();
            if (page == "tags") RenderTags();
            if (page == "settings") { RenderSettings(); SettingsPage.ScrollToTop(); }
        }

        void Page_Click(object sender, RoutedEventArgs e) =>
            ShowPage(sender == PageDownload ? "download" : sender == PageStorage ? "storage" : sender == PageTags ? "tags" : "deploy");

        // ---------------------------------------------------------------- the storage page

        // how the two lists of mods are sorted (the user, 2026-10-24)
        string storageSort = "size";
#if HAS_NEXUS
        string downloadSort = "order";
#endif

        /// <summary>The buttons that pick the order of a list: the one in use is lit.</summary>
        UIElement SortBy(string now, Action<string> pick, params (string Key, string Text)[] ways)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = Texts.Get("sort_by"), FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var group = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            var inside = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (key, text) in ways)
            {
                var b = new RadioButton { Style = (Style)FindResource("Segment"), Content = text, MinWidth = 88, IsChecked = key == now, GroupName = "sort" + now.GetHashCode() };
                var mine = key;
                b.Click += (s, e) => pick(mine);
                inside.Children.Add(b);
            }
            group.Child = inside;
            row.Children.Add(group);
            return row;
        }

        // the folder shown with its mods, and the mods ticked in it
        string storageSel;
        readonly HashSet<string> ticked = new HashSet<string>();

        static string Gb(long b) => Texts.Size(b);

        string GameDir() => Store.GameDir(null, false);

        /// <summary>Where mods on another drive are best moved: the default folder when it is on the
        /// game's drive, else HD2 Mods on the game's drive.</summary>
        string GameDriveTarget(List<LibraryFolder> folders)
        {
            var def = folders.FirstOrDefault(f => f.IsDefault);
            if (def != null && def.GameDrive) return def.Path;
            var g = GameDir();
            return g == null ? def?.Path : IOPath.Combine(IOPath.GetPathRoot(g), "HD2 Mods");
        }

        /// <summary>Folders whose drive the player chose to keep (no advice for them again).</summary>
        static List<string> KeptDrives()
        {
            try { return Json.List(Store.Load("gui.json"), "drive_ok").OfType<string>().ToList(); }
            catch (Exception e) when (e is IOException || e is FormatException) { return new List<string>(); }
        }

        static void KeepDrives(IEnumerable<string> folders)
        {
            try
            {
                var d = Store.Load("gui.json");
                var list = Json.List(d, "drive_ok").OfType<string>().ToList();
                list.AddRange(folders);
                d["drive_ok"] = list.Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToList();
                Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException) { }
        }

        /// <summary>The storage page: the mod folders as Steam lists its library folders (drive, free
        /// space, mods and their size, whether deploys can use hard links), the default for new mods,
        /// advice for mods on another drive than the game, and a chosen folder's mods to move.</summary>
        void RenderStorage()
        {
            Storages.Children.Clear();
            var lib = Library.Load();
            var folders = Storage.Folders(lib, GameDir());
            if (storageSel == null || folders.All(f => !string.Equals(f.Path, storageSel, StringComparison.OrdinalIgnoreCase)))
                storageSel = (folders.FirstOrDefault(f => f.Mods.Count > 0) ?? folders.FirstOrDefault())?.Path;

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var addFolder = new Button { Content = Texts.Get("st_add_folder"), Style = (Style)FindResource("Ghost"), MinHeight = 40 };
            addFolder.Click += (s, e) => { var p = PickFolder("pick_lib_title"); if (p != null) AddLibraryFolder(p); };
            tools.Children.Add(addFolder);
            var import = new Button { Content = Texts.Get("st_import"), Style = (Style)FindResource("Ghost"), MinHeight = 40, Margin = new Thickness(8, 0, 0, 0) };
            import.Click += (s, e) => ImportFromManagers(false);
            tools.Children.Add(import);
            DockPanel.SetDock(tools, Dock.Right);
            head.Children.Add(tools);
            head.Children.Add(new TextBlock { Text = Texts.Get("st_title"), FontSize = 28, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            Storages.Children.Add(head);
            Storages.Children.Add(Texts.Hints ? Small(Texts.Get("st_intro"), new Thickness(0, 0, 0, 20)) : new Border { Height = 12 });

            Storages.Children.Add(GameCard());
            Storages.Children.Add(new Border { Height = 20 });

            // what the hard links have saved over every deploy: a number that only grows, so it
            // lives here and not in the caption (the user, 2026-10-24)
            var won = new StackPanel();
            won.Children.Add(Heading(Texts.Get("st_saved_title")));
            var nums = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            void Stat(string key, string value)
            {
                var one = new StackPanel { Margin = new Thickness(0, 0, 48, 0) };
                one.Children.Add(new TextBlock { Text = Texts.Get(key), FontSize = 12, Foreground = B("Muted") });
                one.Children.Add(new TextBlock { Text = value, FontFamily = Mono, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = B("Blue") });
                nums.Children.Add(one);
            }
            Stat("stat_links", st == null ? "—" : Texts.F("stat_links_v", Texts.N(st.LinkedFiles), Texts.Size(st.LinkedBytes)));
            Stat("stat_saved", st == null ? "—" : Texts.Size(st.SavedBytes));
            won.Children.Add(nums);
            // an explaining line, gone with the others (the user, 2026-10-25)
            if (Texts.Hints) won.Children.Add(Small(Texts.Get("st_saved_note"), new Thickness(0, 12, 0, 0)));
            Storages.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(16), Child = won });
            Storages.Children.Add(new Border { Height = 20 });

            // mods on another drive than the game, unless the player chose to keep them there
            var kept = KeptDrives();
            var far = folders.Where(f => !f.GameDrive && f.Mods.Count > 0 && !kept.Contains(f.Path, StringComparer.OrdinalIgnoreCase)).ToList();
            if (far.Count > 0 && GameDir() != null)
            {
                var target = GameDriveTarget(folders);
                var advice = new StackPanel();
                advice.Children.Add(Heading(Texts.F("st_far", far.Sum(f => f.Mods.Count), string.Join("、", far.Select(f => f.Drive.TrimEnd('\\'))), IOPath.GetPathRoot(GameDir()).TrimEnd('\\'))));
                advice.Children.Add(Small(Texts.Get("st_far_why"), new Thickness(0, 8, 0, 0)));
                var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                var go = new Button { Content = Texts.F("st_far_move", target), Style = (Style)FindResource("Primary"), MinHeight = 40, Margin = new Thickness(0, 0, 8, 0) };
                go.Click += (s, e) => MoveMods(far.SelectMany(f => f.Mods).ToList(), target);
                row.Children.Add(go);
                var keep = new Button { Content = Texts.Get("st_far_keep"), Style = (Style)FindResource("Ghost"), MinHeight = 40 };
                keep.Click += (s, e) => { KeepDrives(far.Select(f => f.Path)); RenderStorage(); };
                row.Children.Add(keep);
                advice.Children.Add(row);
                Storages.Children.Add(new Border { BorderBrush = B("Yellow"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(16), Child = advice });
                Storages.Children.Add(new Border { Height = 20 });
            }

            // the folders
            foreach (var f in folders) Storages.Children.Add(FolderCard(f));

            // the chosen folder's mods
            var sel = folders.FirstOrDefault(f => string.Equals(f.Path, storageSel, StringComparison.OrdinalIgnoreCase));
            if (sel == null) return;
            ticked.RemoveWhere(id => sel.Mods.All(m => m.Id != id));
            Storages.Children.Add(new Border { Height = 12 });
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            // plain, as "Open its folder": moving is not the page's main act (the user, 2026-10-25)
            var moveBtn = new Button { Content = Texts.F("st_move_n", ticked.Count), Style = (Style)FindResource("Ghost"), MinHeight = 40, IsEnabled = ticked.Count > 0 };
            moveBtn.Click += (s, e) =>
            {
                // no other library folder: a menu of one choice is a click for nothing (the user, 2026-10-25)
                if (!folders.Any(x => x != sel))
                {
                    var p = PickFolder("pick_move_title");
                    if (p != null) MoveMods(sel.Mods.Where(m => ticked.Contains(m.Id)).ToList(), p);
                    return;
                }
                var menu = new ContextMenu { PlacementTarget = moveBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                foreach (var other in folders.Where(x => x != sel))
                {
                    var it = new MenuItem { Header = other.Path + (other.GameDrive ? "" : "  (" + Texts.Get("st_other_drive") + ")") };
                    var dest = other.Path;
                    it.Click += (s2, e2) => MoveMods(sel.Mods.Where(m => ticked.Contains(m.Id)).ToList(), dest);
                    menu.Items.Add(it);
                }
                var pick = new MenuItem { Header = Texts.Get("st_move_other") };
                pick.Click += (s2, e2) => { var p = PickFolder("pick_move_title"); if (p != null) MoveMods(sel.Mods.Where(m => ticked.Contains(m.Id)).ToList(), p); };
                menu.Items.Add(pick);
                menu.IsOpen = true;
            };
            // the page for seeing what takes space is where it gets deleted (the user, 2026-10-25);
            // Delete at the end, Move before it
            var delBtn = new Button
            {
                Content = Texts.F("st_delete_n", ticked.Count), Style = (Style)FindResource("Danger"), MinHeight = 40,
                IsEnabled = ticked.Count > 0,
            };
            delBtn.Click += (s, e) => DeleteTicked(sel.Mods.Where(m => ticked.Contains(m.Id)).ToList());
            DockPanel.SetDock(delBtn, Dock.Right);
            bar.Children.Add(delBtn);
            moveBtn.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(moveBtn, Dock.Right);
            bar.Children.Add(moveBtn);
            var how = SortBy(storageSort, k => { storageSort = k; RenderStorage(); },
                             ("size", Texts.Get("sort_size")), ("name", Texts.Get("sort_name")));
            ((FrameworkElement)how).Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(how, Dock.Right);
            bar.Children.Add(how);
            var all = new CheckBox
            {
                Style = (Style)FindResource("Tick"),
                Content = new TextBlock { Text = Texts.F("st_mods_in", sel.Path), FontWeight = FontWeights.SemiBold, Foreground = B("Ink") },
                IsChecked = sel.Mods.Count > 0 && sel.Mods.All(m => ticked.Contains(m.Id)), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(17, 0, 0, 0),   // the card's border and padding, so it lines up with the ticks in it
            };
            all.Click += (s, e) =>
            {
                if (all.IsChecked == true) foreach (var m in sel.Mods) ticked.Add(m.Id);
                else ticked.Clear();
                RenderStorage();
            };
            bar.Children.Add(all);
            Storages.Children.Add(bar);
            if (sel.Mods.Count == 0) { Storages.Children.Add(Small(Texts.Get("st_empty"), new Thickness(0, 0, 0, 0))); return; }
            var list = new StackPanel();
            // keyed one by one, not with ToDictionary: two mods with the same id must not bring
            // the window down while it is only drawing a list (the user, 2026-10-24)
            var sizes = new Dictionary<string, long>();
            foreach (var x in sel.Mods)
                sizes[x.Id ?? ""] = Directory.Exists(x.Path) ? StateDoc.LibraryBytes(new DirectoryInfo(x.Path)) : 0L;
            var shown = storageSort == "name"
                ? sel.Mods.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
                : sel.Mods.OrderByDescending(x => sizes.TryGetValue(x.Id ?? "", out var b) ? b : 0L).ToList();
            int band = 0;
            foreach (var m in shown)
            {
                var id = m.Id;
                // the tick is the box alone, so the mod's name and folder beside it can be selected
                // and copied (the user, 2026-10-26)
                var box = new CheckBox { Style = (Style)FindResource("Tick"), IsChecked = ticked.Contains(id), VerticalAlignment = VerticalAlignment.Center };
                var line = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
                DockPanel.SetDock(box, Dock.Left);
                line.Children.Add(box);
                var size = new TextBlock { Text = Directory.Exists(m.Path) && sizes.TryGetValue(m.Id ?? "", out var own) ? Gb(own) : "—", FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), Width = 90, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(size, Dock.Right);
                line.Children.Add(size);
                // the mod's own picture: a name and a folder are hard work to read (the user, 2026-10-24)
                var pic = Picture(st?.Mods.FirstOrDefault(x => x.Id == id)?.Icon, GlyphMod, 40, m.Enabled ? "Blue" : "Muted");
                pic.Margin = new Thickness(0, 0, 12, 0);
                DockPanel.SetDock(pic, Dock.Left);
                line.Children.Add(pic);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var title = Selectable.Text(m.Name, 14, m.Enabled ? "Ink" : "Muted");
                title.FontWeight = FontWeights.SemiBold;
                words.Children.Add(title);
                words.Children.Add(Selectable.Text(IOPath.GetFileName(m.Path.TrimEnd('\\')), 11, "Muted", Mono));
                line.Children.Add(words);
                box.Click += (s, e) => { if (box.IsChecked == true) ticked.Add(id); else ticked.Remove(id); RenderStorage(); };
                list.Children.Add(Band(line, band++));
            }
            Storages.Children.Add(Card(list));
            OldVersions();
        }

        /// <summary>The ticked mods deleted from the disk, after one question that names them and their size.</summary>
        async void DeleteTicked(List<Mod> mods)
        {
            if (mods.Count == 0) return;
            long size = mods.Sum(m => Directory.Exists(m.Path) ? StateDoc.LibraryBytes(new DirectoryInfo(m.Path)) : 0L);
            var names = string.Join("\n", mods.Take(10).Select(m => "· " + m.Name)) + (mods.Count > 10 ? "\n…" : "");
            if (!Dialog.AskDanger(this, Texts.F("st_delete_ask", mods.Count, Gb(size), names), Texts.Get("delete_btn"))) return;
            var ids = mods.Select(m => m.Id).ToList();
            var r = await Exec(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var go = lib.Mods.Where(m => ids.Contains(m.Id)).ToList();
                var (_, bytes, failed) = Ops.DeleteMods(lib, go, Store.GameDir());
                var said = Texts.F("st_deleted_n", go.Count - failed.Count, Texts.Size(bytes));
                return failed.Count == 0 ? said : said + "\n\n" + Texts.F("st_delete_failed", string.Join("\n", failed));
            }));
            ticked.Clear();
            if (r.Ok) { if (r.Out.Contains("\n")) Dialog.Tell(this, r.Out); else Foot(r.Out, "Blue"); }
            else if (!Refused(r)) Dialog.Tell(this, Texts.F("failed", r.Message));
            Refresh();
        }

        /// <summary>The folders older versions were unpacked into, which no mod uses any more:
        /// nothing for a small mod, hundreds of megabytes for an armour pack (the user,
        /// 2026-10-25). Ticked off and deleted here; the game is not touched.</summary>
        void OldVersions()
        {
            var left = st?.Leftovers ?? new List<Leftover>();
            if (left.Count == 0) return;
            Storages.Children.Add(Label("st_old", new Thickness(0, 20, 0, 6)));
            var box = new StackPanel();
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            // one tick for all of them, as in the mod list; "Clear" beside "Select all" read as
            // something else again (the user, 2026-10-25)
            int picked = left.Count(l => oldPick.Contains(l.Path));
            var drop = new Button
            {
                Content = picked > 0 ? Texts.F("st_old_drop_n", picked) : Texts.Get("st_old_drop"), Style = (Style)FindResource("Danger"), MinHeight = 34,
                IsEnabled = picked > 0,
            };
            drop.Click += (s, e) => DropOldVersions();
            DockPanel.SetDock(drop, Dock.Right);
            bar.Children.Add(drop);
            var every = new CheckBox
            {
                Style = (Style)FindResource("Tick"), IsChecked = picked == left.Count, VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock
                {
                    Text = Texts.Get("pick_all") + "  ·  " + Texts.F("st_old_n", left.Count, Gb(left.Sum(l => l.Bytes))),
                    FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"),
                },
            };
            every.Click += (s, e) =>
            {
                if (every.IsChecked == true) foreach (var l in left) oldPick.Add(l.Path);
                else foreach (var l in left) oldPick.Remove(l.Path);
                RenderStorage();
            };
            bar.Children.Add(every);
            box.Children.Add(bar);
            if (Texts.Hints) box.Children.Add(Small(Texts.Get("st_old_note"), new Thickness(0, 0, 0, 10)));
            int oldBand = 0;
            foreach (var l in left)
            {
                var path = l.Path;
                var line = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var tick = new CheckBox
                {
                    Style = (Style)FindResource("Tick"), IsChecked = oldPick.Contains(path), VerticalAlignment = VerticalAlignment.Center,
                };
                tick.Click += (s, e) =>
                {
                    if (tick.IsChecked == true) oldPick.Add(path); else oldPick.Remove(path);
                    RenderStorage();
                };
                DockPanel.SetDock(tick, Dock.Left);
                line.Children.Add(tick);
                var size = new TextBlock
                {
                    Text = Gb(l.Bytes), FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), Width = 90,
                    TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(size, Dock.Right);
                line.Children.Add(size);
                var folder = Selectable.Text(l.Name, 12, "Muted", Mono);
                folder.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(folder);
                box.Children.Add(Band(line, oldBand++));
            }
            Storages.Children.Add(Card(box));
        }

        async void DropOldVersions()
        {
            var go = (st?.Leftovers ?? new List<Leftover>()).Where(l => oldPick.Contains(l.Path)).ToList();
            if (go.Count == 0) return;
            if (!Dialog.AskDanger(this, Texts.F("st_old_ask", go.Count, Gb(go.Sum(l => l.Bytes))), Texts.Get("st_old_drop"))) return;
            var args = new List<string> { "leftovers", "--delete" };
            args.AddRange(go.Select(l => l.Path));
            long bytes = go.Sum(l => l.Bytes);
            oldPick.Clear();
            if (await Act(() => Cli.Run(args.ToArray()), false)) Foot(Texts.F("st_old_done", go.Count, Gb(bytes)), "Blue");
            Refresh();
        }

        /// <summary>One folder as Steam shows a library folder: path, badges, the drive's use, its
        /// mods and size, and what can be done with it.</summary>
        /// <summary>A library folder added, then what it holds offered, as Steam finds the games
        /// in a library folder it is given (the user, 2026-10-25).</summary>
        void AddLibraryFolder(string p)
        {
            var r = Cli.Call(() => Storage.AddFolder(p));
            if (!r.Ok) { Dialog.Tell(this, Texts.F("failed", r.Message)); return; }
            storageSel = r.Out;
            RenderStorage();
            var finds = Storage.Look(r.Out, Library.Load());
            // a manager's store whose list was not found, or found and not read, is said and its list
            // asked for, as Import from managers does: the folder was only added, in silence (players
            // on 0.11.0 picked their HD2 Arsenal folder there, 2026-10-27)
            if (finds.Managers.Count > 0 || finds.Loose.Count > 0 || finds.NeedsConfig != null || finds.Problems.Count > 0)
                ImportFromManagers(false, finds, r.Out);
        }

        /// <summary>The menu at the list's title: the profiles (one is in use), the mod library,
        /// then making, renaming and deleting profiles (the user, 2026-10-25).</summary>
        void Profile_Click(object sender, RoutedEventArgs e)
        {
            if (JustClosed(ProfileBtn) || st == null) return;
            var menu = new ContextMenu { PlacementTarget = ProfileBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var name in st.Profiles)
            {
                var it = new MenuItem { Header = ProfileName(name), IsChecked = !viewLibrary && name == st.Profile };
                var to = name;
                it.Click += async (s, e2) =>
                {
                    viewLibrary = false;
                    if (to != st.Profile) await Exec("profile-use", to);
                    Refresh();
                };
                menu.Items.Add(it);
            }
            menu.Items.Add(new Separator());
            var lib = new MenuItem { Header = Texts.F("pf_library_n", st.Mods.Count), IsChecked = viewLibrary };
            lib.Click += (s, e2) => { viewLibrary = true; RenderNav(); RenderList(); RenderDetail(); };
            menu.Items.Add(lib);
            menu.Items.Add(new Separator());
            var make = new MenuItem { Header = Texts.Get("pf_new") };
            make.Click += async (s, e2) =>
            {
                var name = Dialog.AskText(this, Texts.Get("pf_new_ask"), Texts.Get("ok"), "");
                if (string.IsNullOrWhiteSpace(name)) return;
                int how = Dialog.Choose(this, Texts.F("pf_new_how", name.Trim()), Texts.Get("pf_copy"), Texts.Get("pf_empty"), Texts.Get("cancel"));
                if (how != 0 && how != 1) return;
                var r = await Exec(how == 1 ? new[] { "profile-new", name.Trim(), "--empty" } : new[] { "profile-new", name.Trim() });
                if (!r.Ok) { Dialog.Tell(this, Texts.F("failed", r.Message)); return; }
                viewLibrary = false;
                await Exec("profile-use", name.Trim());
                Refresh();
            };
            menu.Items.Add(make);
            var rename = new MenuItem { Header = Texts.F("pf_rename", ProfileName(st.Profile)) };
            rename.Click += async (s, e2) =>
            {
                var was = st.Profile;
                var name = Dialog.AskText(this, Texts.Get("pf_rename_ask"), Texts.Get("ok"), ProfileName(was));
                if (string.IsNullOrWhiteSpace(name) || name.Trim() == ProfileName(was)) return;
                var r = await Exec("profile-rename", was, name.Trim());
                if (!r.Ok) Dialog.Tell(this, Texts.F("failed", r.Message));
                Refresh();
            };
            menu.Items.Add(rename);
            var others = st.Profiles.Where(p => p != st.Profile).ToList();
            var drop = new MenuItem { Header = Texts.Get("pf_delete"), IsEnabled = others.Count > 0 };
            drop.Click += async (s, e2) =>
            {
                var pick = Dialog.Ticks(this, Texts.Get("pf_delete_ask"), others.Select(ProfileName).ToArray(), Texts.Get("delete_btn"), false);
                if (pick == null) return;
                for (int i = 0; i < others.Count; i++)
                    if (pick[i]) await Exec("profile-delete", others[i]);
                Refresh();
            };
            menu.Items.Add(drop);
            Dismissable(menu);
            menu.IsOpen = true;
        }

        /// <summary>The row menu's way in and out of the profile in use, for the mods taken.</summary>
        void ProfileItems(ContextMenu menu, List<ModRow> mods)
        {
            if (st == null || !st.ProfilesOn || mods.Count == 0) return;
            var name = ProfileName(st.Profile);
            var outside = mods.Where(x => !x.InProfile).ToList();
            var inside = mods.Where(x => x.InProfile).ToList();
            if (outside.Count > 0)
            {
                var add = new MenuItem { Header = outside.Count == 1 ? Texts.F("pf_add", name) : Texts.F("pf_add_n", name, outside.Count) };
                add.Click += (s, e) => Run(new[] { "profile-add" }.Concat(outside.Select(x => x.Id)).ToArray());
                menu.Items.Add(add);
            }
            if (inside.Count > 0)
            {
                var take = new MenuItem { Header = inside.Count == 1 ? Texts.F("pf_remove", name) : Texts.F("pf_remove_n", name, inside.Count) };
                take.Click += (s, e) => Run(new[] { "profile-remove" }.Concat(inside.Select(x => x.Id)).ToArray());
                menu.Items.Add(take);
            }
            menu.Items.Add(new Separator());
        }

        /// <summary>The game folder on the Storage page: where it is, whether Helldivers 2 is
        /// really there, and changing it, by hand or found in the Steam libraries (the user,
        /// 2026-10-25: changed here, not in the deploy target card).</summary>
        Border GameCard()
        {
            var g = GameDir();
            var box = new StackPanel();
            var top = new DockPanel();
            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var change = new Button { Content = Texts.Get("st_game_change"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
            change.Click += (s, e) => { var p = PickGame(); if (p != null) SwitchGame(p); };
            acts.Children.Add(change);
            var find = new Button { Content = Texts.Get("st_game_find"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
            find.Click += (s, e) =>
            {
                var found = Ops.FindGame();
                if (found == null) { Dialog.Tell(this, Texts.Get("st_game_none_found")); return; }
                if (g != null && string.Equals(Paths.Abs(found).TrimEnd('\\'), Paths.Abs(g).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                { Dialog.Tell(this, Texts.F("st_game_same", found)); return; }
                SwitchGame(found);
            };
            acts.Children.Add(find);
            if (g != null && Directory.Exists(g))
            {
                var open = new Button { Content = Texts.Get("open"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
                open.Click += (s, e) => OpenFolder(g);
                acts.Children.Add(open);
            }
            DockPanel.SetDock(acts, Dock.Right);
            top.Children.Add(acts);
            top.Children.Add(Heading(Texts.Get("st_game")));
            box.Children.Add(top);
            bool ok = g != null && Ops.GameFolderOf(g) != null;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            line.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(ok ? "Blue" : "Yellow"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock { Text = Texts.Get(g == null ? "st_game_none" : ok ? "st_game_ok" : "st_game_gone"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            box.Children.Add(line);
            if (g != null)
            {
                var path = Selectable.Text(g, 15, "Ink", Mono);
                path.Margin = new Thickness(0, 8, 0, 0);
                box.Children.Add(path);
            }
            return new Border { BorderBrush = B(ok ? "Grid" : "Yellow"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(16), Child = box };
        }

        /// <summary>Deploying to another game folder from now on, after saying what stays in the old one.</summary>
        void SwitchGame(string to)
        {
            var g = GameDir();
            if (g != null && Directory.Exists(g) &&
                Dialog.Choose(this, Texts.F("st_game_switch", to, g), Texts.Get("st_game_switch_go"), Texts.Get("cancel")) != 0) return;
            _ = Act(() => Cli.Run("init", "--game", to), false).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => { if (StoragePage.Visibility == Visibility.Visible) RenderStorage(); })));
        }

        /// <summary>A library folder that is somewhere else now: its mods follow it.</summary>
        async void ChangeFolderPath(LibraryFolder f)
        {
            var to = Pick.Folder(this, Texts.Get("st_path_title"), Directory.Exists(f.Path) ? f.Path : IOPath.GetDirectoryName(f.Path));
            if (to == null || string.Equals(Paths.Abs(to).TrimEnd('\\'), Paths.Abs(f.Path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            var r = await Exec(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var (followed, missing) = Storage.ChangeFolder(lib, f.Path, to);
                lib.Save();
                return Texts.F("st_path_done", followed, Paths.Abs(to)) + (missing > 0 ? "\n\n" + Texts.F("st_path_missing", missing) : "");
            }));
            if (r.Ok) { storageSel = Paths.Abs(to).TrimEnd('\\'); Dialog.Tell(this, r.Out); }
            else Dialog.Tell(this, Texts.F("failed", r.Message));
            Refresh();
            RenderStorage();
        }

        Border FolderCard(LibraryFolder f)
        {
            bool cur = string.Equals(f.Path, storageSel, StringComparison.OrdinalIgnoreCase);
            var panel = new StackPanel();
            var top = new DockPanel();
            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            if (!f.IsDefault)
            {
                var def = new Button { Content = Texts.Get("st_make_default"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
                def.Click += (s, e) => { var r = Cli.Call(() => { Storage.SetDefault(f.Path); return ""; }); if (!r.Ok) Dialog.Tell(this, Texts.F("failed", r.Message)); RenderStorage(); };
                acts.Children.Add(def);
            }
            if (Directory.Exists(f.Path))
            {
                var open = new Button { Content = Texts.Get("open"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
                open.Click += (s, e) => OpenFolder(f.Path);
                acts.Children.Add(open);
            }
            if (f.Registered || f.IsDefault)
            {
                var path = new Button { Content = Texts.Get("st_change_path"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
                path.Click += (s, e) => ChangeFolderPath(f);
                acts.Children.Add(path);
            }
            if (f.Registered && !f.IsDefault)
            {
                var off = new Button { Content = Texts.Get("st_remove"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };
                off.Click += (s, e) => { var r = Cli.Call(() => { Storage.RemoveFolder(f.Path); return ""; }); if (!r.Ok) Dialog.Tell(this, Texts.F("failed", r.Message)); RenderStorage(); };
                acts.Children.Add(off);
            }
            DockPanel.SetDock(acts, Dock.Right);
            top.Children.Add(acts);
            var title = new WrapPanel();
            var folderPath = Selectable.Text(f.Path, 15, "Ink", Mono);
            folderPath.FontWeight = FontWeights.Bold;
            folderPath.TextWrapping = TextWrapping.NoWrap;
            folderPath.Margin = new Thickness(0, 0, 10, 4);
            title.Children.Add(folderPath);
            void Badge(string text, string fill, string ink)
            {
                title.Children.Add(new Border
                {
                    Background = B(fill), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 6, 4), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B(ink) },
                });
            }
            if (f.IsDefault) Badge(Texts.Get("st_default"), "Yellow", "TapeInk");
            if (GameDir() != null) Badge(Texts.Get(f.GameDrive ? "st_game_drive" : "st_other_drive"), f.GameDrive ? "Blue" : "Grid", f.GameDrive ? "TapeInk" : "Ink");
            if (f.InAppData) Badge("AppData", "Grid", "Ink");
            top.Children.Add(title);
            panel.Children.Add(top);
            panel.Children.Add(new TextBlock
            {
                Text = Texts.F("st_line", f.Mods.Count, Gb(f.Bytes), f.Drive.TrimEnd('\\'), Gb(f.Free), Gb(f.Total)),
                FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 2, 0, 8),
            });
            // the drive: used by others, by this folder's mods, free
            if (f.Total > 0)
            {
                var barGrid = new Grid { Height = 8 };
                double used = Math.Max(0, f.Total - f.Free - f.Bytes), mods = f.Bytes, free = f.Free;
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(used, GridUnitType.Star) });
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(mods, f.Total / 300.0), GridUnitType.Star) });
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(free, GridUnitType.Star) });
                var a = new Border { Background = B("Muted"), Opacity = 0.5 };
                var b = new Border { Background = B("Yellow") };
                var c = new Border { Background = B("Grid") };
                Grid.SetColumn(b, 1);
                Grid.SetColumn(c, 2);
                barGrid.Children.Add(a);
                barGrid.Children.Add(b);
                barGrid.Children.Add(c);
                panel.Children.Add(barGrid);
            }
            var card = new Border
            {
                BorderBrush = B(cur ? "Yellow" : "Grid"), BorderThickness = new Thickness(cur ? 2 : 1), Background = B("Ground700"),
                Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 10), Child = panel, Cursor = Cursors.Hand,
            };
            card.MouseLeftButtonUp += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
                storageSel = f.Path;
                ticked.Clear();
                RenderStorage();
            };
            return card;
        }

        static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && !(d is T)) d = VisualTreeHelper.GetParent(d);
            return d as T;
        }

        /// <summary>Mods moved into a folder, after a question that says where to, why a move to the
        /// game's drive is worth it, and whether the old folders go or stay (copy: another manager
        /// may still use them); afterwards, what was moved where.</summary>
        void MoveMods(List<Mod> mods, string target)
        {
            if (mods.Count == 0) return;
            var g = GameDir();
            bool toGameDrive = g != null && string.Equals(IOPath.GetPathRoot(target), IOPath.GetPathRoot(g), StringComparison.OrdinalIgnoreCase);
            bool fromOther = g != null && mods.Any(m => !string.Equals(IOPath.GetPathRoot(m.Path), IOPath.GetPathRoot(g), StringComparison.OrdinalIgnoreCase));
            var text = Texts.F("mv_ask", mods.Count, target);
            if (toGameDrive && fromOther) text += "\n\n" + Texts.Get("mv_why");
            text += "\n\n" + Texts.Get("mv_how");
            int pick = Dialog.Choose(this, text, Texts.Get("mv_move"), Texts.Get("mv_copy"), Texts.Get("cancel"));
            if (pick != 0 && pick != 1) return;
            bool keep = pick == 1;
            var ids = mods.Select(m => m.Id).ToList();
            string summary = null;
            _ = Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var chosen = lib.Mods.Where(m => ids.Contains(m.Id)).ToList();
                var moved = Storage.Move(lib, chosen, target, keep, g);
                string links = "";
                if (moved.Count > 0 && g != null)
                {
                    try
                    {
                        var r = Hd2Mod.Deploy.Relink(Library.Load(), g);
                        if (r.Linked > 0) links = "\n\n" + Texts.F("mv_linked", Texts.N(r.Linked), Texts.Size(r.Bytes));
                    }
                    catch (ModError) { links = "\n\n" + Texts.Get("mv_link_later"); }
                }
                summary = Texts.F(keep ? "mv_done_copy" : "mv_done_move", moved.Count, target) + "\n\n" +
                          string.Join("\n", moved.Take(12).Select(x => x.Mod.Name + "  →  " + x.To)) + (moved.Count > 12 ? "\n…" : "") + links;
                return Texts.F(keep ? "mv_foot_copy" : "mv_foot_move", moved.Count, target);
            }), true).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                ticked.Clear();
                if (summary != null) Dialog.Tell(this, summary);
                RenderStorage();
            })));
        }

        /// <summary>Other managers' lists into the library (ticked ones; all of them on a first start),
        /// then advice for the mods that landed on another drive than the game.</summary>
        async void ImportFromManagers(bool firstStart, FolderFinds finds = null, string folder = null)
        {
            // another manager's store whose list was not found: its list asked for, never loose mods
            if (finds?.NeedsConfig != null)
            {
                var chosen = AskForList(finds.NeedsConfig, folder);
                if (chosen == null) return;
                finds = new FolderFinds { Managers = { chosen } };
            }
            // from a library folder just added: the lists kept in it and its other mod folders
            var all = finds != null ? finds.Managers.Concat(finds.Problems).ToList() : Importers.Found();
            var found = all.Where(x => x.Problem == null).ToList();
            var loose = finds?.Loose ?? new List<string>();
            // a list found and not read is said, never left out as if there were none (the user, 2026-10-26)
            var problems = string.Join("\n\n", all.Where(x => x.Problem != null).Select(x =>
                Texts.F(x.ProblemKind == "bad_format" || x.ProblemKind == "bad_version" ? "im_problem_format" : "im_problem", x.Name, x.Where, x.Problem)));
            if (found.Count == 0 && loose.Count == 0)
            {
                // a library folder whose manager's list could not be read: said, and another file
                // of that manager can be chosen (the user, 2026-10-27)
                var unread = folder != null ? all.FirstOrDefault(x => x.Problem != null) : null;
                if (unread != null)
                {
                    if (!Dialog.Ask(this, problems + "\n\n" + Texts.Get("lf_list_other"), Texts.Get("lf_list_pick"))) return;
                    var other = AskForList(unread.Key, folder, false);
                    if (other != null) ImportFromManagers(false, new FolderFinds { Managers = { other } }, folder);
                    return;
                }
                if (problems.Length > 0) Dialog.Tell(this, problems);
                else if (!firstStart) Dialog.Tell(this, Texts.Get("im_none"));
                return;
            }
            var items = found.Select(x => Texts.F("im_item", x.Name, x.Mods, x.Where)).ToList();
            if (loose.Count > 0) items.Add(Texts.F("lf_loose", loose.Count));
            var ask = Texts.Get(folder != null ? "lf_ask" : firstStart ? "im_ask_first" : "im_ask");
            var pick = Dialog.Ticks(this, problems.Length > 0 ? problems + "\n\n" + ask : ask, items.ToArray(), Texts.Get("arsenal_import"));
            if (pick == null || !pick.Any(x => x)) return;
            // managers that keep several profiles: all of them as profiles, or each one's in use (the user, 2026-10-25)
            var several = found.Where((x, k) => pick[k] && x.ProfileNames.Count > 1).ToList();
            bool allProfiles = false;
            if (several.Count > 0)
            {
                var which = string.Join("\n", several.Select(x => Texts.F("im_profiles_line", x.Name, string.Join("、", x.ProfileNames))));
                int how = Dialog.Choose(this, Texts.F("im_profiles_ask", which), Texts.Get("im_profiles_all"), Texts.Get("im_profiles_one"), Texts.Get("cancel"));
                if (how != 0 && how != 1) return;
                allProfiles = how == 0;
            }
            var before = new HashSet<string>(Library.Load().Mods.Select(m => m.Id));
            var lines = new List<string>();
            // where each mod just added came from: 1, 2, ... one per manager (0, already here)
            var group = new Dictionary<string, int>();
            bool arsenalIn = false;
            for (int i = 0; i < found.Count; i++)
            {
                if (!pick[i]) continue;
                var args = new List<string> { "import-" + found[i].Key };
                if (found[i].Dir != null) { args.Add("--dir"); args.Add(found[i].Dir); }
                if (found[i].File != null) { args.Add("--file"); args.Add(found[i].File); }
                if (allProfiles && found[i].ProfileNames.Count > 1) args.Add("--profiles");
                var r = await Exec(args.ToArray());
                lines.Add(r.Ok ? Last(r.Out.Split('\n').FirstOrDefault() ?? "") : Texts.F("failed", r.Message));
                foreach (var m in Library.Load().Mods)
                    if (!before.Contains(m.Id) && !group.ContainsKey(m.Id)) group[m.Id] = i + 1;
                if (found[i].Key == "arsenal" && r.Ok) arsenalIn = true;
            }
            if (loose.Count > 0 && pick[found.Count])
            {
                var r = await Exec("add-loose", folder);
                lines.Add(r.Ok ? Last(r.Out) : Texts.F("failed", r.Message));
                foreach (var m in Library.Load().Mods)
                    if (!before.Contains(m.Id) && !group.ContainsKey(m.Id)) group[m.Id] = found.Count + 1;
            }
            Refresh();
            var lib = Library.Load();
            var added = lib.Mods.Where(m => !before.Contains(m.Id)).ToList();
            var said = Texts.F("im_done", added.Count, added.Count(m => m.Enabled));
            // HD2 Arsenal's list came in the other way round: say so, and leave the list's direction alone
            if (arsenalIn && Ops.ArsenalTopWins()) said += "\n\n" + Texts.Get("im_arsenal_top");
            // mods from different managers, and the ones already here, meet for the first time
            int cross = added.Count > 0 && group.Values.Distinct().Count() + (before.Count > 0 ? 1 : 0) > 1 ? Importers.CrossClashes(lib, group) : 0;
            if (cross > 0) said += "\n\n" + Texts.F("im_cross", cross);
            Dialog.Tell(this, said + "\n\n" + Texts.Get("arsenal_only_one"));
            AdviseDrive(added);
        }

        /// <summary>A manager's store whose list was not found: the player is told which manager's it
        /// is and asked for its list file, which must list mods in this folder. Null: not given.</summary>
        ImportSource AskForList(string key, string folder, bool ask = true)
        {
            var (name, file, usual) = Importers.ConfigOf(key);
            if (ask && !Dialog.Ask(this, Texts.F("lf_list_ask", name, file), Texts.Get("lf_list_pick"))) return null;
            var pick = new Microsoft.Win32.OpenFileDialog
            {
                Title = Texts.F("lf_list_title", name), Filter = file + "|" + file + "|JSON (*.json)|*.json", CheckFileExists = true,
            };
            if (usual != null && Directory.Exists(usual)) pick.InitialDirectory = usual;
            if (pick.ShowDialog(this) != true) return null;
            ImportSource src;
            try { src = Importers.FromChosenFile(key, pick.FileName, folder); }
            catch (Exception e) when (e is ModError || e is IOException || e is FormatException || e is UnauthorizedAccessException || e is InvalidCastException)
            {
                Dialog.Tell(this, Texts.F("lf_list_bad", pick.FileName, e.Message));
                return null;
            }
            if (src.Mods == 0) { Dialog.Tell(this, Texts.F("lf_list_none", pick.FileName, folder)); return null; }
            return src;
        }

        /// <summary>Mods just added that sit on another drive than the game: say what moving them
        /// gives and where they would go; moved, copied, or kept where they are as the player says
        /// (kept: no advice for those folders again).</summary>
        void AdviseDrive(List<Mod> mods)
        {
            var g = GameDir();
            if (g == null) return;
            var kept = KeptDrives();
            var far = mods.Where(m => !string.Equals(IOPath.GetPathRoot(m.Path), IOPath.GetPathRoot(g), StringComparison.OrdinalIgnoreCase)
                                      && !kept.Contains(IOPath.GetDirectoryName(m.Path.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase)).ToList();
            if (far.Count == 0) return;
            var target = GameDriveTarget(Storage.Folders(Library.Load(), g));
            var drives = string.Join("、", far.Select(m => IOPath.GetPathRoot(m.Path).TrimEnd('\\')).Distinct());
            var text = Texts.F("adv_text", far.Count, drives, IOPath.GetPathRoot(g).TrimEnd('\\')) + "\n\n" + Texts.Get("mv_why") + "\n\n" + Texts.F("adv_where", target);
            int pick = Dialog.Choose(this, text, Texts.Get("adv_move"), Texts.Get("adv_copy"), Texts.Get("adv_keep"));
            if (pick == 0 || pick == 1) MoveModsNow(far, target, pick == 1);
            else
            {
                KeepDrives(far.Select(m => IOPath.GetDirectoryName(m.Path.TrimEnd('\\'))).Distinct());
                Foot(Texts.Get("adv_kept"), "Blue");
            }
        }

        /// <summary>MoveMods without its question (the advice asked already).</summary>
        void MoveModsNow(List<Mod> mods, string target, bool keep)
        {
            var g = GameDir();
            var ids = mods.Select(m => m.Id).ToList();
            string summary = null;
            _ = Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var moved = Storage.Move(lib, lib.Mods.Where(m => ids.Contains(m.Id)).ToList(), target, keep, g);
                string links = "";
                try
                {
                    var r = Hd2Mod.Deploy.Relink(Library.Load(), g);
                    if (r.Linked > 0) links = "\n\n" + Texts.F("mv_linked", Texts.N(r.Linked), Texts.Size(r.Bytes));
                }
                catch (ModError) { links = "\n\n" + Texts.Get("mv_link_later"); }
                summary = Texts.F(keep ? "mv_done_copy" : "mv_done_move", moved.Count, target) + links;
                return Texts.F(keep ? "mv_foot_copy" : "mv_foot_move", moved.Count, target);
            }), true).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { if (summary != null) Dialog.Tell(this, summary); })));
        }
#if HAS_NEXUS
        /// <summary>A handler command's program, by its file name ("HD2Arsenal"); null without one.</summary>
        static string Program(string h)
        {
            if (string.IsNullOrEmpty(h)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(h, "^\\s*\"?([^\"]+?\\.exe)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? IOPath.GetFileNameWithoutExtension(m.Groups[1].Value) : h;
        }
#endif

#if HAS_NEXUS
        /// <summary>The Nexus Mods card once Nexus Mods has issued this program's sign-in id: signed in
        /// as whom, or the button that signs in (OAuth with PKCE, NexusOAuth). Our testing builds'
        /// own way in is not offered then.</summary>
        void SignInCard(StackPanel panel)
        {
            if (NexusOAuth.ClientId == null)
            {
                panel.Children.Add(Heading(Texts.Get("nx_out")));
                panel.Children.Add(Small(Texts.Get("nx_no_client"), new Thickness(0, 8, 0, 0)));
                return;
            }
            (string Name, bool Premium)? who = null;
            try { if (NexusOAuth.SignedIn) who = NexusOAuth.Account(); }
            catch (ModError) { }
            var head = new DockPanel();
            var dot = new Ellipse { Width = 10, Height = 10, Fill = B(who != null ? "Blue" : "Yellow"), Margin = new Thickness(0, 7, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(dot, Dock.Left);
            head.Children.Add(dot);
            head.Children.Add(Heading(who == null ? Texts.Get("nx_out") : Texts.F(who.Value.Premium ? "nx_in_premium" : "nx_in", who.Value.Name)));
            panel.Children.Add(head);
            panel.Children.Add(Small(Texts.Get(who == null ? "nx_out_note" : "nx_in_note"), new Thickness(0, 8, 0, 0)));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            if (who == null)
            {
                var signIn = new Button { Content = Texts.Get("nx_sign_in"), Style = (Style)FindResource("Primary"), MinHeight = 40 };
                signIn.Click += async (s, e) => { await SignIn(); RenderDownloads(); };
                row.Children.Add(signIn);
            }
            else
            {
                var signOut = new Button { Content = Texts.Get("nx_sign_out"), Style = (Style)FindResource("Ghost"), MinHeight = 40 };
                signOut.Click += (s, e) => { if (Dialog.Ask(this, Texts.Get("nx_sign_out_ask"), Texts.Get("nx_sign_out"))) { NexusOAuth.Logout(); RenderDownloads(); } };
                row.Children.Add(signOut);
            }
            panel.Children.Add(row);
        }

        /// <summary>The sign-in: Nexus Mods' page opened in the browser, the window waiting (five minutes
        /// at most) for the player to allow it there. True when signed in.</summary>
        async Task<bool> SignIn()
        {
            Foot(Texts.Get("nx_signing_in"), "Blue");
            var r = await Task.Run(() => Cli.Call(() =>
            {
                var (name, premium) = NexusOAuth.Login(url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }), TimeSpan.FromMinutes(5));
                return Texts.F(premium ? "nx_in_premium" : "nx_in", name);
            }));
            Activate();
            if (!r.Ok) { Dialog.Tell(this, Texts.F("nx_sign_in_failed", r.Message)); FootState(); return false; }
            Foot(r.Out, "Blue");
            return true;
        }

        /// <summary>The player's Nexus Mods account connected: the sign-in when this build has a
        /// sign-in id; else, in our testing builds only, their own way in. True when connected.</summary>
        async Task<bool> AskKey()
        {
            if (NexusOAuth.ClientId != null) return await SignIn();
            Func<Task<bool>> other = null;
            OurConnect(ref other);
            if (other != null) return await other();
            Dialog.Tell(this, Texts.Get("nx_no_client"));
            return false;
        }

        // What only our own builds show is in private/ (not in the repository), each through one
        // of these; a build without it has none of them, and the compiler leaves their calls out.
        partial void OurRefusal(CliResult r, ref bool said);
        partial void OurConnectCard(StackPanel panel, ref bool drawn);
        partial void OurConnect(ref Func<Task<bool>> connect);
        static partial void OurWhy(string kind, ref string why);
#endif

#if HAS_NEXUS
        readonly Queue<string> links = new Queue<string>();
        bool linking;
#endif

        /// <summary>The window's pipe: a later start sends a line here, and the window comes forward.</summary>
        void Listen()
        {
            var t = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new System.IO.Pipes.NamedPipeServerStream(App.Pipe, System.IO.Pipes.PipeDirection.In, 1))
                        {
                            server.WaitForConnection();
                            using (var r = new StreamReader(server, System.Text.Encoding.UTF8))
                            {
                                var line = (r.ReadLine() ?? "").Trim();
                                Dispatcher.BeginInvoke(new Action(() => Arrived(line)));
                            }
                        }
                    }
                    catch (IOException) { System.Threading.Thread.Sleep(500); }
                }
            }) { IsBackground = true, Name = "window pipe" };
            t.Start();
        }

        void Arrived(string link)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
#if HAS_NEXUS
            if (string.IsNullOrEmpty(link)) return;
            links.Enqueue(link);
            if (!linking) TakeLinks();
#endif
        }

#if HAS_NEXUS
        /// <summary>Each link in turn: another game's goes on to the program that had the
        /// button before; Helldivers 2's is downloaded and put in the library.</summary>
        async void TakeLinks()
        {
            linking = true;
            try
            {
                while (links.Count > 0)
                {
                    var url = links.Dequeue();
                    NxmLink link;
                    try { link = NxmLink.Parse(url); }
                    catch (ModError) { Dialog.Tell(this, Texts.F("nx_bad_link", url)); continue; }
                    if (link.Game != Nexus.Game)
                    {
                        if (!Nexus.PassOn(url)) Dialog.Tell(this, Texts.F("nx_other_game", link.Game));
                        continue;
                    }
                    if (Nexus.Credential() == null && !await AskKey()) continue;
                    while (busy) await Task.Delay(200);
                    downloading = Texts.F("dl_unnamed", link.Mod, link.File);
                    ShowPage("download");
                    long shown = 0;
                    await Act(() => Cli.Call(() =>
                    {
                        var (mod, replaced) = Nexus.Install(link, Nexus.Credential(), (done, total) =>
                        {
                            // the footer, at most every 128 KB
                            if (done - shown < 128 * 1024 && done != total) return;
                            shown = done;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                var text = total > 0 ? Texts.F("nx_progress", Texts.Size(done), Texts.Size(total), done * 100 / total)
                                                     : Texts.F("nx_progress_n", Texts.Size(done));
                                Foot(text, "Blue");
                                if (progressText != null) progressText.Text = text;
                            }));
                        });
                        return Texts.F(replaced ? "nx_replaced" : "nx_added", mod.Name);
                    }), true);
                    downloading = null;
                    progressText = null;
                    RenderDownloads();
                }
            }
            finally { linking = false; }
        }
#endif

        // ---------------------------------------------------------------- the list

        void RenderList()
        {
            if (dragRow != null) EndDrag(false);         // the rows are about to be built again
            ListTitle.Text = filter == "all" ? Texts.Get("t_all") : Texts.Get("f_" + filter);
            bool profiled = st.ProfilesOn;
            if (!profiled) viewLibrary = false;
            ListTitle.Visibility = profiled ? Visibility.Collapsed : Visibility.Visible;
            ProfileBtn.Visibility = profiled ? Visibility.Visible : Visibility.Collapsed;
            if (profiled)
            {
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock { Text = viewLibrary ? Texts.Get("pf_library") : ProfileName(st.Profile), FontSize = 28, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(new Path
                {
                    Data = Geometry.Parse("M0,0 L7,7 L14,0"), Stroke = B("Yellow"), StrokeThickness = 2.2,
                    Margin = new Thickness(12, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                });
                ProfileBtn.Template = Plain();
                ProfileBtn.Content = head;
            }
            ShowView();
            ListHint.Text = Texts.Get(groupView ? "g_hint" : Texts.TopWins ? "list_hint_top" : "list_hint");
            if (groupView) { RenderGroups(); return; }
            // a row is built again only when what it shows changed; the others stay put
            var want = new List<UIElement>();
            var seen = new HashSet<string>();
            foreach (var m in Seen())
            {
                var sig = Sig(m);
                if (!rows.TryGetValue(m.Id, out var had) || had.Sig != sig) rows[m.Id] = had = (sig, Row(m));
                want.Add(had.Row);
                seen.Add(m.Id);
            }
            foreach (var gone in rows.Keys.Where(k => !seen.Contains(k)).ToList()) rows.Remove(gone);
            picked.RemoveWhere(id => !seen.Contains(id));
            if (want.Count == 0 && searchWords.Length > 0)
            {
                noMatch = noMatch ?? new TextBlock { Foreground = B("Muted"), Margin = new Thickness(20) };
                noMatch.Text = Texts.Get("search_none");
                want.Add(noMatch);
            }
            var keep = new HashSet<UIElement>(want);
            for (int i = 0; i < want.Count; i++)
            {
                if (i < List.Children.Count && List.Children[i] == want[i]) continue;
                if (i < List.Children.Count && !keep.Contains(List.Children[i])) List.Children.RemoveAt(i);
                if (List.Children.Contains(want[i])) List.Children.Remove(want[i]);
                List.Children.Insert(i, want[i]);
            }
            while (List.Children.Count > want.Count) List.Children.RemoveAt(List.Children.Count - 1);
        }

        readonly Dictionary<string, (string Sig, Border Row)> rows = new Dictionary<string, (string, Border)>();

        /// <summary>Everything a row shows or acts on, as one string.</summary>
        string Sig(ModRow m)
        {
            var sb = new System.Text.StringBuilder();
            void Add(object x) => sb.Append(x).Append('\u001f');
            Add(m.Id == selectedId); Add(picked.Contains(m.Id)); Add(open.Contains(m.Id)); Add(filter == "all");
            Add(m.InProfile); Add(viewLibrary); Add(st.Profile);
            Add(m.Index); Add(m.Name); Add(m.Path); Add(m.Enabled); Add(m.Icon); Add(m.Status); Add(m.Error); Add(m.Lua); Add(m.Loader); Add(string.Join(",", m.Tags)); Add(HasUpdate(m)); Add(m.ShownVersion); Add(m.VersionFrom);
            Add(Checked(m)?.Status); Add(checkingOne.Contains(m.Id)); Add(checking); Add(updated.Contains(m.Id));
            Add(m.Files.Count); Add(m.DeployBytes); Add(m.LibraryBytes); Add(m.Changes); Add(m.Conflicts); Add(m.Shared); Add(m.Chosen); Add(m.Choices);
            Add(NoRun(m)?.Text);
            foreach (var p in PacksOf(m)) { Add(p.Id); Add(p.Name); Add(p.Enabled); Add(p.Id == selectedId); Add(NoRun(p)?.Text); }
            if (open.Contains(m.Id))
                foreach (var o in m.Options)
                {
                    Add(o.Name); Add(o.Enabled); Add(o.Icon); Add(o.Files); Add(o.Bytes); Add(o.Description);
                    foreach (var s in o.Subs) { Add(s.Name); Add(s.Enabled); Add(s.Icon); Add(s.Files); Add(s.Bytes); Add(s.Description); }
                }
            return sb.ToString();
        }

        Border Row(ModRow m)
        {
            bool cur = m.Id == selectedId, isOpen = open.Contains(m.Id), alsoTaken = picked.Contains(m.Id);
            var item = new Border
            {
                BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Tag = m, AllowDrop = filter == "all" && !viewLibrary,
                Background = cur || alsoTaken ? B("Ground900") : Brushes.Transparent,
            };
            var edge = new Border
            {
                // yellow is the row on its own; in a group every row taken is blue, the one whose
                // details are shown among them, so what is marked is what will be acted on
                BorderThickness = new Thickness(cur ? 4 : 2, 0, 0, 0),
                BorderBrush = alsoTaken ? B("Blue") : cur ? B("Yellow") : Brushes.Transparent,
            };
            item.Child = edge;
            var col = new StackPanel();
            edge.Child = col;

            var line = new DockPanel { Margin = new Thickness(4, 8, 16, 8) };
            col.Children.Add(line);
            // the load order's number
            // a mod outside the profile in use has no place in its load order
            var num = new TextBlock { Text = st.ProfilesOn && !m.InProfile ? "" : Num(m).ToString(), FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), Width = 28, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            DockPanel.SetDock(num, Dock.Left);
            line.Children.Add(num);
            // open / close
            var chev = new Button { Width = 36, Height = 44, Cursor = Cursors.Hand, Template = Plain(), ToolTip = Texts.Get(isOpen ? "collapse" : "expand"), FocusVisualStyle = null };
            chev.Content = new Path
            {
                Data = Geometry.Parse("M9 5l7 7-7 7"), Stroke = B(isOpen ? "Yellow" : "Muted"), StrokeThickness = 2.5, Width = 16, Height = 16, Stretch = Stretch.Uniform,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(isOpen ? 90 : 0), Margin = new Thickness(10),
            };
            chev.Click += (s, e) => { if (!open.Remove(m.Id)) open.Add(m.Id); Current(m.Id); RenderList(); RenderDetail(); };
            DockPanel.SetDock(chev, Dock.Left);
            line.Children.Add(chev);
            // on / off
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = m.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = m.Name };
            sw.Click += (s, e) =>
            {
                bool on = sw.IsChecked == true;
                // turned on from the library: it goes into the profile in use, on
                if (on && st.ProfilesOn && !m.InProfile) { Run("profile-add", m.Id); return; }
                Change(m.Id, c => c.Enabled = on, on ? "enable" : "disable", m.Id);
            };
            // outside the profile in use (the library on show): faint, its switch says it is off
            if (st.ProfilesOn && !m.InProfile) { item.Opacity = 0.55; sw.ToolTip = Texts.Get("pf_outside"); }
            DockPanel.SetDock(sw, Dock.Left);
            line.Children.Add(sw);
            switches[m.Id] = sw;
            // the right column: size, then how it is deployed (the file count is a
            // detail nobody plays with: it stays on the mod's own page)
            var how = new StackPanel { Width = 104, VerticalAlignment = VerticalAlignment.Center };
            how.Children.Add(new TextBlock { Text = Texts.Size(m.DeployBytes > 0 ? m.DeployBytes : m.LibraryBytes), FontFamily = Mono, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right });
            var (stText, stColour) = StatusText(m);
            how.Children.Add(new TextBlock { Text = stText, FontFamily = Mono, FontSize = 12, Foreground = B(stColour), HorizontalAlignment = HorizontalAlignment.Right });
            DockPanel.SetDock(how, Dock.Right);
            line.Children.Add(how);
            // this one mod's update check, where its file count used to be (the user,
            // 2026-10-24): one small mark, not a button the size of the row. It keeps the
            // colour of the last answer, which its tip spells out
            bool asking = checkingOne.Contains(m.Id);
            var said = Checked(m);
            bool newer = said?.Status == "update", done = updated.Contains(m.Id) || said?.Status == "current";
            var ask = new Button
            {
                Width = 36, Height = 36, Cursor = Cursors.Hand, Template = Plain(), FocusVisualStyle = null,
                IsEnabled = !asking && !checking, Opacity = asking ? 0.4 : 1, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = newer ? Texts.F("up_row_get", said.Latest ?? "")
                        : done ? Texts.Get(updated.Contains(m.Id) ? "up_row_done" : "up_row_current") + " · " + Texts.Get("up_check_row_tip")
                        : said == null ? Texts.Get("up_check_row_tip")
                        : Texts.Get("up_row_" + said.Status) + " · " + Texts.Get("up_check_row_tip"),
                Content = new Path
                {
                    Data = Geometry.Parse(newer ? GlyphDown : done ? GlyphTick : GlyphCheck),
                    Width = 18, Height = 18, Stretch = Stretch.Uniform, Margin = new Thickness(9),
                    Stroke = B(newer ? "Yellow" : done ? "Blue" : "Muted"),
                    StrokeThickness = 2, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round,
                },
            };
            // the mark is the whole thing: it checks, and then installs what the check found
            ask.Click += (s, e) =>
            {
                // the mod checked is the one shown: its result is in the details, and showing
                // another mod's left it out of sight until the row was clicked (the user, 2026-10-25)
                if (selectedId != m.Id)
                {
                    Current(m.Id);
                    RenderList();
                    RenderDetail();
                }
                if (newer) ApplyUpdate(m, said); else CheckOne(m);
            };
            DockPanel.SetDock(ask, Dock.Right);
            line.Children.Add(ask);
            // the picture, the name and what is chosen
            var pick = new Button { Cursor = Cursors.Hand, Template = Plain(), FocusVisualStyle = null, HorizontalContentAlignment = HorizontalAlignment.Stretch, Opacity = m.Enabled ? 1 : 0.55, Foreground = B("Ink") };
            var who = new DockPanel { MinHeight = 56 };
            var pic = Picture(m.Icon, GlyphMod, 48, m.Enabled ? "Blue" : "Muted");
            DockPanel.SetDock(pic, Dock.Left);
            who.Children.Add(pic);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
            // the name, with nothing in front of it
            var nameLine = new DockPanel();
            // a newer version on its channel, after an update check
            if (HasUpdate(m))
            {
                var tag = new Border
                {
                    Background = B("Yellow"), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.F("up_tag_tip", updates[m.Id].Channel),
                    Child = new TextBlock { Text = Texts.Get("up_tag"), FontFamily = Mono, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("TapeInk") },
                };
                DockPanel.SetDock(tag, Dock.Left);
                nameLine.Children.Add(tag);
            }
            nameLine.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = m.Name });
            names.Children.Add(nameLine);
            // the line under the name: everything on it shares one centre, the LUA tag as
            // much as the words (the user, 2026-10-24)
            var meta = new WrapPanel();
            TextBlock Fact(string text, string colour, string tip = null) => new TextBlock
            {
                Text = text, FontSize = 12, Foreground = B(colour), ToolTip = tip,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            if (m.ShownVersion != null)
            {
                var ver = Fact(V(m.ShownVersion), "Ink", VersionTip(m));
                ver.FontFamily = Mono;
                meta.Children.Add(ver);
            }
            meta.Children.Add(Fact(m.Options.Count > 0 ? Texts.F("opts_some", m.Chosen, m.Choices) : Texts.Get("opts_none"), "Muted"));
            // a LUA tag when the mod ships a script (any option of it): here, beside the
            // other facts about the mod, not in front of its name
            // the player's own labels first, in their own colour
            foreach (var t in m.Tags)
                meta.Children.Add(new Border
                {
                    BorderBrush = B("Ink"), BorderThickness = new Thickness(1), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, -0.75, 10, 0.75),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.Get("tag_tip"),
                    Child = new TextBlock { Text = t, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center },
                });
            // a loader replaces one of the game's own scripts: its own colour, because two
            // loaders cannot both have their way (the user, 2026-10-24)
            if (m.Loader)
                meta.Children.Add(new Border
                {
                    Background = B("Ink"), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(0, -0.75, 10, 0.75),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.Get("loader_tip"),
                    Child = new TextBlock
                    {
                        Text = Texts.Get("loader_tag"), FontFamily = Mono, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("TapeInk"),
                        VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
                    },
                });
            if (m.Lua)
                meta.Children.Add(new Border
                {
                    // the box is taller than the words it stands beside, so it is nudged up by
                    // its own overhang: the ink of both then shares one middle
                    BorderBrush = B("Blue"), BorderThickness = new Thickness(1), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(0, -0.75, 10, 0.75),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.Get("lua_tip"),
                    Child = new TextBlock
                    {
                        Text = "LUA", FontFamily = Mono, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("Blue"),
                        VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
                    },
                });
            if (m.Conflicts > 0) meta.Children.Add(Fact(Texts.F("row_clash", m.Conflicts), "Yellow"));
            // the same bytes in two mods are only worth a mention (the user, 2026-10-24)
            if (m.Shared > 0) meta.Children.Add(Fact(Texts.F("row_same", m.Shared), "Muted", Texts.Get("same_tip")));
            if (m.Error != null) meta.Children.Add(new TextBlock { Text = Texts.Get("broken"), FontSize = 12, Foreground = B("Yellow") });
            // its scripts will not run, and the shortest why (the details say the rest)
            var noRun = NoRun(m);
            if (noRun != null) meta.Children.Add(Fact(noRun.Value.Text, noRun.Value.Colour));
            names.Children.Add(meta);
            who.Children.Add(names);
            pick.Content = who;
            pick.Click += (s, e) => Take(m);
            // a name of the player's own: two clicks on the mod (the user, 2026-10-24)
            pick.PreviewMouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { e.Handled = true; dragFrom = null; RenameMod(m); } };
            line.Children.Add(pick);

            if (isOpen) col.Children.Add(Options(m));
            foreach (var p in PacksOf(m)) col.Children.Add(PackLine(p));

            // dragging changes the load order (in the full list only, not the library's)
            if (filter == "all" && !viewLibrary)
            {
                item.PreviewMouseLeftButtonDown += (s, e) => { dragStart = e.GetPosition(ListArea); dragFrom = m; };
                item.PreviewMouseMove += (s, e) =>
                {
                    if (dragFrom != m || dragRow != null || e.LeftButton != MouseButtonState.Pressed) return;
                    if (Math.Abs(e.GetPosition(ListArea).Y - dragStart.Y) < 6) return;
                    BeginDrag(m, item, e.GetPosition(ListArea), e.GetPosition(item).Y);
                };
            }
            item.PreviewMouseLeftButtonUp += (s, e) => dragFrom = null;
            // built when it opens, not with the row: rows are kept while what they show does not
            // change, and a menu built with one held the labels there were then -- a label made
            // from another row's menu was missing until something drew this row again (the user,
            // 2026-10-25). The mod is looked up again too, as the row may be from an older read.
            item.ContextMenu = Menu(m);
            var id = m.Id;
            item.ContextMenuOpening += (s, e) => item.ContextMenu = Menu(st?.Mods.FirstOrDefault(x => x.Id == id) ?? m);
            return item;
        }

        /// <summary>A translation pack as one small line under the mod it translates (the user,
        /// 2026-10-26): its tick turns it on and off, its name shows its details, its menu is a row's.</summary>
        UIElement PackLine(ModRow p)
        {
            bool cur = p.Id == selectedId;
            var line = new DockPanel { Margin = new Thickness(4, 0, 16, 8), Background = Brushes.Transparent };
            // its place in the load order, under the rows' numbers: they do not seem to skip one
            var num = new TextBlock
            {
                Text = st.ProfilesOn && !p.InProfile ? "" : Num(p).ToString(), FontFamily = Mono, FontSize = 12, Foreground = B("Muted"),
                Width = 28, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 92, 0),
            };
            DockPanel.SetDock(num, Dock.Left);
            line.Children.Add(num);
            var tick = new CheckBox { Style = (Style)FindResource("Tick"), IsChecked = p.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = p.Name };
            tick.Click += (s, e) => { bool on = tick.IsChecked == true; Change(p.Id, c => c.Enabled = on, on ? "enable" : "disable", p.Id); };
            DockPanel.SetDock(tick, Dock.Left);
            line.Children.Add(tick);
            var name = new Button { Template = Plain(), Cursor = Cursors.Hand, FocusVisualStyle = null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var words = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = p.Enabled ? 1 : 0.55 };
            words.Inlines.Add(new System.Windows.Documents.Run(Texts.Get("pack_line") + "  ·  ") { Foreground = B("Muted") });
            words.Inlines.Add(new System.Windows.Documents.Run(p.Name) { Foreground = B(cur ? "Yellow" : "Ink"), FontWeight = cur ? FontWeights.SemiBold : FontWeights.Normal });
            var noRun = NoRun(p);
            if (noRun != null) words.Inlines.Add(new System.Windows.Documents.Run("   " + noRun.Value.Text) { Foreground = B(noRun.Value.Colour) });
            name.Content = words;
            name.Click += (s, e) => { Current(p.Id); RenderList(); RenderDetail(); };
            line.Children.Add(name);
            line.ContextMenu = Menu(p);
            var id = p.Id;
            line.ContextMenuOpening += (s, e) => line.ContextMenu = Menu(st?.Mods.FirstOrDefault(x => x.Id == id) ?? p);
            return line;
        }

        /// <summary>A row clicked: on its own it is the one shown; with Ctrl it joins or leaves
        /// the mods taken; with Shift every row from the one shown down to it is taken.</summary>
        void Take(ModRow m)
        {
            // in the groups, the column the mod is in, in its order: Shift took the load order's
            // range, mods of other groups out of sight among them, and a drag moved them all
            // (the user, 2026-10-27)
            var mods = groupView ? GroupShown().Where(x => x.Group == m.Group).ToList() : Seen().ToList();
            var keys = Keyboard.Modifiers;
            if ((keys & ModifierKeys.Shift) != 0 && selectedId != null)
            {
                int from = mods.FindIndex(x => x.Id == selectedId), to = mods.FindIndex(x => x.Id == m.Id);
                if (from >= 0 && to >= 0)
                {
                    picked.Clear();
                    for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) picked.Add(mods[i].Id);
                }
            }
            else if ((keys & ModifierKeys.Control) != 0)
            {
                if (!picked.Remove(m.Id))
                {
                    if (picked.Count == 0 && selectedId != null && mods.Any(x => x.Id == selectedId)) picked.Add(selectedId);
                    picked.Add(m.Id);
                    selectedId = m.Id;
                }
            }
            else
            {
                picked.Clear();
                if (selectedId == m.Id) { RenderList(); return; }
                selectedId = m.Id;
                descOpen = false;
            }
            if (picked.Count == 1) picked.Clear();
            RenderList();
            RenderDetail();
        }

        /// <summary>The row whose details are shown, moved by something other than a click on
        /// the list: the chevron, a drag, a download, a jump to a mod with an update. A row taken
        /// up this way is not part of a group that was picked before it, and leaving the group
        /// behind it would show three rows marked and act on two (the user, 2026-10-25).</summary>
        void Current(string id)
        {
            if (id != null && !picked.Contains(id)) picked.Clear();
            selectedId = id;
        }

        /// <summary>The mods taken together, in load order, when this one is among them.</summary>
        /// <summary>The mods an action on m acts on: the ones taken with it, else m alone. In the
        /// groups only those of m's own column, and shown: a drag must never move mods out of
        /// sight (the user, 2026-10-27: other groups' mods were moved into the one dropped on).</summary>
        List<ModRow> Taken(ModRow m)
        {
            if (picked.Count <= 1 || !picked.Contains(m.Id)) return new List<ModRow> { m };
            var taken = st.Mods.Where(x => picked.Contains(x.Id));
            if (groupView) taken = taken.Where(x => x.Group == m.Group && Shown(x));
            return taken.ToList();
        }

        void ClearDrop()
        {
            if (dropRow == null) return;
            dropRow.BorderThickness = new Thickness(0, 0, 0, 1);
            dropRow.BorderBrush = B("Grid");
            dropRow = null;
        }

        // ---------------------------------------------------------------- dragging a row

        // While a row is dragged it follows the pointer: the row itself stays where it is,
        // dimmed, and its picture is carried on DragLayer (the user, 2026-10-24: the row has
        // to follow the pointer, not jump only once the button is let go). The mouse is this
        // window's meanwhile, so no OLE drag runs and dropping files in still works.
        Border dragRow;          // the row being dragged
        ModRow dragMod;          // the mod it is
        List<ModRow> dragBlock = new List<ModRow>();   // every mod carried: the ones taken together when it is one of them, in load order
        UIElement ghost;         // its picture, under the pointer
        double grabY;            // where inside the row the pointer took hold
        ModRow dropAnchor;       // the mod the block would land next to; null = it would stay where it is
        bool dropAfter;          // after it in the load order, else before
        double edgeBy;           // pixels per tick while the pointer is at the list's edge
        readonly DispatcherTimer edgeScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

        /// <summary>The row's picture, taken before it is dimmed.</summary>
        UIElement Ghost(Border row)
        {
            double w = row.ActualWidth, h = row.ActualHeight;
            var dpi = VisualTreeHelper.GetDpi(row);
            var drawn = new DrawingVisual();
            using (var dc = drawn.RenderOpen())
                dc.DrawRectangle(new VisualBrush(row), null, new Rect(0, 0, w, h));
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
                                             dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(drawn);
            bmp.Freeze();
            // the ghost must hide the row it is over, so its own ground is solid: the window's
            // is see-through while the acrylic backdrop is on
            var ground = (B("Ground700") as SolidColorBrush)?.Color ?? Colors.Black;
            ground.A = 0xFF;
            var inside = new Grid();
            inside.Children.Add(new Image { Source = bmp, Width = w, Height = h, Stretch = Stretch.Fill });
            // several carried: how many, on the picture's top edge, over no text (the user, 2026-10-25)
            if (dragBlock.Count > 1)
                inside.Children.Add(new Border
                {
                    Background = B("Yellow"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, -11, 16, 0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = Texts.F("drag_n", dragBlock.Count), Foreground = B("TapeInk"), FontWeight = FontWeights.SemiBold, FontSize = 12 },
                });
            return new Border
            {
                Width = w, Height = h, Background = new SolidColorBrush(ground),
                BorderBrush = B("Yellow"), BorderThickness = new Thickness(1),
                Child = inside,
            };
        }

        /// <summary>The rows of the mods carried, dimmed while they are, or bright again.</summary>
        void DimCarried(double opacity)
        {
            foreach (var x in dragBlock)
                if (rows.TryGetValue(x.Id, out var r)) r.Row.Opacity = opacity;
            if (dragRow != null) dragRow.Opacity = opacity;
        }

        void BeginDrag(ModRow m, Border row, Point at, double inRow, bool grab = true)
        {
            if (row.ActualWidth < 1 || row.ActualHeight < 1) return;
            dragFrom = null;
            dragRow = row;
            dragMod = m;
            // a row of the mods taken together carries all of them (the user, 2026-10-25)
            dragBlock = Taken(m);
            grabY = inRow;
            ghost = Ghost(row);
            DragLayer.Children.Add(ghost);
            DimCarried(0.3);
            if (grab) Mouse.Capture(ListArea, CaptureMode.SubTree);
            DragAt(at);
        }

        /// <summary>The picture put under the pointer, the place it would land marked, and the
        /// list scrolled on while the pointer rests against its top or bottom edge.</summary>
        void DragAt(Point at)
        {
            if (ghost == null) return;
            double h = ((FrameworkElement)ghost).Height;
            Canvas.SetLeft(ghost, 1);
            Canvas.SetTop(ghost, Math.Max(0, Math.Min(ListArea.ActualHeight - h, at.Y - grabY)));
            // the row the pointer is over, and which side of its middle
            Border over = null;
            ModRow onto = null;
            bool below = false;
            foreach (var child in List.Children)
            {
                if (!(child is Border r) || !(r.Tag is ModRow x)) continue;
                double top = r.TranslatePoint(new Point(0, 0), ListArea).Y;
                onto = x; over = r; below = at.Y > top + r.ActualHeight / 2;
                if (at.Y < top + r.ActualHeight) break;
            }
            dropAnchor = null;
            if (onto != null && !dragBlock.Any(x => x.Id == onto.Id))
            {
                // below on screen is later in the load order, unless the list is shown the other way round
                bool later = Texts.TopWins ? !below : below;
                // a place that leaves the order as it is is no place to drop
                if (!Moved(st.Mods, dragBlock, onto, later).SequenceEqual(st.Mods)) { dropAnchor = onto; dropAfter = later; }
            }
            if (dropAnchor == null || over == null) ClearDrop();
            else
            {
                ClearDrop();
                dropRow = over;
                over.BorderThickness = below ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 1);
                over.BorderBrush = B("Yellow");
            }
            edgeBy = at.Y < 40 ? -10 : at.Y > ListArea.ActualHeight - 40 ? 10 : 0;
            if (edgeBy == 0) edgeScroll.Stop();
            else if (!edgeScroll.IsEnabled) edgeScroll.Start();
        }

        /// <summary>The load order with the block taken out and put back, in its own order, next to
        /// anchor. By id: a row on screen may hold an older read of its mod than the order does.</summary>
        static List<ModRow> Moved(List<ModRow> order, List<ModRow> block, ModRow anchor, bool after)
        {
            var ids = new HashSet<string>(block.Select(x => x.Id));
            var carried = order.Where(x => ids.Contains(x.Id)).ToList();
            var rest = order.Where(x => !ids.Contains(x.Id)).ToList();
            int at = rest.FindIndex(x => x.Id == anchor.Id);
            if (at < 0) return order;
            rest.InsertRange(at + (after ? 1 : 0), carried);
            return rest;
        }

        /// <summary>The drag over: the mods carried put where they were let go (commit), or left alone.</summary>
        void EndDrag(bool commit)
        {
            if (dragRow == null) return;
            edgeScroll.Stop();
            DimCarried(1);
            if (ghost != null) DragLayer.Children.Remove(ghost);
            ghost = null;
            var moving = dragMod;
            var block = dragBlock;
            var anchor = dropAnchor;
            bool after = dropAfter;
            dragRow = null; dragMod = null; dragFrom = null; dropAnchor = null; dragBlock = new List<ModRow>();
            ClearDrop();
            if (Mouse.Captured == ListArea) Mouse.Capture(null);
            if (!commit || moving == null || anchor == null) return;
            Current(moving.Id);
            var args = new List<string> { "move" };
            args.AddRange(block.Select(x => x.Id));
            args.Add(after ? "--after" : "--before");
            args.Add(anchor.Id);
            Change(moving.Id, c =>
            {
                st.Mods = Moved(st.Mods, block, anchor, after);
                for (int k = 0; k < st.Mods.Count; k++) st.Mods[k].Index = k + 1;
            }, args.ToArray());
        }

        static (string, string) StatusText(ModRow m)
        {
            switch (m.Status)
            {
                case "broken": return (Texts.Get("st_broken"), "Yellow");
                case "off": return (Texts.Get("st_off"), "Muted");
                case "pending": return (Texts.Get("st_pending"), "Yellow");
                case "link": return (Texts.Get("st_link"), "Blue");
                case "copy": return (Texts.Get("st_copy"), "Muted");
                case "partial": return (Texts.Get("st_partial"), "Blue");
                default: return (Texts.Get("st_keep"), "Muted");
            }
        }

        /// <summary>An open mod's options, as the board's cards: a manifest option with
        /// sub-options is a group (pick one), the options without
        /// any are one group of parts that can each be on or off.</summary>
        UIElement Options(ModRow m)
        {
            var box = new StackPanel { Margin = new Thickness(72, 4, 16, 16) };
            if (m.Options.Count == 0)
            {
                box.Children.Add(new TextBlock { Text = Texts.Get("no_options"), FontSize = 12, Foreground = B("Muted") });
                return box;
            }
            var parts = new List<(Option, int)>();
            for (int i = 0; i < m.Options.Count; i++)
            {
                var o = m.Options[i];
                string oref = (i + 1).ToString();
                if (o.Subs.Count == 0) { parts.Add((o, i)); continue; }
                var head = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
                var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = o.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                int oi = i;
                sw.Click += (s, e) =>
                {
                    bool on = sw.IsChecked == true;
                    Change(m.Id, c => c.Options[oi].Enabled = on, "set", m.Id, oref, on ? "on" : "off");
                };
                DockPanel.SetDock(sw, Dock.Left);
                head.Children.Add(sw);
                var t = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                t.Children.Add(new TextBlock { Text = o.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
                t.Children.Add(new TextBlock { Text = Texts.Get("rule_one"), FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Bottom });
                head.Children.Add(t);
                box.Children.Add(head);
                if (!string.IsNullOrWhiteSpace(o.Description)) box.Children.Add(new TextBlock { Text = o.Description, FontSize = 12, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 8) });
                var cards = new WrapPanel { IsEnabled = o.Enabled, Opacity = o.Enabled ? 1 : 0.45 };
                for (int j = 0; j < o.Subs.Count; j++)
                {
                    var sub = o.Subs[j];
                    int sj = j;
                    string sref = (i + 1) + "." + (j + 1);
                    cards.Children.Add(Card(sub.Name, sub.Icon, sub.Files, sub.Bytes, sub.Enabled, sub.Description, () =>
                    {
                        // one of a kind: the sub-options replace the same resources (the user, 2026-10-27)
                        bool was = sub.Enabled;
                        if (!was)
                            Change(m.Id, c => { for (int k = 0; k < c.Options[oi].Subs.Count; k++) c.Options[oi].Subs[k].Enabled = k == sj; },
                                   "set", m.Id, sref, "on", "--only");
                    }));
                }
                box.Children.Add(cards);
            }
            if (parts.Count > 0)
            {
                var t = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
                t.Children.Add(new TextBlock { Text = Texts.Get("extras"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
                t.Children.Add(new TextBlock { Text = Texts.F("rule_any", parts.Count(p => p.Item1.Enabled), parts.Count), FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Bottom });
                box.Children.Add(t);
                var cards = new WrapPanel();
                foreach (var (o, i) in parts)
                {
                    string oref = (i + 1).ToString();
                    cards.Children.Add(Card(o.Name, o.Icon, o.Files, o.Bytes, o.Enabled, o.Description, () =>
                    {
                        bool was = o.Enabled;
                        Change(m.Id, c => c.Options[i].Enabled = !was, "set", m.Id, oref, was ? "off" : "on");
                    }));
                }
                box.Children.Add(cards);
            }
            var chosenFiles = m.Files.Count;
            box.Children.Add(new TextBlock
            {
                Text = Texts.F("opt_foot", m.Chosen, Texts.N(chosenFiles), Texts.Size(m.DeployBytes)),
                FontFamily = Mono, FontSize = 12, Foreground = B("Blue"), Margin = new Thickness(0, 14, 0, 0),
            });
            return box;
        }

        Button Card(string name, string icon, int files, long bytes, bool on, string description, Action act)
        {
            // a picture when the mod has one; else only the words, all of them (the name and
            // the author's description wrap; nothing is cut or left to a tooltip)
            bool hasPicture = LoadImage(icon) != null;
            var b = new Button { Width = hasPicture ? (bigCards ? 220 : 140) : 200, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Template = Plain(), FocusVisualStyle = null, Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Top };
            var frame = new Border { BorderBrush = on ? B("Yellow") : B("Grid"), BorderThickness = new Thickness(2), Background = B("Ground900"), Padding = new Thickness(8) };
            var grid = new Grid();
            var col = new StackPanel();
            if (hasPicture)
                col.Children.Add(new Border
                {
                    Height = bigCards ? 200 : 120, BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground700"), Opacity = on ? 1 : 0.6,
                    Child = (bigCards ? Full(icon) : null) ?? Picture(icon, GlyphPart, 0, on ? "Yellow" : "Muted", 56),
                });
            // the tick sits top right: words beside it keep clear of it
            var right = hasPicture ? 0 : 26;
            col.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, hasPicture ? 8 : 0, right, 2) });
            if (!string.IsNullOrWhiteSpace(description))
                col.Children.Add(new TextBlock { Text = description.Trim(), FontSize = 12, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, hasPicture ? 0 : right, 4) });
            col.Children.Add(new TextBlock { Text = files > 0 ? Texts.F("card_meta", files, Texts.Size(bytes)) : Texts.Get("card_vanilla"), FontFamily = Mono, FontSize = 11, Foreground = B("Muted") });
            grid.Children.Add(col);
            if (on)
                grid.Children.Add(new Border
                {
                    Width = 22, Height = 22, Background = B("Yellow"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0),
                    Child = new Path { Data = Geometry.Parse("M5 12l5 5L20 7"), Stroke = B("TapeInk"), StrokeThickness = 3.5, Width = 14, Height = 14, Stretch = Stretch.Uniform },
                });
            frame.Child = grid;
            b.Content = frame;
            b.Click += (s, e) => act();
            return b;
        }

        // ---------------------------------------------------------------- the chosen mod

        void RenderDetail()
        {
            FillPreview();
            Detail.Children.Clear();
            var m = st?.Mods.FirstOrDefault(x => x.Id == selectedId);
            if (m == null && st != null && groupView && groupPath.Count > 0)
            {
                var g = st.Groups.FirstOrDefault(x => x.Id == groupPath[groupPath.Count - 1]);
                if (g != null) GroupDetail(g);
                return;
            }
            if (m == null) return;

            // the picture, the name and its files
            var head = new DockPanel();
            var pic = Picture(m.Icon, GlyphMod, 72, m.Enabled ? "Blue" : "Muted", 44);
            DockPanel.SetDock(pic, Dock.Left);
            head.Children.Add(pic);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            var title = Selectable.Text(m.Name, 22);
            title.FontWeight = FontWeights.Bold;
            names.Children.Add(title);
            // a name the player gave it: the one it came with is kept beneath (the user, 2026-10-24)
            if (m.SiteAuthor != null)
            {
                var by = Selectable.Text(Texts.F("by_author", m.SiteAuthor), 12, "Muted");
                by.Margin = new Thickness(0, 2, 0, 0);
                names.Children.Add(by);
            }
            if (m.RealName != null)
            {
                var real = Selectable.Text(Texts.F("real_name", m.RealName), 12, "Muted");
                real.Margin = new Thickness(0, 2, 0, 0);
                real.ToolTip = Texts.Get("real_name_tip");
                names.Children.Add(real);
            }
            if (m.ShownVersion != null)
            {
                var ver = Selectable.Text(V(m.ShownVersion), 14, "Ink", Mono);
                ver.ToolTip = VersionTip(m);
                ver.Margin = new Thickness(0, 2, 0, 2);
                names.Children.Add(ver);
            }
            else names.Children.Add(new TextBlock { Text = Texts.Get("ver_none"), FontFamily = Mono, FontSize = 14, Foreground = B("Muted"), ToolTip = VersionTip(m), Margin = new Thickness(0, 2, 0, 2) });
            names.Children.Add(new TextBlock { Text = Texts.F("n_files", Texts.N(m.Files.Count)) + " · " + Texts.Size(m.DeployBytes), FontFamily = Mono, FontSize = 14, FontWeight = FontWeights.Bold });
            head.Children.Add(names);
            Detail.Children.Add(head);

            // the manifest, or the site's own words when the mod folder says nothing
            bool fromSite = string.IsNullOrWhiteSpace(m.Description) && !string.IsNullOrWhiteSpace(m.SiteSummary);
            var man = new StackPanel();
            man.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left, Background = B("Yellow"), Padding = new Thickness(8, 0, 8, 0),
                Child = new TextBlock { Text = Texts.Get(fromSite ? "site_words" : "manifest"), FontFamily = Mono, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B("TapeInk") },
            });
            string desc = fromSite ? m.SiteSummary.Trim() : string.IsNullOrWhiteSpace(m.Description) ? Texts.Get("no_desc") : m.Description.Trim();
            bool longDesc = desc.Length > 90 || desc.Contains("\n");
            string descColour = string.IsNullOrWhiteSpace(desc) || (!fromSite && string.IsNullOrWhiteSpace(m.Description)) ? "Muted" : "Ink";
            bool hasDesc = descColour == "Ink";
            if (longDesc && !descOpen)
            {
                var dt = new TextBlock { Text = desc, Foreground = B(descColour), TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 12, 0, 0), MaxHeight = 42, TextTrimming = TextTrimming.WordEllipsis };
                Selectable.Menu(dt, () => desc);
                man.Children.Add(dt);
            }
            else if (hasDesc)
            {
                var dt = Selectable.Text(desc, 14, descColour, null, 21);
                dt.Margin = new Thickness(0, 12, 0, 0);
                man.Children.Add(dt);
            }
            else man.Children.Add(new TextBlock { Text = desc, Foreground = B(descColour), TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 12, 0, 0) });
            if (longDesc)
            {
                var more = new Button { Content = Texts.Get(descOpen ? "less" : "more"), Template = Plain(), Foreground = B("Blue"), FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
                more.Click += (s, e) => { descOpen = !descOpen; RenderDetail(); };
                man.Children.Add(more);
            }
            if (m.Error != null)
            {
                var err = Selectable.Text(Texts.Get("broken") + ": " + m.Error, 14, "Yellow");
                err.Margin = new Thickness(0, 10, 0, 0);
                man.Children.Add(err);
            }
            Detail.Children.Add(Panel(man, new Thickness(0, 20, 0, 0)));

            // the facts
            var facts = new Grid { Margin = new Thickness(0, 20, 0, 0) };
            facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void Fact(string key, string value, bool mono = false, bool copy = false)
            {
                int r = facts.RowDefinitions.Count;
                facts.RowDefinitions.Add(new RowDefinition());
                var k = new TextBlock { Text = Texts.Get(key), FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 3, 16, 3) };
                Grid.SetRow(k, r);
                facts.Children.Add(k);
                FrameworkElement v;
                if (copy)
                {
                    var t = Selectable.Text(value, 12, "Ink", mono ? Mono : null);
                    t.TextAlignment = TextAlignment.Right;
                    v = t;
                }
                else
                {
                    var t = new TextBlock { Text = value, FontSize = 12, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap };
                    if (mono) t.FontFamily = Mono;
                    v = t;
                }
                v.Margin = new Thickness(0, 3, 0, 3);
                v.ToolTip = value;
                Grid.SetRow(v, r);
                Grid.SetColumn(v, 1);
                facts.Children.Add(v);
            }
            // a loader has no place in the list (it goes last) and no options (the user, 2026-10-26)
            if (m.Loader) { }
            else if (st.ProfilesOn && !m.InProfile) Fact("d_order", Texts.Get("pf_outside"));
            else Fact("d_order", Texts.F("d_order_v", Num(m), st.ProfilesOn ? st.Mods.Count(x => x.InProfile && !x.Loader) : st.Mods.Count(x => !x.Loader)), true);
            if (!m.Loader) Fact("d_opts", m.Options.Count > 0 ? m.Chosen + " / " + m.Choices : "—");
            if (!m.Loader && st.Groups.Count > 0)
                Fact("d_group", m.Group != null ? string.Join(" › ", PathTo(m.Group).Select(x => x.Name)) : Texts.Get("g_top"));
            Fact("d_folder", m.Path, true, true);
            {
                // remove from the library, and open the folder. A row that does not fit wraps onto
                // the next line rather than cutting off the first button (the user, 2026-10-26); each
                // button keeps 8 px on its left, which the panel's -8 takes back at the line's start
                int r = facts.RowDefinitions.Count;
                facts.RowDefinitions.Add(new RowDefinition());
                var acts = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(-8, 0, 0, 6) };
                var removeBtn = new Button { Content = Texts.Get("remove_btn"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 4, 0, 0) };
                removeBtn.Click += (s, e) => { if (Dialog.Ask(this, Texts.F("remove_ask", m.Name), Texts.Get("remove_btn"))) Run("remove", m.Id); };
                // red: deleting from the disk cannot be undone
                var deleteBtn = new Button { Content = Texts.Get("delete_btn"), Style = (Style)FindResource("Danger"), MinHeight = 32, Margin = new Thickness(8, 4, 0, 0) };
                deleteBtn.Click += (s, e) => DeleteFromDisk(m);
                acts.Children.Add(deleteBtn);
                acts.Children.Add(removeBtn);
                if (m.Exists)
                {
                    var openBtn = new Button { Content = Texts.Get("open"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 4, 0, 0) };
                    openBtn.Click += (s, e) => OpenFolder(m.Path);
                    acts.Children.Add(openBtn);
                }
                Grid.SetRow(acts, r);
                Grid.SetColumnSpan(acts, 2);      // the whole row: three buttons do not fit the value column
                facts.Children.Add(acts);
            }
            if (!string.IsNullOrEmpty(m.Source) && m.Source != m.Path) Fact("d_source", m.Source, true, true);
            Detail.Children.Add(facts);
            // in the groups the options are chosen in the preview (Space), not here: a long list of
            // them kept the details scrolling (the user, 2026-10-27)
            if (groupView && m.Options.Count > 0)
            {
                var choose = new Button { Content = Texts.Get("pv_open"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
                choose.Click += (s, e) => { if (preview == null) OpenPreview(); else preview.Activate(); };
                Detail.Children.Add(choose);
            }
            // its scripts: what starts them, and whether they will run (the user, 2026-10-26);
            // before its updates, which matter less than whether it runs at all
            var luaCard = LuaCard(m);
            if (luaCard != null) Detail.Children.Add(luaCard);
            // a script name another mod declares too: only one of them runs (the later in the order)
            foreach (var issue in (st.Lua?.Issues ?? new List<(string Kind, List<string> Mods, string Detail)>()).Where(i => i.Kind == "same_name" && i.Mods.Contains(m.Id)))
            {
                var box = new StackPanel();
                var sameHead = Selectable.Text(Texts.Get("lua_same_title"), 14, "Yellow");
                sameHead.FontWeight = FontWeights.SemiBold;
                box.Children.Add(sameHead);
                box.Children.Add(Small(Texts.F("lua_same_info", issue.Detail), new Thickness(0, 6, 0, 0)));
                box.Children.Add(Small(Texts.Get("pick_runs_label"), new Thickness(0, 8, 0, 4)));
                box.Children.Add(WinnerRows(issue.Mods.ToList(), issue.Mods[0], "pick_runs_now"));
                Detail.Children.Add(new Border { BorderBrush = B("Yellow"), BorderThickness = new Thickness(1), Padding = new Thickness(12), Margin = new Thickness(0, 16, 0, 0), Child = box });
            }
            Detail.Children.Add(UpdatePanel(m));

            // what a deploy does to it
            if (m.Enabled && m.Changes > 0) Detail.Children.Add(Changes(m));
            else
            {
                var ok = new StackPanel();
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(m.Enabled ? "Blue" : "Muted"), Margin = new Thickness(0, 0, 8, 0) });
                line.Children.Add(new TextBlock { Text = Texts.Get(m.Enabled ? "in_sync" : "is_off"), FontWeight = FontWeights.SemiBold });
                ok.Children.Add(line);
                ok.Children.Add(Small(Texts.Get(m.Enabled ? "in_sync_n" : "is_off_n"), new Thickness(0, 8, 0, 0)));
                Detail.Children.Add(Panel(ok, new Thickness(0, 20, 0, 0)));
            }

            // conflicts, and below them the resources another mod ships identically: those
            // are said plainly, because nothing is overridden by them
            foreach (var c in st.Clashes.Where(c => c.Mods.Contains(m.Name)).OrderBy(c => c.Same))
            {
                var box = new StackPanel();
                var others = string.Join(" / ", c.Mods.Where(x => x != m.Name));
                var clash = Selectable.Text(Texts.F(c.ManagedBoot ? "managed_boot" : c.Same ? "same" : "clash", c.Count, others), 14, c.Same || c.ManagedBoot ? "Ink" : "Yellow");
                clash.FontWeight = FontWeights.SemiBold;
                box.Children.Add(clash);
                // a boot the loader coordinates has nothing to choose
                if (c.ManagedBoot) box.Children.Add(Small(Texts.Get("managed_boot_tip"), new Thickness(0, 6, 0, 0)));
                else if (c.Same) box.Children.Add(Small(Texts.Get("same_tip"), new Thickness(0, 6, 0, 0)));
                else
                {
                    // the one in effect shown at once, and chosen here by a click (the user, 2026-10-27:
                    // it took a button and a window to see it)
                    var ids = c.Mods.Select(n => st.Mods.FirstOrDefault(x => x.Name == n)?.Id).Where(id => id != null).ToList();
                    var winner = st.Mods.FirstOrDefault(x => x.Name == c.Winner)?.Id;
                    if (ids.Count > 1)
                    {
                        box.Children.Add(Small(Texts.Get("pick_wins_label"), new Thickness(0, 8, 0, 4)));
                        box.Children.Add(WinnerRows(ids, winner, "pick_now"));
                    }
                    else box.Children.Add(Small(Texts.F("clash_win", c.Winner), new Thickness(0, 6, 0, 0)));
                }
                // which resources (the user, 2026-10-25): type, then the name or the id
                const int lines = 8;
                var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                foreach (var r in c.Resources.Take(lines))
                {
                    // the type, then the name or the id, which can be copied without the type
                    var dp = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                    var type = new TextBlock { Text = TypeLabel(r.Type) + "  ·  ", FontSize = 13, Foreground = B("Muted") };
                    DockPanel.SetDock(type, Dock.Left);
                    dp.Children.Add(type);
                    var what = r.Name != null ? Selectable.Text(r.Name, 13) : Selectable.Text(r.Id, 12, "Muted", Mono);
                    what.VerticalAlignment = VerticalAlignment.Center;
                    dp.Children.Add(what);
                    list.Children.Add(dp);
                }
                if (c.Count > Math.Min(lines, c.Resources.Count))
                    list.Children.Add(Small(Texts.F("clash_more", c.Count - Math.Min(lines, c.Resources.Count)), new Thickness(0, 2, 0, 0)));
                if (list.Children.Count > 0) box.Children.Add(list);
                if (!st.ResourceNames && c.Resources.Count > 0 && Texts.Hints)
                    box.Children.Add(Small(Texts.Get("clash_no_names"), new Thickness(0, 8, 0, 0)));
                Detail.Children.Add(new Border { BorderBrush = B(c.Same || c.ManagedBoot ? "Grid" : "Yellow"), BorderThickness = new Thickness(1), Padding = new Thickness(12), Margin = new Thickness(0, 16, 0, 0), Child = box });
            }

            // how it is deployed
            var dep = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
            dep.Children.Add(Label("deploy_info", new Thickness(0, 0, 0, 6)));
            void Pair(string key, string value, string colour = "Ink", bool mono = false)
            {
                var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var v = new TextBlock { Text = value, FontSize = 12, Foreground = B(colour) };
                if (mono) v.FontFamily = Mono;
                DockPanel.SetDock(v, Dock.Right);
                dp.Children.Add(v);
                dp.Children.Add(new TextBlock { Text = Texts.Get(key), FontSize = 12, Foreground = B("Muted") });
                dep.Children.Add(dp);
            }
            // only what the cards above do not say (the user, 2026-10-26: no repeats): how it sits in
            // the game while deployed; off, or waiting for a deploy, the card above says so. The
            // number of its files is at the top of the details
            bool deployed = m.Enabled && m.Changes == 0 && m.Files.Count > 0;
            if (deployed) Pair("di_mode", Texts.Get(m.Status == "link" ? "mode_link" : m.Status == "partial" ? "mode_partial" : "mode_copy"));
            Pair("di_lib", Texts.Size(m.LibraryBytes), "Ink", true);
            if (deployed)
            {
                long extra = m.Files.Where(f => f.Action == "keep" && !f.Linked).Sum(f => f.Bytes);
                Pair("di_extra", Texts.Size(extra), extra == 0 ? "Blue" : "Ink", true);
            }
            Detail.Children.Add(dep);
        }

        /// <summary>A mod's part of the next deploy: the board's update section, made of
        /// what hd2qd will keep, rename, write and delete.</summary>
        UIElement Changes(ModRow m)
        {
            var box = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
            long keep = m.Files.Where(f => f.Action == "keep").Sum(f => f.Bytes);
            long move = m.Files.Where(f => f.Action == "move").Sum(f => f.Bytes);
            long write = m.Files.Where(f => f.Action == "write").Sum(f => f.Bytes);
            int moved = m.Files.Count(f => f.Action == "move"), written = m.Files.Count(f => f.Action == "write"), kept = m.Files.Count(f => f.Action == "keep");
            box.Children.Add(new TextBlock { Text = Texts.Get("changes"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Yellow") });

            // by size
            var head = new DockPanel { Margin = new Thickness(0, 14, 0, 6) };
            var total = new TextBlock { Text = Texts.Size(keep + move + write), FontFamily = Mono, FontSize = 12, Foreground = B("Muted") };
            DockPanel.SetDock(total, Dock.Right);
            head.Children.Add(total);
            head.Children.Add(new TextBlock { Text = Texts.Get("by_size"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted") });
            box.Children.Add(head);
            var bar = new Grid { Height = 16 };
            void Part(long size, Brush fill, Brush stroke, int minPx)
            {
                if (size <= 0) return;
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(size, 1), GridUnitType.Star), MinWidth = minPx });
                var r = new Border { Background = fill, BorderBrush = stroke, BorderThickness = new Thickness(stroke == null ? 0 : 1), Margin = new Thickness(0, 0, 2, 0) };
                Grid.SetColumn(r, bar.ColumnDefinitions.Count - 1);
                bar.Children.Add(r);
            }
            var blueSoft = new SolidColorBrush(Color.FromArgb(0x59, 0x5A, 0xAA, 0xFF));
            Part(keep, blueSoft, B("Blue"), 2);
            Part(move, B("Yellow"), null, 4);
            Part(write, B("Ink"), null, 4);
            box.Children.Add(bar);
            var legend = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            legend.ColumnDefinitions.Add(new ColumnDefinition());
            legend.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            void Key(Brush fill, Brush stroke, string text, string value)
            {
                int r = legend.RowDefinitions.Count;
                legend.RowDefinitions.Add(new RowDefinition());
                var k = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                k.Children.Add(new Border { Width = 10, Height = 10, Background = fill, BorderBrush = stroke, BorderThickness = new Thickness(stroke == null ? 0 : 1), Margin = new Thickness(0, 0, 6, 0) });
                k.Children.Add(new TextBlock { Text = text, FontSize = 12 });
                Grid.SetRow(k, r);
                legend.Children.Add(k);
                var v = new TextBlock { Text = value, FontFamily = Mono, FontSize = 12 };
                Grid.SetRow(v, r);
                Grid.SetColumn(v, 1);
                legend.Children.Add(v);
            }
            Key(blueSoft, B("Blue"), Texts.Get("c_keep"), kept + " · " + Texts.Size(keep));
            Key(B("Yellow"), null, Texts.Get("c_move"), moved + " · " + Texts.Size(move));
            Key(B("Ink"), null, Texts.Get("c_write"), written + " · " + Texts.Size(write));
            box.Children.Add(legend);

            // the two numbers
            var tiles = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            tiles.ColumnDefinitions.Add(new ColumnDefinition());
            tiles.ColumnDefinitions.Add(new ColumnDefinition());
            long copyBytes = m.SameDrive ? 0 : write;
            tiles.Children.Add(Tile(Texts.Get("tile_write"), Texts.Size(copyBytes), m.SameDrive ? "Blue" : "Yellow", Texts.Get(m.SameDrive ? "tile_write_n" : "tile_write_c"), 0));
            tiles.Children.Add(Tile(Texts.Get("tile_reuse"), Texts.Size(keep + move), "Blue", Texts.F("tile_reuse_n", Texts.N(kept + moved)), 1));
            box.Children.Add(tiles);

            // the files
            box.Children.Add(new TextBlock { Text = Texts.Get("files_changed"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"), Margin = new Thickness(0, 16, 0, 6) });
            var list = new StackPanel();
            foreach (var f in m.Files.Where(f => f.Action != "keep").Take(40))
            {
                var row = new DockPanel { MinHeight = 32, Margin = new Thickness(10, 0, 10, 0) };
                bool w = f.Action == "write";
                var tag = new Border
                {
                    Width = 36, Background = w ? B("Ink") : B("Yellow"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                    Child = new TextBlock { Text = Texts.Get(w ? "tag_write" : "tag_move"), FontFamily = Mono, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B("TapeInk"), HorizontalAlignment = HorizontalAlignment.Center },
                };
                DockPanel.SetDock(tag, Dock.Left);
                row.Children.Add(tag);
                var size = new TextBlock { Text = Texts.Size(f.Bytes), FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(size, Dock.Right);
                row.Children.Add(size);
                var fileName = new TextBlock
                {
                    Text = f.Name + (f.From != null ? "  " + Texts.F("moved_from", f.From) : ""), FontFamily = Mono, FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = f.Src,
                };
                var copied = f.Name;
                Selectable.Menu(fileName, () => copied);
                row.Children.Add(fileName);
                list.Children.Add(new Border { BorderBrush = B("Ground700"), BorderThickness = new Thickness(0, 0, 0, 1), Child = row });
            }
            box.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground900"), Child = list });
            return box;
        }

        Border Tile(string label, string value, string colour, string note, int column)
        {
            var s = new StackPanel();
            s.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = B("Muted") });
            s.Children.Add(new TextBlock { Text = value, FontFamily = Mono, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = B(colour) });
            s.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap });
            var t = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground900"), Padding = new Thickness(12), Child = s, Margin = new Thickness(column == 0 ? 0 : 4, 0, column == 0 ? 4 : 0, 0) };
            Grid.SetColumn(t, column);
            return t;
        }

        // ---------------------------------------------------------------- small pieces

        TextBlock Label(string key, Thickness margin) => new TextBlock
        {
            Text = Texts.Get(key), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"), Margin = margin,
        };

        TextBlock Small(string text, Thickness margin) => new TextBlock
        {
            Text = text, FontSize = 12, LineHeight = 16, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, Margin = margin,
        };

        FrameworkElement Code(string text)
        {
            var t = Selectable.Text(text, 11, "Ink", Mono);
            t.Margin = new Thickness(0, 2, 0, 0);
            return t;
        }

        Border Panel(UIElement child, Thickness margin) => new Border
        {
            BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground900"), Padding = new Thickness(16), Margin = margin, Child = child,
        };

        /// <summary>A picture from a mod's files (read into memory, so the file is not held
        /// open: a deploy or a new version may need it), else the board's line drawing.</summary>
        readonly Dictionary<string, (DateTime Written, ImageSource Image)> images = new Dictionary<string, (DateTime, ImageSource)>();

        /// <summary>A picture decoded once (to 256 px: a mod's icon is often 1254 px and
        /// 2.5 MB) and kept until its file changes; null without one.</summary>
        ImageSource LoadImage(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            try
            {
                var written = File.GetLastWriteTimeUtc(file);
                if (written.Year < 1700) return null;                 // no such file
                if (images.TryGetValue(file, out var had) && had.Written == written) return had.Image;
                var img = Decode(file, written);
                if (img != null) images[file] = (written, img);
                return img;
            }
            catch (Exception e) when (e is IOException || e is NotSupportedException || e is UriFormatException || e is ArgumentException
                                      || e is UnauthorizedAccessException) { return null; }
        }

        /// <summary>A picture as the list shows it, on any thread: its small copy in thumbs        /// when there is one, else the picture itself, decoded to 256 px and its small copy kept.
        /// 24 pictures took 345 ms of the list's first drawing (the user, 2026-10-26: the start
        /// was slow); their small copies are a few kilobytes each.</summary>
        static ImageSource Decode(string file, DateTime written)
        {
            string thumb;
            using (var sha = System.Security.Cryptography.SHA1.Create())
                thumb = IOPath.Combine(Store.Home, "thumbs", BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(file.ToLowerInvariant() + "|" + written.Ticks))).Replace("-", "").Substring(0, 24) + ".png");
            if (File.Exists(thumb))
                try { return Open(thumb, 0); }
                catch (Exception e) when (e is IOException || e is NotSupportedException || e is ArgumentException) { }
            var bmp = Open(file, 256);
            try
            {
                Directory.CreateDirectory(IOPath.GetDirectoryName(thumb));
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bmp));
                var tmp = thumb + ".tmp";
                using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write)) png.Save(f);
                if (File.Exists(thumb)) File.Delete(tmp); else File.Move(tmp, thumb);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException || e is InvalidOperationException) { }
            return bmp;
        }

        static BitmapImage Open(string path, int width)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (width > 0) bmp.DecodePixelWidth = width;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        async void Later(Border frame, string file, DateTime written)
        {
            ImageSource img;
            try { img = await Task.Run(() => Decode(file, written)); }
            catch (Exception e) when (e is IOException || e is NotSupportedException || e is UriFormatException || e is ArgumentException
                                      || e is UnauthorizedAccessException || e is InvalidOperationException) { return; }
            if (img == null) return;
            images[file] = (written, img);
            frame.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
        }

        Border Picture(string file, string glyph, double size, string tint, double glyphSize = 28)
        {
            var frame = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground900") };
            if (size > 0) { frame.Width = size; frame.Height = size; }
            else { frame.BorderThickness = new Thickness(0); frame.Background = Brushes.Transparent; }
            frame.Child = new Path
            {
                Data = Geometry.Parse(glyph), Stroke = B(tint), StrokeThickness = 1.75, Width = glyphSize, Height = glyphSize, Stretch = Stretch.Uniform,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            if (string.IsNullOrEmpty(file)) return frame;
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(file); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException) { return frame; }
            if (written.Year < 1700) return frame;                     // no such file
            if (images.TryGetValue(file, out var had) && had.Written == written)
            {
                frame.Child = new Image { Source = had.Image, Stretch = Stretch.UniformToFill };
                return frame;
            }
            // not decoded yet: the line drawing now, the picture once it is read (the list never waits for it)
            Later(frame, file, written);
            return frame;
        }

        /// <summary>A button that draws only its content.</summary>
        static ControlTemplate Plain()
        {
            var t = new ControlTemplate(typeof(Button));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            var bg = new FrameworkElementFactory(typeof(Border));
            bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bg.AppendChild(cp);
            t.VisualTree = bg;
            return t;
        }

        ContextMenu Menu(ModRow m)
        {
            var menu = new ContextMenu();
            void Item(string key, Action act, bool danger = false)
            {
                var it = new MenuItem { Header = Texts.Get(key) };
                if (danger) it.Foreground = B("Red");
                it.Click += (s, e) => act();
                menu.Items.Add(it);
            }
            // several mods taken at once: what can be done to all of them (the user, 2026-10-24)
            var many = Taken(m);
            if (many.Count > 1)
            {
                // only the switch that does something: all on, Turn off alone; all off, Turn on alone
                // (the user, 2026-10-27: the menu had grown long)
                if (many.Any(x => !x.Enabled))
                {
                    var on = new MenuItem { Header = Texts.F("many_on", many.Count) };
                    on.Click += (s, e) => { foreach (var x in many.Where(x => !x.Enabled)) Change(x.Id, c => c.Enabled = true, "enable", x.Id); };
                    menu.Items.Add(on);
                }
                if (many.Any(x => x.Enabled))
                {
                    var off = new MenuItem { Header = Texts.F("many_off", many.Count) };
                    off.Click += (s, e) => { foreach (var x in many.Where(x => x.Enabled)) Change(x.Id, c => c.Enabled = false, "disable", x.Id); };
                    menu.Items.Add(off);
                }
                // the top and bottom of the screen: with the winner shown at the top, the other
                // ends of the load order. One move of them all, in the order they have
                string[] To(string end) => new[] { "move" }.Concat(many.Select(x => x.Id)).Concat(new[] { end }).ToArray();
                ProfileItems(menu, many);
                // the order's ends only where the order is shown: in the groups which one wins is
                // chosen in a conflict's card
                if (!groupView)
                {
                    var up = new MenuItem { Header = Texts.F("many_top", many.Count) };
                    up.Click += (s, e) => Run(To(Texts.TopWins ? "--bottom" : "--top"));
                    menu.Items.Add(up);
                    var down = new MenuItem { Header = Texts.F("many_bottom", many.Count) };
                    down.Click += (s, e) => Run(To(Texts.TopWins ? "--top" : "--bottom"));
                    menu.Items.Add(down);
                }
                menu.Items.Add(TagMenu(m));
                menu.Items.Add(ModGroupMenu(many));
                if (many.Any(x => x.Channel == "Nexus Mods" || x.Channel == "GitHub"))
                {
                    var match = new MenuItem { Header = Texts.F("many_match", many.Count) };
                    match.Click += (s, e) => MatchSource(m);
                    menu.Items.Add(match);
                }
                menu.Items.Add(new Separator());
                var gone = new MenuItem { Header = Texts.F("many_remove", many.Count) };
                gone.Click += (s, e) =>
                {
                    if (!Dialog.Ask(this, Texts.F("many_remove_ask", many.Count), Texts.Get("remove"))) return;
                    RunMany(many.Select(x => new[] { "remove", x.Id }).ToList());
                };
                menu.Items.Add(gone);
                // the files themselves, last and in red (the user, 2026-10-25): the same question
                // and the same deletion as the ticked mods on the storage page
                var del = new MenuItem { Header = Texts.F("many_delete", many.Count), Foreground = B("Red") };
                del.Click += (s, e) =>
                {
                    var ids = new HashSet<string>(many.Select(x => x.Id));
                    DeleteTicked(Library.Load().Mods.Where(x => ids.Contains(x.Id)).ToList());
                };
                menu.Items.Add(del);
                return menu;
            }
            // opening the folder and matching the source are on the mod's own page (the user,
            // 2026-10-24: the menu had grown too long); removing and deleting are here too, as for
            // several mods (the user, 2026-10-27)
            ProfileItems(menu, Taken(m));
            Item("rename", () => RenameMod(m));
            menu.Items.Add(TagMenu(m));
            menu.Items.Add(ModGroupMenu(new List<ModRow> { m }));
            // the top and bottom of the screen, as the list is shown
            bool topWins = Texts.TopWins;
            if (!groupView)
            {
                Item(topWins ? "top_wins_top" : "top", () => Run("move", m.Id, topWins ? "--bottom" : "--top"));
                Item(topWins ? "top_wins_bottom" : "bottom", () => Run("move", m.Id, topWins ? "--top" : "--bottom"));
            }
            menu.Items.Add(new Separator());
            Item("replace_zip", () => { var p = PickZip(); if (p != null) Run("replace", m.Id, p); });
            Item("replace_folder", () => { var p = PickFolder(); if (p != null) Run("replace", m.Id, p); });
            menu.Items.Add(new Separator());
            // the same questions as the details' buttons, asked once the menu has closed
            Item("remove_menu", () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Dialog.Ask(this, Texts.F("remove_ask", m.Name), Texts.Get("remove_btn"))) Run("remove", m.Id);
            }), DispatcherPriority.ContextIdle));
            Item("delete_menu", () => Dispatcher.BeginInvoke(new Action(() => DeleteFromDisk(m)), DispatcherPriority.ContextIdle), true);
            return menu;
        }

/// <summary>The labels, as a menu of its own: every label there is, lit when the mods taken
        /// all carry it, and a line at the end to make a new one. A list of them in a question was
        /// no good with ten of them (the user, 2026-10-24).</summary>
        MenuItem TagMenu(ModRow m)
        {
            var many = Taken(m);
            var top = new MenuItem
            {
                Header = many.Count > 1 ? Texts.F("many_tag", many.Count) : Texts.Get("tag_add"),
                Style = (Style)FindResource("SubMenuItem"),
            };
            foreach (var (name, _) in AllTags())
            {
                bool all = many.All(x => Carries(x, name, false));
                var it = new MenuItem { Header = name, IsCheckable = true, IsChecked = all };
                var tag = name;
                it.Click += (s, e) => PutTag(many, tag, !all);
                top.Items.Add(it);
            }
            if (top.Items.Count > 0) top.Items.Add(new Separator());
            var make = new MenuItem { Header = Texts.Get("tag_new_dlg") };
            make.Click += (s, e) =>
            {
                var name = Dialog.AskText(this, Texts.Get("tag_new_ask"), Texts.Get("tag_new_save"), "");
                if (name == null || name.Trim().Length == 0) return;
                PutTag(many, name.Trim(), true);
            };
            top.Items.Add(make);
            return top;
        }

        /// <summary>A label put on the mods, or taken off all of them.</summary>
        void PutTag(List<ModRow> mods, string tag, bool on)
        {
            foreach (var x in mods)
            {
                bool has = Carries(x, tag, false);
                if (on && !has) Change(x.Id, c => c.Tags.Add(tag), "tag", x.Id, tag);
                else if (!on && has) Change(x.Id, c => c.Tags.RemoveAll(t => string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase)), "untag", x.Id, tag);
            }
        }

                /// <summary>A name of the player's own for a mod, asked for with the current one in the
        /// box. The mod's folder and files are left alone; the name it came with is kept and the
        /// details show it. An empty box, or the same name, changes nothing.</summary>
        void RenameMod(ModRow m)
        {
            var name = Dialog.AskText(this, Texts.Get("rename_ask"), Texts.Get("rename_save"), m.Name);
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0 || name == m.Name) return;
            Run("rename", m.Id, name);
        }

        /// <summary>A mod off the disk for good, after a red question that names its folder:
        /// out of the library and the game (a deploy), its folder deleted.</summary>
        void DeleteFromDisk(ModRow m)
        {
            if (!Dialog.AskDanger(this, Texts.F("delete_ask", m.Name, m.Path), Texts.Get("delete_btn"))) return;
            var id = m.Id;
            _ = Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var mod = lib.Mods.FirstOrDefault(x => x.Id == id) ?? throw new ModError("not in the library any more: " + m.Name);
                var r = Ops.DeleteMod(lib, mod, Store.GameDir());
                return Texts.F("deleted_mod", mod.Name, Texts.Size(r.Bytes));
            }), true);
        }

        // ---------------------------------------------------------------- the buttons

        // ---------------------------------------------------------------- update checks

        // what each mod was when it was checked: a result stops counting once the mod changes
        readonly Dictionary<string, string> checkedAs = new Dictionary<string, string>();

        static string CheckedAs(string path, string version, string page) => path + "\u001f" + version + "\u001f" + page;

        /// <summary>The last check's result for the mod as it is now; null when not checked or since changed.</summary>
        UpdateInfo Checked(ModRow m) =>
            updates.TryGetValue(m.Id, out var u) && checkedAs.TryGetValue(m.Id, out var was) && was == CheckedAs(m.Path, m.Version, m.Page) ? u : null;

        bool HasUpdate(ModRow m) => Checked(m)?.Status == "update";

        /// <summary>Every mod checked on its own channel (Nexus Mods, GitHub), the window kept usable
        /// meanwhile; then the mods with updates are listed and a summary is shown.</summary>
        async void Updates_Click(object sender, RoutedEventArgs e)
        {
            if (checking || busy) return;
            checking = true;
            UpdatesBtn.Content = Texts.Get("up_checking_btn");
            UpdatesBtn.IsEnabled = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            List<UpdateInfo> found = null;
            var was = new Dictionary<string, string>();
            string failed = null;
            try
            {
                // the origins HD2 Arsenal recorded are learnt first, in turn with the other commands
                var lib = await Queued(() =>
                {
                    var l = Library.Load();
                    if (Origins.FromArsenal(l) > 0) l.Save();
                    return l;
                });
                foreach (var m in lib.Mods) was[m.Id] = CheckedAs(m.Path, m.Nexus?.Version ?? m.GitHub?.Tag, Origins.Of(m).Page);
                found = await Task.Run(() => Updates.Check(lib, (i, n) =>
                    Dispatcher.BeginInvoke(new Action(() => Foot(Texts.F("up_progress", i, n), "Blue")))));
            }
            catch (Exception x) when (x is ModError || x is IOException || x is FormatException || x is UnauthorizedAccessException) { failed = x.Message; }
            finally
            {
                checking = false;
                UpdatesBtn.IsEnabled = true;
                UpdatesBtn.Content = Texts.Get("up_check");
            }
            if (failed != null) { Foot(Texts.F("failed", failed), "Yellow"); Dialog.Tell(this, Texts.F("failed", failed)); return; }

            updates.Clear();
            checkedAs.Clear();
            updated.Clear();
            foreach (var u in found)
            {
                updates[u.ModId] = u;
                checkedAs[u.ModId] = was[u.ModId];
            }
            int Count(string status, Func<UpdateInfo, bool> also = null) => found.Count(u => u.Status == status && (also == null || also(u)));
#if HAS_NEXUS
            int upd = Count("update"), noKey = Count("error", u => u.Why == "no_key"), err = Count("error", u => u.Why != "no_key"),
                none = Count("no_source"), other = Count("no_check");
#else
            int upd = Count("update"), err = Count("error"), none = Count("no_source"), other = Count("no_check");
#endif
            Foot(Texts.F("up_foot", upd, Seconds(clock.Elapsed.TotalSeconds)), upd > 0 ? "Yellow" : "Blue");
            if (upd > 0)
            {
                filter = "updates";
                Current(found.First(u => u.Status == "update").ModId);
            }
            if (st != null) { RenderNav(); RenderList(); RenderDetail(); }

            var text = Texts.F("up_summary", found.Count, upd, Count("current"), Count("unknown"), none);
#if HAS_NEXUS
            if (noKey > 0) text += "\n\n" + Texts.F("up_summary_key", noKey);
#endif
            if (other > 0) text += "\n\n" + Texts.F("up_summary_other", other);
            if (err > 0) text += "\n\n" + Texts.F("up_summary_err", err);
            if (none > 0) text += "\n\n" + Texts.Get("up_summary_source");
#if HAS_NEXUS
            if (noKey > 0)
            {
                if (Dialog.Choose(this, text, Texts.Get("up_set_key"), Texts.Get("ok")) == 0 && await AskKey()) Updates_Click(sender, e);
            }
            else Dialog.Tell(this, text);
#else
            Dialog.Tell(this, text);
#endif
        }

        // the mods whose own check is running: their row says so meanwhile
        readonly HashSet<string> checkingOne = new HashSet<string>();

        // the mods updated while this window has been open: their mark is a tick (the user,
        // 2026-10-24), which the next check takes away again
        readonly HashSet<string> updated = new HashSet<string>();

        /// <summary>One mod checked on its own channel, from its own row (the user, 2026-10-24).
        /// The row says "checking", then keeps the answer; the window stays usable and the other
        /// rows keep theirs. What came back is said in the footer, not in a window to close.</summary>
        async void CheckOne(ModRow m)
        {
            if (checking || !checkingOne.Add(m.Id)) return;
            RenderList();
            RenderDetail();
            string was = null, failed = null;
            UpdateInfo u = null;
            try
            {
                // the origin HD2 Arsenal recorded is learnt first, as the whole library's check does
                var mod = await Queued(() =>
                {
                    var l = Library.Load();
                    if (Origins.FromArsenal(l) > 0) l.Save();
                    return l.Mods.FirstOrDefault(x => x.Id == m.Id);
                });
                if (mod == null) return;
                was = CheckedAs(mod.Path, mod.Nexus?.Version ?? mod.GitHub?.Tag, Origins.Of(mod).Page);
                u = await Task.Run(() => Updates.One(mod));
            }
            catch (Exception x) when (x is ModError || x is IOException || x is FormatException || x is UnauthorizedAccessException) { failed = x.Message; }
            finally { checkingOne.Remove(m.Id); }
            if (failed != null)
            {
                Foot(Texts.F("failed", failed), "Yellow");
                if (st != null) { RenderList(); RenderDetail(); }
                return;
            }
            updates[u.ModId] = u;
            checkedAs[u.ModId] = was;
            updated.Remove(u.ModId);
            Foot(Texts.F("up_one_foot", m.Name, Texts.Get("up_row_" + u.Status)), u.Status == "update" ? "Yellow" : "Blue");
            if (st != null) { RenderNav(); RenderList(); RenderDetail(); }
        }

        /// <summary>The details' update section: where the mod comes from, its version, the last
        /// check's result, and what can be done (update, open its page, set its source).</summary>
        /// <summary>Buttons in two columns of the same width, in the order given; a last one
        /// with no partner takes the whole width, so no row is left ragged.</summary>
        UIElement Columns(List<Button> buttons, Thickness margin)
        {
            var grid = new Grid { Margin = margin };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < buttons.Count; i++)
            {
                bool alone = i == buttons.Count - 1 && i % 2 == 0;
                int row = i / 2, col = i % 2;
                while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition());
                var b = buttons[i];
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.Margin = new Thickness(alone || col == 0 ? 0 : 4, row == 0 ? 0 : 8, alone || col == 1 ? 0 : 4, 0);
                Grid.SetRow(b, row);
                Grid.SetColumn(b, col);
                if (alone) Grid.SetColumnSpan(b, 2);
                grid.Children.Add(b);
            }
            return grid;
        }

        UIElement UpdatePanel(ModRow m)
        {
            var box = new StackPanel();
            box.Children.Add(Label("up_section", new Thickness(0, 0, 0, 6)));
            void Line(string key, string value, string tip = null)
            {
                var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var v = new TextBlock { Text = value, FontSize = 12, FontFamily = Mono, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = tip ?? value, Margin = new Thickness(16, 0, 0, 0) };
                DockPanel.SetDock(v, Dock.Right);
                dp.Children.Add(v);
                dp.Children.Add(new TextBlock { Text = Texts.Get(key), FontSize = 12, Foreground = B("Muted") });
                box.Children.Add(dp);
            }
            // no source: the sentence below says so. The installed version is at the top of the
            // details, so it is not said again here (the user, 2026-10-26: no repeats)
            if (m.Channel != null) Line("d_channel", m.Channel);
            // what the source's page said its newest was, when it was last asked: never the
            // installed version, and only shown when it is not the same (the user, 2026-10-25)
            if (!string.IsNullOrWhiteSpace(m.SiteVersion) &&
                !string.Equals((m.SiteVersion ?? "").TrimStart('v', 'V'), (m.ShownVersion ?? "").TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase))
                Line("d_site_version", V(m.SiteVersion));

            var u = Checked(m);
            string text, colour = "Muted";
            bool apply = false;
            if (u == null) text = m.Channel == null ? Texts.Get("up_no_source") : Texts.Hints ? Texts.Get("up_not_checked") : "";
            else switch (u.Status)
            {
                case "update":
                    text = string.IsNullOrEmpty(u.Current) ? Texts.F("up_new_unknown", u.Latest) : Texts.F("up_new", u.Current, u.Latest);
                    colour = "Yellow";
                    apply = true;
                    break;
                case "unknown":
                    text = Texts.F("up_new_unknown", u.Latest ?? "?");
                    colour = "Ink";
                    apply = u.Latest != null;
                    break;
                case "current": text = Texts.Get("up_current"); colour = "Blue"; break;
                case "no_source": text = Texts.Get("up_no_source"); break;
                case "no_check": text = Texts.F("up_no_check", u.Channel); break;
#if HAS_NEXUS
                default: text = u.Why == "no_key" ? Texts.Get("up_no_key") : Texts.F("up_error", CheckWhy(u.Why)); colour = "Yellow"; break;
#else
                default: text = Texts.F("up_error", CheckWhy(u.Why)); colour = "Yellow"; break;
#endif
            }
            // a GitHub release without an archive to download: its page only
            if (apply && m.Channel == "GitHub" && string.IsNullOrEmpty(u.Download)) apply = false;
            if (text.Length > 0)
            {
                var said = Selectable.Text(text, 12, colour);
                said.Margin = new Thickness(0, 8, 0, 0);
                box.Children.Add(said);
            }

            // the buttons in two columns of the same width, the odd one last across both:
            // a row of them wrapped into a ragged second line (the user, 2026-10-24)
            var buttons = new List<Button>();
            var check = new Button
            {
                Content = Texts.Get(checkingOne.Contains(m.Id) ? "up_checking_btn" : "up_check"),
                Style = (Style)FindResource("Ghost"), MinHeight = 34,
                IsEnabled = !checking && !checkingOne.Contains(m.Id),
                ToolTip = Texts.Get("up_check_row_tip"),
            };
            check.Click += (s, e) => CheckOne(m);
            buttons.Add(check);
            if (apply)
            {
                var go = new Button { Content = Texts.Get(u.Status == "update" ? "up_apply" : "up_apply_latest"), Style = (Style)FindResource("Primary"), MinHeight = 34 };
                go.Click += (s, e) => ApplyUpdate(m, u);
                buttons.Add(go);
            }
            var page = u?.Page ?? m.Page;
            if (page != null)
            {
                var see = new Button { Content = Texts.Get("up_page"), Style = (Style)FindResource("Ghost"), MinHeight = 34 };
                see.Click += (s, e) => Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
                buttons.Add(see);
            }
            if (m.Channel != null)
            {
                var again = new Button { Content = Texts.Get("match"), Style = (Style)FindResource("Ghost"), MinHeight = 34, ToolTip = Texts.Get("match_tip") };
                again.Click += (s, e) => MatchSource(m);
                buttons.Add(again);
            }
            var set = new Button { Content = Texts.Get("up_set_source"), Style = (Style)FindResource("Ghost"), MinHeight = 34 };
            set.Click += (s, e) => SetSource(m);
            buttons.Add(set);
            box.Children.Add(Columns(buttons, new Thickness(0, 10, 0, 0)));
            return Panel(box, new Thickness(0, 20, 0, 0));
        }

        /// <summary>Why a check failed, in the window's words.</summary>
        static string CheckWhy(string why)
        {
            switch (why)
            {
                case "offline": return Texts.Get("up_why_offline");
                case "missing": return Texts.Get("up_why_missing");
                case "busy": return Texts.Get("up_why_busy");
#if HAS_NEXUS
                case "nexus_offline": return Texts.Get("up_why_offline");
                case "nexus_busy": return Texts.Get("up_why_busy");
                case "nexus_key": return Texts.Get("up_why_key");
#endif
                default: return Why(why);
            }
        }

        /// <summary>A mod's newer version put in its place (its place in the order, on/off and
        /// options kept), from GitHub at once.</summary>
        async void ApplyUpdate(ModRow m, UpdateInfo u)
        {
            if (busy) return;
            if (m.Channel == "GitHub")
            {
                if (await Act(() => Cli.Call(() => Texts.F("up_done", Updates.ApplyGitHub(m.Id, u).Name, u.Latest)), true))
                { updates.Remove(m.Id); updated.Add(m.Id); }
                return;
            }
#if HAS_NEXUS
            if (Nexus.Credential() == null && !await AskKey()) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long shown = 0;
            var r = await Exec(() => Cli.Call(() =>
            {
                var link = new NxmLink { Game = Nexus.Game, Mod = m.NexusMod, File = u.LatestFile };
                var (mod, _) = Nexus.Install(link, Nexus.Credential(), (done, total) =>
                {
                    if (done - shown < 128 * 1024 && done != total) return;
                    shown = done;
                    Dispatcher.BeginInvoke(new Action(() => Foot(total > 0 ? Texts.F("nx_progress", Texts.Size(done), Texts.Size(total), done * 100 / total)
                                                                           : Texts.F("nx_progress_n", Texts.Size(done)), "Blue")));
                });
                return Texts.F("up_done", mod.Name, u.Latest);
            }));
            if (!r.Ok && r.Refused?.Kind == "nexus_refused")
            {
                Process.Start(new ProcessStartInfo(u.Page ?? m.Page) { UseShellExecute = true });
                Dialog.Tell(this, Texts.Get(Nexus.IsOurs(Exe) ? "up_nexus_page" : "up_nexus_page_manual"));
            }
            else if (!Refused(r) && r.Ok)
            {
                lastDone = Texts.F("took", Last(r.Out), Seconds(clock.Elapsed.TotalSeconds));
                updates.Remove(m.Id);
                updated.Add(m.Id);
            }
            Refresh();
#endif
        }

        /// <summary>Where a mod comes from, typed in: a Nexus Mods page, a GitHub repository or release, another page.</summary>
        void SetSource(ModRow m)
        {
            var url = Dialog.AskText(this, Texts.Get("up_source_ask"), Texts.Get("up_source_save"), m.Page ?? "");
            if (url == null) return;
            url = url.Trim();
            if (url.Length == 0 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { Dialog.Tell(this, Texts.F("up_source_bad", url)); return; }
            // the address is kept, and then the site is asked what this mod is: a source typed
            // in by hand should fill the rest in by itself (the user, 2026-10-24)
            SetThenMatch(m, url);
        }

        async void SetThenMatch(ModRow m, string url)
        {
            bool ok = await Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var mod = lib.Mods.FirstOrDefault(x => x.Id == m.Id) ?? throw new ModError("not in the library any more", "gone");
                Origins.FromUrl(mod, url);
                lib.Save();
                return Texts.F("up_source_done", mod.Name, Origins.Of(mod).Channel);
            }), true);
            if (ok) MatchSource(m);
        }

        /// <summary>A mod matched against its source: the name, version, author, words and picture
        /// the site has for it are filled in (the user, 2026-10-24, as a reference manager fills a
        /// paper in). The name it came with is kept and the details say it.</summary>
        async void MatchSource(ModRow m)
        {
            if (busy) return;
            var many = Taken(m).Where(x => x.Channel == "Nexus Mods" || x.Channel == "GitHub").ToList();
            if (many.Count == 0) { Dialog.Tell(this, Texts.Get("match_no_source")); return; }
#if HAS_NEXUS
            if (many.Any(x => x.Channel == "Nexus Mods") && Nexus.Credential() == null && !await AskKey()) return;
#endif
            var ids = many.Select(x => x.Id).ToList();
            var r = await Exec(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                int done = 0;
                string last = null;
                foreach (var id in ids)
                {
                    var mod = lib.Mods.FirstOrDefault(x => x.Id == id);
                    if (mod == null) continue;
                    try { Site.Fill(lib, mod); done++; last = mod.Name; }
                    catch (ModError e) when (ids.Count > 1) { last = e.Message; }
                }
                lib.Save();
                return ids.Count == 1 ? Texts.F("match_done", last) : Texts.F("match_done_n", done, ids.Count);
            }));
            if (!Refused(r) && r.Ok) lastDone = Last(r.Out);
            Refresh();
        }

/// <summary>The first filter inside the search field: where a mod comes from and what it
        /// is. Any one of them in a group will do; a mod must pass every group that has one.</summary>
        void Filter_Click(object sender, RoutedEventArgs e)
        {
            if (JustClosed(FilterBtn)) return;
            var mods = (st?.Mods ?? new List<ModRow>()).Where(x => !x.Loader).ToList();   // the loaders are in the panel
            var menu = new ContextMenu { PlacementTarget = FilterBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            Group(menu, false, fromFilter, new List<(string, string, int)>
            {
                ("nexus", Texts.Get("filter_nexus"), mods.Count(m => FromOf(m) == "nexus")),
                ("github", Texts.Get("filter_github"), mods.Count(m => FromOf(m) == "github")),
                ("none", Texts.Get("filter_no_from"), mods.Count(m => FromOf(m) == "none")),
            });
            Group(menu, true, kindFilter, new List<(string, string, int)>
            {
                ("lua", Texts.Get("filter_lua"), mods.Count(m => KindOf(m) == "lua")),
                ("asset", Texts.Get("filter_asset"), mods.Count(m => KindOf(m) == "asset")),
            });
            Dismissable(menu);
            menu.IsOpen = true;
        }

        /// <summary>The second one, beside it: the player's own labels.</summary>
        void TagFilter_Click(object sender, RoutedEventArgs e)
        {
            if (JustClosed(TagFilterBtn)) return;
            var menu = new ContextMenu { PlacementTarget = TagFilterBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var tags = AllTags().Concat(AutoTags().Where(t => !hiddenTags.Contains(t.Name))).ToList();
            if (tags.Count == 0) menu.Items.Add(new MenuItem { Header = Texts.Get("filter_no_tags"), IsEnabled = false });
            else TagGroup(menu, tags);
            Dismissable(menu);
            menu.IsOpen = true;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SetFocus(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetFocus();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        int wheelSeen;             // tests only
        bool altForTest;           // tests only: a click taken as Alt+click

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        ContextMenu lastMenu;      // tests only: the filter menu last opened

        /// <summary>A filter's menu stays open while choices are taken, so several can be lit at
        /// once. A menu is a window of its own, though, and while it is open the wheel goes to
        /// it wherever the pointer is: the list would not scroll anywhere until something was
        /// clicked to dismiss it (the user, 2026-10-25). So a turn of the wheel from outside closes
        /// it and scrolls the page on show instead of being spent on the menu. The pointer
        /// leaving it does not close it: a menu stays open until a choice, a click outside or Esc
        /// (the user, 2026-10-25: closing on leave is not how menus behave).
        ///
        /// The handlers go on that window's own root, not on the menu: with the mouse held by
        /// the menu, an event never reaches a child of the element that holds it. Only for menus
        /// without submenus: a submenu is outside its parent, and this would close both.</summary>
        void Dismissable(ContextMenu menu)
        {
            lastMenu = menu;
            UIElement root = null;
            MouseWheelEventHandler wheel = null;
            wheel = (s, e) =>
            {
                if (Over(menu, e)) return;
                wheelSeen++;
                menu.IsOpen = false;
                var page = OnShow();
                if (page != null) page.ScrollToVerticalOffset(page.VerticalOffset - e.Delta);
                e.Handled = true;
            };
            menu.Opened += (s, e) =>
            {
                root = (PresentationSource.FromVisual(menu) as HwndSource)?.RootVisual as UIElement;
                if (root == null) return;
                root.PreviewMouseWheel += wheel;
            };
            // the moment it closes, not its Closed event: that comes after the click that closed
            // it has already reached the button underneath
            var isOpen = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
            EventHandler shut = (s, e) => { if (!menu.IsOpen) { closedFor = menu.PlacementTarget; closedAt = Environment.TickCount; } };
            isOpen.AddValueChanged(menu, shut);
            menu.Closed += (s, e) =>
            {
                isOpen.RemoveValueChanged(menu, shut);
                // the menu's own window had the keyboard, and Windows sends the wheel to the
                // window that has it: without this the list stays unscrollable until something
                // is clicked, which is the whole fault being fixed here
                // the menu held the mouse and the keyboard: both have to come back, or the
                // wheel keeps going to a window that is no longer there
                if (Mouse.Captured != null && !ReferenceEquals(Mouse.Captured, ListArea)) Mouse.Capture(null);
                Activate();
                SetFocus(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                if (root == null) return;
                root.PreviewMouseWheel -= wheel;
                root = null;
            };
        }

        // the scroll the wheel is turning now, when the wheel last turned, and when that scroll last moved
        ScrollViewer wheelTarget;
        int wheelLast, wheelMoved;
        const int WheelRestMs = 400;
        const int WheelHoldMs = 250;

        /// <summary>The wheel the way browsers have it, for a list with a scroll of its own inside
        /// a scrolling page (the user, 2026-10-25). A ScrollViewer keeps every turn of the wheel,
        /// even with nothing to scroll, so the tags page never moved with the pointer over its
        /// add list, at the list's end or with no scroll bar on it at all. Now a turn goes to the
        /// innermost scroll under the pointer that can still move that way, and stays with it
        /// until the wheel rests for WheelRestMs. A list that reaches its end holds the wheel for
        /// WheelHoldMs, so a flick past its end does not throw the page; a wheel still turning
        /// after that moves the page at once. Holding until the wheel rested kept the page still
        /// for as long as the wheel turned, 4 to 10 seconds (the user, 2026-10-25).</summary>
        void Wheel(object sender, MouseWheelEventArgs e)
        {
            if (!e.Handled && e.Delta != 0 && Keyboard.Modifiers == ModifierKeys.Shift && GroupScroll.IsVisible &&
                e.OriginalSource is Visual sv && GroupScroll.IsAncestorOf(sv))
            {
                // the groups' columns sideways with Shift, as browsers do (the user, 2026-10-27)
                GroupScroll.ScrollToHorizontalOffset(GroupScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
                return;
            }
            if (e.Handled || e.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None) return;
            var from = e.OriginalSource as DependencyObject;
            var scrolls = Scrolls(from).ToList();
            // the jump button floats over the list, outside its scroll: a wheel on it turns the
            // list still (the user, 2026-10-27: the wheel stopped with the pointer on the button)
            if (flingLayer != null && from is Visual v && flingLayer.IsAncestorOf(v)) scrolls = Scrolls(List).ToList();
            bool fresh = unchecked(e.Timestamp - wheelLast) > WheelRestMs || wheelTarget == null ||
                         !wheelTarget.IsVisible || !scrolls.Contains(wheelTarget);
            wheelLast = e.Timestamp;
            var innermost = scrolls.FirstOrDefault(v => CanMove(v, e.Delta));
            if (fresh) { wheelTarget = innermost; wheelMoved = e.Timestamp; }
            // the page brought a list under a pointer that stayed put: the list takes the wheel at
            // once, not when the wheel rests (the user, 2026-10-25: it took a long while)
            else if (innermost != null && innermost != wheelTarget && scrolls.IndexOf(innermost) < scrolls.IndexOf(wheelTarget))
            { wheelTarget = innermost; wheelMoved = e.Timestamp; }
            if (wheelTarget == null) return;                    // nothing here moves that way
            e.Handled = true;
            if (!CanMove(wheelTarget, e.Delta))
            {
                if (unchecked(e.Timestamp - wheelMoved) < WheelHoldMs) return;      // just reached its end: held
                // still turning: on to the next scroll out that can move
                var next = scrolls.SkipWhile(v => v != wheelTarget).Skip(1).FirstOrDefault(v => CanMove(v, e.Delta));
                if (next == null) return;
                wheelTarget = next;
            }
            wheelMoved = e.Timestamp;
            wheelTarget.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent, Source = wheelTarget,
            });
            if (wheelTarget == ListScroll) Fling(e);
        }

        // ------------------------------------------------------------ a fast wheel on the list

        /// <summary>The wheel turned fast on the list, this many notches (120 each) in one direction
        /// within FlingWindowMs: a button by the pointer goes to the top or the end (the user,
        /// 2026-10-26: the buttons over the list are far from the pointer).</summary>
        const int FlingWindowMs = 700, FlingDelta = 5 * 120, FlingRestMs = 1500;
        readonly List<(int At, int Delta)> flingTurns = new List<(int, int)>();
        Canvas flingLayer;
        Border flingBtn;
        int flingDir;
        DispatcherTimer flingHide;

        void Fling(MouseWheelEventArgs e)
        {
            if (!Texts.FlingButton) return;
            int dir = Math.Sign(e.Delta);
            if (flingBtn != null && flingDir != dir) HideFling();          // turned the other way
            flingTurns.RemoveAll(t => unchecked(e.Timestamp - t.At) > FlingWindowMs || Math.Sign(t.Delta) != dir);
            flingTurns.Add((e.Timestamp, e.Delta));
            if (flingBtn != null) { flingHide.Stop(); flingHide.Start(); return; }       // already shown: kept while it turns
            if (Math.Abs(flingTurns.Sum(t => t.Delta)) < FlingDelta) return;
            // only when the end it goes to is more than a page away
            var left = dir < 0 ? ListScroll.ScrollableHeight - ListScroll.VerticalOffset : ListScroll.VerticalOffset;
            if (left < ListScroll.ViewportHeight) return;
            ShowFling(dir, e.GetPosition(ListArea));
        }

        void ShowFling(int dir, Point at)
        {
            if (flingLayer == null)
            {
                flingLayer = new Canvas { ClipToBounds = true };
                ListArea.Children.Add(flingLayer);
                ListScroll.ScrollChanged += (s, e) =>
                {
                    // there: nothing left to jump to
                    if (flingBtn != null && (flingDir > 0 ? ListScroll.VerticalOffset < 0.5 : ListScroll.VerticalOffset > ListScroll.ScrollableHeight - 0.5))
                        HideFling();
                };
                flingHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FlingRestMs) };
                flingHide.Tick += (s, e) =>
                {
                    if (flingBtn != null && flingBtn.IsMouseOver) return;   // the pointer on it: it stays
                    HideFling();
                };
            }
            flingDir = dir;
            var words = new StackPanel { Orientation = Orientation.Horizontal };
            words.Children.Add(new Path
            {
                Data = Geometry.Parse(dir > 0 ? GlyphTop : GlyphBottom), Width = 14, Height = 14, Stretch = Stretch.Uniform,
                Stroke = B("Blue"), StrokeThickness = 2, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            });
            words.Children.Add(new TextBlock { Text = Texts.Get(dir > 0 ? "jump_top" : "jump_bottom"), FontSize = 13, FontWeight = FontWeights.SemiBold,
                                               Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center });
            flingBtn = new Border
            {
                Background = B("Ground700"), BorderBrush = B("Blue"), BorderThickness = new Thickness(1), Padding = new Thickness(12, 7, 14, 7),
                Cursor = Cursors.Hand, Child = words,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.5, Color = Colors.Black },
            };
            flingBtn.MouseLeftButtonUp += (s, e) =>
            {
                if (flingDir > 0) ListScroll.ScrollToTop(); else ListScroll.ScrollToBottom();
                HideFling();
                e.Handled = true;
            };
            flingBtn.MouseLeave += (s, e) => { flingHide.Stop(); flingHide.Start(); };
            // beside the pointer, to its right, and inside the list (clear of its scroll bar)
            flingBtn.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = flingBtn.DesiredSize;
            var x = Math.Max(8, Math.Min(at.X + 18, ListArea.ActualWidth - size.Width - 26));
            var y = Math.Max(8, Math.Min(at.Y - size.Height / 2, ListArea.ActualHeight - size.Height - 8));
            Canvas.SetLeft(flingBtn, x);
            Canvas.SetTop(flingBtn, y);
            flingLayer.Children.Add(flingBtn);
            flingHide.Stop();
            flingHide.Start();
        }

        void HideFling()
        {
            flingHide?.Stop();
            if (flingBtn != null) flingLayer.Children.Remove(flingBtn);
            flingBtn = null;
            flingTurns.Clear();
        }

        /// <summary>The scrolls d is inside, innermost first.</summary>
        static IEnumerable<ScrollViewer> Scrolls(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ScrollViewer v) yield return v;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
        }

        /// <summary>Whether v can still scroll the way a turn of delta goes.</summary>
        static bool CanMove(ScrollViewer v, int delta) =>
            v.ScrollableHeight > 0.5 && (delta < 0 ? v.VerticalOffset < v.ScrollableHeight - 0.5 : v.VerticalOffset > 0.5);

        // the button whose menu closed last, and when
        UIElement closedFor;
        int closedAt;

        /// <summary>Whether a click on the button that opens a menu is the one that just closed that
        /// menu: the press outside closes it, then the same click opened it again, so it flashed
        /// instead of going (the user, 2026-10-25). A second click on the button closes its menu.</summary>
        bool JustClosed(UIElement button) =>
            ReferenceEquals(closedFor, button) && unchecked(Environment.TickCount - closedAt) < 300;

        /// <summary>The scroll of the page on show.</summary>
        ScrollViewer OnShow() =>
            StoragePage.Visibility == Visibility.Visible ? StoragePage
            : TagsPage.Visibility == Visibility.Visible ? TagsPage
            : DownloadPage.Visibility == Visibility.Visible ? DownloadPage
            : SettingsPage.Visibility == Visibility.Visible ? SettingsPage
            : ListScroll;

        /// <summary>Whether the pointer is inside the menu itself.</summary>
        static bool Over(ContextMenu menu, MouseEventArgs e)
        {
            var p = e.GetPosition(menu);
            return p.X >= 0 && p.Y >= 0 && p.X <= menu.ActualWidth && p.Y <= menu.ActualHeight;
        }

        /// <summary>The choices in a filter's menu, nothing else: a line above them when they
        /// follow another group, and each one lit while it is taken (the user, 2026-10-24).</summary>
        void Group(ContextMenu menu, bool line, HashSet<string> set, List<(string Key, string Name, int N)> items)
        {
            if (line) menu.Items.Add(new Separator());
            foreach (var (key, name, n) in items)
            {
                var it = new MenuItem { Header = name + "   " + n, IsCheckable = true, IsChecked = set.Contains(key), StaysOpenOnClick = true };
                var mine = key;
                it.Click += (s, e) =>
                {
                    if (!set.Remove(mine)) set.Add(mine);
                    AfterFilter();
                };
                menu.Items.Add(it);
            }
        }

        /// <summary>The labels in their menu. A click takes a label: a mod carrying any taken one
        /// is shown. Alt+click leaves a label out instead: a mod carrying it is not shown, drawn
        /// in faded hazard stripes (the user, 2026-10-25: Alt+click on Probe hides every Probe
        /// mod). One label is taken, left out or neither. A line at the bottom says how.</summary>
        void TagGroup(ContextMenu menu, List<(string Name, int N)> items)
        {
            foreach (var (name, n) in items)
            {
                var it = new MenuItem { Header = name + "   " + n, StaysOpenOnClick = true };
                var mine = name;
                void Paint()
                {
                    it.IsChecked = tagFilter.Contains(mine);
                    it.Tag = tagExclude.Contains(mine) ? "exclude" : null;
                }
                Paint();
                it.Click += (s, e) =>
                {
                    if (altForTest || (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
                    {
                        tagFilter.Remove(mine);
                        if (!tagExclude.Remove(mine)) tagExclude.Add(mine);
                    }
                    else
                    {
                        tagExclude.Remove(mine);
                        if (!tagFilter.Remove(mine)) tagFilter.Add(mine);
                    }
                    Paint();
                    AfterFilter();
                };
                menu.Items.Add(it);
            }
            // the line that says how: an explaining line, gone with the others (the user, 2026-10-25)
            if (Texts.Hints)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem
                {
                    Header = new TextBlock { Text = Texts.Get("filter_tags_alt"), FontSize = 12, Foreground = B("Muted") },
                    IsEnabled = false, Cursor = Cursors.Arrow,
                });
            }
            // Alt on its own would take the menu into keyboard mode and close it: here it is held
            // for a click, so the menu keeps it
            KeyEventHandler alt = (s, e) =>
            {
                if (e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt)) e.Handled = true;
            };
            menu.PreviewKeyDown += alt;
            menu.PreviewKeyUp += alt;
        }

        /// <summary>What each filter button says: its name, how many choices are lit, and the
        /// little arrow that says it opens.</summary>
        void FilterWords()
        {
            int narrow = fromFilter.Count + kindFilter.Count;
            FilterBtn.Content = Caret(narrow == 0 ? Texts.Get("filter") : Texts.F("filter_n", narrow), narrow > 0);
            int labels = tagFilter.Count + tagExclude.Count;
            TagFilterBtn.Content = Caret(labels == 0 ? Texts.Get("filter_tags") : Texts.F("filter_tags_n", labels), labels > 0);
        }

        UIElement Caret(string text, bool lit)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = text, Foreground = B(lit ? "Yellow" : "Ink"), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new Path
            {
                Data = Geometry.Parse("M0,0 L5,5 L10,0"), Stroke = B(lit ? "Yellow" : "Muted"), StrokeThickness = 1.6,
                Margin = new Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            return line;
        }

        void AfterFilter()
        {
            FilterWords();
            if (st != null) { RenderList(); RenderDetail(); }
        }

        /// <summary>Every label in the library, the most used first.</summary>
        List<(string Name, int N)> AllTags()
        {
            var counts = new List<(string Name, int N)>();
            foreach (var m in st?.Mods ?? new List<ModRow>())
                foreach (var t in m.Tags)
                {
                    int i = counts.FindIndex(x => string.Equals(x.Name, t, StringComparison.CurrentCultureIgnoreCase));
                    if (i < 0) counts.Add((t, 1));
                    else counts[i] = (counts[i].Name, counts[i].N + 1);
                }
            // the labels made but not yet given to a mod: the library keeps them too
            foreach (var t in st?.Tags ?? new List<string>())
                if (!counts.Any(x => string.Equals(x.Name, t, StringComparison.CurrentCultureIgnoreCase))) counts.Add((t, 0));
            if (newTag != null && !counts.Any(x => string.Equals(x.Name, newTag, StringComparison.CurrentCultureIgnoreCase))) counts.Add((newTag, 0));
            return counts.OrderByDescending(x => x.N).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ---------------------------------------------------------------- the labels page

        // the label the page is showing (and whether it is one of the manifests'), one just
        // made that is on no mod yet, and what the page last did
        string tagSel, newTag, tagDone;
        bool tagAuto;

        /// <summary>The labels page: every label with how many mods carry it, and under the chosen
        /// one every mod in the library with a tick for whether it carries it (the user,
        /// 2026-10-24: a whole page for the labels, after storage).</summary>
        // the two boxes the page types into, kept across renders so typing is not interrupted
        TextBox tagFind, tagHunt;
        /// <summary>The mods ticked in the add box, so a label goes onto many at once, and
        /// whether that box leaves out the mods that already carry a label of the player's
        /// (the user's tester, 2026-10-25: labelling mod by mod is too slow).</summary>
        /// <summary>The folders of old versions ticked on the storage page.</summary>
        readonly HashSet<string> oldPick = new HashSet<string>();
        readonly HashSet<string> tagPick = new HashSet<string>();
        bool tagBare;
        TextBlock tagHint;
        StackPanel tagHits;

        /// <summary>A control kept from the last render, put into the new one. It has to leave its
        /// old parent first: WPF refuses a child that is already somewhere else.</summary>
        static void Reuse(FrameworkElement kept, Panel into)
        {
            if (kept.Parent is Panel was) was.Children.Remove(kept);
            into.Children.Add(kept);
        }

        /// <summary>The name in the box made into a label. It carries no mod yet, so it lives in
        /// the window until one is given it; a name already there is simply shown.</summary>
        void NewTag()
        {
            var name = (tagHunt?.Text ?? "").Trim();
            if (name.Length == 0) { tagHunt?.Focus(); return; }
            tagHunt.Text = "";
            tagAuto = false;
            tagSel = name;
            if (!AllTags().Any(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                newTag = name;              // shown at once; the library is told as well
                Run("tag-new", name);
            }
            RenderTags();
        }

        /// <summary>A label's note and whether it is hidden from the filter, in gui.json: the
        /// labels themselves live on the mods, these belong to the window.</summary>
        static Dictionary<string, object> TagNotes()
        {
            try { return Json.Obj(Store.Load("gui.json"), "tag_notes") ?? new Dictionary<string, object>(); }
            catch (Exception e) when (e is IOException || e is FormatException) { return new Dictionary<string, object>(); }
        }

        static void SaveTagNote(string tag, string note)
        {
            try
            {
                var d = Store.Load("gui.json");
                var notes = Json.Obj(d, "tag_notes") ?? new Dictionary<string, object>();
                if (string.IsNullOrWhiteSpace(note)) notes.Remove(tag);
                else notes[tag] = note.Trim();
                d["tag_notes"] = notes;
                Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        readonly HashSet<string> hiddenTags = LoadHiddenTags();

        static HashSet<string> LoadHiddenTags()
        {
            var set = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            try { foreach (var t in Json.List(Store.Load("gui.json"), "tag_hidden")) if (t is string x) set.Add(x); }
            catch (Exception e) when (e is IOException || e is FormatException) { }
            return set;
        }

        void SaveHiddenTags()
        {
            try
            {
                var d = Store.Load("gui.json");
                d["tag_hidden"] = hiddenTags.Cast<object>().ToList();
                Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>Every label a mod's own manifest declares, the most used first.</summary>
        List<(string Name, int N)> AutoTags()
        {
            var counts = new List<(string Name, int N)>();
            foreach (var m in st?.Mods ?? new List<ModRow>())
                foreach (var t in m.AutoTags)
                {
                    int i = counts.FindIndex(x => string.Equals(x.Name, t, StringComparison.CurrentCultureIgnoreCase));
                    if (i < 0) counts.Add((t, 1));
                    else counts[i] = (counts[i].Name, counts[i].N + 1);
                }
            return counts.OrderByDescending(x => x.N).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        bool Carries(ModRow m, string tag, bool auto) =>
            (auto ? m.AutoTags : m.Tags).Any(t => string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase));

        /// <summary>The labels page (the design board's Tags.dc.html): the labels down the left,
        /// mine and the ones the mods' manifests declare; on the right the chosen one, what can be
        /// done to it, the mods that carry it and a box to find a mod and add it.</summary>
        void RenderTags()
        {
            TagsBox.Children.Clear();
            if (st == null) return;
            var mine = AllTags();
            var auto = AutoTags();
            if (tagSel == null || !(tagAuto ? auto : mine).Any(t => string.Equals(t.Name, tagSel, StringComparison.CurrentCultureIgnoreCase)))
            {
                tagAuto = mine.Count == 0 && auto.Count > 0;
                tagSel = (tagAuto ? auto : mine).Select(t => t.Name).FirstOrDefault();
            }
            bool typing = tagHunt != null && tagHunt.IsKeyboardFocusWithin;
            bool finding = tagFind != null && tagFind.IsKeyboardFocusWithin;

            // the head: what the page holds
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            head.Children.Add(new TextBlock { Text = Texts.Get("tag_title"), FontSize = 28, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            var sum = new TextBlock
            {
                Text = Texts.F("tag_summary", st.Mods.Count, mine.Count, auto.Count), FontFamily = Mono, FontSize = 12,
                Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 6, 0, 0),
            };
            head.Children.Add(sum);
            TagsBox.Children.Add(head);

            var two = new Grid();
            two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            two.ColumnDefinitions.Add(new ColumnDefinition());
            TagsBox.Children.Add(two);

            // ---------------------------------------------------------- the labels
            var left = new StackPanel();
            Grid.SetColumn(left, 0);
            two.Children.Add(left);

            // the name goes in the box, the button makes it: a box that only searched was taken
            // for the name every time (the user, 2026-10-24). There are never enough labels to
            // need searching anyway
            var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var make = new Button { Content = Texts.Get("tag_new"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
            make.Click += (s, e) => NewTag();
            DockPanel.SetDock(make, Dock.Right);
            tools.Children.Add(make);
            if (tagHunt == null)
            {
                tagHunt = new TextBox
                {
                    Background = B("Ground900"), Foreground = B("Ink"), CaretBrush = B("Ink"), BorderBrush = B("Grid"),
                    BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), FontSize = 13,
                };
                tagHunt.TextChanged += (s, e) => { if (tagHint != null) tagHint.Visibility = tagHunt.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
                tagHunt.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; NewTag(); } };
            }
            var nameBox = new Grid();
            Reuse(tagHunt, nameBox);
            tagHint = new TextBlock
            {
                Text = Texts.Get("tag_new_hint"), Foreground = B("Muted"), FontSize = 13, IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                Visibility = tagHunt.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
            };
            nameBox.Children.Add(tagHint);
            tools.Children.Add(nameBox);
            left.Children.Add(tools);

            var mineShown = mine;
            var autoShown = auto;

            void Row(string name, int n, bool isAuto)
            {
                bool cur = tagAuto == isAuto && string.Equals(name, tagSel, StringComparison.CurrentCultureIgnoreCase);
                bool off = isAuto && hiddenTags.Contains(name);
                var b = new Button { Cursor = Cursors.Hand, Template = Plain(), FocusVisualStyle = null, Margin = new Thickness(0, 0, 0, 2) };
                var edge = new Border { BorderThickness = new Thickness(2, 0, 0, 0), BorderBrush = cur ? B("Yellow") : Brushes.Transparent, Background = cur ? B("Ground900") : Brushes.Transparent };
                var dp = new DockPanel { Margin = new Thickness(12, 9, 12, 9), Opacity = off ? 0.5 : 1 };
                var count = new TextBlock { Text = n.ToString(), FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(count, Dock.Right);
                dp.Children.Add(count);
                if (off)
                {
                    var mark = new TextBlock { Text = Texts.Get("tag_hidden"), FontSize = 11, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
                    DockPanel.SetDock(mark, Dock.Right);
                    dp.Children.Add(mark);
                }
                // the colour has to be said: a button with a plain template of its own does not
                // take the window's ink (the user, 2026-10-24)
                dp.Children.Add(new TextBlock
                {
                    Text = name, FontWeight = FontWeights.SemiBold, Foreground = B(off ? "Muted" : "Ink"),
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                });
                edge.Child = dp;
                b.Content = edge;
                var mineName = name;
                b.Click += (s, e) => { tagSel = mineName; tagAuto = isAuto; RenderTags(); };
                left.Children.Add(b);
            }

            if (mineShown.Count > 0)
            {
                left.Children.Add(Label("tag_mine", new Thickness(2, 6, 0, 6)));
                foreach (var (name, n) in mineShown) Row(name, n, false);
            }
            if (autoShown.Count > 0)
            {
                left.Children.Add(Label("tag_auto", new Thickness(2, 14, 0, 6)));
                foreach (var (name, n) in autoShown) Row(name, n, true);
            }
            if (mineShown.Count + autoShown.Count == 0)
                left.Children.Add(Small(Texts.Get("tag_none"), new Thickness(2, 6, 0, 0)));
            if (typing) tagHunt.Focus();
            if (tagSel == null) return;

            // ---------------------------------------------------------- the chosen label
            var right = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
            Grid.SetColumn(right, 1);
            two.Children.Add(right);

            right.Children.Add(new TextBlock { Text = Texts.Get(tagAuto ? "tag_kind_auto" : "tag_kind_mine"), FontFamily = Mono, FontSize = 12, Foreground = B("Muted") });
            var bar = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (tagAuto)
            {
                var hide = new Button { Content = Texts.Get(hiddenTags.Contains(tagSel) ? "tag_unhide" : "tag_hide"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
                hide.Click += (s, e) =>
                {
                    if (!hiddenTags.Remove(tagSel)) hiddenTags.Add(tagSel);
                    SaveHiddenTags();
                    tagFilter.RemoveWhere(x => hiddenTags.Contains(x));
                    tagExclude.RemoveWhere(x => hiddenTags.Contains(x));
                    AfterFilter();
                    RenderTags();
                };
                acts.Children.Add(hide);
            }
            else
            {
                var rename = new Button { Content = Texts.Get("tag_rename"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
                rename.Click += (s, e) =>
                {
                    var name = Dialog.AskText(this, Texts.Get("tag_rename_ask"), Texts.Get("tag_rename_save"), tagSel);
                    if (name == null || name.Trim().Length == 0 || name.Trim() == tagSel) return;
                    var now = name.Trim();
                    var was = tagSel;
                    tagSel = now;
                    if (newTag != null && string.Equals(newTag, was, StringComparison.CurrentCultureIgnoreCase)) { newTag = now; RenderTags(); return; }
                    if (tagFilter.Remove(was)) tagFilter.Add(now);
                    if (tagExclude.Remove(was)) tagExclude.Add(now);
                    SaveTagNote(now, Json.Str(TagNotes(), was));
                    SaveTagNote(was, null);
                    Run("tag-rename", was, now);
                };
                acts.Children.Add(rename);
                var drop = new Button { Content = Texts.Get("tag_delete"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
                drop.Click += (s, e) =>
                {
                    if (!Dialog.Ask(this, Texts.F("tag_delete_ask", tagSel), Texts.Get("tag_delete"))) return;
                    var was = tagSel;
                    if (newTag != null && string.Equals(newTag, was, StringComparison.CurrentCultureIgnoreCase)) newTag = null;
                    tagSel = null;
                    tagFilter.Remove(was);
                    tagExclude.Remove(was);
                    SaveTagNote(was, null);
                    tagDone = Texts.F("tag_dropped", was);
                    Run("tag-delete", was);
                };
                acts.Children.Add(drop);
            }
            var show = new Button { Content = Texts.Get("tag_show"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
            show.Click += (s, e) =>
            {
                tagFilter.Clear();
                tagExclude.Clear();
                tagFilter.Add(tagSel);
                AfterFilter();
                ShowPage("deploy");
            };
            acts.Children.Add(show);
            DockPanel.SetDock(acts, Dock.Right);
            bar.Children.Add(acts);
            bar.Children.Add(new TextBlock { Text = tagSel, FontSize = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            right.Children.Add(bar);
            if (tagAuto && Texts.Hints) right.Children.Add(Small(Texts.Get("tag_auto_note"), new Thickness(0, 8, 0, 0)));

            // a note of the player's own
            if (!tagAuto)
            {
                var noteLine = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
                var label = new TextBlock { Text = Texts.Get("tag_note"), FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                DockPanel.SetDock(label, Dock.Left);
                noteLine.Children.Add(label);
                var note = new TextBox
                {
                    Text = Json.Str(TagNotes(), tagSel) ?? "", Background = B("Ground900"), Foreground = B("Ink"), CaretBrush = B("Ink"),
                    BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), FontSize = 13, MaxWidth = 460,
                    HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 320,
                };
                var forTag = tagSel;
                note.LostFocus += (s, e) => SaveTagNote(forTag, note.Text);
                noteLine.Children.Add(note);
                right.Children.Add(noteLine);
            }

            // the mods that carry it
            var members = st.Mods.Where(m => Carries(m, tagSel, tagAuto)).ToList();
            var all = new DockPanel { Margin = new Thickness(0, 20, 0, 6) };
            var bulk = new StackPanel { Orientation = Orientation.Horizontal };
            var only = new Button { Content = Texts.Get("tag_only"), Style = (Style)FindResource("Ghost"), MinHeight = 34, Margin = new Thickness(8, 0, 0, 0), ToolTip = Texts.F("tag_only_tip", members.Count) };
            only.Click += (s, e) =>
            {
                // the mods under the label turned on; the rest are left as they are (the user,
                // 2026-10-24: nobody turns a part of their mods on and everything else off)
                foreach (var m in members.Where(x => !x.Enabled)) Change(m.Id, c => c.Enabled = true, "enable", m.Id);
                tagDone = Texts.F("tag_only_done", tagSel, members.Count);
                RenderTags();
            };
            bulk.Children.Add(only);
            var offAll = new Button { Content = Texts.Get("tag_off_all"), Style = (Style)FindResource("Ghost"), MinHeight = 34, Margin = new Thickness(8, 0, 0, 0) };
            offAll.Click += (s, e) =>
            {
                foreach (var m in members.Where(x => x.Enabled)) Change(m.Id, c => c.Enabled = false, "disable", m.Id);
                tagDone = Texts.F("tag_off_done", tagSel, members.Count);
                RenderTags();
            };
            bulk.Children.Add(offAll);
            DockPanel.SetDock(bulk, Dock.Right);
            all.Children.Add(bulk);
            all.Children.Add(new TextBlock { Text = Texts.F("tag_on_n", members.Count), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center });
            right.Children.Add(all);

            var carry = new StackPanel();
            if (members.Count == 0) carry.Children.Add(Small(Texts.Get("tag_empty"), new Thickness(0, 0, 0, 0)));
            int memberBand = 0;
            foreach (var m in members)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                if (!tagAuto)
                {
                    var offBtn = new Button { Content = "✕", Template = Plain(), Cursor = Cursors.Hand, FocusVisualStyle = null, Foreground = B("Muted"), Width = 28, ToolTip = Texts.F("tag_off", m.Name) };
                    var mod = m;
                    var name = tagSel;
                    offBtn.Click += (s, e) =>
                    {
                        Change(mod.Id, c => c.Tags.RemoveAll(t => string.Equals(t, name, StringComparison.CurrentCultureIgnoreCase)), "untag", mod.Id, name);
                        RenderTags();
                    };
                    DockPanel.SetDock(offBtn, Dock.Right);
                    row.Children.Add(offBtn);
                }
                var state = new TextBlock
                {
                    Text = Texts.Get(m.Enabled ? "tag_on_state" : "tag_off_state"), FontFamily = Mono, FontSize = 12,
                    Foreground = B(m.Enabled ? "Blue" : "Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
                };
                DockPanel.SetDock(state, Dock.Right);
                row.Children.Add(state);
                var ver = new TextBlock { Text = m.ShownVersion != null ? V(m.ShownVersion) : "", FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(ver, Dock.Right);
                row.Children.Add(ver);
                var pic = Picture(m.Icon, GlyphMod, 36, m.Enabled ? "Blue" : "Muted");
                DockPanel.SetDock(pic, Dock.Left);
                row.Children.Add(pic);
                row.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
                carry.Children.Add(Band(row, memberBand++));
            }
            right.Children.Add(Panel(carry, new Thickness(0, 0, 0, 16)));

            // a box to find a mod and put the label on it
            if (!tagAuto)
            {
                right.Children.Add(Label("tag_put", new Thickness(0, 0, 0, 6)));
                var findLine = new DockPanel();
                var bare = new CheckBox
                {
                    Style = (Style)FindResource("Tick"), IsChecked = tagBare, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16, 0, 0, 0),
                    Content = new TextBlock { Text = Texts.Get("tag_bare"), FontSize = 12, Foreground = B("Muted") },
                };
                bare.Click += (s, e) => { tagBare = bare.IsChecked == true; FillTagHits(); };
                DockPanel.SetDock(bare, Dock.Right);
                findLine.Children.Add(bare);
                if (tagFind == null)
                {
                    tagFind = new TextBox
                    {
                        Background = B("Ground900"), Foreground = B("Ink"), CaretBrush = B("Ink"), BorderBrush = B("Grid"),
                        BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), FontSize = 14,
                    };
                    tagFind.TextChanged += (s, e) => FillTagHits();
                }
                Reuse(tagFind, findLine);
                right.Children.Add(findLine);
                tagHits = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                right.Children.Add(tagHits);
                FillTagHits();
                if (finding) tagFind.Focus();
            }

            // what just happened, and how many mods have no label of mine
            int none = st.Mods.Count(m => m.Tags.Count == 0);
            var foot = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
            var rest = new TextBlock
            {
                Text = none == 0 ? Texts.Get("tag_all_done") : Texts.F("tag_rest", none), FontSize = 12, Foreground = B("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(rest, Dock.Right);
            foot.Children.Add(rest);
            foot.Children.Add(new TextBlock
            {
                // what was just done stays; the line that explains labels is an explaining line (the user, 2026-10-25)
                Text = tagDone ?? (Texts.Hints ? Texts.Get("tag_foot") : ""), FontSize = 12, Foreground = B(tagDone != null ? "Blue" : "Muted"),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            });
            right.Children.Add(foot);
        }

        /// <summary>The mods that do not carry the label yet: every one of them, each with a tick,
        /// so a label goes onto many in one call (the user's tester, 2026-10-25: a box that showed
        /// six of them made labelling a library too slow).</summary>
        void FillTagHits()
        {
            if (tagHits == null || st == null || tagSel == null) return;
            tagHits.Children.Clear();
            var words = (tagFind?.Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var hits = st.Mods
                .Where(m => !Carries(m, tagSel, false))
                .Where(m => !tagBare || m.Tags.Count == 0)
                .Where(m => words.All(w => m.Name.IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0))
                .ToList();
            if (hits.Count == 0)
            {
                tagHits.Children.Add(Small(Texts.Get((tagFind?.Text ?? "").Trim().Length == 0 && !tagBare ? "tag_all_in" : "tag_no_hits"), new Thickness(0, 4, 0, 0)));
                return;
            }

            // the line underneath first: the rows count up on it without the list being drawn
            // again, so the scroll stays where the player left it
            var foot = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var count = new TextBlock
            {
                Text = Texts.F("tag_picked", tagPick.Count), FontSize = 12, Foreground = B("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = tagSel;
            var put = new Button
            {
                Content = Texts.F("tag_add_these", label), Style = (Style)FindResource("Ghost"), MinHeight = 34,
                FontSize = 12, Margin = new Thickness(8, 0, 0, 0), IsEnabled = tagPick.Count > 0,
            };
            put.Click += (s, e) => TagThese(st.Mods.Where(m => tagPick.Contains(m.Id) && !Carries(m, label, false)).ToList(), label);
            DockPanel.SetDock(put, Dock.Right);
            foot.Children.Add(put);
            // one tick for all of them, as in the mod list (the user, 2026-10-25)
            var every = new CheckBox
            {
                Style = (Style)FindResource("Tick"), IsChecked = hits.All(m => tagPick.Contains(m.Id)), VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = Texts.Get("pick_all"), FontSize = 12, Foreground = B("Ink") },
                Margin = new Thickness(8, 0, 16, 0),      // in line with the rows' ticks, inside their bands
            };
            every.Click += (s, e) =>
            {
                if (every.IsChecked == true) foreach (var m in hits) tagPick.Add(m.Id);
                else foreach (var m in hits) tagPick.Remove(m.Id);
                FillTagHits();
            };
            DockPanel.SetDock(every, Dock.Left);
            foot.Children.Add(every);
            foot.Children.Add(count);

            var rows = new StackPanel();
            int hitBand = 0;
            foreach (var m in hits)
            {
                var row = new DockPanel();
                var has = new TextBlock
                {
                    Text = m.Tags.Count > 0 ? string.Join(" · ", m.Tags) : Texts.Get("tag_none_of_mine"), FontSize = 12,
                    Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                DockPanel.SetDock(has, Dock.Right);
                row.Children.Add(has);
                var pic = Picture(m.Icon, GlyphMod, 32, m.Enabled ? "Blue" : "Muted");
                pic.Margin = new Thickness(0, 0, 10, 0);
                DockPanel.SetDock(pic, Dock.Left);
                row.Children.Add(pic);
                row.Children.Add(new TextBlock { Text = m.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var id = m.Id;
                var box = new CheckBox
                {
                    Style = (Style)FindResource("Tick"), IsChecked = tagPick.Contains(id), Content = row,
                    Margin = new Thickness(0, 3, 0, 3),
                };
                box.Click += (s, e) =>
                {
                    if (box.IsChecked == true) tagPick.Add(id); else tagPick.Remove(id);
                    count.Text = Texts.F("tag_picked", tagPick.Count);
                    put.IsEnabled = tagPick.Count > 0;
                    every.IsChecked = hits.All(x => tagPick.Contains(x.Id));
                };
                rows.Children.Add(Band(box, hitBand++, false));
            }
            tagHits.Children.Add(new ScrollViewer
            {
                Content = rows, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
            tagHits.Children.Add(foot);
        }

        /// <summary>One label onto the mods that were ticked: one call and one save, not one of
        /// each per mod.</summary>
        async void TagThese(List<ModRow> mods, string name)
        {
            if (mods.Count == 0 || busy) return;
            foreach (var m in mods) m.Tags.Add(name);
            newTag = null;
            tagPick.Clear();
            tagDone = mods.Count == 1 ? Texts.F("tag_added", mods[0].Name, name) : Texts.F("tag_added_n", mods.Count, name);
            RenderTags();
            var args = new List<string> { "tag-many", name };
            args.AddRange(mods.Select(m => m.Id));
            waiting++;
            var r = await Queued(() => Cli.Run(args.ToArray()));
            waiting--;
            quietUntil = DateTime.Now.AddSeconds(1.5);
            Refused(r);
            Refresh();
        }

        /// <summary>The gear: the settings page, or back to the page it was opened from.</summary>
        void Settings_Click(object sender, RoutedEventArgs e) =>
            ShowPage(SettingsPage.Visibility == Visibility.Visible ? pageBack : "settings");

        /// <summary>The settings page (the user, 2026-10-24; a page, not a window, since
        /// 2026-10-27): the language, whether the explaining lines are shown, who made this and
        /// where it comes from.</summary>
        void RenderSettings()
        {
            SettingsBox.Children.Clear();
            SettingsBox.Children.Add(new TextBlock { Text = Texts.Get("settings"), FontSize = 28, FontWeight = FontWeights.Bold });
            var body = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            SettingsBox.Children.Add(body);

            // the language
            body.Children.Add(Label("set_lang", new Thickness(0, 20, 0, 6)));
            var langs = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
            var pair = new StackPanel { Orientation = Orientation.Horizontal };
            var zh = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "setlang", Content = "中文", MinWidth = 96, IsChecked = Texts.Zh };
            var en = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "setlang", Content = "English", MinWidth = 96, IsChecked = !Texts.Zh };
            zh.Click += (s, e2) => Language(true);
            en.Click += (s, e2) => Language(false);
            pair.Children.Add(zh);
            pair.Children.Add(en);
            langs.Child = pair;
            body.Children.Add(langs);

            // profiles: a switch of their own (the user, 2026-10-25)
            body.Children.Add(Label("set_profiles", new Thickness(0, 20, 0, 6)));
            var pfSw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = st?.ProfilesOn == true, VerticalAlignment = VerticalAlignment.Center };
            var pfLine = new StackPanel { Orientation = Orientation.Horizontal };
            pfLine.Children.Add(pfSw);
            pfLine.Children.Add(new TextBlock { Text = Texts.Get("set_profiles_on"), Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            pfSw.Click += async (s, e2) =>
            {
                viewLibrary = false;
                await Exec(pfSw.IsChecked == true ? "profiles-on" : "profiles-off");
                Refresh();
            };
            body.Children.Add(pfLine);
            body.Children.Add(Small(Texts.Get("set_profiles_note"), new Thickness(0, 8, 0, 0)));

            // which way the list is shown: the winner at the bottom (the load order as it is) or at the top
            body.Children.Add(Label("set_order", new Thickness(0, 20, 0, 6)));
            var orders = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
            var two = new StackPanel { Orientation = Orientation.Horizontal };
            var bottomWins = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "setorder", Content = Texts.Get("set_order_bottom"), MinWidth = 96, IsChecked = !Texts.TopWins };
            var topWinsBtn = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "setorder", Content = Texts.Get("set_order_top"), MinWidth = 96, IsChecked = Texts.TopWins };
            void Direction(bool top)
            {
                if (Texts.TopWins == top) return;
                Texts.ShowTopWins(top);
                rows.Clear();
                List.Children.Clear();
                Words();
                if (st != null) Render();
            }
            bottomWins.Click += (s, e2) => Direction(false);
            topWinsBtn.Click += (s, e2) => Direction(true);
            two.Children.Add(bottomWins);
            two.Children.Add(topWinsBtn);
            orders.Child = two;
            body.Children.Add(orders);
            body.Children.Add(Small(Texts.Get("set_order_note"), new Thickness(0, 8, 0, 0)));

            // the explaining lines
            body.Children.Add(Label("set_hints", new Thickness(0, 20, 0, 6)));
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = Texts.Hints, VerticalAlignment = VerticalAlignment.Center };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(sw);
            line.Children.Add(new TextBlock { Text = Texts.Get("set_hints_on"), Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            sw.Click += (s, e2) =>
            {
                Texts.ShowHints(sw.IsChecked == true);
                rows.Clear();
                List.Children.Clear();
                Words();
                if (st != null) Render();
                if (TagsPage.Visibility == Visibility.Visible) RenderTags();
            };
            body.Children.Add(line);
            body.Children.Add(Small(Texts.Get("set_hints_note"), new Thickness(0, 8, 0, 0)));

            // the jump button by the pointer on a fast wheel (Fling)
            body.Children.Add(Label("set_fling", new Thickness(0, 20, 0, 6)));
            var fsw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = Texts.FlingButton, VerticalAlignment = VerticalAlignment.Center };
            var fline = new StackPanel { Orientation = Orientation.Horizontal };
            fline.Children.Add(fsw);
            fline.Children.Add(new TextBlock { Text = Texts.Get("set_fling_on"), Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            fsw.Click += (s, e2) =>
            {
                Texts.ShowFlingButton(fsw.IsChecked == true);
                if (!Texts.FlingButton) HideFling();
            };
            body.Children.Add(fline);
            body.Children.Add(Small(Texts.Get("set_fling_note"), new Thickness(0, 8, 0, 0)));

            // this program's own updates (the user, 2026-10-25)
            body.Children.Add(Label("set_update", new Thickness(0, 20, 0, 6)));
            body.Children.Add(new TextBlock
            {
                Text = Texts.Get("title") + "  " + StateDoc.Version + "  ·  " + Texts.Get(SelfUpdate.Official ? "build_official" : "build_dev") +
                       (HasNexus ? "" : "  " + Texts.Get("set_no_nexus")),
                FontFamily = Mono, FontSize = 13, Foreground = B("Ink"), Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
            });
            var upBox = new StackPanel();
            body.Children.Add(upBox);
            string failed = null;
            bool checking = false;
            void FillUpdate()
            {
                upBox.Children.Clear();
                var u = SelfUpdate.Last();
                string when = u.CheckedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(u.CheckedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : null;
                string said = checking ? Texts.Get("self_checking")
                    : failed != null ? Texts.F("self_failed", failed)
                    : u.Newer ? Texts.F("self_new", u.Latest + (u.Prerelease ? Texts.Get("self_pre") : ""))
                    : when == null ? Texts.Get("self_none")
                    : u.Latest == null ? Texts.F("self_no_release", when)
                    : Texts.F("self_current", when);
                upBox.Children.Add(new TextBlock
                {
                    Text = said, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                    Foreground = B(u.Newer && !checking && failed == null ? "Yellow" : "Ink"),
                    FontWeight = u.Newer && !checking && failed == null ? FontWeights.SemiBold : FontWeights.Normal,
                });
                var btns = new StackPanel { Orientation = Orientation.Horizontal };
                if (u.Newer && !string.IsNullOrEmpty(u.Page))
                {
                    var open = new Button { Content = Texts.Get("self_open"), Style = (Style)FindResource("Primary"), MinHeight = 36, Margin = new Thickness(0, 0, 8, 0), ToolTip = u.Page };
                    var page = u.Page;
                    open.Click += (s, e2) => Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
                    btns.Children.Add(open);
                }
                var check = new Button { Content = Texts.Get("self_check"), Style = (Style)FindResource("Ghost"), MinHeight = 36, IsEnabled = !checking };
                check.Click += async (s, e2) =>
                {
                    checking = true;
                    failed = null;
                    FillUpdate();
                    try { await Task.Run(() => SelfUpdate.Check()); }
                    catch (ModError err) { failed = CheckWhy(err.Kind); }
                    catch (Exception err) when (err is IOException || err is UnauthorizedAccessException || err is TaskCanceledException) { failed = Texts.Get("up_why_offline"); }
                    checking = false;
                    FillUpdate();
                    ShowUpdateDot();
                };
                btns.Children.Add(check);
                upBox.Children.Add(btns);
            }
            FillUpdate();
            var autoSw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = SelfUpdate.Auto, VerticalAlignment = VerticalAlignment.Center };
            var autoLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            autoLine.Children.Add(autoSw);
            autoLine.Children.Add(new TextBlock { Text = Texts.Get("self_auto"), Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            autoSw.Click += (s, e2) =>
            {
                try { SelfUpdate.Auto = autoSw.IsChecked == true; }
                catch (Exception err) when (err is IOException || err is UnauthorizedAccessException) { autoSw.IsChecked = SelfUpdate.Auto; }
            };
            body.Children.Add(autoLine);
            body.Children.Add(Small(Texts.Get(SelfUpdate.Official ? "self_auto_note_official" : "self_auto_note_dev"), new Thickness(0, 8, 0, 0)));

            // a problem report to send with a question (the user, 2026-10-29: one click, here)
            body.Children.Add(Label("set_report", new Thickness(0, 20, 0, 6)));
            body.Children.Add(Small(Texts.Get("report_note"), new Thickness(0, 0, 0, 10)));
            var reportBox = new StackPanel();
            body.Children.Add(reportBox);
            string reportPath = null, reportFailed = null;
            bool packing = false;
            void FillReport()
            {
                reportBox.Children.Clear();
                if (packing) reportBox.Children.Add(Small(Texts.Get("report_making"), new Thickness(0, 0, 0, 10)));
                else if (reportFailed != null)
                    reportBox.Children.Add(new TextBlock { Text = Texts.F("failed", reportFailed), Foreground = B("Yellow"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
                else if (reportPath != null)
                {
                    reportBox.Children.Add(new TextBlock { Text = Texts.Get("report_done"), Foreground = B("Ink"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
                    var where = Selectable.Text(reportPath, 11, "Muted", Mono);
                    where.Margin = new Thickness(0, 0, 0, 10);
                    reportBox.Children.Add(where);
                }
                var btns = new StackPanel { Orientation = Orientation.Horizontal };
                var make = new Button { Content = Texts.Get("report_do"), Style = (Style)FindResource("Ghost"), MinHeight = 36, IsEnabled = !packing };
                make.Click += async (s, e2) =>
                {
                    packing = true;
                    reportFailed = null;
                    FillReport();
                    (string Path, string Summary) made = default;
                    var r = await Queued(() => Cli.Call(() => { made = Report.Write(Library.Load(), Store.GameDir(null, false)); return made.Path; }));
                    packing = false;
                    if (r.Ok)
                    {
                        reportPath = made.Path;
                        try { Clipboard.SetText(made.Summary); } catch (Exception) { }      // another program holding the clipboard: the zip is enough
                        ShowInFolder(reportPath);
                    }
                    else { reportPath = null; reportFailed = r.Message; }
                    FillReport();
                };
                btns.Children.Add(make);
                if (reportPath != null && !packing)
                {
                    var show = new Button { Content = Texts.Get("report_open"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
                    show.Click += (s, e2) => ShowInFolder(reportPath);
                    btns.Children.Add(show);
                }
                reportBox.Children.Add(btns);
            }
            FillReport();

            // who made it, and where it comes from (last)
            body.Children.Add(Label("set_about", new Thickness(0, 20, 0, 6)));
            // which library this window is reading. Two windows on two libraries look exactly
            // alike, and one of them is then showing a library nobody else has (the user,
            // 2026-10-25)
            body.Children.Add(new TextBlock
            {
                Text = Cli.LibraryFile, FontFamily = Mono, FontSize = 11, Foreground = B("Muted"),
                Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(Link(Texts.Get("author_name"), Texts.Get("author_url")));
            body.Children.Add(Link(Texts.Get("project_name"), Texts.Get("project_url")));
        }

        /// <summary>The mods of a conflict as lines: picture, name, the one that wins now marked
        /// (tagKey). A click on another makes it win: it moves just after the last of the others in
        /// the load order (the last one wins); the others keep their places.</summary>
        UIElement WinnerRows(List<string> ids, string now, string tagKey)
        {
            var mods = ids.Select(id => st.Mods.FirstOrDefault(x => x.Id == id)).Where(x => x != null).ToList();
            var rowsBox = new StackPanel();
            foreach (var x in mods)
            {
                bool wins = x.Id == now;
                var line = new DockPanel();
                var pic = Picture(x.Icon, GlyphMod, 28, "Blue", 18);
                DockPanel.SetDock(pic, Dock.Left);
                line.Children.Add(pic);
                if (wins)
                {
                    var tag = new Border { Background = B("Yellow"), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                                          Child = new TextBlock { Text = Texts.Get(tagKey), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = B("TapeInk") } };
                    DockPanel.SetDock(tag, Dock.Right);
                    line.Children.Add(tag);
                }
                line.Children.Add(new TextBlock { Text = x.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                                                  TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = x.Name });
                var row = new Border
                {
                    BorderBrush = B(wins ? "Yellow" : "Grid"), BorderThickness = new Thickness(1), Background = B("Ground900"),
                    Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6), Child = line, Cursor = wins ? Cursors.Arrow : Cursors.Hand,
                };
                if (!wins)
                {
                    row.MouseEnter += (s, e) => row.BorderBrush = B("Muted");
                    row.MouseLeave += (s, e) => row.BorderBrush = B("Grid");
                    var id = x.Id;
                    row.MouseLeftButtonUp += (s, e) =>
                    {
                        var last = mods.Where(o => o.Id != id).OrderBy(o => o.Index).Last();
                        if (mods.First(o => o.Id == id).Index < last.Index) Run("move", id, "--after", last.Id);
                    };
                }
                rowsBox.Children.Add(row);
            }
            return rowsBox;
        }

        /// <summary>A resource type as the conflicts name it: in words for the common ones, else the engine's name.</summary>
        static string TypeLabel(string type) => Texts.Has("rt_" + type) ? Texts.Get("rt_" + type) : type;

        /// <summary>A line that opens an address in the browser.</summary>
        UIElement Link(string text, string url)
        {
            var b = new Button { Template = Plain(), Cursor = Cursors.Hand, FocusVisualStyle = null, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2), ToolTip = url };
            b.Content = new TextBlock { Text = text, Foreground = B("Blue"), FontWeight = FontWeights.SemiBold };
            b.Click += (s, e) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return b;
        }

        /// <summary>Where the list was left, so a library that grows at the end does not have to
        /// be scrolled down again on every start (the user, 2026-10-25). Kept in gui.json, taken
        /// up once the first list has been drawn and only while nothing is filtered or
        /// searched.</summary>
        void SaveWhere()
        {
            try
            {
                var d = Store.Load("gui.json");
                d["list_offset"] = (long)Math.Round(ListScroll.VerticalOffset);
                // the filter and the labels as they were left (the user, 2026-10-26: probes left
                // out, or static assets only, should stay so)
                List<object> Of(IEnumerable<string> set) => set.Cast<object>().ToList();
                d["filters"] = new Dictionary<string, object>
                {
                    ["labels"] = Of(tagFilter), ["without"] = Of(tagExclude), ["from"] = Of(fromFilter), ["kind"] = Of(kindFilter),
                };
                Store.Save("gui.json", d);
            }
            catch (Exception x) when (x is IOException || x is UnauthorizedAccessException || x is FormatException) { }
        }

        bool filtersPruned;

        /// <summary>The filter and the labels the window was closed with, before the first list.</summary>
        void LoadFilters()
        {
            Dictionary<string, object> f;
            try { f = Json.Obj(Store.Load("gui.json"), "filters"); }
            catch (Exception x) when (x is IOException || x is UnauthorizedAccessException || x is FormatException) { return; }
            if (f == null) return;
            void Fill(HashSet<string> set, string key) { foreach (var v in Json.List(f, key).OfType<string>()) set.Add(v); }
            Fill(tagFilter, "labels");
            Fill(tagExclude, "without");
            Fill(fromFilter, "from");
            Fill(kindFilter, "kind");
            kindFilter.Remove("loader");                    // loaders are no longer in the list
        }

        bool wentBack;

        void WhereItWas()
        {
            if (wentBack || filter != "all" || searchWords.Length > 0) return;
            wentBack = true;
            long at;
            try { at = Json.Long(Store.Load("gui.json"), "list_offset"); }
            catch (Exception x) when (x is IOException || x is FormatException) { return; }
            if (at <= 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ListScroll.ScrollToVerticalOffset(Math.Min(at, ListScroll.ScrollableHeight));
            }), DispatcherPriority.Loaded);
        }

        void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        void Deploy_Click(object sender, RoutedEventArgs e)
        {
            if (st != null && st.GameDir == null) ChooseGame();
            else _ = DeployNow();
        }

        Task<bool> DeployNow() => Act(() => Cli.Call(() =>
        {
            var r = Hd2Mod.Deploy.Run(Library.Load(), Store.GameDir());
            return Texts.F("deployed", Texts.N(r.Kept), Texts.N(r.Moved), Texts.N(r.Written), Texts.N(r.Linked), Texts.N(r.Copied),
                           Texts.N(r.Deleted), Texts.N(r.Files));
        }), true);

        /// <summary>The game through Steam; changes not deployed yet are deployed first,
        /// when the player says so.</summary>
        async void Launch_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            if (Hd2Mod.Deploy.GameRunning()) { Dialog.Tell(this, Texts.Get("game_already")); return; }
            if (st != null && st.GameDir != null && st.Pending > 0)
            {
                if (!Dialog.Ask(this, Texts.F("launch_ask", st.Pending), Texts.Get("deploy_launch"))) return;
                if (!await DeployNow()) return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("steam://rungameid/553850") { UseShellExecute = true });
                Foot(Texts.Get("launching"), "Blue");
                WatchGame();
            }
            catch (Exception x) when (x is System.ComponentModel.Win32Exception || x is InvalidOperationException)
            {
                Dialog.Tell(this, Texts.Get("no_steam"));
            }
        }

        static void OpenFolder(string path)
        {
            if (Directory.Exists(path)) Process.Start("explorer.exe", Cli.Quote(path));
        }

        /// <summary>Explorer opened on a file's folder with the file selected.</summary>
        static void ShowInFolder(string file)
        {
            if (!File.Exists(file)) { OpenFolder(IOPath.GetDirectoryName(file)); return; }
            try { Process.Start("explorer.exe", "/select," + Cli.Quote(file)); }
            catch (System.ComponentModel.Win32Exception) { }
        }

        void Purge_Click(object sender, RoutedEventArgs e)
        {
            if (st != null && st.GameDir == null) ChooseGame();
            else if (Dialog.Ask(this, Texts.Get("purge_ask"), Texts.Get("purge")))
                _ = Act(() => Cli.Call(() => Texts.F("purged", Texts.N(Hd2Mod.Deploy.Purge(Store.GameDir())))), true);
        }

        void Add_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = AddBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var zip = new MenuItem { Header = Texts.Get("add_zip") };
            zip.Click += (s, a) => { var p = PickZip(); if (p != null) Run("add", p); };
            var folder = new MenuItem { Header = Texts.Get("add_folder") };
            folder.Click += async (s, a) =>
            {
                var p = PickFolder();
                if (p == null) return;
                var before = new HashSet<string>(Library.Load().Mods.Select(m => m.Id));
                if (await Act(() => Cli.Run("add", p), false))
                    AdviseDrive(Library.Load().Mods.Where(m => !before.Contains(m.Id)).ToList());
            };
            menu.Items.Add(zip);
            menu.Items.Add(folder);
            var managers = new MenuItem { Header = Texts.Get("st_import") };
            managers.Click += (s, a) => ImportFromManagers(false);
            menu.Items.Add(managers);
            menu.IsOpen = true;
        }

        // ---------------------------------------------------------------- mods dropped on the list

        static readonly string[] Archives = { ".zip", ".7z", ".rar", ".tar", ".gz", ".xz" };

        /// <summary>What a drop from Explorer holds that can be installed: mod folders and
        /// archives, in the order the system gives them.</summary>
        static List<string> Dropped(IDataObject data)
        {
            var paths = data != null && data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;
            if (paths == null) return new List<string>();
            return paths.Where(p => Directory.Exists(p)
                                    || (System.IO.File.Exists(p) && Archives.Contains(IOPath.GetExtension(p).ToLowerInvariant()))).ToList();
        }

        /// <summary>While mods are dragged over the list: what dropping them would do, over
        /// the whole list, so there is no doubt that it takes them (the user, 2026-10-24).</summary>
        void List_DragOver(object sender, DragEventArgs e)
        {
            var many = Dropped(e.Data);
            bool ok = many.Count > 0 && !busy;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            DropHintText.Text = Texts.Get(ok ? "drop_here" : "drop_no");
            DropHintWhat.Text = ok ? Texts.F("drop_what", Texts.N(many.Count)) : Texts.Get(busy ? "working" : "drop_none");
            DropHint.Visibility = Visibility.Visible;
        }

        void List_DragLeave(object sender, DragEventArgs e)
        {
            DropHint.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        /// <summary>Mods dropped on the list: each archive or folder added to the library, in
        /// the order dropped, then the first of them chosen and the drive advice given once.</summary>
        async void List_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            DropHint.Visibility = Visibility.Collapsed;
            var many = Dropped(e.Data);
            if (many.Count == 0) { Dialog.Tell(this, Texts.Get("drop_none")); return; }
            if (busy) return;
            var before = new HashSet<string>(Library.Load().Mods.Select(m => m.Id));
            int added = 0;
            foreach (var p in many) if (await Act(() => Cli.Run("add", p), false)) added++;
            var fresh = Library.Load().Mods.Where(m => !before.Contains(m.Id)).ToList();
            if (fresh.Count == 0)
            {
                if (added > 0) Foot(Texts.F("drop_processed", Texts.N(added)), "Blue");
                return;
            }
            Current(fresh[0].Id);
            Foot(Texts.F("drop_processed", Texts.N(added)), "Blue");
            AdviseDrive(fresh);
        }

        string PickZip() =>
            Pick.File(this, Texts.Get("pick_zip_title"), Texts.Get("pick_zip_kind"), "*.zip;*.7z;*.rar;*.tar;*.gz;*.xz");

        string PickFolder(string titleKey = "pick_mod_title") => Pick.Folder(this, Texts.Get(titleKey));

        // ---------------------------------------------------------------- the window's own frame (as HD2 Mod Setting GUI)

        void Min_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        void Max_Click(object sender, RoutedEventArgs e) => ToggleMax();
        void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

        void ToggleMax()
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

        void FitMaximized()
        {
            bool max = WindowState == WindowState.Maximized;
            MaxGlyph.Data = Geometry.Parse(max ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z" : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
            if (max)
            {
                var src = PresentationSource.FromVisual(this);
                double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
                int px = GetSystemMetrics(32) + GetSystemMetrics(92);   // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
                Frame.Margin = new Thickness(px * sx);
                Frame.BorderThickness = new Thickness(0);
            }
            else
            {
                Frame.Margin = new Thickness(0);
                Frame.BorderThickness = new Thickness(1);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(Hook);
            acrylic = Backdrop.Enable(this);
            if (acrylic) Frame.Background = B("Ground900");
        }

        // Windows 11's snap layouts show only over a real maximize button: over ours the
        // system is told HTMAXBUTTON
        const int WM_NCHITTEST = 0x84, WM_NCMOUSELEAVE = 0x2A2, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2, WM_NCMOUSEMOVE = 0xA0, HTMAXBUTTON = 9;

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_NCHITTEST:
                    if (OverMax(lParam)) { SetMaxHover(true); handled = true; return new IntPtr(HTMAXBUTTON); }
                    SetMaxHover(false);
                    break;
                case WM_NCMOUSELEAVE:
                    SetMaxHover(false);
                    break;
                case WM_NCMOUSEMOVE:
                    if (wParam.ToInt32() != HTMAXBUTTON) SetMaxHover(false);
                    break;
                case WM_NCLBUTTONDOWN:
                    if (wParam.ToInt32() == HTMAXBUTTON) { handled = true; return IntPtr.Zero; }
                    break;
                case WM_NCLBUTTONUP:
                    if (wParam.ToInt32() == HTMAXBUTTON) { handled = true; ToggleMax(); return IntPtr.Zero; }
                    break;
            }
            return IntPtr.Zero;
        }

        bool OverMax(IntPtr lParam)
        {
            if (!MaxBtn.IsVisible) return false;
            int v = unchecked((int)lParam.ToInt64());
            var p = new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
            try
            {
                var local = MaxBtn.PointFromScreen(p);
                return local.X >= 0 && local.Y >= 0 && local.X < MaxBtn.ActualWidth && local.Y < MaxBtn.ActualHeight;
            }
            catch (InvalidOperationException) { return false; }
        }

        void SetMaxHover(bool on)
        {
            if (on == overMax) return;
            overMax = on;
            MaxBtn.Background = on ? B("Grid") : Brushes.Transparent;
        }
    }
}
