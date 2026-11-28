using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hd2Mod;

namespace QuickDeploy
{
    /// <summary>A thin progress bar in the window's colours: a share of its width filled, or, with
    /// no share known (unpacking, adding), a short piece running across it. A WPF ProgressBar
    /// would bring the system's own look.</summary>
    sealed class Bar : Grid
    {
        readonly Border fill;
        readonly TranslateTransform slide = new TranslateTransform();
        double? share = 0;

        public Bar(double height)
        {
            Height = height;
            ClipToBounds = true;
            Background = (Brush)Application.Current.Resources["Grid"];
            fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)Application.Current.Resources["Blue"], RenderTransform = slide, Width = 0 };
            Children.Add(fill);
            SizeChanged += (s, e) => Draw();
        }

        /// <summary>How much is done, 0 to 1; null while it cannot be said.</summary>
        public void Set(double? done)
        {
            if (done == share && done != null) return;
            bool wasMoving = share == null;
            share = done == null ? (double?)null : Math.Max(0, Math.Min(1, done.Value));
            if (share == null && wasMoving) return;
            Draw();
        }

        void Draw()
        {
            double w = ActualWidth;
            if (share != null)
            {
                slide.BeginAnimation(TranslateTransform.XProperty, null);
                slide.X = 0;
                fill.Width = w * share.Value;
                return;
            }
            fill.Width = Math.Max(24, w * 0.3);
            if (!Motion.On) { slide.BeginAnimation(TranslateTransform.XProperty, null); slide.X = (w - fill.Width) / 2; return; }
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-fill.Width, w, TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    /// <summary>Every download the window makes, one at a time and with the window free (the user,
    /// 2026-11-23: "都改，用进度条"): a Nexus Mods file, a mod's update from GitHub, GameBanana,
    /// ModWorkshop or a direct link, a file added from Find a mod. Each goes the same way:
    /// downloaded with its progress, unpacked, then put in the library in the changes' turn; the
    /// stage and a bar show in the footer, on the download page's card and, for Find a mod, in
    /// its own card.</summary>
    public partial class MainWindow
    {
        /// <summary>The download going now.</summary>
        sealed class Job
        {
            public string Title, Site;
            /// <summary>"download", "unpack", "add".</summary>
            public string Stage = "download";
            public long Done, Total;
            public readonly Stopwatch Clock = Stopwatch.StartNew();
            public readonly Stopwatch StageClock = Stopwatch.StartNew();
            public bool FromFind;
        }

        Job job;
        // the bars and words showing the job now (the footer's, the cards'): drawn anew with each page
        readonly List<(Bar Bar, TextBlock Words)> jobShown = new List<(Bar, TextBlock)>();
        Bar footBar;
        DispatcherTimer jobTicker;
        // one download at a time, apart from the queue of changes
        readonly SemaphoreSlim fetchGate = new SemaphoreSlim(1, 1);
        // the mods whose update is on its way: a second click waits for nothing
        readonly HashSet<string> fetching = new HashSet<string>();

        /// <summary>The job's stage in words.</summary>
        string JobWords(bool title = true)
        {
            var j = job;
            if (j == null) return "";
            string what;
            switch (j.Stage)
            {
                case "unpack": what = Texts.F("nx_unpacking", (int)j.StageClock.Elapsed.TotalSeconds); break;
                case "add": what = Texts.Get(j.FromFind ? "dl_adding_drop" : "nx_adding"); break;
                // the move package (MoveView): bytes read, of all of them
                case "pack":
                case "restore":
                    what = Texts.F("mv_progress", Texts.Size(j.Done), Texts.Size(j.Total), j.Total > 0 ? j.Done * 100 / j.Total : 0);
                    break;
                default:
                    what = j.Total > 0 ? Texts.F("dl_progress", j.Site, Texts.Size(j.Done), Texts.Size(j.Total), j.Done * 100 / j.Total)
                                       : Texts.F("dl_progress_n", j.Site, Texts.Size(j.Done));
                    break;
            }
            return title && !string.IsNullOrEmpty(j.Title) ? j.Title + " · " + what : what;
        }

        double? JobShare() => job == null ? 0 : (job.Stage == "download" || job.Stage == "pack" || job.Stage == "restore") && job.Total > 0
                                                ? job.Done / (double)job.Total : (double?)null;

        /// <summary>The job as it stands, wherever it shows.</summary>
        void ShowJob()
        {
            if (footBar == null && FootText.Parent is StackPanel foot)
            {
                footBar = new Bar(4) { Width = 160, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
                foot.Children.Add(footBar);
            }
            if (job == null)
            {
                fetchLine = null;
                if (footBar != null) footBar.Visibility = Visibility.Collapsed;
                return;
            }
            var text = JobWords();
            var share = JobShare();
            fetchLine = text;
            Foot(text, "Blue");
            if (footBar != null) { footBar.Visibility = Visibility.Visible; footBar.Set(share); }
            foreach (var (bar, words) in jobShown)
            {
                bar.Set(share);
                if (words != null) words.Text = JobWords(false);
            }
        }

        /// <summary>A bar and its words for a card, kept up to date while the job goes on.</summary>
        UIElement JobCard(double barHeight)
        {
            var panel = new StackPanel();
            var words = new TextBlock { FontFamily = Mono, FontSize = 13, Foreground = B("Blue"), TextWrapping = TextWrapping.Wrap, Text = JobWords(false) };
            var bar = new Bar(barHeight) { Margin = new Thickness(0, 10, 0, 0) };
            bar.Set(JobShare());
            panel.Children.Add(words);
            panel.Children.Add(bar);
            jobShown.Add((bar, words));
            return panel;
        }

        /// <summary>A download begun: waits for the one before it, then shows.</summary>
        async Task BeginJob(string title, string site, bool fromFind = false)
        {
            await fetchGate.WaitAsync();
            job = new Job { Title = title, Site = site, FromFind = fromFind };
            jobTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            jobTicker.Tick += (s, e) => { if (job?.Stage == "unpack") ShowJob(); };
            jobTicker.Start();
            if (shownPage == "download") RenderDownloads();
            ShowJob();
        }

        /// <summary>The download's progress, from any thread; said at most every 128 KB.</summary>
        Action<long, long> JobProgress()
        {
            long shown = -1;
            var j = job;
            return (done, total) =>
            {
                if (done - shown < 128 * 1024 && done != total) return;
                shown = done;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (job != j) return;
                    j.Done = done; j.Total = total;
                    ShowJob();
                }));
            };
        }

        void JobStage(string stage)
        {
            if (job == null) return;
            job.Stage = stage;
            job.StageClock.Restart();
            ShowJob();
        }

        void EndJob()
        {
            jobTicker?.Stop();
            jobTicker = null;
            job = null;
            ShowJob();
            fetchGate.Release();
            if (shownPage == "download") RenderDownloads();
            Refresh();
        }

        /// <summary>A mod's newer version from GitHub, GameBanana, ModWorkshop or a direct link:
        /// downloaded and unpacked with the window free, then put in its place (its place in the
        /// order, on/off and options kept) in the changes' turn.</summary>
        async void FetchUpdate(ModRow m, UpdateInfo u)
        {
            if (!fetching.Add(m.Id)) return;
            try
            {
                bool github = m.Channel == "GitHub";
                await BeginJob(m.Name, github ? "GitHub" : m.Channel ?? "");
                string file = null, page = null, unpacked = null, withIt = null;
                Mod mod = null;
                try
                {
                    var progress = JobProgress();
                    var r = await Task.Run(() => Cli.Call(() =>
                    {
                        if (github) file = Updates.FetchGitHub(u, progress);
                        else (file, page) = Sites.FetchUpdate(m.Id, u, progress);
                        return "";
                    }));
                    if (r.Ok)
                    {
                        JobStage("unpack");
                        r = await Task.Run(() => Cli.Call(() => { unpacked = Ops.SourceFolder(file, Store.Config()); return ""; }));
                    }
                    if (r.Ok)
                    {
                        JobStage("add");
                        r = await Queued(() => Cli.Call(() =>
                        {
                            mod = github ? Updates.PutGitHub(m.Id, u, file, unpacked) : Sites.PutUpdate(m.Id, page, file, unpacked);
                            return Texts.F("up_done", mod.Name, u.Latest);
                        }));
                    }
                    if (!Refused(r) && r.Ok)
                    {
                        lastDone = Texts.F("took", r.Out, Seconds(job.Clock.Elapsed.TotalSeconds));
                        updates.Remove(m.Id);
                        updated.Add(m.Id);
                        withIt = mod?.Id ?? m.Id;
                    }
                }
                finally
                {
                    if (file != null) try { System.IO.File.Delete(file); } catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { }
                    EndJob();
                }
                // what the new version needs, updated with it once its own job has ended
                if (withIt != null) PlanAfterUpdate(withIt);
            }
            finally { fetching.Remove(m.Id); }
        }
    }
}
