using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Hd2Mod;

namespace QuickDeploy
{
    // Mods for their author's members (hd2_license.json, core\Plugins.cs): the license
    // plugin's card on the plugins page (the plug beside the gear) with each author, signing in
    // through the browser, and renewal in the background.
    public partial class MainWindow
    {
        CancellationTokenSource licenseWait;
        string licenseSigningIn;

        static string Until(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        /// <summary>What a mod row says of its license; null when it needs none or has one.</summary>
        static string LicenseMark(ModRow m) =>
            m.License == null || m.License == "licensed" ? null
            : m.License == "expired" ? Texts.Get("row_license_expired") : Texts.Get("row_license");

        static string LicenseWords(string state, long expires) =>
            state == "licensed" ? Texts.F("lic_licensed", Until(expires))
            : state == "expired" ? Texts.F("lic_expired", Until(expires))
            : Texts.Get("lic_" + state);

        /// <summary>The plugins page: Add plugin… beside the title, and a card for each plugin
        /// added (PluginFiles), headed by its title, file, version and Remove. None ships with the
        /// program (the user, 2026-11-02): they are added here, or dropped on the page. Two kinds:
        /// one drawn in this window (the license plugin's authors and sign-in), one with a window
        /// of its own, opened from its card.</summary>
        void RenderPlugins()
        {
            PluginsBox.Children.Clear();
            var top = new DockPanel();
            var add = new Button { Content = Texts.Get("plugin_add"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            add.Click += (s, e) => PickPlugin();
            DockPanel.SetDock(add, Dock.Right);
            top.Children.Add(add);
            top.Children.Add(new TextBlock { Text = Texts.Get("plugins"), FontSize = 28, FontWeight = FontWeights.Bold });
            PluginsBox.Children.Add(top);
            PluginsBox.Children.Add(Texts.Hints ? Small(Texts.Get("plugins_intro"), new Thickness(0, 4, 0, 20)) : new Border { Height = 16 });
            var all = PluginFiles.All();
            if (all.Count == 0) PluginsBox.Children.Add(Small(Texts.Get("plugins_none"), new Thickness(0, 0, 0, 0)));
            bool first = true;
            foreach (var (file, type, why) in all)
            {
                var card = new StackPanel();
                var head = new DockPanel();
                var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
                right.Children.Add(new TextBlock
                {
                    Text = System.IO.Path.GetFileNameWithoutExtension(file) + (type != null ? " " + PluginFiles.Version(type) : ""),
                    FontSize = 12, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0),
                });
                var remove = new Button { Content = Texts.Get("plugin_remove"), Style = (Style)FindResource("Ghost"), MinHeight = 32, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(12, 0, 0, 0) };
                remove.Click += (s, e) => RemovePlugin(file, type != null ? PluginFiles.Title(type, Texts.Zh) ?? file : file);
                right.Children.Add(remove);
                DockPanel.SetDock(right, Dock.Right);
                head.Children.Add(right);
                var title = Heading(type != null ? PluginFiles.Title(type, Texts.Zh) ?? file : file);
                title.VerticalAlignment = VerticalAlignment.Center;          // level with the version and Remove
                head.Children.Add(title);
                card.Children.Add(head);
                if (type == null)
                    card.Children.Add(Small(Texts.Get(why == "api" ? "plugin_api" : "plugin_broken"), new Thickness(0, 8, 0, 0)));
                else if (string.Equals(file, "HD2QD.License.dll", StringComparison.OrdinalIgnoreCase))
                    card.Children.Add(LicenseSection() ?? Small(Texts.Get("lic_nothing"), new Thickness(0, 8, 0, 0)));
                else
                {
                    var summary = PluginFiles.Summary(type, Texts.Zh);
                    if (summary != null) card.Children.Add(Small(summary, new Thickness(0, 6, 0, 0)));
                    if (PluginFiles.Kind(type) == "window") card.Children.Add(OpenButton(type));
                }
                if (!first) PluginsBox.Children.Add(new Border { Height = 16 });
                PluginsBox.Children.Add(Card(card));
                first = false;
            }
            // mods for an author's members, and no license plugin to sign in with: said here, where
            // their rows point
            if (!all.Any(p => string.Equals(p.File, "HD2QD.License.dll", StringComparison.OrdinalIgnoreCase)) && LicenseSection() is UIElement need)
            {
                var card = new StackPanel();
                card.Children.Add(Heading(Texts.Get("lic_title")));
                card.Children.Add(need);
                PluginsBox.Children.Add(new Border { Height = 16 });
                PluginsBox.Children.Add(Card(card));
            }
        }

        /// <summary>A window plugin's Open, with what the manager gives every such plugin: its
        /// settings folder, the game's data folder, the language, and the enabled mods in load
        /// order (a Func, read each time the plugin asks).</summary>
        UIElement OpenButton(Type type)
        {
            var game = Store.GameDir(null, false);
            var open = new Button
            {
                Content = Texts.Get("plugin_open"), Style = (Style)FindResource("Primary"), MinHeight = 36, MinWidth = 96,
                Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = game != null,
                ToolTip = game == null ? Texts.Get("plugin_no_game") : null,
            };
            ToolTipService.SetShowOnDisabled(open, true);
            open.Click += (s, e) =>
            {
                var host = new Dictionary<string, object>
                {
                    ["home"] = Store.Home, ["data"] = Paths.Join(game, "data"), ["zh"] = Texts.Zh,
                    ["mods"] = (Func<List<Dictionary<string, object>>>)(() => Vram.Mods(Library.Load())),
                    // every library mod whose Lua writes lists for the settings window: {name,
                    // enabled, settings: [list names]} (LuaMods.SettingsNames); may read archives
                    ["library"] = (Func<List<Dictionary<string, object>>>)(() => LuaMods.Of(Library.Load())
                        .Where(ml => ml.Settings.Any())
                        .Select(ml => new Dictionary<string, object> { ["name"] = ml.Mod.Name, ["enabled"] = ml.Mod.Enabled, ["settings"] = ml.Settings.Cast<object>().ToList() })
                        .ToList()),
                    // the manager makes the plugin's window, so its caption matches the main one
                    ["window"] = (Func<string, FrameworkElement, Window>)((title, content) => new ChromeWindow(this, title, content)),
                };
                try { type.GetMethod("Open").Invoke(null, new object[] { this, host }); }
                catch (System.Reflection.TargetInvocationException x) { Dialog.Tell(this, Texts.F("failed", x.InnerException?.Message ?? x.Message)); }
            };
            return open;
        }

        void PickPlugin()
        {
            var pick = new Microsoft.Win32.OpenFileDialog
            {
                Title = Texts.Get("plugin_add_title"), Filter = Texts.Get("plugin_filter") + "|HD2QD.*.dll", CheckFileExists = true, Multiselect = true,
            };
            if (pick.ShowDialog(this) == true) AddPlugins(pick.FileNames);
        }

        /// <summary>Plugin files chosen or dropped: each one read without running it, then added
        /// once the player agrees (a plugin runs as this program does), over one of the same name.</summary>
        void AddPlugins(IEnumerable<string> paths)
        {
            var added = new List<string>();
            foreach (var path in paths)
            {
                var (file, version, why) = PluginFiles.Inspect(path);
                if (why != null)
                {
                    Dialog.Tell(this, Texts.F(why == "name" ? "plugin_add_name" : why == "api" ? "plugin_add_api" : "plugin_add_broken", file));
                    continue;
                }
                var old = PluginFiles.All().FirstOrDefault(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase));
                bool replace = old.File != null;
                var ask = replace ? Texts.F("plugin_replace_ask", file, version, old.Type != null ? PluginFiles.Version(old.Type) : "?")
                                  : Texts.F("plugin_add_ask", file, version);
                if (!Dialog.Ask(this, ask, Texts.Get(replace ? "plugin_replace_go" : "plugin_add_go"))) continue;
                try { PluginFiles.Install(path); added.Add(file + " " + version); }
                catch (Exception e) when (e is ModError || e is System.IO.IOException || e is UnauthorizedAccessException)
                { Dialog.Tell(this, Texts.F("failed", e.Message)); }
            }
            if (added.Count == 0) return;
            Foot(Texts.F("plugin_added", string.Join("、", added)), "Blue");
            RenderPlugins();
            Refresh();                      // the license plugin changes what the mod rows say
        }

        void RemovePlugin(string file, string title)
        {
            if (!Dialog.AskDanger(this, Texts.F("plugin_remove_ask", title, file), Texts.Get("plugin_remove"))) return;
            try { PluginFiles.Remove(file); }
            catch (Exception e) when (e is ModError || e is System.IO.IOException || e is UnauthorizedAccessException)
            { Dialog.Tell(this, Texts.F("failed", e.Message)); return; }
            Foot(Texts.F("plugin_removed", title), "Blue");
            RenderPlugins();
            Refresh();
        }

        static List<string> DroppedPlugins(IDataObject data) =>
            (data != null && data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null)?
                .Where(p => System.IO.File.Exists(p) && p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<string>();

        void PluginsPage_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DroppedPlugins(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void PluginsPage_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            var paths = DroppedPlugins(e.Data);
            if (paths.Count > 0) AddPlugins(paths);
        }

        void Plugins_Click(object sender, RoutedEventArgs e)
        {
            if (PluginsPage.Visibility == Visibility.Visible) { ShowPage(pageBack); return; }
            ShowPage("plugins");
        }

        /// <summary>Every author whose mods are in the library, and every author signed in here
        /// whose mods are not; null when there is none.</summary>
        UIElement LicenseSection()
        {
            List<(Dictionary<string, object> Decl, List<Mod> Mods)> issuers;
            try { issuers = Licenses.Present ? Licenses.Known(Library.Load()) : null; }
            catch (Exception e) when (e is ModError || e is System.IO.IOException) { return null; }
            var marked = st?.Mods.Where(x => x.License != null).ToList() ?? new List<ModRow>();
            if (issuers == null ? marked.Count == 0 : issuers.Count == 0) return null;

            var panel = new StackPanel();
            panel.Children.Add(Small(Texts.Get("lic_intro"), new Thickness(0, 8, 0, 0)));
            if (issuers == null)
            {
                panel.Children.Add(Line(Texts.Get("lic_no_plugin"), "Yellow", Texts.F("lic_mods", string.Join("、", marked.Select(x => x.Name)))));
                return panel;
            }
            foreach (var (decl, mods) in issuers)
            {
                string issuer = Json.Str(decl, "issuer");
                // read now, not from the last state: a sign-in or sign-out shows at once
                var now = mods.Count > 0 ? Licenses.StateOf(mods[0].Path) : Licenses.Status(decl);
                string state = Json.Str(now, "state") ?? "none";
                bool busy = licenseSigningIn == issuer;
                var line = Line(busy ? Texts.Get("lic_waiting") : LicenseWords(state, Json.Long(now, "expires")),
                                busy ? "Blue" : state == "licensed" ? "Blue" : "Yellow",
                                Json.Str(decl, "name") + " · " + issuer + " · " +
                                (mods.Count > 0 ? Texts.F("lic_mods", string.Join("、", mods.Select(m => m.Name))) : Texts.Get("lic_no_mods")));
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 10, 0, 0) };
                if (busy)
                {
                    var cancel = new Button { Content = Texts.Get("cancel"), Style = (Style)FindResource("Ghost"), MinHeight = 36 };
                    cancel.Click += (s, e) => licenseWait?.Cancel();
                    buttons.Children.Add(cancel);
                }
                else
                {
                    bool canSignIn = state != "no_account" && state != "key_changed";
                    var signIn = new Button
                    {
                        Content = Texts.Get(state == "licensed" || state == "expired" ? "lic_sign_in_again" : "lic_sign_in"),
                        Style = (Style)FindResource(state == "licensed" ? "Ghost" : "Primary"), MinHeight = 36, IsEnabled = canSignIn && licenseSigningIn == null,
                    };
                    signIn.Click += (s, e) => SignInLicense(decl);
                    buttons.Children.Add(signIn);
                    if (state != "none" && state != "no_account")
                    {
                        var signOut = new Button { Content = Texts.Get("lic_sign_out"), Style = (Style)FindResource("Ghost"), MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
                        signOut.Click += (s, e) =>
                        {
                            if (!Dialog.Ask(this, Texts.Get("lic_sign_out_ask"), Texts.Get("lic_sign_out"))) return;
                            try { Licenses.Forget(issuer); }
                            catch (Exception x) when (x is ModError || x is System.IO.IOException || x is UnauthorizedAccessException) { Dialog.Tell(this, Texts.F("failed", x.Message)); }
                            Refresh();
                            RenderPlugins();
                        };
                        buttons.Children.Add(signOut);
                    }
                }
                var block = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                block.Children.Add(line);
                block.Children.Add(buttons);
                panel.Children.Add(block);
            }
            return panel;
        }

        /// <summary>A dot, the state in words, and what it is about underneath.</summary>
        UIElement Line(string words, string colour, string about)
        {
            var head = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var dot = new Ellipse { Width = 10, Height = 10, Fill = B(colour), Margin = new Thickness(0, 5, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(dot, Dock.Left);
            head.Children.Add(dot);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = words, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(Small(about, new Thickness(0, 2, 0, 0)));
            head.Children.Add(text);
            return head;
        }

        async void SignInLicense(Dictionary<string, object> decl)
        {
            string issuer = Json.Str(decl, "issuer");
            Dictionary<string, object> begin;
            List<string> accounts;
            try
            {
                begin = Licenses.BeginSignIn(decl);
                accounts = Licenses.Accounts();
            }
            catch (ModError x) { Dialog.Tell(this, Texts.F("failed", x.Message)); return; }
            if (Json.Str(begin, "error") != null) { Dialog.Tell(this, Texts.Get("lic_no_account")); return; }
            // the issuer learns the game account: said before anything is sent
            if (!Dialog.Ask(this, Texts.F("lic_ask", issuer, Json.Str(decl, "name"), accounts[0]), Texts.Get("lic_sign_in"))) return;

            licenseSigningIn = issuer;
            licenseWait = new CancellationTokenSource();
            RenderPlugins();
            Foot(Texts.Get("lic_waiting"), "Blue");
            Dictionary<string, object> r;
            try
            {
                Process.Start(new ProcessStartInfo(Json.Str(begin, "url")) { UseShellExecute = true });
                var token = licenseWait.Token;
                r = await Task.Run(() => Licenses.FinishSignIn(decl, Json.Str(begin, "nonce"), TimeSpan.FromMinutes(10), token));
            }
            catch (Exception x) when (x is ModError || x is System.ComponentModel.Win32Exception || x is System.IO.IOException || x is UnauthorizedAccessException)
            {
                r = new Dictionary<string, object> { ["error"] = "other", ["detail"] = x.Message };
            }
            finally
            {
                licenseSigningIn = null;
                licenseWait = null;
            }
            var error = Json.Str(r, "error");
            if (error == null) Foot(Texts.F("lic_done", Until(Json.Long(r, "expires"))), "Blue");
            else if (error == "cancelled") Foot(Texts.Get("lic_cancelled"), "Muted");
            else
            {
                Foot(LicenseError(error, Json.Str(r, "detail")), "Yellow");
                Dialog.Tell(this, LicenseError(error, Json.Str(r, "detail")));
            }
            Refresh();
            RenderPlugins();
        }

        static string LicenseError(string error, string detail = null)
        {
            switch (error)
            {
                case "not_member": case "account_limit": case "denied": case "timeout": case "network": case "bad_license": case "key_changed":
                    return Texts.Get("lic_err_" + error);
                case "sign_in_again": case "unknown": return Texts.Get("lic_err_sign_in_again");
                default: return Texts.F("lic_err_other", detail ?? error);
            }
        }

        /// <summary>At start, in the background: each author's license that is half way to its
        /// end is renewed. A failure says nothing here (the card shows the state, and the old
        /// license runs to its end); the next start tries again.</summary>
        async void LicensesAtStart()
        {
            if (!Licenses.Present) return;
            bool renewed = false;
            try
            {
                renewed = await Task.Run(() =>
                {
                    bool any = false;
                    foreach (var (decl, _) in Licenses.Issuers(Library.Load()))
                        any |= Json.Bool(Licenses.Renew(decl, false), "renewed");
                    return any;
                });
            }
            catch (Exception e) when (e is ModError || e is System.IO.IOException || e is UnauthorizedAccessException) { return; }
            if (renewed) Refresh();
        }
    }
}
