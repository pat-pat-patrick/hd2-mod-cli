using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Windows;

namespace QuickDeploy
{
    public partial class App : Application
    {
        /// <summary>The pipe a running window listens on: one per Windows user and settings
        /// folder (a window on a test library never takes the player's links).</summary>
        public static readonly string Pipe = "HD2ModQuickDeploy-" + Environment.UserName + "-" + HomeTag();

        static string HomeTag()
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(Hd2Mod.Store.Home.ToLowerInvariant().TrimEnd('\\')))
                                        .Take(6).Select(b => b.ToString("x2")));
        }

#if HAS_NEXUS
        /// <summary>A Nexus download link this start was given (nxm://...), for the window.</summary>
        public static string StartLink;
#endif

        /// <summary>A move package this start was given (double-clicked), for the window to restore.</summary>
        public static string StartMove;

        static System.Threading.Mutex single;

        /// <summary>One window: a start while it runs brings it forward and ends.</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            Hd2Mod.PluginHost.Lang = () => Texts.Lang;      // plugins answer in the window's language
#if HAS_NEXUS
            StartLink = e.Args.FirstOrDefault(a => a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
#endif
            StartMove = e.Args.FirstOrDefault(a => a.IndexOf(".hd2qdmove", StringComparison.OrdinalIgnoreCase) >= 0 && System.IO.File.Exists(a));
            // --after-update PID: started by the version this one replaced (SelfUpdate.Install),
            // which is ending: wait for it, so this start is the window and not a second one
            int at = Array.IndexOf(e.Args, "--after-update");
            int oldPid = 0;
            bool afterUpdate = at >= 0 && at + 1 < e.Args.Length && int.TryParse(e.Args[at + 1], out oldPid);
            if (afterUpdate)
                try { using (var old = System.Diagnostics.Process.GetProcessById(oldPid)) old.WaitForExit(120000); }
                catch (ArgumentException) { }                       // it has ended already
            // the first window holds this; a second start knows at once (no wait on the pipe).
            // After an update the old version holds it until it has ended: waited for, so a slow
            // end (a plugin finishing, a slow disk) does not make this start a second one that
            // hands itself to the ending window and leaves none open
            bool first;
            if (afterUpdate)
            {
                single = new System.Threading.Mutex(false, Pipe);
                try { first = single.WaitOne(TimeSpan.FromMinutes(2)); }
                catch (System.Threading.AbandonedMutexException) { first = true; }   // ended without letting go: ours now
            }
            else single = new System.Threading.Mutex(true, Pipe, out first);
            // --tray: started with Windows, into the notification area; --exit: a window put away
            // there ends (for scripts that replace this program's file)
            bool tray = e.Args.Contains("--tray"), exit = e.Args.Contains("--exit");
            if (!first && tray) { Shutdown(); return; }            // one runs already: it stays as it is
            if (first && exit) { Shutdown(); return; }             // none runs: nothing to end
            if (!first)
            {
                // the window that comes forward may take the foreground this start was given
                AllowSetForegroundWindow(ASFW_ANY);
                try
                {
                    using (var c = new NamedPipeClientStream(".", Pipe, PipeDirection.Out))
                    {
                        c.Connect(5000);
#if HAS_NEXUS
                        var bytes = Encoding.UTF8.GetBytes((exit ? ExitLine : StartLink ?? StartMove ?? "") + "\n");
#else
                        var bytes = Encoding.UTF8.GetBytes((exit ? ExitLine : StartMove ?? "") + "\n");
#endif
                        c.Write(bytes, 0, bytes.Length);
                        c.Flush();
                    }
                    Shutdown();
                    return;
                }
                catch (Exception x) when (x is TimeoutException || x is IOException) { }   // it does not answer: open anyway
                if (exit) { Shutdown(); return; }                   // but never a window for --exit
            }
            base.OnStartup(e);
            // what an update left beside this program's files (the old version, renamed)
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            Hd2Mod.SelfUpdate.CleanOld(exe, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe), "hd2qd.exe"));
            // the manager's log: when the window ran, and what broke it
            Hd2Mod.ManagerLog.Where = "window";
            Hd2Mod.ManagerLog.Info("window started: HD2 Mod Quick Deploy " + Hd2Mod.StateDoc.Version + " (" + (Hd2Mod.SelfUpdate.Official ? "official build" : "development build") + ")");
            DispatcherUnhandledException += (s, x) => Hd2Mod.ManagerLog.Error("the window stopped on an error:\n" + x.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, x) => Hd2Mod.ManagerLog.Error("the window stopped on an error:\n" + x.ExceptionObject);
            // Windows signing out or shutting down: the window closes for good, not into the tray
            SessionEnding += (s, x) => QuickDeploy.MainWindow.Leaving = true;
            shown = true;
            var w = new MainWindow();
            if (tray) w.StartInTray();
            w.Show();
        }

        /// <summary>The line --exit sends: no link starts with a control character.</summary>
        public const string ExitLine = "\u0001exit";

        const int ASFW_ANY = -1;
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

        static bool shown;

        protected override void OnExit(ExitEventArgs e)
        {
            if (shown) Hd2Mod.ManagerLog.Info("window closed");
            base.OnExit(e);
        }
    }
}
