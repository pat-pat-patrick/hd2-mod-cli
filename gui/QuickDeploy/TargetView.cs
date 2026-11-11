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
        /// <summary>The switch between the targets, at the top of the left column, above the
        /// library (the user, 2026-11-11: beside Deploy it did not share its height, and the place was
        /// wrong); each target as wide as the other. Null when no plugin offers a target.</summary>
        UIElement TargetSwitch()
        {
            if (st == null) return null;
            var offers = st.TargetOffers.Where(o => o.Found || o.Id == st.Target).ToList();
            if (offers.Count == 0 && st.OnOfficial) return null;
            var all = new List<(string Id, string Title, string Tip)> { ("official", Texts.Get("target_official"), Texts.Get("target_official_tip")) };
            all.AddRange(offers.Select(o => (o.Id, o.Title, o.Found ? Texts.F("target_tip", o.Title) : o.Why)));
            if (!st.OnOfficial && all.All(a => a.Id != st.Target)) all.Add((st.Target, st.Target, Texts.F("target_no_plugin", st.Target)));
            var row = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = all.Count };
            foreach (var (id, title, tip) in all)
            {
                var rb = new RadioButton
                {
                    Content = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis }, Style = (Style)FindResource("Segment"),
                    GroupName = "deploy_target", IsChecked = id == st.Target, ToolTip = tip, HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                rb.Click += (s, e) => { if (id != st.Target) SwitchTarget(id, title); };
                row.Children.Add(rb);
            }
            return new Border
            {
                BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 5, 0, 20),   // 5: level with the list's Group | Order bar (measured 2026-11-11)
                ToolTip = Texts.Get("target_switch_tip"), Child = row,
            };
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

namespace QuickDeploy
{
    public partial class MainWindow
    {
        /// <summary>A mod for another target than the one in use (Mod.For): off here, its switch locked.</summary>
        bool ForElsewhere(ModRow m) => m.For != null && st != null && m.For != st.Target;

        /// <summary>Whether a mod's target is worth saying: always while a plugin offers a target;
        /// without one (the plugin removed), only for a mod made for a target no plugin offers,
        /// which is off and locked, so the player sees why (the user, 2026-11-12: with the HellForge
        /// plugin removed, its switch and words stayed).</summary>
        bool ShowFor(ModRow m) => m.For != null && st != null && (st.TargetOffers.Count > 0 || m.For != "official");

        string fellBackFrom;

