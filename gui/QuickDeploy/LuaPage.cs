using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Hd2Mod;

namespace QuickDeploy
{
    /// <summary>The Lua loaders page, after Deploy (the user, 2026-11-01: a page of its own that
    /// makes the loaders clear, as easy to use and as complete as can be; the loader card on the
    /// deploy page's left stays). From the top:
    /// - where things stand: the loader in effect, how many Lua mods start, how many do not, the
    ///   conflicts;
    /// - the loaders of the library side by side, "no loader" too: what would not start under
    ///   each and why, its version, update and previous version, its native part (MDL3), its last
    ///   session; choosing it, its log, and in its menu details, update, new version, rollback,
    ///   folder, remove and delete;
    /// - the conflicts and problems, each in one sentence;
    /// - every Lua mod: its switch, what starts it, what it does under the loader in effect, its
    ///   last session; the ones in trouble first;
    /// - the mods that bring a start-up script of their own, and what becomes of each;
    /// - the loaders' logs, by level, by session, searched.
    /// Every fact comes from the state the rest of the window reads, and the logs.</summary>
    public partial class MainWindow
    {
        const int LuaCols = 3;


        void RenderLua()
        {
            LuaBox.Children.Clear();
            if (st == null) return;
            var lua = st.Lua;
            var loaders = st.Mods.Where(x => x.Loader).ToList();
            var mods = st.Mods.Where(x => x.Lua && !x.Loader && (!st.ProfilesOn || x.InProfile)).ToList();
            var on = mods.Where(x => x.Enabled).ToList();

            // ---------------------------------------------------------- the head
            // 12 on the right, as every card and tile has: their right edges in one line
            var head = new DockPanel { Margin = new Thickness(0, 0, 12, 16) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var add = new Button { Content = Texts.Get("lp_add"), Style = (Style)FindResource("Ghost"), MinHeight = 40 };
            add.Click += (s, e) => { var p = PickZip(); if (p != null) Run("add", p); };
            tools.Children.Add(add);
            var logs = new Button { Content = Texts.Get("lp_logs_folder"), Style = (Style)FindResource("Ghost"), MinHeight = 40, Margin = new Thickness(8, 0, 0, 0) };
            logs.Click += (s, e) => OpenFolder(LuaMods.GameFolder);
            tools.Children.Add(logs);
            DockPanel.SetDock(tools, Dock.Right);
            head.Children.Add(tools);
            head.Children.Add(new TextBlock { Text = Texts.Get("lp_title"), FontSize = 28, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            LuaBox.Children.Add(head);

            if (lua == null)
            {
                LuaBox.Children.Add(Small(Texts.Get(st.LuaPending ? "lp_pending" : "lp_unknown"), new Thickness(0)));
                return;
            }

            // ---------------------------------------------------------- where things stand
            var now = on.Select(NowOf).ToList();
            int starting = now.Count(c => c.Colour == "Blue"), stopped = now.Count(c => c.Colour == "Yellow"), broken = now.Count(c => c.Colour == "Red");
            var problems = Problems();
            // the loader's name gets the widest tile
            var tiles = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            foreach (var w in new[] { 1.8, 1, 1, 1, 1 }) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            tiles.Children.Add(Tile("lp_tile_loader", RunningLabel() ?? Texts.Get("lp_none_in_effect"), RunningLabel() != null ? "Blue" : on.Count > 0 ? "Yellow" : "Ink", "loaders"));
            tiles.Children.Add(Tile("lp_tile_start", Texts.F("lp_n_of_m", starting, on.Count), "Blue", "mods"));
            tiles.Children.Add(Tile("lp_tile_stop", stopped.ToString(), stopped > 0 ? "Yellow" : "Ink", "mods"));
            tiles.Children.Add(Tile("lp_tile_broken", broken.ToString(), broken > 0 ? "Red" : "Ink", "mods"));
            tiles.Children.Add(Tile("lp_tile_conflicts", problems.Count.ToString(), problems.Count > 0 ? "Yellow" : "Ink", "loaders"));
            for (int c = 0; c < tiles.Children.Count; c++) Grid.SetColumn(tiles.Children[c], c);
            LuaBox.Children.Add(tiles);

            // ---------------------------------------------------------- the tabs
            // (the user, 2026-11-01: with a long list of Lua mods the logs were far down the page)
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (key, text) in new[] { ("loaders", Texts.Get("lp_tab_loaders")), ("mods", Texts.F("lp_tab_mods", mods.Count)), ("logs", Texts.Get("lp_tab_logs")) })
            {
                var b = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "luatab", Content = text, MinWidth = 120, IsChecked = luaTab == key };
                var k = key;
                b.Click += (s, e) => ShowLuaTab(k);
                tabs.Children.Add(b);
            }
            LuaBox.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16), Child = tabs });

