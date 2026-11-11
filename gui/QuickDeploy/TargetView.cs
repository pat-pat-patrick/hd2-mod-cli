using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Hd2Mod;

namespace QuickDeploy
{
    /// <summary>Where the library deploys (core Targets, PluginTargets): a switch beside Deploy,
    /// shown only when a plugin offers a target (the user, 2026-11-11: "官方 | HellForge"). Each
    /// target keeps its own list, so the list, its conflicts and its options are the target's;
    /// Deploy, Purge and Launch go to the target in use.</summary>
    public partial class MainWindow
    {
        string targetsDrawn;

        void RenderTargets()
        {
            if (st == null) return;
            var offers = st.TargetOffers.Where(o => o.Found || o.Id == st.Target).ToList();
            bool show = offers.Count > 0 || !st.OnOfficial;
            var key = st.Target + "|" + string.Join(",", offers.Select(o => o.Id + o.Title));
            TargetBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (key == targetsDrawn) return;
            targetsDrawn = key;
            TargetBox.Children.Clear();
            var all = new List<(string Id, string Title, string Tip)> { ("official", Texts.Get("target_official"), Texts.Get("target_official_tip")) };
            all.AddRange(offers.Select(o => (o.Id, o.Title, o.Found ? Texts.F("target_tip", o.Title) : o.Why)));
            if (!st.OnOfficial && all.All(a => a.Id != st.Target)) all.Add((st.Target, st.Target, Texts.F("target_no_plugin", st.Target)));
            foreach (var (id, title, tip) in all)
            {
                var rb = new RadioButton
                {
                    Content = title, Style = (Style)FindResource("Segment"), GroupName = "deploy_target", IsChecked = id == st.Target,
                    MinWidth = 88, ToolTip = tip,
                };
                rb.Click += (s, e) => { if (id != st.Target) SwitchTarget(id, title); };
                TargetBox.Children.Add(rb);
            }
        }

        void SwitchTarget(string id, string title)
        {
            _ = Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                Targets.Switch(lib, id);
                lib.Save();
                return Texts.F("target_switched", title);
            }), true);
        }

        /// <summary>A deploy to the plugin's target: the plugin takes the list in use.</summary>
        Task<bool> DeployToTarget()
        {
            var host = PluginHostHere();
            var id = st.Target;
            return Act(() => Cli.Call(() =>
            {
                var offer = PluginTargets.Of(id, host);
                if (!offer.Found) throw new ModError(offer.Title + ": " + (offer.Why ?? ""), "target_missing", id);
                var r = PluginTargets.Deploy(offer, host);
                var refused = Json.List(r, "refused").OfType<string>().ToList();
                if (refused.Count > 0)
                    Dispatcher.BeginInvoke(new Action(() => Dialog.Tell(this, Texts.Get("target_refused") + "\n" + string.Join("\n", refused))));
                return Json.Str(r, "said") ?? Texts.F("target_deployed", offer.Title);
            }), true);
        }

        /// <summary>The target's game, as its plugin starts it; changes not deployed yet first, when asked.</summary>
        async Task LaunchTarget()
        {
            if (st.TargetRunning) { Dialog.Tell(this, Texts.F("target_running", st.TargetTitle)); return; }
            if (st.Pending > 0)
            {
                if (!Dialog.Ask(this, Texts.F("launch_ask", st.Pending), Texts.Get("deploy_launch"))) return;
                if (!await DeployToTarget()) return;
            }
            try
            {
                var host = PluginHostHere();
                var offer = PluginTargets.Of(st.Target, host);
                if (offer.Type.GetMethod("TargetLaunch") == null) { Dialog.Tell(this, Texts.F("target_no_launch", offer.Title)); return; }
                PluginFiles.Call(offer.Type, "TargetLaunch", this, host);
            }
            catch (Exception x) when (x is ModError || x is InvalidOperationException || x is System.IO.IOException)
            {
                Dialog.Tell(this, x.Message);
            }
        }
    }
}

namespace QuickDeploy
{
    public partial class MainWindow
    {
        /// <summary>The left column's card for a plugin's target: where it deploys, whether the
        /// target is there and free, the way back to the official game, and taking out what this
        /// manager put in.</summary>
        void TargetSide(StackPanel card)
        {
            var head = new DockPanel();
            bool ok = st.TargetOffered && st.TargetFound && !st.TargetRunning;
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = B(ok ? "Blue" : "Yellow"), Margin = new Thickness(0, 3.5, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(dot, Dock.Left);
            head.Children.Add(dot);
            head.Children.Add(new TextBlock { Text = Texts.F("target_side", st.TargetTitle ?? st.Target), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(head);
            string why = !st.TargetOffered ? Texts.F("target_no_plugin", st.Target) : !st.TargetFound ? Texts.F("target_not_found", st.TargetTitle)
                       : st.TargetRunning ? Texts.F("target_running", st.TargetTitle) : Texts.F("target_side_note", st.TargetTitle);
            card.Children.Add(Small(why, new Thickness(0, 8, 0, 0)));
            var back = new Button
            {
                Content = new TextBlock { Text = Texts.Get("target_back"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            back.Click += (s, e) => SwitchTarget("official", Texts.Get("target_official"));
            card.Children.Add(back);
            if (st.TargetOffered && st.TargetFound)
            {
                var purge = new Button
                {
                    Content = new TextBlock { Text = Texts.F("target_purge", st.TargetTitle), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                    Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsEnabled = !st.TargetRunning,
                };
                purge.Click += Purge_Click;
                card.Children.Add(purge);
            }
            Nav.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(12), Child = card });
        }
    }
}
