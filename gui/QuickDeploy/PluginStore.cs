using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hd2Mod;

namespace QuickDeploy
{
    // The plugins page as a store (the user, 2026-11-28: "插件多了不可能一下子全堆在一页", as
    // Obsidian and Notion have it): two tabs over the page, Added (the cards, PluginsView.cs) and
    // Browse. Browse lists every plugin of the official and community lists on the left, with a
    // search and filters, and shows the one chosen on the right, with what it can do there.
    public partial class MainWindow
    {
        /// <summary>The tab shown: "added" or "browse"; null until the page is first drawn (then
        /// Browse when no plugin is added, else Added).</summary>
        string pluginTab;
        string browseFilter = "all", browseQuery = "", browseSel;
        TextBox browseSearch;
        TextBlock browseHint;
        StackPanel browseBody;

        /// <summary>A listed plugin as Browse shows it: whether it is added (its type when it
        /// loads), a newer release of it, and the library's mods that need it.</summary>
        class StoreEntry
        {
            public OfficialPlugin Plugin;
            public bool Added;
            public Type Type;
            public string Newer;
            public List<string> Mods = new List<string>();
        }

        /// <summary>Every listed plugin (official, then community): those the library's mods need
        /// and do not have first, then those with an update, then by name in the window's language.</summary>
        List<StoreEntry> StoreEntries()
        {
            var added = PluginFiles.All().GroupBy(x => x.File, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            Library lib = null;
            try { lib = Library.Load(); }
            catch (Exception e) when (e is ModError || e is System.IO.IOException) { }
            var list = new List<StoreEntry>();
            foreach (var p in PluginFiles.Listed())
            {
                var en = new StoreEntry { Plugin = p };
                if (added.TryGetValue(p.File, out var a))
                {
                    en.Added = true;
                    en.Type = a.Type;
                    if (a.Type != null && PluginFiles.UpdateOf(a.File, a.Type) is var u && u != null) en.Newer = u.Value.Newest;
                }
                if (p.WantedBy != null && lib != null)
                    en.Mods = lib.Mods.Where(m => System.IO.Directory.Exists(m.Path) && System.IO.File.Exists(System.IO.Path.Combine(m.Path, p.WantedBy))).Select(m => m.Name).ToList();
                list.Add(en);
            }
            return list.OrderBy(e => e.Mods.Count > 0 && !e.Added ? 0 : e.Newer != null ? 1 : 2)
                       .ThenBy(e => e.Plugin.Community ? 1 : 0)
                       .ThenBy(e => e.Plugin.TitleIn(Texts.Lang), NameOrder).ToList();
        }

        /// <summary>The filter and the search: every word typed is found in the title (in the
        /// window's language or English), the summary, the author or the file name.</summary>
        bool Shown(StoreEntry e)
        {
            if (browseFilter == "official" && e.Plugin.Community) return false;
            if (browseFilter == "community" && !e.Plugin.Community) return false;
            if (browseFilter == "needed" && (e.Mods.Count == 0 || e.Added)) return false;
            var words = browseQuery.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return true;
            var p = e.Plugin;
            var hay = string.Join("\n", p.TitleIn(Texts.Lang), p.Title, p.SummaryIn(Texts.Lang), p.Summary, p.Author, p.File);
            return words.All(w => hay.IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }

        /// <summary>The page's head, on both tabs: the title, the two tabs, and Add plugin… with
        /// the drop tip (the user, 2026-11-28: always said, not only while files are dragged).</summary>
        FrameworkElement PluginsHead(int added, int listed)
        {
            var top = new DockPanel();
            var add = new Button { Content = Texts.Get("plugin_add"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            add.Click += (s, e) => PickPlugin();
            DockPanel.SetDock(add, Dock.Right);
            top.Children.Add(add);
            var tip = new TextBlock { Text = Texts.Get("plugin_drop_tip"), FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(tip, Dock.Right);
            top.Children.Add(tip);
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            // the big CJK title sits 1.5 px high of the controls' middle (measured at 200 %)
            left.Children.Add(new TextBlock { Text = Texts.Get("plugins"), FontSize = 28, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) });
            // the two tabs as one control (the user, 2026-11-28: two buttons side by side did not
            // line up): one frame, the tab shown filled, as tall as Add plugin… beside it
            var seg = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (key, text) in new[] { ("added", Texts.F("plugins_tab_added", added)), ("browse", Texts.F("plugins_tab_browse", listed)) })
            {
                var on = pluginTab == key;
                var face = new Border
                {
                    Height = 34, Padding = new Thickness(16, 0, 16, 0), Background = on ? B("Yellow") : System.Windows.Media.Brushes.Transparent,
                    Child = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = on ? B("TapeInk") : B("Ink"), VerticalAlignment = VerticalAlignment.Center },
                };
                var b = new Button { Template = Plain(), Cursor = on ? Cursors.Arrow : Cursors.Hand, FocusVisualStyle = null, Content = face };
                if (!on)
                {
                    b.MouseEnter += (s, e) => face.Background = B("Ground700");
                    b.MouseLeave += (s, e) => face.Background = System.Windows.Media.Brushes.Transparent;
                }
                System.Windows.Automation.AutomationProperties.SetAutomationId(b, "plugins_tab:" + key);
                var k = key;
                b.Click += (s, e) => { if (pluginTab != k) ShowPluginTab(k); };
                tabs.Children.Add(b);
            }
            seg.Child = tabs;
            left.Children.Add(seg);
            top.Children.Add(left);
            return top;
        }

        /// <summary>A tab of the plugins page.</summary>
        void ShowPluginTab(string tab)
        {
            pluginTab = tab;
            RenderPlugins();
            PluginsPage.ScrollToTop();
        }

        /// <summary>The Browse tab: the list on the left (where the Added tab has its guide), wider,
        /// and the chosen plugin in the page's column.</summary>
        void RenderBrowse()
        {
            PluginGuide.Children.Clear();
            PluginGuideBar.Visibility = Visibility.Visible;
            // the search field, kept while the page is drawn again, so typing goes on
            if (browseSearch == null)
            {
                browseSearch = new TextBox
                {
                    Background = System.Windows.Media.Brushes.Transparent, Foreground = B("Ink"), CaretBrush = B("Ink"),
                    BorderThickness = new Thickness(0), Padding = new Thickness(10, 8, 8, 8), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center,
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(browseSearch, "plugins_search");
                browseHint = new TextBlock { IsHitTestVisible = false, Foreground = B("Muted"), FontSize = 14, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                browseSearch.TextChanged += (s, e) =>
                {
                    browseQuery = browseSearch.Text;
                    browseHint.Visibility = browseSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                    FillBrowse();
                };
            }
            browseHint.Text = Texts.Get("plugins_search_hint");
            browseHint.Visibility = browseSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (browseSearch.Parent is Panel old) old.Children.Clear();
            var field = new Grid();
            field.Children.Add(browseSearch);
            field.Children.Add(browseHint);
            PluginGuide.Children.Add(new Border { Background = B("Ground900"), BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Child = field });
            browseBody = new StackPanel();
            PluginGuide.Children.Add(browseBody);
            // where an author lists one: under the list, also when it is empty, so authors find it
            var how = new Button { Content = Texts.Get("plugins_community_how"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            how.Click += (s, e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CommunityHowIn(Texts.Lang)) { UseShellExecute = true });
            PluginGuide.Children.Add(how);
            FillBrowse();
        }

        /// <summary>What the search, the filters and the choice change: the filters, the list and
        /// the column (the search field stays, with the caret in it).</summary>
        void FillBrowse()
        {
            var entries = StoreEntries();
            browseBody.Children.Clear();
            // the filters: the library's needs only when it has some
            var filters = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
            var needed = entries.Count(e => e.Mods.Count > 0 && !e.Added);
            if (browseFilter == "needed" && needed == 0) browseFilter = "all";
            foreach (var f in new[] { "all", "official", "community", "needed" })
            {
                if (f == "needed" && needed == 0) continue;
                var on = browseFilter == f;
                var b = new Button
                {
                    Cursor = Cursors.Hand, FocusVisualStyle = null, Template = Plain(), Margin = new Thickness(0, 0, 6, 6),
                    Content = new Border
                    {
                        Background = on ? B("Ground700") : System.Windows.Media.Brushes.Transparent, BorderBrush = on ? B("Yellow") : B("Grid"),
                        BorderThickness = new Thickness(1), Padding = new Thickness(10, 3, 10, 4),
                        Child = new TextBlock { Text = Texts.Get("plugins_filter_" + f), FontSize = 12, Foreground = on ? B("Ink") : B("Muted"), FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal },
                    },
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(b, "plugins_filter:" + f);
                var k = f;
                b.Click += (s, e) => { browseFilter = k; browseSel = null; FillBrowse(); };
                filters.Children.Add(b);
            }
            browseBody.Children.Add(filters);

            var shown = entries.Where(Shown).ToList();
            if (browseSel == null || !shown.Any(e => Same(e.Plugin.File, browseSel))) browseSel = shown.FirstOrDefault()?.Plugin.File;
            foreach (var e in shown) browseBody.Children.Add(StoreRow(e));
            if (shown.Count == 0 && PluginFiles.OfficialJson() != null)
                browseBody.Children.Add(Small(Texts.Get("plugins_browse_none"), new Thickness(8, 8, 8, 0)));

            PluginsBox.Children.Clear();
            PluginsBox.Children.Add(PluginsHead(PluginFiles.All().Count, entries.Count));
            PluginsBox.Children.Add(new Border { Height = 24 });
            if (PluginFiles.OfficialJson() == null)
            {
                // never read: what is happening
                PluginsBox.Children.Add(Small(listWhy == null ? Texts.Get("plugins_list_reading") : Texts.F("plugins_list_failed", Texts.Get("plugins_list_" + listWhy)), new Thickness(0)));
                return;
            }
            var sel = shown.FirstOrDefault(e => Same(e.Plugin.File, browseSel));
            if (sel != null) PluginsBox.Children.Add(StoreDetail(sel));
        }

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>A line of the list: the title and what needs the player (needed, an update),
        /// else that it is added or a community plugin; under it who made it (official or the
        /// author) and its newest version. The one chosen lit as the guide lights its line.</summary>
        FrameworkElement StoreRow(StoreEntry e)
        {
            var p = e.Plugin;
            bool cur = Same(p.File, browseSel);
            var btn = new Button { Cursor = Cursors.Hand, FocusVisualStyle = null, Margin = new Thickness(0, 1, 0, 1), Template = Plain(), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var row = new Border
            {
                Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(2, 0, 0, 0),
                BorderBrush = cur ? B("Yellow") : System.Windows.Media.Brushes.Transparent, Background = cur ? B("Ground700") : System.Windows.Media.Brushes.Transparent,
            };
            var words = new StackPanel();
            var head = new DockPanel();
            string badge = null; bool hot = false;
            if (e.Mods.Count > 0 && !e.Added) { badge = "plugin_state_needed"; hot = true; }
            else if (e.Newer != null) { badge = "plugin_state_update"; hot = true; }
            else if (e.Added) badge = "plugin_state_added";
            else if (p.Community) badge = "plugin_guide_community";
            if (badge != null)
            {
                var b = Chip(Texts.Get(badge), hot);
                DockPanel.SetDock(b, Dock.Right);
                head.Children.Add(b);
            }
            head.Children.Add(new TextBlock { Text = p.TitleIn(Texts.Lang), FontWeight = FontWeights.SemiBold, Foreground = cur ? B("Yellow") : B("Ink"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            words.Children.Add(head);
            var latest = p.Latest;
            words.Children.Add(new TextBlock
            {
                Text = (p.Community ? p.Author ?? "?" : Texts.Get("plugins_filter_official")) + (latest != null ? " · " + latest.Value.Version : ""),
                FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Child = words;
            btn.Content = row;
            btn.ToolTip = p.TitleIn(Texts.Lang) + "\n" + p.File;
            System.Windows.Automation.AutomationProperties.SetName(btn, p.TitleIn(Texts.Lang));
            System.Windows.Automation.AutomationProperties.SetAutomationId(btn, "store:" + p.File);
            var file = p.File;
            btn.Click += (s, ev) => { browseSel = file; FillBrowse(); PluginsPage.ScrollToTop(); };
            return btn;
        }

        /// <summary>A small badge, as the guide draws its own: the accent when it needs the player.</summary>
        Border Chip(string text, bool hot) => new Border
        {
            Background = hot ? B("Yellow") : B("Grid"), Padding = new Thickness(6, 1, 6, 0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = hot ? B("TapeInk") : B("Muted") },
        };

        /// <summary>The plugin chosen: its title and whose it is, the library's mods that need it,
        /// what it does, and what can be done: Download and add, its update, or (added) open it or
        /// go to its card; its source; for a community plugin, that nobody reviewed it.</summary>
        FrameworkElement StoreDetail(StoreEntry e)
        {
            var p = e.Plugin;
            var latest = p.Latest;
            var box = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = p.TitleIn(Texts.Lang), FontSize = 22, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var kind = Chip(Texts.Get(p.Community ? "plugin_guide_community" : "plugins_filter_official"), false);
            kind.Margin = new Thickness(12, 2, 0, 0);
            head.Children.Add(kind);
            box.Children.Add(head);
            var meta = Texts.F("plugin_author_licence", p.Author ?? "?", p.License ?? "?") + " · " + System.IO.Path.GetFileNameWithoutExtension(p.File) + (latest != null ? " " + latest.Value.Version : "");
            if (e.Added && e.Type != null) meta += " · " + Texts.F("plugin_added_version", PluginFiles.Version(e.Type));
            box.Children.Add(Small(meta, new Thickness(0, 4, 0, 0)));
            if (e.Mods.Count > 0 && !e.Added)
                box.Children.Add(Line(Texts.Get("plugin_needed"), "Yellow", Texts.F("plugin_needed_mods", string.Join("、", e.Mods))));
            var summary = p.SummaryIn(Texts.Lang);
            if (summary != null)
                box.Children.Add(new TextBlock { Text = summary, FontSize = 14, LineHeight = 22, Foreground = B("Ink"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) });

            var row = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
            Button Act(string key, string style)
            {
                var b = new Button { Content = Texts.Get(key), Style = (Style)FindResource(style), MinHeight = 36, MinWidth = 96, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 0, 12, 8) };
                System.Windows.Automation.AutomationProperties.SetAutomationId(b, "store_act:" + key);
                return b;
            }
            if (!e.Added)
            {
                var get = Act(latest != null ? "plugin_get" : "plugin_from_file", latest != null ? "Primary" : "Ghost");
                if (latest != null) get.Click += (s, ev) => FetchPlugin(p, (Button)s);
                else get.Click += (s, ev) => PickPlugin(p.File);
                row.Children.Add(get);
            }
            else
            {
                if (e.Newer != null)
                {
                    var up = Act("plugin_update_go", "Primary");
                    up.Content = Texts.F("plugin_update_to", e.Newer);
                    up.Click += (s, ev) => FetchPlugin(p, (Button)s);
                    row.Children.Add(up);
                }
                var card = Act("plugin_show_added", "Ghost");
                var file = p.File;
                card.Click += (s, ev) =>
                {
                    ShowPluginTab("added");
                    Dispatcher.BeginInvoke(new Action(() => GuideGo(file)), System.Windows.Threading.DispatcherPriority.Loaded);
                };
                row.Children.Add(card);
            }
            // its code, where anyone can read it before adding it
            var source = Act("plugin_source", "Ghost");
            var page = "https://github.com/" + p.Repository;
            source.ToolTip = page;
            source.Click += (s, ev) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true });
            row.Children.Add(source);
            box.Children.Add(row);
            if (latest == null)
                box.Children.Add(Small(Texts.Get("plugin_unreleased"), new Thickness(0, 4, 0, 0)));
            if (p.Community)
                box.Children.Add(new Border
                {
                    BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(0, 12, 0, 0),
                    Child = Small(Texts.Get("plugins_community_note"), new Thickness(0)),
                });
            return box;
        }
    }
}
