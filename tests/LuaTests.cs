using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>The Lua checks before a deploy (the user, 2026-10-26): the syntax of the game's
    /// Lua 5.1, the LuaJIT libraries, whose boot runs, which loader that is, what each mod needs.
    /// Every rule has a script that must pass and one that must not.</summary>
    public class LuaChecks : Fixture
    {
        static bool Compiles(string src) => LuaSyntax.Check(src) == null;

        [Fact]
        public void TheSyntaxIsLua51s()
        {
            // LuaJIT and Lua 5.2+ syntax: refused; the same thing written for 5.1: compiles
            Assert.False(Compiles("goto done ::done::"));
            Assert.True(Compiles("do local done end"));
            Assert.False(Compiles("local x = 0xf6978e86e4f4b0d5ull"));
            Assert.True(Compiles("local x = 0xf6978e86"));
            Assert.False(Compiles("x = a // b"));
            Assert.True(Compiles("x = math.floor(a / b)"));
            Assert.False(Compiles("x = a & b"));
            Assert.False(Compiles("x = 1..2"));
            Assert.True(Compiles("x = 1 .. 2"));
            // a call split from its function by a line break; in a table constructor 5.1 lets it pass
            Assert.False(Compiles("local f = g\n(h)()"));
            Assert.True(Compiles("local f = g(h)()"));
            Assert.True(Compiles("t = { f\n(x) }"));
            // [[ inside [[ ]]: refused in 5.1; with a level, or [==[ ]==] around it, fine
            Assert.False(Compiles("s = [[ [[x]] ]]"));
            Assert.True(Compiles("s = [=[ [[x]] ]=]"));
            Assert.False(Compiles("--[[ [[ ]] ]] x = 1"));
            // break and return end a block
            Assert.False(Compiles("break"));
            Assert.True(Compiles("while true do break end"));
            Assert.False(Compiles("x = 1; return; y = 2"));
            Assert.True(Compiles("do return end y = 2"));
            Assert.False(Compiles("function f() return ... end"));
            Assert.True(Compiles("function f(...) return ... end"));
            Assert.False(Compiles("f() = 1"));
            Assert.True(Compiles("f().x = 1"));
            // escapes: \z is just z in 5.1; above \255 is refused
            Assert.True(Compiles("x = '\\z'"));
            Assert.False(Compiles("x = '\\300'"));
            Assert.True(Compiles("x = '\\255'"));
            // a UTF-8 BOM is not skipped by loadstring; UTF-8 in strings and comments is fine
            Assert.False(LuaSyntax.Check(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes("x = 1")).ToArray()) == null);
            Assert.True(Compiles("x = '中文' -- 中文"));
            Assert.False(Compiles("中文 = 1"));
            // the line is the line PUC 5.1 names
            Assert.Equal(3, LuaSyntax.Check("x = 1\ny = 2\nz = = 3").Value.Line);
            Assert.Contains("'end' expected (to close 'if' at line 1)", LuaSyntax.Check("if x then\ny = 1\n").Value.Message);
        }

        /// <summary>What a script that does not compile uses, in words a player can follow (the
        /// user, 2026-10-26: say what it uses, not only that it fails).</summary>
        [Fact]
        public void ACompileErrorSaysWhatTheScriptUses()
        {
            string Kind(string src) => LuaMods.Look(1, Encoding.UTF8.GetBytes(src)).ErrorKind;
            string KindBytes(byte[] src) => LuaMods.Look(1, src).ErrorKind;
            Assert.Equal("luajit_number", Kind("local x = 0xf6978e86e4f4b0d5ull"));
            Assert.Equal("luajit_number", Kind("local x = 4294967296ULL"));
            Assert.Equal("goto", Kind("for i = 1, 2 do\ngoto continue\n::continue::\nend"));
            Assert.Equal("new_operator", Kind("x = a // b"));
            Assert.Equal("new_operator", Kind("x = a & b"));
            Assert.Equal("new_operator", Kind("x = a << 2"));
            Assert.Equal("bom", KindBytes(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes("x = 1")).ToArray()));
            Assert.Equal("locals", Kind("local " + string.Join(", ", Enumerable.Range(0, 201).Select(i => "v" + i))));
            // an ordinary mistake stays a syntax error with PUC's own words; a comment or a string
            // that mentions goto does not make it one
            Assert.Equal("syntax", Kind("x = = 1 -- goto x"));
            Assert.Equal("syntax", Kind("x = '//' y"));
            Assert.Null(Kind("x = 1"));
        }

        /// <summary>Chef Shared Loader hands its interface to a mod as the script's argument; under any
        /// other loader the first chef.x fails (NarrowHUD 1.0.5 under Boot Mod Loader, 2026-10-26).</summary>
        [Fact]
        public void ChefsOwnInterfaceUsedWithoutATestNeedsChef()
        {
            bool Api(string src) => LuaMods.UsesChefApi(LuaSyntax.Tokens(Encoding.UTF8.GetBytes(src)));
            Assert.True(Api("local chef = ...\nchef.log('x')"));
            Assert.True(Api("local chef = ...\nif type(chef.own_log) == 'function' then chef.own_log('n') end"));   // NarrowHUD's line 647
            Assert.True(Api("local chef = ...\nlocal id = chef.call and chef.call('x')"));                            // a field tested, not chef
            Assert.False(Api("local chef = ...\nif chef then chef.log('x') end"));                                     // a fallback
            Assert.False(Api("local chef = ...\nif type(chef) == 'table' then chef.log('x') end"));
            Assert.False(Api("local chef = ...\nlocal log = chef and chef.log or print"));
            Assert.False(Api("local chef = require('chef')\nchef.log('x')"));                                         // not the argument
            Assert.False(Api("local t = ...\nt.chef.log('x')"));                                                        // somebody's field
            Assert.False(Api("-- local chef = ...\nx = 'chef.log'"));                                                  // comments and strings
            // a chef of a function's or block's own is another variable: its tests do not count
            // (HUDdle 0.4.9's modules, 2026-10-27)
            Assert.True(Api("local chef = ...\nlocal M = (function()\n  local chef, O = nil, {}\n  if chef then end\n  chef = 1\nend)()\nchef.own_log('x')"));
            Assert.True(Api("local chef = ...\nlocal f = function(chef, x) if chef then end end\nchef.time()"));
            Assert.True(Api("local chef = ...\ndo local chef = 2; if chef then end end\nchef.time()"));
            // the script's chef tested inside a function of its own: still a fallback
            Assert.False(Api("local chef = ...\nlocal function f() if chef then end end\nchef.time()"));
            // a function's ... is its own arguments, not the script's
            Assert.False(Api("local function f(...) local chef = ... chef.time() end"));
        }

        /// <summary>The settings window's lists a script writes, as their names appear in it; a name
        /// made at run time, a field of another table, a comment or a path are not taken.</summary>
        [Fact]
        public void SettingsListsAreFoundByTheirNames()
        {
            List<string> Names(string src, string header = null) =>
                LuaMods.SettingsNames(LuaSyntax.Tokens(Encoding.UTF8.GetBytes(src)), header);
            Assert.Equal(new[] { "ammo_lamp" }, Names("local f = io.open(dir .. 'ammo_lamp_options.json', 'wb')"));
            Assert.Empty(Names("local path = dir .. c.name .. '_options.json'"));                      // the shared library's own line
            Assert.Empty(Names("-- writes ammo_lamp_options.json\nx = 1"));                               // a comment
            Assert.Empty(Names("x = 'C:/game/ammo_lamp_options.json'"));                                   // a path, not a name
            // the shared library's ModSettings.new: its name, not a name inside its options
            Assert.Equal(new[] { "ammo_lamp" }, Names("local ok, s = pcall(ModSettings.new, {\n  name = 'ammo_lamp', title = 'Ammo Lamp',\n  options = { { key = 'a', name = 'inner' } },\n})"));
            Assert.Equal(new[] { "stratagem_hotkeys" }, Names("S = ModSettings.new{ title = 'x', name = \"stratagem_hotkeys\" }"));
            Assert.Equal(new[] { "x" }, Names("S = ModSettings.new({ name = 'x' })"));
            Assert.Empty(Names("S = Other.new{ name = 'x' }"));
            Assert.Empty(Names("S = ModSettings.new{ name = prefix .. 'x' }"));                           // made at run time
            Assert.Empty(Names("S = ModSettings.new{ name = 'x' .. suffix }"));
            // a variable set to one string (BFV Kill Feedback: M.SW_NAME = "KillFeedback")
            Assert.Equal(new[] { "KillFeedback" }, Names("M.SW_NAME = \"KillFeedback\"\nlocal S = pcall(ModSettings.new, { name = M.SW_NAME })"));
            Assert.Equal(new[] { "kf" }, Names("local NAME = 'kf'\nS = ModSettings.new{ name = NAME }"));
            Assert.Empty(Names("M.N = 'a'\nif x then M.N = 'b' end\nS = ModSettings.new{ name = M.N }"));          // two values: neither
            Assert.Empty(Names("M.N = 'a'\nM.N = read()\nS = ModSettings.new{ name = M.N }"));                     // set at run time too
            Assert.Empty(Names("M.N = 'a'\nS = ModSettings.new{ name = X.M.N }"));                                // another variable
            Assert.Empty(Names("M.N = 'a' .. v\nS = ModSettings.new{ name = M.N }"));
            // the old menus: the lists Boot Mod Loader writes for the mod
            Assert.Equal(new[] { "mom_action_cam", "mbm_action_cam" },
                         Names("ModOptionsMenu.register_option({ key = 'a' })", "mods/author/Action_Cam"));
            Assert.Equal(new[] { "mom_actioncam", "mbm_actioncam" },
                         Names("ModOptionsMenu.register_option({ mod_id = 'ActionCam', key = 'a' })", "mods/author/Action_Cam"));
            Assert.Empty(Names("x = 1", "mods/author/Action_Cam"));                                       // no menus: no list
            // kept with the script's other facts
            Assert.Equal(new[] { "ammo_lamp" }, LuaMods.Look(1, Encoding.UTF8.GetBytes("x = 'ammo_lamp_options.json'")).Settings);
        }

        [Fact]
        public void TheParsersLimitsAreLua51s()
        {
            string Locals(int n) => "local " + string.Join(", ", Enumerable.Range(0, n).Select(i => "v" + i));
            Assert.True(Compiles(Locals(200)));
            Assert.Contains("main function has more than 200 local variables", LuaSyntax.Check(Locals(201)).Value.Message);
            // a for loop's three hidden variables count too
            Assert.True(Compiles(Locals(196) + "\nfor i = 1, 2 do end"));
            Assert.False(Compiles(Locals(197) + "\nfor i = 1, 2 do end"));
            string Upvalues(int n) => Locals(n) + "\nfunction f() return " + string.Join(" + ", Enumerable.Range(0, n).Select(i => "v" + i)) + " end";
            Assert.True(Compiles(Upvalues(60)));
            Assert.Contains("has more than 60 upvalues", LuaSyntax.Check(Upvalues(61)).Value.Message);
            // the same upvalue twice is one
            Assert.True(Compiles(Locals(60) + "\nfunction f() return v0 + v0 + v1 end"));
        }

        static List<string> Jit(string src) => LuaMods.JitUses(LuaSyntax.Tokens(Encoding.UTF8.GetBytes(src)));

        [Fact]
        public void LuaJitLibrariesAreFoundOnlyWhereTheyWouldFail()
        {
            Assert.Equal(new[] { "ffi" }, Jit("local ffi = require('ffi')"));
            Assert.Equal(new[] { "ffi" }, Jit("local ffi = require 'ffi'"));
            Assert.Empty(Jit("local ok, ffi = pcall(require, 'ffi')"));                 // it copes without
            Assert.Empty(Jit("local ok, ffi = pcall(function() return require('ffi') end)"));
            Assert.Equal(new[] { "bit" }, Jit("x = bit.band(a, b)"));
            Assert.Empty(Jit("local bit = { band = function() end }\nx = bit.band(a, b)"));  // its own
            Assert.Empty(Jit("if jit then jit.off() end"));                                 // tested first
            Assert.Empty(Jit("local band = bit and bit.band or function() end"));
            Assert.Empty(Jit("-- require('ffi')\nx = 'ffi.cdef'"));                          // comments and strings
            Assert.Empty(Jit("x = t.bit.band(1)"));                                          // somebody's field
            Assert.Empty(Jit("function f(bit) return bit.band(1) end"));                      // a parameter
        }

        /// <summary>MDL3 3.0.0-dev.18's boot (2026-10-30) requires bit only inside a function its
        /// FFI path hands ffi: that is no use the game's Lua meets. The same require anywhere
        /// else still counts, and so does a parameter's library outside its function.</summary>
        [Fact]
        public void LibrariesInsideAFunctionHandedThemDoNotCount()
        {
            const string slots = "function M.resolve_slots(ffi, platform)\n  local bit = package.loaded.bit or require('bit')\n  return bit.band(1, 2)\nend\n";
            Assert.Empty(Jit(slots));
            Assert.Empty(Jit("local function f(jit, x) local ffi = require('ffi') return ffi.cast('int', x) end"));
            Assert.Empty(Jit("function a.b:c(bit) return bit.bxor(1, 2) end"));
            Assert.Empty(Jit("local f = function(ffi) return function() return require('bit') end end"));   // nested
            Assert.Empty(Jit("function f(ffi) if x then do local bit = require('bit') end end end"));     // blocks
            // must fail: the same outside it, after it, or a library it was not handed
            Assert.Equal(new[] { "bit" }, Jit("local bit = package.loaded.bit or require('bit')\nreturn bit.band(1, 2)"));
            Assert.Equal(new[] { "bit" }, Jit(slots + "local bit = require('bit')"));
            Assert.Equal(new[] { "bit" }, Jit("function f(bit) end\nx = bit.band(1)"));
            Assert.Equal(new[] { "ffi" }, Jit("function f(bit) local ffi = require('ffi') end"));
            Assert.Equal(new[] { "ffi" }, Jit("function f(x) if x then repeat until x end end\nlocal ffi = require('ffi')"));
            Assert.Equal(new[] { "ffi" }, Jit("call(function(ffi) end, require('ffi'))"));
        }

        const ulong A1 = 0x1000000000000001, A2 = 0x1000000000000002, A3 = 0x1000000000000003;

        string Bml(string version = "0.11.0") => Mod("Boot Mod Loader", new Dictionary<string, byte[]>
        {
            [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- Boot Mod Loader: runs HD2-Addon mods\nlocal MARKER = \"boot-mod-loader-" + version + "\"\n")),
        });

        string Chef() => Mod("Chef Shared Loader", new Dictionary<string, byte[]>
        {
            [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- Chef Shared Loader start-up stub, version 1 --@@LOADER@@\nlocal x = 1\n"),
                                          (LuaMods.ChefCoreId, "-- Chef Shared Loader - its core\nlocal VERSION = '1.2.0'\n")),
        });

        string Addon(string folder, string name, string extra = "", ulong id = A1) => Mod(folder, new Dictionary<string, byte[]>
        {
            [A + ".patch_0"] = LuaArchive((id, "-- HD2-Addon: " + name + "\n" + extra + "local x = 1\n")),
        });

        static List<string> Kinds(LuaMods.Session s) => s.Mods.Select(m => m.State).ToList();

        (ModLua Owner, string Running, List<LuaIssue> Issues) Now()
        {
            var lib = Library.Load();
            var (owner, _, running, issues) = LuaMods.Check(lib, LuaMods.Of(lib));
            return (owner, running, issues);
        }

        [Fact]
        public void WhoseBootRunsAndWhatThatMeans()
        {
            Cli("add", Addon("Lamp", "mods/me/lamp"));
            // no loader: the mod does not start
            var (_, running, issues) = Now();
            Assert.Null(running);
            Assert.Equal("no_loader", issues.Single().Kind);

            Cli("add", Bml());
            (_, running, issues) = Now();
            Assert.Equal("bml", running);
            Assert.Empty(issues);

            // Chef added after it, no choice made: the last loader enabled is in effect, and only it
            // goes to the game, last (the user, 2026-10-26: one loader, chosen in a panel)
            Cli("add", Chef());
            var now = Now();
            Assert.Equal("Chef Shared Loader", now.Owner.Mod.Name);
            Assert.Equal("chef", now.Running);
            Assert.Empty(now.Issues);
            Assert.DoesNotContain(Deploy.Plan(Library.Load()), w => w.Mod == "Boot Mod Loader");

            // Boot Mod Loader chosen: it goes last wherever it stands, and Chef not at all
            Cli("loader", "Boot Mod Loader");
            Cli("move", "Boot Mod Loader", "--top");
            now = Now();
            Assert.Equal("bml", now.Running);
            var plan = Deploy.Plan(Library.Load());
            Assert.Equal("Boot Mod Loader", plan.Last().Mod);
            Assert.DoesNotContain(plan, w => w.Mod == "Chef Shared Loader");
            Assert.Empty(now.Issues);
            // the choice is for every profile: its own switch does not matter
            Cli("disable", "Boot Mod Loader");
            Assert.Equal("bml", Now().Running);

            // a mod only Chef Shared Loader runs, under Boot Mod Loader
            Cli("add", Addon("Radar", "mods/chef/radar", "-- HD2-Requires: chef-shared-loader\n", A2));
            Assert.Contains(Now().Issues, i => i.Kind == "needs_chef" && i.Mods.Single().Name == "Radar");

            // none: no loader goes to the game
            Cli("loader", "none");
            Assert.Null(Now().Running);
            Assert.DoesNotContain(Deploy.Plan(Library.Load()), w => w.Mod == "Boot Mod Loader" || w.Mod == "Chef Shared Loader");
            Assert.Contains(Now().Issues, i => i.Kind == "no_loader");
            Assert.Throws<ModError>(() => Loaders.Choose(Library.Load(), "Radar"));      // no loader

            // a boot nobody knows: the loader's goes last and wins; that one does not run
            Cli("loader", "Boot Mod Loader");
            Cli("add", Mod("Installer", new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- swaps files\nlocal y = 2\n")) }));
            now = Now();
            Assert.Equal("bml", now.Running);
            Assert.Contains(now.Issues, i => i.Kind == "boot_conflict" && i.Mods[0].Name == "Boot Mod Loader" && i.Mods[1].Name == "Installer");
        }

        /// <summary>MDL3 3.0.0-dev.18 (2026-10-30) replaces the boot and also names
        /// CowboyBingusModLoader and Bingus's boot, for the mods made for that loader: it was taken
        /// for Bingus Shared Loader. Its first line says whose it is.</summary>
        [Fact]
        public void Mdl3IsALoaderOfItsOwn()
        {
            const string bingus = "local loader = CowboyBingusModLoader\nlocal R_BOOT = '9ba626afa44a3aa3'\n";
            string Boot(string folder, string text) => Mod(folder, new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, text)) });
            Cli("add", Addon("Lamp", "mods/me/lamp"));
            Cli("add", Boot("MDL3", "-- MDL3 3.0.0-dev.18 game boot (PUC Lua 5.1). Signed native host 0.4.1,\n-- more\n" + bingus));
            Assert.Equal("mdl3", Loaders.KindOf(Library.Load().Mods.Single(m => m.Name == "MDL3")));
            var now = Now();
            Assert.Equal("mdl3", now.Running);
            Assert.Empty(now.Issues);                                    // it runs the Addon mods
            var lua = Cli("lua");
            Assert.Contains("start-up script (boot): MDL3 (MDL3 3.0.0-dev.18)", lua);
            Assert.DoesNotContain("Bingus", lua);
            // must fail: not on the first line, the same boot is not MDL3's
            Cli("add", Boot("Other", "-- a boot\n-- MDL3 3.0.0-dev.18 game boot (PUC Lua 5.1)\n" + bingus));
            Assert.Equal("bsl", Loaders.KindOf(Library.Load().Mods.Single(m => m.Name == "Other")));
        }

        /// <summary>Universal Mod Loader replaces the boot and sets CowboyBingusModLoader with Bingus's
        /// boot named, to give Bingus's interface: it was taken for Bingus Shared Loader (issues 12, 23).
        /// Every version's boot opens with its own name (v1.0.1, v2.0.0-v2.0.3).</summary>
        [Fact]
        public void UmlIsALoaderOfItsOwn()
        {
            const string bingus = "rawset(_G, \"CowboyBingusModLoader\", loader_state)\n-- Replaces engine boot resource 0xF476DF93691895FA in bundle 9ba626afa44a3aa3\n";
            string Boot(string folder, string text) => Mod(folder, new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, text)) });
            Cli("add", Addon("Lamp", "mods/me/lamp"));
            Cli("add", Boot("UML", "-- Universal Mod Loader v2.0.3 for Helldivers 2 (Steam Builds 25988413 / 26088801 / 26251122+ / Lua 5.1)\n" + bingus));
            Assert.Equal("uml", Loaders.KindOf(Library.Load().Mods.Single(m => m.Name == "UML")));
            var now = Now();
            Assert.Equal("uml", now.Running);
            Assert.Empty(now.Issues);                                    // it runs the Addon mods
            var lua = Cli("lua");
            Assert.Contains("Universal Mod Loader 2.0.3", lua);
            Assert.DoesNotContain("Bingus", lua);
            // the mods made for Chef from 2.0.1 (its boot reads "-- HD2-Boot:" from then on)
            Assert.DoesNotContain(LoaderCaps.ChefMods, LoaderCaps.Of("uml", "2.0.0", null));
            Assert.Contains(LoaderCaps.ChefMods, LoaderCaps.Of("uml", "2.0.1", null));
            Assert.Equal(new[] { LoaderCaps.Addons, LoaderCaps.Bingus }, LoaderCaps.Of("uml", "1.0.1", null));
            // must fail: not on the first line, the same boot is not UML's
            Cli("add", Boot("Other", "-- a boot\n-- Universal Mod Loader v2.0.3 for Helldivers 2\n" + bingus.Replace("rawset(_G, \"CowboyBingusModLoader\", loader_state)", "local loader = CowboyBingusModLoader")));
            Assert.Equal("bsl", Loaders.KindOf(Library.Load().Mods.Single(m => m.Name == "Other")));
        }

        /// <summary>A state read keeps each mod's loader answers for that read only (Loaders.Pass:
        /// asked again for every choice of loader, they were 1.3 s of a 2.4 s read, the user,
        /// 2026-11-03). Must live: the read keeps what it saw. Must die: the next read sees a change.</summary>
        [Fact]
        public void LoaderAnswersLastOneStateRead()
        {
            Cli("add", Addon("Lamp", "mods/me/lamp"));
            var lamp = Library.Load().Mods.Single(m => m.Name == "Lamp");
            var archive = Directory.GetFiles(lamp.Path, A + ".patch_0", SearchOption.AllDirectories).Single();
            using (Loaders.Pass())
            {
                Assert.Null(Loaders.KindOf(lamp));
                File.WriteAllBytes(archive, LuaArchive((LuaMods.BootId, "-- MDL3 3.0.0-dev.18 game boot (PUC Lua 5.1)\nlocal x = 1\n")));
                Assert.Null(Loaders.KindOf(lamp));
            }
            Assert.Equal("mdl3", Loaders.KindOf(lamp));
        }

        /// <summary>The user (2026-10-26, "先试试协调方案"): Boot Mod Loader in effect, Chef Shared Loader
        /// helping with the mods made for it only: Chef goes to the game too, just before the loader,
        /// and is no second loader and no boot in the way; only while such a mod is on, and only with a
        /// Boot Mod Loader that hands off (0.13.0 or later).</summary>
        [Fact]
        public void ChefHelpsBootModLoaderWithTheModsMadeForIt()
        {
            Cli("add", Bml("0.13.0"));
            Cli("add", Chef());
            Cli("add", Addon("Lamp", "mods/me/lamp"));
            Cli("add", Addon("Narrow", "mods/x/narrow", "local chef = ...\nchef.log('x')\n", A2));
            Cli("loader", "Boot Mod Loader");
            var now = Now();
            Assert.Equal("bml", now.Running);
            Assert.Empty(now.Issues);                                         // no second loader, no boot conflict, nothing unstarted
            var lib = Library.Load();
            Assert.Equal("Chef Shared Loader", Loaders.Helper(lib).Name);
            var mods = Deploy.Plan(lib).Select(w => w.Mod).Distinct().ToList();
            Assert.Equal(new[] { "Lamp", "Narrow", "Chef Shared Loader", "Boot Mod Loader" }, mods.ToArray());
            Assert.Matches("\"loader_helpers\": \\[\\s*\"" + lib.Mods.Single(m => m.Name == "Chef Shared Loader").Id + "\"\\s*\\]", Cli("state"));

            // no mod made for Chef on: Chef stays out
            Cli("disable", "Narrow");
            Assert.Null(Loaders.Helper(Library.Load()));
            Assert.DoesNotContain(Deploy.Plan(Library.Load()), w => w.Mod == "Chef Shared Loader");

            // a Boot Mod Loader that does not hand off: no helper, and the mod is said not to start
            Cli("enable", "Narrow");
            Cli("remove", "Boot Mod Loader");
            Cli("add", Bml("0.12.1"));
            Cli("loader", "Boot Mod Loader");
            Assert.Null(Loaders.Helper(Library.Load()));
            Assert.Contains(Now().Issues, i => i.Kind == "needs_chef" && i.Mods.Single().Name == "Narrow");
        }

        string Mdl3(string version = "3.0.0-dev.27") => Mod("MDL3", new Dictionary<string, byte[]>
        {
            [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- MDL3 " + version + " game boot (PUC Lua 5.1). Signed native host\nlocal x = 1\n")),
        });

        /// <summary>MDL3 3.0.0-dev.22 and later run the mods made for Chef Shared Loader themselves,
        /// without what they draw for now, and the start-up scripts deployed before them (their notes,
        /// read 2026-11-01; the user: "MDL dev27作者说已经做了CSL的兼容"): under such an MDL3 a mod made
        /// for Chef starts and a boot of its own is in no conflict. Under dev.21 neither.</summary>
        [Fact]
        public void Mdl3From22RunsTheModsMadeForChef()
        {
            bool Takes(string v) => LoaderCaps.Has("mdl3", v, null, LoaderCaps.ChefMods);
            Assert.False(Takes("3.0.0-dev.21"));
            Assert.True(Takes("3.0.0-dev.22"));
            Assert.True(Takes("3.0.0-dev.27"));
            Assert.True(Takes("3.0.0"));
            Assert.True(Takes("3.1.0-dev.1"));
            Assert.False(Takes("2.2.1"));
            Assert.False(Takes(null));

            Cli("add", Addon("Narrow", "mods/x/narrow", "local chef = ...\nchef.log('x')\n", A2));
            Cli("add", Mod("Installer", new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- swaps files\nlocal y = 2\n")) }));
            Cli("add", Mdl3("3.0.0-dev.27"));
            Cli("loader", "MDL3");
            var now = Now();
            Assert.Equal("mdl3", now.Running);
            Assert.Empty(now.Issues);                                         // no needs_chef, no boot conflict
            var all = LuaMods.Of(Library.Load(), false, out _);
            LuaMods.Check(Library.Load(), all);
            Assert.True(all.Single(x => x.Mod.Name == "Narrow").Mdl3Chef);
            Assert.True(all.Single(x => x.Mod.Name == "Installer").Mdl3Runs);
            Assert.Contains("run by MDL3 in Chef Shared Loader's place", Cli("lua"));

            // must fail: an MDL3 before dev.22 runs neither
            Cli("remove", "MDL3");
            Cli("add", Mdl3("3.0.0-dev.21"));
            Cli("loader", "MDL3");
            now = Now();
            Assert.Contains(now.Issues, i => i.Kind == "needs_chef" && i.Mods.Single().Name == "Narrow");
            Assert.Contains(now.Issues, i => i.Kind == "boot_conflict");
        }

        /// <summary>The user (2026-11-01, after a player's video of Quick Deploy 0.12.0 running Boot Mod
        /// Loader, Chef Shared Loader and MDL3 together: "这个是特性，因为可以让依赖不同loader的MOD同时使用"):
        /// with Boot Mod Loader 0.12.0 or later in effect and a mod on that reads MDL3 or HD2Data, MDL3
        /// goes to the game too, before the loader, its boot off the start-up slot for Boot Mod Loader to
        /// run as a guest; no second loader and no boot in the way. Not without such a mod, not with an
        /// older Boot Mod Loader, and MDL3 in effect keeps its boot.</summary>
        [Fact]
        public void Mdl3GoesAlongAsBootModLoadersGuest()
        {
            Cli("add", Bml("0.12.0"));
            Cli("add", Mdl3());
            Cli("add", Addon("Lamp", "mods/me/lamp"));
            Cli("add", Addon("Hud", "mods/me/hud", "local m = rawget(_G, 'MDL3')\n", A2));
            Cli("loader", "Boot Mod Loader");
            var now = Now();
            Assert.Equal("bml", now.Running);
            Assert.Equal("Boot Mod Loader", now.Owner.Mod.Name);
            Assert.Empty(now.Issues);                                         // no second loader, no boot conflict
            var lib = Library.Load();
            Assert.Equal(new[] { "MDL3" }, Loaders.Helpers(lib).Select(m => m.Name).ToArray());
            var plan = Deploy.Plan(lib);
            Assert.Equal(new[] { "Lamp", "Hud", "MDL3", "Boot Mod Loader" }, plan.Select(w => w.Mod).Distinct().ToArray());
            var m3 = plan.Single(w => w.Mod == "MDL3" && Deploy.Patch.IsMatch(w.Name));
            Assert.Contains("guest_boots", m3.Src);
            Assert.DoesNotContain(Deploy.Rows(m3.Src), r => r.Id == LuaMods.BootId);
            Assert.Contains(Deploy.Rows(m3.Src), r => r.Id == GuestBoots.IdOf("MDL3"));
            Assert.DoesNotContain(plan, w => w.Mod == "Boot Mod Loader" && w.Src.Contains("guest_boots"));
            Assert.Contains("\"uses_mdl3\": true", Cli("state"));
            Assert.Contains("uses MDL3 (in the library: MDL3 3.0.0-dev.27 (helping))", Cli("lua"));

            // HD2Data, the Data Hub MDL3 ships, by its global name, counts as well
            Cli("disable", "Hud");
            Cli("add", Addon("Board", "mods/me/board", "local p = HD2Data.players()\n", A3));
            Assert.Equal(new[] { "MDL3" }, Loaders.Helpers(Library.Load()).Select(m => m.Name).ToArray());

            // must fail: a field of something else, or the name in any other string, is no use of MDL3
            Cli("disable", "Board");
            Cli("add", Addon("Other", "mods/me/other", "local t = {}\nt.MDL3 = 1\nprint('MDL3')\nlocal s = t.HD2Data\n", A3));
            Assert.False(Loaders.UsesMdl3(Library.Load().Mods.Single(m => m.Name == "Other")));
            Assert.Empty(Loaders.Helpers(Library.Load()));
            Assert.DoesNotContain(Deploy.Plan(Library.Load()), w => w.Mod == "MDL3");

            // a Boot Mod Loader that runs no guests: MDL3 stays out
            Cli("enable", "Hud");
            Cli("remove", "Boot Mod Loader");
            Cli("add", Bml("0.11.0"));
            Cli("loader", "Boot Mod Loader");
            Assert.Empty(Loaders.Helpers(Library.Load()));
            Assert.DoesNotContain(Deploy.Plan(Library.Load()), w => w.Mod == "MDL3");

            // MDL3 in effect: last, its own boot on the slot
            Cli("loader", "MDL3");
            Assert.Equal("mdl3", Now().Running);
            var own = Deploy.Plan(Library.Load());
            Assert.Equal("MDL3", own.Last().Mod);
            Assert.All(own, w => Assert.DoesNotContain("guest_boots", w.Src));
        }

        /// <summary>What each loader can do is one table (LoaderCaps, the user 2026-11-01): a capability
        /// comes from a version on, or from the feature's marker in the loader's code. A Boot Mod Loader
        /// that names an old version but carries both markers hosts guests and hands off to Chef; the
        /// same without them does neither. A Chef core before 1.2.0 without the marker takes no hand-off.</summary>
        [Fact]
        public void A_loaders_capabilities_come_from_its_version_or_its_code()
        {
            string Marked(string version, string extra) => Mod("Boot Mod Loader", new Dictionary<string, byte[]>
            {
                [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- Boot Mod Loader: runs HD2-Addon mods\nlocal MARKER = \"boot-mod-loader-" + version + "\"\n" + extra)),
            });
            Cli("add", Marked("0.11.0", "local g = \"^%-%- HD2%-Guest%-Boot: \"\nrawset(_G, \"ChefSharedLoaderHandoff\", nil)\n"));
            Cli("add", Chef());
            Cli("add", Mdl3());
            Cli("add", Addon("Narrow", "mods/x/narrow", "local chef = ...\nchef.log('x')\n", A2));
            Cli("add", Addon("Hud", "mods/me/hud", "local m = rawget(_G, 'MDL3')\n", A3));
            Cli("loader", "Boot Mod Loader");
            var lib = Library.Load();
            var caps = Loaders.CapsOf(lib.Mods.Single(m => m.Name == "Boot Mod Loader"));
            Assert.Contains(LoaderCaps.HostsGuests, caps);
            Assert.Contains(LoaderCaps.HandsToChef, caps);
            Assert.Equal(new[] { "MDL3", "Chef Shared Loader" }, Loaders.Helpers(lib).Select(m => m.Name).ToArray());
            Assert.Empty(Now().Issues);

            // must fail: the same version without the markers does neither
            Cli("remove", "Boot Mod Loader");
            Cli("add", Marked("0.11.0", ""));
            Cli("loader", "Boot Mod Loader");
            Assert.Empty(Loaders.Helpers(Library.Load()));
            Assert.Contains(Now().Issues, i => i.Kind == "needs_chef");

            // a Chef core older than the hand-off, without its marker, is not brought along
            Assert.False(LoaderCaps.Has("chef", "1.1.0", null, LoaderCaps.TakesHandOff));
            Assert.True(LoaderCaps.Has("chef", "1.1.0", new[] { "ChefSharedLoaderHandoff" }, LoaderCaps.TakesHandOff));
        }

        /// <summary>The compatibility table's verdicts come from the same table as the deploy: two
        /// loaders go together exactly when the deploy brings the second along beside the first.</summary>
        [Fact]
        public void The_compatibility_verdicts_agree_with_the_deploy()
        {
            List<string> Of(string kind, string version) => LoaderCaps.Of(kind, version, null);
            var v = LoaderCaps.Pair("bml", Of("bml", "0.13.4"), "chef", Of("chef", "1.3.0"));
            Assert.Equal(("together", "bml", "chef", "hand_off"), (v.State, v.In, v.Beside, v.How));
            v = LoaderCaps.Pair("bml", Of("bml", "0.13.4"), "mdl3", Of("mdl3", "3.0.0-dev.27"));
            Assert.Equal(("together", "bml", "mdl3", "guest"), (v.State, v.In, v.Beside, v.How));
            v = LoaderCaps.Pair("chef", Of("chef", "1.3.0"), "mdl3", Of("mdl3", "3.0.0-dev.27"));
            Assert.Equal(("covered", "mdl3"), (v.State, v.Coverer));
            Assert.Equal("one", LoaderCaps.Pair("chef", Of("chef", "1.3.0"), "mdl3", Of("mdl3", "3.0.0-dev.21")).State);
            v = LoaderCaps.Pair("bml", Of("bml", "0.13.4"), "bsl", Of("bsl", null));
            Assert.Equal(("covered", (string)null), (v.State, v.Coverer));
            // together only with a later Boot Mod Loader: what it lacks is named
            v = LoaderCaps.Pair("bml", Of("bml", "0.12.1"), "chef", Of("chef", "1.3.0"));
            Assert.Equal("together", v.State);
            Assert.Equal(new[] { ("bml", LoaderCaps.HandsToChef) }, v.Short.ToArray());

            // the deploy: for every loader chosen, the others it brings along are those the verdict says go beside it
            Cli("add", Bml("0.13.0"));
            Cli("add", Chef());
            Cli("add", Mdl3());
            Cli("add", Addon("Narrow", "mods/x/narrow", "local chef = ...\nchef.log('x')\n", A2));
            Cli("add", Addon("Hud", "mods/me/hud", "local m = rawget(_G, 'MDL3')\n", A3));
            var lib = Library.Load();
            var loaders = Loaders.Of(lib);
            foreach (var chosen in loaders)
                foreach (var other in loaders.Where(m => m != chosen))
                {
                    var p = LoaderCaps.Pair(Loaders.KindOf(chosen), Loaders.CapsOf(chosen), Loaders.KindOf(other), Loaders.CapsOf(other));
                    bool beside = p.State == "together" && p.Short.Count == 0 && p.In == Loaders.KindOf(chosen);
                    Assert.True(beside == Loaders.HelpersFor(lib, chosen).Any(m => m.Id == other.Id), chosen.Name + " / " + other.Name);
                }
        }

        /// <summary>Chef Shared Loader 1.2.1 reads no Lua resource whose row is over 4 MB (Super Holo
        /// HUD's 8.8 MB script did not start under it, 2026-10-26): said when Chef starts it, not
        /// under Boot Mod Loader; a script just under the limit is fine.</summary>
        [Fact]
        public void AScriptOver4MbDoesNotStartUnderChef()
        {
            string Big(int n) => "local data = '" + new string('x', n) + "'\n";
            Cli("add", Bml("0.13.0"));
            Cli("add", Chef());
            Cli("add", Addon("Holo", "mods/me/holo", Big(4 * 1024 * 1024)));                       // over: 4 MiB + header
            Cli("add", Addon("Small", "mods/me/small", Big(4 * 1024 * 1024 - 200), A2));            // the row just under 4 MiB
            Cli("loader", "Boot Mod Loader");
            Assert.DoesNotContain(Now().Issues, i => i.Kind == "too_big_for_chef");             // Boot Mod Loader has no cap
            Cli("loader", "Chef Shared Loader");
            var big = Now().Issues.Where(i => i.Kind == "too_big_for_chef").ToList();
            Assert.Equal(new[] { "Holo" }, big.Select(i => i.Mods.Single().Name).ToArray());
            Assert.True(long.Parse(big[0].Detail) > LuaMods.ChefMaxLua);
            Assert.Contains("Holo will not start: Chef Shared Loader reads no script over 4 MB at present", Cli("lua"));
            Assert.Contains("\"biggest\": " + (4 * 1024 * 1024 + "local data = ''\n".Length + "-- HD2-Addon: mods/me/holo\n".Length + "local x = 1\n".Length + 8), Cli("state"));
        }

        /// <summary>The user (2026-10-26, "好不统一"): under every choice of loader, the one in effect too,
        /// the same question is answered: which mods would not start, and why. Chef Shared Loader runs
        /// the boots deployed before it, so a boot that starts only its mod is no conflict under it.</summary>
        [Fact]
        public void EveryChoiceOfLoaderSaysWhatWouldNotStart()
        {
            Cli("add", Bml("0.13.0"));
            Cli("add", Chef());
            Cli("add", Addon("Lamp", "mods/me/lamp"));
            Cli("add", Addon("Narrow", "mods/x/narrow", "local chef = ...\nchef.log('x')\n", A2));
            Cli("add", Addon("Holo", "mods/me/holo", "local data = '" + new string('x', 4 * 1024 * 1024) + "'\n", A3));
            Cli("add", Tracker());
            Cli("loader", "Boot Mod Loader");
            var lib = Library.Load();
            var all = LuaMods.Of(lib);
            LuaMods.Check(lib, all);
            var outs = LuaMods.Outcomes(lib, all).ToDictionary(x => x.Choice?.Name ?? "none", x => x.Outcomes);
            string Say(string choice) => string.Join("; ", outs[choice].Select(o => o.Why + ": " + string.Join(", ", o.Mods.Select(m => m.Name))));
            Assert.Equal("", Say("Boot Mod Loader"));                                   // Chef helps, the tracker is a guest
            Assert.Equal("too_big_for_chef: Holo", Say("Chef Shared Loader"));           // the tracker's boot: Chef runs it
            Assert.Equal("no_loader: Lamp, Narrow, Holo", Say("none"));
            // asking the other choices leaves the flags of the one in effect alone
            Assert.True(all.Single(x => x.Mod.Name == "Tracker").Guest);
            Assert.False(all.Single(x => x.Mod.Name == "Tracker").ChefRuns);
            Assert.Contains("\"outcomes\"", Cli("state"));
            Assert.Contains("Chef Shared Loader: will not start (script over Chef Shared Loader's 4 MB limit): Holo", Cli("lua"));

            // Chef in effect: the tracker's boot is run by it, no conflict
            Cli("loader", "Chef Shared Loader");
            var now = Now();
            Assert.Equal("chef", now.Running);
            Assert.DoesNotContain(now.Issues, i => i.Kind == "boot_conflict");
            lib = Library.Load();
            all = LuaMods.Of(lib);
            LuaMods.Check(lib, all);
            Assert.True(all.Single(x => x.Mod.Name == "Tracker").ChefRuns);
            Assert.False(all.Single(x => x.Mod.Name == "Tracker").Guest);
        }

        // a mod that is no loader, with a boot that starts only itself (as Objective Tracker 0.6.7)
        const string TrackerBoot = "function init() end\nfunction update(dt) end\nlocal OT = 'starts itself'\n";
        static bool Guest(string name)
        {
            var lib = Library.Load();
            var all = LuaMods.Of(lib);
            LuaMods.Check(lib, all);
            return all.Single(x => x.Mod.Name == name).Guest;
        }

        string Tracker(string boot = TrackerBoot) => Mod("Tracker", new Dictionary<string, byte[]>
        {
            [A + ".patch_0"] = LuaArchive((LuaMods.BootId, boot), (A2, "local OT_ADDON='mods/x/tracker'\nlocal y = 1\n")),
        });

        /// <summary>The user (2026-10-26): players must never mind which boot comes last, and a mod
        /// that starts only itself is no loader. With Boot Mod Loader 0.12.0 or later winning, its
        /// boot goes to the game off the slot, as a guest the loader runs; the library is not
        /// touched. With an older loader, nothing is changed (nobody would start it).</summary>
        [Fact]
        public void ABootThatStartsOnlyItsModIsRunByTheLoader()
        {
            Assert.Equal(LuaMods.BootId, GuestBoots.Murmur64(Bytes("boot")));
            Assert.Equal(Deploy.LuaType, GuestBoots.Murmur64(Bytes("lua")));
            Assert.Equal(0xC27789387E64579Aul, GuestBoots.Murmur64(Bytes("mods/chef_shared_loader/core_1")));

            Cli("add", Bml("0.11.0"));
            var trackerDir = Tracker();
            Cli("add", trackerDir);
            var original = File.ReadAllBytes(Path.Combine(trackerDir, A + ".patch_0"));
            // an older loader runs no guests: the loader's boot wins (it goes last), the tracker's does not run
            var now = Now();
            Assert.Equal("bml", now.Running);
            Assert.Contains(now.Issues, i => i.Kind == "boot_conflict");
            Assert.All(Deploy.Plan(Library.Load()), w => Assert.DoesNotContain("guest_boots", w.Src));

            Cli("remove", "Boot Mod Loader");
            Cli("add", Bml("0.12.0"));
            Cli("move", "Boot Mod Loader", "--top");          // where it stands does not matter
            now = Now();
            Assert.Equal("bml", now.Running);
            Assert.Equal("Boot Mod Loader", now.Owner.Mod.Name);
            Assert.DoesNotContain(now.Issues, i => i.Kind == "boot_conflict" || i.Kind == "no_loader");
            Assert.True(Guest("Tracker"));
            Assert.Contains("\"guest\": true", Cli("state"));

            var w0 = Deploy.Plan(Library.Load()).Single(w => w.Mod == "Tracker" && Deploy.Patch.IsMatch(w.Name));
            Assert.Equal(A + ".patch_0", w0.Name);                // the loader is numbered last
            Assert.Contains("guest_boots", w0.Src);
            var d = File.ReadAllBytes(w0.Src);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(trackerDir, A + ".patch_0")));     // the library is not touched
            // every row but the boot as it was; the boot renamed, its script at the end with the guest line
            var before = Deploy.Rows(Path.Combine(trackerDir, A + ".patch_0"));
            var after = Deploy.Rows(w0.Src);
            Assert.Equal(before.Count, after.Count);
            Assert.Equal(before[1].Id, after[1].Id);
            Assert.Equal(before[1].Off, after[1].Off);
            Assert.Equal(before[1].Size, after[1].Size);
            Assert.Equal(GuestBoots.IdOf("Tracker"), after[0].Id);
            Assert.Equal(Deploy.LuaType, after[0].Type);
            Assert.Equal(0ul, after[0].Off % 16);
            Assert.True(after.All(r => r.Off + r.Size <= (ulong)d.Length));
            Assert.Equal((ulong)d.Length, after[0].Off + after[0].Size);
            Assert.Equal(original, d.Take(original.Length).Select((b, i) => i >= 0x48 + 32 && i < 0x48 + 32 + 80 ? original[i] : b).ToArray());
            var text = Encoding.UTF8.GetString(d, (int)after[0].Off + 8, (int)after[0].Size - 8);
            Assert.Equal("-- HD2-Guest-Boot: Tracker\n" + TrackerBoot, text);
            Assert.Equal((uint)(after[0].Size - 8), BitConverter.ToUInt32(d, (int)after[0].Off));
            Assert.Equal(2u, BitConverter.ToUInt32(d, (int)after[0].Off + 4));
            Assert.DoesNotContain(after, r => r.Id == LuaMods.BootId);

            // deployed: the game has the derived copy; a second deploy keeps it
            Cli("deploy");
            Assert.Equal(d, File.ReadAllBytes(Path.Combine(Data, A + ".patch_0")));
            Assert.Contains("wrote 0", Cli("deploy"));
            // the tracker first: still a guest (the loader's boot wins either way)
            Cli("move", "Tracker", "--top");
            Assert.True(Guest("Tracker"));
            Assert.Equal("bml", Now().Running);

            // a boot in bytecode cannot be a guest: it stays, and the loader's boot wins over it
            Cli("remove", "Tracker");
            Cli("add", Mod("Compiled", new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "\u001bLuaQ...")) }));
            Assert.All(Deploy.Plan(Library.Load()), w => Assert.DoesNotContain("guest_boots", w.Src));
            Assert.Contains(Now().Issues, i => i.Kind == "boot_conflict");
        }

        [Fact]
        public void SameNamesPacksAndScriptsTheGameCannotRun()
        {
            Cli("add", Bml());
            Cli("add", Addon("Cam", "mods/actioncam/actioncam"));
            Cli("add", Addon("Cam copy", "mods/actioncam/actioncam", "", A2));
            var same = Now().Issues.Single(i => i.Kind == "same_name");
            Assert.Equal("mods/actioncam/actioncam", same.Detail);
            Assert.Equal("Cam copy", same.Mods[0].Name);                       // the later one runs
            Cli("remove", "Cam copy");

            // a translation pack: the same author's folder, the mod's name and a language, and it wraps the menus
            Cli("add", Addon("Cam zh", "mods/actioncam/actioncam_zh_cn", "local m = rawget(_G, 'ModOptionsMenu')\nlocal old = m.register_option\n", A3));
            var lib = Library.Load();
            var pack = LuaMods.Of(lib).Single(x => x.Mod.Name == "Cam zh");
            Assert.Equal("Cam", pack.PackOf.Name);
            Assert.Empty(Now().Issues);
            // the mod turned off takes its pack with it, and nothing is said (the user, 2026-10-30)
            Assert.Contains("off Cam zh (translation pack of Cam)", Cli("disable", "Cam"));
            Assert.False(Library.Load().Mods.Single(m => m.Name == "Cam zh").Enabled);
            Assert.Empty(Now().Issues);
            // turned on again, it brings the pack back
            Assert.Contains("on  Cam zh (translation pack of Cam)", Cli("enable", "Cam"));
            Assert.True(Library.Load().Mods.Single(m => m.Name == "Cam zh").Enabled);
            Assert.Null(Library.Load().Mods.Single(m => m.Name == "Cam zh").OffWith);
            // a pack the player turned off stays off when the mod comes back
            Cli("disable", "Cam zh");
            Cli("disable", "Cam");
            Cli("enable", "Cam");
            Assert.False(Library.Load().Mods.Single(m => m.Name == "Cam zh").Enabled);
            // and one the player turned on again while the mod was off is no longer the mod's to bring
            Cli("disable", "Cam");
            Cli("enable", "Cam zh");
            Assert.Null(Library.Load().Mods.Single(m => m.Name == "Cam zh").OffWith);
            // a pack on with its mod off is no issue; with its mod not installed it is
            Assert.Empty(Now().Issues);
            Cli("remove", "Cam");
            Assert.Contains(Now().Issues, i => i.Kind == "pack_alone" && i.Mods.Single().Name == "Cam zh");
            // a name that only looks like one, with no menus wrapped: not a pack
            Cli("add", Addon("Cam ru", "mods/actioncam/actioncam_ru", "", A3));
            Assert.Null(LuaMods.Of(Library.Load()).Single(x => x.Mod.Name == "Cam ru").PackOfName);

            // goto, and ffi without a fallback: the game's Lua cannot run them
            Cli("add", Addon("Old", "mods/old/one", "goto skip ::skip::\n", A2));
            Cli("add", Addon("Older", "mods/old/two", "local ffi = require('ffi')\n", A1));
            var broken = Now().Issues.Where(i => i.Kind == "cannot_run").Select(i => i.Mods.Single().Name).ToArray();
            Assert.Equal(new[] { "Old", "Older" }, broken);
            Assert.Contains("cannot run: mods/old/one line 2", Cli("lua"));         // line 1 is the header
        }

        [Fact]
        public void TheLastSessionAndTheLastCrash()
        {
            var folder = Path.Combine(Root, "Arrowhead");
            Directory.CreateDirectory(Path.Combine(folder, "dumps"));
            LuaMods.GameFolderOverride = folder;
            try
            {
                Assert.Null(LuaMods.LastSession());
                File.WriteAllText(Path.Combine(folder, "boot_mod_loader.log"),
                    "10:00:00 == boot-mod-loader-0.10.0 2026-10-24 10:00:00 (Lua 5.1)\n" +
                    "10:00:01 mods/me/old (patch_3, 10 bytes): loaded in 1 ms\n" +
                    "19:21:58 == boot-mod-loader-0.11.0 2026-10-25 19:21:58 (Lua 5.1)\n" +
                    "19:21:58 game boot: ran\n" +
                    "19:21:58 mods/me/lamp (patch_68, 240458 bytes): loaded in 12 ms\n" +
                    "19:21:58 mods/me/radio (patch_70): FAILED: [string \"mods/me/radio\"]:5: attempt to index nil\n" +
                    "stack traceback:\n\t[C]: in function 'error'\n" +
                    "19:21:58 mods/me/old (patch_71): does not compile on Lua 5.1: mods/me/old:9: malformed number near '1ULL'\n" +
                    "19:21:58 started 1 of 3 mods in 303 ms\n", new UTF8Encoding(false));
                var s = LuaMods.LastSession();
                Assert.Equal("boot-mod-loader-0.11.0", s.Loader);
                Assert.Equal(new DateTime(2026, 10, 25, 19, 21, 58), s.When);
                Assert.Equal(new[] { "loaded", "failed", "no_compile" }, Kinds(s));
                Assert.Equal(12, s.Mods[0].Ms);
                Assert.Contains("stack traceback", s.Mods[1].Why);
                Assert.Equal((1, 3), (s.StartedCount, s.Total));

                // a crash: the time it was written, not the one in its name (the game's start)
                Assert.Empty(LuaMods.Crashes());
                var dump = Path.Combine(folder, "dumps", "dump-2026-10-25-19.21.40-3d5d3f48-PC-1.dmp");
                File.WriteAllBytes(dump, new byte[] { 1 });
                var crashed = new DateTime(2026, 10, 25, 19, 43, 26);
                File.SetLastWriteTime(dump, crashed);
                Assert.Equal(crashed, LuaMods.Crashes().Single().When);
                // what was in the game then: the last deploy before it
                Assert.Null(LuaMods.DeployedAt(crashed));
                LuaMods.Record(new[] { "Lamp", "Lamp", "Radio" });
                Assert.Null(LuaMods.DeployedAt(crashed));                     // that deploy came after
                Assert.Equal(new[] { "Lamp", "Radio" }, LuaMods.DeployedAt(DateTime.Now.AddMinutes(1)).Value.Mods.ToArray());
            }
            finally { LuaMods.GameFolderOverride = null; }
        }

        /// <summary>A minidump with an exception at address and one module (base, size, name), as
        /// the game writes them: the header, the stream directory, the exception, the modules.</summary>
        static byte[] Minidump(ulong address, ulong moduleBase, uint moduleSize, string module)
        {
            var b = new byte[400];
            BitConverter.GetBytes(0x504D444Du).CopyTo(b, 0);
            BitConverter.GetBytes(2u).CopyTo(b, 8);
            BitConverter.GetBytes(32u).CopyTo(b, 12);
            BitConverter.GetBytes(6u).CopyTo(b, 32); BitConverter.GetBytes(168u).CopyTo(b, 36); BitConverter.GetBytes(56u).CopyTo(b, 40);
            BitConverter.GetBytes(4u).CopyTo(b, 44); BitConverter.GetBytes(112u).CopyTo(b, 48); BitConverter.GetBytes(224u).CopyTo(b, 52);
            BitConverter.GetBytes(0xC0000005u).CopyTo(b, 56 + 8);
            BitConverter.GetBytes(address).CopyTo(b, 56 + 24);
            BitConverter.GetBytes(1u).CopyTo(b, 224);
            BitConverter.GetBytes(moduleBase).CopyTo(b, 228);
            BitConverter.GetBytes(moduleSize).CopyTo(b, 236);
            BitConverter.GetBytes(336u).CopyTo(b, 228 + 20);
            var name = Encoding.Unicode.GetBytes(module);
            BitConverter.GetBytes((uint)name.Length).CopyTo(b, 336);
            name.CopyTo(b, 340);
            return b;
        }

        /// <summary>The user (2026-10-26): the crash card listed the mods and said nothing, and the
        /// game crashes at one place all the time. A crash at a place seen 3 times in the last 50
        /// shows no card; a new place is named with what the deploy before it added.</summary>
        [Fact]
        public void ACrashIsShownOnlyAtAPlaceTheGameDoesNotCrashAtOften()
        {
            var folder = Path.Combine(Root, "hd2");
            Directory.CreateDirectory(Path.Combine(folder, "dumps"));
            LuaMods.GameFolderOverride = folder;
            try
            {
                const ulong exe = 0x7FF600000000;
                void Dump(int minute, ulong offset, string module = @"C:\Games\helldivers2.exe")
                {
                    var p = Path.Combine(folder, "dumps", "dump-2026-10-26-09." + minute.ToString("00") + ".00-x-PC-1.dmp");
                    File.WriteAllBytes(p, Minidump(exe + offset, exe, 0x2000000, module));
                    File.SetLastWriteTime(p, new DateTime(2026, 10, 26, 10, minute, 0));
                }
                Dump(1, 0x664D86);
                Dump(2, 0x664D86);
                Assert.Equal("helldivers2.exe+0x664D86", LuaMods.SiteOf(Path.Combine(folder, "dumps", LuaMods.Crashes(1).Single().File)));
                Assert.Null(LuaMods.SiteOf(Path.Combine(folder, "nothing.dmp")));             // unreadable: no place
                // twice is not yet often
                var (site, times, of) = LuaMods.SiteCount(LuaMods.Crashes(1).Single().File);
                Assert.Equal(("helldivers2.exe+0x664D86", 2, 2), (site, times, of));
                Assert.Contains("\"common\": false", Cli("state"));
                Dump(3, 0x664D86);
                Assert.Equal(3, LuaMods.SiteCount(LuaMods.Crashes(1).Single().File).Times);
                Assert.Contains("\"common\": true", Cli("state"));
                // a new place: named, once
                Dump(4, 0x564F8C);
                Assert.Equal(("helldivers2.exe+0x564F8C", 1, 4), LuaMods.SiteCount(LuaMods.Crashes(1).Single().File));
                Assert.Contains("\"common\": false", Cli("state"));
                Assert.Contains("at helldivers2.exe+0x564F8C, 1 of the last 4 crashes there", Cli("lua"));
            }
            finally { LuaMods.GameFolderOverride = null; }
        }

        /// <summary>What the deploy before a crash put in the game that the one before it had not.</summary>
        [Fact]
        public void TheDeployBeforeACrashSaysWhatItAdded()
        {
            Assert.Null(LuaMods.AddedAt(DateTime.Now.AddMinutes(1)));                 // nothing recorded
            LuaMods.Record(new[] { "Lamp v1", "Radio" });
            Assert.Null(LuaMods.AddedAt(DateTime.Now.AddMinutes(1)));                 // one deploy: nothing to compare
            System.Threading.Thread.Sleep(1100);
            LuaMods.Record(new[] { "Lamp v2", "Radio", "Tracker" });
            Assert.Equal(new[] { "Lamp v2", "Tracker" }, LuaMods.AddedAt(DateTime.Now.AddMinutes(1)).ToArray());
        }

        /// <summary>The window's list never waits for the scripts (the user, 2026-10-26: the start
        /// was slower): a state read with Lazy says the Lua facts are pending until the archives
        /// are read in the background, and the next run finds them in lua_scan.json.</summary>
        [Fact]
        public void TheListNeverWaitsForTheScripts()
        {
            Cli("add", Bml());
            Cli("add", Addon("Lamp", "mods/me/lamp"));
            LuaMods.ForgetRun();
            LuaMods.Lazy = true;
            try
            {
                string Lua() => Json.Write(((Dictionary<string, object>)Json.Parse(Cli("state")))["lua_check"]);
                Assert.Contains("\"pending\":true", Lua().Replace(" ", ""));
                LuaMods.Warm(Library.Load());
                Assert.Contains("\"running\":\"bml\"", Lua().Replace(" ", ""));
                Assert.True(File.Exists(Store.PathOf("lua_scan.json")));
                // a new run: nothing read yet in memory, all of it on disk
                LuaMods.ForgetRun();
                Assert.Contains("\"running\":\"bml\"", Lua().Replace(" ", ""));
                // an archive that changes is read again, not taken from the disk
                var lamp = Library.Load().Mods.Single(m => m.Name == "Lamp").Path;
                File.WriteAllBytes(Path.Combine(lamp, A + ".patch_0"), LuaArchive((A1, "-- HD2-Addon: mods/me/lamp\ngoto x ::x::\n")));
                File.SetLastWriteTimeUtc(Path.Combine(lamp, A + ".patch_0"), DateTime.UtcNow.AddMinutes(1));
                Assert.Contains("\"pending\":true", Lua().Replace(" ", ""));
                LuaMods.Warm(Library.Load());
                Assert.Contains("cannot_run", Lua());
            }
            finally { LuaMods.Lazy = false; }
        }

        [Fact]
        public void ADeployIsRecordedForLaterCrashes()
        {
            Cli("add", Addon("Lamp", "mods/me/lamp"));
            Cli("deploy");
            var then = LuaMods.DeployedAt(DateTime.Now.AddSeconds(5));
            Assert.Equal(new[] { "Lamp" }, then.Value.Mods.ToArray());
        }
    }
}
