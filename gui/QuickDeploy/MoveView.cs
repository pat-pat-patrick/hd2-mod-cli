using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Hd2Mod;
using IOPath = System.IO.Path;

namespace QuickDeploy
{
    /// <summary>Moving the whole setup to another computer (MovePack; the user, 2026-11-25: "一键搬迁
    /// ... 不要再找路径、复制粘贴"): one button packs it into a file, and on the new computer the file
    /// opened (double-clicked, dropped on the window, or chosen on the Storage page) is restored and
    /// deployed. Both run as a job with its bar, the window free meanwhile.</summary>
    public partial class MainWindow
    {
        static readonly System.Text.RegularExpressions.Regex MovePart =
            new System.Text.RegularExpressions.Regex(@"\.hd2qdmove(\.\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>A move package's file, or one of its later parts (NAME.hd2qdmove.2).</summary>
        static bool IsMovePack(string p) => System.IO.File.Exists(p) && MovePart.IsMatch(p);

        /// <summary>The package's first part, which is opened for all of them.</summary>
        static string FirstPart(string p) => MovePart.Replace(p, MovePack.Extension);

        /// <summary>The Storage page's card: what moves, and the two buttons.</summary>
        UIElement MoveCard()
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading(Texts.Get("mv_title")));
            panel.Children.Add(Small(Texts.Get("mv_note"), new Thickness(0, 8, 0, 0)));
            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            var pack = new Button { Content = Texts.Get("mv_export"), Style = (Style)FindResource("Ghost"), MinHeight = 40, Margin = new Thickness(0, 0, 8, 0) };
            pack.Click += (s, e) => ExportMove();
            row.Children.Add(pack);
            var restore = new Button { Content = Texts.Get("mv_restore"), Style = (Style)FindResource("Ghost"), MinHeight = 40 };
            restore.Click += (s, e) =>
            {
                var f = Pick.File(this, Texts.Get("mv_open_title"), Texts.Get("mv_kind"), "*" + MovePack.Extension);
                if (f != null) RestoreMove(f);
            };
            row.Children.Add(restore);
            panel.Children.Add(row);
            return new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Background = B("Ground700"), Padding = new Thickness(16), Child = panel };
        }

        /// <summary>A refusal of the move package in the window's words.</summary>
        static string MoveWhy(CliResult r)
        {
            var e = r.Refused;
            var parts = (e?.Arg ?? "").Split('|');
            switch (e?.Kind)
            {
                case "move_damaged": return Texts.Get("mv_damaged");
                case "move_short":
                case "move_part_missing": return Texts.F("mv_short", IOPath.GetFileName(e.Arg ?? ""));
                case "move_part_other": return Texts.F("mv_part_other", IOPath.GetFileName(e.Arg ?? ""));
                case "move_not_pack": return Texts.Get("mv_not_pack");
                case "move_newer": return Texts.F("mv_newer", e.Arg);
                case "move_disk_full": return Texts.F("mv_disk_full", e.Arg);
                case "move_space": return parts.Length == 3 && long.TryParse(parts[0], out var need) && long.TryParse(parts[1], out var free)
                                          ? Texts.F("mv_space", Texts.Size(need), Texts.Size(free), parts[2].TrimEnd('\\'))
                                          : Texts.F("failed", r.Message);
                case "move_changed": return Texts.F("mv_changed", e.Arg);
                case "move_read": return Texts.F("mv_read", e.Arg);
                default: return Texts.F("failed", r.Message);
            }
        }

        /// <summary>The library packed into a file the player names (today's name offered).</summary>
        async void ExportMove()
        {
            if (Library.Load().Mods.Count == 0) { Dialog.Tell(this, Texts.Get("mv_empty")); return; }
            var file = Pick.Save(this, Texts.Get("mv_save_title"), Texts.Get("mv_kind"), "*" + MovePack.Extension, MovePack.DefaultName(Texts.Get("mv_file_word")));
            if (file == null) return;
            await BeginJob(Texts.Get("mv_job_pack"), "");
            JobStage("pack");
            var progress = JobProgress();
            MovePack.Packed p = null;
            CliResult r;
            try { r = await Task.Run(() => Cli.Call(() => { p = MovePack.Export(file, progress); return "packed: " + p.Mods + " mods into " + string.Join(", ", p.Parts); })); }
            finally { EndJob(); }
            if (!r.Ok) { Dialog.Tell(this, MoveWhy(r)); return; }
            var said = Texts.F("mv_packed", p.Mods, Texts.Size(p.Bytes), Texts.Size(p.Size), Seconds(p.Seconds), p.Parts[0]);
            if (p.Parts.Count > 1) said += "\n\n" + Texts.F("mv_parts", p.Parts.Count, string.Join("\n", p.Parts.Select(IOPath.GetFileName)));
            if (p.Missing.Count > 0) said += "\n\n" + Texts.F("mv_missing", p.Missing.Count, string.Join(Texts.Sep, p.Missing));
            Foot(Texts.F("mv_packed_foot", p.Mods, Texts.Size(p.Size)), "Blue");
            Dialog.Tell(this, said);
        }

        /// <summary>A package put in place here: said what it holds and where its mods go, then
        /// restored as a job, its plugins added as any plugin file is (an official release at once,
        /// another one asked), and the game deployed.</summary>
        async void RestoreMove(string file)
        {
            file = FirstPart(file);
            MovePack.Info info;
            try { info = MovePack.Read(file); }
            catch (ModError e) { Dialog.Tell(this, MoveWhy(new CliResult { Code = 1, Err = e.Message, Refused = e })); return; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Dialog.Tell(this, Texts.F("failed", e.Message)); return; }
            var game = st?.GameDir ?? Ops.FindGame();
            if (game == null)
            {
                Dialog.Tell(this, Texts.Get("game_ask"));
                game = PickGame();
                if (game == null) return;
            }
            var into = IOPath.Combine(IOPath.GetPathRoot(game), "HD2 Mods");
            int have = Library.Load().Mods.Count;
            var made = DateTimeOffset.FromUnixTimeSeconds(info.Created).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            var ask = Texts.F("mv_restore_ask", info.Mods, Texts.Size(info.ModBytes), made, into);
            if (have > 0)
            {
                if (!Dialog.AskDanger(this, ask + "\n\n" + Texts.F("mv_restore_replace", have), Texts.Get("mv_restore_go"))) return;
            }
            else if (!Dialog.Ask(this, ask, Texts.Get("mv_restore_go"))) return;

            await BeginJob(Texts.Get("mv_job_restore"), "");
            JobStage("restore");
            var progress = JobProgress();
            MovePack.Restored res = null;
            CliResult r;
            // in the changes' turn: the library is replaced at its end
            try { r = await Queued(() => Cli.Call(() => { res = MovePack.Restore(file, game, into, have > 0, progress); return "restored: " + res.Mods + " mods into " + res.Into; })); }
            finally { EndJob(); }
            if (!r.Ok) { Dialog.Tell(this, MoveWhy(r)); return; }
            Refresh();

            // the plugins it held, as plugin files are added: the official list first, so an
            // official release goes in without a question
            if (res.Plugins.Count > 0)
            {
                var lang = Texts.Lang;
                try { await Task.Run(() => PluginFiles.RefreshList(lang)); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                AddPlugins(res.Plugins);
                try { Directory.Delete(MovePack.Held, true); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }

            // the game filled as the library says, on the official game; a plugin's target is
            // deployed from its own switch
            bool official = string.IsNullOrEmpty(Library.Load().Target);
            bool deployed = official && await Act(() => Cli.Call(() =>
            {
                var lib = Library.Load();
                Protected.Prepare(lib, Session.IsOpen());
                return DeploySaid(Hd2Mod.Deploy.Run(lib, Store.GameDir()));
            }), true);
            var said = Texts.F(deployed ? "mv_restored" : "mv_restored_not_deployed", res.Mods, Seconds(res.Seconds));
#if HAS_NEXUS
            said += "\n\n" + Texts.Get("mv_sign_in_again");
#endif
            Dialog.Tell(this, said);
        }

        /// <summary>A move package opens with this program: double-clicking one on the new computer
        /// restores it (HKCU, no administrator; the path follows this program when it moves). Not for
        /// a window on a settings folder of its own (a test or a demo).</summary>
        static void RegisterMovePack()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HD2MOD_HOME"))) return;
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                var command = "\"" + exe + "\" \"%1\"";
                const string prog = "HD2QuickDeploy.MovePackage";
                using (var cls = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
                {
                    bool changed = false;
                    using (var ext = cls.CreateSubKey(MovePack.Extension))
                        if (ext.GetValue("") as string != prog) { ext.SetValue("", prog); changed = true; }
                    using (var p = cls.CreateSubKey(prog))
                    {
                        if (p.GetValue("") as string != Texts.Get("mv_kind")) p.SetValue("", Texts.Get("mv_kind"));
                        using (var icon = p.CreateSubKey("DefaultIcon"))
                            if (icon.GetValue("") as string != exe + ",0") { icon.SetValue("", exe + ",0"); changed = true; }
                        using (var open = p.CreateSubKey(@"shell\open\command"))
                            if (open.GetValue("") as string != command) { open.SetValue("", command); changed = true; }
                    }
                    if (changed) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is System.Security.SecurityException || e is IOException || e is InvalidOperationException) { }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
    }
}
