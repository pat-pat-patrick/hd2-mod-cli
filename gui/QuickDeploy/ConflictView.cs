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
                Add(m.Id); Add(m.Name); Add(m.Icon); Add(m.Enabled); Add(m.InProfile);
            }
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
            card.Children.Add(new TextBlock
            {
                Text = Texts.F("cf_set", c.Count, c.Mods.Count, c.Resources.Count > 0 ? TypeLabel(c.Resources[0].Type) : ""), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = B("Yellow"),
                Margin = new Thickness(0, 0, 0, 10),
            });
            var winner = st.Mods.FirstOrDefault(x => x.Name == c.Winner)?.Id;
            var inGame = st.Mods.FirstOrDefault(x => x.Name == c.Deployed)?.Id;     // whose copy the game has now
            // no bracket: each set is a card of its own (the user, 2026-11-03)
            card.Children.Add(WinnerRows(ids, winner, "pick_now", inGame, true));
            // which resources: the type, then the name or the id
            const int lines = 5;
            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var r in c.Resources.Take(lines))
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
            if (c.Count > Math.Min(lines, c.Resources.Count))
                list.Children.Add(Small(Texts.F("clash_more", c.Count - Math.Min(lines, c.Resources.Count)), new Thickness(0, 2, 0, 0)));
            if (list.Children.Count > 0) card.Children.Add(list);
            return new Border
            {
                BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(20, 16, 20, 16), Child = card,
            };
        }

        /// <summary>HD2QD_CLICK_TEST=conflictpick (measuring only): the conflicts view, then a click
        /// on the first mod not in effect, then on the switch of the first mod turned off. After
        /// each, for 3 s, every change on screen: the rows (name, tag, dashed), the scroll, and how
        /// many cards were drawn again; to HD2QD_WHEEL_OUT. Posted clicks do not reach WPF, so the
        /// clicks are raised here.</summary>
        void ConflictPickTest()
        {
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
            string Screen() => "scroll " + ListScroll.VerticalOffset + "\n" + string.Join("\n", Rows().Select(r =>
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
                var row = Rows().FirstOrDefault(r => r.Cursor == System.Windows.Input.Cursors.Hand);
                Watch("a click on " + row?.Tag, () => row?.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
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
            };
            t.Start();
        }
    }
}
