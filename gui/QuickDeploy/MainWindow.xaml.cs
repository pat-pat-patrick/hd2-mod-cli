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
        bool busy, overMax, acrylic, checking;
        // what was just done, for the bottom line: said by every state read in the next 10 s, so
        // the read the folder watcher starts after our own write does not put "pending" over it
        // at once (the MDL3 drop picture, 2026-11-01)
        string lastDone { get => doneLine; set { doneLine = value; doneAt = DateTime.Now; } }
        string doneLine;
        DateTime doneAt;
        // what the download under way is doing, while one is (NexusInstall)
        string fetchLine;
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

        /// <summary>The page tabs in the middle of the window while there is room, else as far
        /// right as the gear lets them; and the window no narrower than the title, the tabs and
        /// the gear side by side (the user, 2026-11-02: narrowed, the gear sat on the tabs). Run
        /// whenever one of them changes size: the language and the file count change their widths.</summary>
        void FitHeader()
        {
            var head = (FrameworkElement)PageBar.Parent;
            if (head.ActualWidth <= 0) return;
            const double gap = 24;
            double left = HeadTitle.TranslatePoint(new Point(HeadTitle.ActualWidth, 0), head).X + gap;
            double right = HeadRight.TranslatePoint(new Point(0, 0), head).X - gap;
            double w = PageBar.ActualWidth;
            double x = Math.Max(left, Math.Min((head.ActualWidth - w) / 2, right - w));
            if (Math.Abs(PageBar.Margin.Left - x) > 0.5) PageBar.Margin = new Thickness(x, 0, 0, 0);
            double need = left + w + (head.ActualWidth - right) + (ActualWidth - head.ActualWidth);
            double min = Math.Max(1200, Math.Ceiling(need));
            if (Math.Abs(MinWidth - min) > 0.5) MinWidth = min;
        }

        public MainWindow()
        {
            Mark("window made");
            InitializeComponent();
            RestorePlace();
            // the pages' text selects and copies across lines (the user, 2026-11-06); not the mod
            // list, whose rows are dragged to reorder
            foreach (var page in new FrameworkElement[] { Detail, LuaBox, Storages, TagsBox, Downloads, SettingsBox, PluginsBox })
                Selectable.Group(page);
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
                // the files as the last read found them: that read has shown them already. Our own
                // command's write was read twice, the list drawn twice (the user, 2026-11-06)
                if (!refreshing && Written() == readStamp) return;
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
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && !e.Handled && (SettingsPage.Visibility == Visibility.Visible || PluginsPage.Visibility == Visibility.Visible)) { ShowPage(pageBack); e.Handled = true; } };
            PreviewTextInput += TypeToSearch;
            PreviewMouseDown += (s, e) =>
            {
                pressClosedInfo = loaderPop != null && loaderPop.IsOpen;
                CloseLoaderInfo();
            };
            Deactivated += (s, e) => CloseLoaderInfo();
            LocationChanged += (s, e) => CloseLoaderInfo();
            SizeChanged += (s, e) => CloseLoaderInfo();
            foreach (var part in new FrameworkElement[] { HeadTitle, HeadRight, PageBar, (FrameworkElement)PageBar.Parent })
                part.SizeChanged += (s, e) => FitHeader();
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
            AddBtn.Content = Texts.Get("add");
            UpdatesBtn.Content = Texts.Get(checking ? "up_checking_btn" : "up_check");
            RefreshBtn.Content = Texts.Get("refresh");
            DeployBtn.Content = Texts.Get("deploy");
            LaunchBtn.Content = Texts.Get("launch");
            ListHint.Text = Texts.Get(groupView ? "g_hint" : Texts.TopWins ? "list_hint_top" : "list_hint");
            ViewGroups.Content = Texts.Get("view_groups");
            ViewOrder.Content = Texts.Get("view_order");
            PageDeploy.Content = Texts.Get("page_deploy");
            PageLua.Content = Texts.Get("page_lua");
            PageDownload.Content = Texts.Get("page_download");
            PageStorage.Content = Texts.Get("page_storage");
            PageTags.Content = Texts.Get("page_tags");
            // a build without downloads keeps the page for what needs no sign-in (the user,
            // 2026-11-04): a mod by its number, and the mods from Nexus Mods
            if (!HasNexus) PageDownload.Content = Texts.Get("page_nexus");
#if HAS_NEXUS
            SearchHint.Text = Texts.Get("search_hint");
#else
            SearchHint.Text = Texts.Get("search_hint_plain");
