using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace QuickDeploy
{
    /// <summary>The window's transitions (the user, 2026-11-06: "很多时候都很生硬，缺少过渡动画"): what
    /// comes in fades in and moves the last few pixels into place; what goes fades out. One length
    /// and one ease for all of it, short enough never to hold a click up. Nothing moves when Windows
    /// is set to show no animations. Each one ends on the element's own opacity and place, so a
    /// row drawn faint (outside the profile in use) stays faint.</summary>
    static class Motion
    {
        public const int Ms = 160;
        static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

        static IEasingFunction Freeze(CubicEase e) { e.Freeze(); return e; }

        /// <summary>Whether Windows shows animations (Settings → Accessibility → Visual effects).</summary>
        public static bool On => SystemParameters.ClientAreaAnimation;

        /// <summary>The element faded in, coming the last dx, dy pixels into its place.</summary>
        public static void In(UIElement e, double dx = 0, double dy = 8, int ms = Ms, double from = 0)
        {
            if (e == null || !On) return;
            var time = TimeSpan.FromMilliseconds(ms);
            double to = e.Opacity;
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from * to, to, time) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
            if (dx == 0 && dy == 0) return;
            var t = Shift(e);
            if (t == null) return;
            if (dx != 0) t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, time) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
            if (dy != 0) t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, time) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
        }

        /// <summary>The element faded out; done when it has. It stays transparent: the caller takes it away.</summary>
        public static Task Out(UIElement e, int ms = 120)
        {
            if (e == null || !On) return Task.CompletedTask;
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(e.Opacity, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
            return Task.Delay(ms + 10);
        }

        /// <summary>The element's own move, when it has none or one of its own; null when it carries
        /// another transform, which is left alone.</summary>
        static TranslateTransform Shift(UIElement e)
        {
            if (e.RenderTransform is TranslateTransform own && !own.IsFrozen) return own;
            if (e.RenderTransform != null && !(e.RenderTransform is MatrixTransform m && m.Matrix.IsIdentity)) return null;
            var t = new TranslateTransform();
            e.RenderTransform = t;
            return t;
        }

        /// <summary>A switch's knob slid to where it is now from the other end (the row is drawn anew
        /// after a click, with the knob already there).</summary>
        public static void Slide(CheckBox sw)
        {
            if (!On || sw == null) return;
            sw.ApplyTemplate();
            if (!(sw.Template?.FindName("knob", sw) is Border knob)) return;
            var t = Shift(knob);
            if (t == null) return;
            double from = sw.IsChecked == true ? -20 : 20;
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
        }

        /// <summary>A scroll box moved sideways to an offset over a moment, not at once.</summary>
        public static void ScrollX(ScrollViewer sv, double to, int ms = 220)
        {
            if (sv == null) return;
            double from = sv.HorizontalOffset;
            if (!On || Math.Abs(to - from) < 1) { sv.ScrollToHorizontalOffset(to); return; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            EventHandler step = null;
            step = (s, e) =>
            {
                double k = Math.Min(1, clock.ElapsedMilliseconds / (double)ms);
                double eased = 1 - Math.Pow(1 - k, 3);
                sv.ScrollToHorizontalOffset(from + (to - from) * eased);
                if (k >= 1) CompositionTarget.Rendering -= step;
            };
            CompositionTarget.Rendering += step;
        }

        /// <summary>Every element of a type under an element.</summary>
        public static IEnumerable<T> Under<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t) yield return t;
                foreach (var x in Under<T>(c)) yield return x;
            }
        }
    }
}
