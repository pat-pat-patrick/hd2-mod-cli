using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Hd2Mod;

namespace QuickDeploy
{
    /// <summary>Changes to the list shown at once (the user, 2026-11-06: a mod moved into a group
    /// stayed where it was, then the list was drawn twice, "卡卡的"; deleting the same): what the
    /// change implies is drawn first, the command runs behind it in order, and one state read
    /// follows. A mod deleted from the disk is struck through, left to right, before it goes.</summary>
    public partial class MainWindow
    {
        /// <summary>A change to the groups: drawn from what it implies, then done in the
        /// background in order, as a switch is (Change); a refusal is said and the read that
        /// follows puts the list back as it is.</summary>
        async void QuickGroups(Action local, params string[] args)
        {
            if (st == null || busy) return;
            local();
            RenderList();
            RenderNav();
            RenderDetail();
            waiting++;
            var r = await Queued(() => Cli.Run(args));
            waiting--;
            quietUntil = DateTime.Now.AddSeconds(1.5);
            Refused(r);
            Refresh();
        }

        /// <summary>Mods put in a group (or at the top: null), at once.</summary>
        void PutInGroup(IEnumerable<string> ids, string group)
        {
            var list = ids.ToList();
            QuickGroups(() =>
            {
                foreach (var m in st.Mods.Where(x => list.Contains(x.Id))) m.Group = group;
            }, new[] { "group-put" }.Concat(list).Concat(group == null ? new[] { "--top" } : new[] { "--in", group }).ToArray());
        }

        /// <summary>A group moved into another (or to the top: null), with what it holds, at once.</summary>
        void MoveGroup(string id, string into)
        {
            QuickGroups(() =>
            {
                var g = st.Groups.FirstOrDefault(x => x.Id == id);
                if (g != null) g.Parent = into;
            }, into == null ? new[] { "group-move", id, "--top" } : new[] { "group-move", id, "--in", into });
        }

        /// <summary>The rows the list shows for a mod now: its row in the load order, or its item
        /// in the group view's columns.</summary>
        List<Border> RowsShowing(string id, bool laidOut = true)
        {
            var found = new List<Border>();
            if (groupView)
            {
                foreach (var col in GroupCols.Children.OfType<Border>())
                    if (col.Tag is GroupDrop d && d.Items != null)
                        found.AddRange(d.Items.Where(sh => !sh.IsGroup && sh.Id == id).Select(sh => sh.Item));
            }
            // a row is kept while another filter hides its mod: only one in the list is showing
            else if (rows.TryGetValue(id, out var r) && r.Row.Parent == List) found.Add(r.Row);
            // a row drawn a moment ago is not laid out yet (a switch's row, drawn anew after its click)
            return laidOut ? found.Where(b => b.IsVisible).ToList() : found;
        }

        /// <summary>A line drawn across a row, from its left to its right as Progress goes from 0 to 1.</summary>
        sealed class Strike : Adorner
        {
            public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
                nameof(Progress), typeof(double), typeof(Strike), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

            public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

            readonly Pen pen;
            readonly double at;

            /// <summary>at: how far down the row the line goes (Middle).</summary>
            public Strike(UIElement row, Brush ink, double at) : base(row)
            {
                IsHitTestVisible = false;
                pen = new Pen(ink, 2);
                pen.Freeze();
                this.at = at;
            }

            protected override void OnRender(DrawingContext dc)
            {
                var size = AdornedElement.RenderSize;
                double x0 = 12, x1 = Math.Max(x0, size.Width - 12), y = Math.Round(at) + 0.5;
                if (Progress > 0) dc.DrawLine(pen, new Point(x0, y), new Point(x0 + (x1 - x0) * Progress, y));
            }
        }

        const int StrikeMs = 360;

