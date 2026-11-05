using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickDeploy
{
    /// <summary>Text a player may want to copy: a name, a version, a path, a script's name, an
    /// error. It is drawn like the text around it, can be selected with the mouse and copied
    /// with Ctrl+C, and right-click → Copy copies the selection, or all of it when nothing is
    /// selected (the user, 2026-10-26). Labels, counts and buttons stay plain text.</summary>
    static class Selectable
    {
        static Brush B(string key) => (Brush)Application.Current.Resources[key];

        public static TextBox Text(string text, double size, string colour = "Ink", FontFamily font = null, double lineHeight = 0)
        {
            var t = new TextBox
            {
                Style = (Style)Application.Current.Resources["Copyable"], Text = text ?? "", FontSize = size, Foreground = B(colour),
            };
            if (font != null) t.FontFamily = font;
            if (lineHeight > 0) t.SetValue(TextBlock.LineHeightProperty, lineHeight);
            Menu(t, () => Selection(t) ?? t.Text);
            // a piece without the focus draws its part of a selection itself (Marks)
            t.SelectionChanged += (s, e) => Marks.Update(t);
            t.IsKeyboardFocusedChanged += (s, e) => Marks.Update(t);
            // Ctrl+C in a group copies the whole selection, not only this piece's
            t.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (s, e) =>
            {
                var x = Selection(t);
                if (!string.IsNullOrEmpty(x)) SetClipboard(x);
            }, (s, e) => { e.CanExecute = Selection(t) != null; e.Handled = true; }));
            return t;
        }

        /// <summary>Right-click → Copy on text that is cut short on screen (a line with "…"):
        /// the whole of it is copied.</summary>
        public static void Menu(FrameworkElement e, Func<string> text)
        {
            var item = new MenuItem { Header = Texts.Get("copy") };
            item.Click += (s, a) =>
            {
                var x = text();
                if (string.IsNullOrEmpty(x)) return;
                SetClipboard(x);
            };
            var menu = new ContextMenu();
            menu.Items.Add(item);
            e.ContextMenu = menu;
        }

        /// <summary>For a test: what a copy would put on the clipboard goes here instead, so a test
        /// never touches the clipboard the player is using.</summary>
        public static Action<string> TestSink;

        static void SetClipboard(string x)
        {
            if (TestSink != null) { TestSink(x); return; }
            try { Clipboard.SetText(x); } catch (Exception) { }      // another program holding the clipboard: nothing copied
        }

        static bool IsPiece(TextBox t) => t.Style == Application.Current.Resources["Copyable"];

        // ------------------------------------------------------------ a selection across pieces

        // Each piece of text is a box of its own, so a drag selected one line at a time and Ctrl+C
        // copied that line (the user, 2026-11-06: "只能一行一行选"). In a group, a drag from one
        // piece to another selects every piece between them, in the order they are on screen, and
        // a copy takes all of it: pieces on one line joined by a space, lines by a line break.

        static readonly DependencyProperty GroupProperty =
            DependencyProperty.RegisterAttached("SelectGroup", typeof(GroupState), typeof(Selectable), new PropertyMetadata(null));

        sealed class GroupState
        {
            public FrameworkElement Root;
            public List<TextBox> Pieces = new List<TextBox>();
            public TextBox Anchor;
            public int AnchorAt;
            public bool Dragging;
            public Window Window;
        }

        /// <summary>The pieces of text under root, from here on, select and copy as one text.
        /// Lists that are dragged to reorder are not groups: a drag there moves the row.</summary>
        public static void Group(FrameworkElement root)
        {
            if (root.GetValue(GroupProperty) != null) return;
            var g = new GroupState { Root = root };
            root.SetValue(GroupProperty, g);
            // The piece pressed keeps the mouse as a text box does (it takes the capture in a class
            // handler that runs even for a handled press: a capture of the group's own was taken
            // from it at once, and the drag stopped there, 2026-11-06). Every move still passes
            // through the group on its way to the piece: while the mouse is over the piece pressed,
            // the piece selects as it always has; past it, the group selects and keeps the move.
            root.PreviewMouseLeftButtonDown += (s, e) => Down(g, e);
            root.PreviewMouseMove += (s, e) => { if (g.Dragging && Drag(g, e.GetPosition(root))) e.Handled = true; };
            root.PreviewMouseLeftButtonUp += (s, e) => g.Dragging = false;
            // a click anywhere else in the window ends the selection, as it does in one text box
            MouseButtonEventHandler outside = (s, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsUnder(d, root)) return;
                Clear(g, null);
            };
            root.Loaded += (s, e) =>
            {
                g.Window = Window.GetWindow(root);
                if (g.Window != null) g.Window.PreviewMouseDown += outside;
            };
            root.Unloaded += (s, e) => { if (g.Window != null) g.Window.PreviewMouseDown -= outside; g.Window = null; };
        }

        static bool IsUnder(DependencyObject d, DependencyObject root)
        {
            for (var x = d; x != null; x = x is Visual || x is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
                if (x == root) return true;
            return false;
        }

        static TextBox PieceOf(DependencyObject d)
        {
            for (var x = d; x != null; x = x is Visual || x is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            {
                if (x is TextBox t) return IsPiece(t) ? t : null;
                if (x is ButtonBase) return null;
            }
            return null;
        }

        /// <summary>The group's pieces that are on screen, in the order a reader meets them.</summary>
        static List<TextBox> Pieces(FrameworkElement root)
        {
            var found = new List<TextBox>();
            void Walk(DependencyObject o)
            {
                if (o is UIElement u && u.Visibility != Visibility.Visible) return;
                if (o is TextBox t) { if (IsPiece(t)) found.Add(t); return; }
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++) Walk(VisualTreeHelper.GetChild(o, i));
            }
            Walk(root);
            return found;
        }

        static void Down(GroupState g, MouseButtonEventArgs e)
        {
            var t = e.OriginalSource is DependencyObject d ? PieceOf(d) : null;
            if (t == null) { Clear(g, null); return; }
            // a double or triple click selects a word or the piece, in the piece itself
            if (e.ClickCount > 1) { Clear(g, t); return; }
            Clear(g, null);
            g.Pieces = Pieces(g.Root);
            g.Anchor = t;
            g.AnchorAt = At(t, e.GetPosition(t));
            g.Dragging = true;
        }

        /// <summary>Where in a piece a point falls: the index before the character the point is on,
        /// or after it when the point is past the character's middle.</summary>
        static int At(TextBox t, Point p)
        {
            if (t.Text.Length == 0) return 0;
            int i = t.GetCharacterIndexFromPoint(p, true);
            if (i < 0) return 0;
            var r = t.GetRectFromCharacterIndex(i);
            var r2 = t.GetRectFromCharacterIndex(i, true);
            // below the piece's last line: its end; above its first: its start
            var last = t.GetRectFromCharacterIndex(t.Text.Length);
            if (p.Y > last.Bottom) return t.Text.Length;
            if (p.Y < t.GetRectFromCharacterIndex(0).Top) return 0;
            if (!r.IsEmpty && !r2.IsEmpty && p.X > (r.Left + r2.Left) / 2 && Math.Abs(r.Top - r2.Top) < 1) i++;
            return Math.Min(i, t.Text.Length);
        }

        /// <summary>The selection to the point: false while the point is over the piece pressed (the
        /// piece selects by itself), true when the group made it.</summary>
        static bool Drag(GroupState g, Point p)
        {
            if (g.Anchor == null) return false;
            // past the top or bottom of the box it scrolls in: scrolled a line that way
            var scroll = Ancestor<ScrollViewer>(g.Anchor);
            if (scroll != null)
            {
                var sp = g.Root.TranslatePoint(p, scroll);
                if (sp.Y < 0) scroll.LineUp();
                else if (sp.Y > scroll.ActualHeight) scroll.LineDown();
            }
            var target = Nearest(g, p);
            if (target == null) return false;
            if (target == g.Anchor)
            {
                Clear(g, g.Anchor);
                return false;
            }
            int ti = g.Pieces.IndexOf(target), ai = g.Pieces.IndexOf(g.Anchor);
            int at = At(target, g.Root.TranslatePoint(p, target));
            for (int i = 0; i < g.Pieces.Count; i++)
            {
                var x = g.Pieces[i];
                if (ti == ai && i == ai) x.Select(Math.Min(g.AnchorAt, at), Math.Abs(g.AnchorAt - at));
                else if (i == ai) { if (ti > ai) x.Select(g.AnchorAt, x.Text.Length - g.AnchorAt); else x.Select(0, g.AnchorAt); }
                else if (i == ti) { if (ti > ai) x.Select(0, at); else x.Select(at, x.Text.Length - at); }
                else if (i > Math.Min(ai, ti) && i < Math.Max(ai, ti)) x.SelectAll();
                else if (x.SelectionLength > 0) x.Select(0, 0);
            }
            return true;
        }

        static T Ancestor<T>(DependencyObject d) where T : DependencyObject
        {
            for (var x = VisualTreeHelper.GetParent(d); x != null; x = VisualTreeHelper.GetParent(x))
                if (x is T found) return found;
            return null;
        }

        /// <summary>The piece under the point, else the one nearest it: the last piece whose top is
        /// above the point, on the line the point is on or the one before (reading order).</summary>
        static TextBox Nearest(GroupState g, Point p)
        {
            var hit = g.Root.InputHitTest(p) as DependencyObject;
            var t = hit != null ? PieceOf(hit) : null;
            if (t != null && g.Pieces.Contains(t)) return t;
            TextBox best = null;
            double bestD = double.MaxValue;
            foreach (var x in g.Pieces)
            {
                if (!x.IsVisible) continue;
                Rect r;
                try { r = x.TransformToAncestor(g.Root).TransformBounds(new Rect(0, 0, x.ActualWidth, x.ActualHeight)); }
                catch (InvalidOperationException) { continue; }
                double dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
                double dx = p.X < r.Left ? r.Left - p.X : p.X > r.Right ? p.X - r.Right : 0;
                double dist = dy * 1000 + dx;      // the line first, then the side
                if (dist < bestD) { bestD = dist; best = x; }
            }
            return best;
        }

        static void Clear(GroupState g, TextBox keep)
        {
            foreach (var x in g.Pieces)
                if (x != keep && x.SelectionLength > 0) x.Select(0, 0);
            if (keep == null) { g.Anchor = null; g.Pieces = new List<TextBox>(); }
        }

        static GroupState GroupOf(TextBox t)
        {
            for (DependencyObject x = t; x != null; x = VisualTreeHelper.GetParent(x))
                if (x.GetValue(GroupProperty) is GroupState g) return g;
            return null;
        }

        /// <summary>What is selected: across the group when the selection spans pieces, else in
        /// this piece; null when nothing is.</summary>
        public static string Selection(TextBox t)
        {
            var g = GroupOf(t);
            var chosen = g == null ? new List<TextBox>() : g.Pieces.Where(x => x.SelectionLength > 0).ToList();
            if (chosen.Count <= 1 || !chosen.Contains(t) && t.SelectionLength > 0)
                return t.SelectionLength > 0 ? t.SelectedText : null;
            var sb = new StringBuilder();
            Rect? lastLine = null;
            foreach (var x in chosen)
            {
                Rect r;
                try { r = x.TransformToAncestor(g.Root).TransformBounds(new Rect(0, 0, x.ActualWidth, x.ActualHeight)); }
                catch (InvalidOperationException) { r = Rect.Empty; }
                if (sb.Length > 0)
                    // beside the piece before (its line's height overlaps): a space; else a new line
                    sb.Append(lastLine.HasValue && !r.IsEmpty && r.Top < lastLine.Value.Bottom - 1 && r.Left >= lastLine.Value.Right - 1 ? " " : Environment.NewLine);
                sb.Append(x.SelectedText);
                lastLine = r;
            }
            return sb.ToString();
        }

        /// <summary>The selection drawn on a piece that does not have the focus. A text box draws its
        /// selection only while it has the focus, IsInactiveSelectionHighlightEnabled or not (measured
        /// 2026-11-06: a selection across five pieces showed in the focused one only, so the player
        /// saw one line selected), so each piece's part is drawn here, in the selection's own colour,
        /// line by line.</summary>
        sealed class Marks : System.Windows.Documents.Adorner
        {
            readonly TextBox t;

            Marks(TextBox t) : base(t) { this.t = t; IsHitTestVisible = false; }

            public static void Update(TextBox t)
            {
                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(t);
                if (layer == null) return;
                var mine = layer.GetAdorners(t)?.OfType<Marks>().FirstOrDefault();
                if (mine == null)
                {
                    if (t.SelectionLength == 0) return;
                    mine = new Marks(t);
                    layer.Add(mine);
                }
                mine.InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                if (t.SelectionLength == 0 || t.IsKeyboardFocused || t.Text.Length == 0) return;
                var brush = t.SelectionBrush.Clone();
                brush.Opacity = t.SelectionOpacity;
                brush.Freeze();
                int s = t.SelectionStart, e = Math.Min(t.Text.Length, s + t.SelectionLength);
                int first = t.GetLineIndexFromCharacterIndex(s), last = t.GetLineIndexFromCharacterIndex(Math.Max(s, e - 1));
                if (first < 0 || last < 0) return;
                for (int line = first; line <= last; line++)
                {
                    int ls = t.GetCharacterIndexFromLineIndex(line), le = ls + t.GetLineLength(line);
                    // a line's own break is not drawn
                    while (le > ls && (t.Text[le - 1] == '\n' || t.Text[le - 1] == '\r')) le--;
                    int a = Math.Max(s, ls), b = Math.Min(e, le);
                    if (b <= a) continue;
                    var ra = t.GetRectFromCharacterIndex(a);
                    var rb = t.GetRectFromCharacterIndex(b - 1, true);
                    if (ra.IsEmpty || rb.IsEmpty) continue;
                    dc.DrawRectangle(brush, null, new Rect(new Point(ra.Left, Math.Min(ra.Top, rb.Top)), new Point(Math.Max(rb.Right, ra.Left + 1), Math.Max(ra.Bottom, rb.Bottom))));
                }
            }
        }

        /// <summary>For a test: a selection from one piece to another as a drag would make it, and
        /// what Ctrl+C on the first then copies.</summary>
        public static string TestDrag(FrameworkElement root, TextBox from, int fromAt, TextBox to, int toAt)
        {
            Group(root);
            var g = (GroupState)root.GetValue(GroupProperty);
            Clear(g, null);
            g.Pieces = Pieces(root);
            g.Anchor = from;
            g.AnchorAt = fromAt;

            var p = to.GetRectFromCharacterIndex(Math.Min(toAt, to.Text.Length));
            var pt = to.TranslatePoint(new Point(p.Left + 0.5, (p.Top + p.Bottom) / 2), root);
            Drag(g, pt);
            return Selection(from);
        }
    }
}
