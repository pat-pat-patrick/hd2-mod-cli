using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Hd2Mod;

namespace QuickDeploy
{
    // The conflicts view (the library's Conflicts, the user, 2026-11-03): who competes with whom,
    // each set of mods that replace the same resources one card, its names joined on the left by
    // a bracket. Every mod in a set stays on and what it does not share takes effect; only the
    // shared resources come from one of them, chosen here (WinnerRows).
    public partial class MainWindow
    {
        /// <summary>The conflicts, in place of the mod list: the sets of mods that replace the same
        /// resources with different bytes, the larger sets first, then the more resources. A set
        /// whose files are the very same bytes, or a boot the loader coordinates, is no conflict.</summary>
        void RenderConflicts()
        {
            ListHint.Text = Texts.Get("cf_hint");
            // in an order a choice does not change (the load order's names did)
            var sets = st.AllClashes.Where(c => !c.Same && !c.ManagedBoot && c.Mods.Count > 1)
                                 .OrderByDescending(c => c.Mods.Count).ThenByDescending(c => c.Count)
                                 .ThenBy(c => string.Join("\u001f", c.Mods.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)), StringComparer.CurrentCultureIgnoreCase).ToList();
            // a card is drawn again only when what it shows changed; the others stay as they are.
            // Every click drew the whole view twice (at once, and after the state read), and it
            // flickered (the user, 2026-11-03)
            var want = new List<UIElement>();
            var keep = new HashSet<string>();
            int hidden = 0;
            foreach (var c in sets)
            {
                var ids = c.Mods.Select(n => st.Mods.FirstOrDefault(x => x.Name == n)?.Id).Where(id => id != null).ToList();
                // the tick off: only what conflicts now (StateOf)
                if (!showPotential && StateOf(c, ids) != "conflict") { hidden++; continue; }
                // by its mods and its items: two items can have the same mods
                var key = string.Join("|", ids.OrderBy(x => x, StringComparer.Ordinal)) + "#" + string.Join("|", c.Groups.SelectMany(g => g.Of).Select(a => a.Category + "/" + a.Name).Distinct());
                var sig = CardSig(c, ids);
                if (!cfCards.TryGetValue(key, out var had) || had.Sig != sig) cfCards[key] = had = (sig, ConflictCard(c, ids));
                keep.Add(key);
                want.Add(had.Card);
            }
            foreach (var k in cfCards.Keys.Where(k => !keep.Contains(k)).ToList()) cfCards.Remove(k);
            if (want.Count == 0)
            {
                // none now, some possible: the tick says where they are
                var none = Texts.Get(hidden > 0 ? "cf_none_now" : "cf_none");
                if (!(List.Children.Count == 1 && List.Children[0] is TextBlock t && t.Text == none))
                {
                    List.Children.Clear();
                    List.Children.Add(new TextBlock { Text = none, Foreground = B("Muted"), Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap });
                }
                return;
            }
            if (List.Children.Count == want.Count && want.Select((w, i) => List.Children[i] == w).All(x => x)) return;
            double at = ListScroll.VerticalOffset;
            List.Children.Clear();
            foreach (var w in want) List.Children.Add(w);
            Dispatcher.BeginInvoke(new Action(() => ListScroll.ScrollToVerticalOffset(at)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // the cards on show, by their mods, with what they show (CardSig)
        readonly Dictionary<string, (string Sig, UIElement Card)> cfCards = new Dictionary<string, (string, UIElement)>();

        /// <summary>What a card shows: its resources, its mods, which is on, whose version is in
        /// use now and in the game. The same signature draws the same card.</summary>
        string CardSig(Clash c, List<string> ids)
        {
            var sb = new System.Text.StringBuilder();
            void Add(object x) => sb.Append(x).Append('\u001f');
            Add(Texts.Lang); Add(c.Count);
            foreach (var g in c.Groups) { Add(g.Type); Add(g.Count); Add(GroupWords(g)); }
            foreach (var id in ids.OrderBy(x => x, StringComparer.Ordinal))     // not the load order, which a choice changes
            {
                var m = st.Mods.First(x => x.Id == id);
                Add(m.Id); Add(m.Name); Add(m.Icon); Add(m.Enabled); Add(m.InProfile); Add(m.Id == selectedId);
                foreach (var o in m.Options)
                {
                    Add(o.Name); Add(o.Enabled); Add(string.Join("|", o.Clashes));
                    foreach (var s in o.Subs) { Add(s.Name); Add(s.Enabled); Add(string.Join("|", s.Clashes)); }
                }
            }
            foreach (var f in c.From) { Add(f.Mod); Add(f.Option); Add(f.Sub); }
            Add(c.Now);
            Add(NowOf(ids, st.Mods.FirstOrDefault(x => x.Name == c.Winner)?.Id));
            Add(st.Mods.FirstOrDefault(x => x.Name == c.Deployed)?.Id);
            return sb.ToString();
        }

        /// <summary>Whether a set conflicts now ("conflict"), or why not ("options", "one_on",
        /// "none_on"). A switch just turned (not read yet): by the mods on, as the state will say.</summary>
        string StateOf(Clash c, List<string> ids)
        {
            var mods = ids.Select(ById).Where(x => x != null).ToList();
            int on = mods.Count(x => x.Enabled);
            return mods.Any(x => x.Assumed != null) ? (on > 1 ? (c.Now == "options" ? "options" : "conflict") : on == 1 ? "one_on" : "none_on")
                 : c.Now ?? (on > 1 ? "conflict" : on == 1 ? "one_on" : "none_on");
        }

        /// <summary>One set: how many resources and mods, the mods with their switches (whose
        /// version is used marked; a click on another enabled one uses its), and the first of the
        /// resources.</summary>
        UIElement ConflictCard(Clash c, List<string> ids)
        {
            var card = new StackPanel();
            // a set stays when it conflicts no more (every option counts, chosen or not: the user,
            // 2026-11-03): its heading grey, and why. A switch just turned (not read yet): by the
            // mods on, as the state will say
            var state = StateOf(c, ids);
            bool clashing = state == "conflict";
            // the heading is what they replace: the game item, else the resource's name, else its
            // type; how many mods and resources is the line under it (the user, 2026-11-03: the
            // count was the heading and what it was the small print)
            var (title, named) = CardTitle(c);
            card.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = B(clashing ? "Yellow" : "Muted"), TextWrapping = TextWrapping.Wrap });
            var types = c.Groups.GroupBy(g => TypeLabel(g.Type)).Select(g => (Type: g.Key, Count: g.Sum(x => x.Count))).ToList();
            var why = Texts.F("cf_why", c.Mods.Count, c.Count, types.Count == 1 ? types[0].Type : string.Join("  ·  ", types.Select(t => t.Type + " ×" + t.Count)));
            if (!clashing) why += "  ·  " + Texts.Get(state == "options" ? "cf_quiet_options" : state == "one_on" ? "cf_quiet_1" : "cf_quiet_0");
            card.Children.Add(new TextBlock { Text = why, FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 3, 0, 10), TextWrapping = TextWrapping.Wrap });
            // the option that brings it, under the mod's name: a click offers the others, each
            // with what it would conflict with (the user, 2026-11-03: switching mods on and off,
            // or choosing whose version wins, does not solve a conflict an option brings)
            UIElement FromLine(ModRow m)
            {
                var mine = c.From.Where(f => f.Mod == m.Name && f.Option >= 0 && f.Option < m.Options.Count).ToList();
                if (mine.Count == 0) return null;
                // the option as it is chosen now (any of its choices may be the one in the set)
                string Label((string Mod, int Option, int Sub) f)
                {
                    var o = m.Options[f.Option];
                    if (!o.Enabled) return o.Name + Texts.Get("cf_opt_closed");
                    var sub = o.Subs.FirstOrDefault(x => x.Enabled);
                    return sub != null ? o.Name + " › " + sub.Name : o.Name;
                }
                var link = new Button { Template = Plain(), Cursor = System.Windows.Input.Cursors.Hand, FocusVisualStyle = null, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
                // the arrow the Filter and Labels buttons have (Caret), not a character (the user,
                // 2026-11-03: the small triangle was hard to see)
                var words = new TextBlock { Text = Texts.F("cf_from", string.Join(Texts.Get("sep_list"), mine.Select(Label).Distinct())), FontSize = 13,
                                            Foreground = B("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                var arrow = new System.Windows.Shapes.Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M0,0 L5,5 L10,0"), Stroke = B("Muted"), StrokeThickness = 1.6,
                    Margin = new Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(words);
                content.Children.Add(arrow);
                link.Content = content;
                link.MouseEnter += (s, e) => { words.Foreground = B("Ink"); arrow.Stroke = B("Ink"); };
                link.MouseLeave += (s, e) => { words.Foreground = B("Muted"); arrow.Stroke = B("Muted"); };
                link.Click += (s, e) =>
                {
                    var menu = OptionMenu(m, mine.Select(f => f.Option).Distinct().ToList());
                    lastOptionMenu = menu;
                    menu.PlacementTarget = link;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                };
                return link;
            }
            // whose version: only while they conflict
            var winner = clashing ? st.Mods.FirstOrDefault(x => x.Name == c.Winner)?.Id : null;
            var inGame = clashing ? st.Mods.FirstOrDefault(x => x.Name == c.Deployed)?.Id : null;     // whose copy the game has now
            // no bracket: each set is a card of its own (the user, 2026-11-03)
            // every mod of the set, on or off, whatever the tick: it hides sets, never a set's rows
            // (the user, 2026-11-04)
            card.Children.Add(WinnerRows(ids, winner, "pick_now", inGame, true, FromLine, clashing));
            // under the mods, only the named resources the heading does not say already ("贴图 ·
            // <its name>"); the item's own ones repeated it (the user, 2026-11-03). An id alone tells
            // a player nothing and is left to the mod's details
            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var g in c.Groups.Where(g => !named.Contains(g)).Take(5))
                list.Children.Add(new TextBlock
                {
                    Text = TypeLabel(g.Type) + (g.Count > 1 ? " ×" + g.Count : "") + "  ·  " + GroupWords(g), FontSize = 12, Foreground = B("Muted"),
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 1),
                });
            if (list.Children.Count > 0) card.Children.Add(list);
            return new Border
            {
                BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(20, 16, 20, 16), Child = card,
            };
        }