            if (luaTab == "mods") { LuaModsTab(mods, on, starting, stopped, broken); return; }
            if (luaTab == "logs") { LuaLogsTab(); return; }

            // ---------------------------------------------------------- the loaders
            LuaBox.Children.Add(Label("lp_choose_head", new Thickness(0, 0, 0, 4)));
            if (Texts.Hints) LuaBox.Children.Add(Small(Texts.Get("lp_choose_note"), new Thickness(0, 0, 12, 10)));
            if (loaders.Count == 0)
                LuaBox.Children.Add(Panel(Small(Texts.Get("lp_no_loaders"), new Thickness(0)), new Thickness(0, 0, 12, 12)));
            var cards = new Grid();
            for (int c = 0; c < LuaCols; c++) cards.ColumnDefinitions.Add(new ColumnDefinition());
            var all = loaders.Concat(new ModRow[] { null }).ToList();
            for (int i = 0; i < all.Count; i++)
            {
                if (i % LuaCols == 0) cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var card = LoaderCard(all[i], on.Count(x => x.Lu?.Broken.Count == 0));
                Grid.SetRow(card, i / LuaCols);
                Grid.SetColumn(card, i % LuaCols);
                cards.Children.Add(card);
            }
            LuaBox.Children.Add(cards);

            // ---------------------------------------------------------- loaders together
            var compat = CompatTable(loaders);
            if (compat != null)
            {
                LuaBox.Children.Add(Label("lp_compat_head", new Thickness(0, 12, 0, 4)));
                if (Texts.Hints) LuaBox.Children.Add(Small(Texts.Get("lp_compat_note"), new Thickness(0, 0, 12, 10)));
                LuaBox.Children.Add(compat);
            }

