using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    public class FileModDiagnostics : Fixture
    {
        const ulong PayloadId = 0xb5b0c0ffee5b5b11;
        const string Guard = "if rawget(_G, \"jit\") then jit.off() end";

        static List<string> Jit(string code) => LuaMods.Look(1, Encoding.UTF8.GetBytes(code)).Jit;

        [Fact]
        public void OptionalJitOffDoesNotRequireLuaJit()
        {
            Assert.Empty(Jit(Guard));
            Assert.Empty(Jit("if rawget ( _G, 'jit' ) then -- optional\n jit.off ( ) end"));
            Assert.Equal(new[] { "jit" }, Jit(Guard + "\njit.off()"));
            Assert.Equal(new[] { "jit" }, Jit("if rawget(_G, 'jit') then x = 1 else jit.off() end"));
            Assert.Equal(new[] { "jit" }, Jit("rawget(_G, 'jit')\njit.off()"));
            Assert.Equal(new[] { "jit" }, Jit("if rawget(other, 'jit') then jit.off() end"));
        }

        static byte[] Payload(string target = "data/game/generated_entities.dl_bin", int size = 4)
        {
            var header = Encoding.UTF8.GetBytes("name=Test file mod\nversion=1.0\ntarget=" + target + "\nsize=" + size + "\n");
            return Encoding.ASCII.GetBytes("HD2FM1").Concat(BitConverter.GetBytes((uint)header.Length))
                .Concat(header).Concat(new byte[] { 0, 255, 4, 0 }).ToArray();
        }

        static byte[] PayloadArchive(byte[] payload, ulong id = PayloadId)
        {
            var resource = BitConverter.GetBytes((uint)payload.Length).Concat(BitConverter.GetBytes(2u)).Concat(payload).ToArray();
            var archive = Archive(resource, (Deploy.LuaType, id));
            BitConverter.GetBytes(Deploy.LuaType).CopyTo(archive, 0x48 + 8);
            return archive;
        }

        [Fact]
        public void FileModBinaryDataIsNotCompiledAsLuaButStillDeploys()
        {
            var folder = Mod("File mod", new Dictionary<string, byte[]>
            {
                [A + ".patch_0"] = LuaArchive((LuaMods.BootId, Guard)),
                [B + ".patch_0"] = PayloadArchive(Payload()),
            });
            Assert.Empty(LuaMods.Read(Path.Combine(folder, B + ".patch_0")));
            Cli("add", folder);
            var lib = Library.Load();
            var lua = LuaMods.Of(lib);
            Assert.Single(lua.Single().Scripts);
            Assert.Empty(lua.Single().Broken);
            Assert.DoesNotContain(LuaMods.Check(lib, lua).Issues, issue => issue.Kind == "cannot_run");
            Assert.Equal(6, Deploy.Plan(lib).Count); // both original archives and their companions remain
            var before = File.ReadAllBytes(Path.Combine(folder, B + ".patch_0"));
            Cli("deploy", "--verify");
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(Data, B + ".patch_0")));
        }

        [Fact]
        public void MalformedOrExecutableResourcesAreNotHiddenAsPayloads()
        {
            var invalid = new[]
            {
                Encoding.ASCII.GetBytes("HD2FM1"),
                Encoding.ASCII.GetBytes("HD2FM1").Concat(BitConverter.GetBytes(uint.MaxValue)).ToArray(),
                Payload(size: 200),
                Payload(target: "../game/generated_entities.dl_bin"),
                Payload(target: "data/game/../generated_entities.dl_bin"),
                Encoding.UTF8.GetBytes("local x = = 1"),
            };
            for (int i = 0; i < invalid.Length; i++)
            {
                var folder = Mod("Invalid " + i, new Dictionary<string, byte[]> { [B + ".patch_0"] = PayloadArchive(invalid[i]) });
                Assert.NotNull(LuaMods.Read(Path.Combine(folder, B + ".patch_0")).Single().Error);
            }
            var boot = Mod("Invalid boot", new Dictionary<string, byte[]> { [A + ".patch_0"] = PayloadArchive(Payload(), LuaMods.BootId) });
            Assert.NotNull(LuaMods.Read(Path.Combine(boot, A + ".patch_0")).Single().Error);
        }

        [Fact]
        public void CoordinatedBootsAreDistinguishedFromRealResourceConflicts()
        {
            Cli("add", Mod("Installer", new Dictionary<string, byte[]>
            {
                [A + ".patch_0"] = LuaArchive((LuaMods.BootId, Guard)),
                [B + ".patch_0"] = Archive(Bytes("installer"), (12ul, 42ul)),
            }));
            Cli("add", Mod("Chef", new Dictionary<string, byte[]>
            {
                [A + ".patch_0"] = LuaArchive(
                    (LuaMods.BootId, "-- Chef Shared Loader start-up stub --@@LOADER@@\nlocal x = 1\n"),
                    (LuaMods.ChefCoreId, "-- Chef Shared Loader\nlocal VERSION = '1.2.3'\n")),
                [B + ".patch_0"] = Archive(Bytes("chef"), (12ul, 42ul)),
            }));
            var state = StateDoc.Build(Library.Load(), Game);
            var clashes = ((List<object>)state["conflicts"]).OfType<Dictionary<string, object>>().ToList();
            Assert.Equal(2, clashes.Count);
            Assert.Single(clashes, c => Json.Bool(c, "managed_boot"));
            Assert.Single(clashes, c => !Json.Bool(c, "same") && !Json.Bool(c, "managed_boot"));

            Cli("add", Mod("Old BML", new Dictionary<string, byte[]>
            {
                [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- Boot Mod Loader\nlocal MARKER = 'boot-mod-loader-0.11.0'\n")),
            }));
            Cli("loader", "Old BML");
            state = StateDoc.Build(Library.Load(), Game);
            clashes = ((List<object>)state["conflicts"]).OfType<Dictionary<string, object>>().ToList();
            Assert.NotEmpty(clashes);
            Assert.DoesNotContain(clashes, c => Json.Bool(c, "managed_boot"));
        }
    }
}
