using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hd2Mod;

namespace QuickDeploy
{
    /// <summary>An install plan on the download page (Recipes). Offered once a mod that has one is
    /// added and something it needs is missing (the user, 2026-11-28: after the package is dropped
    /// in; a member package says by itself which platform it is from, so nothing guesses): each
    /// step with where it stands and the one button that moves it on, Install all for the steps
    /// that need no hand, then Deploy and launch, and what the mod's log said of the last game.</summary>
    public partial class MainWindow
    {
        Recipes.Recipe foundPlan;
        // why the step last run did not go through, shown on its row with its pages
        string planSaid;
        Recipes.Step planSaidStep;
        bool planRunning;
        // the plans offered in this window's life: offered again only after the next start
        readonly HashSet<string> planOffered = new HashSet<string>();
        // steps whose download needs the player (Recipes.ByHand), by StepKey: their pages instead
        readonly HashSet<string> planByHand = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static DateTime planListRead;

        /// <summary>After mods were added: a plan whose mod is now in the library and lacks something
        /// it needs is offered, once a window's life; yes shows it on the download page.</summary>
        async void PlanAfterAdd()
        {
            // the author's list read again at most every hour: a new HUD may need new things
            if (DateTime.Now - planListRead > TimeSpan.FromHours(1))
            {
                planListRead = DateTime.Now;
                await Task.Run(() => Recipes.Refresh());
            }
            var lang = Texts.Lang;
            // the Requirements of the Nexus Mods pages of the mods on, read again once a day (no key)
            try
            {
                await Task.Run(() =>
                {
                    var lib = Library.Load();
                    Recipes.RefreshNeeds(lib.Mods.Where(m => m.Enabled && m.Nexus != null && Profiles.InProfile(lib, m)).Select(m => m.Nexus.Mod));
                });
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is ModError || e is UnauthorizedAccessException) { }
            List<Recipes.Recipe> plans;
            try { plans = await Task.Run(() => Recipes.Plans(Library.Load())); }
            catch (Exception e) when (e is IOException || e is FormatException || e is ModError || e is UnauthorizedAccessException) { return; }
            foreach (var plan in plans)
            {
                if (planOffered.Contains(plan.Id) || foundPlan?.Id == plan.Id) continue;
                List<Recipes.State> left;
                try { left = await Task.Run(() => Recipes.Unfinished(plan, Library.Load())); }
                catch (Exception e) when (e is IOException || e is FormatException || e is ModError || e is UnauthorizedAccessException) { continue; }
                if (left.Count == 0) continue;
                planOffered.Add(plan.Id);
                var names = string.Join(Environment.NewLine, left.Select(s => "• " + Recipes.In(s.Step.Name, lang)));
                // HD2QD_PLAN_YES (pictures only): answered yes without the question
                if (Environment.GetEnvironmentVariable("HD2QD_PLAN_YES") == null
                    && !Dialog.Ask(this, Texts.F("plan_ask", Recipes.In(plan.Title, lang), names), Texts.Get("plan_ask_go"), Texts.Get("plan_ask_later"))) return;
                foundPlan = plan;
                planSaid = null;
                planSaidStep = null;
                ShowPage("download");
                RenderDownloads();
                return;
            }
        }

        /// <summary>The plan shown, as a card at the top of the download page.</summary>
        void PlanOnPage()
        {
            var plan = PlanNow();
            if (plan == null) return;
            Downloads.Children.Add(Card(PlanFound(plan)));
            Downloads.Children.Add(new Border { Height = 20 });
        }

        static bool CanDo(Recipes.State s) => Recipes.CanDo(s);

        /// <summary>The plan shown, as it is now (Rebuilt).</summary>
        Recipes.Recipe PlanNow() => foundPlan == null ? null : Rebuilt(foundPlan);

        /// <summary>A plan as it is now: a mod's own list is built again from the library (a
        /// dependency added shows what it needs in turn).</summary>
        static Recipes.Recipe Rebuilt(Recipes.Recipe plan)
        {
            var main = plan.Steps.FirstOrDefault(s => s.Main);
            try
            {
                var lib = Library.Load();
                var m = main == null ? null : Recipes.ModFor(lib, main);
                return (m != null ? Recipes.PlanFor(lib, m) : null) ?? plan;
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is ModError || e is UnauthorizedAccessException) { return plan; }
        }