            // ---------------------------------------------------------- conflicts and problems
            LuaBox.Children.Add(Label("lp_problems_head", new Thickness(0, 12, 0, 4)));
            var plist = new StackPanel();
            if (problems.Count == 0) plist.Children.Add(new TextBlock { Text = Texts.Get("lp_no_problems"), Foreground = B("Blue") });
            foreach (var (text, colour) in problems)
            {
                var t = Selectable.Text(text, 13, colour);
                t.Margin = new Thickness(0, 2, 0, 2);
                plist.Children.Add(t);
            }
            LuaBox.Children.Add(Panel(plist, new Thickness(0, 0, 12, 0)));
        }

        /// <summary>The tab of the Lua loaders page on show: "loaders" (the cards and the
        /// conflicts), "mods" (every Lua mod), "logs" (the loaders' logs).</summary>
        string luaTab = "loaders";

        void ShowLuaTab(string tab)
        {
            luaTab = tab;
            RenderLua();
            LuaPage.ScrollToTop();
        }

        /// <summary>The Lua mods' tab: every Lua mod, the ones in trouble first.</summary>
        void LuaModsTab(List<ModRow> mods, List<ModRow> on, int starting, int stopped, int broken)
        {
            // the tab is its title and the tiles above count them: no head of its own
            if (mods.Count == 0) LuaBox.Children.Add(Small(Texts.Get("lp_no_lua_mods"), new Thickness(0, 4, 0, 0)));
            else
            {
                var table = new StackPanel();
                table.Children.Add(LuaRow(null));
                // the ones in trouble first, then those that start, then those off; each in the load order
                int Rank(ModRow x) => !x.Enabled ? 3 : NowOf(x).Colour == "Red" ? 0 : NowOf(x).Colour == "Yellow" ? 1 : 2;
                foreach (var x in mods.OrderBy(Rank).ThenBy(x => x.Index)) table.Children.Add(LuaRow(x));
                LuaBox.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1, 1, 1, 0), Background = B("Ground700"), Child = table, Margin = new Thickness(0, 0, 12, 0) });
            }
        }

        /// <summary>The logs' tab: the loaders' own logs.</summary>
        void LuaLogsTab()
        {
            var log = LuaLog();
            log.Box = new StackPanel();
            LuaBox.Children.Add(Panel(log.Box, new Thickness(0, 0, 12, 0)));
            RenderLog(log);
        }

        /// <summary>A number at the top of the page, with what it counts above it; a click opens
        /// the tab that lists them.</summary>
        FrameworkElement Tile(string key, string value, string colour, string tab)
        {
            var tile = new Border
            {
                BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground900"), Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 12, 0), Cursor = Cursors.Hand, ToolTip = Texts.Get("lp_tile_open_" + tab),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = Texts.Get(key), FontSize = 12, Foreground = B("Muted") },
                        new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = B(colour), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            tile.MouseEnter += (s, e) => tile.BorderBrush = B("Muted");
            tile.MouseLeave += (s, e) => tile.BorderBrush = B("Grid");
            tile.MouseLeftButtonUp += (s, e) => ShowLuaTab(tab);
            return tile;
        }

        /// <summary>The conflicts between mods' scripts, each in one sentence: start-up scripts, two
        /// loaders, a script name declared twice, a translation pack without its mod. What depends on
        /// the loader is on the loaders' cards, and what a mod lacks in its line of the table: not
        /// said again here.</summary>
        List<(string Text, string Colour)> Problems()
        {
            var said = new List<(string, string)>();
            var sep = Texts.Zh ? "、" : ", ";
            string Names(IEnumerable<string> ids) => string.Join(sep, ids.Select(id => ById(id)?.Name ?? id));
            foreach (var i in st.Lua.Issues)
            {
                switch (i.Kind)
                {
                    case "boot_conflict":
                        said.Add((Texts.F("lp_p_boot_conflict", ById(i.Mods[0])?.Name ?? "?", Names(i.Mods.Skip(1))), "Yellow"));
                        break;
                    case "two_loaders": said.Add((Texts.F("lp_p_two_loaders", Names(i.Mods)), "Yellow")); break;
                    case "same_name": said.Add((Texts.F("lp_p_same_name", i.Detail, ById(i.Mods[0])?.Name ?? "?", Names(i.Mods.Skip(1))), "Yellow")); break;
                    case "pack_alone": said.Add((Texts.F("lp_p_pack_alone", Names(i.Mods), i.Detail), "Yellow")); break;
                }
            }
            return said;
        }

        /// <summary>One loader's card (null: no loader): its name and state, what would not start
        /// under it, its version, update and previous version, its native part and last session;
        /// the buttons to choose it and see its log, and a menu for the rest.</summary>
        FrameworkElement LoaderCard(ModRow x, int luaOn)
        {
            var lua = st.Lua;
            bool on = x == null ? st.LoaderChoice == null : x.Id == st.LoaderChoice;
            bool helping = x != null && !on && st.LoaderHelpers.Contains(x.Id);
            var box = new StackPanel();

            // the name, and its state on the right
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var state = new Border
            {
                BorderBrush = B(on || helping ? "Blue" : "Grid"), BorderThickness = new Thickness(1), Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0),
                Child = new TextBlock
                {
                    Text = Texts.Get(on && loaderSwitching ? "lua_side_switching" : on ? "lua_side_on" : helping ? "lua_side_helping" : "lua_side_off"),
                    FontSize = 12, Foreground = B(on || helping ? "Blue" : "Muted"),
                },
            };
            DockPanel.SetDock(state, Dock.Right);
            top.Children.Add(state);
            var title = x == null ? Texts.Get("lua_side_nothing") : LoaderLabel(x.LoaderKind, x.Lu?.LoaderVersion) ?? x.Name;
            var name = Selectable.Text(title, 16, "Ink");
            name.FontWeight = FontWeights.SemiBold;
            top.Children.Add(name);
            box.Children.Add(top);
            // the library's own name for it, when that is not what the card is called
            // (not when it is the same name but for its version)
            if (x != null && NewVersions.Key(x.Name) != NewVersions.Key(title))
                box.Children.Add(Small(x.Name, new Thickness(0, -4, 0, 8)));

            void Say(string text, string colour, double size = 13)
            {
                var t = Selectable.Text(text, size, colour);
                t.Margin = new Thickness(0, 2, 0, 2);
                box.Children.Add(t);
            }
            // what would not start under it, the same question for every card
            var key = x?.Id ?? Loaders.None;
            if (lua.Outcomes.TryGetValue(key, out var outs))
            {
                var sep = Texts.Zh ? "、" : ", ";
                if (outs.Count == 0) Say(luaOn == 0 ? Texts.Get("lp_no_lua_on") : Texts.F(on ? "lp_all_start" : "lp_all_start_if", luaOn), "Blue");
                foreach (var o in outs)
                {
                    var over = ById(o.Over);
                    var why = Texts.F("lua_why_" + o.Why, over == null ? "?" : over.Loader ? LoaderLabel(over.LoaderKind, over.Lu?.LoaderVersion) ?? over.Name : over.Name);
                    Say(Texts.F(on ? "lua_side_wont" : "lua_side_wont_if", why, string.Join(sep, o.Mods.Select(id => ById(id)?.Name ?? id))), "Yellow");
                }
            }
            // helping Boot Mod Loader: the mods it starts (Chef), or runs for (MDL3 as its guest)
            if (helping)
            {
                bool guest = x.LoaderKind == "mdl3";
                var helped = st.Mods.Where(y => y.Enabled && !y.Loader && (guest ? y.Lu?.UsesMdl3 == true : y.Lu?.Needs == "chef")).Select(y => y.Name).ToList();
                var chosen = ById(st.LoaderChoice);
                Say(Texts.F(guest ? "lua_info_guest" : "lua_info_helping", chosen != null ? LoaderLabel(chosen.LoaderKind, chosen.Lu?.LoaderVersion) ?? chosen.Name : "?",
                            string.Join(Texts.Zh ? "、" : ", ", helped)), "Blue");
            }
            UpdateInfo u = null;
            if (x != null)
            {
                // its version, an update, the version kept before it
                u = Checked(x);
                // the version is in the title when the loader's script names it
                var parts = new List<string>();
                if (x.Lu?.LoaderVersion == null) parts.Add(Texts.F("lp_version", x.ShownVersion ?? Texts.Get("up_none")));
                if (x.PrevPath != null) parts.Add(Texts.F("lp_prev", x.PrevVersion ?? "?"));
                if (parts.Count > 0) Say(string.Join(" · ", parts), "Muted", 12);
                if (u?.Status == "update") Say(Texts.F("lp_update", u.Latest ?? "?"), "Yellow", 12);
            }
            // MDL3's native part: without it MDL3 does not work
            if (x != null && x.Id == lua.Mdl3Mod)
            {
                if (lua.Mdl3Problem != null) Say(Texts.Get("lp_native_manual"), "Yellow", 12);
                else if (lua.Mdl3State == "installed") Say(Texts.F("lp_native_ok", lua.Mdl3Native), "Muted", 12);
                else if (lua.Mdl3State == "not installed") Say(Texts.F("lp_native_missing", lua.Mdl3Native), "Yellow", 12);
                else Say(Texts.F("lp_native_old", lua.Mdl3Native), "Yellow", 12);
            }
            // the last session, from Boot Mod Loader's log
            if (x?.LoaderKind == "bml" && lua.Started >= 0)
                Say(Texts.F("lua_side_session", When(lua.SessionWhen), lua.Started, lua.Total), "Muted", 12);
            // its own scripts the game cannot run
            if (x?.Lu != null && x.Lu.Broken.Count > 0) Say(Texts.F("lp_loader_broken", x.Lu.Broken.Count), "Red", 12);

            // the buttons, at the bottom of the card whatever its height
            var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            Button Btn(string text, string style, Action act)
            {
                var b = new Button { Content = text, Style = (Style)FindResource(style), MinHeight = 36, Margin = new Thickness(0, 0, 8, 8) };
                b.Click += (s, e) => act();
                buttons.Children.Add(b);
                return b;
            }
            if (!on) Btn(Texts.Get(x == null ? "lp_use_none" : "lp_use"), "Primary", () => ChooseLoader(x)).IsEnabled = !busy && !loaderSwitching;
            if (x != null)
            {
                if (u?.Status == "update") Btn(Texts.Get("lp_do_update"), "Ghost", () => ApplyUpdate(x, u));
                if (LoaderLogs.Of(x.LoaderKind).Any(l => l.Exists)) Btn(Texts.Get("lp_log"), "Ghost", () => ShowLuaLog(x.LoaderKind));
                Button more = null;
                more = Btn(Texts.Get("lp_more"), "Ghost", () => LoaderMenu(x, u, more));
            }
            var dock = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(buttons);
            dock.Children.Add(box);
            return new Border
            {
                BorderBrush = B(on ? "Blue" : "Grid"), BorderThickness = new Thickness(on ? 2 : 1), Background = B("Ground900"),
                Padding = new Thickness(on ? 15 : 16), Margin = new Thickness(0, 0, 12, 12), Child = dock,
            };
        }

        /// <summary>A loader's menu: its details, update, a new version, rollback, its folder,
        /// removing it from the library and deleting it from the disk.</summary>
        void LoaderMenu(ModRow x, UpdateInfo u, FrameworkElement at)
        {
            var menu = new ContextMenu { PlacementTarget = at, Placement = PlacementMode.Bottom };
            void Item(string text, Action act, bool enabled = true)
            {
                var it = new MenuItem { Header = text, IsEnabled = enabled };
                it.Click += (s, e) => Dispatcher.BeginInvoke(act, System.Windows.Threading.DispatcherPriority.ContextIdle);
                menu.Items.Add(it);
            }
            Item(Texts.Get("lp_details"), () => ShowMod(x.Id));
            if (u?.Status == "update") Item(Texts.F("lp_update_to", u.Latest ?? "?"), () => ApplyUpdate(x, u));
            else Item(Texts.Get("lp_check_update"), () => CheckOne(x), FromOf(x) != "none");
            Item(Texts.Get("replace_zip"), () => { var p = PickZip(); if (p != null) Run("replace", x.Id, p); });
            Item(Texts.Get("replace_folder"), () => { var p = PickFolder(); if (p != null) Run("replace", x.Id, p); });
            if (x.PrevPath != null) Item(Texts.F("lp_rollback", x.PrevVersion ?? "?"), () => Rollback(x));
            menu.Items.Add(new Separator());
            Item(Texts.Get("lp_open_folder"), () => OpenFolder(x.Path));
            menu.Items.Add(new Separator());
            Item(Texts.Get("remove_menu"), () => { if (Dialog.Ask(this, Texts.F("remove_ask", x.Name), Texts.Get("remove_btn"))) Run("remove", x.Id); });
            Item(Texts.Get("delete_menu"), () => DeleteFromDisk(x));
            menu.IsOpen = true;
        }

        /// <summary>A Lua mod under the loader in effect: whether it starts, and why not.</summary>
        (string Text, string Colour) NowOf(ModRow m)
        {
            var lu = m.Lu;
            if (lu == null) return ("", "Muted");
            if (!m.Enabled) return (Texts.Get("lp_now_off"), "Muted");
            var sep = Texts.Zh ? "、" : ", ";
            if (lu.Broken.Count > 0)
            {
                var libs = lu.Broken.SelectMany(b => b.Jit).Distinct().ToList();
                var why = libs.Count > 0 ? Texts.F("lp_uses", string.Join(sep, libs.Select(j => Texts.Get("lua_lib_" + j))))
                        : lu.Broken.Any(b => b.LuaJitBytecode) ? Texts.Get("lp_bytecode") : Texts.Get("lp_bad_code");
                return (Texts.Get("lua_tag_broken") + Texts.Get("lp_colon") + why, "Red");
            }
            if (st.Lua.Outcomes.TryGetValue(st.LoaderChoice ?? Loaders.None, out var outs))
                foreach (var o in outs.Where(o => o.Mods.Contains(m.Id)))
                {
                    var over = ById(o.Over);
                    var why = Texts.F("lua_why_" + o.Why, over == null ? "?" : over.Loader ? LoaderLabel(over.LoaderKind, over.Lu?.LoaderVersion) ?? over.Name : over.Name);
                    if (o.Why == "boot_off") why = Texts.F("lp_boot_over", over?.Name ?? "?");
                    return (Texts.Get(o.Why == "boot_off" ? "lua_tag_boot_off" : "lua_tag_nostart") + Texts.Get("lp_colon") + why, "Yellow");
                }
            foreach (var i in IssuesOf(m))
            {
                bool first = i.Mods.Count > 0 && i.Mods[0] == m.Id;
                if (i.Kind == "same_name" && !first)
                    return (Texts.Get("lua_tag_nostart") + Texts.Get("lp_colon") + Texts.F("lp_same_name", ById(i.Mods[0])?.Name ?? "?"), "Yellow");
                if (i.Kind == "pack_alone" && first) return (Texts.Get("lua_tag_pack_off") + Texts.Get("lp_colon") + Texts.F("lp_pack_alone", lu.PackOfName), "Yellow");
            }
            if (lu.LastState == "failed" || lu.LastState == "no_compile") return (Texts.Get("lua_tag_failed"), "Red");
            // a mod that uses MDL3's audio or data: whether MDL3 runs beside the loader in effect
            if (lu.UsesMdl3 && st.Lua.Running != "mdl3")
                return (Texts.Get(st.Mods.Any(x => x.LoaderKind == "mdl3" && st.LoaderHelpers.Contains(x.Id)) ? "lp_now_mdl3_guest" : "lp_now_no_mdl3"), "Blue");
            if (lu.Mdl3Chef) return (Texts.Get("lp_now_mdl3_chef"), "Blue");
            return (Texts.Get(lu.Guest ? "lp_now_guest" : lu.ChefRuns ? "lp_now_chef_runs" : lu.Mdl3Runs ? "lp_now_mdl3_runs" : "lp_now_starts"), "Blue");
        }

        /// <summary>Whether an MDL3 of the library runs the mods made for Chef Shared Loader (Loaders.Mdl3TakesChef).</summary>
        bool Mdl3TakesChef() => st.Mods.Any(x => x.LoaderKind == "mdl3" && Loaders.Mdl3TakesChef(x.Lu?.LoaderVersion));

        // the loaders in the order the compatibility table pairs them
        static readonly string[] CompatKinds = { "bml", "chef", "mdl3", "bsl" };

        /// <summary>Which of the library's loaders can be used together, a line for each pair: whether,
        /// how and under what conditions (the user, 2026-11-01: no basis column).
        /// Only the loaders the library has (the user, 2026-10-26); null with fewer than two kinds.</summary>
        FrameworkElement CompatTable(List<ModRow> loaders)
        {
            var byKind = CompatKinds.Select(k => loaders.LastOrDefault(x => x.LoaderKind == k)).Where(x => x != null).ToList();
            if (byKind.Count < 2) return null;
            var table = new StackPanel();
            Grid Row(bool head)
            {
                var g = new Grid { Margin = new Thickness(12, head ? 6 : 8, 12, head ? 6 : 8) };
                foreach (var w in new[] { 220.0, 150, 0 })
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
                return g;
            }
            void Cell(Grid g, UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }
            FrameworkElement Text(string t, string colour)
            {
                var x = Selectable.Text(t, 13, colour);
                x.TextWrapping = TextWrapping.Wrap;
                x.VerticalAlignment = VerticalAlignment.Top;
                x.Margin = new Thickness(0, 0, 12, 0);
                return x;
            }
            var headRow = Row(true);
            foreach (var (key, col) in new[] { ("lp_compat_col_pair", 0), ("lp_compat_col_state", 1), ("lp_compat_col_how", 2) })
                Cell(headRow, new TextBlock { Text = Texts.Get(key), Foreground = B("Muted"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0) }, col);
            table.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Background = B("Ground900"), Child = headRow });

            string Name(ModRow x) => LoaderLabel(x.LoaderKind, x.Lu?.LoaderVersion) ?? x.Name;
            for (int i = 0; i < byKind.Count; i++)
                for (int j = i + 1; j < byKind.Count; j++)
                {
                    ModRow a = byKind[i], b = byKind[j];
                    var pair = a.LoaderKind + "_" + b.LoaderKind;
                    // MDL3 3.0.0-dev.22 or later runs the mods made for Chef in its place
                    if (pair == "chef_mdl3" && Loaders.Mdl3TakesChef(b.Lu?.LoaderVersion)) pair = "chef_mdl3_takes";
                    bool together = pair == "bml_chef" || pair == "bml_mdl3";
                    bool now = together && a.Id == st.LoaderChoice && st.LoaderHelpers.Contains(b.Id);
                    var state = now ? "lp_compat_now" : together ? "lp_compat_together" : pair == "chef_mdl3" ? "lp_compat_one" : "lp_compat_covered";
                    var how = new StackPanel();
                    how.Children.Add(Text(Texts.Get("lp_compat_" + pair), "Ink"));
                    // a Boot Mod Loader too old for the other to come along
                    var floor = pair == "bml_chef" ? Loaders.FirstHandOff : pair == "bml_mdl3" ? GuestBoots.FirstHost : null;
                    if (floor != null && (!Version.TryParse(a.Lu?.LoaderVersion ?? "", out var v) || v < floor))
                    {
                        var warn = Text(Texts.F("lp_compat_version", "Boot Mod Loader", a.Lu?.LoaderVersion ?? "?", floor.ToString(3)), "Yellow");
                        warn.Margin = new Thickness(0, 4, 12, 0);
                        how.Children.Add(warn);
                    }
                    var row = Row(false);
                    Cell(row, Text(Name(a) + "\n+ " + Name(b), "Ink"), 0);
                    Cell(row, Text(Texts.Get(state), together ? "Blue" : "Muted"), 1);
                    Cell(row, how, 2);
                    table.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Child = row });
                }
            return new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1, 1, 1, 0), Background = B("Ground700"), Child = table, Margin = new Thickness(0, 0, 12, 0) };
        }

        // the table's columns: switch, mod, what starts it, under the loader in effect, last session
        static readonly double[] LuaColumns = { 52, 0, 180, 380, 150 };

        /// <summary>One line of the Lua mods' table (null: its head).</summary>
        FrameworkElement LuaRow(ModRow m)
        {
            var grid = new Grid { Margin = new Thickness(12, m == null ? 6 : 8, 12, m == null ? 6 : 8) };
            // the last session is Boot Mod Loader's log: its column only while that is the loader in
            // effect, else it would be another loader's session
            bool session = st.Lua.Running == "bml";
            for (int c = 0; c < LuaColumns.Length; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = LuaColumns[c] == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(c == 4 && !session ? 0 : LuaColumns[c]),
                });
            void Cell(UIElement e, int col) { Grid.SetColumn(e, col); grid.Children.Add(e); }
            TextBlock Text(string t, string colour, double size = 13) => new TextBlock
            {
                Text = t, Foreground = B(colour), FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            };
            if (m == null)
            {
                Cell(Text(Texts.Get("lp_col_name"), "Muted", 12), 1);
                Cell(Text(Texts.Get("lp_col_needs"), "Muted", 12), 2);
                Cell(Text(Texts.Get("lp_col_now"), "Muted", 12), 3);
                if (session) Cell(Text(Texts.Get("lp_col_last"), "Muted", 12), 4);
                foreach (TextBlock t in grid.Children) t.FontWeight = FontWeights.SemiBold;
                if (session) ((FrameworkElement)grid.Children[grid.Children.Count - 1]).ToolTip = Texts.Get("lp_col_last_tip");
                return new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Background = B("Ground900"), Child = grid };
            }
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = m.Enabled, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = m.Name };
            sw.Click += (s, e) => { bool turn = sw.IsChecked == true; Change(m.Id, c => c.Enabled = turn, turn ? "enable" : "disable", m.Id); };
            Cell(sw, 0);
            Cell(ModLink(m, double.NaN), 1);
            var lu = m.Lu;
            Cell(Text(lu?.Needs == "chef" ? Texts.Get(lu.NeedsWhy != "name" && Mdl3TakesChef() ? "lp_needs_chef_mdl3" : "lp_needs_chef") : lu?.Needs == "any" ? Texts.Get("lp_needs_any")
                    : lu?.Boot != null ? Texts.Get("lp_needs_boot") : "—", "Muted"), 2);
            var (now, colour) = NowOf(m);
            var nowText = Selectable.Text(now, 13, colour);
            nowText.VerticalAlignment = VerticalAlignment.Center;
            nowText.Margin = new Thickness(0, 0, 12, 0);
            Cell(nowText, 3);
            string last = "—", lastColour = "Muted";
            if (lu?.LastState == "loaded") last = Texts.F("lp_last_loaded", lu.LastMs);
            else if (lu?.LastState == "failed") { last = Texts.Get("lp_last_failed"); lastColour = "Red"; }
            else if (lu?.LastState == "no_compile") { last = Texts.Get("lp_last_nocompile"); lastColour = "Red"; }
            var lastText = Text(last, lastColour, 12);
            if (lu?.LastWhy != null) lastText.ToolTip = lu.LastWhy.Trim();
            if (session) Cell(lastText, 4);
            return new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid, Opacity = m.Enabled ? 1 : 0.7 };
        }

        /// <summary>A mod's name that opens it on the deploy page.</summary>
        FrameworkElement ModLink(ModRow m, double width)
        {
            var t = new TextBlock
            {
                Text = m.Name, Cursor = Cursors.Hand, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0), ToolTip = Texts.Get("lp_open_mod"),
            };
            if (!double.IsNaN(width)) t.Width = width;
            t.MouseEnter += (s, e) => t.TextDecorations = TextDecorations.Underline;
            t.MouseLeave += (s, e) => t.TextDecorations = null;
            var id = m.Id;
            t.MouseLeftButtonUp += (s, e) => ShowMod(id);
            return t;
        }

        /// <summary>A mod chosen and shown on the deploy page, its details open.</summary>
        void ShowMod(string id)
        {
            selectedId = id;
            ShowPage("deploy");
            if (groupView) RevealSelected();
            RenderNav();
            RenderList();
            RenderDetail();
            Dispatcher.BeginInvoke(new Action(() => { if (rows.TryGetValue(id, out var r)) r.Row.BringIntoView(); }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ---------------------------------------------------------------- the logs

        /// <summary>The loaders' logs on this page: the library's loaders first, the one in effect at first.</summary>
        LogView luaLog;

        LogView LuaLog() => luaLog ?? (luaLog = new LogView
        {
            Group = "lualog", Height = 620,
            Kinds = () => st.Mods.Where(x => x.Loader && x.LoaderKind != null).Select(x => x.LoaderKind).Distinct()
                            .Concat(new[] { "bml", "chef", "bsl", "mdl3" }).Distinct().ToList(),
            First = () => st.Lua?.Running ?? ById(st.LoaderChoice)?.LoaderKind,
        });

        /// <summary>A loader's log on show, scrolled to.</summary>
        void ShowLuaLog(string kind)
        {
            var v = LuaLog();
            v.Kind = kind;
            v.File = null;
            ShowLuaTab("logs");
        }
    }
}
