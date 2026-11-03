using System.Collections.Generic;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>Undoing the changes made since the last deploy (undo-changes; the window's button
    /// under Restore vanilla; the user, 2026-11-04): the deploy keeps the library as it took it.</summary>
    public class UndoChanges : Fixture
    {
        static string IdOf(string name) => Library.Load().Mods.Single(m => m.Name == name).Id;

        bool CanUndo() => Json.Bool(StateDoc.Build(Library.Load(), Game), "can_undo");

        void ThreeModsDeployed()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", Files((A + ".patch_0", "two"))));
            Cli("add", Mod("Three", new Dictionary<string, byte[]>
            {
                ["red/" + A + ".patch_0"] = Bytes("three red"),
                ["blue/" + A + ".patch_0"] = Bytes("three blue"),
            }, "{\"Name\": \"Three\", \"Options\": [{\"Name\": \"Colour\", \"SubOptions\": [" +
               "{\"Name\": \"Red\", \"Include\": [\"red\"]}, {\"Name\": \"Blue\", \"Include\": [\"blue\"]}]}]}"));
            Cli("set", IdOf("Three"), "1.1", "on");                            // Red
            Cli("deploy");
        }

        /// <summary>Must live: switches, the order and an option go back as the deploy took them; a
        /// mod added since is turned off and goes last; a removed one is named, not brought back.
        /// Then nothing is left to undo.</summary>
        [Fact]
        public void The_library_goes_back_to_the_last_deploy()
        {
            ThreeModsDeployed();
            Assert.False(CanUndo());
            Cli("disable", IdOf("One"));
            Cli("move", IdOf("Three"), "--top");
            Cli("set", IdOf("Three"), "1.2", "on", "--only");                  // Blue
            Cli("add", Mod("Four", Files((B + ".patch_0", "four"))));
            Cli("remove", IdOf("Two"));
            Assert.True(CanUndo());

            var said = Cli("undo-changes");
            Assert.Contains("removed since, not brought back: Two", said);
            var lib = Library.Load();
            Assert.Equal(new[] { "One", "Three", "Four" }, lib.Mods.Select(m => m.Name));
            Assert.Equal(new[] { true, true, false }, lib.Mods.Select(m => m.Enabled));
            var colour = lib.Mods.Single(m => m.Name == "Three").Options.Single();
            Assert.Equal(new[] { true, false }, colour.Subs.Select(s => s.Enabled));      // Red again
            // the removed one cannot come back, so nothing is left to undo
            Assert.False(CanUndo());
            Assert.Contains("nothing to undo", Cli("undo-changes"));
        }

        /// <summary>Must die: before any deploy there is nothing to go back to; with nothing
        /// changed, nothing is done; another profile in use than the deploy's is refused.</summary>
        [Fact]
        public void Nothing_to_go_back_to_is_refused()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Assert.Equal("undo_none", Refused("undo-changes"));
            Assert.False(CanUndo());
            Cli("deploy");
            Assert.Contains("nothing to undo", Cli("undo-changes"));
            Cli("profiles-on");
            Cli("profile-new", "Second");
            Cli("profile-use", "Second", "--no-deploy");
            Assert.False(CanUndo());
            Assert.Equal("undo_profile", Refused("undo-changes"));
        }

        /// <summary>A deploy made before the library was kept with it: while nothing is left to
        /// deploy, a state read keeps the library as that deploy's (the user, 2026-11-04: the
        /// button never came). Must live: kept, then a change can be undone. Must die: with a
        /// change not deployed, nothing is kept (the library is not what the game has).</summary>
        [Fact]
        public void An_older_deploy_gets_the_library_once_nothing_is_left_to_deploy()
        {
            ThreeModsDeployed();
            var r = Store.Load("deployed.json");
            r.Remove("library");
            r.Remove("names");
            Store.Save("deployed.json", r);
            Cli("disable", IdOf("One"));
            Assert.False(CanUndo());
            Assert.Null(Json.Obj(Store.Load("deployed.json"), "library"));    // One off, not deployed: not the game's
            Cli("enable", IdOf("One"));
            Assert.False(CanUndo());
            Assert.NotNull(Json.Obj(Store.Load("deployed.json"), "library")); // as the game has it again: kept
            Cli("move", IdOf("Three"), "--top");
            Assert.True(CanUndo());
            Cli("undo-changes");
            Assert.Equal(new[] { "One", "Two", "Three" }, Library.Load().Mods.Select(m => m.Name));
        }

        /// <summary>A deploy after the changes is the new point to go back to.</summary>
        [Fact]
        public void A_new_deploy_is_the_new_point()
        {
            ThreeModsDeployed();
            Cli("disable", IdOf("One"));
            Cli("deploy");
            Assert.False(CanUndo());
            Cli("enable", IdOf("One"));
            Cli("undo-changes");
            Assert.False(Library.Load().Mods.Single(m => m.Name == "One").Enabled);
        }
    }
}