#endif
            SettingsBtn.ToolTip = Texts.Get("settings");
            PluginsBtn.ToolTip = Texts.Get("plugins");
            FilterWords();
            ListHint.Visibility = Texts.Hints ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The whole window in another language at once (a language tag), kept for the next start.</summary>
        void Language(string tag)
        {
            if (tag == Texts.Lang) { Words(); return; }
            Texts.Choose(tag);
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
            if (PluginsPage.Visibility == Visibility.Visible) RenderPlugins();
            PinnedPlugins();                    // their names, in this language
            Refresh();                          // what the plugins say of each mod, in this language
            RefreshListSoon(true);              // the official list's translations for it
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
            ConflictPickTest();
            ReportDialogTest();
            PreviewTest();
            PickTest();
            DragTest();
            DropTest();
            Listen();
            SelfUpdateAtStart();
            ModUpdatesAtStart();
            PluginsAtStart();
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
            NotesAtStart();
            if (!SelfUpdate.Due()) return;
            try { await Task.Run(() => SelfUpdate.Check()); }
            catch (Exception e) when (e is ModError || e is IOException || e is UnauthorizedAccessException || e is TaskCanceledException) { return; }
            ShowUpdateDot();
            NotesAtStart();
        }

        // the switch of each row on screen, for the click test
        readonly Dictionary<string, CheckBox> switches = new Dictionary<string, CheckBox>();

        /// <summary>HD2QD_CLICK_TEST=N (tests only): 3 s after the start, the switch of mod
        /// N is clicked the way a click does it, so a test outside can time what follows.</summary>
        void ClickTest()
        {
            var page = Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") ?? "";
            if (page.StartsWith("filter:"))
            {
                // "filter:conflict" (pictures only): the library's filter chosen 3 s after the start
                var tf = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                tf.Tick += (s, e) => { tf.Stop(); filter = page.Substring(7); if (st != null) { RenderNav(); RenderList(); } };
                tf.Start();
                return;
            }
            if (page.StartsWith("page:"))
            {
                // "page:settings" (pictures only): that page shown 3 s after the start;
                // "page:lua@900": and scrolled 900 down
                var tp = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                var at = page.Substring(5).Split('@');
                // "page:about": the settings on their second tab
                if (at[0] == "about") { settingsTab = "about"; at[0] = "settings"; }
                // "page:lua-mods", "page:lua-logs": the Lua loaders page on that tab
                if (at[0].StartsWith("lua-")) { luaTab = at[0].Substring(4); at[0] = "lua"; }
                tp.Tick += (s, e) =>
                {
                    tp.Stop();
                    ShowPage(at[0]);
                    if (at.Length > 1 && double.TryParse(at[1], out var y))
                        Dispatcher.BeginInvoke(new Action(() => OnShow().ScrollToVerticalOffset(y)), DispatcherPriority.Loaded);
                };
                tp.Start();
                return;
            }
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
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "groupmove")
            {
                // "groupmove": the first mod at the top level of the group view put into the first
                // group, as a drop does; whether the column shows it there at once, and how often
                // the state is drawn in the 4 s after (the user, 2026-11-06: twice, "卡卡的"), go
                // to HD2QD_WHEEL_OUT
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var t17 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                t17.Tick += (s, e) =>
                {
                    t17.Stop();
                    if (path == null || st == null) return;
                    var g = st.Groups.FirstOrDefault(x => x.Parent == null);
                    var m = st.Mods.FirstOrDefault(x => x.Group != g?.Id);
                    if (g == null || m == null) { System.IO.File.WriteAllText(path, "nothing to move\r\n"); return; }
                    var lines = new List<string> { "mod\t" + m.Name, "into\t" + g.Name };
                    int before = renders;
                    DropInto("mod", m.Id, g.Id);
                    lines.Add("group_at_once\t" + (st.Mods.First(x => x.Id == m.Id).Group == g.Id));
                    lines.Add("draws_at_once\t" + (renders - before));
                    var t18 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                    t18.Tick += (s2, e2) =>
                    {
                        t18.Stop();
                        lines.Add("state_draws_in_4s\t" + (renders - before));
                        lines.Add("group_after_read\t" + (st.Mods.First(x => x.Id == m.Id).Group == g.Id));
                        System.IO.File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
                    };
                    t18.Start();
                };
                t17.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "flicker")
            {
                // "flicker": each thing done to the list, the way a click does it, then every frame for
                // 3 s pictured by the window itself: the list and the details hashed, and each new look
                // saved. A look that comes after the first change has settled, or comes back, is the
                // list flickering (the user, 2026-11-06: "开关mod还会闪"). The draws of the state, the
                // list and the details are logged with what they made anew. Into the folder
                // HD2QD_WHEEL_OUT names
                var dir = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var log = new List<string>();
                drawLog = log;
                ModRow First(Func<ModRow, bool> where) => (groupView ? GroupShown().Where(x => x.Group == groupPath.LastOrDefault()) : Seen()).FirstOrDefault(where);
                var steps = new List<(string Name, Action Do)>
                {
                    ("order_read", () => { if (groupView) View_Click(ViewOrder, null); Refresh(); }),
                    ("order_toggle", () => { var m = First(x => !x.Loader); Change(m.Id, c => c.Enabled = !c.Enabled, m.Enabled ? "disable" : "enable", m.Id); }),
                    ("order_toggle_back", () => { var m = First(x => !x.Loader); Change(m.Id, c => c.Enabled = !c.Enabled, m.Enabled ? "disable" : "enable", m.Id); }),
                    ("order_select", () => Take(Seen().Skip(2).First())),
                    ("order_option", () =>
                    {
                        var m = st.Mods.FirstOrDefault(x => x.Options.Count > 0 && x.Options[0].Subs.Count == 0);
                        if (m == null) { log.Add("no mod with a plain option"); return; }
                        var o = m.Options[0];
                        bool on = !o.Enabled;
                        Change(m.Id, c => c.Options[0].Enabled = on, "set", m.Id, "1", on ? "on" : "off");
                    }),
                    ("order_move", () => { var m = Seen().Skip(1).First(); Run("move", m.Id, "--bottom"); }),
                    ("group_view", () => View_Click(ViewGroups, null)),
                    ("group_open", () => { var g = st.Groups.First(x => x.Parent == null && st.Mods.Any(m => m.Group == x.Id)); groupPath.Clear(); groupPath.Add(g.Id); selectedId = null; groupCol = 0; RenderGroups(); RenderDetail(); }),
                    ("group_select", () => { var m = First(x => true); groupCol = groupPath.Count; Take(m); RenderGroups(); }),
                    ("group_toggle", () => { var m = First(x => !x.Loader); Change(m.Id, c => c.Enabled = !c.Enabled, m.Enabled ? "disable" : "enable", m.Id); }),
                    ("group_toggle_back", () => { var m = First(x => !x.Loader); Change(m.Id, c => c.Enabled = !c.Enabled, m.Enabled ? "disable" : "enable", m.Id); }),
                    ("group_read", () => Refresh()),
                    ("scrolled_open", () =>
                    {
                        var g = st.Groups.OrderByDescending(x => st.Mods.Count(m => m.Group == x.Id)).First();
                        groupPath.Clear(); groupPath.Add(g.Id); selectedId = null; groupCol = 0; RenderGroups(); RenderDetail();
                    }),
                    ("scrolled_end", () =>
                    {
                        var sv = GroupCols.Children.OfType<Border>().Last().Child as ScrollViewer;
                        sv?.ScrollToEnd();
                        log.Add("   column: offset after scroll asked, extent " + sv?.ExtentHeight + ", view " + sv?.ViewportHeight);
                    }),
                    ("scrolled_toggle", () =>
                    {
                        var sv = GroupCols.Children.OfType<Border>().Last().Child as ScrollViewer;
                        log.Add("   column offset before " + sv?.VerticalOffset);
                        var m = GroupShown().Where(x => x.Group == groupPath.LastOrDefault() && !x.Loader).Last();
                        Change(m.Id, c => c.Enabled = !c.Enabled, m.Enabled ? "disable" : "enable", m.Id);
                        var after = GroupCols.Children.OfType<Border>().Last().Child as ScrollViewer;
                        log.Add("   same scroll box: " + ReferenceEquals(sv, after) + ", offset now " + after?.VerticalOffset);
                    }),
                    ("scrolled_toggle_back", () =>
                    {
                        var m = GroupShown().Where(x => x.Group == groupPath.LastOrDefault() && !x.Loader).Last();
                        Change(m.Id, c => c.Enabled = !c.Enabled, m.Enabled ? "disable" : "enable", m.Id);
                    }),
                };
                int at = 0;
                string Hash(FrameworkElement e, string save)
                {
                    if (e == null || e.ActualWidth < 1 || e.ActualHeight < 1) return "-";
                    var bmp = new RenderTargetBitmap((int)e.ActualWidth, (int)e.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(e);
                    var px = new byte[(int)e.ActualWidth * (int)e.ActualHeight * 4];
                    bmp.CopyPixels(px, (int)e.ActualWidth * 4, 0);
                    ulong h = 1469598103934665603;
                    for (int i = 0; i < px.Length; i += 4) { h ^= (ulong)(px[i] | px[i + 1] << 8 | px[i + 2] << 16); h *= 1099511628211; }
                    var key = h.ToString("x16").Substring(0, 8);
                    if (save != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, save + "_" + key + ".png")))
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(bmp));
                        using (var f = System.IO.File.Create(System.IO.Path.Combine(dir, save + "_" + key + ".png"))) enc.Save(f);
                    }
                    return key;
                }
                var t26 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                t26.Tick += (s, e) =>
                {
                    t26.Interval = TimeSpan.FromMilliseconds(3600);
                    if (dir == null || st == null) return;
                    if (at >= steps.Count)
                    {
                        t26.Stop();
                        drawLog = null;
                        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "log.txt"), string.Join("\r\n", log) + "\r\n");
                        return;
                    }
                    var (name, act) = steps[at++];
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    string lastList = Hash(ListArea, null), lastDetail = Hash(DetailScroll, null);
                    log.Add("== " + name + "   list " + lastList + "  details " + lastDetail);
                    drawClock.Restart();
                    try { act(); }
                    catch (Exception x) { log.Add("   " + x.GetType().Name + " " + x.Message); }
                    EventHandler frame = null;
                    frame = (s2, e2) =>
                    {
                        if (clock.ElapsedMilliseconds > 3000) { CompositionTarget.Rendering -= frame; return; }
                        var l = Hash(ListArea, name + "_list");
                        var d = Hash(DetailScroll, name + "_details");
                        if (l != lastList || d != lastDetail)
                            log.Add(string.Format("   {0,5} ms  list {1}{2}  details {3}{4}", clock.ElapsedMilliseconds, l, l != lastList ? "*" : " ", d, d != lastDetail ? "*" : " "));
                        lastList = l; lastDetail = d;
                    };
                    CompositionTarget.Rendering += frame;
                };
                t26.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "quickpress")
            {
                // "quickpress": presses on a mod's line in the group view and in the order, as the
                // mouse sends them: a second quick press (ClickCount 2) on its switch, its picture and
                // its name, and a click on its picture. Only the name renames; the picture's click
                // opens the preview (the user, 2026-11-06: quick clicks on the switch renamed; the
                // picture should open the preview). Each window opened is listed and closed. To
                // HD2QD_WHEEL_OUT
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var lines = new List<string>();
                async System.Threading.Tasks.Task Press(string what, FrameworkElement at, int clicks, bool release, Action between = null)
                {
                    var opened = new List<string>();
                    var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                    close.Tick += (s2, e2) =>
                    {
                        close.Stop();
                        foreach (Window w in Application.Current.Windows)
                            if (w != this && w.IsVisible) { opened.Add(w == preview ? "preview" : "another window"); w.Close(); }
                    };
                    close.Start();
                    var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
                    typeof(MouseButtonEventArgs).GetProperty("ClickCount").GetSetMethod(true).Invoke(down, new object[] { clicks });
                    at.RaiseEvent(down);
                    if (!down.Handled)
                    {
                        var down2 = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent };
                        typeof(MouseButtonEventArgs).GetProperty("ClickCount").GetSetMethod(true).Invoke(down2, new object[] { clicks });
                        at.RaiseEvent(down2);
                    }
                    between?.Invoke();
                    if (release)
                    {
                        // the release goes where the mouse sends it: to the element holding the mouse
                        // when one does (the order's line, a button), else to the one under the pointer
                        var to = Mouse.Captured as UIElement ?? at;
                        if (to != at) lines.Add("   released on " + to.GetType().Name + ", which holds the mouse");
                        to.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
                        to.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                        if (Mouse.Captured != null) Mouse.Capture(null);
                    }
                    await System.Threading.Tasks.Task.Delay(1000);
                    lines.Add(string.Format("{0,-34} windows opened: {1}", what, opened.Count == 0 ? "none" : string.Join(",", opened)));
                }
                // the pointer over the middle of what is pressed: the window is off the screen
                PointerOn = el => new Point(el.ActualWidth / 2, el.ActualHeight / 2);
                var t27 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                t27.Tick += async (s, e) =>
                {
                    t27.Stop();
                    if (st == null) { System.IO.File.WriteAllText(path, "no state"); return; }
                    try
                    {
                        View_Click(ViewGroups, null);
                        var g = st.Groups.OrderByDescending(x => st.Mods.Count(m => m.Group == x.Id)).First();
                        groupPath.Clear(); groupPath.Add(g.Id); selectedId = null; groupCol = 0; RenderGroups(); RenderDetail();
                        await System.Threading.Tasks.Task.Delay(500);
                        FrameworkElement Col() => GroupCols.Children.OfType<Border>().Last();
                        CheckBox Sw() => Motion.Under<CheckBox>(Col()).First(c => c.Style == FindResource("Switch"));
                        bool was = Sw().IsChecked == true;
                        await Press("groups: switch, second press", (Sw().Template?.FindName("knob", Sw()) as FrameworkElement) ?? Sw(), 2, false);
                        await Press("groups: picture, second press", Motion.Under<FrameworkElement>(Col()).First(f => f.Name == OpensPreview), 2, false);
                        await Press("groups: name, second press", Motion.Under<TextBlock>((DependencyObject)Sw().Parent).First(t => t.FontWeight == FontWeights.SemiBold), 2, false);
                        selectedId = null; RenderGroups(); RenderDetail();
                        await Press("groups: picture, click", Motion.Under<FrameworkElement>(Col()).First(f => f.Name == OpensPreview), 1, true);
                        lines.Add("   chosen after the click: " + (st.Mods.FirstOrDefault(x => x.Id == selectedId)?.Name ?? "none") + "; switch unchanged " + (Sw().IsChecked == was));

                        View_Click(ViewOrder, null);
                        selectedId = null; RenderList(); RenderDetail();
                        await System.Threading.Tasks.Task.Delay(500);
                        FrameworkElement Pic() => Motion.Under<FrameworkElement>(ListArea).First(f => f.Name == OpensPreview);
                        await Press("order: picture, second press", Pic(), 2, false);
                        var pick = Motion.Under<Button>(ListArea).First(x => Motion.Under<FrameworkElement>(x).Contains(Pic()));
                        var name = Motion.Under<TextBlock>(pick).First(t => !string.IsNullOrWhiteSpace(t.Text) && !Motion.Under<TextBlock>(Pic()).Contains(t));
                        await Press("order: name, second press", name, 2, false);
                        selectedId = null; RenderList(); RenderDetail();
                        await System.Threading.Tasks.Task.Delay(300);
                        // a raised press leaves the real button up, so the line's button neither takes the
                        // mouse nor counts as pressed (ButtonBase reads Mouse.LeftButton): both done here,
                        // as a real press does
                        var row = Motion.Under<Button>(ListArea).First(x => Motion.Under<FrameworkElement>(x).Contains(Pic()));
                        void HoldLikeAPress()
                        {
                            row.CaptureMouse();
                            typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("SetIsPressed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(row, new object[] { true });
                            lines.Add("   the line holds the mouse: " + row.IsMouseCaptured);
                        }
                        await Press("order: picture, click", Pic(), 1, true, HoldLikeAPress);
                        lines.Add("   chosen after the click: " + (st.Mods.FirstOrDefault(x => x.Id == selectedId)?.Name ?? "none"));
                        // let go after the pointer left the picture: no preview
                        selectedId = null; RenderList(); RenderDetail();
                        await System.Threading.Tasks.Task.Delay(300);
                        row = Motion.Under<Button>(ListArea).First(x => Motion.Under<FrameworkElement>(x).Contains(Pic()));
                        PointerOn = el => new Point(-5, -5);
                        await Press("order: picture, let go off it", Pic(), 1, true, HoldLikeAPress);
                        PointerOn = el => new Point(el.ActualWidth / 2, el.ActualHeight / 2);
                        // the filters show the order too: the updates, the ones on, the ones off
                        foreach (var key in new[] { "on", "off" })
                        {
                            filter = key; selectedId = null; RenderNav(); RenderList(); RenderDetail();
                            await System.Threading.Tasks.Task.Delay(400);
                            if (!Motion.Under<FrameworkElement>(ListArea).Any(f => f.Name == OpensPreview && f.IsVisible)) { lines.Add("filter " + key + ": no mods"); continue; }
                            row = Motion.Under<Button>(ListArea).First(x => Motion.Under<FrameworkElement>(x).Contains(Pic()));
                            await Press("order, filter " + key + ": picture, click", Pic(), 1, true, HoldLikeAPress);
                        }
                        filter = "all";

                        // the conflicts: a mod's line there takes it to the details; its picture opens the preview
                        filter = "conflict"; selectedId = null; RenderNav(); RenderList(); RenderDetail();
                        await System.Threading.Tasks.Task.Delay(500);
                        var cpics = Motion.Under<FrameworkElement>(ListArea).Where(f => f.Name == OpensPreview && f.IsVisible).ToList();
                        lines.Add("conflicts: " + cpics.Count + " pictures");
                        if (cpics.Count > 1)
                        {
                            await Press("conflicts: picture, second press", cpics[1], 2, false);
                            selectedId = null; RenderList(); RenderDetail();
                            await System.Threading.Tasks.Task.Delay(300);
                            cpics = Motion.Under<FrameworkElement>(ListArea).Where(f => f.Name == OpensPreview && f.IsVisible).ToList();
                            await Press("conflicts: picture, click", cpics[1], 1, true);
                            lines.Add("   chosen after the click: " + (st.Mods.FirstOrDefault(x => x.Id == selectedId)?.Name ?? "none"));
                        }
                    }
                    catch (Exception x) { lines.Add(x.GetType().Name + " " + x.Message + " " + x.StackTrace); }
                    System.IO.File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
                };
                t27.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "viewtimes")
            {
                // "viewtimes": the list's two views swapped as their switch does, from the view the
                // window opened in, six times: each timed from the click to its first frame on screen,
                // split into the draw, the layout and the frame, with the rows and items made anew (the
                // user, 2026-11-06: the first swap to the order after the window opened was slow). The
                // first swap is the one that counts. To HD2QD_WHEEL_OUT
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var lines = new List<string>();
                int round = 0;
                var t28 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("HD2QD_VIEW_WAIT") == "short" ? 2 : 8) };
                t28.Tick += (s, e) =>
                {
                    t28.Interval = TimeSpan.FromMilliseconds(1500);
                    if (path == null || st == null) return;
                    if (round >= 6)
                    {
                        t28.Stop();
                        lines.Add("the other view warmed at idle in " + warmedSteps + " steps, the longest " + warmedViewMs + " ms");
                        System.IO.File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
                        return;
                    }
                    if (round == 5 && !groupView)
                    {
                        // the order as the warm-up made it, then made anew from nothing: the same picture
                        string Hash()
                        {
                            UpdateLayout();
                            var bmp = new RenderTargetBitmap((int)ListArea.ActualWidth, (int)ListArea.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                            bmp.Render(ListArea);
                            var px = new byte[(int)ListArea.ActualWidth * (int)ListArea.ActualHeight * 4];
                            bmp.CopyPixels(px, (int)ListArea.ActualWidth * 4, 0);
                            ulong h = 1469598103934665603;
                            foreach (var b in px) { h ^= b; h *= 1099511628211; }
                            return h.ToString("x16");
                        }
                        ListScroll.ScrollToEnd();
                        var warm = Hash();
                        rows.Clear(); List.Children.Clear(); RenderList();
                        ListScroll.ScrollToEnd();
                        lines.Add("order as warmed " + warm + ", made anew " + Hash() + (warm == Hash() ? "  same" : "  DIFFERENT"));
                    }
                    round++;
                    var to = groupView ? ViewOrder : ViewGroups;
                    string name = groupView ? "to order " : "to groups";
                    int built = rowsBuilt, boxes = 0;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    View_Click(to, null);
                    long drawn = clock.ElapsedMilliseconds;
                    UpdateLayout();
                    long laid = clock.ElapsedMilliseconds;
                    boxes = Motion.Under<FrameworkElement>(groupView ? (FrameworkElement)GroupCols : ListShown).Count();
                    EventHandler frame = null;
                    frame = (s2, e2) =>
                    {
                        CompositionTarget.Rendering -= frame;
                        lines.Add(string.Format("{0}  {1}\tdrawn {2} ms\tlaid out {3} ms\tframe {4} ms\trows made {5}\telements {6}",
                            round, name, drawn, laid, clock.ElapsedMilliseconds, rowsBuilt - built, boxes));
                    };
                    CompositionTarget.Rendering += frame;
                };
                t28.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "picring")
            {
                // "picring": a mod's picture in the groups and in the order, the pointer brought over
                // it and taken away as the mouse reports it: its frame's colour read on its outer line,
                // its middle and one pixel outside, at the start, halfway in, hovered and after, and
                // the frame's place before and after (nothing may move). Pictures of the line saved.
                // To HD2QD_WHEEL_OUT, pictures beside it
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var dir = System.IO.Path.GetDirectoryName(path);
                var lines = new List<string>();
                string Px(byte[] px, int stride, int x, int y) { int i = y * stride + x * 4; return px[i + 2].ToString("x2") + px[i + 1].ToString("x2") + px[i].ToString("x2"); }
                string Look(string name, FrameworkElement pic)
                {
                    var line = (FrameworkElement)VisualTreeHelper.GetParent(pic);
                    UpdateLayout();
                    // a picture of a visual keeps the visual's own offset in its parent (its margin)
                    var off = VisualTreeHelper.GetOffset(line);
                    var at = pic.TransformToAncestor(line).Transform(new Point(off.X, off.Y));
                    var bmp = new RenderTargetBitmap((int)(line.ActualWidth + off.X) * 2, (int)(line.ActualHeight + off.Y) * 2, 192, 192, PixelFormats.Pbgra32);
                    bmp.Render(line);
                    int stride = bmp.PixelWidth * 4;
                    var px = new byte[stride * bmp.PixelHeight];
                    bmp.CopyPixels(px, stride, 0);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var f = System.IO.File.Create(System.IO.Path.Combine(dir, "picring_" + name + ".png"))) enc.Save(f);
                    int x0 = (int)Math.Round(at.X * 2), y0 = (int)Math.Round(at.Y * 2), w = (int)Math.Round(pic.ActualWidth * 2);
                    int ym = y0 + w / 2;
                    return string.Format("frame at {0:0.#},{1:0.#} size {2:0.#}   left edge: outside {3} line {4} {5} inside {6} {7} {8}   top middle {9}",
                        at.X, at.Y, pic.ActualWidth, x0 > 0 ? Px(px, stride, x0 - 1, ym) : "-", Px(px, stride, x0, ym), Px(px, stride, x0 + 1, ym),
                        Px(px, stride, x0 + 2, ym), Px(px, stride, x0 + 3, ym), Px(px, stride, x0 + 5, ym), Px(px, stride, x0 + w / 2, y0));
                }
                async System.Threading.Tasks.Task Hover(string view, FrameworkElement pic)
                {
                    lines.Add(view + " before   " + Look(view + "_0_before", pic));
                    pic.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                    await System.Threading.Tasks.Task.Delay(45);
                    lines.Add(view + " 45 ms    " + Look(view + "_1_half", pic));
                    await System.Threading.Tasks.Task.Delay(300);
                    lines.Add(view + " hovered  " + Look(view + "_2_hovered", pic));
                    pic.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                    await System.Threading.Tasks.Task.Delay(400);
                    lines.Add(view + " left     " + Look(view + "_3_left", pic));
                }
                var t29 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                t29.Tick += async (s, e) =>
                {
                    t29.Stop();
                    if (st == null) { System.IO.File.WriteAllText(path, "no state"); return; }
                    try
                    {
                        lines.Add("blue is " + ((SolidColorBrush)B("Blue")).Color + ", the grey line " + ((SolidColorBrush)B("Grid")).Color);
                        View_Click(ViewGroups, null);
                        var g = st.Groups.OrderByDescending(x => st.Mods.Count(m => m.Group == x.Id)).First();
                        groupPath.Clear(); groupPath.Add(g.Id); selectedId = null; groupCol = 0; RenderGroups(); RenderDetail();
                        await System.Threading.Tasks.Task.Delay(1500);
                        await Hover("groups", Motion.Under<FrameworkElement>(GroupCols.Children.OfType<Border>().Last()).First(f => f.Name == OpensPreview));
                        View_Click(ViewOrder, null);
                        await System.Threading.Tasks.Task.Delay(1500);
                        await Hover("order", Motion.Under<FrameworkElement>(ListArea).Where(f => f.Name == OpensPreview).Skip(1).First());
                        filter = "conflict"; selectedId = null; RenderNav(); RenderList(); RenderDetail();
                        await System.Threading.Tasks.Task.Delay(1500);
                        await Hover("conflicts", Motion.Under<FrameworkElement>(ListArea).Where(f => f.Name == OpensPreview && f.IsVisible).Skip(1).First());
                    }
                    catch (Exception x) { lines.Add(x.GetType().Name + " " + x.Message + " " + x.StackTrace); }
                    System.IO.File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
                };
                t29.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "filtertimes")
            {
                // "filtertimes": the library's filters on the left clicked as a player does, All, On,
                // Off and back, twice round, in the order and in the groups: each timed from the click
                // to its first frame on screen, split into the draw, the layout and the frame, with the
                // rows made anew (the user, 2026-11-06: half a second each). To HD2QD_WHEEL_OUT
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var lines = new List<string>();
                var plan = new List<(string View, string Key)>();
                foreach (var view in new[] { "order", "groups" })
                    for (int r = 0; r < 2; r++)
                        foreach (var k in new[] { "on", "off", "all" }) plan.Add((view, k));
                int at = 0;
                var t30 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                t30.Tick += (s, e) =>
                {
                    t30.Interval = TimeSpan.FromMilliseconds(1200);
                    if (path == null || st == null) return;
                    if (at >= plan.Count)
                    {
                        t30.Stop();
                        try
                        {
                            View_Click(ViewOrder, null);
                            // a press on a row starts a drag in the full list only, filters kept rows or not
                            foreach (var key2 in new[] { "on", "all", "off", "all" })
                            {
                                filter = key2; RenderNav(); RenderList(); UpdateLayout();
                                var r0 = rows[Seen().Skip(1).First().Id].Row;
                                dragFrom = null;
                                r0.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
                                lines.Add("press on a row, filter " + key2 + ": drag " + (dragFrom != null ? "ready" : "not offered") + ", rows take drops " + r0.AllowDrop);
                                dragFrom = null;
                            }
                            // the full list as kept across the filters, then made anew from nothing: the same picture
                            string Hash()
                            {
                                UpdateLayout();
                                var bmp = new RenderTargetBitmap((int)ListArea.ActualWidth, (int)ListArea.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                                bmp.Render(ListArea);
                                var px = new byte[(int)ListArea.ActualWidth * (int)ListArea.ActualHeight * 4];
                                bmp.CopyPixels(px, (int)ListArea.ActualWidth * 4, 0);
                                ulong h = 1469598103934665603;
                                foreach (var b in px) { h ^= b; h *= 1099511628211; }
                                return h.ToString("x16");
                            }
                            foreach (var key2 in new[] { "all", "off" })
                            {
                                filter = key2; RenderNav(); RenderList();
                                ListScroll.ScrollToVerticalOffset(300);
                                var kept = Hash();
                                rows.Clear(); List.Children.Clear(); RenderList();
                                ListScroll.ScrollToVerticalOffset(300);
                                var anew = Hash();
                                lines.Add("filter " + key2 + " as kept " + kept + ", made anew " + anew + (kept == anew ? "  same" : "  DIFFERENT"));
                            }
                        }
                        catch (Exception x) { lines.Add(x.GetType().Name + " " + x.Message + " " + x.StackTrace); }
                        System.IO.File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
                        return;
                    }
                    var (view, key) = plan[at++];
                    if ((view == "groups") != groupView) { View_Click(view == "groups" ? ViewGroups : ViewOrder, null); lines.Add("-- " + view); return; }
                    var btn = Nav.Children.OfType<Button>().FirstOrDefault(b => Motion.Under<TextBlock>(b).Any(t => t.Text == Texts.Get("f_" + key)));
                    if (btn == null) { lines.Add("no button " + key); return; }
                    int built = rowsBuilt;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    drawLog = new List<string>(); drawClock.Restart();
                    btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    long drawn = clock.ElapsedMilliseconds;
                    UpdateLayout();
                    long laid = clock.ElapsedMilliseconds;
                    var log = drawLog; drawLog = null;
                    EventHandler frame = null;
                    frame = (s2, e2) =>
                    {
                        CompositionTarget.Rendering -= frame;
                        lines.Add(string.Format("{0,-6} {1,-4}\tdrawn {2} ms\tlaid out {3} ms\tframe {4} ms\trows made {5}\tshown {6}",
                            view, key, drawn, laid, clock.ElapsedMilliseconds, rowsBuilt - built, groupView ? GroupShown().Count() : Seen().Count()));
                        foreach (var l in log.Where(x => !x.StartsWith("   rebuilt")).Take(8)) lines.Add("        " + l);
                        foreach (var l in log.Where(x => x.StartsWith("   rebuilt")).Take(3)) lines.Add("        " + l);
                    };
                    CompositionTarget.Rendering += frame;
                };
                t30.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "storagetick")
            {
                // "storagetick": the storage page, then the first mod's tick clicked as a player does,
                // on and off: from the click to the frame that shows it, and whether the page was
                // made anew (the user, 2026-11-06: the tick felt sticky). To HD2QD_WHEEL_OUT
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var lines = new List<string>();
                int round = 0;
                var t25 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                t25.Tick += (s, e) =>
                {
                    t25.Interval = TimeSpan.FromMilliseconds(800);
                    if (path == null || st == null) return;
                    if (round == 0) { ShowPage("storage"); round++; return; }
                    if (round > 4) { t25.Stop(); System.IO.File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n"); return; }
                    round++;
                    var firstChild = Storages.Children.Count > 0 ? Storages.Children[0] : null;
                    var tick = Motion.Under<CheckBox>(Storages).Where(c => c.Style == FindResource("Tick") && !(c.Content is TextBlock)).FirstOrDefault();
                    if (tick == null) { lines.Add("no tick"); return; }
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    tick.IsChecked = tick.IsChecked != true;
                    tick.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    UpdateLayout();
                    EventHandler frame = null;
                    frame = (s2, e2) =>
                    {
                        CompositionTarget.Rendering -= frame;
                        Dispatcher.BeginInvoke(new Action(() =>
                            lines.Add(string.Format("tick {0}: on screen {1} ms, page made anew: {2}, buttons: {3}", tick.IsChecked == true ? "on " : "off",
                                clock.ElapsedMilliseconds, Storages.Children.Count == 0 || Storages.Children[0] != firstChild,
                                string.Join(" | ", Motion.Under<Button>(Storages).Where(b => b.Content is string t && (t.Contains("(") || t.Contains("（"))).Select(b => (string)b.Content))))), DispatcherPriority.Background);
                    };
                    CompositionTarget.Rendering += frame;
                };
                t25.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "pagetimes")
            {
                // "pagetimes": every page opened as its button does, twice round, each timed from the
                // click to its first frame on screen: building it, laying it out, drawing it (the
                // user, 2026-11-06: Download, Storage and About took about a second); with how many
                // elements and text boxes it holds. To HD2QD_WHEEL_OUT
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var pages = new List<(string Name, Action Open)>
                {
                    ("deploy", () => ShowPage("deploy")), ("lua", () => ShowPage("lua")), ("download", () => ShowPage("download")),
                    ("storage", () => ShowPage("storage")), ("tags", () => ShowPage("tags")),
                    ("settings", () => { settingsTab = "general"; ShowPage("settings"); }),
                    ("about", () => { settingsTab = "about"; ShowPage("settings"); }),
                    ("plugins", () => ShowPage("plugins")),
                };
                var lines = new List<string>();
                int at = 0;
                var t24 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                t24.Tick += (s, e) =>
                {
                    t24.Interval = TimeSpan.FromMilliseconds(1500);
                    if (path == null || st == null) return;
                    if (at >= pages.Count * 2)
                    {
                        t24.Stop();
                        System.IO.File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
                        return;
                    }
                    var (name, open) = pages[at++ % pages.Count];
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    open();
                    long built = clock.ElapsedMilliseconds;
                    UpdateLayout();
                    long laid = clock.ElapsedMilliseconds;
                    EventHandler frame = null;
                    frame = (s2, e2) =>
                    {
                        CompositionTarget.Rendering -= frame;
                        // the frame after this one is the first with the page drawn
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            var page = PageOf(shownPage);
                            int all = page == null ? 0 : Motion.Under<FrameworkElement>(page).Count();
                            int boxes = page == null ? 0 : Motion.Under<TextBox>(page).Count();
                            lines.Add(string.Format("{0,-9} built {1,4} ms  laid out {2,4} ms  on screen {3,4} ms  elements {4,5}  text boxes {5,4}", name, built, laid, clock.ElapsedMilliseconds, all, boxes));
                        }), DispatcherPriority.Background);
                    };
                    CompositionTarget.Rendering += frame;
                };
                t24.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "abouttab")
            {
                // "abouttab": the settings' About and diagnostics tab opened as its button does, timed:
                // drawing it, then laying it out; the general tab the same (the user, 2026-11-06:
                // about a second before it showed); to HD2QD_WHEEL_OUT
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var t23 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
                t23.Tick += (s, e) =>
                {
                    t23.Stop();
                    if (path == null) return;
                    var lines = new List<string>();
                    ShowPage("settings");
                    foreach (var tab in new[] { "general", "about", "general", "about" })
                    {
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        settingsTab = tab;
                        RenderSettings();
                        long drawn = clock.ElapsedMilliseconds;
                        UpdateLayout();
                        lines.Add(tab + "\tdrawn " + drawn + " ms\tlaid out " + clock.ElapsedMilliseconds + " ms\ttext boxes " + Motion.Under<TextBox>(SettingsBox).Count());
                    }
                    System.IO.File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
                };
                t23.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "transitions")
            {
                // "transitions": one after another, what the window does on a click (the user,
                // 2026-11-06: "缺少过渡动画"), each pictured by the window itself 60 ms after it
                // began, mid-way, and once more when it is over; into the folder HD2QD_WHEEL_OUT
                // names, with a list of what was done and any error
                var dir = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var log = new List<string>();
                void Picture(string name)
                {
                    try
                    {
                        var dpi = VisualTreeHelper.GetDpi(this);
                        var bmp = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                        bmp.Render((Visual)Content);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(bmp));
                        using (var f = System.IO.File.Create(System.IO.Path.Combine(dir, name + ".png"))) enc.Save(f);
                    }
                    catch (Exception x) { log.Add("picture " + name + ": " + x.Message); }
                }
                var steps = new List<(string Name, Action Do)>
                {
                    ("page_lua", () => ShowPage("lua")),
                    ("page_deploy", () => ShowPage("deploy")),
                    ("open_group", () => { var g = st.Groups.First(x => x.Parent == null); groupPath.Clear(); groupPath.Add(g.Id); selectedId = null; groupCol = 0; RenderGroups(); RenderDetail(); }),
                    ("other_mod", () => { selectedId = st.Mods.First(x => x.Group == groupPath.LastOrDefault()).Id; RenderList(); RenderDetail(); }),
                    ("switch", () => { var m = st.Mods.First(x => x.Id == selectedId); Change(m.Id, c => c.Enabled = !c.Enabled, m.Enabled ? "disable" : "enable", m.Id); }),
                    ("view_order", () => View_Click(ViewOrder, null)),
                    ("filter_on", () => Nav.Children.OfType<Button>().ElementAt(2).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent))),
                    ("filter_all", () => Nav.Children.OfType<Button>().ElementAt(0).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent))),
                    ("expand", () =>
                    {
                        var row = rows[selectedId ?? Seen().First().Id].Row;
                        var chev = Motion.Under<Button>(row).First(b => b.Width == 36);
                        chev.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    }),
                    ("remove", () => { var m = st.Mods.First(x => x.Id == selectedId); _ = Gone(m, false, null, new[] { "remove", m.Id }); }),
                    ("dialog", () => Dispatcher.BeginInvoke(new Action(() => Dialog.Tell(this, "A dialog for the test.")))),
                };
                int at = 0;
                var t22 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
                t22.Tick += (s, e) =>
                {
                    t22.Interval = TimeSpan.FromMilliseconds(900);
                    if (dir == null || st == null) return;
                    if (at >= steps.Count)
                    {
                        t22.Stop();
                        foreach (Window w in OwnedWindows) w.Close();
                        log.Add("done");
                        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "log.txt"), string.Join("\r\n", log) + "\r\n");
                        return;
                    }
                    var (name, act) = steps[at++];
                    try { act(); log.Add(name); }
                    catch (Exception x) { log.Add(name + ": " + x.GetType().Name + " " + x.Message); }
                    var mid = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                    mid.Tick += (s2, e2) =>
                    {
                        mid.Stop();
                        Picture(name + "_mid");
                        // what is moving, mid-way: the page, the list, the details, a new column, a switch's knob
                        string X(UIElement u) => u == null ? "-" : u.Opacity.ToString("0.00") + (u.RenderTransform is TranslateTransform t ? "@" + t.X.ToString("0") + "," + t.Y.ToString("0") : "");
                        var lastCol = GroupCols.Children.OfType<Border>().LastOrDefault();
                        var knob = RowsShowing(selectedId ?? "").SelectMany(r => Motion.Under<CheckBox>(r)).Select(c => c.Template?.FindName("knob", c) as Border).FirstOrDefault(k => k != null);
                        log.Add("   mid: page " + X(PageOf(shownPage)) + "  list " + X(ListShown) + "  details " + X(Detail) + "  last column " + X(lastCol) + "  knob " + X(knob));
                    };
                    mid.Start();
                    var end = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                    end.Tick += (s2, e2) => { end.Stop(); Picture(name + "_end"); };
                    end.Start();
                };
                t22.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "deletemod")
            {
                // "deletemod": the chosen mod deleted from the disk as the question's yes does (run on
                // a library whose folders are copies); every draw of the state and of the list in the
                // 8 s after, with its time, goes to HD2QD_WHEEL_OUT (the user, 2026-11-06: the list
                // was drawn again after the row had gone)
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var t20 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                t20.Tick += (s, e) =>
                {
                    t20.Stop();
                    var m = st?.Mods.FirstOrDefault(x => x.Id == selectedId);
                    if (path == null || m == null) return;
                    drawLog = new List<string> { drawClock.ElapsedMilliseconds + "\tdelete " + m.Name };
                    DeleteNow(m);
                    var t21 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                    t21.Tick += (s2, e2) =>
                    {
                        t21.Stop();
                        drawLog.Add(drawClock.ElapsedMilliseconds + "\tend; in the library: " + st.Mods.Any(x => x.Name == m.Name));
                        System.IO.File.WriteAllText(path, string.Join("\r\n", drawLog) + "\r\n");
                    };
                    t21.Start();
                };
                t20.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "strike")
            {
                // "strike": the chosen mod's row struck through as a delete does, nothing deleted; the
                // picture is taken by the script once HD2QD_WHEEL_OUT says how many rows it drew on
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var t19 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                t19.Tick += async (s, e) =>
                {
                    t19.Stop();
                    if (path == null || selectedId == null) return;
                    var drawn = await StrikeOut(selectedId);
                    System.IO.File.WriteAllText(path, "rows\t" + drawn.Count + "\r\n");
                };
                t19.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "tagdrag")
            {
                // "tagdrag" (measuring only): the labels page, the first of my labels taken up and
                // carried below the last through the drag's own steps (a test move's position is
                // read off the real cursor, so press and moves are not sent as input), held 3 s
                // for a picture, then let go; my labels' order before and after to HD2QD_WHEEL_OUT
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var t20 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                t20.Tick += (s, e) =>
                {
                    t20.Stop();
                    ShowPage("tags");
                    UpdateLayout();
                    var log = new List<string> { "before\t" + string.Join(", ", tagRows.Select(r => r.Name)) };
                    if (tagRows.Count < 2) { if (path != null) System.IO.File.WriteAllText(path, "fewer than two labels\r\n"); return; }
                    var first = tagRows[0];
                    var last = tagRows[tagRows.Count - 1];
                    var from = first.Row.TranslatePoint(new Point(40, first.Row.ActualHeight / 2), tagHost);
                    // a real press: the label's button holds the mouse until the drag takes it
                    // (taken first: taking it raises a move, which in a test has no button down)
                    first.Row.CaptureMouse();
                    tagPress = (first.Name, from, first.Row.ActualHeight / 2);
                    log.Add("pressed\t" + (Mouse.Captured == first.Row));
                    if (!BeginTagDrag() || tagPress == null || tagGhost == null)
                    {
                        log.Add("drag ended at its start");
                        if (path != null) System.IO.File.WriteAllText(path, string.Join("\r\n", log) + "\r\n");
                        return;
                    }
                    TagDragAt(last.Row.TranslatePoint(new Point(40, last.Row.ActualHeight * 0.8), tagHost));
                    log.Add("line\t" + (tagLine?.Visibility == Visibility.Visible) + "\tcaptured\t" + (Mouse.Captured == tagHost));
                    UpdateLayout();
                    var gh = tagGhost as FrameworkElement;
                    log.Add("ghost\t" + gh?.ActualWidth + "x" + gh?.ActualHeight + " at " + Canvas.GetLeft(gh) + "," + Canvas.GetTop(gh) + " visible " + gh?.IsVisible
                            + "\tline at " + Canvas.GetTop(tagLine) + "\tlayer " + tagLayer.ActualWidth + "x" + tagLayer.ActualHeight + " children " + tagLayer.Children.Count);
                    if (path != null)
                    {
                        // the labels column as WPF draws it mid-drag, beside the output
                        var col = tagHost;
                        var bmp = new RenderTargetBitmap((int)col.ActualWidth * 2, (int)col.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
                        bmp.Render(col);
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(bmp));
                        using (var f = System.IO.File.Create(path + ".png")) png.Save(f);
                    }
                    var t21 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    t21.Tick += (s2, e2) =>
                    {
                        t21.Stop();
                        // the player's real mouse moving meanwhile moves the drop too: set again
                        if (tagGhost != null) TagDragAt(last.Row.TranslatePoint(new Point(40, last.Row.ActualHeight * 0.8), tagHost));
                        log.Add("dropping\t" + (tagDropOrder == null ? "-" : string.Join(", ", tagDropOrder)) + "\tbusy\t" + busy + "\tdragging\t" + (tagGhost != null));
                        EndTagDrag(true);
                        log.Add("at drop\t" + string.Join(", ", tagRows.Select(r => r.Name)));
                        var t22 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                        t22.Tick += (s3, e3) =>
                        {
                            t22.Stop();
                            log.Add("after\t" + string.Join(", ", tagRows.Select(r => r.Name)));
                            log.Add("ordered\t" + st?.TagsOrdered);
                            if (path != null) System.IO.File.WriteAllText(path, string.Join("\r\n", log) + "\r\n");
                        };
                        t22.Start();
                    };
                    t21.Start();
                };
                t20.Start();
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "selectmouse")
            {
                // "selectmouse": a drag through all of WPF's input (the user, 2026-11-06: selectacross
                // drove the group by itself and passed, while a real drag stayed in one line): raw
                // mouse reports handed to the input manager, as the window's own mouse messages are
                // (posted messages are dropped for a window the real cursor is not over). It checks
                // what broke: the pressed text box keeps the mouse, and the moves still reach the
                // group. Where a move points is read from the real cursor by WPF, so the pieces it
                // reaches are not the ones aimed at; that the selection spans pieces and copies as
                // lines is what counts. What is selected and what Ctrl+C copies (to a test sink,
                // never the clipboard) go to HD2QD_WHEEL_OUT
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var t13 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                t13.Tick += (s, e) =>
                {
                    t13.Stop();
                    if (path == null) return;
                    var found = new List<TextBox>();
                    void Walk(DependencyObject o)
                    {
                        if (o is TextBox tb && tb.Style == FindResource("Copyable")) found.Add(tb);
                        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++) Walk(VisualTreeHelper.GetChild(o, i));
                    }
                    Walk(Detail);
                    var view = DetailScroll;
                    // the first changed line near the top of the view
                    var line = found.FirstOrDefault(x => x.Text.StartsWith("Fixed:") || x.Text.StartsWith("New:") || x.Text.StartsWith("Changed:"));
                    if (line != null)
                    {
                        var top = line.TransformToAncestor(Detail).Transform(new Point(0, 0)).Y;
                        view.ScrollToVerticalOffset(Math.Max(0, top - 60));
                        view.UpdateLayout();
                    }
                    bool Shown(TextBox tb)
                    {
                        var r = tb.TransformToAncestor(view).TransformBounds(new Rect(0, 0, tb.ActualWidth, tb.ActualHeight));
                        return r.Top >= 0 && r.Bottom <= view.ViewportHeight;
                    }
                    var shown = found.Where(x => x.IsVisible && Shown(x)).ToList();
                    int first = shown.FindIndex(x => x.Text.StartsWith("Fixed:") || x.Text.StartsWith("New:") || x.Text.StartsWith("Changed:"));
                    var log = new List<string>();
                    if (first < 0 || first + 3 >= shown.Count) { System.IO.File.WriteAllText(path, "no lines to drag over\r\n"); return; }
                    var a = shown[first];
                    var b = shown[first + 3];
                    log.Add("from\t" + a.Text);
                    log.Add("to\t" + b.Text);
                    var dev = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
                    var from = dev.Transform(a.TranslatePoint(new Point(40, a.ActualHeight / 2), this));
                    var to = dev.Transform(b.TranslatePoint(new Point(b.ActualWidth / 2, b.ActualHeight / 2), this));
                    var keys = new byte[256];
                    TestInput.GetKeyboardState(keys);
                    keys[0x01] = 0x80;      // VK_LBUTTON down
                    TestInput.SetKeyboardState(keys);
                    var src = PresentationSource.FromVisual(this);
                    TestInput.Mouse(src, TestInput.Activate | TestInput.Move, from);
                    TestInput.Mouse(src, TestInput.Move | TestInput.Press, from);
                    // a real press makes the text box take the mouse (its own handler, run even for a
                    // handled press); this thread's button state reads "released" off-screen, so the
                    // text box skipped it here, and the test takes the mouse for it as it would
                    log.Add("captured_by_press	" + (Mouse.Captured as TextBox)?.Text);
                    if (!(Mouse.Captured is TextBox)) a.CaptureMouse();
                    int step = 0;
                    var t15 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                    t15.Tick += (s2, e2) =>
                    {
                        step++;
                        if (step <= 10) { TestInput.Mouse(src, TestInput.Move, new Point(from.X + (to.X - from.X) * step / 10, from.Y + (to.Y - from.Y) * step / 10)); return; }
                        t15.Stop();
                        keys[0x01] = 0;
                        TestInput.SetKeyboardState(keys);
                        TestInput.Mouse(src, TestInput.Move | TestInput.Release, to);
                        var t16 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                        t16.Tick += (s3, e3) =>
                        {
                            t16.Stop();
                            string copied = null;
                            Selectable.TestSink = x => copied = x;
                            foreach (var tb in found.Where(x => x.SelectionLength > 0)) log.Add("selected\t" + tb.SelectedText.Replace("\n", " "));
                            var focused = Keyboard.FocusedElement as TextBox;
                            log.Add("focused\t" + (focused?.Text ?? "(none)"));
                            if (focused != null) ApplicationCommands.Copy.Execute(null, focused);
                            log.Add("copied\t" + (copied ?? "(nothing)").Replace("\r\n", "\\n"));
                            System.IO.File.WriteAllText(path, string.Join("\r\n", log) + "\r\n");
                        };
                        t16.Start();
                    };
                    t15.Start();
                };
                t13.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "selectacross")
            {
                // "selectacross": in the chosen mod's details, a drag from the middle of the first
                // changed line to the middle of the fourth piece after it, as Selectable makes it;
                // what Ctrl+C on the first piece and the menu on the last then copy go to
                // HD2QD_WHEEL_OUT (the user, 2026-11-06: one line at a time before)
                var t12 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                t12.Tick += (s, e) =>
                {
                    t12.Stop();
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path == null) return;
                    DetailScroll.ScrollToEnd();
                    DetailScroll.UpdateLayout();
                    var found = new List<TextBox>();
                    void Walk(DependencyObject o)
                    {
                        if (o is TextBox tb && tb.Style == FindResource("Copyable")) found.Add(tb);
                        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++) Walk(VisualTreeHelper.GetChild(o, i));
                    }
                    Walk(Detail);
                    string copied = null;
                    Selectable.TestSink = x => copied = x;      // the player's clipboard is left alone
                    var lines = new List<string> { "found=" + found.Count };
                    int first = found.FindIndex(x => x.Text.StartsWith("Fixed:") || x.Text.StartsWith("New:") || x.Text.StartsWith("Changed:"));
                    if (first >= 0 && first + 4 < found.Count)
                    {
                        var a = found[first];
                        var b = found[first + 4];
                        var said = Selectable.TestDrag(Detail, a, a.Text.Length / 2, b, b.Text.Length / 2);
                        lines.Add("selection=" + said.Replace("\r\n", "\\n"));
                        a.Focus();
                        ApplicationCommands.Copy.Execute(null, a);
                        lines.Add("ctrl_c_same=" + (copied == said));
                        copied = null;
                        ((MenuItem)b.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        lines.Add("menu_same=" + (copied == said));
                        lines.Add("pieces_selected=" + found.Count(x => x.SelectionLength > 0));
                    }
                    System.IO.File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
                };
                t12.Start();
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
            if (settingsTest == "plugins")
            {
                // the plugins page, so a test outside can take its picture
                var g = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                g.Tick += (s, e) =>
                {
                    g.Stop();
                    ShowPage("plugins");
                    // HD2QD_OPEN_PLUGIN=AssetCheck: that plugin's window opened too, for its picture
                    var open = Environment.GetEnvironmentVariable("HD2QD_OPEN_PLUGIN");
                    var p = open == null ? default : PluginFiles.All().FirstOrDefault(x => string.Equals(x.File, "HD2QD." + open + ".dll", StringComparison.OrdinalIgnoreCase));
                    if (p.Type != null) PluginFiles.Call(p.Type, "Open", this, PluginHostHere());
                    // whether its window has the acrylic (a picture of a window does not show it)
                    var said = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (said != null)
                        Dispatcher.BeginInvoke(new Action(() => File.WriteAllText(said, string.Join("\n", Application.Current.Windows.OfType<ChromeWindow>()
                            .Select(w => w.Title + " | background " + w.Background + " | acrylic " + (w.Background == Brushes.Transparent))))), DispatcherPriority.ApplicationIdle);
                };
                g.Start();
                return;
            }
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
            SavePlace();
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
        // the state files as the last state read began (Written), set on the read's own thread
        volatile string readStamp;

        /// <summary>The files whose change is a change of the state the window shows.</summary>
        static readonly HashSet<string> StateFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "library.json", "deployed.json", "deploying.json", "config.json",
