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
        /// the window has: window(title, content), the manager's own window frame; ask(text,
        /// action) and tell(text), its dialogs; foot(text, colour), its status line; refresh(),
        /// the library and this page read again.</summary>
        Dictionary<string, object> PluginHostHere()
        {
            var host = PluginHost.Make();
            host["window"] = (Func<string, FrameworkElement, Window>)((title, content) => new ChromeWindow(this, title, content));
            host["ask"] = (Func<string, string, bool>)((text, action) => Dialog.Ask(this, text, action));
            host["tell"] = (Action<string>)(text => Dialog.Tell(this, text));
            host["foot"] = (Action<string, string>)((text, colour) => Foot(text, colour ?? "Blue"));
            host["refresh"] = (Action)(() => { Refresh(); if (PluginsPage.Visibility == Visibility.Visible) RenderPlugins(); });
            return host;
        }

        /// <summary>The plugins page: Add plugin… beside the title, and a card for each plugin
        /// added, headed by its title, file, version and Remove. None ships with the program
        /// (the user, 2026-11-02): they are added here, or dropped on the page. Two kinds: a
        /// panel, whose plugin draws the card's body (Panel), and a window of its own, opened from
        /// its card (Open). Below them, the official plugins the library's mods need that are not
        /// added.</summary>
        void RenderPlugins()
        {
            PluginsBox.Children.Clear();
            var top = new DockPanel();
            var add = new Button { Content = Texts.Get("plugin_add"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            add.Click += (s, e) => PickPlugin();
            DockPanel.SetDock(add, Dock.Right);
            top.Children.Add(add);
            top.Children.Add(new TextBlock { Text = Texts.Get("plugins"), FontSize = 28, FontWeight = FontWeights.Bold });
            PluginsBox.Children.Add(top);
            PluginsBox.Children.Add(Texts.Hints ? Small(Texts.Get("plugins_intro"), new Thickness(0, 4, 0, 20)) : new Border { Height = 16 });
            // by the name shown, in the window's language (the user, 2026-11-03); the official ones
            // not added keep those the library's mods need first
            var all = ByName(PluginFiles.All());
            List<(OfficialPlugin Plugin, List<string> Mods)> available;
            try { available = PluginFiles.Available(Library.Load()); }
            catch (Exception e) when (e is ModError || e is System.IO.IOException) { available = new List<(OfficialPlugin, List<string>)>(); }
            available = available.OrderBy(a => a.Mods.Count > 0 ? 0 : 1).ThenBy(a => a.Plugin.TitleIn(Texts.Lang), NameOrder).ToList();
            if (all.Count == 0) PluginsBox.Children.Add(Small(Texts.Get("plugins_none"), new Thickness(0, 0, 0, 0)));
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
                    }
                }
                if (!first) PluginsBox.Children.Add(new Border { Height = 16 });
                PluginsBox.Children.Add(Card(card));
                first = false;
            }
            // the official plugins not added (the user, 2026-11-03: a page that shows only what
            // is added does not say what can be added), those the library's mods need first. The
            // list is read from GitHub (RefreshListSoon); until it has been read once, what is
            // happening is said instead
            bool neverRead = PluginFiles.OfficialJson() == null;
            if (available.Count == 0 && !neverRead) return;
            var section = new TextBlock { Text = Texts.Get("plugins_official"), FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 28, 0, 12) };
            PluginsBox.Children.Add(section);
            if (neverRead)
            {
                section.Margin = new Thickness(0, 28, 0, 0);
                PluginsBox.Children.Add(Small(listWhy == null ? Texts.Get("plugins_list_reading") : Texts.F("plugins_list_failed", Texts.Get("plugins_list_" + listWhy)),
                                              new Thickness(0, 4, 0, 0)));
                return;
            }
            // not released: said once under the heading when it holds for all, else on each card
            bool noneReleased = available.All(a => a.Plugin.Latest == null);
            if (noneReleased)
            {
                section.Margin = new Thickness(0, 28, 0, 0);
                PluginsBox.Children.Add(Small(Texts.Get("plugins_unreleased"), new Thickness(0, 4, 0, 12)));
            }
            first = true;
            foreach (var (p, mods) in available)
            {
                var card = new StackPanel();
                var head = new DockPanel();
                var latest = p.Latest;
                var file = new TextBlock
                {
                    Text = System.IO.Path.GetFileNameWithoutExtension(p.File) + (latest != null ? " " + latest.Value.Version : ""),
                    FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0),
                };
                DockPanel.SetDock(file, Dock.Right);
                head.Children.Add(file);
                var title = Heading(p.TitleIn(Texts.Lang));
                title.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(title);
                card.Children.Add(head);
                var summary = p.SummaryIn(Texts.Lang);
                if (summary != null) card.Children.Add(Small(summary, new Thickness(0, 6, 0, 0)));
                if (mods.Count > 0)
                    card.Children.Add(Line(Texts.Get("plugin_needed"), "Yellow", Texts.F("plugin_needed_mods", string.Join("、", mods))));
                var get = new Button
                {
                    Content = Texts.Get(latest != null ? "plugin_get" : "plugin_from_file"), Style = (Style)FindResource(latest != null ? "Primary" : "Ghost"),
                    MinHeight = 36, MinWidth = 96, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                };
                var plugin = p;
                if (latest != null) get.Click += (s, e) => FetchPlugin(plugin, (Button)s);
                else get.Click += (s, e) => PickPlugin(plugin.File);
                if (latest == null && !noneReleased)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(get);
                    row.Children.Add(new TextBlock
                    {
                        Text = Texts.Get("plugin_unreleased"), FontSize = 12, Foreground = B("Muted"),
                        VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 9),
                    });
                    card.Children.Add(row);
                }
                else card.Children.Add(get);
                if (!first) PluginsBox.Children.Add(new Border { Height = 16 });
                PluginsBox.Children.Add(Card(card));
                first = false;
            }
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
            foreach (var (file, type, _) in ByName(PluginFiles.All()))
            {
                if (type == null || !PluginFiles.HasButton(file, type) || !PluginFiles.Pinned(file)) continue;
                var icon = PluginIcon(type);
                if (icon == null) continue;
                var title = PluginFiles.Title(type, Texts.Lang) ?? file;
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
                b.Click += (s, e) =>
                {
                    try { PluginFiles.Call(t, "Open", this, PluginHostHere()); }
                    catch (Exception x) when (!(x is OutOfMemoryException)) { Dialog.Tell(this, Texts.F("failed", x.Message)); }
                };
                PinnedBox.Children.Add(b);
            }
        }

        /// <summary>A plugin's own icon, 20 x 20 like the header's glyphs: the frame of its app.ico
        /// that is the smallest at least as large as 20 at this screen's scale, scaled smoothly.</summary>
        UIElement PluginIcon(Type type)
        {
            var bytes = PluginFiles.IconBytes(type);
            if (bytes == null) return null;
            try
            {
                var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
                var frames = new System.Windows.Media.Imaging.IconBitmapDecoder(new System.IO.MemoryStream(bytes),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames;
                var want = 20 * scale;
                var frame = frames.Where(f => f.PixelWidth >= want).OrderBy(f => f.PixelWidth).FirstOrDefault()
                            ?? frames.OrderByDescending(f => f.PixelWidth).First();
                var image = new Image { Source = frame, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, System.Windows.Media.BitmapScalingMode.HighQuality);
                return image;
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

        /// <summary>The file picker for plugins; only: the one file an official plugin is.</summary>
        void PickPlugin(string only = null)
        {
            var pick = new Microsoft.Win32.OpenFileDialog
            {
                Title = only != null ? Texts.F("plugin_add_one_title", only) : Texts.Get("plugin_add_title"),
                Filter = only != null ? only + "|" + only : Texts.Get("plugin_filter") + "|HD2QD.*.dll", CheckFileExists = true, Multiselect = only == null,
            };
            if (pick.ShowDialog(this) == true) AddPlugins(pick.FileNames);
        }

        /// <summary>Plugin files chosen or dropped: each one read without running it, then added
        /// once the player agrees (a plugin runs as this program does), over one of the same name.</summary>
        void AddPlugins(IEnumerable<string> paths)
        {
            var added = new List<string>();
            foreach (var path in paths)
            {
                var (file, version, why) = PluginFiles.Inspect(path);
                if (why != null)
                {
                    Dialog.Tell(this, Texts.F(why == "name" ? "plugin_add_name" : why == "api" ? "plugin_add_api" : "plugin_add_broken", file));
                    continue;
                }
                var old = PluginFiles.All().FirstOrDefault(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase));
                bool replace = old.File != null;
                var ask = replace ? Texts.F("plugin_replace_ask", file, version, old.Type != null ? PluginFiles.Version(old.Type) : "?")
                                  : Texts.F("plugin_add_ask", file, version);
                if (!Dialog.Ask(this, ask, Texts.Get(replace ? "plugin_replace_go" : "plugin_add_go"))) continue;
                try { PluginFiles.Install(path); added.Add(file + " " + version); }
                catch (Exception e) when (e is ModError || e is System.IO.IOException || e is UnauthorizedAccessException)
                { Dialog.Tell(this, Texts.F("failed", e.Message)); }
            }
            if (added.Count == 0) return;
            Foot(Texts.F("plugin_added", string.Join(Texts.Sep, added)), "Blue");
            RenderPlugins();
            PinnedPlugins();
            Refresh();                      // the license plugin changes what the mod rows say
        }

        void RemovePlugin(string file, string title)
        {
            if (!Dialog.AskDanger(this, Texts.F("plugin_remove_ask", title, file), Texts.Get("plugin_remove"))) return;
            try { PluginFiles.Remove(file); }
            catch (Exception e) when (e is ModError || e is System.IO.IOException || e is UnauthorizedAccessException)
            { Dialog.Tell(this, Texts.F("failed", e.Message)); return; }
            Foot(Texts.F("plugin_removed", title), "Blue");
            RenderPlugins();
            PinnedPlugins();
            Refresh();
        }

        static List<string> DroppedPlugins(IDataObject data) =>
            (data != null && data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null)?
                .Where(p => System.IO.File.Exists(p) && p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<string>();

        void PluginsPage_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DroppedPlugins(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void PluginsPage_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            var paths = DroppedPlugins(e.Data);
            if (paths.Count > 0) AddPlugins(paths);
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