        /// <summary>Before a mod goes off or out: the mods that need it named, and asked; true to go on
        /// (none needs it, or the player said so).</summary>
        bool DependentsAgree(string id, string askKey, string goKey)
        {
            List<Mod> by;
            string name;
            try
            {
                var lib = Library.Load();
                var m = lib.Mods.FirstOrDefault(x => x.Id == id);
                if (m == null) return true;
                name = m.Name;
                by = Recipes.NeededBy(lib, m);
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is ModError || e is UnauthorizedAccessException) { return true; }
            if (by.Count == 0) return true;
            var names = string.Join(Environment.NewLine, by.Select(x => "• " + x.Name));
            return Dialog.Ask(this, Texts.F(askKey, name, names), Texts.Get(goKey));
        }

        /// <summary>After a mod went out: what its install list pulled in that nothing needs now,
        /// offered for the same way out (removed, or deleted from the disk).</summary>
        async void OfferOrphans(string gone, bool delete)
        {
            List<Mod> left;
            try { left = await Task.Run(() => Recipes.Orphans(Library.Load())); }
            catch (Exception e) when (e is IOException || e is FormatException || e is ModError || e is UnauthorizedAccessException) { return; }
            if (left.Count == 0) return;
            var names = string.Join(Environment.NewLine, left.Select(x => "• " + x.Name));
            if (delete)
            {
                if (!Dialog.AskDanger(this, Texts.F("dep_orphans_delete", gone, names), Texts.Get("delete_btn"))) return;
                var ids = left.Select(x => x.Id).ToList();
                var r = await Exec(() => Cli.Call(() =>
                {
                    var lib = Library.Load();
                    var go = lib.Mods.Where(x => ids.Contains(x.Id)).ToList();
                    var (_, bytes, failed) = Ops.DeleteMods(lib, go, Store.GameDir());
                    var said = Texts.F("st_deleted_n", go.Count - failed.Count, Texts.Size(bytes));
                    return failed.Count == 0 ? said : said + "\n\n" + Texts.F("st_delete_failed", string.Join("\n", failed));
                }));
                if (r.Ok) { if (r.Out.Contains("\n")) Dialog.Tell(this, r.Out); else Foot(r.Out, "Blue"); }
                else if (!Refused(r)) Dialog.Tell(this, Texts.F("failed", r.Message));
                Refresh();
            }
            else
            {
                if (!Dialog.Ask(this, Texts.F("dep_orphans_remove", gone, names), Texts.Get("remove_btn"))) return;
                RunMany(left.Select(x => new[] { "remove", x.Id }).ToList());
            }
        }

        /// <summary>After a mod's update: what its new version needs that the library lacks or has
        /// too old, done with it, with each download's bar (the user, 2026-11-28: updated together);
        /// a step that needs a hand, or one that fails, shows the list on the download page.</summary>
        async void PlanAfterUpdate(string id)
        {
            Recipes.Recipe plan;
            List<Recipes.State> left;
            try
            {
                var lib = Library.Load();
                var m = lib.Mods.FirstOrDefault(x => x.Id == id);
                plan = m == null ? null : Recipes.PlanFor(lib, m);
                left = plan == null ? new List<Recipes.State>() : Recipes.Unfinished(plan, lib);
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is ModError || e is UnauthorizedAccessException) { return; }
            if (left.Count == 0) return;
            var names = left.Select(s => Recipes.In(s.Step.Name, Texts.Lang)).ToList();
            bool all = left.All(CanDo) && await PlanSteps(plan);
            if (all) { Foot(Texts.F("dep_updated_with", string.Join("、", names)), "Blue"); return; }
            foundPlan = plan;
            ShowPage("download");
            RenderDownloads();
        }

