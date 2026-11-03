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

        /// <summary>A resource's hash is kept while its archive is unchanged (Deploy.Hashes,
        /// resource_hashes.json, 2026-11-03). Must live: the same bytes stay shared on the next
        /// read, from what was kept. Must die: an archive written over is hashed again.</summary>
        [Fact]
        public void A_kept_hash_follows_an_archive_written_over()
        {
            var bytes = Archive(Bytes("the same"), (1, 101));
            Cli("add", Mod("One", new Dictionary<string, byte[]> { [A + ".patch_0"] = bytes }));
            Cli("add", Mod("Two", new Dictionary<string, byte[]> { [A + ".patch_0"] = bytes }));
            bool Same() => Json.Bool(Json.List(StateDoc.Build(Library.Load(), Game), "conflicts").OfType<Dictionary<string, object>>().Single(), "same");
            Assert.True(Same());
            Assert.True(Same());
            Assert.True(File.Exists(Path.Combine(Store.Home, "resource_hashes.json")));
            var two = Library.Load().Mods.Single(m => m.Name == "Two");
            File.WriteAllBytes(Directory.GetFiles(two.Path, A + ".patch_0", SearchOption.AllDirectories).Single(), Archive(Bytes("not the same"), (1, 101)));
            Assert.False(Same());
        }

        /// <summary>The name list the Resource names plugin downloads into the settings folder is
        /// read before HD2SDK's in Blender, and a new download is used at the next read (the user,
        /// 2026-11-03: the player fetches it from HD2SDK's repository). Must live: its name. Must
        /// die: after a download without that resource, no name.</summary>
        [Fact]
        public void The_downloaded_name_list_is_read_and_followed()
        {
            var list = ResourceNames.Downloaded;
            Directory.CreateDirectory(Path.GetDirectoryName(list));
            File.WriteAllText(list, "101 Helmet One\n102 Cape Two\n");
            Assert.Equal(list, ResourceNames.ListFile());
            System.Threading.Thread.Sleep(2100);                               // past the two seconds a list is kept
            Assert.Equal("Helmet One", ResourceNames.Name(101));
            File.WriteAllText(list, "102 Cape Two\n");
            File.SetLastWriteTimeUtc(list, System.DateTime.UtcNow.AddSeconds(5));
            System.Threading.Thread.Sleep(2100);
            Assert.Null(ResourceNames.Name(101));
            Assert.Equal("Cape Two", ResourceNames.Name(102));
        }

        /// <summary>A conflict often comes from an option, and another option avoids it (the user,
        /// 2026-11-03). Must live: the sub-option chosen is named as where the conflict comes from,
        /// and says whom it conflicts with. Must die: the other sub-option, which replaces something
        /// else, conflicts with nothing.</summary>
        [Fact]
        public void A_conflict_names_the_option_it_comes_from()
        {
            Cli("add", Mod("One", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("one's own"), (1, 101)) }));
            Cli("add", Mod("Two", new Dictionary<string, byte[]>
            {
                ["red/" + A + ".patch_0"] = Archive(Bytes("two's red"), (1, 101)),
                ["blue/" + A + ".patch_0"] = Archive(Bytes("two's blue"), (1, 102)),
            }, "{\"Name\": \"Two\", \"Options\": [{\"Name\": \"Colour\", \"SubOptions\": [" +
               "{\"Name\": \"Red\", \"Include\": [\"red\"]}, {\"Name\": \"Blue\", \"Include\": [\"blue\"]}]}]}"));
            var doc = StateDoc.Build(Library.Load(), Game);
            var set = Json.List(doc, "all_conflicts").OfType<Dictionary<string, object>>().Single(c => !Json.Bool(c, "same"));
            var from = Json.List(set, "from").OfType<Dictionary<string, object>>().Single();
            Assert.Equal(("Two", 0L, 0L), (Json.Str(from, "mod"), Json.Long(from, "option"), Json.Long(from, "sub")));
            var two = Json.List(doc, "mods").OfType<Dictionary<string, object>>().Single(m => Json.Str(m, "name") == "Two");
            var subs = Json.List(Json.List(two, "options").OfType<Dictionary<string, object>>().Single(), "subs").OfType<Dictionary<string, object>>().ToList();
            Assert.Equal(new[] { "One" }, Json.List(subs[0], "clashes").OfType<string>());
            Assert.Empty(Json.List(subs[1], "clashes"));
        }

        /// <summary>The conflicts view lists every mod in a conflict, on or off, with its switch
        /// (the user, 2026-11-03); the rest of the window marks a conflict only while the mods are
        /// on. Must live: the mod turned off is in all_conflicts. Must die: not in conflicts.</summary>
        [Fact]
        public void A_mod_turned_off_is_still_in_the_conflicts_view()
        {
            Cli("add", Mod("One", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("one's own"), (1, 100), (1, 101)) }));
            Cli("add", Mod("Two", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("two's own"), (1, 101), (2, 7)) }));
            Cli("add", Mod("Three", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("three's own"), (1, 101)) }));
            Cli("disable", Library.Load().Mods.Single(m => m.Name == "Three").Id);
            var doc = StateDoc.Build(Library.Load(), Game);
            List<Dictionary<string, object>> Sets(string key) => Json.List(doc, key).OfType<Dictionary<string, object>>().Where(c => !Json.Bool(c, "same")).ToList();
            var all = Sets("all_conflicts").Single();
            Assert.Equal(new[] { "One", "Two", "Three" }, Json.List(all, "mods").OfType<string>());
            Assert.Equal("Two", Json.Str(all, "winner"));                      // the last one on, not Three
            Assert.Equal(new[] { "One", "Two" }, Json.List(Sets("conflicts").Single(), "mods").OfType<string>());
        }
    }
}
