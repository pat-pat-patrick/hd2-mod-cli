using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>The version before the newest kept for mods with a script, rolled back to, deleted;
    /// and a newest version skipped (the user, 2026-10-30).</summary>
    public class KeptVersions : Fixture
    {
        /// <summary>A zip of a mod: its manifest and one patch archive.</summary>
        string Zip(string name, string manifestName, byte[] patch)
        {
            var path = Path.Combine(Root, name + ".zip");
            using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(z.CreateEntry("manifest.json").Open())) w.Write("{\"Name\": \"" + manifestName + "\"}");
                using (var s = z.CreateEntry(A + ".patch_0").Open()) s.Write(patch, 0, patch.Length);
            }
            return path;
        }

        string Scripted(string version) =>
            Zip("scripted-" + version, "Scripted", LuaArchive((0x1234567890ABCDEF, "-- HD2-Addon: mods/test/scripted\nlocal v = '" + version + "'\n")));

        Mod The(string name) => Library.Load().Mods.Single(m => m.Name == name);

        [Fact]
        public void AScriptModKeepsTheVersionBeforeAndRollsBackBothWays()
        {
            Cli("keep-previous-on");
            Cli("add", Scripted("1"));
            var v1 = The("Scripted").Path;
            Assert.Contains("is kept", Cli("replace", "Scripted", Scripted("2")));
            var mod = The("Scripted");
            var v2 = mod.Path;
            Assert.NotEqual(v1, v2);
            Assert.Equal(v1, mod.Previous.Path);
            Assert.True(Directory.Exists(v1));
            // a version kept is not an old version left over
            Assert.Contains("no folders of old versions", Cli("leftovers"));
            Assert.Contains("Scripted", Cli("previous"));

            Cli("rollback", "Scripted");
            mod = The("Scripted");
            Assert.Equal(v1, mod.Path);
            Assert.Equal(v2, mod.Previous.Path);
            Cli("rollback", "Scripted");
            mod = The("Scripted");
            Assert.Equal(v2, mod.Path);
            Assert.Equal(v1, mod.Previous.Path);
        }

        [Fact]
        public void OneVersionIsKeptTheOneBeforeGoes()
        {
            Cli("keep-previous-on");
            Cli("add", Scripted("1"));
            var v1 = The("Scripted").Path;
            Cli("replace", "Scripted", Scripted("2"));
            var v2 = The("Scripted").Path;
            Cli("replace", "Scripted", Scripted("3"));
            var mod = The("Scripted");
            Assert.Equal(v2, mod.Previous.Path);
            Assert.False(Directory.Exists(v1));
        }

        /// <summary>Rolled back, then the same update again: the same archive unpacks into the same
        /// folder, which is the one kept; it must not be deleted as the one before.</summary>
        [Fact]
        public void TheSameUpdateAfterARollbackKeepsItsFolder()
        {
            Cli("keep-previous-on");
            Cli("add", Scripted("1"));
            var v1 = The("Scripted").Path;
            var two = Scripted("2");
            Cli("replace", "Scripted", two);
            var v2 = The("Scripted").Path;
            Cli("rollback", "Scripted");
            Cli("replace", "Scripted", two);
            var mod = The("Scripted");
            Assert.Equal(v2, mod.Path);
            Assert.True(Directory.Exists(v2));
            Assert.Equal(v1, mod.Previous.Path);
        }

        [Fact]
        public void ModsWithoutScriptsAndTheSettingOffKeepNothing()
        {
            Cli("keep-previous-on");
            Cli("add", Zip("plain-1", "Plain", Archive((0xAAAAu, 1))));
            var p1 = The("Plain").Path;
            Cli("replace", "Plain", Zip("plain-2", "Plain", Archive((0xAAAAu, 2))));
            Assert.Null(The("Plain").Previous);
            Assert.False(Directory.Exists(p1));

            Cli("keep-previous-off");
            Cli("add", Scripted("1"));
            var v1 = The("Scripted").Path;
            Cli("replace", "Scripted", Scripted("2"));
            Assert.Null(The("Scripted").Previous);
            Assert.False(Directory.Exists(v1));
        }

        [Fact]
        public void AKeptVersionCanBeDeleted()
        {
            Cli("keep-previous-on");
            Cli("add", Scripted("1"));
            var v1 = The("Scripted").Path;
            Cli("replace", "Scripted", Scripted("2"));
            var v2 = The("Scripted").Path;
            Assert.Contains("deleted the earlier version of Scripted", Cli("previous", "--delete", "Scripted"));
            var mod = The("Scripted");
            Assert.Null(mod.Previous);
            Assert.Equal(v2, mod.Path);
            Assert.False(Directory.Exists(v1));
            Assert.Equal("no_previous", Refusal("rollback", "Scripted"));
        }

        /// <summary>A mod deleted for good takes the version kept with it.</summary>
        [Fact]
        public void DeletingAModDeletesItsKeptVersion()
        {
            Cli("keep-previous-on");
            Cli("add", Scripted("1"));
            var v1 = The("Scripted").Path;
            Cli("replace", "Scripted", Scripted("2"));
            Cli("delete", "Scripted", "--yes");
            Assert.False(Directory.Exists(v1));
        }

        [Fact]
        public void ASkippedVersionIsNotOfferedANewerOneIs()
        {
            var m = new Mod { Name = "Cape" };
            UpdateInfo Check(string latest, long file = 0) =>
                Versions.Skipped(m, new UpdateInfo { Status = "update", Channel = file > 0 ? "Nexus Mods" : "GitHub", Latest = latest, LatestFile = file });
            Versions.Skip(m, new UpdateInfo { Status = "update", Channel = "GitHub", Latest = "v2.0" });
            Assert.Equal("skipped", Check("v2.0").Status);
            Assert.Equal("update", Check("v3.0").Status);
            Versions.Skip(m, new UpdateInfo { Status = "update", Channel = "Nexus Mods", Latest = "2.0", LatestFile = 7 });
            Assert.Equal("skipped", Check("2.0", 7).Status);
            Assert.Equal("update", Check("2.0", 8).Status);
        }

        [Fact]
        public void TheKeptVersionAndTheSkipAreSaved()
        {
            Cli("keep-previous-on");
            Cli("add", Scripted("1"));
            Cli("replace", "Scripted", Scripted("2"));
            var lib = Library.Load();
            lib.Mods[0].Skip = "github:v9";
            lib.Save();
            var mod = The("Scripted");
            Assert.Equal("github:v9", mod.Skip);
            Assert.NotNull(mod.Previous);
            Assert.True(mod.Previous.Kept > 0);
        }

        string Refusal(params string[] args)
        {
            Commands.Run(args, new StringWriter(), new StringWriter(), out var refused);
            return refused?.Kind;
        }
    }
}