        /// <summary>A card's heading: its game items ("护甲：O-3 Free Spirit", the first four and how
        /// many more), else the first resource name, else the types; with the kinds of resources
        /// it says, which the card does not list again, and those with no name.</summary>
        (string Title, HashSet<(string Type, string Name, List<(string Category, string Name)> Of, int Count, List<string> Ids)> Said) CardTitle(Clash c)
        {
            var said = new HashSet<(string Type, string Name, List<(string Category, string Name)> Of, int Count, List<string> Ids)>(
                c.Groups.Where(g => GroupWords(g) == null || (g.Of.Count > 0 && GroupWords(g) == ItemsLabel(g.Of))));
            // an item's name once, with its categories ("护甲 / 头盔：SR-24 Street Scout")
            var items = c.Groups.SelectMany(g => g.Of).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(g => (Cats: string.Join(" / ", g.Select(a => CategoryLabel(a.Category)).Distinct()), Name: g.Key)).ToList();
            if (items.Count > 0)
                return (string.Join(Texts.Get("sep_clause"), items.Take(4).GroupBy(i => i.Cats).Select(g => Texts.F("archive_named", g.Key, string.Join(" / ", g.Select(i => i.Name)))))
                        + (items.Count > 4 ? Texts.F("cf_items_more", items.Count - 4) : ""), said);
            var first = c.Groups.FirstOrDefault(g => GroupWords(g) != null);
            if (first.Type != null) { said.Add(first); return (GroupWords(first), said); }
            return (string.Join(Texts.Get("sep_list"), c.Groups.Select(g => TypeLabel(g.Type)).Distinct()), said);
        }