#if HAS_NEXUS
            "nexus.json",
#endif
        };

        /// <summary>The size and time of the files that hold the state (StateFiles), as one string.</summary>
        static string Written()
        {
            var out_ = new System.Text.StringBuilder();
            foreach (var name in StateFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
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
                    int readNo = ++readsStarted;
                    var r = await Queued(() => { readStamp = Written(); return Cli.Run("state"); });
                    beatSaw = readStamp;
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
                    read.ReadNo = readNo;
                    st = read;
                    stateJson = r.Out;
                    fromSaved = false;
                    Mark("state parsed");
                    Render();
                    Mark("rendered");
                    RememberTitles();
                    if (!st.OnOfficial && !st.TargetOffered) FallBack(st.Target);
                    if (!st.LuaPending) AdjoinPacks();
                    if (timing != null) Dispatcher.BeginInvoke(new Action(() => Mark("drawn")), DispatcherPriority.ContextIdle);
                    if (st.LuaPending) WarmLua();
                } while (refreshAgain);
            }
            finally { refreshing = false; }
            CollectWhenIdle();
            if (!setupDone)
            {
                setupDone = true;
                Setup();
                WarmPages();
            }
        }

        /// <summary>The pages behind the buttons built and laid out once the window is idle after it
        /// opens, one at a time, so the first click on each shows it at once: the first visit made
        /// their controls and pictures for the first time and took a quarter of a second (the user,
        /// 2026-11-06). Once a session; a page drawn since is left as it is. The list's other view
        /// first: the first swap from the groups to the order made its rows and took 0.75 s on 94
        /// mods, the other way 0.17 s (the user, 2026-11-06).</summary>
        void WarmPages()
        {
            var pages = new Queue<string>(new[] { "view", "storage", "download", "tags", "plugins" });
            void Next()
            {
                if (pages.Count == 0 || st == null) return;
                var page = pages.Dequeue();
                if (page == "view") { WarmOtherView(); Dispatcher.BeginInvoke(new Action(Next), DispatcherPriority.ApplicationIdle); return; }
                var el = PageOf(page);
                if (el != null && el.Visibility != Visibility.Visible && !PageFresh(page))
                {
                    if (page == "storage") RenderStorage();
                    else if (page == "download") RenderDownloads();
                    else if (page == "tags") RenderTags();
                    else if (page == "plugins") RenderPlugins();
                    // measured and arranged while hidden, then put away again
                    el.Visibility = Visibility.Hidden;
                    el.UpdateLayout();
                    if (shownPage != page) el.Visibility = Visibility.Collapsed;
                }
                Dispatcher.BeginInvoke(new Action(Next), DispatcherPriority.ApplicationIdle);
            }
            Dispatcher.BeginInvoke(new Action(Next), DispatcherPriority.ApplicationIdle);
        }

        /// <summary>The list's view not on show drawn and laid out while hidden, in the list's place:
        /// the first swap to it shows it at once. The order's rows a few at a time, each step when
        /// the window is idle, so none holds the window long: all at once they took 0.6 s on 94 mods.
        /// Stops when the list is not on show, shows the conflicts, a row is dragged, or the view
        /// was swapped meanwhile (RenderListNow then makes the rest).</summary>
        void WarmOtherView()
        {
            bool Can() => st != null && filter != "conflict" && dragRow == null && !gDragging && Body.Visibility == Visibility.Visible;
            if (!Can()) return;
            if (!groupView)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                RenderGroups();
                GroupScroll.Visibility = Visibility.Hidden;
                GroupScroll.UpdateLayout();
                ShowView();
                warmedViewMs = clock.ElapsedMilliseconds;
                warmedSteps = 1;
                return;
            }
            // the order's mods, as RenderListNow takes them, with the view switched only meanwhile
            List<string> ids;
            groupView = false;
            try { ids = Seen().Select(m => m.Id).ToList(); }
            finally { groupView = true; }
            int at = 0;
            warmedViewMs = 0; warmedSteps = 0;
            void Step()
            {
                if (!groupView || !Can() || at >= ids.Count) return;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                groupView = false;
                try
                {
                    for (int k = 0; k < 10 && at < ids.Count; k++, at++)
                    {
                        var m = st.Mods.FirstOrDefault(x => x.Id == ids[at]);
                        if (m == null) continue;
                        var sig = Sig(m);
                        if (!rows.TryGetValue(m.Id, out var had) || had.Sig != sig) { rows[m.Id] = had = (sig, Row(m)); rowsBuilt++; }
                        if (!List.Children.Contains(had.Row)) List.Children.Add(had.Row);
                    }
                }
                finally { groupView = true; }
                ListScroll.Visibility = Visibility.Hidden;
                ListScroll.UpdateLayout();
                ShowView();
                warmedViewMs = Math.Max(warmedViewMs, clock.ElapsedMilliseconds);
                warmedSteps++;
                if (at < ids.Count) Dispatcher.BeginInvoke(new Action(Step), DispatcherPriority.ApplicationIdle);
            }
            Step();
        }

        // the longest step of WarmOtherView and how many it took, for the "viewtimes" test
        long warmedViewMs = -1;
        int warmedSteps;

        /// <summary>Whether the page was last drawn for the state on show, in this language (and
        /// marks it so when it was not: the caller draws it now).</summary>
        bool PageFresh(string p)
        {
            var key = (st?.ReadNo ?? -1) + "|" + Texts.Lang + "|" + Texts.Hints + "|" + Texts.TopWins;
            if (pageDrawn.TryGetValue(p, out var was) && was == key) return true;
            pageDrawn[p] = key;
            return false;
        }

        bool setupDone;
        bool warming;
        // the state reads started so far: a read that began before a command finished does not
        // have its change yet (WinnerRows' choices wait for one that began after)
        int readsStarted;

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
        /// <summary>A full collection once the window is idle after a state read: a read allocates
        /// hundreds of megabytes (864 MB on 102 mods, 2.8 GB when every script is read again), and
        /// an idle window allocates nothing more, so no collection ever came and the last read's
        /// garbage stayed: 700 MB at rest on 102 mods where 75 MB is live, a player's window at
        /// 2.9 GB (2026-11-14). One collection per read, after it is drawn.</summary>
        void CollectWhenIdle()
        {
            if (collectQueued) return;
            collectQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                collectQueued = false;
                if (refreshing || warming) return;      // the read or the scan that is still on calls this again
                var clock = Stopwatch.StartNew();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                Mark("collected in " + clock.ElapsedMilliseconds + " ms");
            }), DispatcherPriority.ApplicationIdle);
        }
        bool collectQueued;

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
        /// <summary>A command, the window holding still meanwhile: it takes no clicks at once, and
        /// greys and says it is working only when the command takes longer than 0.3 s. Greyed at
        /// once, the whole list flashed grey and back for a command done in a tenth of a second (a
        /// move to the top, a rename, Prefer this; the user, 2026-11-06: "列表闪来闪去").</summary>
        async Task<CliResult> Exec(Func<CliResult> work)
        {
            busy = true;
            Body.IsHitTestVisible = DeployBtn.IsHitTestVisible = LaunchBtn.IsHitTestVisible = false;
            var slow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            slow.Tick += (s, e) =>
            {
                slow.Stop();
                if (!busy) return;
                SetEnabled(false);
                Foot(Texts.Get("working"), "Blue");
            };
            slow.Start();
            CliResult r;
            try { r = await Queued(work); }
            finally
            {
                slow.Stop();
                quietUntil = DateTime.Now.AddSeconds(1.5);
                busy = false;
                Body.IsHitTestVisible = DeployBtn.IsHitTestVisible = LaunchBtn.IsHitTestVisible = true;
                SetEnabled(true);
            }
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
        async Task<bool> Act(Func<CliResult> work, bool done, Action worked = null)
        {
            if (busy) return false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var r = await Exec(work);
            worked?.Invoke();
            if (r.Refused?.Kind == "maybe_update") r = await NewVersionOrNot(r);
            if (r.Refused?.Kind == "same_guid") r = await SameModOrNot(r);
            clock.Stop();
            if (Refused(r)) return false;
            if (done && r.Ok) lastDone = Texts.F("took", Last(r.Out), Seconds(clock.Elapsed.TotalSeconds));
            Refresh();
            var m3 = Mdl3Said(out bool bad);
            if (m3 != null)
            {
                mdl3Note = m3;
                if (bad) Dialog.Tell(this, m3);
                // said after the state read, which would otherwise put "pending" over it at once
                // (the MDL3 drop picture, 2026-11-01)
                else lastDone = lastDone != null ? lastDone + " · " + m3 : m3;
            }
            return r.Ok;
        }

        /// <summary>A mod added whose name alone matches one in the library (NewVersions): asked
        /// whether it is that mod's new version, then put in its place or added on its own; a
        /// cancel adds nothing (the user, 2026-11-01).</summary>
        async Task<CliResult> NewVersionOrNot(CliResult r)
        {
            var parts = r.Refused.Arg.Split('\n');
            if (parts.Length != 2) return r;
            var old = Library.Load().Mods.FirstOrDefault(m => m.Id == parts[0]);
            var name = IOPath.GetFileName(parts[1].TrimEnd('\\'));
            switch (Dialog.Choose(this, Texts.F("add_maybe_update", name, old?.Name ?? name),
                                  Texts.Get("add_as_new_version"), Texts.Get("add_as_own_mod"), Texts.Get("cancel")))
            {
                case 0: return await Exec(() => Cli.Run("replace", parts[0], parts[1]));
                case 1: return await Exec(() => Cli.Run("add", parts[1], "--new"));
                default: return new CliResult { Code = 0, Out = "" };
            }
        }

        /// <summary>What an add or a deploy did about MDL3's native part (Mdl3Native), said once.</summary>
        string mdl3Note;

        static string Mdl3Said(out bool bad)
        {
            var o = Mdl3Native.Last;
            Mdl3Native.Last = null;
            bad = o?.Kind == "failed";
            if (o == null) return null;
            return o.Kind == "installed" ? Texts.F("mdl3_installed", o.Version) : o.Kind == "waits" ? Texts.Get("mdl3_waits") : Texts.F("mdl3_failed", o.Detail);
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
            // its translation packs go off with it and come back with it, as the command line does
            // (the user, 2026-10-30)
            if (!m.Enabled)
                foreach (var p in st.Mods.Where(x => x.Lu?.PackOf == m.Id && x.Enabled)) { p.Enabled = false; p.Assumed = "off"; }
            else
                foreach (var p in st.Mods.Where(x => x.OffWith == m.Id && !x.Enabled)) { p.Enabled = true; p.Assumed = "pending"; }
            RenderList();
            if (id == selectedId) RenderDetail();
            // the switches of the row drawn anew slide over, as one clicked does (the user, 2026-11-06)
            var sw = (Style)FindResource("Switch");
            foreach (var row in RowsShowing(id, false))
                foreach (var c in Motion.Under<CheckBox>(row).Where(x => x.Style == sw)) Motion.Slide(c);
            if (id == selectedId)
                foreach (var c in Motion.Under<CheckBox>(Detail).Where(x => x.Style == sw)) Motion.Slide(c);
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
                case "download_cut": Dialog.Tell(this, Texts.Get("nx_download_cut")); break;
#endif
                case "not_zip":
                    {
                        var parts = r.Refused.Arg.Split(new[] { '\n' }, 2);
                        Dialog.Tell(this, Texts.F("unpack_failed", System.IO.Path.GetFileName(parts[0]), parts.Length > 1 && parts[1].Length > 0 ? parts[1] : Texts.Get("unpack_no_reason")));
                        break;
                    }
                case "archive_damaged": Dialog.Tell(this, Texts.F("unpack_damaged", System.IO.Path.GetFileName(r.Refused.Arg))); break;
                case "archive_password": Dialog.Tell(this, Texts.F("unpack_password", System.IO.Path.GetFileName(r.Refused.Arg))); break;
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
            if (!st.OnOfficial)
            {
                if (!st.TargetOffered) Foot(Texts.F("target_no_plugin", st.Target), "Yellow");
                else if (!st.TargetFound) Foot(Texts.F("target_not_found", st.TargetTitle), "Yellow");
                else if (st.TargetRunning) Foot(Texts.F("target_running", st.TargetTitle), "Yellow");
                else if (lastDone != null) Foot(Texts.F("done", lastDone), "Blue");
                else if (st.Pending == 0) Foot(Texts.F("target_synced", st.TargetTitle), "Blue");
                else Foot(Texts.F("target_pending", st.TargetTitle), "Yellow");
                if (DateTime.Now - doneAt > TimeSpan.FromSeconds(10)) doneLine = null;
                return;
            }
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
            // a download on its way: the list is free meanwhile, and a state read must not hide it
            else if (fetchLine != null) Foot(fetchLine, "Blue");
            else if (lastDone != null) Foot(Texts.F("done", lastDone), "Blue");
            // another program changed the game folder since the last deploy (the user,
            // 2026-11-03): the rows say which mods and how
            else if (st.Check.AlteredMods > 0 || st.Check.Added > 0)
                Foot(string.Join(Texts.Get("sep_sentence"), new[] { st.Check.AlteredMods > 0 ? Texts.F("altered_foot", st.Check.AlteredMods) : null,
                                              st.Check.Added > 0 ? Texts.F("added_foot", st.Check.Added) : null }.Where(t => t != null)), "Yellow");
            else if (st.Pending == 0) Foot(Texts.Get("synced"), "Blue");
            else Foot(Texts.F("pending", st.Written, st.Moved, st.Deleted, Texts.Size(st.CopiedBytes)), "Yellow");
            if (DateTime.Now - doneAt > TimeSpan.FromSeconds(10)) doneLine = null;
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
        int Num(ModRow m) => m.Index - (st?.Mods.Count(x => (x.Loader || ForElsewhere(x)) && x.Index < m.Index) ?? 0);

        // a loader is not in the list: the panel on the left holds the loaders (the user, 2026-10-26)
        /// <summary>In the list: no loader (they have their panel), in the profile, and made for the
        /// target in use or both (the user, 2026-11-12: HellForge's and the official game's mods
        /// were shown together; a mod for the other target is managed there).</summary>
        bool InScope(ModRow m) => st == null || (!m.Loader && !ForElsewhere(m) && (!st.ProfilesOn || viewLibrary || m.InProfile));

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
                case "mdl3": return "MDL3" + (version != null ? " " + version : "");
                case "uml": return "Universal Mod Loader" + (version != null ? " " + version : "");
                default: return null;
            }
        }

        ModRow ById(string id) => id == null ? null : st.Mods.FirstOrDefault(x => x.Id == id);

        /// <summary>The library's loaders with this capability (LoaderCaps), by name and version; one that is off says so.</summary>
        string[] LibraryLoaders(string cap) =>
            st.Mods.Where(x => x.Lu?.Loader != null && x.Lu.Caps.Contains(cap) && st.RunsLoader(x.Lu.Loader))
                   .Select(x => LoaderLabel(x.Lu.Loader, x.Lu.LoaderVersion) + (x.Id == st.LoaderChoice ? "" : st.LoaderHelpers.Contains(x.Id) ? Texts.Get("lua_helping_suffix") : Texts.Get("lua_off_suffix")))
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
        /// <summary>A loader deployed beside the one in effect: whose guest it runs as (MDL3) or whom it
        /// helps (Chef), and for which mods.</summary>
        string HelperSaid(ModRow x)
        {
            bool guest = x.LoaderKind == "mdl3";
            var helped = st.Mods.Where(y => y.Enabled && !y.Loader && (guest ? y.Lu?.UsesMdl3 == true : y.Lu?.Needs == "chef")).Select(y => y.Name);
            var chosen = ById(st.LoaderChoice);
            return Texts.F(guest ? "lua_info_guest" : "lua_info_helping", chosen != null ? LoaderLabel(chosen.LoaderKind, chosen.Lu?.LoaderVersion) ?? chosen.Name : "?",
                           string.Join(Texts.Sep, helped));
        }

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
                if (whole != null) Selectable.Menu(t, () => Selectable.Selection(t) ?? whole);
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
            // a loader deployed beside the one in effect (MDL3 as its guest, Chef handed to): its boot
            // runs too, so never "does not run" (the MDL3 drop picture, 2026-11-01)
            else if (m.Loader && m.Id != st.LoaderChoice && st.LoaderHelpers.Contains(m.Id)) Line(HelperSaid(m), "Blue");
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
                var able = LibraryLoaders(lu.Needs != "chef" ? LoaderCaps.Addons : lu.NeedsWhy == "name" ? LoaderCaps.ChefNames : LoaderCaps.ChefMods);
                if (lu.Needs == "chef")
                    Line(Texts.F((lu.NeedsWhy == "declared" ? "lua_needs_chef_declared" : lu.NeedsWhy == "api" ? "lua_needs_chef_api" : able.Length > 0 ? "lua_needs_chef_have" : "lua_needs_chef")
                                 + (able.Length > 0 ? "" : "_none"),
                                 string.Join(Texts.Sep, able)), "Muted");
                else Line(able.Length > 0 ? Texts.F("lua_needs_any_have", string.Join(Texts.Sep, able)) : Texts.Get("lua_needs_any_none"), "Muted");
                if (m.Enabled)
                {
                    var issues = IssuesOf(m).ToList();
                    if (issues.Any(i => i.Kind == "no_loader"))
                        Line(st.Lua.Running == "other" && owner != null ? Texts.F("lua_other_boot", owner.Name) : Texts.Get("lua_no_loader"), "Yellow");
                    else if (issues.Any(i => i.Kind == "needs_chef"))
                        Line(Texts.F("lua_wrong_loader", running ?? "-"), "Yellow");
                    // run by MDL3 in Chef's place: what it draws does not show yet
                    if (lu.Mdl3Chef) Line(Texts.Get("lua_mdl3_chef"), "Yellow");
                    // started by Chef Shared Loader, which reads no script over 4 MB (2026-10-26)
                    if (issues.Any(i => i.Kind == "too_big_for_chef")) Line(Texts.F("lua_too_big", Texts.Size(lu.Biggest)), "Yellow");
                    // the loader in effect is not repeated here: the card on the left says it (the user, 2026-10-26);
                    // a script name another mod declares too is its own card below, where it is chosen
                }
            }
            // a mod on MDL3's interface: where that comes from, or that nothing provides it (the MDL3
            // drop picture, 2026-11-01: without MDL3 the details said nothing)
            // (not on a target whose game runs no MDL3, such as HellForge's: there HD2Data comes from
            // its own port, and MDL3 is no choice to point at)
            if (lu.UsesMdl3 && m.Enabled && st.RunsLoader("mdl3"))
            {
                var mdl3 = st.Mods.FirstOrDefault(x => x.Loader && x.LoaderKind == "mdl3" && (x.Id == st.LoaderChoice || st.LoaderHelpers.Contains(x.Id)));
                if (mdl3 != null) Line(Texts.F("lua_mdl3_from", LoaderLabel(mdl3.LoaderKind, mdl3.Lu?.LoaderVersion) ?? mdl3.Name), "Muted");
                else Line(Texts.Get(st.Mods.Any(x => x.Loader && x.LoaderKind == "mdl3") ? "lua_mdl3_off" : "lua_mdl3_missing"), "Yellow");
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
                    else why = Texts.F("lua_broken_uses", b.Script, string.Join(Texts.Sep, b.Jit.Select(j => Texts.Get("lua_lib_" + j))));
                    var tb = new DockPanel();
                    var cannot = new TextBlock { Text = Texts.Get("lua_broken") + Texts.Get("sep_sentence"), FontSize = 12, Foreground = B("Red"), FontWeight = FontWeights.SemiBold };
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
                string sep = Texts.Sep;
                // what the game lacks, as clauses of one sentence: only what the scripts use
                var clauses = new List<string>();
                if (code > 0) clauses.Add(Texts.Get(code > 1 ? "lua_clause_code_n" : "lua_clause_code"));
                if (bytecode) clauses.Add(Texts.Get("lua_clause_bytecode"));
                if (used.Count > 0) clauses.Add(Texts.F("lua_clause_libs", string.Join(sep, used.Select(j => Texts.Get("lua_lib_" + j)))));
                string lacks = clauses.Count == 1 ? clauses[0]
                             : string.Join(Texts.Get("join"), clauses.Take(clauses.Count - 1)) + Texts.Get("join_last") + clauses.Last();
                var basis = new List<string>();
                // each loader in the library, as far as it is known what it gives, only for the libraries
                var known = st.Mods.Where(x => x.Lu?.Loader == "bml" || x.Lu?.Loader == "chef").Select(x => LoaderLabel(x.Lu.Loader, x.Lu.LoaderVersion)).Distinct().ToList();
                // which game, where there is more than one (a plugin's deploy target): the
                // official game's Lua 5.1, or the target's (HellForge's older game runs LuaJIT)
                var game = st.OnOfficial && st.TargetOffers.Count == 0 ? null
                         : st.OnOfficial ? Texts.Get("lua_game_official") : Texts.F("lua_game_target", st.TargetTitle ?? st.Target);
                basis.Add(used.Count > 0 && known.Count > 0
                    ? (game == null ? Texts.F("lua_basis_loaders", lacks, string.Join(sep, known)) : Texts.F("lua_basis_loaders_of", lacks, string.Join(sep, known), game))
                    : (game == null ? Texts.F("lua_basis", lacks) : Texts.F("lua_basis_of", lacks, game)));
                if (used.Count > 0)
                {
                    foreach (var x in st.Mods.Where(x => x.Lu?.Loader == "bsl"))
                        if (used.Contains("ffi")) basis.Add(Texts.F("lua_basis_bsl", LoaderLabel("bsl", x.Lu.LoaderVersion)));
                    foreach (var x in st.Mods.Where(x => x.Lu?.Boot == "other"))
                        basis.Add(Texts.F("lua_basis_unknown", x.Name));
                }
                box.Children.Add(Small(string.Join(Texts.Get("sep_sentence"), basis), new Thickness(0, 6, 0, 0)));   // as the language puts sentences together
            }
            // a translation pack, and the packs of a mod
            if (lu.PackOfName != null)
            {
                var of = ById(lu.PackOf);
                if (of == null) CopyLine(Texts.F("lua_pack_missing", lu.PackOfName), m.Enabled ? "Yellow" : "Muted");
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
            // a plugin's target loads Lua its own way (HellForge: Bingus Shared Loader only); its plugin says so on the rows
            if (!st.OnOfficial) { if (loaderPop != null) loaderPop.IsOpen = false; return; }
            var loaders = st.Mods.Where(x => x.Loader && st.RunsLoader(x.LoaderKind) && !ForElsewhere(x)).ToList();   // only those the target's game runs, made for it
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
                bool helping = !on && st.LoaderHelpers.Contains(x.Id);
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
                StaysOpen = true, AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
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
            var sep = Texts.Sep;
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
                // helping Boot Mod Loader: the mods it starts (Chef), or runs for (MDL3 as its guest), by
                // name (the panel says only "helping")
                if (!on && st.LoaderHelpers.Contains(x.Id)) Say(HelperSaid(x), "Blue");
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
            // Chef and MDL3 help only Boot Mod Loader; whether they still do, the state says
            if (x?.LoaderKind != "bml") st.LoaderHelpers.Clear();
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
        /// <summary>A tag for what a mod holds (LUA, or a plugin's such as HellForge's C), on a row.</summary>
        Border ContentTag(string text, string tip) => new Border
        {
            // the box is taller than the words it stands beside, so it is nudged up by
            // its own overhang: the ink of both then shares one middle
            BorderBrush = B("Blue"), BorderThickness = new Thickness(1), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(0, -0.75, 10, 0.75),
            VerticalAlignment = VerticalAlignment.Center, ToolTip = tip,
            Child = new TextBlock
            {
                Text = text, FontFamily = Mono, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("Blue"),
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
            },
        };

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
            foreach (var t in m.PluginTags) Add(t.Text);
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
                case "options": return m.Options.Count > 0;
                case "on": return m.Enabled;
                case "off": return !m.Enabled;
                case "conflict": return m.InConflict;
                case "broken": return m.Error != null;
                case "lua": return m.Lua;
                case "updates": return HasUpdate(m);
                case "norun": return NoRun(m) != null;
                default: return true;
            }
        }

        // how often the state was drawn: the window's own tests count it (groupmove)
        int renders;

        // when the window's own test asks: each draw of the state and of the list, with its time
        List<string> drawLog;
        readonly System.Diagnostics.Stopwatch drawClock = System.Diagnostics.Stopwatch.StartNew();

        // HD2QD_OPEN_MOD=<name> (pictures only): that mod chosen and opened at the start
        string openModAtStart = Environment.GetEnvironmentVariable("HD2QD_OPEN_MOD");

        void Render()
        {
            renders++;
            drawLog?.Add(drawClock.ElapsedMilliseconds + "\tstate");
            if (openModAtStart != null && st.Mods.FirstOrDefault(x => x.Name == openModAtStart) is ModRow startMod)
            {
                openModAtStart = null;
                open.Add(startMod.Id);
                selectedId = startMod.Id;
            }
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
            DeployBtn.Content = st.Pending > 0 ? Texts.F("deploy_n", st.Pending) : Texts.Get("deploy");
            DeployBtn.IsEnabled = st.Deploying == null && !fromSaved;   // two at once would undo each other; a saved list is not the library yet
            if (!st.OnOfficial)
            {
                SummaryText.Text = Texts.F("target_summary", st.TargetTitle ?? st.Target);
                SummaryText.Visibility = Visibility.Visible;
                // the target's game keeps the mods it started with (HellForge refuses changes then)
                DeployBtn.IsEnabled = !fromSaved && st.TargetOffered && st.TargetFound && !st.TargetRunning;
            }
            rowTarget = st.OnOfficial ? null : st.TargetTitle ?? st.Target;
            rowStates = st.OnOfficial ? null : st.TargetMods;
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
            if (LuaPage.Visibility == Visibility.Visible) RenderLua();
            // the labels page shows the new read too (a drag of labels put in order, 2026-11-14,
            // stayed in the old order until the page was opened again); not under a drag, which
            // the drawing would end: drawn once it is let go
            // nor while a label's note is being typed (its box is made anew): a later read draws it
            if (TagsPage.Visibility == Visibility.Visible)
            {
                bool noting = Keyboard.FocusedElement is TextBox typed && typed != tagHunt && typed != tagFind && TagsBox.IsAncestorOf(typed);
                if (tagGhost != null) tagRedraw = true;
                else if (!noting && !PageFresh("tags")) RenderTags();
            }
            if (startPage =="download" || startPage == "storage" || startPage == "tags" || startPage == "lua") ShowPage(startPage);
            startPage = null;
        }

        // ---------------------------------------------------------------- the filters and the target

        void RenderNav()
        {
            Nav.Children.Clear();
            var targets = TargetSwitch();
            if (targets != null) Nav.Children.Add(targets);
            Nav.Children.Add(Label("library", new Thickness(8, 0, 0, 4)));
            if (filter == "lua" || filter == "pending") filter = "all";
            var scope = st.Mods.Where(InScope).ToList();
            var defs = new[]
            {
                // what needs doing something, counted in what the list shows (the profile in use,
                // or the library); what a mod is (Lua and the rest) is the Filter's (the user, 2026-10-25)
                // no "to deploy": one small change makes many files to deploy, and the count said
                // little (the user, 2026-11-03); the Deploy button counts them
                ("all", scope.Count),
                ("updates", scope.Count(HasUpdate)),
                ("on", scope.Count(m => m.Enabled)),
                ("off", scope.Count(m => !m.Enabled)),
                ("conflict", scope.Count(m => m.InConflict)), ("broken", scope.Count(m => m.Error != null)),
                ("norun", scope.Count(m => NoRun(m) != null)),
            };
            foreach (var (key, n) in defs)
            {
                if ((key == "broken" || key == "updates" || key == "norun") && n == 0 && filter != key) continue;
                bool cur = filter == key, hot = (key == "broken" || key == "updates") && n > 0;
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
                btn.Click += (s, e) =>
                {
                    bool order = OrderShown;
                    bool swap = filter != key;
                    filter = key;
                    RenderNav();
                    FollowList();
                    listSwap = swap;
                    RenderList();
                    if (swap) Motion.In(ListShown, 0, 6);
                    // into or out of the conflicts: the same mod's details change (OrderShown)
                    if (OrderShown != order) RenderDetail();
                };
                Nav.Children.Add(btn);
            }

            // the deploy target, without a heading: its state says what it is (the user, 2026-10-25)
            Nav.Children.Add(new Border { Height = 24 });
            var card = new StackPanel();
            if (!st.OnOfficial) { TargetSide(card); return; }
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
            // every change since the last deploy taken back (the user, 2026-11-04): only while
            // there is one
            if (st.CanUndo)
            {
                var undo = new Button
                {
                    Content = new TextBlock { Text = Texts.Get("undo_changes"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                    Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
                    ToolTip = Texts.Get("undo_changes_tip"),
                };
                undo.Click += Undo_Click;
                card.Children.Add(undo);
            }
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

        /// <summary>The download page: the site's Mod Manager Download button (who gets it, take
        /// it or give it back), the Nexus Mods account, the look-up by number, the download going
        /// now, the downloads so far, and the mods in the library that came from Nexus Mods. A
        /// build without downloads has the look-up and the mods only, and says so.</summary>
        void RenderDownloads()
        {
            Downloads.Children.Clear();
            Downloads.Children.Add(new TextBlock { Text = Texts.Get(HasNexus ? "dl_title" : "dl_title_dev"), FontSize = 28, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
#if !HAS_NEXUS
            // what this build leaves out is said whatever the hints setting
            Downloads.Children.Add(Small(Texts.Get("dl_intro_dev"), new Thickness(0, 0, 0, 20)));
            Downloads.Children.Add(Card(FindByNumber(false)));
            Downloads.Children.Add(new Border { Height = 20 });
#else
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
#endif

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
        bool findTested;

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
            // HD2QD_FIND (pictures only): that number typed in and looked up once the page is first drawn
            var typed = Environment.GetEnvironmentVariable("HD2QD_FIND");
            if (typed != null && !findTested)
            {
                findTested = true;
                box.Text = typed;
                Dispatcher.BeginInvoke(new Action(Find), DispatcherPriority.Loaded);
            }

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
            // Mod Manager Download there comes back here only while the button is ours; a build
            // without downloads: by hand, then dropped in
            if (m.Available) words.Children.Add(Small(Texts.Get(!HasNexus ? "dl_find_manual" : ours ? "dl_find_files_n" : "dl_find_take"), new Thickness(0, 6, 0, 0)));
            card.Children.Add(words);
            return card;
        }

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
                // the public look-ups, in every build
                case "nexus_offline": return Texts.Get("nx_offline");
                case "nexus_busy": return Texts.Get("nx_busy");
                case "nexus_missing": return Texts.Get("dl_missing");
#if HAS_NEXUS
                case "no_key": return Texts.Get("up_no_key");
                case "nexus_refused": return Texts.Get("nx_refused");
                case "download_cut": return Texts.Get("dl_cut");
#endif
                case "not_zip": return Texts.Get("dl_not_zip");
                case "archive_damaged": return Texts.Get("dl_damaged");
                case "archive_password": return Texts.Get("dl_password");
                default: return kind ?? "";
            }
        }

        // the page the settings or plugins page was opened from: Esc, the gear and the plug go back to it
        string pageBack = "deploy";

        /// <summary>One of the pages: "deploy", "download", "storage", "tags", or "settings" (the
        /// gear; a page of its own since 2026-10-27, the window was taller than the main one).</summary>
        // the page on show, so a page that comes in fades in (Motion); the window opens on the deploy page
        string shownPage = "deploy";
        // what each page was last drawn for by ShowPage (state read, language, hints, direction)
        readonly Dictionary<string, string> pageDrawn = new Dictionary<string, string>();

        UIElement PageOf(string page) => page == "deploy" ? Body : page == "download" ? DownloadPage : page == "lua" ? LuaPage
            : page == "storage" ? StoragePage : page == "tags" ? TagsPage : page == "settings" ? SettingsPage : page == "plugins" ? (UIElement)PluginsArea : null;

        void ShowPage(string page)
        {
            if (shownPage != null && shownPage != page) Motion.In(PageOf(page), 0, 6, 120, 0.35);
            shownPage = page;
            if ((page == "settings" || page == "plugins") && SettingsPage.Visibility != Visibility.Visible && PluginsPage.Visibility != Visibility.Visible)
                pageBack = StoragePage.IsVisible ? "storage" : TagsPage.IsVisible ? "tags" : DownloadPage.IsVisible ? "download" : LuaPage.IsVisible ? "lua" : "deploy";
            PageDeploy.IsChecked = page == "deploy";
            PageLua.IsChecked = page == "lua";
            PageDownload.IsChecked = page == "download";
            PageStorage.IsChecked = page == "storage";
            PageTags.IsChecked = page == "tags";
            Body.Visibility = page == "deploy" ? Visibility.Visible : Visibility.Collapsed;
            DownloadPage.Visibility = page == "download" ? Visibility.Visible : Visibility.Collapsed;
            LuaPage.Visibility = page == "lua" ? Visibility.Visible : Visibility.Collapsed;
            StoragePage.Visibility = page == "storage" ? Visibility.Visible : Visibility.Collapsed;
            TagsPage.Visibility = page == "tags" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
            PluginsPage.Visibility = page == "plugins" ? Visibility.Visible : Visibility.Collapsed;
            PluginsArea.Visibility = PluginsPage.Visibility;            // the column and its guide
            // the gear and the plug lit while their page is on show, as the page buttons are
            SettingsBtn.Background = page == "settings" ? B("Yellow") : Brushes.Transparent;
            SettingsBtn.BorderBrush = page == "settings" ? B("Yellow") : B("Grid");
            GearGlyph.Foreground = page == "settings" ? B("TapeInk") : B("Ink");
            PluginsBtn.Background = page == "plugins" ? B("Yellow") : Brushes.Transparent;
            PluginsBtn.BorderBrush = page == "plugins" ? B("Yellow") : B("Grid");
            PlugGlyph.Foreground = page == "plugins" ? B("TapeInk") : B("Ink");
            // a page drawn for the state on show, in this language, is shown as it is: each was drawn
            // again on every visit, and Storage, Download, Plugins and Settings took up to a second to
            // come (the user, 2026-11-06). The Lua page and the settings carry logs that grow, and are
            // always drawn
            bool Fresh(string p) => PageFresh(p);
            if (page == "download" && !Fresh("download")) RenderDownloads();
            if (page == "storage" && !Fresh("storage")) RenderStorage();
            if (page == "lua") RenderLua();
            if (page == "tags" && !Fresh("tags")) RenderTags();
            if (page == "settings") { RenderSettings(); SettingsPage.ScrollToTop(); }
            if (page == "plugins") { if (!Fresh("plugins")) RenderPlugins(); PluginsPage.ScrollToTop(); RefreshListSoon(false); }
        }

        void Page_Click(object sender, RoutedEventArgs e) =>
            ShowPage(sender == PageDownload ? "download" : sender == PageStorage ? "storage" : sender == PageTags ? "tags" : sender == PageLua ? "lua" : "deploy");

        // ---------------------------------------------------------------- the storage page

        // how the two lists of mods are sorted (the user, 2026-10-24)
        string storageSort = "size";
        string downloadSort = "order";

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
            // each mod's size as the last state read found it: the page walked every mod folder on
            // the disk twice to draw (the user, 2026-11-06: a second to open)
            var known = st?.Mods.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().LibraryBytes) ?? new Dictionary<string, long>();
            long? Known(Mod x) => x.Id != null && known.TryGetValue(x.Id, out var b) ? b : (long?)null;
            var folders = Storage.Folders(lib, GameDir(), Known);
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
                advice.Children.Add(Heading(Texts.F("st_far", far.Sum(f => f.Mods.Count), string.Join(Texts.Sep, far.Select(f => f.Drive.TrimEnd('\\'))), IOPath.GetPathRoot(GameDir()).TrimEnd('\\'))));
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
            // a tick changes the ticks and the two buttons' counts, nothing else: it drew the whole
            // page again, the folder list read from the disk and every row made anew, and the tick
            // felt sticky (the user, 2026-11-06)
            var boxes = new List<(string Id, CheckBox Box)>();
            void Ticks()
            {
                moveBtn.Content = Texts.F("st_move_n", ticked.Count);
                moveBtn.IsEnabled = ticked.Count > 0;
                delBtn.Content = Texts.F("st_delete_n", ticked.Count);
                delBtn.IsEnabled = ticked.Count > 0;
                all.IsChecked = sel.Mods.Count > 0 && sel.Mods.All(m => ticked.Contains(m.Id));
                foreach (var (bid, b) in boxes) b.IsChecked = ticked.Contains(bid);
            }
            all.Click += (s, e) =>
            {
                if (all.IsChecked == true) foreach (var m in sel.Mods) ticked.Add(m.Id);
                else ticked.Clear();
                Ticks();
            };
            bar.Children.Add(all);
            Storages.Children.Add(bar);
            if (sel.Mods.Count == 0) { Storages.Children.Add(Small(Texts.Get("st_empty"), new Thickness(0, 0, 0, 0))); return; }
            var list = new StackPanel();
            // keyed one by one, not with ToDictionary: two mods with the same id must not bring
            // the window down while it is only drawing a list (the user, 2026-10-24)
            var sizes = new Dictionary<string, long>();
            foreach (var x in sel.Mods)
                sizes[x.Id ?? ""] = Known(x) ?? (Directory.Exists(x.Path) ? StateDoc.LibraryBytes(new DirectoryInfo(x.Path)) : 0L);
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
                box.Click += (s, e) => { if (box.IsChecked == true) ticked.Add(id); else ticked.Remove(id); Ticks(); };
                boxes.Add((id, box));
                list.Children.Add(Band(line, band++));
            }
            Storages.Children.Add(Card(list));
            KeptVersions();
            OldVersions();
        }

        readonly HashSet<string> prevPick = new HashSet<string>();

        /// <summary>The versions kept for a rollback (Versions; the user, 2026-10-30): seen, opened,
        /// rolled back to, deleted. Apart from the old versions left over, which no mod uses.</summary>
        void KeptVersions()
        {
            var kept = (st?.Mods ?? new List<ModRow>()).Where(m => m.PrevPath != null).ToList();
            if (kept.Count == 0) return;
            prevPick.RemoveWhere(id => !kept.Any(m => m.Id == id));
            Storages.Children.Add(Label("st_prev", new Thickness(0, 20, 0, 6)));
            var box = new StackPanel();
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            int picked = kept.Count(m => prevPick.Contains(m.Id));
            var drop = new Button
            {
                Content = picked > 0 ? Texts.F("st_old_drop_n", picked) : Texts.Get("st_old_drop"), Style = (Style)FindResource("Danger"), MinHeight = 34,
                IsEnabled = picked > 0,
            };
            drop.Click += (s, e) => DropKeptVersions();
            DockPanel.SetDock(drop, Dock.Right);
            bar.Children.Add(drop);
            var every = new CheckBox
            {
                Style = (Style)FindResource("Tick"), IsChecked = picked == kept.Count, VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock
                {
                    Text = Texts.Get("pick_all") + "  ·  " + Texts.F("st_prev_n", kept.Count, Gb(kept.Sum(m => m.PrevBytes))),
                    FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"),
                },
            };
            // ticks change the ticks and the button, as in the mods above
            var prevBoxes = new List<(string Id, CheckBox Box)>();
            void PrevTicks()
            {
                int n = kept.Count(m => prevPick.Contains(m.Id));
                drop.Content = n > 0 ? Texts.F("st_old_drop_n", n) : Texts.Get("st_old_drop");
                drop.IsEnabled = n > 0;
                every.IsChecked = n == kept.Count;
                foreach (var (bid, b) in prevBoxes) b.IsChecked = prevPick.Contains(bid);
            }
            every.Click += (s, e) =>
            {
                if (every.IsChecked == true) foreach (var m in kept) prevPick.Add(m.Id);
                else prevPick.Clear();
                PrevTicks();
            };
            bar.Children.Add(every);
            box.Children.Add(bar);
            if (Texts.Hints) box.Children.Add(Small(Texts.Get("st_prev_note"), new Thickness(0, 0, 0, 10)));
            int band = 0;
            foreach (var m in kept)
            {
                var id = m.Id;
                var line = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var tick = new CheckBox { Style = (Style)FindResource("Tick"), IsChecked = prevPick.Contains(id), VerticalAlignment = VerticalAlignment.Center };
                tick.Click += (s, e) =>
                {
                    if (tick.IsChecked == true) prevPick.Add(id); else prevPick.Remove(id);
                    PrevTicks();
                };
                prevBoxes.Add((id, tick));
                DockPanel.SetDock(tick, Dock.Left);
                line.Children.Add(tick);
                var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                var open = new Button { Content = Texts.Get("open"), Style = (Style)FindResource("Ghost"), MinHeight = 28, IsEnabled = m.PrevExists };
                var path = m.PrevPath;
                open.Click += (s, e) => OpenFolder(path);
                acts.Children.Add(open);
                var back = new Button { Content = Texts.Get("up_rollback_plain"), Style = (Style)FindResource("Ghost"), MinHeight = 28, Margin = new Thickness(8, 0, 0, 0) };
                back.Click += (s, e) => Rollback(m);
                acts.Children.Add(back);
                DockPanel.SetDock(acts, Dock.Right);
                line.Children.Add(acts);
                var size = new TextBlock
                {
                    Text = Gb(m.PrevBytes), FontFamily = Mono, FontSize = 12, Foreground = B("Muted"), Width = 90,
                    TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(size, Dock.Right);
                line.Children.Add(size);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(new TextBlock
                {
                    Text = m.Name + "  " + (m.PrevVersion != null ? V(m.PrevVersion) : ""), FontSize = 13, Foreground = B("Ink"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                if (m.PrevKept > 0)
                    words.Children.Add(new TextBlock
                    {
                        Text = Texts.F("st_prev_kept", DateTimeOffset.FromUnixTimeSeconds(m.PrevKept).LocalDateTime.ToString("yyyy-MM-dd HH:mm")),
                        FontSize = 11, Foreground = B("Muted"), FontFamily = Mono,
                    });
                line.Children.Add(words);
                box.Children.Add(Band(line, band++));
            }
            Storages.Children.Add(Card(box));
        }

        async void DropKeptVersions()
        {
            var go = (st?.Mods ?? new List<ModRow>()).Where(m => m.PrevPath != null && prevPick.Contains(m.Id)).ToList();
            if (go.Count == 0) return;
            long bytes = go.Sum(m => m.PrevBytes);
            if (!Dialog.AskDanger(this, Texts.F("st_prev_ask", go.Count, Gb(bytes)), Texts.Get("st_old_drop"))) return;
            var args = new List<string> { "previous", "--delete" };
            args.AddRange(go.Select(m => m.Id));
            prevPick.Clear();
            if (await Act(() => Cli.Run(args.ToArray()), false)) Foot(Texts.F("st_old_done", go.Count, Gb(bytes)), "Blue");
            Refresh();
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
            var oldBoxes = new List<(string Path, CheckBox Box)>();
            void OldTicks()
            {
                int n = left.Count(l => oldPick.Contains(l.Path));
                drop.Content = n > 0 ? Texts.F("st_old_drop_n", n) : Texts.Get("st_old_drop");
                drop.IsEnabled = n > 0;
                every.IsChecked = n == left.Count;
                foreach (var (bp, b) in oldBoxes) b.IsChecked = oldPick.Contains(bp);
            }
            every.Click += (s, e) =>
            {
                if (every.IsChecked == true) foreach (var l in left) oldPick.Add(l.Path);
                else foreach (var l in left) oldPick.Remove(l.Path);
                OldTicks();
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
                    OldTicks();
                };
                oldBoxes.Add((path, tick));
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
                var which = string.Join("\n", several.Select(x => Texts.F("im_profiles_line", x.Name, string.Join(Texts.Sep, x.ProfileNames))));
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
            var drives = string.Join(Texts.Sep, far.Select(m => IOPath.GetPathRoot(m.Path).TrimEnd('\\')).Distinct());
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
                    downloading = Texts.F("dl_unnamed", link.Mod, link.File);
                    ShowPage("download");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var (r, mod, replaced) = await NexusInstall(link, text => { if (progressText != null) progressText.Text = text; });
                    downloading = null;
                    progressText = null;
                    if (!Refused(r) && r.Ok)
                        lastDone = Texts.F("took", Texts.F(replaced ? "nx_replaced" : "nx_added", mod.Name), Seconds(clock.Elapsed.TotalSeconds));
                    Refresh();
                    RenderDownloads();
                }
            }
            finally { linking = false; }
        }

        // one download at a time, apart from the queue of changes
        readonly System.Threading.SemaphoreSlim fetchGate = new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>A Nexus file downloaded and unpacked in the background with the window free,
        /// then put in the library in the queue's turn. A big file stood at 100% for over a
        /// minute while it unpacked, the list held all the while, with no word whether it had
        /// failed (the user, 2026-10-30): each stage is said now, in the bottom line and to show.</summary>
        async Task<(CliResult R, Mod Mod, bool Replaced)> NexusInstall(NxmLink link, Action<string> show)
        {
            await fetchGate.WaitAsync();
            var unpacking = new System.Diagnostics.Stopwatch();
            var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            void Say(string text)
            {
                fetchLine = text;
                Foot(text, "Blue");
                show(text);
            }
            ticker.Tick += (s, e) => Say(Texts.F("nx_unpacking", (int)unpacking.Elapsed.TotalSeconds));
            try
            {
                long shown = 0;
                Nexus.Fetched f = null;
                var r = await Task.Run(() => Cli.Call(() =>
                {
                    f = Nexus.Fetch(link, Nexus.Credential(), (done, total) =>
                    {
                        // at most every 128 KB
                        if (done - shown < 128 * 1024 && done != total) return;
                        shown = done;
                        var text = total > 0 ? Texts.F("nx_progress", Texts.Size(done), Texts.Size(total), done * 100 / total)
                                             : Texts.F("nx_progress_n", Texts.Size(done));
                        Dispatcher.BeginInvoke(new Action(() => Say(text)));
                    }, () => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        unpacking.Start();
                        Say(Texts.F("nx_unpacking", 0));
                        ticker.Start();
                    })));
                    return "";
                }));
                ticker.Stop();
                if (!r.Ok) return (r, null, false);
                Say(Texts.Get("nx_adding"));
                Mod mod = null;
                bool replaced = false;
                r = await Queued(() => Cli.Call(() => { (mod, replaced) = Nexus.Put(f); return ""; }));
                return (r, mod, replaced);
            }
            finally
            {
                ticker.Stop();
                fetchLine = null;
                fetchGate.Release();
            }
        }
