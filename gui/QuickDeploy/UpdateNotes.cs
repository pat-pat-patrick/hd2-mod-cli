using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Hd2Mod;

namespace QuickDeploy
{
    // What a newer version of this program brings, in the window's language (the user,
    // 2026-11-02: the notes give the reason to update). Shown by itself once per new version,
    // after the start; Settings, About opens it again while that version is newer.
    public partial class MainWindow
    {
        Window notesWindow;

        /// <summary>The notes of a newer version found by the last check, if they have not been shown
        /// for that version yet: after the window has settled, never in its way at the start.</summary>
        void NotesAtStart()
        {
            var u = SelfUpdate.Last();
            if (!SelfUpdate.NotesDue(u)) return;
            try { SelfUpdate.NotesShown(u.Latest); }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { return; }   // shown next time instead
            Dispatcher.BeginInvoke(new Action(() => ShowUpdateNotes(u)), DispatcherPriority.ApplicationIdle);
        }

        /// <summary>A small window: the version that is out, what each version newer than this one
        /// changed (newest first), and the way to its download page.</summary>
        void ShowUpdateNotes(SelfUpdateInfo u)
        {
            if (notesWindow != null) { notesWindow.Activate(); return; }
            var root = new DockPanel { Margin = new Thickness(28, 24, 28, 20) };
            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = Texts.F("notes_title", Bare(u.Latest) + (u.Prerelease ? Texts.Get("self_pre") : "")), FontSize = 22, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
            head.Children.Add(Small(Texts.F("notes_now", StateDoc.Version), new Thickness(0, 4, 0, 0)));
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var body = new StackPanel();
            if (u.Notes.Count == 0) body.Children.Add(Small(Texts.Get("notes_none"), new Thickness(0, 0, 0, 0)));
            for (int i = 0; i < u.Notes.Count; i++)
            {
                var (tag, _, text) = u.Notes[i];
                var (said, same) = SelfUpdate.NotesIn(text, Texts.Zh);
                body.Children.Add(new TextBlock { Text = Bare(tag), FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, i == 0 ? 0 : 22, 0, 2) });
                // a release text often opens with a title that says the name and this version again
                said = Regex.Replace(said, @"\A#{1,3}[^\n]*" + Regex.Escape(Bare(tag)) + @"[^\n]*\n*", "");
                if (same && Texts.Zh && said.Length > 0 && !Regex.IsMatch(said, @"\p{IsCJKUnifiedIdeographs}"))
                    body.Children.Add(Small(Texts.Get("notes_english"), new Thickness(0, 0, 0, 4)));
                if (said.Length == 0) body.Children.Add(Small(Texts.Get("notes_none"), new Thickness(0, 4, 0, 0)));
                else Markdown(said, body);
            }
            root.Children.Add(new Border
            {
                BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 1, 0, 1), Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 12, 0, 12),
                Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) },
            });

            var w = new Window
            {
                Title = Texts.F("notes_title", Bare(u.Latest)) + " · HD2 Mod Quick Deploy", Width = 680, Height = 640, MinWidth = 480, MinHeight = 360,
                Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Background = B("Ground900"), Foreground = B("Ink"), FontFamily = FontFamily, Content = root, Icon = Icon,
            };
            if (!string.IsNullOrEmpty(u.Page))
            {
                var open = new Button { Content = Texts.Get("self_open"), Style = (Style)FindResource("Primary"), MinHeight = 38, Padding = new Thickness(18, 0, 18, 0), Margin = new Thickness(0, 0, 8, 0), ToolTip = u.Page };
                var page = u.Page;
                open.Click += (s, e) => { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); w.Close(); };
                buttons.Children.Add(open);
            }
            var later = new Button { Content = Texts.Get("notes_later"), Style = (Style)FindResource("Ghost"), MinHeight = 38, Padding = new Thickness(18, 0, 18, 0) };
            later.Click += (s, e) => w.Close();
            buttons.Children.Add(later);
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.SourceInitialized += (s, e) => DarkTitle(w);
            w.Closed += (s, e) => notesWindow = null;
            notesWindow = w;
            w.Show();
        }

        /// <summary>A tag's version without the "v" ("v0.15.0" -> "0.15.0", as the window's own version reads).</summary>
        static string Bare(string tag) => tag != null && tag.Length > 1 && (tag[0] == 'v' || tag[0] == 'V') && char.IsDigit(tag[1]) ? tag.Substring(1) : tag;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>The system's dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE, 20; 19 before
        /// Windows 10 20H1) on a window drawn with the system's frame.</summary>
        static void DarkTitle(Window w)
        {
            var h = new WindowInteropHelper(w).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
        }

        /// <summary>The part of Markdown release notes use: headings, paragraphs, "- " items (their
        /// following indented lines joined), **bold**, `code` and [links](...) as their words.</summary>
        void Markdown(string text, StackPanel into)
        {
            var blocks = new List<(string Kind, string Text)>();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length == 0) { blocks.Add(("gap", "")); continue; }
                var h = Regex.Match(line, @"^#{1,6}\s+(.*)$");
                if (h.Success) { blocks.Add(("head", h.Groups[1].Value)); continue; }
                var item = Regex.Match(line, @"^\s*[-*]\s+(.*)$");
                if (item.Success) { blocks.Add(("item", item.Groups[1].Value)); continue; }
                int last = blocks.Count - 1;
                if (last >= 0 && (blocks[last].Kind == "item" || blocks[last].Kind == "para"))
                    blocks[last] = (blocks[last].Kind, blocks[last].Text + (Texts.Zh && Regex.IsMatch(line, @"^\s*\p{IsCJKUnifiedIdeographs}") ? "" : " ") + line.Trim());
                else blocks.Add(("para", line.Trim()));
            }
            foreach (var (kind, words) in blocks)
            {
                if (kind == "gap") continue;
                var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21 };
                Inlines(words, t);
                if (kind == "head")
                {
                    t.FontSize = 15;
                    t.FontWeight = FontWeights.Bold;
                    t.Margin = new Thickness(0, 12, 0, 4);
                    into.Children.Add(t);
                }
                else if (kind == "item")
                {
                    var row = new DockPanel { Margin = new Thickness(4, 3, 0, 3) };
                    var dot = new TextBlock { Text = "•", FontSize = 14, Foreground = B("Yellow"), Margin = new Thickness(0, 0, 10, 0) };
                    DockPanel.SetDock(dot, Dock.Left);
                    row.Children.Add(dot);
                    row.Children.Add(t);
                    into.Children.Add(row);
                }
                else
                {
                    t.Foreground = B("Muted");
                    t.Margin = new Thickness(0, 4, 0, 4);
                    into.Children.Add(t);
                }
            }
        }

        void Inlines(string words, TextBlock into)
        {
            words = Regex.Replace(words, @"\[([^\]]+)\]\([^)]*\)", "$1");
            foreach (var part in Regex.Split(words, @"(\*\*[^*]+\*\*|`[^`]+`)"))
            {
                if (part.Length == 0) continue;
                if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                    into.Inlines.Add(new Run(part.Substring(2, part.Length - 4)) { FontWeight = FontWeights.Bold });
                else if (part.StartsWith("`") && part.EndsWith("`") && part.Length > 2)
                    into.Inlines.Add(new Run(part.Substring(1, part.Length - 2)) { FontFamily = Mono });
                else into.Inlines.Add(new Run(part));
            }
        }
    }
}
