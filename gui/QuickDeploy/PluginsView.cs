using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Hd2Mod;

namespace QuickDeploy
{
    // The plugins page (the jigsaw piece beside the gear): the plugins added (core\Plugins.cs),
    // adding and removing them, and what the window hands them. The manager knows no plugin by
    // name: each draws its own card (a panel) or opens its own window (the user, 2026-11-02).
    public partial class MainWindow
    {
        /// <summary>What the window hands a plugin: the core's host (PluginHost), and what only
        /// the window has: tab(title, content, closed), a tab of this window ({show, close});
        /// window(title, content), the manager's own window frame; ask(text,
        /// action), ask_text(text, action, start) (a line typed in, or null) and tell(text), its
        /// dialogs; foot(text, colour), its status line; refresh(), the library and this page
        /// read again.</summary>
        Dictionary<string, object> PluginHostHere()
        {
            var host = PluginHost.Make();
            host["window"] = (Func<string, FrameworkElement, Window>)((title, content) => new ChromeWindow(this, title, content));
            // a tab of this window in place of a window (PluginTabs.cs); a plugin that does not
            // know it still asks for "window"
            host["tab"] = TabMaker();
            host["ask"] = (Func<string, string, bool>)((text, action) => Dialog.Ask(this, text, action));
            host["ask_text"] = (Func<string, string, string, string>)((text, action, start) => Dialog.AskText(this, text, action, start));
            host["tell"] = (Action<string>)(text => Dialog.Tell(this, text));
            host["foot"] = (Action<string, string>)((text, colour) => Foot(text, colour ?? "Blue"));
            host["refresh"] = (Action)(() => { Refresh(); if (PluginsPage.Visibility == Visibility.Visible) RenderPlugins(); });
            return host;
        }

        /// <summary>The plugins page: Add plugin… beside the title, and a card for each plugin
        /// added, headed by its title, file, version and Remove. None ships with the program
        /// (the user, 2026-11-02): they are added here, or dropped on the page. Two kinds: a
        /// panel, whose plugin draws the card's body (Panel), and a window of its own, opened from
        /// its card (Open). On the left, the guide to the cards (RenderGuide). The listed plugins,
        /// added or not, are the Browse tab's (PluginStore.cs).</summary>
        void RenderPlugins()
        {
            guideItems.Clear();
            guideAsked = null;
            // first drawn: Browse when nothing is added, so the page shows what can be (PluginStore.cs)
            if (pluginTab == null) pluginTab = PluginFiles.All().Count > 0 ? "added" : "browse";
            if (pluginTab == "browse") { RenderBrowse(); return; }
            RenderPluginCards();
            RenderGuide();
        }

