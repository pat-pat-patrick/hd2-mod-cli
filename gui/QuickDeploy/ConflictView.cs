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
            // the view stays where it was: a choice redraws it, and the state read after it again
            double at = ListScroll.VerticalOffset;
            List.Children.Clear();
            Dispatcher.BeginInvoke(new Action(() => ListScroll.ScrollToVerticalOffset(at)), System.Windows.Threading.DispatcherPriority.Loaded);
            // in an order a choice does not change (the load order's names did)
            var sets = st.AllClashes.Where(c => !c.Same && !c.ManagedBoot && c.Mods.Count > 1)
                                 .OrderByDescending(c => c.Mods.Count).ThenByDescending(c => c.Count)
                                 .ThenBy(c => string.Join("\u001f", c.Mods.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)), StringComparer.CurrentCultureIgnoreCase).ToList();
            if (sets.Count == 0)
            {
                List.Children.Add(new TextBlock { Text = Texts.Get("cf_none"), Foreground = B("Muted"), Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap });
                return;
            }
            foreach (var c in sets) List.Children.Add(ConflictCard(c));
        }

        /// <summary>One set: how many resources and mods, the mods joined by a bracket (the one in
        /// effect marked; a click on another makes it win), and the first of the resources.</summary>
        UIElement ConflictCard(Clash c)
        {
            var card = new StackPanel();
            card.Children.Add(new TextBlock
            {
                Text = Texts.F("cf_set", c.Count, c.Mods.Count, c.Resources.Count > 0 ? TypeLabel(c.Resources[0].Type) : ""), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = B("Yellow"),
                Margin = new Thickness(0, 0, 0, 10),
            });
            var ids = c.Mods.Select(n => st.Mods.FirstOrDefault(x => x.Name == n)?.Id).Where(id => id != null).ToList();
            var winner = st.Mods.FirstOrDefault(x => x.Name == c.Winner)?.Id;
            var inGame = st.Mods.FirstOrDefault(x => x.Name == c.Deployed)?.Id;     // whose copy the game has now
            // the bracket runs from the middle of the first name's row to the middle of the last's:
            // a row is 42 high (a 28 picture, 6 above and below, its border) and 6 apart
            var joined = new Grid();
            joined.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            joined.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bracket = new Border
            {
                BorderBrush = B("Yellow"), BorderThickness = new Thickness(2, 2, 0, 2), Width = 10, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 21, 0, 27),
            };
            joined.Children.Add(bracket);
            var names = WinnerRows(ids, winner, "pick_now", inGame, true);
            Grid.SetColumn((UIElement)names, 1);
            joined.Children.Add(names);
            card.Children.Add(joined);
            // which resources: the type, then the name or the id
            const int lines = 5;
            var list = new StackPanel { Margin = new Thickness(18, 4, 0, 0) };
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
        /// on the first mod not in effect of the first set; each set's rows (name, its tag, dashed),
        /// before, at once and after the state read, go to HD2QD_WHEEL_OUT. Posted clicks do not
        /// reach WPF, so the click is raised here.</summary>
        void ConflictPickTest()
        {
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") != "conflictpick") return;
            var out_ = new System.Text.StringBuilder();
            IEnumerable<Border> Rows(DependencyObject d)
            {
                foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
                {
                    if (c is Border b && b.Tag is string) yield return b;
                    foreach (var r in Rows(c)) yield return r;
                }
            }
            string Words(DependencyObject d) => string.Join(" ", LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()
                .Select(c => c is TextBlock t ? t.Text : Words(c)).Where(s => s.Length > 0));
            void Dump(string when)
            {
                out_.AppendLine("== " + when + " (scroll " + ListScroll.VerticalOffset + ")");
                foreach (var r in Rows(List))
                    out_.AppendLine("  " + Words(r) + (((Grid)r.Parent).Children.OfType<System.Windows.Shapes.Rectangle>().Any() ? "  [dashed]" : ""));
            }
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                filter = "conflict";
                RenderList();
                UpdateLayout();
                Dump("before");
                var row = Rows(List).FirstOrDefault(r => r.Cursor == System.Windows.Input.Cursors.Hand);
                row?.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                UpdateLayout();
                Dump("at once, clicked " + row?.Tag);
                var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                later.Tick += (s1, e1) =>
                {
                    later.Stop();
                    UpdateLayout();
                    Dump("after the state read");
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path != null) System.IO.File.WriteAllText(path, out_.ToString());
                };
                later.Start();
            };
            t.Start();
        }
    }
}