#endif

        // ---------------------------------------------------------------- the list

        // rows and group columns built anew, for the window's own tests (deletemod)
        int rowsBuilt;

        void RenderList()
        {
            int builtBefore = rowsBuilt;
            try { RenderListNow(); }
            finally { drawLog?.Add(drawClock.ElapsedMilliseconds + "\tlist, built anew: " + (rowsBuilt - builtBefore)); }
        }

        // the list swapped whole (another filter, the other view): it fades in as one, its rows do not
        bool listSwap;
        // the mods the load order showed last, so one new to it comes in (an add, a filter's change)
        HashSet<string> orderShown;

        /// <summary>What shows the list now: the group columns or the load order.</summary>
        UIElement ListShown => groupView && filter != "conflict" ? (UIElement)GroupScroll : ListScroll;

        void RenderListNow()
        {
            if (dragRow != null) EndDrag(false);         // the rows are about to be built again
            bool profiled = st.ProfilesOn;
            if (!profiled) viewLibrary = false;
            ProfileBtn.Visibility = profiled ? Visibility.Visible : Visibility.Collapsed;
            // at the left edge, or after the profile's name
            ViewSwitch.Margin = PotentialBox.Margin = new Thickness(profiled ? 16 : 0, 0, 0, 0);
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
            // the conflicts are a view of their own: who competes with whom (ConflictView)
            if (filter == "conflict") { RenderConflicts(); return; }
            ListHint.Text = Texts.Get(groupView ? "g_hint" : Texts.TopWins ? "list_hint_top" : "list_hint");
            if (groupView) { RenderGroups(); return; }
            // a row is built again only when what it shows changed; the others stay put
            var want = new List<UIElement>();
            var seen = new HashSet<string>();
            foreach (var m in Seen())
            {
                var sig = Sig(m);
                if (!rows.TryGetValue(m.Id, out var had) || had.Sig != sig)
                {
                    if (drawLog != null && had.Sig != null)
                    {
                        var a = had.Sig.Split(''); var b = sig.Split('');
                        var diff = Enumerable.Range(0, Math.Min(a.Length, b.Length)).Where(k => a[k] != b[k]).Select(k => k + ":" + a[k] + "->" + b[k]);
                        drawLog.Add("   rebuilt " + m.Name + " " + string.Join(" ", diff));
                    }
                    rows[m.Id] = had = (sig, Row(m)); rowsBuilt++;
                }
                had.Row.AllowDrop = CanDragRows;
                want.Add(had.Row);
                seen.Add(m.Id);
            }
            // the rows of mods another filter hides are kept for when it shows them again (the
            // user, 2026-11-06: each filter made them anew); only those of mods gone are dropped
            var present = new HashSet<string>(st.Mods.Select(x => x.Id));
            foreach (var gone in rows.Keys.Where(k => !present.Contains(k)).ToList()) rows.Remove(gone);
            // a row new to the load order comes into its place
            if (orderShown != null && !listSwap)
                foreach (var m in Seen().Where(x => !orderShown.Contains(x.Id)))
                    if (rows.TryGetValue(m.Id, out var nr)) Motion.In(nr.Row, 0, 6);
            orderShown = seen;
            listSwap = false;
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
        /// <summary>Whether the load order's rows can be dragged now: in the full list only, not
        /// under a filter nor in the library's view.</summary>
        bool CanDragRows => filter == "all" && !viewLibrary;

        string Sig(ModRow m)
        {
            var sb = new System.Text.StringBuilder();
            void Add(object x) => sb.Append(x).Append('\u001f');
            // not the filter: a row is the same in every one (CanDragRows is asked when it is used),
            // and with it every row was made anew at each filter, 0.7 s for 90 (the user, 2026-11-06)
            Add(m.Id == selectedId); Add(picked.Contains(m.Id)); Add(open.Contains(m.Id));
            Add(m.InProfile); Add(viewLibrary); Add(st.Profile);
            Add(st.Target); Add(m.For); Add(StatusText(m).Item1);
            Add(m.Index); Add(m.Name); Add(m.Path); Add(m.Enabled); Add(m.Icon); Add(m.Status); Add(m.Error); Add(m.Lua); Add(string.Join(",", m.PluginTags.Select(t => t.Text + t.Tip))); Add(m.Loader); Add(string.Join("|", m.Notes.Select(n => n.Text + n.Colour))); Add(string.Join(",", m.Tags)); Add(HasUpdate(m)); Add(m.ShownVersion); Add(m.VersionFrom);
            Add(Checked(m)?.Status); Add(checkingOne.Contains(m.Id)); Add(checking); Add(updated.Contains(m.Id));
            Add(m.Files.Count); Add(m.DeployBytes); Add(m.LibraryBytes); Add(m.Changes); Add(m.Conflicts); Add(m.Shared); Add(m.Chosen); Add(m.Choices);
            Add(NoRun(m)?.Text);
            foreach (var p in PacksOf(m)) { Add(p.Id); Add(p.Name); Add(p.Enabled); Add(p.Id == selectedId); Add(NoRun(p)?.Text); }
            if (open.Contains(m.Id))
                foreach (var o in m.Options)
                {
                    Add(o.Name); Add(o.Enabled); Add(o.Icon); Add(o.Files); Add(o.Bytes); Add(o.Description); Add(string.Join("|", o.Clashes));
                    foreach (var s in o.Subs) { Add(s.Name); Add(s.Enabled); Add(s.Icon); Add(s.Files); Add(s.Bytes); Add(s.Description); Add(string.Join("|", s.Clashes)); }
                }
            return sb.ToString();
        }

        Border Row(ModRow m)
        {
            bool cur = m.Id == selectedId, isOpen = open.Contains(m.Id), alsoTaken = picked.Contains(m.Id);
            var item = new Border
            {
                BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Tag = m, AllowDrop = CanDragRows,
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
            chev.Click += async (s, e) =>
            {
                // closing: what it shows under its head fades out first (the user, 2026-11-06)
                if (open.Contains(m.Id) && rows.TryGetValue(m.Id, out var was) && was.Row.Child is Border we && we.Child is StackPanel wc)
                    await Task.WhenAll(wc.Children.Cast<UIElement>().Skip(1).Select(x => Motion.Out(x, 100)));
                if (!open.Remove(m.Id)) open.Add(m.Id);
                Current(m.Id);
                RenderList();
                RenderDetail();
                // opened: it comes in under the head
                if (open.Contains(m.Id) && rows.TryGetValue(m.Id, out var now) && now.Row.Child is Border ne && ne.Child is StackPanel nc)
                    foreach (var x in nc.Children.Cast<UIElement>().Skip(1)) Motion.In(x, 0, -6);
            };
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
            if (ForElsewhere(m)) { item.Opacity = 0.55; sw.IsEnabled = false; sw.ToolTip = Texts.F("for_only_tip", TargetName(m.For)); }
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
            PictureOpensPreview(pic, m);
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
            if (ShowFor(m)) meta.Children.Add(Fact(ForTag(m.For), ForElsewhere(m) ? "Muted" : "Blue"));
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
            // what it holds: the added plugins' tags (HellForge's C, DLL, STATS), then LUA, side by side
            foreach (var (text, tip) in m.PluginTags) meta.Children.Add(ContentTag(text, tip));
            if (m.Lua) meta.Children.Add(ContentTag("LUA", Texts.Get("lua_tip")));
            if (m.Conflicts > 0) meta.Children.Add(Fact(Texts.F("row_clash", m.Conflicts), "Yellow"));
            foreach (var (text, tip, colour) in NotesOf(m)) meta.Children.Add(Fact(text, colour, tip));
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
            pick.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2 && !OnButton(e.OriginalSource as DependencyObject, pick)) { e.Handled = true; dragFrom = null; RenameMod(m); }
            };
            line.Children.Add(pick);

            if (isOpen) col.Children.Add(Options(m));
            foreach (var p in PacksOf(m)) col.Children.Add(PackLine(p));

            // dragging changes the load order (in the full list only, not the library's): asked at
            // the press, as the row is kept from one filter to the next
            item.PreviewMouseLeftButtonDown += (s, e) => { if (CanDragRows) { dragStart = e.GetPosition(ListArea); dragFrom = m; } };
            item.PreviewMouseMove += (s, e) =>
            {
                if (dragFrom != m || dragRow != null || e.LeftButton != MouseButtonState.Pressed || !CanDragRows) return;
                if (Math.Abs(e.GetPosition(ListArea).Y - dragStart.Y) < 6) return;
                BeginDrag(m, item, e.GetPosition(ListArea), e.GetPosition(item).Y);
            };
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
        /// 2026-10-26): its tick turns it on and off, its name shows its details, its menu is a row's.
        /// In the groups too, under the mod's own item (the user, 2026-11-01: "分组视图也应该这么显示"),
        /// its tick where the mod's name starts (the item's edge 3, margin 11, switch 44 + 10, picture
        /// 40 + 10), without the number: the groups show no load order.</summary>
        UIElement PackLine(ModRow p, bool inGroups = false)
        {
            bool cur = p.Id == selectedId;
            var line = new DockPanel { Margin = inGroups ? new Thickness(118, 0, 10, 8) : new Thickness(4, 0, 16, 8), Background = Brushes.Transparent };
            // its place in the load order, under the rows' numbers: they do not seem to skip one
            if (!inGroups)
            {
                var num = new TextBlock
                {
                    Text = st.ProfilesOn && !p.InProfile ? "" : Num(p).ToString(), FontFamily = Mono, FontSize = 12, Foreground = B("Muted"),
                    Width = 28, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 92, 0),
                };
                DockPanel.SetDock(num, Dock.Left);
                line.Children.Add(num);
            }
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

        /// <summary>The deploy target in use, for the rows' words (null: the official game), and
        /// each mod's state there as its plugin says (null: it says nothing).</summary>
        static string rowTarget;
        static Dictionary<string, string> rowStates;

        /// <summary>A row's state. On a plugin's target, the target's own in the official game's words
        /// (the user, 2026-11-12: "改成和原版一致"): 硬链接 / 复制 as its plugin placed it, 已转换 when
        /// the conversion wrote its files, 待部署 when a deploy would place it again, 未部署 when off.</summary>
        static (string, string) StatusText(ModRow m)
        {
            if (rowTarget != null)
            {
                if (!m.Enabled) return (Texts.Get("st_off"), "Muted");
                if (rowStates == null || !rowStates.TryGetValue(m.Id, out var s)) return (rowTarget, "Blue");
                switch (s)
                {
                    case "link": return (Texts.Get("st_link"), "Blue");
                    case "copy": return (Texts.Get("st_copy"), "Muted");
                    case "converted": return (Texts.Get("st_converted"), "Blue");
                    default: return (Texts.Get("st_pending"), "Yellow");
                }
            }
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
            // each group folds under its heading (issue 20, 2026-11-10: a mod with many groups,
            // BAR - Better Aim Reticles, was one long page); with more than FoldAbove groups they
            // start folded, and "expand all" / "collapse all" is over them
            var parts = m.Options.Select((o, k) => (o, k)).Where(x => x.o.Subs.Count == 0).ToList();
            int groupCount = m.Options.Count(o => o.Subs.Count > 0) + (parts.Count > 0 ? 1 : 0);
            bool foldFirst = groupCount > FoldAbove;
            var folds = new List<(string Key, Action<bool> Set)>();
            TextBlock all = null;
            void SayAll()
            {
                if (all != null) all.Text = Texts.Get(folds.Any(f => IsFolded(m.Id, f.Key, foldFirst)) ? "opt_unfold_all" : "opt_fold_all");
            }
            if (foldFirst)
            {
                all = new TextBlock { FontSize = 12, Foreground = B("Blue") };
                var allBtn = new Button { Content = all, Template = Plain(), Cursor = Cursors.Hand, FocusVisualStyle = null, HorizontalAlignment = HorizontalAlignment.Right };
                System.Windows.Automation.AutomationProperties.SetAutomationId(allBtn, "fold:all");
                allBtn.Click += (s, e) =>
                {
                    bool fold = !folds.Any(f => IsFolded(m.Id, f.Key, foldFirst));
                    foreach (var f in folds) f.Set(fold);
                    SayAll();
                };
                box.Children.Add(allBtn);
            }

            // a heading that folds what follows it: the chevron and the name are what is clicked
            // (the switch keeps its own click); folded, it says what is chosen
            void Foldable(string key, DockPanel head, FrameworkElement title, TextBlock chosen, params UIElement[] body)
            {
                var chev = FoldChevron();
                // a button: the keyboard and UI Automation reach it as a mouse does
                var toggle = new Button { Content = chev, Template = Plain(), Cursor = Cursors.Hand, FocusVisualStyle = null, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
                System.Windows.Automation.AutomationProperties.SetAutomationId(toggle, "fold:" + key);
                System.Windows.Automation.AutomationProperties.SetName(toggle, key == ExtrasFold ? Texts.Get("extras") : key);
                DockPanel.SetDock(toggle, Dock.Left);
                head.Children.Insert(0, toggle);
                void Set(bool fold)
                {
                    foreach (var b in body) b.Visibility = fold ? Visibility.Collapsed : Visibility.Visible;
                    ((RotateTransform)chev.RenderTransform).Angle = fold ? 0 : 90;
                    chosen.Visibility = fold && chosen.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                    SetFolded(m.Id, key, fold, foldFirst);
                }
                folds.Add((key, Set));
                void Flip() { Set(!IsFolded(m.Id, key, foldFirst)); SayAll(); }
                toggle.Click += (s, e) => Flip();
                title.Cursor = Cursors.Hand;
                title.MouseLeftButtonUp += (s, e) => { e.Handled = true; Flip(); };
                bool now = IsFolded(m.Id, key, foldFirst);
                foreach (var b in body) b.Visibility = now ? Visibility.Collapsed : Visibility.Visible;
                ((RotateTransform)chev.RenderTransform).Angle = now ? 0 : 90;
                chosen.Visibility = now && chosen.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            for (int i = 0; i < m.Options.Count; i++)
            {
                var o = m.Options[i];
                string oref = (i + 1).ToString();
                if (o.Subs.Count == 0) continue;
                var head = new DockPanel { Margin = new Thickness(0, 8, 0, 8), Background = Brushes.Transparent };
                var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = o.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                int oi = i;
                sw.Click += (s, e) =>
                {
                    bool on = sw.IsChecked == true;
                    Change(m.Id, c => c.Options[oi].Enabled = on, "set", m.Id, oref, on ? "on" : "off");
                };
                DockPanel.SetDock(sw, Dock.Left);
                head.Children.Add(sw);
                var t = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
                t.Children.Add(new TextBlock { Text = o.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
                t.Children.Add(new TextBlock { Text = Texts.Get("rule_one"), FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Bottom });
                var pick = o.Enabled ? o.Subs.FirstOrDefault(x => x.Enabled) : null;
                var chosen = new TextBlock
                {
                    Text = pick != null ? Texts.F("opt_chosen", pick.Name) : "", FontSize = 12, Foreground = B("Blue"),
                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 0),
                };
                t.Children.Add(chosen);
                head.Children.Add(t);
                box.Children.Add(head);
                var body = new List<UIElement>();
                if (!string.IsNullOrWhiteSpace(o.Description))
                {
                    var d = new TextBlock { Text = o.Description, FontSize = 12, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 8) };
                    box.Children.Add(d);
                    body.Add(d);
                }
                var cards = new WrapPanel { IsEnabled = o.Enabled, Opacity = o.Enabled ? 1 : 0.45 };
                for (int j = 0; j < o.Subs.Count; j++)
                {
                    var sub = o.Subs[j];
                    int sj = j;
                    string sref = (i + 1) + "." + (j + 1);
                    cards.Children.Add(Card(sub.Name, sub.Icon, sub.Files, sub.Bytes, sub.Enabled, sub.Description, sub.Clashes, () =>
                    {
                        // one of a kind: the sub-options replace the same resources (the user, 2026-10-27)
                        bool was = sub.Enabled;
                        if (!was)
                            Change(m.Id, c => { for (int k = 0; k < c.Options[oi].Subs.Count; k++) c.Options[oi].Subs[k].Enabled = k == sj; },
                                   "set", m.Id, sref, "on", "--only");
                    }));
                }
                box.Children.Add(cards);
                body.Add(cards);
                Foldable(o.Name, head, t, chosen, body.ToArray());
            }
            if (parts.Count > 0)
            {
                var head = new DockPanel { Margin = new Thickness(0, 8, 0, 8), Background = Brushes.Transparent };
                var t = new WrapPanel { Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
                t.Children.Add(new TextBlock { Text = Texts.Get("extras"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
                t.Children.Add(new TextBlock { Text = Texts.F("rule_any", parts.Count(p => p.o.Enabled), parts.Count), FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Bottom });
                head.Children.Add(t);
                box.Children.Add(head);
                var cards = new WrapPanel();
                foreach (var (o, i) in parts)
                {
                    string oref = (i + 1).ToString();
                    cards.Children.Add(Card(o.Name, o.Icon, o.Files, o.Bytes, o.Enabled, o.Description, o.Clashes, () =>
                    {
                        bool was = o.Enabled;
                        Change(m.Id, c => c.Options[i].Enabled = !was, "set", m.Id, oref, was ? "off" : "on");
                    }));
                }
                box.Children.Add(cards);
                // the count chosen is in the heading already: nothing more to say folded
                Foldable(ExtrasFold, head, t, new TextBlock(), cards);
            }
            SayAll();
            var chosenFiles = m.Files.Count;
            box.Children.Add(new TextBlock
            {
                Text = Texts.F("opt_foot", m.Chosen, Texts.N(chosenFiles), Texts.Size(m.DeployBytes)),
                FontFamily = Mono, FontSize = 12, Foreground = B("Blue"), Margin = new Thickness(0, 14, 0, 0),
            });
            return box;
        }

        /// <summary>More groups than this in a mod's options: they start folded (issue 20).</summary>
        const int FoldAbove = 3;

        /// <summary>The fold key of the group of parts (options without sub-options); a manifest's
        /// own option names cannot start with it.</summary>
        const string ExtrasFold = "\u0001parts";

        /// <summary>Groups folded or unfolded by the player, per mod and group name: kept in gui.json
        /// ("option_folds"), as the player left them; a group never touched follows the default.</summary>
        Dictionary<string, object> optionFolds;

        bool IsFolded(string mod, string key, bool fallback)
        {
            if (optionFolds == null)
                try { optionFolds = Store.Load("gui.json").TryGetValue("option_folds", out var o) && o is Dictionary<string, object> d ? d : new Dictionary<string, object>(); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { optionFolds = new Dictionary<string, object>(); }
            return optionFolds.TryGetValue(mod, out var v) && v is Dictionary<string, object> mf && mf.TryGetValue(key, out var f) && f is bool b ? b : fallback;
        }

        void SetFolded(string mod, string key, bool fold, bool fallback)
        {
            if (IsFolded(mod, key, fallback) == fold) return;
            if (!(optionFolds.TryGetValue(mod, out var v) && v is Dictionary<string, object> mf)) optionFolds[mod] = mf = new Dictionary<string, object>();
            mf[key] = fold;
            try
            {
                var d = Store.Load("gui.json");
                d["option_folds"] = optionFolds;
                Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { }
        }

        /// <summary>The fold mark before a group's heading: pointing right folded, down open.</summary>
        static Path FoldChevron() => new Path
        {
            Data = Geometry.Parse("M0,0 L5,5 L0,10"), Stroke = B("Muted"), StrokeThickness = 1.6, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 10, Height = 10, Stretch = Stretch.None, RenderTransformOrigin = new Point(0.3, 0.5), RenderTransform = new RotateTransform(90),
        };

        /// <summary>clashes: the enabled mods it conflicts with, said on the card before it is chosen
        /// (the user, 2026-11-03).</summary>
        Button Card(string name, string icon, int files, long bytes, bool on, string description, List<string> clashes, Action act)
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
            if (clashes != null && clashes.Count > 0)
                col.Children.Add(new TextBlock { Text = Texts.F("opt_clash", string.Join(Texts.Get("sep_list"), clashes)), FontSize = 12, Foreground = B("Yellow"),
                                                 TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
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

        // what the details showed last (a mod's id, or "g:" and a group's), so others fade in
        string detailFor;

        void RenderDetail()
        {
            drawLog?.Add(drawClock.ElapsedMilliseconds + "\tdetails");
            FillPreview();
            var showing = selectedId ?? (groupView && groupPath.Count > 0 ? "g:" + groupPath[groupPath.Count - 1] : "");
            if (detailFor != null && showing != detailFor && showing.Length > 0) Motion.In(Detail, 0, 6);
            detailFor = showing;
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
            if (longDesc)
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
            // the whole of it in the details window (the user, 2026-11-06), with the changes
            if (longDesc) man.Children.Add(MoreButton(m));
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
            else Fact("d_order", Texts.F("d_order_v", Num(m), st.ProfilesOn ? st.Mods.Count(x => x.InProfile && !x.Loader && !ForElsewhere(x)) : st.Mods.Count(x => !x.Loader && !ForElsewhere(x))), true);
            if (!m.Loader) Fact("d_opts", m.Options.Count > 0 ? m.Chosen + " / " + m.Choices : "—");
            if (!m.Loader && st.Groups.Count > 0)
                Fact("d_group", m.Group != null ? string.Join(" › ", PathTo(m.Group).Select(x => x.Name)) : Texts.Get("g_top"));
            // what it replaces, by game item (the user, 2026-11-03)
            if (m.Replaces.Count > 0) Fact("d_replaces", ItemsLabel(m.Replaces));
            Fact("d_folder", m.Path, true, true);
            {
                // remove from the library, and open the folder. A row that does not fit wraps onto
                // the next line rather than cutting off the first button (the user, 2026-10-26); each
                // button keeps 8 px on its left, which the panel's -8 takes back at the line's start
                int r = facts.RowDefinitions.Count;
                facts.RowDefinitions.Add(new RowDefinition());
                var acts = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(-8, 0, 0, 6) };
                var removeBtn = new Button { Content = Texts.Get("remove_btn"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Margin = new Thickness(8, 4, 0, 0) };
                removeBtn.Click += (s, e) => RemoveFromLibrary(m);
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
            // where the list cannot open a mod's options in place (the groups, the conflicts) they are
            // chosen in the preview (Space), not here: a long list of them kept the details scrolling
            // (the user, 2026-10-27)
            if (!OrderShown && m.Options.Count > 0)
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
            var forPanel = ForPanel(m);
            if (forPanel != null) Detail.Children.Add(forPanel);
            Detail.Children.Add(UpdatePanel(m));

            // what a deploy does to it
            if (!st.OnOfficial)
            {
                var tbox = new StackPanel();
                var tline = new StackPanel { Orientation = Orientation.Horizontal };
                tline.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(m.Enabled ? "Blue" : "Muted"), Margin = new Thickness(0, 0, 8, 0) });
                tline.Children.Add(new TextBlock { Text = Texts.F(m.Enabled ? "target_detail_on" : "target_detail_off", st.TargetTitle), FontWeight = FontWeights.SemiBold });
                tbox.Children.Add(tline);
                tbox.Children.Add(Small(Texts.F(m.Enabled ? "target_detail_on_n" : "target_detail_off_n", st.TargetTitle), new Thickness(0, 8, 0, 0)));
                Detail.Children.Add(Panel(tbox, new Thickness(0, 20, 0, 0)));
            }
            else if (m.Enabled && m.Changes > 0) Detail.Children.Add(Changes(m));
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
                    var inGame = st.Mods.FirstOrDefault(x => x.Name == c.Deployed)?.Id;
                    if (ids.Count > 1)
                    {
                        box.Children.Add(Small(Texts.Get("pick_wins_label"), new Thickness(0, 8, 0, 4)));
                        box.Children.Add(WinnerRows(ids, winner, "pick_now", inGame));
                    }
                    else box.Children.Add(Small(Texts.F("clash_win", c.Winner), new Thickness(0, 6, 0, 0)));
                }
                // which resources (the user, 2026-10-25), one line for each kind of them
                var list = ResourceLines(c, 8, 13, "Ink", true);
                list.Margin = new Thickness(0, 10, 0, 0);
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
            // the bar and its key say enough: the written and reused totals and the list of files
            // went (the user, 2026-11-03)
            return box;
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
            PictureSet(frame, new Image { Source = img, Stretch = Stretch.UniformToFill });
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
                if (OrderShown)
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
            if (OrderShown)
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
                RemoveFromLibrary(m);
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
        void Updates_Click(object sender, RoutedEventArgs e) => CheckAll(false);

        /// <summary>The last check's results at once (kept from the window before), then, when the
        /// check at start is on and due (once a day), every mod checked in the background (the
        /// user, 2026-11-04). It waits a few seconds, so the first read of the library and the
        /// pictures go first; the window is usable throughout.</summary>
        async void ModUpdatesAtStart()
        {
            try
            {
                var lib = await Queued(() => Library.Load());
                foreach (var u in Updates.Kept(lib))
                {
                    updates[u.ModId] = u;
                    checkedAs[u.ModId] = Updates.As(lib.Mods.First(m => m.Id == u.ModId));
                }
            }
            catch (Exception x) when (x is ModError || x is IOException || x is FormatException || x is UnauthorizedAccessException) { }
            if (st != null) { RenderNav(); RenderList(); RenderDetail(); }
            if (!Updates.DueAtStart()) return;
            await Task.Delay(5000);
            CheckAll(true);
        }

        /// <summary>Every mod checked. atStart: the check the window makes by itself, which says
        /// what it found in the footer only, and only when something has an update: no summary
        /// window, no change of filter or of the mod shown.</summary>
        async void CheckAll(bool atStart)
        {
            if (checking || busy) return;
            checking = true;
            UpdatesBtn.Content = Texts.Get("up_checking_btn");
            UpdatesBtn.IsEnabled = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            List<UpdateInfo> found = null;
            var was = new Dictionary<string, string>();
            string failed = null;
            int known = 0;
            try
            {
                // the origins HD2 Arsenal recorded are learnt first, in turn with the other commands
                var lib = await Queued(() =>
                {
                    var l = Library.Load();
                    if (Origins.FromArsenal(l) > 0) l.Save();
                    return l;
                });
                // then what Nexus Mods knows the archives as (Fingerprints)
                (lib, known) = await Fingerprinted(lib, null);
                foreach (var m in lib.Mods) was[m.Id] = CheckedAs(m.Path, m.Nexus?.Version ?? m.GitHub?.Tag, Origins.Of(m).Page);
                found = await Task.Run(() => Updates.Check(lib, (i, n) =>
                    Dispatcher.BeginInvoke(new Action(() => Foot(Texts.F("up_progress", i, n), "Blue"))), learn: false));
            }
            catch (Exception x) when (x is ModError || x is IOException || x is FormatException || x is UnauthorizedAccessException) { failed = x.Message; }
            finally
            {
                checking = false;
                UpdatesBtn.IsEnabled = true;
                UpdatesBtn.Content = Texts.Get("up_check");
            }
            if (failed != null)
            {
                if (atStart) return;            // the next start, or the button, tries again
                Foot(Texts.F("failed", failed), "Yellow");
                Dialog.Tell(this, Texts.F("failed", failed));
                return;
            }

            updates.Clear();
            checkedAs.Clear();
            updated.Clear();
            foreach (var u in found)
            {
                updates[u.ModId] = u;
                checkedAs[u.ModId] = was[u.ModId];
            }
            int Count(string status, Func<UpdateInfo, bool> also = null) => found.Count(u => u.Status == status && (also == null || also(u)));
            int upd = Count("update"), err = Count("error"), none = Count("no_source"), other = Count("no_check");
            if (atStart)
            {
                // nothing to say: the line goes back to what it says of the deploy
                if (upd > 0) Foot(Texts.F("up_foot_start", upd), "Yellow");
                else if (st != null) FootState();
                if (st != null) { RenderNav(); RenderList(); RenderDetail(); }
                return;
            }
            Foot(Texts.F("up_foot", upd, Seconds(clock.Elapsed.TotalSeconds)), upd > 0 ? "Yellow" : "Blue");
            if (upd > 0)
            {
                filter = "updates";
                Current(found.First(u => u.Status == "update").ModId);
            }
            if (st != null) { RenderNav(); RenderList(); RenderDetail(); }

            var text = Texts.F("up_summary", found.Count, upd, Count("current"), Count("unknown"), none);
            if (known > 0) text += "\n\n" + Texts.F("up_summary_known", known);
            if (Count("skipped") > 0) text += "\n\n" + Texts.F("up_summary_skipped", Count("skipped"));
            if (other > 0) text += "\n\n" + Texts.F("up_summary_other", other);
            if (err > 0) text += "\n\n" + Texts.F("up_summary_err", err);
            if (none > 0) text += "\n\n" + Texts.Get("up_summary_source");
            Dialog.Tell(this, text);
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
            if (LuaPage.IsVisible) RenderLua();
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
                var (lib, _) = await Fingerprinted(null, mod.Id);
                mod = lib.Mods.FirstOrDefault(x => x.Id == m.Id) ?? mod;
                was = CheckedAs(mod.Path, mod.Nexus?.Version ?? mod.GitHub?.Tag, Origins.Of(mod).Page);
                u = await Task.Run(() => Updates.One(mod));
            }
            catch (Exception x) when (x is ModError || x is IOException || x is FormatException || x is UnauthorizedAccessException) { failed = x.Message; }
            finally { checkingOne.Remove(m.Id); }
            if (failed != null)
            {
                Foot(Texts.F("failed", failed), "Yellow");
                if (st != null) { RenderList(); RenderDetail(); if (LuaPage.IsVisible) RenderLua(); }
                return;
            }
            updates[u.ModId] = u;
            checkedAs[u.ModId] = was;
            updated.Remove(u.ModId);
            Foot(Texts.F("up_one_foot", m.Name, Texts.Get("up_row_" + u.Status)), u.Status == "update" ? "Yellow" : "Blue");
            if (st != null) { RenderNav(); RenderList(); RenderDetail(); }
        }

        /// <summary>The library after its archives' fingerprints are asked about (Fingerprints; the
        /// user, 2026-11-04): the MD5s taken and Nexus Mods asked outside the queue, the answers put
        /// in inside it. lib: the library as read (null: read now); onlyId: one mod's own check. Also
        /// how many mods learnt their source; the window reads the state again when any did.</summary>
        async Task<(Library Lib, int Known)> Fingerprinted(Library lib, string onlyId)
        {
            if (lib == null) lib = await Queued(() => Library.Load());
            var read = lib;
            var wanted = await Task.Run(() => Fingerprints.Wanted(read, onlyId));
            if (wanted.Count == 0) return (lib, 0);
            Foot(Texts.Get("up_fingerprints"), "Blue");
            var found = await Task.Run(() =>
            {
                try { return Fingerprints.Look(wanted.Values); }
                catch (ModError) { return new Dictionary<string, NexusRef>(); }    // the check says why for itself
            });
            int known = 0;
            lib = await Queued(() =>
            {
                var l = Library.Load();
                int hashed = l.Mods.Count(m => m.ArchiveMd5 != null);
                known = Fingerprints.Apply(l, wanted, found);
                // saved only when something changed: a save makes the window read the library
                // again, and that read's bottom line took the place of what the check said
                if (known > 0 || l.Mods.Count(m => m.ArchiveMd5 != null) != hashed) l.Save();
                return l;
            });
            if (known > 0) Refresh();
            return (lib, known);
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
            // the version kept for a rollback (Versions; the user, 2026-10-30)
            if (m.PrevPath != null)
                Line("d_previous", (m.PrevVersion != null ? V(m.PrevVersion) : Texts.Get("up_none")) +
                                   (m.PrevKept > 0 ? "  ·  " + DateTimeOffset.FromUnixTimeSeconds(m.PrevKept).LocalDateTime.ToString("yyyy-MM-dd") : ""), m.PrevPath);

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
                case "skipped": text = Texts.F("up_skipped", u.Latest ?? "?"); break;
                case "no_source": text = Texts.Get("up_no_source"); break;
                case "no_check": text = Texts.F("up_no_check", u.Channel); break;
                default: text = Texts.F("up_error", CheckWhy(u.Why)); colour = "Yellow"; break;
            }
            // a GitHub release without an archive to download: its page only
            if (apply && m.Channel == "GitHub" && string.IsNullOrEmpty(u.Download)) apply = false;
            if (text.Length > 0)
            {
                var said = Selectable.Text(text, 12, colour);
                said.Margin = new Thickness(0, 8, 0, 0);
                box.Children.Add(said);
            }
            if (apply && u.Changes.Count > 0) box.Children.Add(ChangesList(u.Changes, m));

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
            // the newest version skipped, so the check stops offering it, or offered again
            if (u?.Status == "update" || u?.Status == "skipped")
            {
                bool skip = u.Status == "update";
                var sk = new Button { Content = Texts.Get(skip ? "up_skip" : "up_unskip"), Style = (Style)FindResource("Ghost"), MinHeight = 34 };
                sk.Click += (s, e) => SkipVersion(m, u, skip);
                buttons.Add(sk);
            }
            if (m.PrevPath != null)
            {
                // the version is on the line above, so the button does not say it again
                var back = new Button { Content = Texts.Get("up_rollback"), Style = (Style)FindResource("Ghost"), MinHeight = 34 };
                back.Click += (s, e) => Rollback(m);
                buttons.Add(back);
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

        /// <summary>What each version since the installed one changed, as its author wrote it on
        /// Nexus Mods (the user, 2026-11-04), newest first: the newest few, then how many more the
        /// page has.</summary>
        /// <summary>What each version since the installed one changed, newest first. In the details
        /// pane at most five lines of it (the user, 2026-11-06: many versions saved up made it very
        /// long), the rest in the details window that more opens; there all of it.</summary>
        UIElement ChangesList(List<Change> changes, ModRow more = null)
        {
            const int Lines = 5;
            int left = more == null ? int.MaxValue : Lines;
            bool cut = false;
            var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            if (more != null) list.Children.Add(new TextBlock { Text = Texts.Get("up_changes"), FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 0, 0, 4) });
            double size = more == null ? 14 : 12;
            bool first = true;
            foreach (var c in changes)
            {
                if (left <= 0) { cut = true; break; }
                var ver = Selectable.Text(V(c.Version ?? ""), size);
                ver.FontWeight = FontWeights.SemiBold;
                ver.Margin = new Thickness(0, first ? 0 : more != null ? 6 : 14, 0, 2);
                first = false;
                list.Children.Add(ver);
                foreach (var line in c.Lines)
                {
                    if (left <= 0) { cut = true; break; }
                    left--;
                    var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                    var dot = new TextBlock { Text = "•", FontSize = size, Foreground = B("Muted"), Width = size + 2 };
                    DockPanel.SetDock(dot, Dock.Left);
                    row.Children.Add(dot);
                    row.Children.Add(Selectable.Text(line, size, "Ink", null, more == null ? 21 : 0));
                    list.Children.Add(row);
                }
            }
            if (cut)
            {
                list.Children.Add(new TextBlock { Text = "…", FontSize = 12, Foreground = B("Muted") });
                list.Children.Add(MoreButton(more));
            }
            return list;
        }

        /// <summary>The button that opens the details window: under a description or a list of
        /// changes cut short.</summary>
        Button MoreButton(ModRow m)
        {
            var b = new Button { Content = Texts.Get("more"), Template = Plain(), Foreground = B("Blue"), FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            b.Click += (s, e) => ShowModText(m);
            return b;
        }

        Window modTextWindow;

        /// <summary>The details window: the mod's whole description and, when there is an update,
        /// everything each version since changed, scrolling when it is long (the user, 2026-11-06).
        /// The text selects and copies across lines.</summary>
        void ShowModText(ModRow m)
        {
            modTextWindow?.Close();
            var root = new DockPanel { Margin = new Thickness(28, 20, 28, 20) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var body = new StackPanel();
            bool fromSite = string.IsNullOrWhiteSpace(m.Description) && !string.IsNullOrWhiteSpace(m.SiteSummary);
            string desc = fromSite ? m.SiteSummary.Trim() : (m.Description ?? "").Trim();
            if (desc.Length > 0)
            {
                body.Children.Add(Tag(Texts.Get(fromSite ? "site_words" : "manifest")));
                var dt = Selectable.Text(desc, 14, "Ink", null, 21);
                dt.Margin = new Thickness(0, 12, 0, 0);
                body.Children.Add(dt);
            }
            var u = Checked(m);
            if (u != null && (u.Status == "update" || u.Status == "unknown") && u.Changes.Count > 0)
            {
                var head = Tag(Texts.Get("up_changes"));
                head.Margin = new Thickness(0, desc.Length > 0 ? 28 : 0, 0, 0);
                body.Children.Add(head);
                var said = Selectable.Text(string.IsNullOrEmpty(u.Current) ? Texts.F("up_new_unknown", u.Latest) : Texts.F("up_new", u.Current, u.Latest), 12, "Muted");
                said.Margin = new Thickness(0, 10, 0, 4);
                body.Children.Add(said);
                body.Children.Add(ChangesList(u.Changes));
            }
            Selectable.Group(body);
            root.Children.Add(new Border
            {
                BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(0, 14, 0, 14),
                Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0), Focusable = false },
            });

            var w = new ChromeWindow(this, m.Name, root)
            {
                Width = 680, SizeToContent = SizeToContent.Height, MinHeight = 220,
                MaxHeight = Math.Max(360, Math.Min(820, ActualHeight - 60)), MinWidth = 480, ShowInTaskbar = false,
            };
            var close = new Button { Content = Texts.Get("close"), Style = (Style)FindResource("Ghost"), MinHeight = 38, Padding = new Thickness(18, 0, 18, 0) };
            close.Click += (s, e) => w.Close();
            buttons.Children.Add(close);
            w.Closed += (s, e) => { if (modTextWindow == w) modTextWindow = null; };
            modTextWindow = w;
            w.Show();
        }

        /// <summary>A section's name on the yellow tape, as the details' "manifest" tag.</summary>
        FrameworkElement Tag(string text) => new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, Background = B("Yellow"), Padding = new Thickness(8, 0, 8, 0),
            Child = new TextBlock { Text = text, FontFamily = Mono, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B("TapeInk") },
        };

        /// <summary>The newest version skipped, so the check stops offering it until a newer one
        /// comes, or offered again.</summary>
        async void SkipVersion(ModRow m, UpdateInfo u, bool skip)
        {
            if (await Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var mod = lib.Mods.FirstOrDefault(x => x.Id == m.Id) ?? throw new ModError("not in the library any more", "gone");
                if (skip) Versions.Skip(mod, u); else mod.Skip = null;
                lib.Save();
                return skip ? Texts.F("up_skip_done", mod.Name, u.Latest) : "";
            }), skip))
                u.Status = skip ? "skipped" : "update";
            Refresh();
        }

        /// <summary>The mod back to the version kept, the one it had kept instead; the game follows on the next deploy.</summary>
        async void Rollback(ModRow m)
        {
            if (!m.PrevExists) { Dialog.Tell(this, Texts.F("up_rollback_gone", m.PrevPath)); return; }
            await Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                var mod = lib.Mods.FirstOrDefault(x => x.Id == m.Id) ?? throw new ModError("not in the library any more", "gone");
                Versions.Rollback(lib, mod);
                lib.Save();
                return Texts.F("up_rollback_done", mod.Name, V(Origins.VersionOf(mod).Version ?? Texts.Get("up_none")));
            }), true);
        }

        /// <summary>Why a check failed, in the window's words.</summary>
        static string CheckWhy(string why)
        {
            switch (why)
            {
                case "offline": return Texts.Get("up_why_offline");
                case "missing": return Texts.Get("up_why_missing");
                case "busy": return Texts.Get("up_why_busy");
                // Nexus Mods is checked in every build, through its public API (Updates)
                case "nexus_offline": return Texts.Get("up_why_offline");
                case "nexus_busy": return Texts.Get("up_why_busy");
                case "nexus_missing": return Texts.Get("up_why_nexus_missing");
                case "nexus_error": return Texts.Get("up_why_nexus_error");
#if HAS_NEXUS
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
            var link = new NxmLink { Game = Nexus.Game, Mod = m.NexusMod, File = u.LatestFile };
            var (r, mod, _) = await NexusInstall(link, text => { });
            if (!r.Ok && r.Refused?.Kind == "nexus_refused")
            {
                Process.Start(new ProcessStartInfo(u.Page ?? m.Page) { UseShellExecute = true });
                Dialog.Tell(this, Texts.Get(Nexus.IsOurs(Exe) ? "up_nexus_page" : "up_nexus_page_manual"));
            }
            else if (!Refused(r) && r.Ok)
            {
                lastDone = Texts.F("took", Texts.F("up_done", mod.Name, u.Latest), Seconds(clock.Elapsed.TotalSeconds));
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
            : LuaPage.Visibility == Visibility.Visible ? LuaPage
            : TagsPage.Visibility == Visibility.Visible ? TagsPage
            : DownloadPage.Visibility == Visibility.Visible ? DownloadPage
            : SettingsPage.Visibility == Visibility.Visible ? SettingsPage
            : PluginsPage.Visibility == Visibility.Visible ? PluginsPage
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

        /// <summary>Every label in the library: in the player's order once they dragged one on the
        /// labels page, else the most used first.</summary>
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
            if (st != null && st.TagsOrdered)
            {
                // the library's order; a label it does not list yet (just made) goes last
                int At(string name)
                {
                    int i = st.Tags.FindIndex(t => string.Equals(t, name, StringComparison.CurrentCultureIgnoreCase));
                    return i < 0 ? int.MaxValue : i;
                }
                return counts.Select((x, i) => (x, i)).OrderBy(p => At(p.x.Name)).ThenBy(p => p.i).Select(p => p.x).ToList();
            }
            return counts.OrderByDescending(x => x.N).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ---------------------------------------------------------------- the labels page

        // dragging my labels into an order: the press (which label, where, where inside its row),
        // the rows that take part, the picture under the pointer and the line where it would land
        (string Name, Point Start, double GrabY)? tagPress;
        readonly List<(string Name, Button Row, Border Edge)> tagRows = new List<(string, Button, Border)>();
        Grid tagHost;
        Canvas tagLayer;
        UIElement tagGhost;
        Border tagLine;
        List<string> tagDropOrder;
        bool tagRedraw;

        /// <summary>The mouse on the list of labels: a press on one of mine that moves becomes a
        /// drag of it; let go, the labels go in the order shown. Our own drag, as the load
        /// order's: the mouse stays this window's.</summary>
        void HookTagDrag(Grid host)
        {
            host.PreviewMouseMove += (s, e) =>
            {
                if (tagPress == null || host != tagHost) return;
                var at = e.GetPosition(host);
                if (tagGhost == null)
                {
                    if (e.LeftButton != MouseButtonState.Pressed) { tagPress = null; return; }
                    if (Math.Abs(at.X - tagPress.Value.Start.X) < 6 && Math.Abs(at.Y - tagPress.Value.Start.Y) < 6) return;
                    if (!BeginTagDrag()) return;
                }
                TagDragAt(at);
                e.Handled = true;
            };
            host.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (tagGhost == null) { tagPress = null; return; }
                e.Handled = true;
                EndTagDrag(true);
            };
            // the pressed label loses the mouse to the list when a drag starts, and that bubbles
            // here too: only the list's own loss ends the drag (as the load order's)
            host.LostMouseCapture += (s, e) => { if (tagGhost != null && !ReferenceEquals(Mouse.Captured, host)) EndTagDrag(false); };
        }

        /// <summary>The pressed label taken up: its picture first, then the mouse taken (taking it
        /// raises a move at once). False when its row is gone.</summary>
        bool BeginTagDrag()
        {
            if (tagPress == null) return false;
            var row = tagRows.FirstOrDefault(r => r.Name == tagPress.Value.Name);
            if (row.Row == null || row.Edge.ActualWidth < 1) { tagPress = null; return false; }
            tagGhost = Ghost(row.Edge);
            tagLayer.Children.Add(tagGhost);
            tagLine = new Border { Height = 3, Background = B("Yellow"), Visibility = Visibility.Collapsed };
            tagLayer.Children.Add(tagLine);
            row.Row.Opacity = 0.3;
            tagHost.CaptureMouse();
            return true;
        }

        /// <summary>The picture under the pointer, and the line where the label would land: between
        /// the rows whose middle the pointer is past. A place that keeps the order shows none.</summary>
        void TagDragAt(Point at)
        {
            if (tagPress == null || tagGhost == null) return;
            var name = tagPress.Value.Name;
            Canvas.SetLeft(tagGhost, 0);
            Canvas.SetTop(tagGhost, at.Y - tagPress.Value.GrabY);
            int to = 0;
            foreach (var r in tagRows)
            {
                double top = r.Row.TranslatePoint(new Point(0, 0), tagHost).Y;
                if (at.Y > top + r.Row.ActualHeight / 2) to++;
            }
            var names = tagRows.Select(r => r.Name).ToList();
            int from = names.IndexOf(name);
            var order = names.Where(x => x != name).ToList();
            order.Insert(to > from ? to - 1 : to, name);
            tagDropOrder = order.SequenceEqual(names) ? null : order;
            if (tagDropOrder == null) { tagLine.Visibility = Visibility.Collapsed; return; }
            // the line on the top edge of the row it goes before, or under the last row
            var edgeRow = to < tagRows.Count ? tagRows[to].Row : tagRows[tagRows.Count - 1].Row;
            double y = edgeRow.TranslatePoint(new Point(0, 0), tagHost).Y + (to < tagRows.Count ? -1 : edgeRow.ActualHeight - 2);
            tagLine.Width = edgeRow.ActualWidth;
            Canvas.SetLeft(tagLine, 0);
            Canvas.SetTop(tagLine, y - 1);
            tagLine.Visibility = Visibility.Visible;
        }

        /// <summary>The drag over: the labels put in the order shown (commit), or left alone.</summary>
        void EndTagDrag(bool commit)
        {
            var order = tagDropOrder;
            var host = tagHost;
            if (tagLayer != null) { tagLayer.Children.Remove(tagGhost); tagLayer.Children.Remove(tagLine); }
            foreach (var r in tagRows) r.Row.Opacity = 1;
            tagGhost = null; tagLine = null; tagPress = null; tagDropOrder = null;
            if (host != null && host.IsMouseCaptured) host.ReleaseMouseCapture();
            // a read that came during the drag, drawn now
            if (tagRedraw)
            {
                tagRedraw = false;
                Dispatcher.BeginInvoke(new Action(() => { if (TagsPage.Visibility == Visibility.Visible && tagGhost == null && !PageFresh("tags")) RenderTags(); }));
            }
            if (!commit || order == null || st == null || busy) return;
            var args = new List<string> { "tag-order" };
            args.AddRange(order);
            // shown at once from what it implies, saved in the background, as the load order's
            // drag (the user, 2026-11-14: the list stood still a second or two after the drop)
            var rest = st.Tags.Where(t => !order.Contains(t, StringComparer.CurrentCultureIgnoreCase)).ToList();
            st.Tags = order.Concat(rest).ToList();
            st.TagsOrdered = true;
            RenderTags();
            TagOrderSaved(args.ToArray());
        }

        /// <summary>The labels' new order written, the window not held; then the state again.</summary>
        async void TagOrderSaved(string[] args)
        {
            waiting++;
            var r = await Queued(() => Cli.Run(args));
            waiting--;
            quietUntil = DateTime.Now.AddSeconds(1.5);
            Refused(r);
            Refresh();
        }

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
            // a drag under way ends with the rows it was over
            if (tagGhost != null) EndTagDrag(false);
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
            // the list, with the dragged label's picture and where it would land drawn over it
            var leftHost = new Grid();
            Grid.SetColumn(leftHost, 0);
            two.Children.Add(leftHost);
            var left = new StackPanel();
            leftHost.Children.Add(left);
            var layer = new Canvas { IsHitTestVisible = false };
            leftHost.Children.Add(layer);
            tagHost = leftHost;
            tagLayer = layer;
            tagRows.Clear();
            HookTagDrag(leftHost);

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
                // my labels are put in order by dragging them (the user, 2026-11-14); a press
                // that does not move is still a click
                if (!isAuto && mineShown.Count > 1)
                {
                    tagRows.Add((mineName, b, edge));
                    b.PreviewMouseLeftButtonDown += (s, e) =>
                    {
                        var at = e.GetPosition(leftHost);
                        tagPress = (mineName, at, at.Y - b.TranslatePoint(new Point(0, 0), leftHost).Y);
                    };
                }
                left.Children.Add(b);
            }

            if (mineShown.Count > 0)
            {
                left.Children.Add(Label("tag_mine", new Thickness(2, 6, 0, 6)));
                foreach (var (name, n) in mineShown) Row(name, n, false);
                if (mineShown.Count > 1 && Texts.Hints) left.Children.Add(Small(Texts.Get("tag_drag_hint"), new Thickness(2, 6, 0, 0)));
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
                    // a drawn cross, centred on the row: the text "✕" sat higher than the name beside
                    // it (the user, 2026-11-04), as it was cut in the search field
                    var cross = new Path
                    {
                        Data = Geometry.Parse("M0,0 L9,9 M9,0 L0,9"), Stroke = B("Muted"), StrokeThickness = 1.5,
                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    };
                    var offBtn = new Button
                    {
                        Content = cross, Template = Plain(), Cursor = Cursors.Hand, FocusVisualStyle = null, Background = Brushes.Transparent,
                        Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.F("tag_off", m.Name),
                    };
                    offBtn.MouseEnter += (s, e) => cross.Stroke = B("Ink");
                    offBtn.MouseLeave += (s, e) => cross.Stroke = B("Muted");
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
        /// <summary>The gear: the settings, on General; on About and diagnostics while a newer
        /// version of this program is out, where it is said (the red dot).</summary>
        void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsPage.Visibility == Visibility.Visible) { ShowPage(pageBack); return; }
            settingsTab = UpdateDot.Visibility == Visibility.Visible ? "about" : "general";
            ShowPage("settings");
        }

        /// <summary>The settings' tab on show: "general" (what the manager does and shows) or
        /// "about" (its version, the problem report, its log, who made it).</summary>
        string settingsTab = "general";

        /// <summary>The manager's own log on the settings page (ManagerLog).</summary>
        LogView managerLog;

        LogView ManagerLogView() => managerLog ?? (managerLog = new LogView
        {
            Group = "mgrlog", Kinds = () => new List<string> { "manager" }, SessionKey = "set_log_since_start", FullTime = false, Height = 380,
            // the command line writes between the window's sessions: all of it at first
            All = true,
        });

        /// <summary>The settings page (the user, 2026-10-24; a page, not a window, since
        /// 2026-10-27) in two tabs (the user, 2026-11-01: it had become a muddle): General, the
        /// interface and how mods are managed; About and diagnostics, this program's version and
        /// updates, the problem report, its log, who made it.</summary>
        void RenderSettings()
        {
            SettingsBox.Children.Clear();
            var head = new DockPanel();
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (key, label) in new[] { ("general", "set_tab_general"), ("about", "set_tab_about") })
            {
                var b = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "settab", Content = Texts.Get(label), MinWidth = 120, IsChecked = settingsTab == key };
                var k = key;
                b.Click += (s, e2) => { settingsTab = k; RenderSettings(); SettingsPage.ScrollToTop(); };
                tabs.Children.Add(b);
            }
            var tabBox = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Child = tabs };
            DockPanel.SetDock(tabBox, Dock.Right);
            head.Children.Add(tabBox);
            head.Children.Add(new TextBlock { Text = Texts.Get("settings"), FontSize = 28, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            SettingsBox.Children.Add(head);
            var general = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var about = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            SettingsBox.Children.Add(settingsTab == "about" ? about : general);
            TextBlock Group(string key) => new TextBlock { Text = Texts.Get(key), FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 24, 0, 0) };
            var body = general;

            body.Children.Add(Group("set_group_ui"));
            // the language
            body.Children.Add(Label("set_lang", new Thickness(0, 20, 0, 6)));
            var langs = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
            // every language the window has a file for, each in its own name
            var pair = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (tag, name) in Texts.Languages)
            {
                var one = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "setlang", Content = name, MinWidth = 96, IsChecked = tag == Texts.Lang };
                one.Click += (s, e2) => Language(tag);
                pair.Children.Add(one);
            }
            langs.Child = pair;
            body.Children.Add(langs);

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

            body.Children.Add(Group("set_group_mods"));
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

            // script mods keep the version they had, for a rollback (the user, 2026-10-30)
            body.Children.Add(Label("set_keep", new Thickness(0, 20, 0, 6)));
            var kpSw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = st?.KeepPrevious == true, VerticalAlignment = VerticalAlignment.Center };
            var kpLine = new StackPanel { Orientation = Orientation.Horizontal };
            kpLine.Children.Add(kpSw);
            kpLine.Children.Add(new TextBlock { Text = Texts.Get("set_keep_on"), Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            kpSw.Click += async (s, e2) =>
            {
                await Exec(kpSw.IsChecked == true ? "keep-previous-on" : "keep-previous-off");
                Refresh();
            };
            body.Children.Add(kpLine);
            body.Children.Add(Small(Texts.Get("set_keep_note"), new Thickness(0, 8, 0, 0)));

            // every mod checked when the window opens, once a day (the user, 2026-11-04)
            body.Children.Add(Label("set_mod_updates", new Thickness(0, 20, 0, 6)));
            var muSw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = Updates.AtStart, VerticalAlignment = VerticalAlignment.Center };
            var muLine = new StackPanel { Orientation = Orientation.Horizontal };
            muLine.Children.Add(muSw);
            muLine.Children.Add(new TextBlock { Text = Texts.Get("set_mod_updates_on"), Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            muSw.Click += (s, e2) =>
            {
                try { Updates.AtStart = muSw.IsChecked == true; }
                catch (Exception err) when (err is IOException || err is UnauthorizedAccessException) { muSw.IsChecked = Updates.AtStart; }
            };
            body.Children.Add(muLine);
            body.Children.Add(Small(Texts.Get("set_mod_updates_note"), new Thickness(0, 8, 0, 0)));

            // About and diagnostics, built only when it is the tab on show: built behind General every
            // time, its log made General as slow to open as itself (the user, 2026-11-06)
            if (settingsTab != "about") return;
            body = about;

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
                    var notes = new Button { Content = Texts.Get("self_notes"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(0, 0, 8, 0) };
                    notes.Click += (s, e2) => ShowUpdateNotes(SelfUpdate.Last());
                    btns.Children.Add(notes);
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

            // a problem report to send with a question (the user, 2026-10-29: one click, here); its
            // window uploads it, or saves it for the player to send (ReportDialog, 2026-11-03)
            body.Children.Add(Label("set_report", new Thickness(0, 20, 0, 6)));
            body.Children.Add(Small(Texts.Get("report_note"), new Thickness(0, 0, 0, 10)));
            var make = new Button { Content = Texts.Get("report_do"), Style = (Style)FindResource("Ghost"), MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Left };
            make.Click += (s, e2) => OpenReportDialog();
            body.Children.Add(make);

            // the manager's own log (the user, 2026-11-01): what it was asked to do and how that ended
            var managerLogHead = Label("set_log", new Thickness(0, 20, 0, 6));
            body.Children.Add(managerLogHead);
            if (Texts.Hints) body.Children.Add(Small(Texts.Get("set_log_note"), new Thickness(0, 0, 0, 10)));
            var mlog = ManagerLogView();
            mlog.Box = new StackPanel { MinHeight = 120 };
            body.Children.Add(mlog.Box);
            // the log once the tab is on screen: laying out its few hundred lines took a fifth of a
            // second, and the tab waited for it (the user, 2026-11-06)
            var logBox = mlog.Box;
            Dispatcher.BeginInvoke(new Action(() => { if (mlog.Box == logBox && logBox.IsVisible) RenderLog(mlog); }), DispatcherPriority.Background);

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
        /// (tagKey). A click on a line shows that mod in the details, on or off (the user,
        /// 2026-11-03: the details panel is there for it); the button on another enabled line
        /// (pointed at, or chosen) makes it win: it moves just after the last of the others in the
        /// load order (the last one wins); the others keep their places. inGame: whose copy the
        /// game has now (the last deploy); when a choice has not been deployed yet, that one keeps
        /// tagKey and its solid frame, and the one chosen is framed in dashes and marked "in effect
        /// after deploy" (the user, 2026-11-03: a click must change the view itself).</summary>
        UIElement WinnerRows(List<string> ids, string now, string tagKey, string inGame = null, bool switches = false, Func<ModRow, UIElement> below = null, bool choose = true)
        {
            // by name, not in the load order: a click moved the mod, and the rows swapped places
            // under the pointer; what is in effect is what they are for (the user, 2026-11-03)
            var mods = ids.Select(id => st.Mods.FirstOrDefault(x => x.Id == id)).Where(x => x != null)
                          .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
            var key = string.Join("|", ids.OrderBy(x => x, StringComparer.Ordinal));
            now = NowOf(ids, now);
            bool pending = inGame != null && inGame != now && mods.Any(x => x.Id == inGame);
            var rowsBox = new StackPanel();
            // one height for every row when some have a line under the name
            bool twoLines = below != null && mods.Any(x => below(x) != null);
            foreach (var x in mods)
            {
                bool off = !x.Enabled;                        // shown with its switch (the conflicts view)
                bool wins = x.Id == now;                      // the load order's: what a deploy gives
                bool solid = (pending ? x.Id == inGame : wins) && !off; // the game's now
                bool next = pending && wins;                  // chosen, not deployed yet
                var line = new DockPanel();
                if (switches)
                {
                    var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = x.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = x.Name };
                    var m = x;
                    sw.Click += (s, e) =>
                    {
                        bool turnOn = sw.IsChecked == true;
                        if (turnOn && st.ProfilesOn && !m.InProfile) { Run("profile-add", m.Id); return; }
                        Change(m.Id, c => c.Enabled = turnOn, turnOn ? "enable" : "disable", m.Id);
                    };
                    DockPanel.SetDock(sw, Dock.Left);
                    line.Children.Add(sw);
                }
                var pic = Picture(x.Icon, GlyphMod, 28, off ? "Muted" : "Blue", 18);
                // its picture opens its preview, as in the groups and the order (the user, 2026-11-06)
                PictureOpensPreview(pic, x);
                pic.Opacity = off ? 0.55 : 1;
                DockPanel.SetDock(pic, Dock.Left);
                line.Children.Add(pic);
                // the tag at the right edge, the choice left of it: pointing at a line moves nothing
                if (solid || next)
                {
                    var tag = new Border
                    {
                        Background = solid ? B("Yellow") : Brushes.Transparent, BorderBrush = B("Yellow"), BorderThickness = new Thickness(solid ? 0 : 1),
                        Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                        Child = new TextBlock { Text = Texts.Get(solid ? tagKey : "pick_after_deploy"), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = B(solid ? "TapeInk" : "Yellow") },
                    };
                    DockPanel.SetDock(tag, Dock.Right);
                    line.Children.Add(tag);
                }
                Button use = null;
                if (choose && !wins && !off)
                {
                    // the choice: on the line pointed at, or the one chosen
                    use = new Button
                    {
                        Content = Texts.Get(tagKey == "pick_runs_now" ? "pick_run_this" : "pick_use_this"), Style = (Style)FindResource("Ghost"), MinHeight = 26, Padding = new Thickness(10, 0, 10, 0),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Tag = "pick",
                        Visibility = x.Id == selectedId ? Visibility.Visible : Visibility.Collapsed,
                    };
                    DockPanel.SetDock(use, Dock.Right);
                    line.Children.Add(use);
                }
                var named = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                named.Children.Add(new TextBlock { Text = x.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = x.Name, Opacity = off ? 0.55 : 1 });
                var under = below?.Invoke(x);
                if (under != null) named.Children.Add(under);
                line.Children.Add(named);
                bool shown = x.Id == selectedId;              // in the details now
                var row = new Border
                {
                    BorderBrush = next ? Brushes.Transparent : B(solid ? "Yellow" : shown ? "Muted" : "Grid"), BorderThickness = new Thickness(1), Background = B(shown ? "Band" : "Ground900"),
                    Padding = new Thickness(8, 6, 8, 6), Child = line, Cursor = Cursors.Hand, Tag = x.Id, MinHeight = twoLines ? 58 : 0,
                };
                // the chosen one's frame in dashes, over the row (it takes no clicks)
                var cell = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                cell.Children.Add(row);
                if (next)
                    cell.Children.Add(new Rectangle { Stroke = B("Yellow"), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false });
                var id = x.Id;
                var rest = row.BorderBrush;
                row.MouseEnter += (s, e) => { if (!solid && !next) row.BorderBrush = B("Muted"); if (use != null) use.Visibility = Visibility.Visible; };
                row.MouseLeave += (s, e) => { row.BorderBrush = rest; if (use != null && id != selectedId) use.Visibility = Visibility.Collapsed; };
                // a click on the line: that mod in the details
                row.MouseLeftButtonUp += (s, e) =>
                {
                    if (selectedId == id) return;
                    Current(id);
                    RenderList();
                    RenderDetail();
                };
                if (use != null)
                    use.Click += (s, e) =>
                    {
                        // drawn at once, and kept until a state read that began after the move
                        // finished: a read already on its way when the button was clicked has the
                        // old winner, and the frame jumped back to it for a moment (the user, 2026-11-03)
                        picksNow[key] = (id, null);
                        var last = mods.Where(o => o.Id != id && o.Enabled).OrderBy(o => o.Index).LastOrDefault();
                        if (last != null && mods.First(o => o.Id == id).Index < last.Index)
                            PickMove(key, id, new[] { "move", id, "--after", last.Id });
                        else picksNow[key] = (id, readsStarted);
                        if (filter == "conflict") RenderConflicts();
                        else RenderDetail();
                    };
                rowsBox.Children.Add(cell);
            }
            return rowsBox;
        }

        // the choices clicked in WinnerRows, by the mods of the set: the mod, and the state reads
        // started when its move finished (null: not finished yet)
        readonly Dictionary<string, (string Id, int? DoneAt)> picksNow = new Dictionary<string, (string, int?)>();

        async void PickMove(string key, string id, string[] args)
        {
            bool ran = false;
            await Act(() => Cli.Run(args), false, () =>
            {
                ran = true;
                if (picksNow.TryGetValue(key, out var p) && p.Id == id) picksNow[key] = (id, readsStarted);
            });
            if (ran) return;
            // turned away (another command was running): the choice is not made
            if (picksNow.TryGetValue(key, out var q) && q.Id == id) picksNow.Remove(key);
            if (filter == "conflict") RenderConflicts();
            else RenderDetail();
        }

        /// <summary>Whose version of a set's resources a deploy uses, as the window has it now: a
        /// choice just clicked, until the state it was made in is read again; with a switch just
        /// turned (not read yet), the last mod on in the load order; else the state's.</summary>
        string NowOf(List<string> ids, string stated)
        {
            var key = string.Join("|", ids.OrderBy(x => x, StringComparer.Ordinal));
            if (picksNow.TryGetValue(key, out var pick))
            {
                // the state on show was read before the move finished: it does not have it yet
                if (pick.DoneAt == null || st.ReadNo <= pick.DoneAt) return pick.Id;
                picksNow.Remove(key);
            }
            var mods = ids.Select(ById).Where(x => x != null).ToList();
            if (mods.Any(x => x.Assumed != null))
                return mods.Where(x => x.Enabled).OrderBy(x => x.Index).LastOrDefault()?.Id;
            return stated;
        }

        /// <summary>Game items as words: "护甲：CM-09 Bonesnapper / AF-02 Haz-Master", the items of
        /// one category together, the category in the window's language when it has it.</summary>
        static string ItemsLabel(List<(string Category, string Name)> items) =>
            string.Join(Texts.Get("sep_clause"), items.GroupBy(i => i.Category)
                .Select(g => Texts.F("archive_named", CategoryLabel(g.Key), string.Join(" / ", g.Select(i => i.Name)))));

        static string CategoryLabel(string category)
        {
            var key = "cat_" + System.Text.RegularExpressions.Regex.Replace(category ?? "", "[^A-Za-z0-9]+", "_").Trim('_');
            return Texts.Has(key) ? Texts.Get(key) : category;
        }

        /// <summary>A conflict's resource in words: its name, else the game items it belongs to, else null.</summary>
        static string ResourceWords((string Type, string Id, string Name, List<(string Category, string Name)> Of) r)
        {
            bool items = r.Of != null && r.Of.Count > 0;
            // "Helmet O-3 Free Spirit" from the name list names the same as the game item, whose
            // category is in the window's language: the item then (the user, 2026-11-03)
            if (r.Name != null && items && r.Of.Any(o => r.Name.IndexOf(o.Name, StringComparison.OrdinalIgnoreCase) >= 0)) return ItemsLabel(r.Of);
            return r.Name ?? (items ? ItemsLabel(r.Of) : null);
        }

        /// <summary>A conflict's resources, one line for each kind of them (Clash.Groups): the type,
        /// how many when more than one, then the name or the game item ("模型 ×16 · 护甲：O-3 Free
        /// Spirit"); with ids, the kinds with no name by their first id. Then how many are not
        /// shown. The same words were listed once a resource (the user, 2026-11-03).</summary>
        StackPanel ResourceLines(Clash c, int lines, double size, string colour, bool ids)
        {
            var list = new StackPanel();
            int shown = 0;
            foreach (var g in c.Groups.Where(g => ids || GroupWords(g) != null).Take(lines))
            {
                var dp = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var type = new TextBlock { Text = TypeLabel(g.Type) + (g.Count > 1 ? " ×" + g.Count : "") + "  ·  ", FontSize = size, Foreground = B("Muted") };
                DockPanel.SetDock(type, Dock.Left);
                dp.Children.Add(type);
                var words = GroupWords(g);
                var what = words != null ? Selectable.Text(words, size, colour)
                    : Selectable.Text(g.Ids.FirstOrDefault() + (g.Count > 1 ? " …" : ""), size - 1, "Muted", Mono);
                what.VerticalAlignment = VerticalAlignment.Center;
                dp.Children.Add(what);
                list.Children.Add(dp);
                shown += g.Count;
            }
            if (list.Children.Count > 0 && c.Count > shown)
                list.Children.Add(Small(Texts.F("clash_more", c.Count - shown), new Thickness(0, 2, 0, 0)));
            return list;
        }

        static string GroupWords((string Type, string Name, List<(string Category, string Name)> Of, int Count, List<string> Ids) g) =>
            ResourceWords((g.Type, null, g.Name, g.Of));

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
            if (st != null && !st.OnOfficial) _ = DeployNow();
            else if (st != null && st.GameDir == null) ChooseGame();
            else _ = DeployNow();
        }

        Task<bool> DeployNow() => st != null && !st.OnOfficial ? DeployToTarget() : Act(() => Cli.Call(() =>
        {
            var r = Hd2Mod.Deploy.Run(Library.Load(), Store.GameDir());
            var said = Texts.F("deployed", Texts.N(r.Kept), Texts.N(r.Moved), Texts.N(r.Written), Texts.N(r.Linked), Texts.N(r.Copied),
                               Texts.N(r.Deleted), Texts.N(r.Files));
            var m3 = Mdl3Native.Last?.Kind == "failed" ? null : Mdl3Said(out _);
            return m3 != null ? said + " · " + m3 : said;
        }), true);

        /// <summary>The game through Steam; changes not deployed yet are deployed first,
        /// when the player says so.</summary>
        async void Launch_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            if (st != null && !st.OnOfficial) { await LaunchTarget(); return; }
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

        void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (!Dialog.Ask(this, Texts.Get("undo_changes_ask"), Texts.Get("undo_changes_do"))) return;
            _ = Act(() => Cli.Call(() =>
            {
                var (changed, gone) = Library.UndoSinceDeploy();
                if (!changed) return Texts.Get("undo_changes_none");
                return gone.Count == 0 ? Texts.Get("undo_changes_done") : Texts.F("undo_changes_gone", string.Join(Texts.Get("sep_list"), gone));
            }), true);
        }

        void Purge_Click(object sender, RoutedEventArgs e)
        {
            if (st != null && !st.OnOfficial)
            {
                if (Dialog.Ask(this, Texts.F("target_purge_ask", st.TargetTitle), Texts.Get("purge")))
                    _ = Act(() => Cli.Call(() => PluginTargets.Purge(PluginTargets.Of(st.Target, PluginHostHere()), PluginHostHere())), true);
            }
            else if (st != null && st.GameDir == null) ChooseGame();
            else if (Dialog.Ask(this, Texts.Get("purge_ask"), Texts.Get("purge")))
                _ = Act(() => Cli.Call(() => Texts.F("purged", Texts.N(Hd2Mod.Deploy.Purge(Store.GameDir())))), true);
        }

        void Add_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = AddBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var zip = new MenuItem { Header = Texts.Get("add_zip") };
            zip.Click += async (s, a) => { var p = PickZips(); if (p != null) await DropPaths(p); };
            var folder = new MenuItem { Header = Texts.Get("add_folder") };
            folder.Click += async (s, a) => { var p = PickFolders(); if (p != null) await DropPaths(p); };
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
            await DropPaths(many);
        }

        /// <summary>HD2QD_DROP_TEST=PATH (pictures only): 3 s after the start, that file or folder
        /// dropped on the list, the way a drop does it (MDL3's one-click install for the video, the
        /// user 2026-11-01).</summary>
        void DropTest()
        {
            var path = Environment.GetEnvironmentVariable("HD2QD_DROP_TEST");
            if (string.IsNullOrEmpty(path)) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += async (s, e) => { t.Stop(); await DropPaths(new List<string> { path }); };
            t.Start();
        }

        /// <summary>Files and folders dropped on the list: each added, the first new one shown, what
        /// happened (MDL3's native part too) at the bottom.</summary>
        async Task DropPaths(List<string> many)
        {
            if (busy) return;
            var before = new HashSet<string>(Library.Load().Mods.Select(m => m.Id));
            int added = 0;
            mdl3Note = null;
            foreach (var p in many) if (await Act(() => Cli.Run("add", p), false)) added++;
            var fresh = Library.Load().Mods.Where(m => !before.Contains(m.Id)).ToList();
            var foot = Texts.F("drop_processed", Texts.N(added)) + (mdl3Note != null ? " · " + mdl3Note : "");
            // now, and again after the state read the adds started
            if (added > 0) { Foot(foot, "Blue"); lastDone = foot; }
            if (fresh.Count == 0) return;
            Current(fresh[0].Id);
            AdviseDrive(fresh);
        }

        const string ArchivePatterns = "*.zip;*.7z;*.rar;*.tar;*.gz;*.xz";

        string PickZip() => Pick.File(this, Texts.Get("pick_zip_title"), Texts.Get("pick_zip_kind"), ArchivePatterns);

        /// <summary>Archives to add, several at once: added one by one as dropped ones are.</summary>
        List<string> PickZips() => Pick.Files(this, Texts.Get("pick_zips_title"), Texts.Get("pick_zip_kind"), ArchivePatterns)?.ToList();

        string PickFolder(string titleKey = "pick_mod_title") => Pick.Folder(this, Texts.Get(titleKey));

        /// <summary>Mod folders to add, several at once (issue 13): added one by one as dropped ones are.</summary>
        List<string> PickFolders() => Pick.Folders(this, Texts.Get("pick_mods_title"))?.ToList();

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
            FitToScreen();
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
