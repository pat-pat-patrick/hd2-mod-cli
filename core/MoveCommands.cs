using System;
using System.IO;
using System.Linq;

namespace Hd2Mod
{
    /// <summary>The move package on the command line (MovePack).</summary>
    public static partial class Commands
    {
        static string Gb(long b) => (b / (double)(1L << 30)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GB";

        static void MoveExport(Args a, TextWriter o)
        {
            var file = a.Pos["file"];
            if (Directory.Exists(file)) file = Path.Combine(file, MovePack.DefaultName("hd2qd-move"));
            var p = MovePack.Export(file);
            o.WriteLine("packed: " + p.Mods + " mods, " + Gb(p.Bytes) + " -> " + Gb(p.Size) + " in " +
                        p.Seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
            foreach (var part in p.Parts) o.WriteLine("  " + part);
            if (p.Parts.Count > 1) o.WriteLine("every part goes with the first: they are read as one");
            if (p.Missing.Count > 0) o.WriteLine("not found, in the list without their files: " + string.Join(", ", p.Missing));
        }

        static void MoveInfo(Args a, TextWriter o)
        {
            var i = MovePack.Read(a.Pos["file"]);
            o.WriteLine("mods: " + i.Mods + " (" + Gb(i.ModBytes) + ")");
            o.WriteLine("made: " + DateTimeOffset.FromUnixTimeSeconds(i.Created).LocalDateTime.ToString("yyyy-MM-dd HH:mm") + " by " + i.Program);
            o.WriteLine("parts: " + i.Parts);
        }

        static void MoveRestore(Args a, TextWriter o)
        {
            var r = MovePack.Restore(a.Pos["file"], a.Value("--game"), a.Value("--into"), a.Flag("--replace"));
            o.WriteLine("restored: " + r.Mods + " mods (" + Gb(r.Bytes) + ") into " + r.Into + " in " +
                        r.Seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
            o.WriteLine("game: " + r.Game);
            if (r.Settings > 0) o.WriteLine("mod settings files: " + r.Settings);
            if (r.Plugins.Count > 0)
            {
                o.WriteLine("plugins in the package, not added (a plugin runs as this program does): add the ones you trust with");
                foreach (var f in r.Plugins) o.WriteLine("  hd2qd plugin-add \"" + f + "\"");
            }
            o.WriteLine("deploy to bring the game folder up to date: hd2qd deploy");
        }
    }
}
