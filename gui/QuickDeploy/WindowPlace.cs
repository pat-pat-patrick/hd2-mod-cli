using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Hd2Mod;

namespace QuickDeploy
{
    /// <summary>The window's place and size: kept in gui.json ("window") when it closes and given
    /// back at the next start, always inside the screen's work area (issue 11, 2026-11-04: at
    /// 1920x1080 and 125% the 900-high window ran past the screen, its top buttons out of reach,
    /// and a size the player set was lost at every start).</summary>
    public partial class MainWindow
    {
        /// <summary>A place was read from gui.json; it is fitted to its screen once the window has one.</summary>
        bool placeRead;
        bool placeMax;

        /// <summary>Before the window is shown: the kept place, else the default size made to fit
        /// the main screen's work area (the window is then centred on it as before).</summary>
        void RestorePlace()
        {
            try
            {
                var d = Store.Load("gui.json");
                if (d.TryGetValue("window", out var o) && o is Dictionary<string, object> w)
                {
                    double N(string k) => w.TryGetValue(k, out var v) && v is IConvertible ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : double.NaN;
                    double left = N("left"), top = N("top"), width = N("width"), height = N("height");
                    if (!double.IsNaN(left) && !double.IsNaN(top) && width >= 200 && height >= 200)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = left; Top = top; Width = width; Height = height;
                        placeMax = Json.Bool(w, "maximized");
                        placeRead = true;
                        return;
                    }
                }
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is FormatException || e is InvalidCastException) { }
            Fit(SystemParameters.WorkArea);
        }

        /// <summary>Once the window has its own screen: a kept place made to fit that screen's work
        /// area (a screen unplugged since, or a scale made larger, leaves nothing out of reach),
        /// then maximized again if it was.</summary>
        void FitToScreen()
        {
            if (!placeRead) return;
            var area = WorkAreaOf(new WindowInteropHelper(this).Handle);
            if (area != null) Fit(area.Value);
            if (placeMax) WindowState = WindowState.Maximized;
        }

        /// <summary>No larger than area (the smallest size too), and inside it.</summary>
        void Fit(Rect area)
        {
            if (area.Width <= 0 || area.Height <= 0) return;
            if (MinWidth > area.Width) MinWidth = area.Width;
            if (MinHeight > area.Height) MinHeight = area.Height;
            if (Width > area.Width) Width = area.Width;
            if (Height > area.Height) Height = area.Height;
            if (WindowStartupLocation != WindowStartupLocation.Manual) return;     // centred by the system
            Left = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
            Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
        }

        /// <summary>When the window closes: its place, and its size when not maximized (the one
        /// it goes back to), and whether it was maximized.</summary>
        void SavePlace()
        {
            try
            {
                var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
                if (r.IsEmpty || double.IsNaN(r.Left) || r.Width < 200 || r.Height < 200) return;
                var d = Store.Load("gui.json");
                d["window"] = new Dictionary<string, object>
                {
                    ["left"] = (long)Math.Round(r.Left), ["top"] = (long)Math.Round(r.Top),
                    ["width"] = (long)Math.Round(r.Width), ["height"] = (long)Math.Round(r.Height),
                    ["maximized"] = WindowState == WindowState.Maximized,
                };
                Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is FormatException) { }
        }

        /// <summary>The work area of the screen the window is on (or nearest to), in the window's
        /// own units; null when it cannot be read.</summary>
        Rect? WorkAreaOf(IntPtr hwnd)
        {
            var source = PresentationSource.FromVisual(this);
            if (hwnd == IntPtr.Zero || source?.CompositionTarget == null) return null;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return null;
            var m = source.CompositionTarget.TransformFromDevice;
            return new Rect(m.Transform(new Point(info.rcWork.Left, info.rcWork.Top)), m.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom)));
        }

        const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    }
}