        /// <summary>A mod's options as a menu, these of them: an option with sub-options offers
        /// each of them, one chosen; every option can be turned off. Each says what it would
        /// conflict with among the enabled mods.</summary>
        ContextMenu lastOptionMenu;     // for the measuring hook

        ContextMenu OptionMenu(ModRow m, List<int> which)
        {
            // narrow: each choice its name, and after it on the same line how many mods it conflicts
            // with; the mods themselves on pointing (the user, 2026-11-03: the names in a line made
            // it very wide; the count on a line of its own made it very long). No taller than most
            // of the screen: a mod with many choices scrolls (it ran off the screen)
            var menu = new ContextMenu { MaxWidth = 380, MaxHeight = SystemParameters.WorkArea.Height * 0.6 };
            MenuItem Item(string name, List<string> clashes, bool chosen, Action act)
            {
                var head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var count = new TextBlock
                {
                    Text = clashes.Count == 0 ? Texts.Get("cf_opt_free") : Texts.F("opt_clash_n", clashes.Count),
                    // on the chosen one, drawn on yellow: the dark ink
                    FontSize = 12, Foreground = B(chosen ? "TapeInk" : clashes.Count == 0 ? "Muted" : "Yellow"),
                    Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(count, 1);
                head.Children.Add(count);
                var it = new MenuItem { Header = head, IsCheckable = false, IsChecked = chosen };
                if (clashes.Count > 0) it.ToolTip = Texts.F("opt_clash", string.Join(Texts.Get("sep_list"), clashes));
                it.Click += (s, e) => { if (!chosen) act(); };
                return it;
            }
            foreach (var i in which)
            {
                var o = m.Options[i];
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                if (o.Subs.Count > 0)
                {
                    menu.Items.Add(new MenuItem { Header = o.Name, IsEnabled = false });
                    for (int k = 0; k < o.Subs.Count; k++)
                    {
                        int sk = k;
                        menu.Items.Add(Item(o.Subs[k].Name, o.Subs[k].Clashes, o.Enabled && o.Subs[k].Enabled, () =>
                            Change(m.Id, c => { c.Options[i].Enabled = true; for (int x = 0; x < c.Options[i].Subs.Count; x++) c.Options[i].Subs[x].Enabled = x == sk; },
                                   "set", m.Id, (i + 1) + "." + (sk + 1), "on", "--only")));
                    }
                }
                else menu.Items.Add(Item(o.Name, o.Clashes, o.Enabled, () => Change(m.Id, c => c.Options[i].Enabled = true, "set", m.Id, (i + 1).ToString(), "on")));
                if (o.Enabled)
                {
                    var off = new MenuItem { Header = Texts.F("cf_opt_off", o.Name) };
                    off.Click += (s, e) => Change(m.Id, c => c.Options[i].Enabled = false, "set", m.Id, (i + 1).ToString(), "off");
                    menu.Items.Add(off);
                }
            }
            return menu;
        }

        /// <summary>HD2QD_CLICK_TEST=conflictpick (measuring only): the conflicts view, then a click
        /// on the first mod not in effect, then on the switch of the first mod turned off. After
        /// each, for 3 s, every change on screen: the rows (name, tag, dashed), the scroll, and how
        /// many cards were drawn again; to HD2QD_WHEEL_OUT. Posted clicks do not reach WPF, so the
        /// clicks are raised here.</summary>
        void ConflictPickTest()
        {
            // conflictmenu: the conflicts view, and the menu of the first option shown in it opened
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "conflictmenu")
            {
                var m = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                m.Tick += (s, e) =>
                {
                    m.Stop();
                    filter = "conflict";
                    RenderNav();
                    RenderList();
                    UpdateLayout();
                    IEnumerable<Button> Links(DependencyObject d)
                    {
                        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
                        {
                            if (c is Button b && (b.Content is TextBlock || (b.Content is StackPanel sp && sp.Children.OfType<System.Windows.Shapes.Path>().Any()))) yield return b;
                            foreach (var r in Links(c)) yield return r;
                        }
                    }
                    var which = int.TryParse(Environment.GetEnvironmentVariable("HD2QD_MENU_AT"), out var n) ? n : 0;
                    var link = Links(List).Skip(which).FirstOrDefault();
                    link?.BringIntoView();
                    link?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    // the menu as text, to HD2QD_WHEEL_OUT
                    var said = new System.Text.StringBuilder();
                    string Words(object x) => x is TextBlock t ? t.Text : x is Panel p ? string.Join(" | ", p.Children.OfType<TextBlock>().Select(t => t.Text)) : x?.ToString();
                    said.AppendLine("open " + (lastOptionMenu?.IsOpen == true));
                    foreach (var it in lastOptionMenu?.Items.Cast<object>() ?? Enumerable.Empty<object>())
                        said.AppendLine(it is Separator ? "--" : it is MenuItem mi ? (mi.IsChecked ? "(*) " : "    ") + Words(mi.Header) + (mi.IsEnabled ? "" : "  [title]") : "?");
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path != null) System.IO.File.WriteAllText(path, said.ToString());
                    // the menu as a picture beside it, and its width (a pop-up is a window of its own)
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var menu = lastOptionMenu;
                        if (path == null || menu == null || menu.ActualWidth <= 0) return;
                        System.IO.File.AppendAllText(path, "width " + menu.ActualWidth + " height " + menu.ActualHeight + Environment.NewLine);
                        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth * 2), (int)Math.Ceiling(menu.ActualHeight * 2), 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
                        bmp.Render(menu);
                        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                        using (var f = System.IO.File.Create(path + ".png")) png.Save(f);
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                };
                m.Start();
                return;
            }
            // optionsbutton: for each view (groups, order) and list (all, conflicts), with a mod that
            // has options chosen, whether the details offer the options button and the mod's menu
            // the order's ends; to HD2QD_WHEEL_OUT
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") == "optionsbutton")
            {
                var m = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                m.Tick += (s, e) =>
                {
                    m.Stop();
                    var said = new System.Text.StringBuilder();
                    var mod = st.Mods.First(x => x.Options.Count > 0 && x.InConflict);
                    foreach (var view in new[] { true, false })
                        foreach (var list in new[] { "all", "conflict" })
                        {
                            groupView = view;
                            filter = list;
                            selectedId = mod.Id;
                            RenderNav();
                            RenderList();
                            RenderDetail();
                            bool button = Detail.Children.OfType<Button>().Any(b => (b.Content as string) == Texts.Get("pv_open"));
                            var menu = Menu(mod);
                            bool ends = menu.Items.OfType<MenuItem>().Any(i => (i.Header as string) == Texts.Get(Texts.TopWins ? "top_wins_top" : "top"));
                            said.AppendLine((view ? "groups" : "order") + " / " + list + ": options button " + button + ", top/bottom " + ends);
                        }
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path != null) System.IO.File.WriteAllText(path, said.ToString());
                };
                m.Start();
                return;
            }
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") != "conflictpick") return;
            var out_ = new System.Text.StringBuilder();
            IEnumerable<T> Find<T>(DependencyObject d, Func<T, bool> so) where T : DependencyObject
            {
                foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
                {
                    if (c is T x && so(x)) yield return x;
                    foreach (var r in Find(c, so)) yield return r;
                }
            }
            IEnumerable<Border> Rows() => Find<Border>(List, b => b.Tag is string);
            string Words(DependencyObject d) => string.Join(" ", LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()
                .Select(c => c is TextBlock t ? t.Text : c is CheckBox cb ? (cb.IsChecked == true ? "[on]" : "[off]") : Words(c)).Where(x => x.Length > 0));
            string Screen() => "details: " + (st?.Mods.FirstOrDefault(x => x.Id == selectedId)?.Name ?? "-") + ", scroll " + ListScroll.VerticalOffset + "\n" + string.Join("\n", Rows().Select(r =>
                "  " + Words(r) + (((Grid)r.Parent).Children.OfType<System.Windows.Shapes.Rectangle>().Any() ? "  [dashed]" : "")));
            void Watch(string what, Action click, Action then)
            {
                var cards = List.Children.Cast<UIElement>().ToList();
                var shown = Screen();
                out_.AppendLine("== before " + what + "\n" + shown);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                click();
                int redrawn = 0;
                var tick = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
                tick.Tick += (s, e) =>
                {
                    var now = Screen();
                    var nowCards = List.Children.Cast<UIElement>().ToList();
                    int fresh = nowCards.Count(c => !cards.Contains(c));
                    if (now != shown || fresh > 0)
                    {
                        redrawn += fresh;
                        out_.AppendLine("-- " + clock.ElapsedMilliseconds + " ms, cards drawn again " + fresh + "\n" + now);
                        shown = now;
                        cards = nowCards;
                    }
                    if (clock.ElapsedMilliseconds < 3000) return;
                    tick.Stop();
                    out_.AppendLine("cards drawn again in all: " + redrawn);
                    then();
                };
                tick.Start();
            }
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                filter = "conflict";
                RenderList();
                UpdateLayout();
                // a choice made while a state read is on its way, as the file watcher starts one
                var pick = Find<Button>(List, b => (b.Tag as string) == "pick").FirstOrDefault();
                Watch("Use this one while a state read is on its way", () =>
                {
                    Refresh();
                    pick?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }, () =>
                {
                var offRow = Rows().FirstOrDefault(r => Find<CheckBox>(r, c => c.IsChecked != true).Any());
                Watch("a click on the line of a mod turned off", () => offRow?.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                                                  { RoutedEvent = UIElement.MouseLeftButtonUpEvent }), () =>
                {
                    var sw = Find<CheckBox>(List, c => c.IsChecked != true).FirstOrDefault();
                    Watch("a click on a switch that is off", () =>
                    {
                        if (sw == null) return;
                        sw.IsChecked = true;
                        sw.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    }, () =>
                    {
                        // HD2QD_SELECT: that mod in the details at the end, for a picture of them
                        var pick = st.Mods.FirstOrDefault(x => x.Name == Environment.GetEnvironmentVariable("HD2QD_SELECT"));
                        if (pick != null) { Current(pick.Id); RenderList(); RenderDetail(); }
                        var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                        if (path != null) System.IO.File.WriteAllText(path, out_.ToString());
                    });
                });
                });
            };
            t.Start();
        }
    }
}
