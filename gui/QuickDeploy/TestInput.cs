using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace QuickDeploy
{
    /// <summary>For the window's own tests (HD2QD_CLICK_TEST): mouse input to itself, through all
    /// of WPF's input, with the button state WPF reads for it. Never used outside a test.</summary>
    static class TestInput
    {
        [DllImport("user32.dll")] public static extern bool GetKeyboardState(byte[] keys);
        [DllImport("user32.dll")] public static extern bool SetKeyboardState(byte[] keys);

        // RawMouseActions (PresentationCore, internal)
        public const int Activate = 0x2, Move = 0x10, Press = 0x40, Release = 0x80;

        /// <summary>A raw mouse report at a point in the window's pixels, handed to the input
        /// manager as the window's own mouse messages are.</summary>
        public static void Mouse(PresentationSource source, int actions, Point devicePx)
        {
            var core = typeof(InputManager).Assembly;
            var raw = core.GetType("System.Windows.Input.RawMouseInputReport");
            var acts = core.GetType("System.Windows.Input.RawMouseActions");
            var ctor = raw.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(InputMode), typeof(int), typeof(PresentationSource), acts, typeof(int), typeof(int), typeof(int), typeof(IntPtr) }, null);
            var report = ctor.Invoke(new object[] { InputMode.Foreground, Environment.TickCount, source, Enum.ToObject(acts, actions), (int)devicePx.X, (int)devicePx.Y, 0, IntPtr.Zero });
            // InputReportEventArgs and PreviewInputReportEvent are internal in .NET Framework 4.8
            var argsType = core.GetType("System.Windows.Input.InputReportEventArgs");
            var args = (InputEventArgs)Activator.CreateInstance(argsType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { System.Windows.Input.Mouse.PrimaryDevice, report }, null);
            args.RoutedEvent = (RoutedEvent)typeof(InputManager).GetField("PreviewInputReportEvent", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
            InputManager.Current.ProcessInput(args);
        }
    }
}
