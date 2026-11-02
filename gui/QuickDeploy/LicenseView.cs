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
    // Mods for their author's members (hd2_license.json, core\Plugins.cs): a section on the
    // settings page with each author, signing in through the browser, and renewal in the
    // background.
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
                            RenderSettings();
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
            RenderSettings();
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
            RenderSettings();
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
