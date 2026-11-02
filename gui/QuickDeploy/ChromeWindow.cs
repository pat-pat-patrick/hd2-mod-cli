using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace QuickDeploy
{
    /// <summary>A window with the same dark caption the main window draws (the user, 2026-11-02:
    /// the pop-ups' title bars did not match): the system frame kept, its caption hidden and
    /// drawn by the window — a title on the left, the minimise/maximise/close buttons on the
    /// right, in HD2 Mod Setting GUI's colours. The manager makes these for the update-notes
    /// window and, through the plugin host, for a plugin's own window, so there is one caption.</summary>
    public sealed class ChromeWindow : Window
    {
        readonly Border frame;
        readonly Path maxGlyph;

        /// <summary>owner's icon, font and theme; body fills under the caption.</summary>
        public ChromeWindow(Window owner, string title, FrameworkElement body)
        {
            Owner = owner;
            Title = title + " · HD2 Mod Quick Deploy";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brush("Ground900");
            Foreground = Brush("Ink");
            FontFamily = owner?.FontFamily ?? SystemFonts.MessageFontFamily;
            FontSize = 14;
            Icon = owner?.Icon;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 47,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });

            var dock = new DockPanel();
            var header = new Border { Background = Brush("Ground700"), BorderBrush = Brush("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Height = 48 };
            DockPanel.SetDock(header, Dock.Top);
            var bar = new DockPanel { LastChildFill = false };
            header.Child = bar;

            var titleText = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            DockPanel.SetDock(titleText, Dock.Left);
            bar.Children.Add(titleText);

            var close = Caption("CaptionClose", new Path { Data = Geometry.Parse("M0,0 L10,10 M10,0 L0,10"), StrokeThickness = 1.2 }, () => Close());
            ((Path)close.Content).SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            DockPanel.SetDock(close, Dock.Right);
            bar.Children.Add(close);

            maxGlyph = new Path { Data = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z"), Stroke = Brush("Ink"), StrokeThickness = 1 };
            var max = Caption("Caption", maxGlyph, Toggle);
            DockPanel.SetDock(max, Dock.Right);
            bar.Children.Add(max);

            var min = Caption("Caption", new Path { Data = Geometry.Parse("M0,0.5 H10"), Stroke = Brush("Ink"), StrokeThickness = 1 }, () => SystemCommands.MinimizeWindow(this));
            DockPanel.SetDock(min, Dock.Right);
            bar.Children.Add(min);

            dock.Children.Add(header);
            dock.Children.Add(body);
            frame = new Border { BorderBrush = Brush("Grid"), BorderThickness = new Thickness(1), Child = dock };
            Content = frame;

            StateChanged += (s, e) => FitMax();
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        Button Caption(string style, UIElement glyph, Action click)
        {
            var b = new Button { Style = (Style)Application.Current.TryFindResource(style), Content = glyph, VerticalAlignment = VerticalAlignment.Stretch };
            b.Click += (s, e) => click();
            return b;
        }

        /// <summary>When maximised, the system frame clips the window, so the content is pulled in
        /// by the resize frame's width, as the main window does it.</summary>
        void FitMax()
        {
            bool max = WindowState == WindowState.Maximized;
            maxGlyph.Data = Geometry.Parse(max ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z" : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
            if (max)
            {
                var src = PresentationSource.FromVisual(this);
                double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
                int px = GetSystemMetrics(32) + GetSystemMetrics(92);     // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
                frame.Margin = new Thickness(px * sx);
                frame.BorderThickness = new Thickness(0);
            }
            else
            {
                frame.Margin = new Thickness(0);
                frame.BorderThickness = new Thickness(1);
            }
        }

        void Toggle()
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        static Brush Brush(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);
    }
}
