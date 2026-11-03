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
            foreach (var c in sets)
            {
                var ids = c.Mods.Select(n => st.Mods.FirstOrDefault(x => x.Name == n)?.Id).Where(id => id != null).ToList();
                var key = string.Join("|", ids.OrderBy(x => x, StringComparer.Ordinal));
                var sig = CardSig(c, ids);
                if (!cfCards.TryGetValue(key, out var had) || had.Sig != sig) cfCards[key] = had = (sig, ConflictCard(c, ids));
                keep.Add(key);
                want.Add(had.Card);
            }
            foreach (var k in cfCards.Keys.Where(k => !keep.Contains(k)).ToList()) cfCards.Remove(k);
            if (sets.Count == 0)
            {
                if (!(List.Children.Count == 1 && List.Children[0] is TextBlock))
                {
                    List.Children.Clear();
                    List.Children.Add(new TextBlock { Text = Texts.Get("cf_none"), Foreground = B("Muted"), Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap });
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
            foreach (var r in c.Resources) { Add(r.Type); Add(r.Id); Add(r.Name); }
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
            Add(NowOf(ids, st.Mods.FirstOrDefault(x => x.Name == c.Winner)?.Id));
            Add(st.Mods.FirstOrDefault(x => x.Name == c.Deployed)?.Id);
            return sb.ToString();
        }

        /// <summary>One set: how many resources and mods, the mods with their switches (whose
        /// version is used marked; a click on another enabled one uses its), and the first of the
        /// resources.</summary>
        UIElement ConflictCard(Clash c, List<string> ids)
        {
            var card = new StackPanel();
            // a set with at most one mod on conflicts with nothing now: its heading grey, and why
            int on = ids.Count(id => st.Mods.First(x => x.Id == id).Enabled);
            var heading = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = B(on > 1 ? "Yellow" : "Muted"), Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
            heading.Inlines.Add(new System.Windows.Documents.Run(Texts.F("cf_set", c.Count, c.Mods.Count, c.Resources.Count > 0 ? TypeLabel(c.Resources[0].Type) : "")));
            if (on <= 1)
                heading.Inlines.Add(new System.Windows.Documents.Run("  ·  " + Texts.Get(on == 1 ? "cf_quiet_1" : "cf_quiet_0")) { FontWeight = FontWeights.Normal, FontSize = 13 });
            card.Children.Add(heading);
            // the option that brings it, under the mod's name: a click offers the others, each
            // with what it would conflict with (the user, 2026-11-03: switching mods on and off,
            // or choosing whose version wins, does not solve a conflict an option brings)
            UIElement FromLine(ModRow m)
            {
                var mine = c.From.Where(f => f.Mod == m.Name && f.Option >= 0 && f.Option < m.Options.Count).ToList();
                if (mine.Count == 0) return null;
                string Label((string Mod, int Option, int Sub) f) =>
                    f.Sub >= 0 && f.Sub < m.Options[f.Option].Subs.Count ? m.Options[f.Option].Name + " › " + m.Options[f.Option].Subs[f.Sub].Name : m.Options[f.Option].Name;
                var link = new Button { Template = Plain(), Cursor = System.Windows.Input.Cursors.Hand, FocusVisualStyle = null, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
                var words = new TextBlock { FontSize = 12, Foreground = B("Muted"), TextTrimming = TextTrimming.CharacterEllipsis };
                words.Inlines.Add(new System.Windows.Documents.Run(Texts.F("cf_from", string.Join(Texts.Get("sep_list"), mine.Select(Label).Distinct()))));
                words.Inlines.Add(new System.Windows.Documents.Run("  ▾"));
                link.Content = words;
                link.MouseEnter += (s, e) => words.Foreground = B("Ink");
                link.MouseLeave += (s, e) => words.Foreground = B("Muted");
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
            var winner = st.Mods.FirstOrDefault(x => x.Name == c.Winner)?.Id;
            var inGame = st.Mods.FirstOrDefault(x => x.Name == c.Deployed)?.Id;     // whose copy the game has now
            // no bracket: each set is a card of its own (the user, 2026-11-03)
            card.Children.Add(WinnerRows(ids, winner, "pick_now", inGame, true, FromLine));
            // which resources, by name when HD2SDK's list knows them; an id alone tells a player
            // nothing and is left to the mod's details (the user, 2026-11-03: the view was cluttered)
            const int lines = 5;
            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var r in c.Resources.Where(x => x.Name != null).Take(lines))
            {
                var dp = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var type = new TextBlock { Text = TypeLabel(r.Type) + "  ·  ", FontSize = 12, Foreground = B("Muted") };
                DockPanel.SetDock(type, Dock.Left);
                dp.Children.Add(type);
                var what = r.Name != null ? Selectable.Text(r.Name, 12, "Muted") : Selectable.Text(r.Id, 12, "Muted", Mono);
                what.VerticalAlignment = VerticalAlignment.Center;
                dp.Children.Add(what);
                list.Children.Add(dp);
            }
            if (list.Children.Count > 0 && c.Count > list.Children.Count)
                list.Children.Add(Small(Texts.F("clash_more", c.Count - list.Children.Count), new Thickness(0, 2, 0, 0)));
            if (list.Children.Count > 0) card.Children.Add(list);
            return new Border
            {
                BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(20, 16, 20, 16), Child = card,
            };
        }

        /// <summary>A mod's options as a menu, these of them: an option with sub-options offers
        /// each of them, one chosen; every option can be turned off. Each says what it would
        /// conflict with among the enabled mods.</summary>
        ContextMenu lastOptionMenu;     // for the measuring hook

        ContextMenu OptionMenu(ModRow m, List<int> which)
        {
            var menu = new ContextMenu();
            MenuItem Item(string name, List<string> clashes, bool chosen, Action act)
            {
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock { Text = name });
                head.Children.Add(new TextBlock
                {
                    Text = "   " + (clashes.Count == 0 ? Texts.Get("cf_opt_free") : Texts.F("opt_clash", string.Join(Texts.Get("sep_list"), clashes))),
                    Foreground = B(clashes.Count == 0 ? "Muted" : "Yellow"),
                });
                var it = new MenuItem { Header = head, IsCheckable = false, IsChecked = chosen };
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
                            if (c is Button b && b.Content is TextBlock) yield return b;
                            foreach (var r in Links(c)) yield return r;
                        }
                    }
                    var which = int.TryParse(Environment.GetEnvironmentVariable("HD2QD_MENU_AT"), out var n) ? n : 0;
                    var link = Links(List).Skip(which).FirstOrDefault();
                    link?.BringIntoView();
                    link?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    // the menu as text, to HD2QD_WHEEL_OUT
                    var said = new System.Text.StringBuilder();
                    string Words(object x) => x is TextBlock t ? t.Text : x is Panel p ? string.Join("", p.Children.OfType<TextBlock>().Select(t => t.Text)) : x?.ToString();
                    said.AppendLine("open " + (lastOptionMenu?.IsOpen == true));
                    foreach (var it in lastOptionMenu?.Items.Cast<object>() ?? Enumerable.Empty<object>())
                        said.AppendLine(it is Separator ? "--" : it is MenuItem mi ? (mi.IsChecked ? "(*) " : "    ") + Words(mi.Header) + (mi.IsEnabled ? "" : "  [title]") : "?");
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
