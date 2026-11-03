using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Hd2Mod;

namespace QuickDeploy
{
    /// <summary>The mods as Finder's column view of the player's groups (the user, 2026-10-27):
    /// one column per level, up to Groups.Depth deep, each holding the groups and the mods inside
    /// the group chosen in the column before it. A group looks like a folder but is only a name
    /// in the library, so sorting mods moves no file and changes no deploy; the load order stays
    /// one click away (the other view).</summary>
    public partial class MainWindow
    {
        // the groups shown, else the load order; kept in gui.json
        bool groupView = true;
        // the groups opened, one per column after the first
        readonly List<string> groupPath = new List<string>();
        // the column the arrow keys work in
        int groupCol;
        // a group just made, opened once the state that has it arrives
        (string Parent, string Name)? openWhenMade;
        // the least a column may be: more levels than fit at this width scroll sideways
        const double GroupColWidth = 250;

        // a top group's colour, its groups inside wear it too
        static readonly Color[] GroupColours =
        {
            Color.FromRgb(0xFF, 0xD2, 0x3F), Color.FromRgb(0x5A, 0xAA, 0xFF), Color.FromRgb(0x4C, 0xC3, 0x8A),
            Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0xF2, 0xA6, 0x5A), Color.FromRgb(0x4F, 0xD1, 0xC5),
        };

        /// <summary>Where a dragged mod or group would go: a group, or the top (null).</summary>
        class GroupDrop
        {
            public string Group;
            // a column's: the state it was drawn from, what it shows apart from what is chosen,
            // and its items, so that a click restyles them instead of drawing it again
            public State St;
            public string Sig;
            public List<Shell> Items;
        }

        /// <summary>An item of a column (a folder or a mod) and how it is drawn as chosen.</summary>
        class Shell
        {
            public Border Item, Edge;
            public bool IsGroup, Chosen;
            public string Id;
        }