        /// <summary>The target in use is one no added plugin offers any more (the plugin removed):
        /// the library goes back to the official game, once, and says so. The target's list is
        /// kept (Targets.Switch), so adding the plugin again brings it back. Said in a dialog: the
        /// list in use changes under the player, and the status line is soon taken by other news.</summary>
        void FallBack(string gone)
        {
            if (fellBackFrom == gone) return;
            fellBackFrom = gone;
            var said = Texts.F("target_fell_back", TargetName(gone));
            bool switched = false;
            var act = Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                if (Targets.Current(lib) == gone)
                {
                    Targets.Switch(lib, Targets.Official);
                    lib.Save();
                    switched = true;
                }
                return said;
            }), true, () => { if (switched) Dialog.Tell(this, said); else fellBackFrom = null; });
            // the window busy with something else: Act did nothing, so the next reading tries again
            if (act.IsCompleted && !act.Result) fellBackFrom = null;
        }

        /// <summary>"仅官方", "仅 HellForge": in Chinese a Latin name gets a space before it.</summary>
        string ForTag(string id)
        {
            var name = TargetName(id);
            bool latin = name.Length > 0 && name[0] < 0x80 && char.IsLetter(name[0]);
            return Texts.F("for_tag", (latin && (Texts.Lang ?? "").StartsWith("zh") ? " " : "") + name);
        }

        /// <summary>A target's name for the player: Official, or the title its plugin gives it, or
        /// the title it had when a plugin last offered it (the plugin removed: "HellForge", not the
        /// id "hellforge").</summary>
        string TargetName(string id) =>
            id == null ? Texts.Get("for_both") : id == "official" ? Texts.Get("target_official")
            : st?.TargetOffers.FirstOrDefault(o => o.Id == id).Title ?? KnownTitle(id) ?? id;

        Dictionary<string, object> knownTitles;

        /// <summary>The titles plugins gave their targets, kept in gui.json ("target_titles"), so a
        /// mod made for a target whose plugin is gone is still named as it was.</summary>
        string KnownTitle(string id)
        {
            if (knownTitles == null)
            {
                try { knownTitles = Json.Obj(Store.Load("gui.json"), "target_titles") ?? new Dictionary<string, object>(); }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is FormatException) { knownTitles = new Dictionary<string, object>(); }
            }
            return knownTitles.TryGetValue(id, out var t) ? t as string : null;
        }

        /// <summary>The offered targets' titles remembered (only when one is new or changed).</summary>
        void RememberTitles()
        {
            if (st == null) return;
            var changed = st.TargetOffers.Where(o => !string.IsNullOrEmpty(o.Title) && KnownTitle(o.Id) != o.Title).ToList();
            if (changed.Count == 0) return;
            foreach (var o in changed) knownTitles[o.Id] = o.Title;
            try
            {
                var d = Store.Load("gui.json");
                d["target_titles"] = knownTitles;
                Store.Save("gui.json", d);
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is FormatException) { }
        }

        /// <summary>The details' card: which target the mod is for (both, Official, a plugin's), and
        /// its other build when it has one. Only where a plugin offers a target, or the mod says one.</summary>
        UIElement ForPanel(ModRow m)
        {
            if (st == null || (st.TargetOffers.Count == 0 && !ShowFor(m))) return null;
            var box = new StackPanel();
            box.Children.Add(new TextBlock { Text = Texts.Get("for_head"), FontWeight = FontWeights.SemiBold });
            var row = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 8, 0, 0) };
            var choices = new List<(string Value, string Title)> { ("both", Texts.Get("for_both")), ("official", Texts.Get("target_official")) };
            choices.AddRange(st.TargetOffers.Select(o => (o.Id, o.Title)));
            if (m.For != null && choices.All(c => c.Value != m.For)) choices.Add((m.For, m.For));
            row.Columns = choices.Count;
            foreach (var (value, title) in choices)
            {
                var rb = new RadioButton
                {
                    Content = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis }, Style = (Style)FindResource("Segment"),
                    GroupName = "for_" + m.Id, IsChecked = (m.For ?? "both") == value, HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                rb.Click += (s, e) => { if ((m.For ?? "both") != value) _ = Act(() => Cli.Run("for", m.Id, value), false); };
                row.Children.Add(rb);
            }
            box.Children.Add(new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Child = row });
            box.Children.Add(Small(Texts.Get("for_note"), new Thickness(0, 8, 0, 0)));
            var other = m.Pair == null ? null : st.Mods.FirstOrDefault(x => x.Id == m.Pair);
            if (other != null)
            {
                var line = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
                var unpair = new Button { Content = Texts.Get("pair_off"), Style = (Style)FindResource("Ghost"), MinHeight = 30, Padding = new Thickness(10, 0, 10, 0) };
                unpair.Click += (s, e) => _ = Act(() => Cli.Run("unpair", m.Id), false);
                DockPanel.SetDock(unpair, Dock.Right);
                line.Children.Add(unpair);
                line.Children.Add(new TextBlock { Text = Texts.F("pair_line", other.Name, TargetName(other.For)), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                box.Children.Add(line);
            }
            return Panel(box, new Thickness(0, 20, 0, 0));
        }

        /// <summary>A mod added whose Guid one in the library has, while a plugin offers a target:
        /// a new version of it, or the same mod's build for a target, beside it.</summary>
        async Task<CliResult> SameModOrNot(CliResult r)
        {
            var parts = r.Refused.Arg.Split('\n');
            if (parts.Length != 2) return r;
            var ids = parts[0].Split('|');
            var lib = Library.Load();
            var names = ids.Select(id => lib.Mods.FirstOrDefault(m => m.Id == id)?.Name).Where(n => n != null).ToList();
            var file = System.IO.Path.GetFileName(parts[1].TrimEnd('\\'));
            var answers = new List<(string Text, string[] Args)>();
            if (ids.Length == 1) answers.Add((Texts.Get("add_as_new_version"), new[] { "add", parts[1], "--replace" }));
            answers.Add((Texts.F("add_as_build", Texts.Get("target_official")), new[] { "add", parts[1], "--for", "official" }));
            foreach (var o in st?.TargetOffers ?? new List<(string Id, string Title, bool Found, string Why)>())
                answers.Add((Texts.F("add_as_build", o.Title), new[] { "add", parts[1], "--for", o.Id }));
            var labels = answers.Select(x => x.Text).Concat(new[] { Texts.Get("cancel") }).ToArray();
            int pick = Dialog.Choose(this, Texts.F("add_same_mod", file, string.Join(Texts.Get("sep_list"), names)), labels);
            if (pick < 0 || pick >= answers.Count) return new CliResult { Code = 0, Out = "" };
            var args = answers[pick].Args;
            return await Exec(() => Cli.Run(args));
        }
    }
}
