using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
            // the manager makes the window, so its caption matches the main one; a plain window
            // (the system's caption) when it did not hand one over (an older manager)
            Window w;
            if (host.TryGetValue("window", out var mk) && mk is Func<string, FrameworkElement, Window> make) w = make(Title(zh), v.Root);
            else w = new Window
            {
                Title = Title(zh) + " · HD2 Mod Quick Deploy", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = v.B("Ground900"), Foreground = v.B("Ink"), Content = v.Root, Icon = owner?.Icon,
                FontFamily = owner?.FontFamily ?? SystemFonts.MessageFontFamily,
            };
            w.Width = 1120; w.Height = 820; w.MinWidth = 760; w.MinHeight = 480;
            w.Tag = v;
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.Closed += (s, e) => open = null;
            open = w;
            w.Show();
            v.Read();
        }

        /// <summary>The window's content: a summary, then one line per mod, the ones to check
        /// first; a click on a line shows its findings and its biggest resources.</summary>
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
                Head(T("Reading the textures and models of the mods and the game…", "正在读取 MOD 及原版游戏的贴图与模型…"));
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
                    Head(T("Check failed: ", "检查失败：") + e.Message);
                    reading = false;
                    return;
                }
                reading = false;
                Show(r);
            }

            void Head(string line)
            {
                head.Children.Clear();
                // the caption carries the title; the Read-again button (right), the intro and the
                // status line open the body
                var top = new DockPanel();
                var again = new Button { Content = T("Check again", "重新检查"), Style = S("Ghost"), MinHeight = 36, Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
                again.Click += (s, e) => Read();
                DockPanel.SetDock(again, Dock.Right);
                top.Children.Add(again);
                top.Children.Add(Small(T(
                    "Compares each enabled mod's models and textures with the game resources they replace: detail levels, triangle counts, shadows, culling boxes, lights, mipmaps, compression and video memory use. Based on file analysis, not measured in game.",
                    "将各已启用 MOD 的模型与贴图与其所替换的原版资源对比：细节层级、三角面数、阴影、剔除盒、灯光、mipmap、压缩及显存占用。结果基于文件分析，并非游戏内实测。"), new Thickness(0, 4, 16, 0)));
                head.Children.Add(top);
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

            static string N(long n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

            string Extra(long bytes) => bytes == 0 ? T("same", "相同")
                : bytes > 0 ? "+" + Mb(bytes) : "−" + Mb(-bytes);

            static long L(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is long n ? n : 0;
            static string Str(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v as string : null;
            static List<Dictionary<string, object>> Items(Dictionary<string, object> m) => ((List<object>)m["items"]).Cast<Dictionary<string, object>>().ToList();
            static bool Has(Dictionary<string, object> it, string flag) => it.TryGetValue("flags", out var f) && f is List<object> l && l.Contains(flag);
            static List<long> Tris(Dictionary<string, object> it, string chain, string side) =>
                it.TryGetValue("facts", out var f) && f is Dictionary<string, object> facts && facts.TryGetValue(chain, out var c) && c is Dictionary<string, object> cd
                && cd.TryGetValue(side, out var l) && l is List<object> list ? list.Cast<long>().ToList() : new List<long>();

            static int Rank(string level) => level == "many" ? 0 : level == "waste" ? 1 : 2;

            void Show(Dictionary<string, object> r)
            {
                var rows = ((List<object>)r["mods"]).Cast<Dictionary<string, object>>().ToList();
                var shown = rows.Where(m => L(m, "textures") + L(m, "models") > 0)
                    .OrderBy(m => Rank(Str(m, "level"))).ThenByDescending(m => L(m, "extra")).ThenBy(m => Str(m, "name")).ToList();
                int check = shown.Count(m => Str(m, "level") == "many"), waste = shown.Count(m => Str(m, "level") == "waste");
                Head(shown.Count == 0
                    ? T("No enabled mod contains textures or models.", "已启用的 MOD 均不包含贴图或模型。")
                    : T(shown.Count + " mods contain models or textures: " + check + " to review, " + waste + " inefficient, " + (shown.Count - check - waste) + " normal.",
                        shown.Count + " 个 MOD 包含模型或贴图：" + check + " 个需检查，" + waste + " 个可优化，" + (shown.Count - check - waste) + " 个正常。"));
                list.Children.Clear();
                if (shown.Count == 0) return;
                // pictures from outside: the first mod's details open
                if (opened.Count == 0 && Environment.GetEnvironmentVariable("HD2QD_VRAM_OPEN_FIRST") == "1") opened.Add(Str(shown[0], "name"));
                list.Children.Add(Row(null, true));
                foreach (var m in shown)
                {
                    list.Children.Add(Row(m, false));
                    if (opened.Contains(Str(m, "name"))) list.Children.Add(Details(m));
                }
                int none = rows.Count - shown.Count;
                if (none > 0) list.Children.Add(Small(T(none + " other enabled mods contain no models or textures.", "另有 " + none + " 个已启用的 MOD 不包含模型或贴图。"), new Thickness(0, 12, 0, 0)));
            }

            static readonly GridLength[] Columns =
                { new GridLength(1.1, GridUnitType.Star), new GridLength(110), new GridLength(1, GridUnitType.Star), new GridLength(130), new GridLength(120) };

            (string Text, string Colour) Badge(string level) =>
                level == "many" ? (T("Needs review", "需检查"), "Red") : level == "waste" ? (T("Inefficient", "可优化"), "Yellow") : (T("Normal", "正常"), "Blue");

            FrameworkElement Row(Dictionary<string, object> m, bool header)
            {
                var g = new Grid { MinHeight = header ? 28 : 44 };
                foreach (var c in Columns) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
                void Cell(int col, UIElement e) { Grid.SetColumn(e, col); g.Children.Add(e); }
                TextBlock Text(string s, int col, bool right = false) => new TextBlock
                {
                    Text = s, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = header ? 12 : 14, Foreground = header ? B("Muted") : B("Ink"),
                    TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(col == 0 ? 12 : 0, 0, 12, 0),
                };
                if (header)
                {
                    Cell(0, Text("MOD", 0)); Cell(1, Text(T("Status", "状态"), 1)); Cell(2, Text(T("Main finding", "主要问题"), 2));
                    Cell(3, Text(T("VRAM vs. game", "显存（较原版）"), 3, true)); Cell(4, Text(T("Models · textures", "模型 · 贴图"), 4, true));
                }
                else
                {
                    var name = Text(Str(m, "name"), 0);
                    if (L(m, "lost") > 0) name.ToolTip = T(L(m, "lost") + " of its resources are overridden by a mod loaded later and are not counted.",
                                                         "其中 " + L(m, "lost") + " 个资源被后加载的 MOD 覆盖，未计入。");
                    Cell(0, name);
                    var (bt, bc) = Badge(Str(m, "level"));
                    Cell(1, new Border
                    {
                        BorderBrush = B(bc), BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = bt, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(bc) },
                    });
                    var f = Findings(m);
                    Cell(2, Text(f.Count > 0 ? f[0].Title : T("No problems found", "未发现问题"), 2));
                    var ex = Text(Extra(L(m, "extra")), 3, true);
                    ex.ToolTip = T("Up to " + Mb(L(m, "max")) + "; game resources replaced: " + Mb(L(m, "game_max")),
                                   "最大占用 " + Mb(L(m, "max")) + "；所替换的原版资源：" + Mb(L(m, "game_max")));
                    Cell(3, ex);
                    Cell(4, Text(L(m, "models") + " · " + L(m, "textures"), 4, true));
                }
                var line = new Border { Child = g, BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent };
                if (!header)
                {
                    line.Cursor = Cursors.Hand;
                    var key = Str(m, "name");
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

            string Name(Dictionary<string, object> it)
            {
                var p = Str(it, "path");
                return p != null ? System.Text.RegularExpressions.Regex.Replace(p, "^[^/]*/", "") : T("model ", "模型 ") + Str(it, "id");
            }

            /// <summary>What a mod's findings mean, worst first (asset_check_src page.html's words):
            /// (level "red"/"amber"/"minor", title, text).</summary>
            List<(string Level, string Title, string Text)> Findings(Dictionary<string, object> m)
            {
                var items = Items(m);
                var models = items.Where(it => Str(it, "type") == "model" && Str(it, "error") == null).ToList();
                var red = new List<(string, string, string)>();
                var amber = new List<(string, string, string)>();
                var minor = new List<(string, string, string)>();
                var many = models.Where(it => Str(it, "many") != null && (Has(it, "lod_flat") || Has(it, "tris_heavy") || Has(it, "shadow_heavy"))).ToList();
                foreach (var it in many)
                {
                    List<long> g = Tris(it, "game", "mod"), gg = Tris(it, "game", "game"), s = Tris(it, "shadow", "mod"), sg = Tris(it, "shadow", "game");
                    bool enemy = Str(it, "many") == "enemy";
                    red.Add(("red", T(enemy ? "More triangles on an enemy model" : "More triangles on a map object model", enemy ? "敌人模型三角面数增加" : "地图物件模型三角面数增加"),
                        Name(it) + T(": " + N(g[0]) + " triangles at close range" + (gg.Count > 0 ? " (game: " + N(gg[0]) + ")" : "") +
                                     (g.Count > 1 ? ", " + N(g.Last()) + " at long range" + (gg.Count > 0 ? " (game: " + N(gg.Last()) + ")" : "") : "") +
                                     (s.Count > 0 ? ", shadow " + N(s[0]) + (sg.Count > 0 ? " (game: " + N(sg[0]) + ")" : "") : "") +
                                     ". Each instance on screen adds this cost; frame drops are most likely in large battles.",
                                     "：近距离 " + N(g[0]) + " 个三角面" + (gg.Count > 0 ? "（原版 " + N(gg[0]) + "）" : "") +
                                     (g.Count > 1 ? "，远距离 " + N(g.Last()) + (gg.Count > 0 ? "（原版 " + N(gg.Last()) + "）" : "") : "") +
                                     (s.Count > 0 ? "，阴影 " + N(s[0]) + (sg.Count > 0 ? "（原版 " + N(sg[0]) + "）" : "") : "") +
                                     "。画面中每个实例均产生此开销，大规模战斗时最易造成掉帧。")));
                }
                void Group(string flag, string level, string title, string cn, Func<List<Dictionary<string, object>>, string> text)
                {
                    var its = items.Where(it => Has(it, flag) && !many.Contains(it)).ToList();
                    if (its.Count == 0) return;
                    var f = (level, T(title, cn) + (its.Count > 1 ? " (" + its.Count + ")" : ""), text(its));
                    (level == "red" ? red : level == "amber" ? amber : minor).Add(f);
                }
                Dictionary<string, object> Worst(List<Dictionary<string, object>> its, Func<Dictionary<string, object>, double> f) => its.OrderByDescending(f).First();
                Group("lod_flat", "amber", "Full detail at every distance", "远距离仍为完整细节", its =>
                {
                    var w = Worst(its, it => Tris(it, "game", "mod").DefaultIfEmpty(0).Last());
                    List<long> g = Tris(w, "game", "mod"), gg = Tris(w, "game", "game");
                    return T("Worst case: " + Name(w) + " still draws " + N(g.Last()) + " triangles at long range" + (gg.Count > 0 ? " (game: " + N(gg.Last()) + ")" : "") + ".",
                             "最严重：" + Name(w) + " 在远距离仍绘制 " + N(g.Last()) + " 个三角面" + (gg.Count > 0 ? "（原版 " + N(gg.Last()) + "）" : "") + "。");
                });
                Group("shadow_heavy", "amber", "Shadows use the full model", "阴影使用完整模型", its =>
                {
                    var w = Worst(its, it => Tris(it, "shadow", "mod").DefaultIfEmpty(0).First());
                    List<long> s = Tris(w, "shadow", "mod"), sg = Tris(w, "shadow", "game");
                    return T("Worst case: " + Name(w) + ", " + N(s[0]) + " triangles per shadow" + (sg.Count > 0 ? " (game: " + N(sg[0]) + ")" : "") + ". Redrawn for every shadow-casting light.",
                             "最严重：" + Name(w) + "，每个阴影 " + N(s[0]) + " 个三角面" + (sg.Count > 0 ? "（原版 " + N(sg[0]) + "）" : "") + "。每个投射阴影的光源均需重新绘制一次。");
                });
                Group("tris_heavy", "amber", "Far more triangles than the game's model", "三角面数远超原版模型", its =>
                {
                    var w = Worst(its, it => Tris(it, "game", "mod").DefaultIfEmpty(0).First());
                    List<long> g = Tris(w, "game", "mod"), gg = Tris(w, "game", "game");
                    return T("Worst case: " + Name(w) + ", " + N(g[0]) + " triangles at close range (game: " + N(gg.DefaultIfEmpty(0).First()) + ").",
                             "最严重：" + Name(w) + "，近距离 " + N(g[0]) + " 个三角面（原版 " + N(gg.DefaultIfEmpty(0).First()) + "）。");
                });
                Group("lod_missing", "amber", "Fewer detail levels than the game's model", "细节层级少于原版模型", its =>
                    T("Some long-range detail levels are missing, so the model switches to simpler versions less often.", "缺少部分远距离细节层级，模型较少切换至简化版本。"));
                Group("aabb_inflated", "amber", "Culling box larger than the model", "剔除盒大于模型", its =>
                {
                    double over = its.Max(it => it.TryGetValue("facts", out var f) && f is Dictionary<string, object> d && d.TryGetValue("aabb_over", out var o) && o is double x ? x : 0);
                    return T("Up to " + over.ToString("0.0") + " m too large: the model is drawn and casts shadows while off screen.", "最多超出 " + over.ToString("0.0") + " 米：模型位于画面外时仍会被绘制并投射阴影。");
                });
                Group("light_shadow", "amber", "Adds shadow-casting lights", "新增投射阴影的灯光", its => T("Each one renders its own shadow map while active.", "每个灯光开启期间均需单独渲染一张阴影贴图。"));
                Group("light_volumetric", "amber", "Adds volumetric lights", "新增体积光", its => T("Each one adds a rendering pass while active.", "每个灯光开启期间均需额外进行一次渲染。"));
                Group("tex_no_mips", "amber", "Textures without mipmaps", "贴图缺少 mipmap", its =>
                    T(its.Count + " texture(s). They shimmer at a distance, and the full image is read even when it covers only a few pixels.", "共 " + its.Count + " 张。远距离显示时会闪烁，且仅占数个像素时仍读取整张贴图。"));
                Group("tex_uncompressed", "amber", "Uncompressed textures", "未压缩的贴图", its =>
                    T(Mb(its.Sum(it => L(it, "max"))) + " in total, 4 to 8 times the size of the game's compressed formats.", "共 " + Mb(its.Sum(it => L(it, "max"))) + "，为游戏压缩格式的 4 至 8 倍。"));
                Group("tex_not_streamed", "amber", "Textures always resident", "贴图常驻显存", its =>
                    T(Mb(its.Sum(it => L(it, "max"))) + " resident in video memory throughout the mission; the game loads the high-resolution versions of its own copies only when needed.",
                      "共 " + Mb(its.Sum(it => L(it, "max"))) + "，整局常驻显存；原版同一贴图仅在需要时加载高分辨率版本。"));
                Group("tex_big_resident", "amber", "Large added textures", "新增大尺寸贴图", its =>
                {
                    var w = Worst(its, it => L(it, "w") * L(it, "h"));
                    return T(its.Count + " texture(s), up to " + L(w, "w") + "×" + L(w, "h") + "; " + Mb(its.Sum(it => L(it, "max"))) + " resident in video memory throughout the mission.",
                             "共 " + its.Count + " 张，最大 " + L(w, "w") + "×" + L(w, "h") + "，合计 " + Mb(its.Sum(it => L(it, "max"))) + "，整局常驻显存。");
                });
                long mem = L(m, "max");
                if (mem >= 300L * 1000 * 1000)
                    amber.Add(("amber", T("High video memory use", "显存占用较高"),
                        T(Mb(mem) + " of textures and models" + (L(m, "game_max") > 0 ? " (game: " + Mb(L(m, "game_max")) + ")" : "") + ". Graphics cards with 8 GB or less may stutter or show blurry textures.",
                          "贴图与模型共 " + Mb(mem) + (L(m, "game_max") > 0 ? "（原版 " + Mb(L(m, "game_max")) + "）" : "") + "。显存为 8 GB 及以下的显卡可能出现卡顿或贴图模糊。")));
                Group("tex_bigger", "minor", "Higher-resolution textures than the game's", "贴图分辨率高于原版", its => T(its.Count + " texture(s).", "共 " + its.Count + " 张。"));
                Group("sections_added", "minor", "More draw calls", "绘制批次增加", its => T(its.Count + " model(s) use more draw calls than the game's.", its.Count + " 个模型的绘制批次多于原版。"));
                Group("stride_wider", "minor", "Larger vertex format", "顶点格式增大", its => T(its.Count + " model(s) store more bytes per vertex than the game's; the memory cost is small.", its.Count + " 个模型的单顶点字节数高于原版，显存开销较小。"));
                return red.Concat(amber).Concat(minor).ToList();
            }

            /// <summary>A mod's findings, then its biggest resources.</summary>
            FrameworkElement Details(Dictionary<string, object> m)
            {
                var box = new StackPanel { Margin = new Thickness(28, 8, 12, 16) };
                var findings = Findings(m);
                if (findings.Count == 0)
                    box.Children.Add(Small(T("Its models and textures are comparable to the game's.", "该 MOD 的模型与贴图与原版相当。"), new Thickness(0, 0, 0, 8)));
                foreach (var (level, title, text) in findings.Where(f => f.Level != "minor"))
                {
                    var line = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                    var bar = new Border { Width = 3, Background = B(level == "red" ? "Red" : "Yellow"), Margin = new Thickness(0, 2, 10, 2) };
                    DockPanel.SetDock(bar, Dock.Left);
                    line.Children.Add(bar);
                    var words = new StackPanel();
                    words.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                    words.Children.Add(Small(text, new Thickness(0, 2, 0, 0)));
                    line.Children.Add(words);
                    box.Children.Add(line);
                }
                var minor = findings.Where(f => f.Level == "minor").Select(f => f.Title).ToList();
                if (minor.Count > 0) box.Children.Add(Small(T("Minor: ", "次要：") + string.Join(" · ", minor), new Thickness(0, 0, 0, 10)));

                var items = Items(m);
                box.Children.Add(new TextBlock { Text = T("Largest resources", "显存占用最高的资源"), FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 6, 0, 4) });
                foreach (var it in items.Take(20))
                {
                    bool tex = Str(it, "type") == "texture";
                    string what;
                    if (tex) what = T("Texture", "贴图") + (L(it, "w") > 0 ? " " + L(it, "w") + "×" + L(it, "h") + Layers(L(it, "layers")) + " " + Str(it, "format") : "");
                    else
                    {
                        var g = Tris(it, "game", "mod");
                        what = Name(it) + (g.Count > 0 ? T(" · " + N(g[0]) + " triangles", " · " + N(g[0]) + " 个三角面") : "");
                    }
                    bool inGame = it.TryGetValue("in_game", out var ig) && ig is bool b && b;
                    string game = !inGame ? T("new", "新增")
                        : T("game ", "原版 ") + (tex && L(it, "game_w") > 0 ? L(it, "game_w") + "×" + L(it, "game_h") + Layers(L(it, "game_layers")) + " · " : "")
                          + (!tex && Tris(it, "game", "game").Count > 0 ? T(N(Tris(it, "game", "game")[0]) + " triangles · ", N(Tris(it, "game", "game")[0]) + " 个三角面 · ") : "")
                          + Mb(L(it, "game_max"));
                    var line = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                    var size = new TextBlock { Text = Mb(L(it, "max")), Foreground = B("Ink"), FontSize = 13, MinWidth = 90, TextAlignment = TextAlignment.Right };
                    DockPanel.SetDock(size, Dock.Right);
                    line.Children.Add(size);
                    line.Children.Add(new TextBlock
                    {
                        FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
                        Inlines = { new Run(what) { Foreground = B("Ink") }, new Run("  ·  " + game) { Foreground = B("Muted") } },
                    });
                    box.Children.Add(line);
                }
                if (items.Count > 20) box.Children.Add(Small(T((items.Count - 20) + " more not shown", "另有 " + (items.Count - 20) + " 个未列出"), new Thickness(0, 4, 0, 0)));
                return box;
            }

            string Layers(long n) => n > 1 ? T(" ×" + n + " layers", " ×" + n + " 层") : "";
        }
    }
}
