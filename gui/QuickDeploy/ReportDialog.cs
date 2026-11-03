using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hd2Mod;

namespace QuickDeploy
{
    // The problem report's window (the user, 2026-11-03): the kind of problem, a few words on
    // it, what will be sent; then Upload, or Save to desktop for a player who sends it
    // themselves. The report is written when the window opens, so the list is what goes.
    public partial class MainWindow
    {
        ChromeWindow reportWindow;

        /// <summary>HD2QD_CLICK_TEST=report (measuring only): the report window opened 3 s after
        /// start; with HD2QD_REPORT_KIND, that kind chosen and a note typed; with
        /// HD2QD_REPORT_UPLOAD=1, Upload clicked once the report is written.</summary>
        void ReportDialogTest()
        {
            if (Environment.GetEnvironmentVariable("HD2QD_CLICK_TEST") != "report") return;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                OpenReportDialog();
                var kind = Environment.GetEnvironmentVariable("HD2QD_REPORT_KIND");
                if (kind == null) return;
                IEnumerable<T> Find<T>(DependencyObject d) where T : DependencyObject
                {
                    foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
                    {
                        if (c is T x) yield return x;
                        foreach (var r in Find<T>(c)) yield return r;
                    }
                }
                var root = (DependencyObject)reportWindow.Content;
                Find<RadioButton>(root).ElementAt(Array.IndexOf(Report.Kinds, kind)).IsChecked = true;
                Find<TextBox>(root).First().Text = "第二次进任务时游戏崩溃，换了护甲之后出现。";
                // HD2QD_REPORT_OPEN=1: the list of files unfolded once the report is written
                bool unfold = Environment.GetEnvironmentVariable("HD2QD_REPORT_OPEN") == "1", send = Environment.GetEnvironmentVariable("HD2QD_REPORT_UPLOAD") == "1";
                if (!unfold && !send) return;
                var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                wait.Tick += (s2, e2) =>
                {
                    var up = Find<Button>(root).FirstOrDefault(b => b.Style == (Style)FindResource("Primary"));
                    if (up == null || !up.IsEnabled) return;
                    wait.Stop();
                    var click = new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent);
                    if (unfold) Find<Button>(root).First(b => b.Content is StackPanel sp && sp.Children.OfType<System.Windows.Shapes.Path>().Any()).RaiseEvent(click);
                    if (send) up.RaiseEvent(click);
                };
                wait.Start();
            };
            t.Start();
        }

        void OpenReportDialog()
        {
            if (reportWindow != null) { reportWindow.Activate(); return; }
            var root = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };
            var w = new ChromeWindow(this, Texts.Get("report_title"), root) { Width = 680, SizeToContent = SizeToContent.Height, MinWidth = 480, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
            reportWindow = w;
            w.Closed += (s, e) => reportWindow = null;

            string zip = null, summary = null, kind = null, failed = null;
            bool uploading = false;
            var status = new StackPanel();
            var upload = new Button { Style = (Style)FindResource("Primary"), MinHeight = 38, Padding = new Thickness(22, 0, 22, 0), Margin = new Thickness(0, 0, 8, 0) };
            var save = new Button { Content = Texts.Get("report_save"), Style = (Style)FindResource("Ghost"), MinHeight = 38, Padding = new Thickness(18, 0, 18, 0) };

            // what kind of problem: a choice, the one thing asked for
            root.Children.Add(Label("report_kind", new Thickness(0, 0, 0, 6)));
            // one row in a frame, as the settings' choices
            var kinds = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var k in Report.Kinds)
            {
                var b = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "reportkind", Content = Texts.Get("report_kind_" + k), MinWidth = 64 };
                var key = k;
                b.Checked += (s, e) => { kind = key; Fill(); };
                kinds.Children.Add(b);
            }
            root.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16), Child = kinds });

            // a few words, optional: required, they would be any words at all
            root.Children.Add(Label("report_note_label", new Thickness(0, 0, 0, 6)));
            var note = new TextBox
            {
                Style = (Style)FindResource("InputArea"), Tag = Texts.Get("report_note_hint"), Height = 108,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 500, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            root.Children.Add(note);

            // what is sent: the report's files, folded; what is never in it, always said
            var contents = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            root.Children.Add(contents);
            root.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            root.Children.Add(buttons);
            buttons.Children.Add(upload);
            buttons.Children.Add(save);
            var privacy = new TextBlock { Text = Texts.Get("report_privacy"), FontSize = 12, Foreground = B("Muted"), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), TextDecorations = TextDecorations.Underline };
            privacy.MouseLeftButtonUp += (s, e) => { try { Process.Start(new ProcessStartInfo(Report.UploadUrl.TrimEnd('/') + "/privacy") { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { } };
            buttons.Children.Add(privacy);

            bool open = false;
            void Contents()
            {
                contents.Children.Clear();
                if (zip == null) { contents.Children.Add(Small(Texts.Get(failed == null ? "report_making" : "report_not_made"), new Thickness(0))); return; }
                var files = Report.Contents(zip);
                var head = new Button { Template = Plain(), Cursor = Cursors.Hand, FocusVisualStyle = null, HorizontalAlignment = HorizontalAlignment.Left };
                var words = new TextBlock { Text = Texts.F("report_contents", files.Count, Texts.Size(new FileInfo(zip).Length)), FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center };
                var arrow = new System.Windows.Shapes.Path
                {
                    Data = System.Windows.Media.Geometry.Parse(open ? "M0,5 L5,0 L10,5" : "M0,0 L5,5 L10,0"), Stroke = B("Muted"), StrokeThickness = 1.6,
                    Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(words);
                line.Children.Add(arrow);
                head.Content = line;
                head.Click += (s, e) => { open = !open; Contents(); };
                contents.Children.Add(head);
                if (open)
                {
                    var list = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                    list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    foreach (var (name, bytes) in files)
                    {
                        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        var n = new TextBlock { Text = name, FontFamily = Mono, FontSize = 11, Foreground = B("Ink"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 1) };
                        var z = new TextBlock { Text = Texts.Size(bytes), FontSize = 11, Foreground = B("Muted"), Margin = new Thickness(16, 1, 0, 1), HorizontalAlignment = HorizontalAlignment.Right };
                        Grid.SetRow(n, list.RowDefinitions.Count - 1);
                        Grid.SetRow(z, list.RowDefinitions.Count - 1);
                        Grid.SetColumn(z, 1);
                        list.Children.Add(n);
                        list.Children.Add(z);
                    }
                    // a long list scrolls: the window stays on the screen
                    contents.Children.Add(new ScrollViewer { Content = list, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });
                }
                contents.Children.Add(Small(Texts.Get("report_never"), new Thickness(0, 6, 0, 0)));
            }

            void Fill()
            {
                status.Children.Clear();
                if (failed != null)
                    status.Children.Add(new TextBlock { Text = failed, Foreground = B("Yellow"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
                upload.Content = Texts.Get(uploading ? "report_uploading" : "report_upload");
                upload.IsEnabled = zip != null && kind != null && !uploading;
                upload.Opacity = upload.IsEnabled ? 1 : 0.4;        // Primary has no look of its own for it
                // why it cannot be clicked yet
                upload.ToolTip = zip != null && kind == null ? Texts.Get("report_kind_first") : null;
                ToolTipService.SetShowOnDisabled(upload, true);
                save.IsEnabled = zip != null && !uploading;
            }

            // sent: the number to quote in place of the form, and nothing left to do
            void Sent(string id)
            {
                root.Children.Clear();
                root.Children.Add(new TextBlock { Text = Texts.Get("report_sent"), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = B("Ink") });
                root.Children.Add(Small(Texts.Get("report_id_note"), new Thickness(0, 6, 0, 14)));
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var code = Selectable.Text(id, 22, "Yellow", Mono);
                code.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(code);
                var copy = new Button { Content = Texts.Get("report_copy"), Style = (Style)FindResource("Ghost"), MinHeight = 34, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(16, 0, 0, 0) };
                copy.Click += (s, e) => { try { Clipboard.SetText(id); copy.Content = Texts.Get("report_copied"); } catch (Exception) { } };
                row.Children.Add(copy);
                root.Children.Add(row);
                var close = new Button { Content = Texts.Get("report_close"), Style = (Style)FindResource("Ghost"), MinHeight = 38, Padding = new Thickness(18, 0, 18, 0), Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                close.Click += (s, e) => w.Close();
                root.Children.Add(close);
            }

            upload.Click += async (s, e) =>
            {
                uploading = true;
                failed = null;
                Fill();
                var text = note.Text;
                var k = kind;
                var path = zip;
                string id = null, code = null;
                await Task.Run(() =>
                {
                    try { id = Report.Upload(path, k, text); }
                    catch (Report.UploadFailed x) { code = x.Code; }
                    catch (Exception x) when (x is IOException || x is UnauthorizedAccessException) { code = "server"; }
                });
                uploading = false;
                if (id != null) { ManagerLog.Info("report uploaded: " + id); Sent(id); return; }
                ManagerLog.Warn("report upload failed: " + code);
                failed = Texts.Get("report_fail_" + code);
                Fill();
            };

            // the player sends it themselves: a copy on the desktop, its summary on the clipboard
            save.Click += (s, e) =>
            {
                try
                {
                    var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    var to = Path.Combine(Directory.Exists(desk) ? desk : Store.Home, Path.GetFileName(zip));
                    File.Copy(zip, to, true);
                    try { Clipboard.SetText(summary); } catch (Exception) { }      // another program holding the clipboard: the file is enough
                    ShowInFolder(to);
                    failed = null;
                    status.Children.Clear();
                    status.Children.Add(new TextBlock { Text = Texts.Get("report_saved"), Foreground = B("Ink"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
                }
                catch (Exception x) when (x is IOException || x is UnauthorizedAccessException)
                {
                    failed = Texts.F("failed", x.Message);
                    Fill();
                }
            };

            Contents();
            Fill();
            w.Show();

            // written in the background: the window is there at once
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                (string Path, string Summary) made = default;
                var r = await Queued(() => Cli.Call(() => { made = Report.Draft(Library.Load(), Store.GameDir(null, false)); return made.Path; }));
                if (reportWindow != w) return;
                if (r.Ok) { zip = made.Path; summary = made.Summary; }
                else failed = Texts.F("failed", r.Message);
                Contents();
                Fill();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
