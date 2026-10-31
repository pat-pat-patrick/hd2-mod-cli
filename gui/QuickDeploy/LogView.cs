using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Hd2Mod;

namespace QuickDeploy
{
    /// <summary>One place a log is shown (LoaderLogs): the Lua loaders page shows the loaders'
    /// logs, the settings page the manager's own (the user, 2026-11-01). Which log and file,
    /// the levels, the session, the search are each view's own.</summary>
    class LogView
    {
        /// <summary>The logs it can show ("bml", "chef", "bsl", "mdl3", "manager"), there or not.</summary>
        public Func<List<string>> Kinds;
        /// <summary>The one shown at first, when it is there; else the first there.</summary>
        public Func<string> First = () => null;
        public string Kind, File;
        public readonly HashSet<string> Levels = new HashSet<string> { "error", "warn", "info" };
        public bool All;
        public TextBox Find;
        public StackPanel Box;
        /// <summary>A name for its buttons' groups, its own.</summary>
        public string Group;
        /// <summary>What "the last session" is called in it.</summary>
        public string SessionKey = "lp_last_session";
        /// <summary>The time as the log writes it (else from the hour on), the lines at most, the
        /// height of the lines' box.</summary>
        public bool FullTime = true;
        public int Most = 400;
        public double Height = 460;
    }

    public partial class MainWindow
    {
        static readonly string[] Levels = { "error", "warn", "info", "debug" };

        // the logs read, by their size and time
        readonly Dictionary<string, (string Sig, List<LoaderLogs.Entry> Entries)> logsRead =
            new Dictionary<string, (string, List<LoaderLogs.Entry>)>(StringComparer.OrdinalIgnoreCase);

        List<LoaderLogs.Entry> LogEntries(LoaderLogs.Source s)
        {
            var fi = new FileInfo(s.Path);
            var sig = fi.Exists ? fi.Length + ":" + fi.LastWriteTimeUtc.Ticks : "";
            if (logsRead.TryGetValue(s.Path, out var k) && k.Sig == sig) return k.Entries;
            var entries = fi.Exists ? LoaderLogs.Read(s) : new List<LoaderLogs.Entry>();
            logsRead[s.Path] = (sig, entries);
            return entries;
        }

        static string LevelColour(string level) => level == "error" ? "Red" : level == "warn" ? "Yellow" : level == "info" ? "Blue" : "Muted";

        string KindLabel(string kind) => kind == "manager" ? "HD2 Mod Quick Deploy" : LoaderLabel(kind, null);

