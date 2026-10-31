using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hd2Mod;

namespace QuickDeploy
{
    /// <summary>Space on a mod: a window of its own with its cover and its options at a size
    /// that shows their pictures (the user, 2026-10-27). In the groups the options are chosen
    /// here, not in the details (a long list of them kept the details scrolling); in the order
    /// it is mostly for the pictures. Space or Esc closes it; the arrow keys move on to the next
    /// mod, as Finder's Quick Look does.</summary>
    public partial class MainWindow
    {
        Window preview;
        ScrollViewer previewScroll;
        string previewOf;
        // the options' cards drawn large, with their pictures at full size (Card)
        bool bigCards;
        readonly Dictionary<string, ImageSource> fullImages = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        void SpacePreview(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && preview != null && !e.Handled) { preview.Close(); e.Handled = true; return; }
            if (e.Key != Key.Space || e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
            if (Body.Visibility != Visibility.Visible || gDragging || dragRow != null || st == null) return;
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox) return;
            if (st.Mods.All(m => m.Id != selectedId)) return;
            e.Handled = true;       // not the focused button's press
            if (preview != null) preview.Close(); else OpenPreview();
        }

        void OpenPreview()
        {
            preview = new Window
            {
                Owner = this, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Width = Math.Max(640, Math.Min(1100, ActualWidth - 160)), Height = Math.Max(480, ActualHeight - 160),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = B("Ground700"), BorderBrush = B("Grid"), BorderThickness = new Thickness(1),
                FontFamily = (FontFamily)FindResource("Ui"), Foreground = B("Ink"), Title = Texts.Get("window"),
            };
            preview.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Space || e.Key == Key.Escape) { e.Handled = true; preview.Close(); return; }
                if (e.Key != Key.Up && e.Key != Key.Down && e.Key != Key.Left && e.Key != Key.Right) return;
                e.Handled = true;
                if (groupView) GroupKeys(this, e);
                else if (e.Key == Key.Up || e.Key == Key.Down)
                {
                    var mods = Seen().ToList();
                    int i = mods.FindIndex(x => x.Id == selectedId);
                    int j = Math.Max(0, Math.Min(mods.Count - 1, i + (e.Key == Key.Down ? 1 : -1)));
                    if (mods.Count > 0 && j != i)
                    {
                        selectedId = mods[j].Id;
                        picked.Clear();
                        RenderList();
                        RenderDetail();
                        if (rows.TryGetValue(selectedId, out var r)) r.Row.BringIntoView();
                    }
                }
            };
            // a click anywhere outside it closes it, the manager's own window too, as a menu does;
            // with it open behind the window, Esc reached the window and left it open (the user, 2026-10-27)
            // (after the deactivation is done: a window closing deactivates too, and closing it again then fails)
            preview.Deactivated += (s, e) => Dispatcher.BeginInvoke(new Action(() => preview?.Close()));
            preview.Closed += (s, e) => { preview = null; previewOf = null; Activate(); };
            FillPreview();
            preview.Show();
        }

        /// <summary>The preview drawn again for the mod chosen now (a click in it, a state read, an
        /// arrow key); its scroll kept while it is the same mod.</summary>
        void FillPreview()
        {
            if (preview == null) return;
            var m = st?.Mods.FirstOrDefault(x => x.Id == selectedId);
            double keep = m != null && m.Id == previewOf && previewScroll != null ? previewScroll.VerticalOffset : 0;
            previewOf = m?.Id;
            var root = new DockPanel();
            var tape = new Border { Height = 3, Background = B("Yellow") };
            DockPanel.SetDock(tape, Dock.Top);
            root.Children.Add(tape);
            if (m == null)
            {
                root.Children.Add(new TextBlock { Text = Texts.Get("pv_none"), Foreground = B("Muted"), Margin = new Thickness(28) });
                preview.Content = root;
                return;
            }

            // the cover, the name and what the author says
            var head = new DockPanel { Margin = new Thickness(28, 24, 28, 16), Background = Brushes.Transparent };
            head.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) preview.DragMove(); };
            var cover = new Border { Width = 220, Height = 220, BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground900"),
                                     Child = Full(m.Icon) ?? Picture(m.Icon, GlyphMod, 0, m.Enabled ? "Blue" : "Muted", 64) };
            DockPanel.SetDock(cover, Dock.Left);
            head.Children.Add(cover);
            var close = new TextBlock { Text = Texts.Get("pv_close"), FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            var words = new StackPanel { Margin = new Thickness(24, 0, 16, 0) };
            words.Children.Add(new TextBlock { Text = m.Name, FontSize = 26, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
            var facts = new List<string>();
            if (m.ShownVersion != null) facts.Add(V(m.ShownVersion));
            if (m.SiteAuthor != null) facts.Add(Texts.F("by_author", m.SiteAuthor));
            if (facts.Count > 0) words.Children.Add(new TextBlock { Text = string.Join(" · ", facts), FontSize = 13, Foreground = B("Muted"), Margin = new Thickness(0, 4, 0, 0) });
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = m.Enabled, VerticalAlignment = VerticalAlignment.Center };
            sw.Click += (s, e) =>
            {
                bool on = sw.IsChecked == true;
                if (on && st.ProfilesOn && !m.InProfile) { Run("profile-add", m.Id); return; }
                Change(m.Id, c => c.Enabled = on, on ? "enable" : "disable", m.Id);
            };
            line.Children.Add(sw);
            line.Children.Add(new TextBlock { Text = Texts.Get(m.Enabled ? "pv_on" : "pv_off"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            words.Children.Add(line);
            if (!string.IsNullOrWhiteSpace(m.Description))
                words.Children.Add(new TextBlock { Text = m.Description.Trim(), FontSize = 14, Foreground = B("Ink"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) });
            head.Children.Add(words);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);
            var rule = new Border { Height = 1, Background = B("Grid") };
            DockPanel.SetDock(rule, Dock.Top);
            root.Children.Add(rule);

            // the options, their pictures large
            var body = new StackPanel { Margin = new Thickness(28, 16, 20, 24) };
            if (m.Options.Count == 0) body.Children.Add(new TextBlock { Text = Texts.Get("no_options"), Foreground = B("Muted") });
            else
            {
                body.Children.Add(new TextBlock { Text = Texts.F("opts_some", m.Chosen, m.Choices), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
                bigCards = true;
                try
                {
                    if (Options(m) is FrameworkElement opts) { opts.Margin = new Thickness(0); body.Children.Add(opts); }
                }
                finally { bigCards = false; }
            }
            previewScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = body };
            root.Children.Add(previewScroll);
            preview.Content = root;
            if (keep > 0) Dispatcher.BeginInvoke(new Action(() => previewScroll?.ScrollToVerticalOffset(keep)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>A picture at full size, for the preview (the list's are 256 px copies); null
        /// when there is none or it cannot be read.</summary>
        FrameworkElement Full(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (!fullImages.TryGetValue(file, out var img))
            {
                try { img = System.IO.File.Exists(file) ? Open(file, 1024) : null; }
                catch (Exception e) when (e is System.IO.IOException || e is NotSupportedException || e is UriFormatException || e is ArgumentException
                                          || e is UnauthorizedAccessException || e is InvalidOperationException) { img = null; }
                fullImages[file] = img;
            }
            return img == null ? null : new Image { Source = img, Stretch = Stretch.Uniform };
        }

        /// <summary>HD2QD_CLICK_TEST=preview (pictures only): the preview of the mod chosen at the
        /// start (--select), open.</summary>
        void PreviewTest()
        {
            var which = Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST");
            if (which == "menudump")
            {
                // "menudump": two mods of one group taken, their menu in both views as text to HD2QD_WHEEL_OUT
                var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                tm.Tick += (s0, e0) =>
                {
                    tm.Stop();
                    var two = st.Mods.Where(x => x.Group != null && x.Enabled).GroupBy(x => x.Group).First(g => g.Count() >= 2).Take(2).ToList();
                    var log = new System.Text.StringBuilder();
                    foreach (var view in new[] { true, false })
                    {
                        groupView = view;
                        picked.Clear();
                        foreach (var x in two) picked.Add(x.Id);
                        selectedId = two[0].Id;
                        log.AppendLine("== " + (view ? "groups" : "order") + ", two taken");
                        foreach (var it in Menu(two[0]).Items)
                            log.AppendLine("  " + (it is MenuItem mi ? mi.Header : "--"));
                        picked.Clear();
                        log.AppendLine("== " + (view ? "groups" : "order") + ", one");
                        foreach (var it in Menu(two[0]).Items)
                            log.AppendLine("  " + (it is MenuItem mi ? mi.Header : "--"));
                    }
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path != null) System.IO.File.WriteAllText(path, log.ToString());
                };
                tm.Start();
                return;
            }
            if (which == "deletemod")
            {
                // "deletemod" (pictures only): the chosen mod's Delete clicked, the first question
                // of this window (the user, 2026-11-01: it came up with only its buttons)
                var tx = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                tx.Tick += (s0, e0) =>
                {
                    tx.Stop();
                    var m = st.Mods.FirstOrDefault(x => x.Id == selectedId) ?? st.Mods.First();
                    DeleteFromDisk(m);
                };
                tx.Start();
                return;
            }
            if (which == "deletegroup")
            {
                // "deletegroup" (pictures only): a folder's menu opened and its Delete clicked, as a
                // hand does, the first time in this window
                var td = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                td.Tick += (s0, e0) =>
                {
                    td.Stop();
                    var g = st.Groups.First(x => x.Parent == null);
                    var folder = GroupCols.Children.OfType<Border>().SelectMany(c => ((StackPanel)((ScrollViewer)c.Child).Content).Children.OfType<Border>())
                        .First(b => b.Tag is GroupDrop d && d.Group == g.Id);
                    var menu = folder.ContextMenu;
                    menu.PlacementTarget = folder;
                    menu.IsOpen = true;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var del = menu.Items.OfType<MenuItem>().Last();
                        del.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, del));
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                };
                td.Start();
                return;
            }
            if (which != "preview" && which != "previewclose") return;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                OpenPreview();
                if (which != "previewclose") return;
                // "previewclose": the manager's window taken back, as a click on it does; then whether
                // the preview is still open goes to HD2QD_WHEEL_OUT
                var t2 = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                t2.Tick += (s2, e2) =>
                {
                    t2.Stop();
                    bool before = preview != null;
                    Activate();
                    var t3 = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    t3.Tick += (s3, e3) =>
                    {
                        t3.Stop();
                        var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                        if (path != null) System.IO.File.WriteAllText(path, "open before " + before + "\nopen after " + (preview != null) + "\n");
                    };
                    t3.Start();
                };
                t2.Start();
            };
            t.Start();
        }
    }
}