        /// <summary>The mod's rows struck through, left to right, and dimmed; done when the line
        /// has crossed. What it drew is taken away by Unstrike.</summary>
        async Task<List<(Border Row, Strike Line)>> StrikeOut(string id)
        {
            var drawn = new List<(Border, Strike)>();
            foreach (var row in RowsShowing(id))
            {
                var layer = AdornerLayer.GetAdornerLayer(row);
                if (layer == null) continue;
                var line = new Strike(row, B("Red"), Middle(row));
                layer.Add(line);
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                line.BeginAnimation(Strike.ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(StrikeMs)) { EasingFunction = ease });
                row.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.4, TimeSpan.FromMilliseconds(StrikeMs)) { EasingFunction = ease });
                drawn.Add((row, line));
            }
            if (drawn.Count > 0) await Task.Delay(StrikeMs + 40);
            return drawn;
        }

        /// <summary>How far down a row the line goes: the middle of the row (the user, 2026-11-06:
        /// through the name was not the middle), or of its head when it is open and shows its
        /// options under it (a load-order row: a column whose first part is the head).</summary>
        static double Middle(Border row)
        {
            if (row.Child is Border edge && edge.Child is StackPanel col && col.Children.Count > 1 && col.Children[0] is FrameworkElement head && head.IsVisible)
                return head.TranslatePoint(new Point(0, head.ActualHeight / 2), row).Y;
            return row.ActualHeight / 2;
        }

        static void Unstrike(List<(Border Row, Strike Line)> drawn)
        {
            foreach (var (row, line) in drawn)
            {
                AdornerLayer.GetAdornerLayer(row)?.Remove(line);
                row.BeginAnimation(OpacityProperty, null);
                row.Opacity = 1;
            }
        }

        /// <summary>A mod off the disk for good, after a red question that names its folder: struck
        /// through and taken from the list at once, then out of the library and the game (a
        /// deploy) and its folder deleted, in the background in order. A refusal is said, and the
        /// read that follows brings the row back.</summary>
        void DeleteFromDisk(ModRow m)
        {
            // what is left afterwards, said by what it was added from (issue 16): an archive kept,
            // its page, or nothing -- the folder is then the only copy
            var left = Ops.LeftAfterDelete(m.Source, m.Path);
            var key = left == "archive" ? "delete_ask" : left == "page" ? "delete_ask_page" : "delete_ask_only";
            if (!Dialog.AskDanger(this, Texts.F(key, m.Name, m.Path), Texts.Get("delete_btn"))) return;
            DeleteNow(m);
        }

        /// <summary>The delete itself, once asked (and by the window's own test, deletemod).</summary>
        async void DeleteNow(ModRow m)
        {
            var id = m.Id;
            await Gone(m, true, () =>
            {
                var lib = Library.Load();
                var mod = lib.Mods.FirstOrDefault(x => x.Id == id) ?? throw new ModError("not in the library any more: " + m.Name);
                var res = Ops.DeleteMod(lib, mod, Store.GameDir());
                return Texts.F("deleted_mod", mod.Name, Texts.Size(res.Bytes));
            }, null);
        }

        /// <summary>A mod out of the library (its folder kept), once asked: faded out and taken from
        /// the list at once, the command behind it, as a delete is.</summary>
        async void RemoveFromLibrary(ModRow m)
        {
            if (!Dialog.Ask(this, Texts.F("remove_ask", m.Name), Texts.Get("remove_btn"))) return;
            await Gone(m, false, null, new[] { "remove", m.Id });
        }

        /// <summary>A mod leaving the list: struck through (a delete) or faded out (a remove), taken
        /// from the list at once with the one after it chosen in its place and the ones after it a
        /// place up, as the read will have them, so the read after the command draws nothing again
        /// (the user, 2026-11-06: the list was drawn a second time once the row had gone; with none
        /// chosen the read chose the first mod, and that row and the details changed again). Then
        /// the command, in the background in order; a refusal is said and the read brings the row back.</summary>
        async Task Gone(ModRow m, bool strike, Func<string> work, string[] args)
        {
            if (st == null || busy) return;
            var id = m.Id;
            var drawn = strike ? await StrikeOut(id) : new List<(Border, Strike)>();
            if (!strike) await Task.WhenAll(RowsShowing(id).Select(r => Motion.Out(r, 140)));
            var gone = st.Mods.FirstOrDefault(x => x.Id == id);
            if (gone != null && selectedId == id)
            {
                var near = (groupView ? GroupShown().Where(x => x.Group == gone.Group) : Seen()).ToList();
                int at = near.FindIndex(x => x.Id == id);
                selectedId = at < 0 ? null : at + 1 < near.Count ? near[at + 1].Id : at > 0 ? near[at - 1].Id : null;
            }
            if (gone != null)
                foreach (var x in st.Mods) if (x.Index > gone.Index) x.Index--;
            st.Mods.RemoveAll(x => x.Id == id);
            rows.Remove(id);
            picked.Remove(id);
            RenderList();
            RenderNav();
            RenderDetail();
            Unstrike(drawn);
            waiting++;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var r = await Queued(() => work != null ? Cli.Call(work) : Cli.Run(args));
            waiting--;
            quietUntil = DateTime.Now.AddSeconds(1.5);
            if (r.Ok && work != null) lastDone = Texts.F("took", Last(r.Out), Seconds(clock.Elapsed.TotalSeconds));
            Refused(r);
            Refresh();
        }
    }
}