        /// <summary>A log on show in its view: which log and file, the levels with how many lines
        /// each has, the session, the search, then the lines (the newest last), drawn again alone
        /// as these change.</summary>
        void RenderLog(LogView v)
        {
            if (v.Box == null) return;
            v.Box.Children.Clear();
            var kinds = v.Kinds().Where(k => LoaderLogs.Of(k).Any(l => l.Exists)).ToList();
            if (kinds.Count == 0) { v.Box.Children.Add(Small(Texts.Get("lp_no_logs"), new Thickness(0))); return; }
            if (v.Kind == null || !kinds.Contains(v.Kind)) v.Kind = kinds.Contains(v.First() ?? "") ? v.First() : kinds[0];
            var files = LoaderLogs.Of(v.Kind).Where(l => l.Exists).ToList();
            var src = files.FirstOrDefault(f => f.Name == v.File) ?? files[0];
            v.File = src.Name;

            // which log, which file
            RadioButton Seg(string text, bool now, Action pick, string group)
            {
                var b = new RadioButton { Style = (Style)FindResource("Segment"), Content = text, MinWidth = 88, IsChecked = now, GroupName = v.Group + group };
                b.Click += (s, e) => pick();
                return b;
            }
            var bar = new WrapPanel();
            if (kinds.Count > 1)
            {
                var kindSeg = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var k in kinds) kindSeg.Children.Add(Seg(KindLabel(k), k == v.Kind, () => { v.Kind = k; v.File = null; RenderLog(v); }, "kind"));
                bar.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Child = kindSeg, Margin = new Thickness(0, 0, 12, 8) });
            }
            if (files.Count > 1)
            {
                var fileSeg = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var f in files) fileSeg.Children.Add(Seg(f.Name, f.Name == v.File, () => { v.File = f.Name; RenderLog(v); }, "file"));
                bar.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Child = fileSeg, Margin = new Thickness(0, 0, 12, 8) });
            }
            if (bar.Children.Count > 0) v.Box.Children.Add(bar);

            var entries = LogEntries(src);
            bool sessions = src.Session != null && entries.Any(e => e.Session > 0);
            var shown = entries.Where(e => v.All || !sessions || e.Session == 0).ToList();
            var words = (v.Find?.Text ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var found = shown.Where(e => words.All(w => e.Text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

            // the levels; the session; the search; the copy and the file
            var filters = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var copy = new Button { Content = Texts.Get("lp_copy_shown"), Style = (Style)FindResource("Ghost"), MinHeight = 34, Margin = new Thickness(8, 0, 0, 0) };
            var openFile = new Button { Content = Texts.Get("lp_open_file"), Style = (Style)FindResource("Ghost"), MinHeight = 34, Margin = new Thickness(8, 0, 0, 0) };
            openFile.Click += (s, e) => ShowInFolder(src.Path);
            right.Children.Add(copy);
            right.Children.Add(openFile);
            DockPanel.SetDock(right, Dock.Right);
            filters.Children.Add(right);
            var left = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var lv in Levels)
            {
                int n = found.Count(e => e.Level == lv);
                // a segment of its own group: lit while its level is shown, a click turns it over
                var chip = new RadioButton
                {
                    Content = Texts.F("lp_level_" + lv, n), IsChecked = v.Levels.Contains(lv), Style = (Style)FindResource("Segment"),
                    MinWidth = 72, GroupName = v.Group + "level_" + lv,
                };
                var level = lv;
                chip.Click += (s, e) => { if (!v.Levels.Remove(level)) v.Levels.Add(level); RenderLog(v); };
                left.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 8), Child = chip });
            }
            if (sessions)
            {
                var sessSeg = new StackPanel { Orientation = Orientation.Horizontal };
                sessSeg.Children.Add(Seg(Texts.Get(v.SessionKey), !v.All, () => { v.All = false; RenderLog(v); }, "sess"));
                sessSeg.Children.Add(Seg(Texts.Get("lp_all_sessions"), v.All, () => { v.All = true; RenderLog(v); }, "sess"));
                left.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 8), Child = sessSeg });
            }
            if (v.Find == null)
            {
                v.Find = new TextBox
                {
                    Background = B("Ground900"), Foreground = B("Ink"), CaretBrush = B("Ink"), BorderBrush = B("Grid"),
                    BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), FontSize = 13, Width = 200,
                    ToolTip = Texts.Get("lp_find_hint"),
                };
                v.Find.TextChanged += (s, e) => RenderLog(v);
            }
            var findBox = new Grid { Margin = new Thickness(6, 0, 0, 8) };
            Reuse(v.Find, findBox);
            findBox.Children.Add(new TextBlock
            {
                Text = Texts.Get("lp_find_hint"), Foreground = B("Muted"), FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0), Visibility = v.Find.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
            });
            left.Children.Add(findBox);
            filters.Children.Add(left);
            v.Box.Children.Add(filters);

            // the lines: the newest last, at most a few hundred
            var lines = found.Where(e => v.Levels.Contains(e.Level)).ToList();
            var drawn = lines.Skip(Math.Max(0, lines.Count - v.Most)).ToList();
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(string.Join(Environment.NewLine, drawn.Select(x => (x.Time.Length > 0 ? x.Time + " " : "") + "[" + x.Level + "] " + x.Text))); }
                catch (System.Runtime.InteropServices.COMException) { }
            };
            var list = new StackPanel();
            foreach (var e in drawn)
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                // from the hour on in a narrow view: the date is in the session anyway
                var when = v.FullTime || e.Time.Length <= 8 ? e.Time : e.Time.Substring(e.Time.Length - 8);
                var time = new TextBlock { Text = when, FontFamily = Mono, FontSize = 11, Foreground = B("Muted"), Width = v.FullTime ? 132 : 62, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
                DockPanel.SetDock(time, Dock.Left);
                row.Children.Add(time);
                var level = new TextBlock { Text = Texts.Get("lp_lv_" + e.Level), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = B(LevelColour(e.Level)), Width = 44, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
                DockPanel.SetDock(level, Dock.Left);
                row.Children.Add(level);
                var text = Selectable.Text(e.Text, 12, e.Level == "error" ? "Red" : e.Level == "warn" ? "Yellow" : e.Level == "debug" ? "Muted" : "Ink", Mono);
                row.Children.Add(text);
                list.Children.Add(row);
            }
            if (drawn.Count == 0) list.Children.Add(Small(Texts.Get(entries.Count == 0 ? "lp_log_empty" : "lp_log_none_shown"), new Thickness(0)));
            var scroll = new ScrollViewer { Content = list, MaxHeight = v.Height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            v.Box.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(10, 8, 10, 8), Child = scroll });
            scroll.Loaded += (s, e) => scroll.ScrollToEnd();
            var note = Texts.F(lines.Count > v.Most ? "lp_log_count_cut" : "lp_log_count", drawn.Count, lines.Count, src.Path);
            if (Texts.Hints && !entries.Any(e => e.Written)) note += " " + Texts.Get("lp_level_note");
            v.Box.Children.Add(Small(note, new Thickness(0, 6, 0, 0)));
        }
    }
}
