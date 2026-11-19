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

        static System.Threading.Mutex single;

        /// <summary>One window: a start while it runs brings it forward and ends.</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            Hd2Mod.PluginHost.Lang = () => Texts.Lang;      // plugins answer in the window's language
#if HAS_NEXUS
            StartLink = e.Args.FirstOrDefault(a => a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
#endif
            // the first window holds this; a second start knows at once (no wait on the pipe)
            single = new System.Threading.Mutex(true, Pipe, out bool first);
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
                        var bytes = Encoding.UTF8.GetBytes((exit ? ExitLine : StartLink ?? "") + "\n");
#else
                        var bytes = Encoding.UTF8.GetBytes((exit ? ExitLine : "") + "\n");
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
