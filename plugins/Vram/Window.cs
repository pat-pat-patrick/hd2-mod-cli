using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hd2Mod.Vram
{
    public static partial class Plugin
    {
        static Window open;

        /// <summary>The plugin's window, beside the manager's (owner). host, as the manager gives
        /// every window plugin: "home" (its settings folder: the game table's cache), "data" (the
        /// game's data folder), "zh" (the language), "mods" (a Func giving the enabled mods in load
        /// order, as Estimate takes them, each time the window reads them). One window at a time:
        /// opening again brings it forward and reads again.</summary>
        public static void Open(Window owner, Dictionary<string, object> host)
        {
            var mods = (Func<List<Dictionary<string, object>>>)host["mods"];
            string home = (string)host["home"], data = (string)host["data"];
            bool zh = host.TryGetValue("zh", out var z) && z is bool b && b;
            if (open != null) { open.Activate(); ((View)open.Tag).Read(); return; }
            var v = new View(mods, home, data, zh);
            var w = new Window
            {
                Title = Title(zh) + " · HD2 Mod Quick Deploy", Width = 1040, Height = 780, MinWidth = 720, MinHeight = 480,
                Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = true,
                Background = v.B("Ground900"), Foreground = v.B("Ink"), Content = v.Root, Tag = v, Icon = owner?.Icon,
                FontFamily = owner?.FontFamily ?? SystemFonts.MessageFontFamily,
            };
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.SourceInitialized += (s, e) => DarkTitleBar(w);
            w.Closed += (s, e) => open = null;
            open = w;
            w.Show();
            v.Read();
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>The system's dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE, 20; 19 before
        /// Windows 10 20H1), as the manager's own dark window looks. Ignored where Windows has none.</summary>
        static void DarkTitleBar(Window w)
        {
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
        }

        /// <summary>The window's content: a summary, then one line per mod, most added first; a
        /// click on a line shows its biggest resources.</summary>
        sealed class View
        {
            readonly Func<List<Dictionary<string, object>>> mods;
            readonly string home, data;
            readonly bool zh;
            public readonly DockPanel Root = new DockPanel { Margin = new Thickness(28, 24, 28, 24) };
            readonly StackPanel head = new StackPanel();
            readonly StackPanel list = new StackPanel();
            readonly HashSet<string> opened = new HashSet<string>();
            bool reading;

            public View(Func<List<Dictionary<string, object>>> mods, string home, string data, bool zh)
            {
                this.mods = mods; this.home = home; this.data = data; this.zh = zh;
                DockPanel.SetDock(head, Dock.Top);
                Root.Children.Add(head);
                Root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) });
            }

            public Brush B(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
            Style S(string key) => Application.Current?.TryFindResource(key) as Style;
            string T(string en, string cn) => zh ? cn : en;

            public async void Read()
            {
                if (reading) return;
                reading = true;
                Head(T("Reading the game's own textures and models…", "正在读取游戏原版的贴图和模型…"), null);
                var bar = new ProgressBar { Height = 6, Maximum = 1, Margin = new Thickness(0, 12, 0, 0), Foreground = B("Yellow"), Background = B("Grid"), BorderThickness = new Thickness(0) };
                head.Children.Add(bar);
                list.Children.Clear();
                Dictionary<string, object> r;
                try
                {
                    var given = mods();
                    var progress = new Progress<double>(p => bar.Value = p);
                    r = await Task.Run(() => Estimate(home, data, given, p => ((IProgress<double>)progress).Report(p)));
                }
                catch (Exception e)
                {
                    Head(T("The estimate failed: ", "估算失败：") + e.Message, null);
                    reading = false;
                    return;
                }
                reading = false;
                Show(r);
            }

            void Head(string line, Dictionary<string, object> total)
            {
                head.Children.Clear();
                var top = new DockPanel();
                var again = new Button { Content = T("Read again", "重新读取"), Style = S("Ghost"), MinHeight = 36, Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
                again.Click += (s, e) => Read();
                DockPanel.SetDock(again, Dock.Right);
                top.Children.Add(again);
                top.Children.Add(new TextBlock { Text = Title(zh), FontSize = 26, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(top);
                head.Children.Add(Small(T(
                    "An estimate from the files, not a measurement. Resident: what a texture or model takes once it is loaded. At most: with the finer texture levels the game streams in when they are needed. What is loaded at all depends on the mission and the graphics settings.",
                    "按文件估算，不是实测。常驻：贴图或模型加载后一定占用的部分。最多：再加上游戏按需加载的高清层级。实际加载哪些资源，取决于任务和画质设置。"), new Thickness(0, 6, 0, 0)));
                if (total != null && L(total, "max") > 0 && L(total, "max") == L(total, "resident"))
                    head.Children.Add(Small(T(
                        "These mods' textures carry no finer levels to stream in: each is on the card whole once loaded, where the game's own keep only the coarse levels resident.",
                        "这些 MOD 的贴图没有拆出按需加载的高清层级，加载后整张常驻显存；原版贴图平时只常驻低清层级。"), new Thickness(0, 4, 0, 0)));
                head.Children.Add(new TextBlock { Text = line, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });
            }

            TextBlock Small(string text, Thickness margin) =>
                new TextBlock { Text = text, FontSize = 12, LineHeight = 16, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, Margin = margin };

            static string Mb(long bytes)
            {
                double mb = bytes / 1048576.0;
                if (Math.Abs(mb) >= 1024) return (mb / 1024).ToString("0.00") + " GB";
                if (Math.Abs(mb) >= 1) return mb.ToString(Math.Abs(mb) >= 100 ? "0" : "0.0") + " MB";
                return Math.Round(bytes / 1024.0).ToString("0") + " KB";
            }

            string Extra(long bytes) => bytes == 0 ? T("same as the game", "与原版相同")
                : bytes > 0 ? T("+" + Mb(bytes), "多 " + Mb(bytes)) : T("−" + Mb(-bytes), "少 " + Mb(-bytes));

            static long L(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v is long n ? n : 0;
            static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as string : null;

            void Show(Dictionary<string, object> r)
            {
                var rows = ((List<object>)r["mods"]).Cast<Dictionary<string, object>>().ToList();
                var shown = rows.Where(m => L(m, "textures") + L(m, "models") > 0).OrderByDescending(m => L(m, "extra")).ThenBy(m => Str(m, "name")).ToList();
                var total = (Dictionary<string, object>)r["total"];
                Head(shown.Count == 0
                        ? T("No enabled mod brings textures or models.", "已启用的 MOD 都没有贴图或模型。")
                        : T(shown.Count + " mods with textures or models: at most " + Mb(L(total, "max")) + ", " + Extra(L(total, "extra")) + " than the game's own copies they replace",
                            shown.Count + " 个 MOD 带有贴图或模型：最多约 " + Mb(L(total, "max")) + "，比它们替换的原版资源" + Extra(L(total, "extra"))),
                     total);
                list.Children.Clear();
                if (shown.Count == 0) return;
                // pictures from outside: the first mod's details open
                if (opened.Count == 0 && Environment.GetEnvironmentVariable("HD2QD_VRAM_OPEN_FIRST") == "1") opened.Add(Str(shown[0], "name"));
                list.Children.Add(Row(null, T("MOD", "MOD"), T("Added", "比原版"), T("At most", "最多"), T("Resident", "常驻"), T("Game's copies", "原版"), T("Textures · models", "贴图 · 模型"), true));
                foreach (var m in shown)
                {
                    var name = Str(m, "name");
                    long extra = L(m, "extra");
                    list.Children.Add(Row(m, name, Extra(extra), Mb(L(m, "max")), Mb(L(m, "resident")), Mb(L(m, "game_max")),
                                          L(m, "textures") + " · " + L(m, "models"), false));
                    if (opened.Contains(name)) list.Children.Add(Details(m));
                }
                int none = rows.Count - shown.Count;
                if (none > 0) list.Children.Add(Small(T(none + " other enabled mods bring no texture or model.", "另有 " + none + " 个已启用的 MOD 没有贴图或模型。"), new Thickness(0, 12, 0, 0)));
            }

            static readonly GridLength[] Columns =
                { new GridLength(1, GridUnitType.Star), new GridLength(130), new GridLength(110), new GridLength(110), new GridLength(110), new GridLength(130) };

            FrameworkElement Row(Dictionary<string, object> m, string name, string extra, string max, string resident, string game, string counts, bool head_)
            {
                var g = new Grid { Margin = new Thickness(0), MinHeight = head_ ? 28 : 40 };
                foreach (var c in Columns) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
                string[] cells = { name, extra, max, resident, game, counts };
                for (int i = 0; i < cells.Length; i++)
                {
                    var t = new TextBlock
                    {
                        Text = cells[i], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                        FontSize = head_ ? 12 : 14, Foreground = head_ ? B("Muted") : B("Ink"),
                        TextAlignment = i == 0 ? TextAlignment.Left : TextAlignment.Right, Margin = new Thickness(i == 0 ? 12 : 0, 0, 12, 0),
                    };
                    if (!head_ && i == 1)
                    {
                        long e = L(m, "extra");
                        t.Foreground = e >= 100L * 1048576 ? B("Yellow") : e < 0 ? B("Blue") : B("Ink");
                        t.FontWeight = FontWeights.SemiBold;
                    }
                    if (!head_ && i == 0 && L(m, "lost") > 0) t.ToolTip = T(L(m, "lost") + " of its resources are overridden by a mod that loads later and are left out.",
                                                                          "其中 " + L(m, "lost") + " 个资源被后加载的 MOD 覆盖，不计入。");
                    Grid.SetColumn(t, i);
                    g.Children.Add(t);
                }
                var line = new Border { Child = g, BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent };
                if (!head_)
                {
                    line.Cursor = Cursors.Hand;
                    var key = name;
                    line.MouseEnter += (s, e) => line.Background = B("Band");
                    line.MouseLeave += (s, e) => line.Background = Brushes.Transparent;
                    line.MouseLeftButtonUp += (s, e) =>
                    {
                        int at = list.Children.IndexOf(line);
                        if (opened.Remove(key)) list.Children.RemoveAt(at + 1);
                        else { opened.Add(key); list.Children.Insert(at + 1, Details(m)); }
                    };
                }
                return line;
            }

            string Layers(long n) => n > 1 ? T(" ×" + n + " layers", " ×" + n + " 层") : "";

            /// <summary>A mod's biggest resources: what each takes, and the game's copy it replaces.</summary>
            FrameworkElement Details(Dictionary<string, object> m)
            {
                var items = ((List<object>)m["items"]).Cast<Dictionary<string, object>>().ToList();
                var box = new StackPanel { Margin = new Thickness(28, 6, 12, 12) };
                foreach (var it in items.Take(20))
                {
                    bool tex = Str(it, "type") == "texture";
                    string what = tex ? T("Texture", "贴图") + (L(it, "w") > 0 ? " " + L(it, "w") + "×" + L(it, "h") + Layers(L(it, "layers")) + " " + Str(it, "format") : "") : T("Model", "模型");
                    bool inGame = it.TryGetValue("in_game", out var ig) && ig is bool b && b;
                    string game = !inGame ? T("new, not in the game", "新增，游戏原版没有")
                        : T("game's copy ", "原版 ") + (tex && L(it, "game_w") > 0 ? L(it, "game_w") + "×" + L(it, "game_h") + Layers(L(it, "game_layers")) + " " + Str(it, "game_format") + " · " : "") + Mb(L(it, "game_max"));
                    var line = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                    var size = new TextBlock { Text = Mb(L(it, "max")), Foreground = B("Ink"), FontSize = 13, MinWidth = 90, TextAlignment = TextAlignment.Right };
                    DockPanel.SetDock(size, Dock.Right);
                    line.Children.Add(size);
                    line.Children.Add(new TextBlock
                    {
                        FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
                        Inlines = { new System.Windows.Documents.Run(what) { Foreground = B("Ink") }, new System.Windows.Documents.Run("  ·  " + game + "  ·  " + Str(it, "id")) { Foreground = B("Muted") } },
                    });
                    box.Children.Add(line);
                }
                if (items.Count > 20) box.Children.Add(Small(T("and " + (items.Count - 20) + " more", "以及另外 " + (items.Count - 20) + " 个"), new Thickness(0, 4, 0, 0)));
                return box;
            }
        }
    }
}