        UIElement PlanFound(Recipes.Recipe plan)
        {
            var lang = Texts.Lang;
            var panel = new StackPanel();
            var top = new DockPanel();
            var close = new Button { Content = Texts.Get("plan_close"), Style = (Style)FindResource("Ghost"), MinHeight = 30, FontSize = 12, VerticalAlignment = VerticalAlignment.Top, IsEnabled = !planRunning };
            close.Click += (s, e) => { foundPlan = null; RenderDownloads(); };
            DockPanel.SetDock(close, Dock.Right);
            top.Children.Add(close);
            top.Children.Add(Heading(Texts.F("plan_title", Recipes.In(plan.Title, lang))));
            panel.Children.Add(top);
            var summary = Recipes.In(plan.Summary, lang);
            if (summary != null) panel.Children.Add(Small(summary, new Thickness(0, 4, 0, 0)));

            List<Recipes.State> states;
            try { states = Recipes.States(plan, Library.Load()); }
            catch (Exception e) when (e is IOException || e is FormatException || e is ModError || e is UnauthorizedAccessException)
            {
                panel.Children.Add(new TextBlock { Text = Texts.F("failed", e.Message), Foreground = B("Yellow"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
                return panel;
            }
            var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            int n = 0;
            foreach (var st in states) list.Children.Add(Band(PlanRow(st, ++n, plan), n - 1));
            panel.Children.Add(list);

            bool free = !busy && !planRunning && job == null;
            bool more = states.Any(s => CanDo(s) && !s.Step.Optional && !planByHand.Contains(Recipes.StepKey(s.Step)));
            // a protected mod not yet unlocked does not hold the game back: its plugin unlocks it
            // before the deploy (Protected.Prepare), and the log says afterwards whether it did
            bool ready = states.All(Recipes.Spare);
            var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
            if (more)
            {
                var all = new Button { Content = Texts.Get("plan_all"), Style = (Style)FindResource("Primary"), MinHeight = 40, Margin = new Thickness(0, 0, 8, 0), IsEnabled = free };
                all.Click += (s, e) => PlanAll(plan);
                buttons.Children.Add(all);
            }
            var launch = new Button { Content = Texts.Get("plan_launch"), Style = (Style)FindResource(more || !ready ? "Ghost" : "Primary"), MinHeight = 40, IsEnabled = free && ready };
            launch.Click += (s, e) => Launch_Click(s, e);
            buttons.Children.Add(launch);
            panel.Children.Add(buttons);
            if (!ready) panel.Children.Add(Small(Texts.Get(more ? "plan_launch_after_all" : "plan_launch_after_hand"), new Thickness(0, 8, 0, 0)));
            // what the log says only of a plan that knows its mod's log lines
            else if (plan.Checks.Count > 0) panel.Children.Add(PlanOutcome(plan));
            return panel;
        }

        /// <summary>A step: its number or a tick, its name and what it is for, where it stands, and its button.</summary>
        UIElement PlanRow(Recipes.State st, int n, Recipes.Recipe plan)
        {
            var lang = Texts.Lang;
            var s = st.Step;
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };

            var mark = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
                Background = st.Done ? B("Blue") : Brushes.Transparent, BorderBrush = B(st.Done ? "Blue" : "Muted"), BorderThickness = new Thickness(1.5),
                Child = st.Done
                    ? (UIElement)new System.Windows.Shapes.Path { Data = Geometry.Parse(GlyphTick), Stroke = B("TapeInk"), StrokeThickness = 2.2, Width = 14, Height = 14, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                    : new TextBlock { Text = n.ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark);

            bool free = !busy && !planRunning && job == null;
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
            var auto = Recipes.AutoSites(s);
            bool byHand = planByHand.Contains(Recipes.StepKey(s));
            string doKey = st.Is == "off" ? "plan_do_on" : st.Is == "loader" ? "plan_do_loader" : st.Is == "old" && auto.Count > 0 && !byHand ? "plan_do_update"
                         : st.Is == "missing" && (s.Plugin != null || (auto.Count > 0 && !byHand)) ? (s.Plugin != null ? "plan_do_add" : "plan_do_install") : null;
            if (doKey != null && !s.Hint)
            {
                var b = new Button { Content = Texts.Get(doKey), Style = (Style)FindResource("Ghost"), MinHeight = 34, IsEnabled = free };
                b.Click += async (o, e) => { await PlanRun(plan, s); RenderDownloads(); };
                actions.Children.Add(b);
            }
            // pages: the way to a mod got by hand (its sites' pages too: a Nexus Mods page's own
            // button hands the file to this window), and to any other once its download failed
            bool failed = planSaidStep == s && planSaid != null;
            if (((st.Is == "missing" || st.Is == "old") && s.Plugin == null && (auto.Count == 0 || byHand)) || failed)
            {
                var pages = s.Pages.Where(p => Recipes.Shown(p, lang)).Select(p => (Label: Recipes.In(p.Label, lang) ?? new Uri(p.Url).Host, p.Url)).ToList();
                foreach (var x in Recipes.SitesOf(s).Where(x => x.Site != "GitHub" || s.Pages.Count == 0))
                    if (!pages.Any(p => p.Url == x.Page)) pages.Add((x.Site, x.Page));
                foreach (var p in pages)
                {
                    var url = p.Url;
                    var b = new Button { Content = Texts.F("plan_open", p.Label), Style = (Style)FindResource("Ghost"), MinHeight = 34, Margin = new Thickness(actions.Children.Count > 0 ? 8 : 0, 0, 0, 0), ToolTip = url };
                    b.Click += (o, e) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    actions.Children.Add(b);
                }
            }
            if (st.Is == "waiting" && st.WhyKey != "protected_no_plugin")      // no plugin yet: its own row adds it
            {
                var b = new Button { Content = Texts.Get("plan_open_plugins"), Style = (Style)FindResource("Ghost"), MinHeight = 34 };
                b.Click += (o, e) => ShowPage("plugins");
                actions.Children.Add(b);
            }
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Selectable.Text(Recipes.In(s.Name, lang) + (s.Optional ? Texts.Get("plan_optional") : ""), 14);
            name.FontWeight = FontWeights.SemiBold;
            words.Children.Add(name);
            var note = Recipes.In(s.Note, lang);
            if (note != null) words.Children.Add(Small(note, new Thickness(0, 2, 0, 0)));
            // where it comes from, before anything is downloaded: a dependency a mod names is the
            // player's to trust
            if (!st.Done && s.Plugin == null && s.Hint) words.Children.Add(Small(Texts.Get("plan_from_nexus_page"), new Thickness(0, 2, 0, 0)));
            else if (!st.Done && s.Plugin == null && Recipes.SitesOf(s).Count > 0)
                words.Children.Add(Small(Texts.F("plan_from", string.Join(Texts.Get("plan_from_or"), Recipes.SitesOf(s).Select(x => x.Site == "GitHub" ? "GitHub " + s.GitHub : x.Site))), new Thickness(0, 2, 0, 0)));
            string where, colour = "Yellow";
            switch (st.Is)
            {
                case "done":
                    where = Texts.Get("note_protected_session");      // shown only then
                    colour = "Blue";
                    break;
                case "missing":
                    where = s.Hint ? Texts.Get("plan_is_hint")
                          : s.Plugin != null || (auto.Count > 0 && !byHand) ? Texts.Get("plan_is_missing")
                          : Recipes.SitesOf(s).Any(x => x.Site == "Nexus Mods") ? Texts.Get("plan_is_hand_nexus") : Texts.Get("plan_is_hand");
                    if (s.Hint) colour = "Muted";
                    break;
                case "off": where = Texts.Get("plan_is_off"); break;
                case "old": where = Texts.F("plan_is_old", s.Version, st.Have ?? "?"); break;
                case "loader": where = Texts.Get("plan_is_loader"); break;
                case "target": where = Texts.Get("plan_is_target"); break;
                case "waiting": where = st.Why ?? Texts.Get("note_" + st.WhyKey + "_tip"); break;
                default: where = st.Is; break;
            }
            if (s.Optional && !st.Done) colour = "Muted";
            // done: the tick says it, unless there is more to say
            if (!st.Done || st.WhyKey != null)
                words.Children.Add(new TextBlock { Text = where, Foreground = B(colour), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            if (failed) words.Children.Add(Selectable.Text(planSaid, 12, "Yellow"));
            row.Children.Add(words);
            return row;
        }

        /// <summary>What the mod's log said of the last game: that it ran, what stopped it, or that it never began.</summary>
        UIElement PlanOutcome(Recipes.Recipe plan)
        {
            var lang = Texts.Lang;
            var box = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            var head = new DockPanel();
            var again = new Button { Content = Texts.Get("plan_check_again"), Style = (Style)FindResource("Ghost"), MinHeight = 30, FontSize = 12 };
            again.Click += (s, e) => RenderDownloads();
            DockPanel.SetDock(again, Dock.Right);
            head.Children.Add(again);
            head.Children.Add(new TextBlock { Text = Texts.Get("plan_last_game"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center });
            box.Children.Add(head);
            Recipes.Outcome o;
            try { o = Recipes.Read(plan); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { o = null; }
            void Say(string text, string colour) =>
                box.Children.Add(new TextBlock { Text = text, Foreground = B(colour), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            if (o == null) Say(Texts.Get("plan_never"), "Muted");
            else
            {
                var when = o.When.ToString("yyyy-MM-dd HH:mm");
                if (!o.Started) Say(Texts.F("plan_not_started", when), "Yellow");
                else if (o.Problems.Count == 0) Say(Texts.F("plan_ran", when), "Blue");
                else
                {
                    Say(Texts.F("plan_problems", when), "Yellow");
                    foreach (var p in o.Problems) Say("• " + (Recipes.In(p.Say, lang) ?? p.Find), "Yellow");
                }
            }
            return box;
        }

        /// <summary>One step done: a file downloaded with its bar in the look-up card, then added,
        /// turned on, its options set (the changes' turn, as every change). Whether it went through.</summary>
        async Task<bool> PlanRun(Recipes.Recipe plan, Recipes.Step step)
        {
            if (busy || planRunning || job != null) return false;
            planRunning = true;
            planSaid = null;
            planSaidStep = null;
            bool jobOn = false;
            try
            {
                Recipes.State st;
                try { st = Recipes.StateOf(plan, step, Library.Load()); }
                catch (Exception e) when (e is IOException || e is FormatException || e is ModError) { planSaid = Texts.F("failed", e.Message); planSaidStep = step; return false; }
                string file = null;
                var auto = Recipes.AutoSites(step);
                if ((st.Is == "missing" || st.Is == "old") && step.Plugin == null && auto.Count > 0)
                {
                    await BeginJob(Recipes.In(step.Name, Texts.Lang), auto[0]);
                    jobOn = true;
                    var progress = JobProgress();
                    var got = await Task.Run(() => Cli.Call(() => { file = Recipes.Download(step, progress); return "install plan " + plan.Id + ": downloaded " + file; }));
                    if (!got.Ok) { Said(got); return false; }
                    JobStage("add");
                }
                var r = await Exec(() => Cli.Call(() => "install plan " + plan.Id + ": " + Recipes.Run(plan, step, null, file)));
                if (file != null) try { File.Delete(file); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                if (!r.Ok) { Said(r); return false; }
                void Said(CliResult x)
                {
                    // got by hand from now on: its pages on its row, Install all goes past it
                    var kind = x.Refused?.Kind;
                    if (kind != null && Recipes.ByHand.Contains(kind)) planByHand.Add(Recipes.StepKey(step));
                    planSaid = kind == "recipe_choose" ? Texts.Get("plan_choose") : kind == "recipe_manual" ? null : PlanWhy(x);
                    planSaidStep = planSaid == null ? null : step;
                }
                Refresh();
                return true;
            }
            finally
            {
                planRunning = false;
                if (jobOn) EndJob();
            }
        }

        string PlanWhy(CliResult r)
        {
            var kind = r.Refused?.Kind;
            if (kind != null && CheckWhy(kind) != kind) return CheckWhy(kind);
            if (kind != null && Why(kind) != kind) return Why(kind);
            return Texts.F("failed", r.Message);
        }

        /// <summary>Every step that needs no hand, in order, each waited for; the first that does not
        /// go through stops it, said on its row.</summary>
        async void PlanAll(Recipes.Recipe plan)
        {
            await PlanSteps(plan);
            RenderDownloads();
        }

        /// <summary>Every step of a plan that needs no hand, in turn, the plan built again after each
        /// (a dependency added may need more); the first that does not go through stops it, said on
        /// its row. Whether none is left undone that could be done.</summary>
        async Task<bool> PlanSteps(Recipes.Recipe plan)
        {
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool handed = false;
            for (int pass = 0; pass < 12; pass++)
            {
                var now = Rebuilt(plan);
                Recipes.State next;
                try
                {
                    var lib = Library.Load();
                    next = now.Steps.Where(s => !s.Optional && !s.Main && !planByHand.Contains(Recipes.StepKey(s))).Select(s => Recipes.StateOf(now, s, lib)).FirstOrDefault(CanDo);
                }
                catch (Exception e) when (e is IOException || e is FormatException || e is ModError) { return false; }
                if (next == null) return !handed;
                if (!tried.Add(Recipes.StepKey(next.Step) + "|" + next.Is)) return false;
                if (!await PlanRun(now, next.Step))
                {
                    // one to get by hand: the rest go on
                    if (!planByHand.Contains(Recipes.StepKey(next.Step))) return false;
                    handed = true;
                }
            }
            return false;
        }
    }
}
