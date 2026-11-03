using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>Files another program changed in the game folder since the last deploy (the user,
    /// 2026-11-03: a sound fix written into deployed files lasts here and not under other
    /// managers, because a hard link written in place changes the library's file too): said on
    /// the mod's row, in the state's check block and by check; gone once deployed again.</summary>
    public class AlteredFiles : Fixture
    {
        const string C = "0123456789abcdef";

        public AlteredFiles()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", Files((B + ".patch_0", "two"))));
        }

        List<(string Name, string Mod, string How)> Altered() => Deploy.Check(Library.Load(), Game).Altered;

        List<string> NotesOf(string mod)
        {
            var row = Json.List(StateDoc.Build(Library.Load(), Game), "mods").OfType<Dictionary<string, object>>().Single(m => Json.Str(m, "name") == mod);
            return Json.List(row, "notes").OfType<Dictionary<string, object>>().Select(n => Json.Str(n, "key")).ToList();
        }

        [Fact]
        public void Nothing_is_said_before_a_deploy_or_after_one()
        {
            File.WriteAllText(Path.Combine(Data, C + ".patch_0"), "another manager's");   // no record yet: nothing to compare with
            Assert.Empty(Altered());
            Cli("deploy");
            Assert.Empty(Altered());
            Assert.Empty(NotesOf("One"));
        }

        /// <summary>A deployed hard link written in place: the library's own file changed with
        /// it, and a deploy keeps it (it is the library's file).</summary>
        [Fact]
        public void A_hard_link_written_in_place_changed_the_library()
        {
            Cli("deploy");
            using (var f = new FileStream(Path.Combine(Data, A + ".patch_0"), FileMode.Append)) f.WriteByte((byte)'!');
            Assert.Equal("one!", File.ReadAllText(Path.Combine(Root, "mods", "One", A + ".patch_0")));
            Assert.Equal(new[] { (A + ".patch_0", "One", "library") }, Altered());
            Assert.Equal(new[] { "altered_library" }, NotesOf("One"));
            Assert.Empty(NotesOf("Two"));
            Assert.Contains("changed in the library by another program (a deploy does not undo it): " + A + ".patch_0 (One)", Cli("check"));
            var check = Json.Obj(StateDoc.Build(Library.Load(), Game), "check");
            Assert.Equal(1L, Json.Long(check, "altered_n"));
            Cli("deploy");
            Assert.Equal("one!", Body(A + ".patch_0"));
            Assert.Empty(Altered());
        }

        /// <summary>The game's copy replaced by a new file: a deploy puts the library's back. One
        /// written again with the same bytes is nothing a deploy changes, and nothing to say.</summary>
        [Fact]
        public void A_replaced_game_file_is_put_back_by_a_deploy()
        {
            Cli("deploy");
            var p = Path.Combine(Data, B + ".patch_0");
            File.Delete(p);
            File.WriteAllText(p, "fixed");
            Assert.Equal(new[] { (B + ".patch_0", "Two", "game") }, Altered());
            Assert.Equal(new[] { "altered_game" }, NotesOf("Two"));
            Cli("deploy");
            Assert.Equal("two", Body(B + ".patch_0"));
            Assert.Empty(Altered());

            File.Delete(p);
            File.WriteAllText(p, "two");                                          // the same bytes, a new file
            File.SetLastWriteTimeUtc(p, File.GetLastWriteTimeUtc(p).AddMinutes(1));
            Assert.Empty(Altered());
        }

        /// <summary>A patch file this program did not put there: a deploy deletes it.</summary>
        [Fact]
        public void A_file_another_program_added_is_said_and_deleted_by_a_deploy()
        {
            Cli("deploy");
            File.WriteAllText(Path.Combine(Data, C + ".patch_0"), "a fix");
            Assert.Equal(new[] { (C + ".patch_0", (string)null, "added") }, Altered());
            Assert.Equal(1L, Json.Long(Json.Obj(StateDoc.Build(Library.Load(), Game), "check"), "added_n"));
            Assert.Contains("added by another program (a deploy deletes it): " + C + ".patch_0", CliCode(1, "check"));
            Cli("deploy");
            Assert.False(File.Exists(Path.Combine(Data, C + ".patch_0")));
            Assert.Empty(Altered());
        }
    }

    /// <summary>Whose copy of a shared resource the game has now, against the load order's winner:
    /// they part when another mod is chosen, and meet again at the deploy (the user, 2026-11-03:
    /// the conflicts view must show a choice not yet deployed).</summary>
    public class DeployedWinners : Fixture
    {
        Dictionary<string, object> Set() =>
            Json.List(StateDoc.Build(Library.Load(), Game), "conflicts").OfType<Dictionary<string, object>>().Single(c => !Json.Bool(c, "same"));

        [Fact]
        public void The_game_keeps_its_winner_until_the_deploy()
        {
            Cli("add", Mod("One", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("one's own"), (1, 100), (1, 101)) }));
            Cli("add", Mod("Two", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("two's own"), (1, 101), (2, 7)) }));
            Assert.Null(Json.Str(Set(), "deployed"));                          // nothing deployed yet
            Cli("deploy");
            Assert.Equal(("Two", "Two"), (Json.Str(Set(), "winner"), Json.Str(Set(), "deployed")));
            var ids = Library.Load().Mods.ToDictionary(m => m.Name, m => m.Id);
            Cli("move", ids["One"], "--after", ids["Two"]);                    // One chosen: not deployed yet
            Assert.Equal(("One", "Two"), (Json.Str(Set(), "winner"), Json.Str(Set(), "deployed")));
            Cli("deploy");
            Assert.Equal(("One", "One"), (Json.Str(Set(), "winner"), Json.Str(Set(), "deployed")));
        }
    }
}
