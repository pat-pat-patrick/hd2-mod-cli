using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickDeploy
{
    /// <summary>The window's own message box (as HD2 Mod Setting GUI: Windows's own
    /// did not match the window): its colours and buttons, over the window.
    /// Esc or the cancel button says no; the action is the yellow button.</summary>
    static class Dialog
    {
        static Brush B(string key) => (Brush)Application.Current.Resources[key];

        /// <summary>A question: true when the action is chosen.</summary>
        public static bool Ask(Window owner, string text, string action) => Show(owner, text, action, Texts.Get("cancel"));

        /// <summary>A question whose action cannot be undone: the action button is red.</summary>
        public static bool AskDanger(Window owner, string text, string action) => Show(owner, text, action, Texts.Get("cancel"), "Danger");

        /// <summary>A message with one button.</summary>
        public static void Tell(Window owner, string text) => Show(owner, text, null, Texts.Get("ok"));

        /// <summary>A question with several answers, the first one yellow: the index chosen, or -1
        /// (Esc, or the last button when it is the dismissal).</summary>
        public static int Choose(Window owner, string text, params string[] answers)
        {
            int chosen = -1;
            var w = Frame(owner, text, out var body);
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
            for (int i = 0; i < answers.Length; i++)
            {
                int k = i;
                var b = new Button
                {
                    Content = answers[i], Style = (Style)Application.Current.Resources[i == 0 ? "Primary" : "Ghost"],
                    MinWidth = 96, MinHeight = 40, Margin = new Thickness(8, 0, 0, 8),
                };
                b.Click += (s, e) => { chosen = k; w.Close(); };
                buttons.Children.Add(b);
            }
            body.Children.Add(buttons);
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.ShowDialog();
            return chosen;
        }

        /// <summary>A list to tick (all ticked at first): which were ticked, or null when cancelled.</summary>
        public static bool[] Ticks(Window owner, string text, string[] items, string action, bool ticked = true)
        {
            var w = Frame(owner, text, out var body);
            // the window's own ticks, not Windows' box (the user, 2026-10-25)
            var boxes = items.Select(x => new CheckBox
            {
                Style = (Style)Application.Current.Resources["Tick"],
                Content = new TextBlock { Text = x, TextWrapping = TextWrapping.Wrap, Foreground = B("Ink"), FontSize = 14 },
                IsChecked = ticked, Margin = new Thickness(0, 10, 0, 0),
            }).ToList();
            foreach (var b in boxes) body.Children.Add(b);
            bool ok = false;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
            var no = new Button { Content = Texts.Get("cancel"), Style = (Style)Application.Current.Resources["Ghost"], MinWidth = 96, IsCancel = true };
            no.Click += (s, e) => w.Close();
            var yes = new Button { Content = action, Style = (Style)Application.Current.Resources["Primary"], MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
            yes.Click += (s, e) => { ok = true; w.Close(); };
            buttons.Children.Add(no);
            buttons.Children.Add(yes);
            body.Children.Add(buttons);
            w.ShowDialog();
            return ok ? boxes.Select(b => b.IsChecked == true).ToArray() : null;
        }

        /// <summary>An empty window in the same frame, for a page of its own to fill.</summary>
        public static Window Blank(Window owner, out StackPanel body)
        {
            var w = new Window
            {
                Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = B("Ground700"), BorderBrush = B("Grid"), BorderThickness = new Thickness(1),
                FontFamily = (FontFamily)Application.Current.Resources["Ui"], Title = Texts.Get("window"),
                MinWidth = 420, MaxWidth = 560,
            };
            var col = new StackPanel();
            col.Children.Add(new Border { Height = 3, Background = B("Yellow") });
            body = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            col.Children.Add(body);
            w.Content = col;
            w.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) w.DragMove(); };
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            Redraw(w);
            return w;
        }

        [DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

        /// <summary>Once on screen, laid out and drawn again: the first question of a session (Delete
        /// group, the user, 2026-10-27; Delete, 2026-11-01) came up with only its buttons drawn.
        /// WPF's own invalidation repaints only what changed, so Windows is asked to paint the
        /// whole window as well (RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW).</summary>
        static void Redraw(Window w) =>
            w.ContentRendered += (s, e) =>
            {
                w.InvalidateMeasure();
                w.UpdateLayout();
                if (w.Content is UIElement c) c.InvalidateVisual();
                w.InvalidateVisual();
                w.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var hwnd = new WindowInteropHelper(w).Handle;
                    if (hwnd != IntPtr.Zero) RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0400 | 0x0080 | 0x0100);
                }), DispatcherPriority.Render);
            };

        /// <summary>The dialog's window: the tape on top, the text, a body to add to.</summary>
        static Window Frame(Window owner, string text, out StackPanel body)
        {
            var w = new Window
            {
                Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = B("Ground700"), BorderBrush = B("Grid"), BorderThickness = new Thickness(1),
                FontFamily = (FontFamily)Application.Current.Resources["Ui"], Title = Texts.Get("window"),
                MinWidth = 380, MaxWidth = 600,
            };
            var col = new StackPanel();
            col.Children.Add(new Border { Height = 3, Background = B("Yellow") });
            body = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            col.Children.Add(body);
            body.Children.Add(Selectable.Text(text, 15, "Ink", null, 22));      // a message can be copied
            w.Content = col;
            w.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) w.DragMove(); };
            Redraw(w);
            return w;
        }

        /// <summary>A secret typed or pasted in (shown as dots): the text, or null when cancelled.</summary>
        public static string AskSecret(Window owner, string text, string action)
        {
            var box = new PasswordBox
            {
                Background = B("Ground900"), Foreground = B("Ink"), BorderBrush = B("Grid"), CaretBrush = B("Ink"),
                BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), FontSize = 14, Margin = new Thickness(0, 16, 0, 0),
            };
            return Show(owner, text, action, Texts.Get("cancel"), "Primary", box) ? box.Password : null;
        }

        /// <summary>A line of text typed or pasted in: the text, or null when cancelled.</summary>
        public static string AskText(Window owner, string text, string action, string start = "")
        {
            var box = new TextBox
            {
                Text = start ?? "", Background = B("Ground900"), Foreground = B("Ink"), BorderBrush = B("Grid"), CaretBrush = B("Ink"),
                BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), FontSize = 14, Margin = new Thickness(0, 16, 0, 0),
            };
            box.Loaded += (s, e) => box.SelectAll();
            return Show(owner, text, action, Texts.Get("cancel"), "Primary", box) ? box.Text : null;
        }

        static bool Show(Window owner, string text, string action, string dismiss, string actionStyle = "Primary", Control input = null)
        {
            var w = new Window
            {
                Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = B("Ground700"), BorderBrush = B("Grid"), BorderThickness = new Thickness(1),
                FontFamily = (FontFamily)Application.Current.Resources["Ui"], Title = Texts.Get("window"),
                MinWidth = 380, MaxWidth = 560,
            };
            var col = new StackPanel();
            col.Children.Add(new Border { Height = 3, Background = B("Yellow") });      // the window's tape
            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            col.Children.Add(body);
            body.Children.Add(Selectable.Text(text, 15, "Ink", null, 22));      // a message can be copied
            if (input != null) body.Children.Add(input);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
            body.Children.Add(buttons);
            bool chosen = false;
            var no = new Button { Content = dismiss, Style = (Style)Application.Current.Resources["Ghost"], MinWidth = 96, IsCancel = true };
            no.Click += (s, e) => w.Close();
            buttons.Children.Add(no);
            if (action != null)
            {
                var yes = new Button { Content = action, Style = (Style)Application.Current.Resources[actionStyle], MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = input != null };
                yes.Click += (s, e) => { chosen = true; w.Close(); };
                buttons.Children.Add(yes);
            }
            w.Content = col;
            // moved by any place that is not a button
            w.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) w.DragMove(); };
            w.Loaded += (s, e) => { if (input != null) input.Focus(); else no.Focus(); };
            Redraw(w);
            w.ShowDialog();
            return chosen;
        }
    }
}