        /// <summary>No plugin added: a dashed area that says so and that .dll files dropped on the
        /// page are installed (the whole page takes the drop, PluginsPage_Drop).</summary>
        FrameworkElement EmptyDropArea()
        {
            var grid = new Grid { MinHeight = 120 };
            grid.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Stroke = B("Muted"), StrokeThickness = 1.5, StrokeDashArray = new System.Windows.Media.DoubleCollection { 6, 4 },
                RadiusX = 4, RadiusY = 4, SnapsToDevicePixels = true,
            });
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 20, 24, 20) };
            words.Children.Add(new TextBlock { Text = Texts.Get("plugins_none"), FontSize = 13, Foreground = B("Muted"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            words.Children.Add(new TextBlock { Text = Texts.Get("plugin_drop_tip"), FontSize = 16, FontWeight = FontWeights.Bold, Foreground = B("Ink"), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            grid.Children.Add(words);
            return grid;
        }

        void RenderPluginCards()
        {
            PluginsBox.Children.Clear();
            // by the name shown, in the window's language (the user, 2026-11-03)
            var all = ByName(PluginFiles.All());
            PluginsBox.Children.Add(PluginsHead(all.Count, PluginFiles.Listed().Count));
            PluginsBox.Children.Add(Texts.Hints ? Small(Texts.Get("plugins_intro"), new Thickness(0, 4, 0, 20)) : new Border { Height = 16 });
            if (all.Count == 0)
            {
                PluginsBox.Children.Add(EmptyDropArea());
                var go = new Button { Content = Texts.Get("plugins_browse_go"), Style = (Style)FindResource("Primary"), MinHeight = 36, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                go.Click += (s, e) => ShowPluginTab("browse");
                PluginsBox.Children.Add(go);
            }
            var host = all.Count > 0 ? PluginHostHere() : null;
            bool first = true;
            foreach (var (file, type, why) in all)
            {
                var card = new StackPanel();
                var head = new DockPanel();
                var update = type != null ? PluginFiles.UpdateOf(file, type) : null;
                var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
                right.Children.Add(new TextBlock
                {
                    Text = System.IO.Path.GetFileNameWithoutExtension(file) + (type != null ? " " + PluginFiles.Version(type) : ""),
                    FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0),
                });
                var remove = new Button { Content = Texts.Get("plugin_remove"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(12, 0, 0, 0) };
                remove.Click += (s, e) => RemovePlugin(file, type != null ? PluginFiles.Title(type, Texts.Lang) ?? file : file);
                right.Children.Add(remove);
                DockPanel.SetDock(right, Dock.Right);
                head.Children.Add(right);
                var title = Heading(type != null ? PluginFiles.Title(type, Texts.Lang) ?? file : file);
                title.VerticalAlignment = VerticalAlignment.Center;          // level with the version and Remove
                head.Children.Add(title);
                card.Children.Add(head);
                // a newer official release: said under the title, with its button (the user, 2026-11-03)
                if (update != null)
                {
                    var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                    var up = new Button
                    {
                        Content = Texts.Get("plugin_update_go"), Style = (Style)FindResource("Primary"),
                        MinHeight = 32, Padding = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
                    };
                    var pl = update.Value.Plugin;
                    up.Click += (s, e) => FetchPlugin(pl, (Button)s);
                    line.Children.Add(up);
                    line.Children.Add(new TextBlock
                    {
                        Text = Texts.F("plugin_update_out", update.Value.Newest, update.Value.Added), Foreground = B("Yellow"), FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                    });
                    card.Children.Add(line);
                }
                if (type == null)
                    card.Children.Add(Small(Texts.Get(why == "api" ? "plugin_api" : "plugin_broken"), new Thickness(0, 8, 0, 0)));
                else if (PluginFiles.Kind(type) == "panel")
                {
                    try { if (PluginFiles.Call(type, "Panel", this, host) is UIElement body) card.Children.Add(body); }
                    catch (Exception e) when (!(e is OutOfMemoryException))
                    {
                        ManagerLog.Warn("plugin " + file + ": Panel: " + e.Message);
                        card.Children.Add(Small(Texts.F("plugin_failed", e.Message), new Thickness(0, 8, 0, 0)));
                    }
                }
                else
                {
                    var summary = PluginFiles.Summary(type, Texts.Lang);
                    if (summary != null) card.Children.Add(Small(summary, new Thickness(0, 6, 0, 0)));
                    if (PluginFiles.Kind(type) == "window")
                    {
                        var row = new StackPanel { Orientation = Orientation.Horizontal };
                        row.Children.Add(OpenButton(type));
                        // an official plugin the official list gives a button has one in the title
                        // bar, which can be taken away
                        if (PluginFiles.HasButton(file, type))
                        {
                            var pin = new CheckBox
                            {
                                Style = (Style)FindResource("Switch"), IsChecked = PluginFiles.Pinned(file),
                                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 12, 0, 0),
                            };
                            var f = file;
                            pin.Click += (s, e) => { PluginFiles.Pin(f, pin.IsChecked == true); PinnedPlugins(); };
                            row.Children.Add(pin);
                            row.Children.Add(new TextBlock
                            {
                                Text = Texts.Get("plugin_pin"), Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 12, 0, 0),
                            });
                        }
                        card.Children.Add(row);
                        // a key that brings it up over the game, when it has one (PluginKeys)
                        if (PluginKeys.Declared(type) != null) card.Children.Add(KeyRow(file, type));
                    }
                }
                if (!first) PluginsBox.Children.Add(new Border { Height = 16 });
                var box = Card(card);
                PluginsBox.Children.Add(box);
                first = false;
                // in the guide: what needs the player first, else that it is not an official plugin
                string badge = null; bool hot = false;
                if (type == null) { badge = "plugin_guide_unusable"; hot = true; }
                else if (update != null) { badge = "plugin_guide_update"; hot = true; }
                else if (ThirdParty(file)) badge = Community(file) ? "plugin_guide_community" : "plugin_guide_third";
                guideItems.Add(new GuideItem
                {
                    Key = file, Title = type != null ? PluginFiles.Title(type, Texts.Lang) ?? file : file, Target = box,
                    Badge = badge != null ? Texts.Get(badge) : null, Hot = hot,
                });
            }
            // what can be added is the Browse tab's (PluginStore.cs), not said here again (the user,
            // 2026-11-28: "这不重复了吗")
        }

        // ---------------------------------------------------------------- the guide

        /// <summary>A line of the guide: an added plugin (Key, its file), and what a click goes to
        /// on the page.</summary>
        class GuideItem
        {
            public string Key, Title, Badge;
            public bool Hot;
            public FrameworkElement Target;
            public Border Row;
            public TextBlock Name;
        }

        /// <summary>Where a plugin's author reads how to list it in the community list: the
        /// README's section in the window's language (GitHub's anchor of its heading).</summary>
        static string CommunityHowIn(string lang) =>
            "https://github.com/pat-pat-patrick/hd2qd-plugins#" + (lang != null && lang.StartsWith("zh") ? Uri.EscapeDataString("社区插件") : "community-plugins");
        readonly List<GuideItem> guideItems = new List<GuideItem>();
        // the line lit (the card at the top of the view), the one clicked (lit while its card is in
        // view, though the end of the page cannot come up to the top), and a scroll on its way
        string guideCur, guideAsked;
        bool guideMoving;
        EventHandler guideStep;

        /// <summary>Not an official plugin: the official list has been read and has no release
        /// with this file's SHA-256 (PluginFiles.OfficialOf). Until the list is read, nothing is said.</summary>
        static bool Community(string file)
        {
            try { return PluginFiles.CommunityOf(file) != null; }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { return false; }
        }

        static bool ThirdParty(string file)
        {
            if (PluginFiles.OfficialJson() == null) return false;
            try { return PluginFiles.OfficialOf(file) == null; }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { return false; }
        }

        /// <summary>The guide on the plugins page's left (the user, 2026-11-10: which plugins are
        /// added, a click going to one's card), drawn as the deploy page's library panel is: a
        /// label, then a line for each card in the page's order, each named by the plugin's own
        /// Title in the window's language, any plugin file in the folder whoever made it. A badge
        /// says what needs the player (unusable, an update), else that it is not an official
        /// plugin. What can be added is the Browse tab's.</summary>
        void RenderGuide()
        {
            PluginGuide.Children.Clear();
            // shown also when empty: the page's column stays where it is (the user, 2026-11-28)
            PluginGuideBar.Visibility = Visibility.Visible;
            bool added = false;
            foreach (var it in guideItems)
            {
                if (!added)
                {
                    PluginGuide.Children.Add(Label("plugins_guide", new Thickness(8, 0, 0, 4)));
                    added = true;
                }
                var btn = new Button { Cursor = System.Windows.Input.Cursors.Hand, FocusVisualStyle = null, Margin = new Thickness(0, 2, 0, 2), Template = Plain() };
                var row = new Border { MinHeight = 44, Padding = new Thickness(12, 0, 12, 0), BorderThickness = new Thickness(2, 0, 0, 0) };
                var dp = new DockPanel();
                if (it.Badge != null)
                {
                    var badge = new Border
                    {
                        Background = it.Hot ? B("Yellow") : B("Grid"), MinWidth = 22,
                        // words sit high in a box their own height: one more above (measured, 1.5 px at 200 %)
                        Padding = new Thickness(6, 1, 6, 0),
                        Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = it.Badge, FontSize = 11, Foreground = it.Hot ? B("TapeInk") : B("Muted"),
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    };
                    DockPanel.SetDock(badge, Dock.Right);
                    dp.Children.Add(badge);
                }
                it.Name = new TextBlock { Text = it.Title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                dp.Children.Add(it.Name);
                row.Child = dp;
                btn.Content = row;
                // the whole name and the file: for a name cut short, and which file a third-party one is
                btn.ToolTip = it.Title + "\n" + it.Key;
                System.Windows.Automation.AutomationProperties.SetName(btn, it.Title);
                System.Windows.Automation.AutomationProperties.SetAutomationId(btn, "guide:" + it.Key);
                it.Row = row;
                var key = it.Key;
                btn.Click += (s, e) => GuideGo(key);
                PluginGuide.Children.Add(btn);
            }
            guideCur = guideItems.FirstOrDefault()?.Key;
            PaintGuide();
            Dispatcher.BeginInvoke(new Action(GuideFollow), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        void PaintGuide()
        {
            foreach (var it in guideItems)
            {
                bool cur = it.Key == guideCur;
                it.Row.BorderBrush = cur ? B("Yellow") : System.Windows.Media.Brushes.Transparent;
                it.Row.Background = cur ? B("Ground700") : System.Windows.Media.Brushes.Transparent;
                it.Name.Foreground = cur ? B("Yellow") : B("Ink");
            }
        }

        /// <summary>Where a line's card is, from the top of the page's view.</summary>
        double GuideTop(GuideItem it) => it.Target.TransformToAncestor(PluginsPage).Transform(new Point(0, 0)).Y;

        /// <summary>A line clicked: the page scrolled until its card is at the top, where the page's
        /// title is (as far as the page goes), and the card's frame lit for a moment.</summary>
        void GuideGo(string key)
        {
            var it = guideItems.FirstOrDefault(x => x.Key == key);
            if (it == null || !it.Target.IsVisible) return;
            guideAsked = guideCur = key;
            PaintGuide();
            PluginsPage.UpdateLayout();
            double to = Math.Max(0, Math.Min(PluginsPage.ScrollableHeight, PluginsPage.VerticalOffset + GuideTop(it) - PluginsBox.Margin.Top));
            if (guideStep != null) { System.Windows.Media.CompositionTarget.Rendering -= guideStep; guideStep = null; }
            double from = PluginsPage.VerticalOffset;
            Action arrived = () => { guideMoving = false; Flash(it.Target as Border); };
            if (!Motion.On || Math.Abs(to - from) < 1) { PluginsPage.ScrollToVerticalOffset(to); arrived(); return; }
            // over a moment, as the group columns move (Motion.ScrollX)
            guideMoving = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            const double ms = 260;
            guideStep = (s, e) =>
            {
                double k = Math.Min(1, clock.ElapsedMilliseconds / ms);
                PluginsPage.ScrollToVerticalOffset(from + (to - from) * (1 - Math.Pow(1 - k, 3)));
                if (k < 1) return;
                System.Windows.Media.CompositionTarget.Rendering -= guideStep;
                guideStep = null;
                arrived();
            };
            System.Windows.Media.CompositionTarget.Rendering += guideStep;
        }

        /// <summary>A card's frame in the accent, fading back to the grid's colour: which card the
        /// click went to, also one the end of the page left lower down. Not with animations off.</summary>
        void Flash(Border card)
        {
            if (card == null || !Motion.On) return;
            var lit = ((System.Windows.Media.SolidColorBrush)B("Yellow")).Color;
            var rest = ((System.Windows.Media.SolidColorBrush)B("Grid")).Color;
            var brush = new System.Windows.Media.SolidColorBrush(lit);
            card.BorderBrush = brush;
            var fade = new System.Windows.Media.Animation.ColorAnimation(lit, rest, TimeSpan.FromMilliseconds(900))
            {
                BeginTime = TimeSpan.FromMilliseconds(200),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn },
            };
            fade.Completed += (s, e) => { if (card.BorderBrush == brush) card.BorderBrush = B("Grid"); };
            brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, fade);
        }

        void PluginsPage_ScrollChanged(object sender, ScrollChangedEventArgs e) => GuideFollow();

        /// <summary>The line lit follows the page: the last card whose top has come up to the top of
        /// the view, else the first; the one clicked while its card is in view.</summary>
        void GuideFollow()
        {
            if (guideMoving || guideItems.Count == 0 || !PluginsPage.IsVisible) return;
            string cur = null, asked = null;
            foreach (var it in guideItems)
            {
                if (!it.Target.IsVisible) continue;
                double y = GuideTop(it);
                if (cur == null || y <= PluginsBox.Margin.Top * 2) cur = it.Key;
                if (it.Key == guideAsked && y >= -1 && y < PluginsPage.ViewportHeight) asked = it.Key;
            }
            guideAsked = asked;
            cur = asked ?? cur;
            if (cur == null || cur == guideCur) return;
            guideCur = cur;
            PaintGuide();
            guideItems.First(x => x.Key == cur).Row.BringIntoView();
        }

        /// <summary>An official plugin's newest release downloaded in the background (checked
        /// against the list's SHA-256), then added as a chosen file is: the player agrees first.</summary>
        async void FetchPlugin(OfficialPlugin p, Button button)
        {
            button.IsEnabled = false;
            Foot(Texts.F("plugin_fetching", p.TitleIn(Texts.Lang)), "Blue");
            string path;
            try { path = await Task.Run(() => PluginFiles.Fetch(p)); }
            catch (Exception e) when (e is ModError || e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                button.IsEnabled = true;
                Foot(Texts.F("failed", e.Message), "Red");
                return;
            }
            try { AddPlugins(new[] { path }); }
            finally
            {
                try { System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(path), true); }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { }
                button.IsEnabled = true;
                FootState();
            }
        }

        /// <summary>The title bar's plugin buttons, left of the plugins page's: each official plugin
        /// the official list gives one (PluginFiles.HasButton) and the player has not taken away,
        /// drawn with the plugin's own icon (its app.ico), its title as the tip.</summary>
        /// <summary>Added plugins by the name shown in the window's language, else their file's.</summary>
        static List<(string File, Type Type, string Why)> ByName(List<(string File, Type Type, string Why)> all) =>
            all.OrderBy(x => (x.Type != null ? PluginFiles.Title(x.Type, Texts.Lang) : null) ?? x.File, NameOrder).ToList();

        /// <summary>Names compared as the window's language orders them (zh-CN by pinyin), not the system's.</summary>
        static StringComparer NameOrder
        {
            get
            {
                try { return StringComparer.Create(new System.Globalization.CultureInfo(Texts.Lang), true); }
                catch (System.Globalization.CultureNotFoundException) { return StringComparer.CurrentCultureIgnoreCase; }
            }
        }

        void PinnedPlugins()
        {
            // a dot on the plugins button while an added plugin has a newer release
            PluginDot.Visibility = PluginFiles.NewerReleases().Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PinnedBox.Children.Clear();
            var game = Store.GameDir(null, false);
            foreach (var (type, title, icon) in Pinned())
            {
                var b = new Button
                {
                    Width = 38, Height = 38, Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                    Style = (Style)FindResource("Ghost"), VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = game == null ? title + "\n" + Texts.Get("plugin_no_game") : title, IsEnabled = game != null,
                    Content = icon,
                };
                ToolTipService.SetShowOnDisabled(b, true);
                System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(b, true);
                var t = type;
                b.Click += (s, e) => OpenPlugin(t);
                PinnedBox.Children.Add(b);
            }
        }

        /// <summary>The plugins with a button that are pinned to the title bar, by name: their type,
        /// title in the window's language and icon. The title bar and the notification-area icon's
        /// menu both show these (the user, 2026-11-19).</summary>
        IEnumerable<(Type Type, string Title, UIElement Icon)> Pinned()
        {
            foreach (var (file, type, _) in ByName(PluginFiles.All()))
            {
                if (type == null || !PluginFiles.HasButton(file, type) || !PluginFiles.Pinned(file)) continue;
                var icon = PluginIcon(type);
                if (icon == null) continue;
                yield return (type, PluginFiles.Title(type, Texts.Lang) ?? file, icon);
            }
        }

        /// <summary>A pinned plugin's Open, with the host.</summary>
        void OpenPlugin(Type type)
        {
            try { PluginFiles.Call(type, "Open", this, PluginHostHere()); }
            catch (Exception x) when (!(x is OutOfMemoryException))
            {
                if (inTray) BringBack();              // the word needs a window to stand on
                Dialog.Tell(this, Texts.F("failed", x.Message));
            }
        }

        /// <summary>A plugin's own icon, 20 x 20 like the header's glyphs: the frame of its app.ico
        /// that is the smallest at least as large as 20 at this screen's scale, scaled smoothly.</summary>
        UIElement PluginIcon(Type type)
        {
            var frame = PluginIconFrame(type);
            if (frame == null) return null;
            var image = new Image { Source = frame, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, System.Windows.Media.BitmapScalingMode.HighQuality);
            return image;
        }

        /// <summary>The frame of a plugin's icon PluginIcon draws; null when it has none.</summary>
        System.Windows.Media.ImageSource PluginIconFrame(Type type)
        {
            var bytes = PluginFiles.IconBytes(type, PluginHostHere());
            if (bytes == null) return null;
            try
            {
                var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
                var frames = new System.Windows.Media.Imaging.IconBitmapDecoder(new System.IO.MemoryStream(bytes),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames;
                var want = 20 * scale;
                return frames.Where(f => f.PixelWidth >= want).OrderBy(f => f.PixelWidth).FirstOrDefault()
                       ?? frames.OrderByDescending(f => f.PixelWidth).First();
            }
            catch (Exception e) when (e is System.IO.IOException || e is NotSupportedException || e is System.IO.FileFormatException || e is ArgumentException)
            {
                ManagerLog.Warn("plugin icon: " + e.Message);
                return null;
            }
        }

        /// <summary>A window plugin's Open, with the host; the game's data folder is needed.</summary>
        UIElement OpenButton(Type type)
        {
            var game = Store.GameDir(null, false);
            var open = new Button
            {
                Content = Texts.Get("plugin_open"), Style = (Style)FindResource("Primary"), MinHeight = 36, MinWidth = 96,
                Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = game != null,
                ToolTip = game == null ? Texts.Get("plugin_no_game") : null,
            };
            ToolTipService.SetShowOnDisabled(open, true);
            open.Click += (s, e) =>
            {
                try { PluginFiles.Call(type, "Open", this, PluginHostHere()); }
                catch (Exception x) when (!(x is OutOfMemoryException)) { Dialog.Tell(this, Texts.F("failed", x.Message)); }
            };
            return open;
        }

        /// <summary>Why the official list could not be read the last time (PluginFiles.RefreshList), else null.</summary>
        string listWhy, listLang;
        DateTime listAsked;

        /// <summary>The official list read again from GitHub in the background: at the window's
        /// start, and when the Plugins page opens if it was not read in the last five minutes.
        /// The rows (a plugin a mod needs) and the page follow what changed.</summary>
        async void RefreshListSoon(bool now)
        {
            // another language since: its translations are read now
            if (!now && DateTime.UtcNow - listAsked < TimeSpan.FromMinutes(5) && listLang == Texts.Lang) return;
            listAsked = DateTime.UtcNow;
            var lang = listLang = Texts.Lang;
            (bool Changed, string Why) r;
            try { r = await Task.Run(() => PluginFiles.RefreshList(lang)); }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { r = (false, "error"); }
            bool whyChanged = r.Why != listWhy;
            listWhy = r.Why;
            if (r.Changed) { Refresh(); PinnedPlugins(); }      // which plugins are official, and get a button, comes from the list
            if ((r.Changed || whyChanged) && PluginsPage.Visibility == Visibility.Visible) RenderPlugins();
        }

        /// <summary>At the window's start, in the background: each plugin's Start (a license
        /// renewed, a cache read); the state read again when one changed something.</summary>
        async void PluginsAtStart()
        {
            PinnedPlugins();
            ArmPluginKeys();
            RefreshListSoon(true);
            var host = PluginHostHere();
            bool changed;
            try { changed = await Task.Run(() => PluginFiles.Start(host)); }
            catch (Exception e) when (e is ModError || e is System.IO.IOException || e is UnauthorizedAccessException) { return; }
            if (changed) Refresh();
        }

        /// <summary>What the plugins say on a mod's row: (text, tip, colour), in the window's language.</summary>
        static IEnumerable<(string Text, string Tip, string Colour)> NotesOf(ModRow m) =>
            m.Notes.Where(n => n.Key != null || n.Text != null).Select(n => n.Key != null
                ? (Texts.F("note_" + n.Key, (n.Args ?? new List<object>()).ToArray()), Texts.F("note_" + n.Key + "_tip", (n.Args ?? new List<object>()).ToArray()), n.Colour ?? "Yellow")
                : (n.Text, n.Tip, n.Colour ?? "Yellow"));

        /// <summary>The file picker for plugins; only: the one file an official plugin is. Every
        /// .dll shows: an official release is known by its SHA-256 under any name (the user,
        /// 2026-11-10: files passed around are renamed), and Inspect refuses the rest.</summary>
        void PickPlugin(string only = null)
        {
            var pick = new Microsoft.Win32.OpenFileDialog
            {
                Title = only != null ? Texts.F("plugin_add_one_title", only) : Texts.Get("plugin_add_title"),
                Filter = Texts.Get("plugin_filter") + "|*.dll", CheckFileExists = true, Multiselect = only == null,
            };
            if (pick.ShowDialog(this) == true) AddPlugins(pick.FileNames);
        }

        /// <summary>Plugin files chosen or dropped: each one read without running it, then added
        /// once the player agrees (a plugin runs as this program does), over one of the same name.</summary>
        void AddPlugins(IEnumerable<string> paths)
        {
            var added = new List<string>();
            var updated = new List<string>();
            foreach (var path in paths)
            {
                string file, version, why;
                // a file gone or locked since it was chosen or dropped is said, not thrown: an
                // exception the window does not catch closes the manager
                try { (file, version, why) = PluginFiles.Inspect(path); }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
                { Dialog.Tell(this, Texts.F("failed", e.Message)); continue; }
                if (why != null)
                {
                    Dialog.Tell(this, Texts.F(why == "name" ? "plugin_add_name" : why == "api" ? "plugin_add_api" : "plugin_add_broken", file));
                    continue;
                }
                var old = PluginFiles.All().FirstOrDefault(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase));
                bool replace = old.File != null;
                // an official release (its SHA-256 in the official list) goes in without the
                // trusted-source question: it is the file the list names, and asking at every
                // update of one was needless (the user, 2026-11-14)
                var listed = PluginFiles.CommunityRelease(path);
                var ask = listed != null
                    ? Texts.F(replace ? "plugin_replace_ask_community" : "plugin_add_ask_community", listed.TitleIn(Texts.Lang), version, listed.Author ?? "?",
                              "https://github.com/" + listed.Repository, old.Type != null ? PluginFiles.Version(old.Type) : "?")
                    : replace ? Texts.F("plugin_replace_ask", file, version, old.Type != null ? PluginFiles.Version(old.Type) : "?")
                              : Texts.F("plugin_add_ask", file, version);
                try
                {
                    if (!PluginFiles.IsOfficialRelease(path) && !Dialog.Ask(this, ask, Texts.Get(replace ? "plugin_replace_go" : "plugin_add_go"))) continue;
                    PluginFiles.Install(path);
                    (replace ? updated : added).Add(file + " " + version);
                }
                catch (Exception e) when (e is ModError || e is System.IO.IOException || e is UnauthorizedAccessException)
                { Dialog.Tell(this, Texts.F("failed", e.Message)); }
            }
            if (added.Count + updated.Count == 0) return;
            // a plugin replaced keeps running in this window as it was loaded (its start, its open
            // tabs): the new version is used once the program is started again
            var said = new List<string>();
            if (added.Count > 0) said.Add(Texts.F("plugin_added", string.Join(Texts.Sep, added)));
            if (updated.Count > 0) said.Add(Texts.F("plugin_updated", string.Join(Texts.Sep, updated)));
            Foot(string.Join(" · ", said), updated.Count > 0 ? "Yellow" : "Blue");
            ArmPluginKeys();
            RenderPlugins();
            PinnedPlugins();
            Refresh();                      // the license plugin changes what the mod rows say
        }

        void RemovePlugin(string file, string title)
        {
            // one whose downloads the manager reads says they go too
            bool data = PluginFiles.DataOf(file).Length > 0;
            if (!Dialog.AskDanger(this, Texts.F(data ? "plugin_remove_ask_data" : "plugin_remove_ask", title, file), Texts.Get("plugin_remove"))) return;
            try { PluginFiles.Remove(file); }
            catch (Exception e) when (e is ModError || e is System.IO.IOException || e is UnauthorizedAccessException)
            { Dialog.Tell(this, Texts.F("failed", e.Message)); return; }
            Foot(Texts.F("plugin_removed", title), "Blue");
            ArmPluginKeys();
            RenderPlugins();
            PinnedPlugins();
            Refresh();
        }

        /// <summary>What a drag from Explorer holds, split as PluginFiles.Dropped does: the .dll
        /// files and the rest. Nothing when it holds no files.</summary>
        static (List<string> Plugins, List<string> Others) DroppedPlugins(IDataObject data)
        {
            string[] paths = null;
            try { paths = data != null && data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null; }
            catch (System.Runtime.InteropServices.COMException) { }     // a drag source that will not give its data
            return PluginFiles.Dropped(paths);
        }

        /// <summary>While files are dragged over the Plugins page: what dropping them would do,
        /// over the whole page, so there is no doubt that it takes plugins (the deploy page's list
        /// does the same for mods).</summary>
        void PluginsPage_DragOver(object sender, DragEventArgs e)
        {
            var (plugins, others) = DroppedPlugins(e.Data);
            bool ok = plugins.Count > 0;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            PluginDropHintText.Text = Texts.Get(ok ? "drop_here" : "drop_no");
            PluginDropHintWhat.Text = !ok ? Texts.Get("plugin_drop_none")
                : Texts.F("plugin_drop_what", Texts.N(plugins.Count)) + (others.Count > 0 ? Texts.F("plugin_drop_skip", Texts.N(others.Count)) : "");
            PluginDropHint.Visibility = Visibility.Visible;
        }

        void PluginsPage_DragLeave(object sender, DragEventArgs e)
        {
            PluginDropHint.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        /// <summary>Plugin files dropped on the page: added or updated exactly as chosen ones are
        /// (AddPlugins), several at once; what is not a .dll file is named and left alone. The
        /// work waits until the drop has returned, so Explorer is not held while a question is up.</summary>
        void PluginsPage_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            PluginDropHint.Visibility = Visibility.Collapsed;
            var (plugins, others) = DroppedPlugins(e.Data);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (plugins.Count == 0) { Dialog.Tell(this, Texts.Get("plugin_drop_none")); return; }
                    AddPlugins(plugins);
                    if (others.Count > 0)
                        Dialog.Tell(this, Texts.F("plugin_drop_skipped", string.Join(Texts.Sep, others.Select(p => System.IO.Path.GetFileName(p.TrimEnd('\\', '/'))))));
                }
                catch (Exception x) when (!(x is OutOfMemoryException))
                {
                    ManagerLog.Warn("plugin drop: " + x);
                    Dialog.Tell(this, Texts.F("failed", x.Message));
                }
            }));
        }

        void Plugins_Click(object sender, RoutedEventArgs e)
        {
            if (PluginsPage.Visibility == Visibility.Visible) { ShowPage(pageBack); return; }
            ShowPage("plugins");
        }

        /// <summary>A dot, the state in words, and what it is about underneath.</summary>
        UIElement Line(string words, string colour, string about)
        {
            var head = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var dot = new Ellipse { Width = 10, Height = 10, Fill = B(colour), Margin = new Thickness(0, 5, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(dot, Dock.Left);
            head.Children.Add(dot);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = words, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(Small(about, new Thickness(0, 2, 0, 0)));
            head.Children.Add(text);
            return head;
        }
    }
}