        void LoadView()
        {
            try { groupView = Json.Str(Store.Load("gui.json"), "list_view") != "order"; }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is FormatException) { }
        }

        void SaveView()
        {
            try
            {
                var d = Store.Load("gui.json");
                d["list_view"] = groupView ? "groups" : "order";
                Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is FormatException) { }
        }

        void View_Click(object sender, RoutedEventArgs e)
        {
            bool want = sender == ViewGroups;
            if (want == groupView) { ShowView(); return; }
            groupView = want;
            SaveView();
            if (groupView) RevealSelected();
            else if (selectedId == null) selectedId = Seen().FirstOrDefault()?.Id;
            RenderList();
            RenderDetail();
        }

        /// <summary>The switch and which of the two is on show.</summary>
        void ShowView()
        {
            ViewGroups.IsChecked = groupView;
            ViewOrder.IsChecked = !groupView;
            // the conflicts are a view of their own in the list's place (ConflictView): groups or
            // order mean nothing there, so their switch is not shown
            bool conflicts = filter == "conflict";
            ((FrameworkElement)((FrameworkElement)ViewGroups.Parent).Parent).Visibility = conflicts ? Visibility.Collapsed : Visibility.Visible;
            ListScroll.Visibility = groupView && !conflicts ? Visibility.Collapsed : Visibility.Visible;
            GroupScroll.Visibility = groupView && !conflicts ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The groups of the mod chosen in the load order, opened.</summary>
        void RevealSelected()
        {
            var m = st?.Mods.FirstOrDefault(x => x.Id == selectedId);
            groupPath.Clear();
            if (m?.Group != null) groupPath.AddRange(PathTo(m.Group).Select(g => g.Id));
            groupCol = groupPath.Count;
        }

        GroupRow GroupOf(string id) => id == null ? null : st.Groups.FirstOrDefault(g => g.Id == id);

        List<GroupRow> PathTo(string id)
        {
            var path = new List<GroupRow>();
            for (var g = GroupOf(id); g != null && path.Count <= st.Groups.Count; g = GroupOf(g.Parent)) path.Insert(0, g);
            return path;
        }

        int LevelOf(string id) => PathTo(id).Count;

        Color ColourOf(string id)
        {
            var top = PathTo(id).FirstOrDefault();
            int i = top == null ? 0 : st.Groups.Where(g => g.Parent == null).ToList().FindIndex(g => g.Id == top.Id);
            return GroupColours[Math.Max(0, i) % GroupColours.Length];
        }

        /// <summary>The mods shown (the filters and the search hold here too), translation packs
        /// left to the mod they translate, as in the load order.</summary>
        List<ModRow> GroupShown()
        {
            var shown = st.Mods.Where(Shown).ToList();
            var ids = new HashSet<string>(shown.Select(x => x.Id));
            return shown.Where(x => x.Lu?.PackOf == null || !ids.Contains(x.Lu.PackOf)).ToList();
        }

        // HD2QD_OPEN_GROUP=Looks/Armour (pictures only): that group opened and chosen at the start
        string openAtStart = Environment.GetEnvironmentVariable("HD2QD_OPEN_GROUP");

        void RenderGroups()
        {
            if (openAtStart != null && st.Groups.Count > 0)
            {
                groupPath.Clear();
                string parent = null;
                foreach (var name in openAtStart.Split('/'))
                {
                    var g = st.Groups.FirstOrDefault(x => x.Parent == parent && x.Name == name);
                    if (g == null) break;
                    groupPath.Add(g.Id);
                    parent = g.Id;
                }
                selectedId = null;
                groupCol = Math.Max(0, groupPath.Count - 1);
                openAtStart = null;
                RenderDetail();
            }
            var shown = GroupShown();
            // narrowed: a filter or the search is on, so a group shows only while it holds a mod that passes
            bool narrowed = shown.Count < st.Mods.Count(m => InScope(m) && (m.Lu?.PackOf == null || !st.Mods.Any(x => x.Id == m.Lu.PackOf && InScope(x))));
            // every group's mods, its groups inside counted
            var under = st.Groups.ToDictionary(g => g.Id, g => new List<ModRow>());
            foreach (var m in shown)
                foreach (var g in PathTo(m.Group)) under[g.Id].Add(m);
            // a group hides only when it has mods and the filter passes none of them: an empty one,
            // a group just made among them, always shows (a remembered filter such as labels left
            // out hid them all, the user, 2026-10-27)
            var holding = new HashSet<string>(st.Mods.Where(InScope).SelectMany(m => PathTo(m.Group)).Select(g => g.Id));
            bool Visible(GroupRow g) => !narrowed || under[g.Id].Count > 0 || !holding.Contains(g.Id);

            if (openWhenMade != null)
            {
                var made = st.Groups.FirstOrDefault(g => g.Parent == openWhenMade.Value.Parent && g.Name == openWhenMade.Value.Name);
                if (made != null)
                {
                    int at = made.Parent == null ? 0 : groupPath.IndexOf(made.Parent) + 1;
                    if (made.Parent == null || at > 0)
                    {
                        groupPath.RemoveRange(at, groupPath.Count - at);
                        groupPath.Add(made.Id);
                        selectedId = null;
                        groupCol = at;
                    }
                    openWhenMade = null;
                }
            }
            // the path holds while each group in it is still there, inside the one before, and shown
            for (int i = 0; i < groupPath.Count; i++)
            {
                var g = GroupOf(groupPath[i]);
                if (g == null || g.Parent != (i == 0 ? null : groupPath[i - 1]) || !Visible(g)) { groupPath.RemoveRange(i, groupPath.Count - i); break; }
            }
            groupCol = Math.Min(groupCol, groupPath.Count);
            // mods taken together live in the last column, the one whose mods can be chosen
            string here = groupPath.Count == 0 ? null : groupPath[groupPath.Count - 1];
            picked.RemoveWhere(id => !shown.Any(m => m.Id == id && m.Group == here));

            // where each column was scrolled to, kept through the redraw: a click redraws them all
            // and every list went back to its top (the user, 2026-10-27)
            // (columns drawn a moment ago and not scrolled back yet say 0: what was kept stands)
            if (!restoring)
            {
                foreach (var old in GroupCols.Children.OfType<Border>())
                    if (old.Tag is GroupDrop d && old.Child is ScrollViewer sv) colOffsets[d.Group ?? ""] = sv.VerticalOffset;
                hadX = GroupScroll.HorizontalOffset;
            }
            int hadCols = GroupCols.ColumnDefinitions.Count;
            var parents = new List<string> { null };
            parents.AddRange(groupPath);
            // a column that shows the same as before stays as it is, only what is chosen in it
            // restyled: a click drew every column again, and each list flashed back to its top
            // (the user, 2026-11-03)
            var had = GroupCols.Children.OfType<Border>().ToList();
            var want = new List<Border>();
            for (int i = 0; i < parents.Count; i++)
            {
                var sig = ColumnSig(parents[i], shown, under, Visible, narrowed);
                if (i < had.Count && had[i].Tag is GroupDrop d && d.St == st && d.Group == parents[i] && d.Sig == sig)
                {
                    Restyle(d, i);
                    want.Add(had[i]);
                }
                else want.Add(GroupColumn(i, parents[i], shown, under, Visible, narrowed, sig));
            }
            for (int i = 0; i < want.Count; i++)
            {
                Grid.SetColumn(want[i], i);
                if (i < GroupCols.Children.Count && GroupCols.Children[i] == want[i]) continue;
                if (i < GroupCols.Children.Count) GroupCols.Children.RemoveAt(i);
                GroupCols.Children.Insert(i, want[i]);
            }
            while (GroupCols.Children.Count > want.Count) GroupCols.Children.RemoveAt(GroupCols.Children.Count - 1);
            while (GroupCols.ColumnDefinitions.Count < want.Count) GroupCols.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            while (GroupCols.ColumnDefinitions.Count > want.Count) GroupCols.ColumnDefinitions.RemoveAt(GroupCols.ColumnDefinitions.Count - 1);
            FitColumns();
            // sideways: a column just opened comes into view, as Finder scrolls to it; otherwise
            // the strip stays where it was
            restoring = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                restoring = false;
                FitColumns();       // laid out now: the first drawing had no width to share yet
                foreach (var c in GroupCols.Children.OfType<Border>())
                    if (c.Tag is GroupDrop d && c.Child is ScrollViewer sv && colOffsets.TryGetValue(d.Group ?? "", out var y)) sv.ScrollToVerticalOffset(y);
                if (parents.Count > hadCols) GroupScroll.ScrollToRightEnd();
                else GroupScroll.ScrollToHorizontalOffset(hadX);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>The columns share the width (the user, 2026-10-27): one level is the whole
        /// width, two a half each, and so on down to GroupColWidth; past that they keep it and
        /// the strip scrolls sideways.</summary>
        void FitColumns()
        {
            int n = GroupCols.Children.Count;
            double room = Math.Floor(GroupScroll.ViewportWidth);
            if (n == 0 || room <= 0) return;           // not laid out yet: SizeChanged comes back
            double w = Math.Max(GroupColWidth, Math.Floor(room / n));
            foreach (var c in GroupCols.Children.OfType<Border>()) c.Width = w;
        }

        // each column's scroll, by the group it shows ("" the top), and the strip's; restoring
        // while the columns just drawn are not scrolled back yet
        readonly Dictionary<string, double> colOffsets = new Dictionary<string, double>();
        double hadX;
        bool restoring;

        /// <summary>What a column shows apart from what is chosen in it: drawn from the same
        /// state, a column with the same signature looks the same.</summary>
        string ColumnSig(string parent, List<ModRow> shown, Dictionary<string, List<ModRow>> under, Func<GroupRow, bool> visible, bool narrowed)
        {
            var sb = new System.Text.StringBuilder();
            void Add(object x) => sb.Append(x).Append('\u001f');
            Add(parent); Add(narrowed);
            foreach (var g in st.Groups.Where(g => g.Parent == parent && visible(g))) { Add(g.Id); Add(g.Name); Add(under[g.Id].Count); Add(under[g.Id].Count(m => m.Enabled)); }
            foreach (var m in shown.Where(m => m.Group == parent))
            {
                Add(m.Id);
                foreach (var p in PacksOf(m)) { Add(p.Id); Add(p.Id == selectedId); }      // a pack line shows itself chosen
            }
            return sb.ToString();
        }

        /// <summary>A column kept: its items drawn chosen or not, as they are now.</summary>
        void Restyle(GroupDrop d, int index)
        {
            bool active = index == groupCol;
            foreach (var sh in d.Items)
                ShowChosen(sh, sh.IsGroup ? index < groupPath.Count && groupPath[index] == sh.Id
                                     : (sh.Id == selectedId || picked.Contains(sh.Id)) && index == groupPath.Count, active);
        }

        void ShowChosen(Shell sh, bool chosen, bool active)
        {
            sh.Chosen = chosen;
            sh.Edge.BorderBrush = chosen ? B(active ? "Yellow" : "Muted") : Brushes.Transparent;
            sh.Item.Background = chosen ? (active ? ChosenHere : B("Ground900")) : sh.Item.IsMouseOver ? B("Band") : Brushes.Transparent;
        }

        // the items of the column being drawn (GroupColumn), for its GroupDrop
        List<Shell> shells;

        Border GroupColumn(int index, string parent, List<ModRow> shown, Dictionary<string, List<ModRow>> under,
                                     Func<GroupRow, bool> visible, bool narrowed, string sig)
        {
            bool active = index == groupCol;
            var list = new StackPanel { Margin = new Thickness(0, 6, 0, 12) };
            shells = new List<Shell>();
            var col = new Border
            {
                // every column one width, the room after them empty, as Finder: the last one
                // filling the rest jumped between narrow and wide (the user, 2026-10-27)
                BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 1, 0), Background = Brushes.Transparent,
                Tag = new GroupDrop { Group = parent, St = st, Sig = sig, Items = shells },
                Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = list },
            };
            col.Width = GroupColWidth;
            col.PreviewMouseDown += (s, e) => groupCol = index;
            // a click on the empty part of a column opens nothing further: what it holds stays
            col.MouseLeftButtonUp += (s, e) =>
            {
                if (e.Handled || gDragging) return;
                groupPath.RemoveRange(index, groupPath.Count - index);
                selectedId = null;
                picked.Clear();
                groupCol = index;
                RenderGroups();
                RenderDetail();
            };
            col.ContextMenu = ColumnMenu(parent);

            var groups = st.Groups.Where(g => g.Parent == parent && visible(g)).ToList();
            var mods = shown.Where(m => m.Group == parent).ToList();
            if (groups.Count == 0 && mods.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = Texts.Get(narrowed ? "g_none" : "g_empty"), FontSize = 13, Foreground = B("Muted"),
                    Margin = new Thickness(14, 8, 14, 8), TextWrapping = TextWrapping.Wrap,
                });
            foreach (var g in groups) list.Children.Add(FolderItem(g, index, active, under[g.Id]));
            foreach (var m in mods)
            {
                list.Children.Add(ModItem(m, index, active));
                // its translation packs under it, as in the load order (PackLine)
                foreach (var p in PacksOf(m)) list.Children.Add(PackLine(p, true));
            }
            if (parent == null || LevelOf(parent) < Groups.Depth)
            {
                var add = new Button { Template = Plain(), Cursor = Cursors.Hand, FocusVisualStyle = null, HorizontalContentAlignment = HorizontalAlignment.Left };
                add.Content = new TextBlock { Text = Texts.Get("g_new"), FontSize = 13, Foreground = B("Muted"), Margin = new Thickness(14, 10, 14, 10) };
                add.Click += (s, e) => NewGroup(parent);
                list.Children.Add(add);
            }
            shells = null;
            return col;
        }

        /// <summary>The look of a chosen item: yellow in the column the keys work in, grey on the
        /// path behind it (Finder: blue and grey).</summary>
        static readonly Brush ChosenHere = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xD2, 0x3F));

        Border ItemShell(bool chosen, bool active, object tag, bool isGroup, string id)
        {
            var edge = new Border { BorderThickness = new Thickness(3, 0, 0, 0) };
            var item = new Border { Tag = tag, Child = edge, Cursor = Cursors.Hand };
            var sh = new Shell { Item = item, Edge = edge, IsGroup = isGroup, Id = id };
            ShowChosen(sh, chosen, active);
            shells?.Add(sh);
            item.MouseEnter += (s, e) => { if (!sh.Chosen) item.Background = B("Band"); };
            item.MouseLeave += (s, e) => { if (!sh.Chosen) item.Background = Brushes.Transparent; };
            return item;
        }

        /// <summary>A folder in the group's colour, in the window's own manner: square corners, its
        /// tab cut at an angle, a corner of its front clipped, a dark line across it. The rounded one
        /// was close to macOS's (the user, 2026-10-27).</summary>
        static FrameworkElement Folder(Color colour, double width)
        {
            var c = new Canvas { Width = 40, Height = 34 };
            // the back, with its tab cut at 45 degrees
            c.Children.Add(new Path { Data = Geometry.Parse("M2,4 H15 L19,8 H38 V30 H2 Z"), Fill = new SolidColorBrush(colour) { Opacity = 0.5 } });
            // the front, its lower right corner clipped
            c.Children.Add(new Path { Data = Geometry.Parse("M2,12 H38 V27 L35,32 H2 Z"), Fill = new SolidColorBrush(colour) });
            // a line across the front, the window's tape colour on it
            c.Children.Add(new Path { Data = Geometry.Parse("M6,17 H20"), Stroke = (Brush)Application.Current.Resources["TapeInk"], StrokeThickness = 2, Opacity = 0.55 });
            return new Viewbox { Width = width, Height = width * 34 / 40, Child = c, VerticalAlignment = VerticalAlignment.Center };
        }

        static Path Chevron() => new Path
        {
            Data = Geometry.Parse("M0,0 L5,5 L0,10"), Stroke = B("Muted"), StrokeThickness = 1.6, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };

        Border FolderItem(GroupRow g, int index, bool active, List<ModRow> mods)
        {
            bool chosen = index < groupPath.Count && groupPath[index] == g.Id;
            var item = ItemShell(chosen, active, new GroupDrop { Group = g.Id }, true, g.Id);
            var line = new DockPanel { MinHeight = 52, Margin = new Thickness(11, 6, 10, 6) };
            var pic = Folder(ColourOf(g.Id), 40);
            DockPanel.SetDock(pic, Dock.Left);
            line.Children.Add(pic);
            var chev = Chevron();
            DockPanel.SetDock(chev, Dock.Right);
            line.Children.Add(chev);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            names.Children.Add(new TextBlock { Text = g.Name, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = g.Name });
            var meta = new WrapPanel();
            int on = mods.Count(m => m.Enabled);
            meta.Children.Add(new TextBlock
            {
                Text = on < mods.Count ? Texts.F("g_mods_off", mods.Count, on) : Texts.F("g_mods", mods.Count),
                FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            if (mods.Any(m => m.Enabled && m.Conflicts > 0)) meta.Children.Add(ClashTag(null));
            names.Children.Add(meta);
            line.Children.Add(names);
            ((Border)item.Child).Child = line;
            item.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (gDragging) return;
                groupPath.RemoveRange(index, groupPath.Count - index);
                groupPath.Add(g.Id);
                selectedId = null;
                picked.Clear();
                groupCol = index;
                RenderGroups();
                RenderDetail();
            };
            item.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) { e.Handled = true; RenameGroup(g); return; }
                gDragFrom = ("group", g.Id, g.Name, e.GetPosition(GroupScroll));
            };
            item.ContextMenu = FolderMenu(g);
            return item;
        }

        Border ClashTag(string tip) => new Border
        {
            BorderBrush = B("Yellow"), BorderThickness = new Thickness(1), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 2, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, ToolTip = tip,
            Child = new TextBlock { Text = Texts.Get("g_clash"), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("Yellow") },
        };

        Border ModItem(ModRow m, int index, bool active)
        {
            bool chosen = (m.Id == selectedId || picked.Contains(m.Id)) && index == groupPath.Count;
            var item = ItemShell(chosen, active, null, false, m.Id);
            var line = new DockPanel { MinHeight = 52, Margin = new Thickness(11, 6, 10, 6) };
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = m.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = m.Name };
            sw.Click += (s, e) =>
            {
                bool on = sw.IsChecked == true;
                if (on && st.ProfilesOn && !m.InProfile) { Run("profile-add", m.Id); return; }
                Change(m.Id, c => c.Enabled = on, on ? "enable" : "disable", m.Id);
            };
            if (st.ProfilesOn && !m.InProfile) { item.Opacity = 0.55; sw.ToolTip = Texts.Get("pf_outside"); }
            DockPanel.SetDock(sw, Dock.Left);
            line.Children.Add(sw);
            var pic = Picture(m.Icon, GlyphMod, 40, m.Enabled ? "Blue" : "Muted", 24);
            pic.Opacity = m.Enabled ? 1 : 0.55;
            DockPanel.SetDock(pic, Dock.Left);
            line.Children.Add(pic);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = m.Enabled ? 1 : 0.55 };
            names.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = m.Name });
            var meta = new WrapPanel();
            if (m.Options.Count > 0)
                meta.Children.Add(new TextBlock { Text = Texts.F("opts_some", m.Chosen, m.Choices), FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            foreach (var t in m.Tags)
                meta.Children.Add(new Border
                {
                    BorderBrush = B("Ink"), BorderThickness = new Thickness(1), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 2, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.Get("tag_tip"),
                    Child = new TextBlock { Text = t, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("Ink") },
                });
            if (m.Lua)
                meta.Children.Add(new Border
                {
                    BorderBrush = B("Blue"), BorderThickness = new Thickness(1), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 2, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Texts.Get("lua_tip"),
                    Child = new TextBlock { Text = "LUA", FontFamily = Mono, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("Blue") },
                });
            if (m.Enabled && m.Conflicts > 0) meta.Children.Add(ClashTag(Texts.F("row_clash", m.Conflicts)));
            foreach (var (text, tip, colour) in NotesOf(m))
                meta.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = B(colour), Margin = new Thickness(0, 2, 6, 0), ToolTip = tip });
            if (m.Error != null) meta.Children.Add(new TextBlock { Text = Texts.Get("broken"), FontSize = 12, Foreground = B("Yellow"), Margin = new Thickness(0, 2, 0, 0) });
            if (meta.Children.Count > 0) names.Children.Add(meta);
            line.Children.Add(names);
            ((Border)item.Child).Child = line;
            item.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (gDragging) return;
                groupPath.RemoveRange(index, groupPath.Count - index);
                groupCol = index;
                Take(m);
                RenderGroups();
            };
            item.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) { e.Handled = true; RenameMod(m); return; }
                gDragFrom = ("mod", m.Id, m.Name, e.GetPosition(GroupScroll));
            };
            var menu = Menu(m);
            // a group made here: with no group yet the column is long and its button far down
            // (the user, 2026-10-27)
            if (m.Group == null || LevelOf(m.Group) < Groups.Depth)
            {
                var make = new MenuItem { Header = Texts.Get("g_new_top") };
                make.Click += (s, e) => NewGroup(m.Group);
                menu.Items.Insert(0, make);
                menu.Items.Insert(1, new Separator());
            }
            item.ContextMenu = menu;
            return item;
        }

        ContextMenu ColumnMenu(string parent)
        {
            var menu = new ContextMenu();
            if (parent == null || LevelOf(parent) < Groups.Depth)
            {
                var it = new MenuItem { Header = Texts.Get(parent == null ? "g_new_top" : "g_new_in") };
                it.Click += (s, e) => NewGroup(parent);
                menu.Items.Add(it);
            }
            // inside any group, at any level (the user, 2026-10-27)
            var inside = GroupPicker(Texts.Get("g_new_inside"), null, g => g != null && LevelOf(g) < Groups.Depth, NewGroup, null, "g_new_here");
            if (inside.Items.Count > 0) menu.Items.Add(inside);
            return menu.Items.Count > 0 ? menu : null;
        }

        /// <summary>The groups as a menu, one level per submenu: pick(group) for where to go (null:
        /// the top), allowed(group) for where it may, current ticked. A group with groups inside
        /// opens; its first line takes the group itself (the text hereKey, "{0}" its name).
        /// make(parent), when given, adds "a new group here" to each level that may hold one.</summary>
        MenuItem GroupPicker(string header, string current, Func<string, bool> allowed, Action<string> pick, Action<string> make, string hereKey, bool withTop = false)
        {
            // a line with lines of its own needs the style that opens them (App.xaml: SubMenuItem)
            var sub = (Style)FindResource("SubMenuItem");
            var root = new MenuItem { Header = header, Style = sub };
            MenuItem Leaf(string text, bool ticked, Action act)
            {
                var it = new MenuItem { Header = text, IsChecked = ticked, IsEnabled = act != null };
                if (act != null) it.Click += (s, e) => act();
                return it;
            }
            MenuItem Node(GroupRow g)
            {
                var kids = st.Groups.Where(x => x.Parent == g.Id).ToList();
                bool may = allowed(g.Id);
                bool canMake = make != null && LevelOf(g.Id) < Groups.Depth;
                if (kids.Count == 0 && !canMake) return Leaf(g.Name, current == g.Id, may ? () => pick(g.Id) : (Action)null);
                var mi = new MenuItem { Header = g.Name, Style = sub };
                mi.Items.Add(Leaf(Texts.F(hereKey, g.Name), current == g.Id, may ? () => pick(g.Id) : (Action)null));
                if (kids.Count > 0)
                {
                    mi.Items.Add(new Separator());
                    foreach (var k in kids) mi.Items.Add(Node(k));
                }
                if (canMake)
                {
                    mi.Items.Add(new Separator());
                    var add = new MenuItem { Header = Texts.F("g_new_in_named", g.Name) };
                    add.Click += (s, e) => make(g.Id);
                    mi.Items.Add(add);
                }
                return mi;
            }
            if (withTop) root.Items.Add(Leaf(Texts.Get("g_top_none"), current == null, allowed(null) ? () => pick(null) : (Action)null));
            var tops = st.Groups.Where(g => g.Parent == null).ToList();
            if (withTop && tops.Count > 0) root.Items.Add(new Separator());
            foreach (var g in tops) root.Items.Add(Node(g));
            if (make != null)
            {
                if (root.Items.Count > 0) root.Items.Add(new Separator());
                var add = new MenuItem { Header = Texts.Get("g_new_and_put") };
                add.Click += (s, e) => make(null);
                root.Items.Add(add);
            }
            return root;
        }

        string PathText(string id) => string.Join("/", PathTo(id).Select(g => g.Name));

        /// <summary>"Move to group" for mods (the ones taken with m): any group at any level, the
        /// top, or a group made there and then.</summary>
        MenuItem ModGroupMenu(List<ModRow> mods)
        {
            var ids = mods.Select(x => x.Id).ToList();
            var groups = mods.Select(x => x.Group).Distinct().ToList();
            // mods from several groups: none of them is ticked
            string current = groups.Count == 1 ? groups[0] : "several";
            void Put(string g) => Run(new[] { "group-put" }.Concat(ids).Concat(g == null ? new[] { "--top" } : new[] { "--in", g }).ToArray());
            void MakeAndPut(string parent) => AfterMenu(() =>
            {
                var name = Dialog.AskText(this, Texts.Get("g_rename_ask"), Texts.Get("g_create"), Texts.Get("g_new_name"))?.Trim();
                if (string.IsNullOrEmpty(name)) return;
                var made = parent == null ? new[] { "group-new", name } : new[] { "group-new", name, "--in", parent };
                var path = parent == null ? name : PathText(parent) + "/" + name;
                RunMany(new List<string[]> { made, new[] { "group-put" }.Concat(ids).Concat(new[] { "--in", path }).ToArray() });
            });
            return GroupPicker(Texts.Get("g_move_to"), current, g => true, Put, MakeAndPut, "g_put_here", true);
        }

        ContextMenu FolderMenu(GroupRow g)
        {
            var menu = new ContextMenu();
            var beside = new MenuItem { Header = Texts.Get("g_new_top") };
            beside.Click += (s, e) => NewGroup(g.Parent);
            menu.Items.Add(beside);
            if (LevelOf(g.Id) < Groups.Depth)
            {
                var inner = new MenuItem { Header = Texts.Get("g_new_in") };
                inner.Click += (s, e) => NewGroup(g.Id);
                menu.Items.Add(inner);
            }
            int below(string id) => st.Groups.Where(x => x.Parent == id).Select(x => 1 + below(x.Id)).DefaultIfEmpty(0).Max();
            bool May(string into) => into != g.Id && !(into != null && PathTo(into).Any(x => x.Id == g.Id)) &&
                                     (into == null ? 0 : LevelOf(into)) + 1 + below(g.Id) <= Groups.Depth;
            menu.Items.Add(GroupPicker(Texts.Get("g_move_to"), g.Parent, May,
                                       into => Run(into == null ? new[] { "group-move", g.Id, "--top" } : new[] { "group-move", g.Id, "--in", into }),
                                       null, "g_put_here", true));
            var rename = new MenuItem { Header = Texts.Get("g_rename") };
            rename.Click += (s, e) => RenameGroup(g);
            menu.Items.Add(rename);
            menu.Items.Add(new Separator());
            var del = new MenuItem { Header = Texts.Get("g_delete"), Foreground = B("Red") };
            del.Click += (s, e) => DeleteGroup(g);
            menu.Items.Add(del);
            return menu;
        }

        /// <summary>A question asked once the menu that called for it has closed: asked while it
        /// was closing, the first one came up half drawn (the user, 2026-10-27).</summary>
        void AfterMenu(Action act) => Dispatcher.BeginInvoke(act, System.Windows.Threading.DispatcherPriority.ContextIdle);

        void NewGroup(string parent) => AfterMenu(() => NewGroupNow(parent));

        void NewGroupNow(string parent)
        {
            var name = Dialog.AskText(this, Texts.Get("g_rename_ask"), Texts.Get("g_create"), Texts.Get("g_new_name"));
            name = name?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            openWhenMade = (parent, name);
            Run(parent == null ? new[] { "group-new", name } : new[] { "group-new", name, "--in", parent });
        }

        void RenameGroup(GroupRow g) => AfterMenu(() => RenameGroupNow(g));

        void RenameGroupNow(GroupRow g)
        {
            var name = Dialog.AskText(this, Texts.Get("g_rename_ask"), Texts.Get("rename_save"), g.Name);
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name == g.Name) return;
            Run("group-rename", g.Id, name);
        }

        void DeleteGroup(GroupRow g) => AfterMenu(() => DeleteGroupNow(g));

        void DeleteGroupNow(GroupRow g)
        {
            if (!Dialog.Ask(this, Texts.F("g_delete_ask", g.Name), Texts.Get("g_delete"))) return;
            Run("group-delete", g.Id);
        }

        /// <summary>A group as the details show it: its folder, what it holds, where it is, and
        /// its mods turned on or off at once.</summary>
        void GroupDetail(GroupRow g)
        {
            var mods = st.Mods.Where(m => InScope(m) && m.Group != null && PathTo(m.Group).Any(x => x.Id == g.Id)).ToList();
            var head = new DockPanel();
            var pic = Folder(ColourOf(g.Id), 72);
            DockPanel.SetDock(pic, Dock.Left);
            head.Children.Add(pic);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            var title = Selectable.Text(g.Name, 22);
            title.FontWeight = FontWeights.Bold;
            names.Children.Add(title);
            int on = mods.Count(m => m.Enabled);
            names.Children.Add(new TextBlock { Text = Texts.F("g_mods_off", mods.Count, on), FontSize = 13, Foreground = B("Muted"), Margin = new Thickness(0, 4, 0, 0) });
            head.Children.Add(names);
            Detail.Children.Add(head);

            var facts = new Grid { Margin = new Thickness(0, 20, 0, 0) };
            facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            facts.ColumnDefinitions.Add(new ColumnDefinition());
            void Fact(string key, string value)
            {
                int r = facts.RowDefinitions.Count;
                facts.RowDefinitions.Add(new RowDefinition());
                var k = new TextBlock { Text = Texts.Get(key), FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 3, 0, 3) };
                Grid.SetRow(k, r);
                facts.Children.Add(k);
                var v = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
                Grid.SetRow(v, r);
                Grid.SetColumn(v, 1);
                facts.Children.Add(v);
            }
            Fact("g_where", g.Parent == null ? Texts.Get("g_top") : string.Join(" › ", PathTo(g.Parent).Select(x => x.Name)));
            Fact("g_subs", st.Groups.Count(x => x.Parent == g.Id).ToString());
            Detail.Children.Add(facts);

            var acts = new WrapPanel { Margin = new Thickness(-8, 12, 0, 0) };
            Button Act(string key, Action act, string style = "Ghost")
            {
                var b = new Button { Content = Texts.Get(key), Style = (Style)FindResource(style), MinHeight = 32, Margin = new Thickness(8, 4, 0, 0) };
                b.Click += (s, e) => act();
                acts.Children.Add(b);
                return b;
            }
            Act("g_all_on", () => { foreach (var m in mods.Where(x => !x.Enabled)) Change(m.Id, c => c.Enabled = true, "enable", m.Id); }).IsEnabled = on < mods.Count;
            Act("g_all_off", () => { foreach (var m in mods.Where(x => x.Enabled)) Change(m.Id, c => c.Enabled = false, "disable", m.Id); }).IsEnabled = on > 0;
            Act("g_rename", () => RenameGroup(g));
            Act("g_delete", () => DeleteGroup(g), "Danger");
            Detail.Children.Add(acts);
            if (Texts.Hints) Detail.Children.Add(Small(Texts.Get("g_note"), new Thickness(0, 16, 0, 0)));
        }

        // ---------------------------------------------------------------- dragging into a group

        (string Kind, string Id, string Name, Point Start)? gDragFrom;
        bool gDragging;
        Border gTarget, gGhost;
        bool gHooked;

        /// <summary>The mouse on the columns: a press that moves becomes a drag of that mod (the
        /// mods taken with it) or group; let go over a folder or a column, it goes in there.
        /// Our own drag, as the load order's: WPF's DoDragDrop freezes the picture.</summary>
        void HookGroupDrag()
        {
            if (gHooked) return;
            gHooked = true;
            GroupScroll.PreviewMouseMove += (s, e) =>
            {
                if (gDragFrom == null) return;
                var at = e.GetPosition(GroupScroll);
                if (!gDragging)
                {
                    if (e.LeftButton != MouseButtonState.Pressed) { gDragFrom = null; return; }
                    if (Math.Abs(at.X - gDragFrom.Value.Start.X) < 6 && Math.Abs(at.Y - gDragFrom.Value.Start.Y) < 6) return;
                    StartGroupDrag();
                }
                DragTo(at, e.GetPosition(DragLayer));
                e.Handled = true;
            };
            GroupScroll.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (!gDragging) { gDragFrom = null; return; }
                e.Handled = true;
                FinishGroupDrag();
            };
            GroupScroll.LostMouseCapture += (s, e) => { if (gDragging) EndGroupDrag(); };
            PreviewKeyDown += (s, e) => { if (gDragging && e.Key == Key.Escape) { EndGroupDrag(); e.Handled = true; } };
            PreviewKeyDown += GroupKeys;
            PreviewKeyDown += SpacePreview;
            // a wheel tilted sideways, or a touchpad's sideways swipe (WM_MOUSEHWHEEL)
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == 0x020E && groupView && GroupScroll.IsVisible && GroupScroll.IsMouseOver)
                {
                    int delta = (short)((wp.ToInt64() >> 16) & 0xFFFF);
                    GroupScroll.ScrollToHorizontalOffset(GroupScroll.HorizontalOffset + delta);
                    handled = true;
                }
                return IntPtr.Zero;
            });
            GroupScroll.SizeChanged += (s, e) => FitColumns();
        }

        /// <summary>The drag begins: its picture first, then the mouse taken. Taking the mouse
        /// raises a move at once, which found no picture and closed the window (the user,
        /// 2026-10-27).</summary>
        void StartGroupDrag()
        {
            gGhost = new Border
            {
                Background = B("Ground900"), BorderBrush = B("Yellow"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6),
                Child = new TextBlock { Text = DragLabel(), FontWeight = FontWeights.SemiBold, Foreground = B("Ink") },
            };
            DragLayer.Children.Add(gGhost);
            gDragging = true;
            GroupScroll.CaptureMouse();
        }

        /// <summary>The picture by the pointer (at: on the columns; onLayer: on the drag layer),
        /// and the folder or column under it lit when the drag may go there.</summary>
        void DragTo(Point at, Point onLayer)
        {
            if (gGhost == null) return;
            Canvas.SetLeft(gGhost, onLayer.X + 14);
            Canvas.SetTop(gGhost, onLayer.Y + 10);
            Target(DropAt(at));
        }

        /// <summary>Let go: into the folder or column lit, if any.</summary>
        void FinishGroupDrag()
        {
            var to = gTarget?.Tag as GroupDrop;
            var from = gDragFrom.Value;
            EndGroupDrag();
            if (to != null) DropInto(from.Kind, from.Id, to.Group);
        }

        string DragLabel()
        {
            var from = gDragFrom.Value;
            if (from.Kind == "group") return from.Name;
            var m = st.Mods.FirstOrDefault(x => x.Id == from.Id);
            var many = m != null ? Taken(m) : new List<ModRow>();
            return many.Count > 1 ? Texts.F("many_drag", many.Count) : from.Name;
        }

        /// <summary>The folder or column under a point, when what is dragged may go there.</summary>
        Border DropAt(Point at)
        {
            var hit = GroupScroll.InputHitTest(at) as DependencyObject;
            for (var d = hit; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is Border b && b.Tag is GroupDrop drop) return Allowed(drop.Group) ? b : null;
            return null;
        }

        bool Allowed(string into)
        {
            var from = gDragFrom.Value;
            if (from.Kind == "mod") return st.Mods.FirstOrDefault(m => m.Id == from.Id)?.Group != into;
            if (into == from.Id || (into != null && PathTo(into).Any(g => g.Id == from.Id))) return false;
            if (GroupOf(from.Id)?.Parent == into) return false;
            int below(string id) => st.Groups.Where(g => g.Parent == id).Select(g => 1 + below(g.Id)).DefaultIfEmpty(0).Max();
            return (into == null ? 0 : LevelOf(into)) + 1 + below(from.Id) <= Groups.Depth;
        }

        void Target(Border b)
        {
            if (gTarget == b) return;
            if (gTarget != null) { gTarget.BorderBrush = gTarget.Tag is GroupDrop && gTarget.Child is ScrollViewer ? B("Grid") : Brushes.Transparent; gTarget.BorderThickness = TargetRest(gTarget); }
            gTarget = b;
            if (b != null) { b.BorderBrush = B("Yellow"); b.BorderThickness = new Thickness(1); }
        }

        Thickness TargetRest(Border b) => b.Child is ScrollViewer ? new Thickness(0, 0, 1, 0) : new Thickness(0);

        void EndGroupDrag()
        {
            Target(null);
            if (gGhost != null) DragLayer.Children.Remove(gGhost);
            gGhost = null;
            gDragging = false;
            gDragFrom = null;
            if (GroupScroll.IsMouseCaptured) GroupScroll.ReleaseMouseCapture();
        }

        void DropInto(string kind, string id, string group)
        {
            var where = group == null ? new[] { "--top" } : new[] { "--in", group };
            if (kind == "group") { Run(new[] { "group-move", id }.Concat(where).ToArray()); return; }
            var m = st.Mods.FirstOrDefault(x => x.Id == id);
            var ids = m != null ? Taken(m).Select(x => x.Id) : new[] { id };
            Run(new[] { "group-put" }.Concat(ids).Concat(where).ToArray());
        }

        // ---------------------------------------------------------------- a test from outside

        /// <summary>HD2QD_CLICK_TEST=groups (measuring only): the window made short, the first
        /// column scrolled down, a click on its last mod; then each column's width and scroll before
        /// and after, and the menus of that mod and of the first folder as indented text, go to
        /// HD2QD_WHEEL_OUT. Posted clicks do not reach WPF, so the click is raised here.</summary>
        void GroupTest()
        {
            var which = Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST");
            if (which == "groupdrag") { GroupDragTest(); return; }
            if (which == "groupdragtaken") { GroupDragTakenTest(); return; }
            if (which == "groupmenu") { GroupMenuTest(); return; }
            if (which != "groups") return;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                Height = 560;
                groupView = true;
                groupPath.Clear();
                selectedId = null;
                RenderList();
                UpdateLayout();
                var out_ = new System.Text.StringBuilder();
                Border First() => GroupCols.Children.OfType<Border>().First();
                ScrollViewer Sv() => (ScrollViewer)First().Child;
                Sv().ScrollToVerticalOffset(80);
                UpdateLayout();
                // the click a moment later, as a hand would: the redraw before it has settled
                var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                wait.Tick += (s1, e1) =>
                {
                wait.Stop();
                Sv().ScrollToVerticalOffset(80);
                UpdateLayout();
                out_.AppendLine("before width " + First().ActualWidth + " scroll " + Sv().VerticalOffset + " columns " + GroupCols.Children.Count);
                var items = ((StackPanel)Sv().Content).Children.OfType<Border>().ToList();
                var mod = items.LastOrDefault(b => b.Tag == null);
                var firstBefore = First();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                mod?.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                UpdateLayout();
                // the first frame after the click: a column drawn again showed its top here
                out_.AppendLine("at once " + clock.ElapsedMilliseconds + " ms, scroll " + Sv().VerticalOffset + ", column kept " + (First() == firstBefore));
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateLayout();
                    out_.AppendLine("after width " + First().ActualWidth + " scroll " + Sv().VerticalOffset + " columns " + GroupCols.Children.Count +
                                    " chosen " + (st.Mods.FirstOrDefault(m => m.Id == selectedId)?.Name ?? "-"));
                    void Dump(ItemsControl menu, int depth)
                    {
                        foreach (var x in menu.Items)
                        {
                            if (x is Separator) { out_.AppendLine(new string(' ', depth * 2) + "--"); continue; }
                            if (!(x is MenuItem mi)) continue;
                            out_.AppendLine(new string(' ', depth * 2) + mi.Header + (mi.IsChecked ? "  [x]" : "") + (mi.IsEnabled ? "" : "  (off)"));
                            Dump(mi, depth + 1);
                        }
                    }
                    var m = st.Mods.FirstOrDefault(x => x.Id == selectedId);
                    if (m != null) { out_.AppendLine("== menu of " + m.Name); Dump(ModGroupMenu(new List<ModRow> { m }), 1); }
                    var g = st.Groups.FirstOrDefault(x => x.Parent == null);
                    if (g != null) { out_.AppendLine("== menu of folder " + g.Name); Dump(FolderMenu(g), 1); }
                    out_.AppendLine("== menu of the top column");
                    var cm = ColumnMenu(null);
                    if (cm != null) Dump(cm, 1);
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path != null) System.IO.File.WriteAllText(path, out_.ToString());
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                };
                wait.Start();
            };
            t.Start();
        }

        /// <summary>HD2QD_CLICK_TEST=groupdrag: the last mod of the top column dragged onto its
        /// first folder, through the drag's own steps (a posted drag does not reach WPF); the
        /// folder lit and the group the mod ends in go to HD2QD_WHEEL_OUT.</summary>
        void GroupDragTest()
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                groupView = true;
                groupPath.Clear();
                RenderList();
                UpdateLayout();
                var first = GroupCols.Children.OfType<Border>().First();
                var items = ((StackPanel)((ScrollViewer)first.Child).Content).Children.OfType<Border>().ToList();
                var folder = items.First(b => b.Tag is GroupDrop);
                var modItem = items.Last(b => b.Tag == null);
                var mod = GroupShown().Last(m => m.Group == null);
                var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                var log = new System.Text.StringBuilder();
                gDragFrom = ("mod", mod.Id, mod.Name, modItem.TranslatePoint(new Point(20, 20), GroupScroll));
                StartGroupDrag();
                log.AppendLine("dragging " + gDragging + " ghost " + (gGhost != null) + " captured " + GroupScroll.IsMouseCaptured);
                var over = folder.TranslatePoint(new Point(60, 20), GroupScroll);
                DragTo(over, folder.TranslatePoint(new Point(60, 20), DragLayer));
                var target = ((GroupDrop)folder.Tag).Group;
                log.AppendLine("lit " + (gTarget == folder) + " folder " + st.Groups.First(g => g.Id == target).Name);
                FinishGroupDrag();
                log.AppendLine("ended dragging " + gDragging + " captured " + GroupScroll.IsMouseCaptured);
                var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                check.Tick += (s2, e2) =>
                {
                    check.Stop();
                    var now = st.Mods.First(m => m.Id == mod.Id);
                    log.AppendLine("mod " + mod.Name + " group " + (now.Group == null ? "-" : st.Groups.First(g => g.Id == now.Group).Name));
                    if (path != null) System.IO.File.WriteAllText(path, log.ToString());
                };
                check.Start();
            };
            t.Start();
        }

        /// <summary>HD2QD_CLICK_TEST=groupdragtaken: every mod taken (as a Shift range over the load
        /// order left them), a group opened, its first mod dragged onto a folder of the top column;
        /// then every mod's group, to HD2QD_WHEEL_OUT. Only the opened group's mods may move.</summary>
        void GroupDragTakenTest()
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                var log = new System.Text.StringBuilder();
                string Where(string id) => id == null ? "-" : string.Join("/", PathTo(id).Select(g => g.Name));
                foreach (var m in st.Mods) log.AppendLine("before " + m.Name + " : " + Where(m.Group));
                var open = st.Groups.First(g => g.Parent == null && st.Mods.Count(m => m.Group == g.Id) >= 2);
                groupView = true;
                groupPath.Clear();
                groupPath.Add(open.Id);
                foreach (var m in st.Mods) picked.Add(m.Id);
                var mod = GroupShown().First(m => m.Group == open.Id);
                selectedId = mod.Id;
                RenderList();
                UpdateLayout();
                log.AppendLine("taken after opening " + open.Name + ": " + string.Join("; ", Taken(mod).Select(m => m.Name)));
                var cols = GroupCols.Children.OfType<Border>().ToList();
                var folder = ((StackPanel)((ScrollViewer)cols[0].Child).Content).Children.OfType<Border>()
                    .First(b => b.Tag is GroupDrop d && d.Group != open.Id);
                var item = ((StackPanel)((ScrollViewer)cols[1].Child).Content).Children.OfType<Border>().First(b => b.Tag == null);
                gDragFrom = ("mod", mod.Id, mod.Name, item.TranslatePoint(new Point(20, 20), GroupScroll));
                StartGroupDrag();
                DragTo(folder.TranslatePoint(new Point(60, 20), GroupScroll), folder.TranslatePoint(new Point(60, 20), DragLayer));
                log.AppendLine("onto " + Where(((GroupDrop)folder.Tag).Group));
                FinishGroupDrag();
                var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                check.Tick += (s2, e2) =>
                {
                    check.Stop();
                    foreach (var m in st.Mods) log.AppendLine("after " + m.Name + " : " + Where(m.Group));
                    var path = Environment.GetEnvironmentVariable("HD2QD_WHEEL_OUT");
                    if (path != null) System.IO.File.WriteAllText(path, log.ToString());
                };
                check.Start();
            };
            t.Start();
        }

        /// <summary>HD2QD_CLICK_TEST=groupmenu: the menu of a mod in a group opened, with "move to
        /// group" and its first group open, so a picture of the windows can show them.</summary>
        void GroupMenuTest()
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                var m = st.Mods.FirstOrDefault(x => x.Group != null) ?? st.Mods.First();
                selectedId = m.Id;
                RevealSelected();
                RenderList();
                UpdateLayout();
                var menu = Menu(m);
                menu.PlacementTarget = GroupScroll;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
                menu.HorizontalOffset = 120;
                menu.VerticalOffset = 40;
                menu.IsOpen = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var move = menu.Items.OfType<MenuItem>().First(x => (x.Header as string) == Texts.Get("g_move_to"));
                    move.IsSubmenuOpen = true;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var inner = move.Items.OfType<MenuItem>().FirstOrDefault(x => x.Items.Count > 0);
                        if (inner != null) inner.IsSubmenuOpen = true;
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
            t.Start();
        }

        // ---------------------------------------------------------------- the arrow keys, as Finder's

        void GroupKeys(object sender, KeyEventArgs e)
        {
            if (!groupView || e.Handled || gDragging || Body.Visibility != Visibility.Visible || st == null) return;
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox) return;
            if (e.Key != Key.Up && e.Key != Key.Down && e.Key != Key.Left && e.Key != Key.Right) return;
            var shown = GroupShown();
            List<(bool Group, string Id)> Items(string parent)
            {
                var narrowed = shown.Count < st.Mods.Count(InScope);
                bool Holds(IEnumerable<ModRow> ms, string id) => ms.Any(m => m.Group != null && PathTo(m.Group).Any(x => x.Id == id));
                var gs = st.Groups.Where(g => g.Parent == parent && (!narrowed || Holds(shown, g.Id) || !Holds(st.Mods.Where(InScope), g.Id)));
                return gs.Select(g => (true, g.Id)).Concat(shown.Where(m => m.Group == parent).Select(m => (false, m.Id))).ToList();
            }
            int i = Math.Min(groupCol, groupPath.Count);
            var items = Items(i == 0 ? null : groupPath[i - 1]);
            int cur = items.FindIndex(x => x.Group ? i < groupPath.Count && groupPath[i] == x.Id : i == groupPath.Count && x.Id == selectedId);
            void PickAt(int j)
            {
                if (j < 0 || j >= items.Count) return;
                groupPath.RemoveRange(i, groupPath.Count - i);
                if (items[j].Group) { groupPath.Add(items[j].Id); selectedId = null; }
                else selectedId = items[j].Id;
            }
            if (e.Key == Key.Down) PickAt(cur < 0 ? 0 : Math.Min(items.Count - 1, cur + 1));
            else if (e.Key == Key.Up) PickAt(cur < 0 ? 0 : Math.Max(0, cur - 1));
            else if (e.Key == Key.Right)
            {
                if (i < groupPath.Count)
                {
                    var inner = Items(groupPath[i]);
                    if (inner.Count > 0) { groupCol = i + 1; i = groupCol; items = inner; PickAt(0); }
                }
            }
            else if (e.Key == Key.Left && i > 0)
            {
                groupCol = i - 1;
                groupPath.RemoveRange(i, groupPath.Count - i);
                selectedId = null;
            }
            e.Handled = true;
            RenderGroups();
            RenderDetail();
        }
    }
}
