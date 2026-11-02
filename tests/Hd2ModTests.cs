using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Hd2Mod;
using Xunit;

// the settings folder and the hooks are static: one test at a time
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Hd2Mod.Tests
{
    /// <summary>hd2qd against a fake game folder and fake mods.</summary>
    public abstract class Fixture : IDisposable
    {
        protected const string A = "9ba626afa44a3aa3", B = "20bdec2a51065e1f";
        protected readonly string Root, Game, Data;

        protected Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "hd2mod_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Store.HomeOverride = Path.Combine(Root, "home");
            Game = Path.Combine(Root, "game");
            Data = Path.Combine(Game, "data");
            Directory.CreateDirectory(Data);
            // a mod folder of the test's own: the default would be the player's HD2 Arsenal folder
            Store.Save("config.json", new Dictionary<string, object> { ["game_dir"] = Game, ["mods_dir"] = Path.Combine(Root, "library") });
            Deploy.GameRunning = () => false;
            Store.Redirected = () => null;             // the test's home is not under AppData anyway
            Deploy.SameDrive = Native.SameDrive;
            // the plugins the test project copies beside itself, as if the player had added them
            PluginFiles.Folder = AppDomain.CurrentDomain.BaseDirectory;
        }

        public virtual void Dispose()
        {
            Store.HomeOverride = null;
            PluginFiles.Folder = null;
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        protected static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

        /// <summary>A patch archive holding resources (type, id): header, one type row, file rows.</summary>
        protected static byte[] Archive(params (ulong Type, ulong Id)[] resources) => Archive(null, resources);

        /// <summary>The same, with bytes for every resource: each one gets its own copy of them,
        /// at its own place in the archive (that is what tells two mods' resources apart).</summary>
        protected static byte[] Archive(byte[] body, params (ulong Type, ulong Id)[] resources)
        {
            var b = new List<byte>();
            var head = new byte[0x48];
            BitConverter.GetBytes(0xF0000011u).CopyTo(head, 0);
            BitConverter.GetBytes(1u).CopyTo(head, 4);
            BitConverter.GetBytes((uint)resources.Length).CopyTo(head, 8);
            b.AddRange(head);
            b.AddRange(new byte[32]);
            int size = body?.Length ?? 0;
            ulong at = (ulong)(0x48 + 32 + 80 * resources.Length);
            foreach (var (t, id) in resources)
            {
                var row = new byte[80];
                BitConverter.GetBytes(id).CopyTo(row, 0);
                BitConverter.GetBytes(t).CopyTo(row, 8);
                BitConverter.GetBytes(at).CopyTo(row, 0x10);
                BitConverter.GetBytes((uint)size).CopyTo(row, 0x38);
                b.AddRange(row);
                at += (ulong)size;
            }
            if (body != null) foreach (var _ in resources) b.AddRange(body);
            return b.ToArray();
        }

        /// <summary>A patch archive of Lua scripts as the game has them: the type table names Lua,
        /// and each resource is its text's length, the kind (2: source), then the text.</summary>
        protected static byte[] LuaArchive(params (ulong Id, string Text)[] scripts)
        {
            var b = new List<byte>();
            var head = new byte[0x48];
            BitConverter.GetBytes(0xF0000011u).CopyTo(head, 0);
            BitConverter.GetBytes(1u).CopyTo(head, 4);
            BitConverter.GetBytes((uint)scripts.Length).CopyTo(head, 8);
            b.AddRange(head);
            var type = new byte[32];
            BitConverter.GetBytes(Deploy.LuaType).CopyTo(type, 8);
            b.AddRange(type);
            var bodies = scripts.Select(s =>
            {
                var text = Bytes(s.Text);
                return BitConverter.GetBytes((uint)text.Length).Concat(BitConverter.GetBytes(2u)).Concat(text).ToArray();
            }).ToList();
            ulong at = (ulong)(0x48 + 32 + 80 * scripts.Length);
            for (int i = 0; i < scripts.Length; i++)
            {
                var row = new byte[80];
                BitConverter.GetBytes(scripts[i].Id).CopyTo(row, 0);
                BitConverter.GetBytes(Deploy.LuaType).CopyTo(row, 8);
                BitConverter.GetBytes(at).CopyTo(row, 0x10);
                BitConverter.GetBytes((uint)bodies[i].Length).CopyTo(row, 0x38);
                b.AddRange(row);
                at += (ulong)bodies[i].Length;
            }
            foreach (var body in bodies) b.AddRange(body);
            return b.ToArray();
        }

        /// <summary>A mod folder: files {relative path: bytes}, plus empty companions for each main file.</summary>
        protected string Mod(string name, Dictionary<string, byte[]> files, string manifest = null)
        {
            var folder = Path.Combine(Root, "mods", name);
            foreach (var kv in files)
            {
                var p = Path.Combine(folder, kv.Key.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllBytes(p, kv.Value);
                if (Deploy.Patch.IsMatch(Path.GetFileName(p)))
                    foreach (var ext in Deploy.Companions) File.WriteAllBytes(p + ext, new byte[0]);
            }
            if (manifest != null)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest, new UTF8Encoding(false));
            }
            return folder;
        }

        protected static Dictionary<string, byte[]> Files(params (string Rel, string Body)[] xs) =>
            xs.ToDictionary(x => x.Rel, x => Bytes(x.Body));

        /// <summary>A command's output; a refusal fails the test unless code says otherwise.</summary>
        protected string Cli(params string[] args) => CliCode(0, args);

        protected string CliCode(int code, params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            int r = Commands.Run(args, o, e);
            Assert.True(r == code, "hd2qd " + string.Join(" ", args) + " returned " + r + ": " + e);
            return o.ToString();
        }

        /// <summary>The kind of a refusal, for the window's own words.</summary>
        protected static string Refused(params string[] args)
        {
            Assert.Equal(1, Commands.Run(args, new StringWriter(), new StringWriter(), out var refused));
            return refused?.Kind;
        }

        protected List<string> Deployed()
        {
            var l = Directory.EnumerateFiles(Data).Select(Path.GetFileName).Where(f => Deploy.Managed.IsMatch(f)).ToList();
            l.Sort(string.CompareOrdinal);
            return l;
        }

        protected string Body(string name) => File.ReadAllText(Path.Combine(Data, name));

        protected static Dictionary<string, string> PlanBodies() =>
            Deploy.Plan(Library.Load()).Where(w => Deploy.Patch.IsMatch(w.Name)).ToDictionary(w => w.Name, w => File.ReadAllText(w.Src));

        protected static List<string> PlanList() =>
            Deploy.Plan(Library.Load()).Where(w => Deploy.Patch.IsMatch(w.Name)).Select(w => File.ReadAllText(w.Src)).ToList();

        protected static bool Same(string a, string b) => Native.SameFile(a, b) == true;

        protected static string J(object x) => Json.Write(x);
    }

    public class Numbering : Fixture
    {
        [Fact]
        public void OrderOptionsAndArchives()
        {
            var one = Mod("One", Files((A + ".patch_0", "one-a"), (B + ".patch_0", "one-b")));
            var two = Mod("Two", Files(("opt/" + A + ".patch_0", "two-opt"), ("sub1/" + A + ".patch_0", "two-sub1"),
                                       ("sub2/" + A + ".patch_0", "two-sub2"), ("sub2/" + A + ".patch_1", "two-sub2b")),
                          J(new Dictionary<string, object>
                          {
                              ["Version"] = 1L, ["Name"] = "Two", ["Options"] = new List<object> { new Dictionary<string, object>
                              {
                                  ["Name"] = "Base", ["Include"] = new List<object> { "opt" },
                                  ["SubOptions"] = new List<object>
                                  {
                                      new Dictionary<string, object> { ["Name"] = "S1", ["Include"] = new List<object> { "sub1" } },
                                      new Dictionary<string, object> { ["Name"] = "S2", ["Include"] = new List<object> { "sub2" } },
                                  },
                              } },
                          }));
            Cli("add", one);
            Cli("add", two);
            // mods top to bottom; Two's option folder, then its first sub-option (the default)
            Assert.Equal(new Dictionary<string, string>
            {
                [A + ".patch_0"] = "one-a", [B + ".patch_0"] = "one-b", [A + ".patch_1"] = "two-opt", [A + ".patch_2"] = "two-sub1",
            }, PlanBodies());
            Cli("set", "Two", "Base/S2", "on", "--only");
            var plan = PlanBodies();
            Assert.Equal("two-sub2", plan[A + ".patch_2"]);
            Assert.Equal("two-sub2b", plan[A + ".patch_3"]);
            // one sub-option at a time: they replace the same resources (the user, 2026-10-27)
            Cli("set", "Two", "Base/S1", "on");
            Assert.Equal("two-sub1", PlanBodies()[A + ".patch_2"]);
            Assert.Equal(1, Library.Load().Mods[1].Options[0].Subs.Count(x => x.Enabled));
            Assert.Equal("pick_one", Refused("set", "Two", "Base/S1", "off"));   // the one chosen stays until another is
            Cli("set", "Two", "Base/S2", "off");                                  // one not chosen: nothing to do
            // several chosen by an older window: the first counts
            var lib = Library.Load();
            foreach (var x in lib.Mods[1].Options[0].Subs) x.Enabled = true;
            lib.Save();
            Assert.Equal("two-sub1", PlanBodies()[A + ".patch_2"]);
            Assert.DoesNotContain("two-sub2", PlanBodies().Values);
            Cli("set", "Two", "Base", "off");
            Assert.DoesNotContain(A + ".patch_2", Deploy.Plan(Library.Load()).Select(w => w.Name));
        }

        [Fact]
        public void LenientManifestAndNoManifest()
        {
            var m = Mod("Loose", Files(("x/" + A + ".patch_0", "x")),
                        "{\n \"name\": \"Loose\",\n \"options\": [\n  {\"name\": \"X\", \"include\": [\"x\"],},\n ],\n}\n");
            var bare = Mod("Bare", Files(("deep/" + A + ".patch_0", "deep")));
            Cli("add", m);
            Cli("add", bare);
            Assert.Equal(new[] { "x", "deep" }, PlanList());
        }
    }

    public class Deploying : Fixture
    {
        readonly string one, two, three;

        public Deploying()
        {
            one = Mod("One", Files((A + ".patch_0", "one")));
            two = Mod("Two", Files((A + ".patch_0", "two"), (B + ".patch_0", "two-b")));
            three = Mod("Three", Files((A + ".patch_0", "three")));
            foreach (var m in new[] { one, two, three }) Cli("add", m);
        }

        [Fact]
        public void DeployThenNothingToDo()
        {
            Assert.Contains("wrote 12", Cli("deploy"));            // 4 patch files, each with 2 companions
            Assert.Equal("one", Body(A + ".patch_0"));
            Assert.Equal("three", Body(A + ".patch_2"));
            Assert.True(Same(Path.Combine(Data, A + ".patch_1"), Path.Combine(two, A + ".patch_0")), "a hard link to the library, not a copy");
            Assert.Contains("kept 12, moved 0, wrote 0", Cli("deploy"));
        }

        /// <summary>Profiles, a switch of their own (the user, 2026-10-25): off, one list; on, the
        /// library holds every mod and each profile some of them, with its own order, on/off and
        /// options. Only the profile in use deploys; a mod added goes into the profile in use.</summary>
        [Fact]
        public void ProfilesHoldTheirOwnModsOrderAndOptions()
        {
            string[] Names() => Library.Load().Mods.Select(m => m.Name).ToArray();
            string[] On() => Library.Load().Mods.Where(m => m.Enabled).Select(m => m.Name).ToArray();
            Cli("deploy");
            Assert.Contains("profiles are off", Cli("profiles"));
            CliCode(1, "profile-new", "Solo");                                   // not while they are off
            Cli("profiles-on");
            Assert.Contains("* Default  (3 mods, 3 on)", Cli("profiles"));
            // an empty profile, two of the library's mods put in, its own order
            Cli("profile-new", "Solo", "--empty");
            Cli("profile-use", "Solo");
            Assert.Empty(On());
            Assert.Empty(Deployed());
            Cli("profile-add", "One", "Three");
            Cli("move", "One", "--bottom");
            Cli("deploy");
            Assert.Equal(new[] { "Three", "One" }, On());
            Assert.Equal(new[] { "three", "one" }, new[] { Body(A + ".patch_0"), Body(A + ".patch_1") });
            Assert.Contains("* Solo  (2 mods, 2 on)", Cli("profiles"));
            // back to Default: its three, its order
            Cli("profile-use", "Default");
            Assert.Equal(new[] { "One", "Two", "Three" }, On());
            Assert.Equal(new[] { "one", "two", "three" }, new[] { Body(A + ".patch_0"), Body(A + ".patch_1"), Body(A + ".patch_2") });
            // a mod added goes into the profile in use only
            Cli("add", Mod("Four", Files((B + ".patch_1", "four"))));
            Assert.Contains("* Default  (4 mods, 4 on)", Cli("profiles"));
            Assert.Contains("  Solo  (2 mods, 2 on)", Cli("profiles"));
            Cli("profile-use", "Solo", "--no-deploy");
            Assert.Equal(new[] { "Three", "One" }, On());
            Assert.Equal(new[] { "Three", "One" }, Names().Take(2));               // its own mods first
            // out of the profile, still in the library
            Cli("profile-remove", "One");
            Assert.Equal(new[] { "Three" }, On());
            Assert.Contains(Library.Load().Mods, m => m.Name == "One");
            Cli("profile-add", "One");
            // a mod's options are each profile's own
            Cli("add", Mod("Cape", Files(("a/" + B + ".patch_0", "cape-a"), ("b/" + B + ".patch_0", "cape-b")),
                           "{\"Name\": \"Cape\", \"Options\": [{\"Name\": \"Pick\", \"SubOptions\": [" +
                           "{\"Name\": \"A\", \"Include\": [\"a\"]}, {\"Name\": \"B\", \"Include\": [\"b\"]}]}]}"));
            Cli("set", "Cape", "1.2", "on"); Cli("set", "Cape", "1.1", "off");  // Solo: B
            Cli("profile-use", "Default", "--no-deploy");
            Cli("profile-add", "Cape");
            Cli("set", "Cape", "1.1", "on"); Cli("set", "Cape", "1.2", "off");  // Default: A
            string Chosen() => Library.Load().Mods.Single(m => m.Name == "Cape").Options[0].Subs.Single(s => s.Enabled).Name;
            Assert.Equal("A", Chosen());
            Cli("profile-use", "Solo", "--no-deploy");
            Assert.Equal("B", Chosen());
            // off: one list of every mod; on again: the profile as it was kept
            Cli("profiles-off");
            Assert.Equal(5, Library.Load().Mods.Count);
            Assert.Contains("profiles are off", Cli("profiles"));
            Cli("profiles-on");
            Assert.Contains("* Solo  (3 mods", Cli("profiles"));
            // the one in use cannot go; renamed, deleted
            CliCode(1, "profile-delete", "Solo");
            Cli("profile-rename", "Solo", "Online");
            Assert.Contains("* Online", Cli("profiles"));
            Cli("profile-delete", "Default");
            Assert.DoesNotContain("Default", Cli("profiles"));
            CliCode(1, "profile-new", "online");                                 // names are one regardless of case
        }

        [Fact]
        public void ReorderOnlyRenames()
        {
            Cli("deploy");
            Cli("move", "Three", "--top");
            var out_ = Cli("deploy");
            Assert.Contains("moved", out_);
            Assert.Contains("wrote 0", out_);
            Assert.Equal(new[] { "three", "one", "two" }, Enumerable.Range(0, 3).Select(i => Body(A + ".patch_" + i)));
        }

        [Fact]
        public void DisableClosesTheGap()
        {
            Cli("deploy");
            Cli("disable", "One");
            Cli("deploy");
            var want = new List<string>
            {
                A + ".patch_0", A + ".patch_0.gpu_resources", A + ".patch_0.stream",
                A + ".patch_1", A + ".patch_1.gpu_resources", A + ".patch_1.stream",
                B + ".patch_0", B + ".patch_0.gpu_resources", B + ".patch_0.stream",
            };
            want.Sort(string.CompareOrdinal);
            Assert.Equal(want, Deployed());
            Assert.Equal(new[] { "two", "three" }, new[] { Body(A + ".patch_0"), Body(A + ".patch_1") });
        }

        [Fact]
        public void ReplaceTouchesOnlyThatMod()
        {
            Cli("deploy");
            var v2 = Mod("Two v2", Files((A + ".patch_0", "two v2"), (B + ".patch_0", "two-b v2")));
            Cli("replace", "Two", v2);
            Assert.Contains("kept 10, moved 0, wrote 2", Cli("deploy"));   // only Two's two patch files (its empty companions match)
            Assert.Equal("two v2", Body(A + ".patch_1"));
            Assert.Equal(v2, Library.Load().Mods[1].Path);
        }

        [Fact]
        public void AdoptsIdenticalFilesFromAnotherManager()
        {
            foreach (var (src, name) in new[] { (one, A + ".patch_0"), (two, A + ".patch_1"), (three, A + ".patch_2") })
                File.Copy(Path.Combine(src, A + ".patch_0"), Path.Combine(Data, name));
            File.Copy(Path.Combine(two, B + ".patch_0"), Path.Combine(Data, B + ".patch_0"));
            File.WriteAllText(Path.Combine(Data, A + ".patch_9"), "left over");
            var out_ = Cli("deploy");
            Assert.Contains("kept 4", out_);            // the four copies are taken over, not rewritten
            Assert.Contains("deleted 1", out_);
            Assert.DoesNotContain(A + ".patch_9", Deployed());
        }

        [Fact]
        public void RelinkTurnsCopiesIntoLinks()
        {
            Cli("deploy", "--copy");
            var a0 = Path.Combine(Data, A + ".patch_0");
            Assert.False(Same(a0, Path.Combine(one, A + ".patch_0")));
            Assert.Contains("would link 12 copies", Cli("relink", "--dry-run"));
            Assert.Contains("linked 12 copies", Cli("relink"));
            Assert.True(Same(a0, Path.Combine(one, A + ".patch_0")));
            Assert.Equal("three", Body(A + ".patch_2"));
            Assert.Empty(Directory.EnumerateFiles(Data, "*.hd2mod-link"));
            Assert.Contains("kept 12, moved 0, wrote 0", Cli("deploy"));
            Assert.Contains("linked 0 copies", Cli("relink"));
        }

        [Fact]
        public void RelinkLeavesOtherDrivesAndWrongFiles()
        {
            Cli("deploy", "--copy");
            File.WriteAllText(Path.Combine(Data, A + ".patch_1"), "bad");   // same size as "two", wrong bytes: a deploy rewrites it, relink does not
            Deploy.SameDrive = (a, b) => !a.StartsWith(three);
            Assert.Contains("linked 8 copies", Cli("relink"));    // 12, less Three's 3 (another drive) and the wrong file
            Assert.Equal("bad", Body(A + ".patch_1"));
            Assert.False(Same(Path.Combine(Data, A + ".patch_2"), Path.Combine(three, A + ".patch_0")));
        }

        [Fact]
        public void AChangedFileIsRewritten()
        {
            Cli("deploy", "--copy");
            File.WriteAllText(Path.Combine(Data, A + ".patch_0"), "oops");
            Assert.Contains("wrote 1", Cli("deploy", "--copy"));
            Assert.Equal("one", Body(A + ".patch_0"));
        }

        [Fact]
        public void StatusChangesNothingAndPurgeEmpties()
        {
            var before = Deployed();
            Assert.Contains("would keep 0", Cli("status"));
            Assert.Equal(before, Deployed());
            Cli("deploy");
            CliCode(1, "purge");
            Cli("purge", "--yes");
            Assert.Empty(Deployed());
            Assert.True(File.Exists(Path.Combine(one, A + ".patch_0")), "the library keeps its files");
        }

        static Dictionary<string, object> ByName(Dictionary<string, object> st, string name) =>
            Json.List(st, "mods").Cast<Dictionary<string, object>>().First(m => (string)m["name"] == name);

        static List<string> Actions(Dictionary<string, object> mod) =>
            Json.List(mod, "files").Cast<Dictionary<string, object>>().Select(f => f["name"] + " " + f["action"]).ToList();

        Dictionary<string, object> State() => (Dictionary<string, object>)Json.Parse(Cli("state"));

        [Fact]
        public void StateSaysWhatADeployWouldDo()
        {
            Cli("deploy");
            Cli("move", "Three", "--top");
            Cli("disable", "Two");
            Cli("add", Mod("Four", Files((A + ".patch_0", "four!"))));
            var st = State();
            Assert.Equal(new[] { A + ".patch_0 move" }, Actions(ByName(st, "Three")));
            Assert.Equal(A + ".patch_2", Json.List(ByName(st, "Three"), "files").Cast<Dictionary<string, object>>().First()["from"]);
            Assert.Equal(new[] { A + ".patch_1 move" }, Actions(ByName(st, "One")));
            Assert.Equal(new[] { A + ".patch_2 write" }, Actions(ByName(st, "Four")));
            Assert.Empty(Json.List(ByName(st, "Two"), "files"));
            Assert.Contains(B + ".patch_0", Json.List(st, "deletes"));
            // Four's patch file; its two empty companions are Two's, renamed
            Assert.Equal(1L, Json.Obj(st, "summary")["written"]);
            Cli("deploy");
            st = State();
            Assert.All(Json.List(st, "mods").Cast<Dictionary<string, object>>().SelectMany(m => Json.List(m, "files").Cast<Dictionary<string, object>>()),
                       f => Assert.Equal("keep", f["action"]));
            Assert.Empty(Json.List(st, "deletes"));
        }

        [Fact]
        public void StateShowsABrokenModAndTheRest()
        {
            Directory.Delete(two, true);
            var st = State();
            Assert.Contains("folder is gone", (string)ByName(st, "Two")["error"]);
            Assert.False((bool)ByName(st, "Two")["exists"]);
            Assert.Equal(new[] { A + ".patch_1 write" }, Actions(ByName(st, "Three")));
        }

        [Fact]
        public void DeleteTakesAModOffTheDisk()
        {
            Cli("deploy");
            Assert.Contains("deleted Two", Cli("delete", "Two", "--yes"));
            Assert.DoesNotContain(Library.Load().Mods, m => m.Name == "Two");
            Assert.False(Directory.Exists(two), "its folder is gone");
            Assert.True(Directory.Exists(one) && Directory.Exists(three), "the others stay");
            var want = new List<string> { A + ".patch_0", A + ".patch_0.gpu_resources", A + ".patch_0.stream",
                                          A + ".patch_1", A + ".patch_1.gpu_resources", A + ".patch_1.stream" };
            Assert.Equal(want, Deployed());
            Assert.Equal(new[] { "one", "three" }, new[] { Body(A + ".patch_0"), Body(A + ".patch_1") });
        }

        /// <summary>Several mods deleted at once, as the Storage page does with its ticked mods
        /// (the user, 2026-10-25): one deploy, every folder gone, the rest untouched.</summary>
        [Fact]
        public void DeleteTakesSeveralModsOffTheDiskAtOnce()
        {
            Cli("deploy");
            Assert.Contains("deleted 2 mods", Cli("delete", "One", "Two", "--yes"));
            Assert.Equal(new[] { "Three" }, Library.Load().Mods.Select(m => m.Name));
            Assert.False(Directory.Exists(one) || Directory.Exists(two), "their folders are gone");
            Assert.True(Directory.Exists(three), "the other stays");
            Assert.Equal(new[] { "three" }, new[] { Body(A + ".patch_0") });
        }

        [Fact]
        public void DeleteChangesNothingWithoutYesOrWhileTheGameRuns()
        {
            Cli("deploy");
            var before = Deployed();
            CliCode(1, "delete", "Two");
            Deploy.GameRunning = () => true;
            Assert.Equal(1, Commands.Run(new[] { "delete", "Two", "--yes" }, new StringWriter(), new StringWriter(), out var refused));
            Assert.Equal("game_running", refused.Kind);
            Assert.True(Directory.Exists(two));
            Assert.Contains(Library.Load().Mods, m => m.Name == "Two");
            Assert.Equal(before, Deployed());
        }

        [Fact]
        public void DeleteRefusesAFolderThatIsNotTheModsOwn()
        {
            Cli("add", Path.GetDirectoryName(one), "--name", "Everything", "--whole");     // the folder that holds One, Two and Three
            Assert.Equal(1, Commands.Run(new[] { "delete", "Everything", "--yes" }, new StringWriter(), new StringWriter(), out var shared));
            Assert.Equal("shared", shared.Kind);
            Cli("add", Game, "--name", "The game");
            Assert.Equal(1, Commands.Run(new[] { "delete", "The game", "--yes" }, new StringWriter(), new StringWriter(), out var guarded));
            Assert.Equal("protected", guarded.Kind);
            Assert.True(Directory.Exists(one) && Directory.Exists(Data), "nothing deleted");
        }

        [Fact]
        public void RefusesWhileTheGameRuns()
        {
            Deploy.GameRunning = () => true;
            CliCode(1, "deploy");
            Assert.Empty(Deployed());
        }
    }

    /// <summary>Downloads and update checks against fakes of the sites on localhost.</summary>
    public partial class OnlineSources : Fixture
    {
        readonly System.Net.HttpListener server = new System.Net.HttpListener();
        readonly string baseUrl;
        readonly Dictionary<string, byte[]> zips = new Dictionary<string, byte[]>();
        public readonly List<string> Seen = new List<string>();

        public OnlineSources()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            baseUrl = "http://localhost:" + port + "/";
            server.Prefixes.Add(baseUrl);
            server.Start();
#if HAS_NEXUS
            Nexus.ApiBase = baseUrl.TrimEnd('/');
#endif
            Updates.GitHubApi = baseUrl + "gh";
            System.Threading.Tasks.Task.Run(Serve);
            for (int f = 1; f <= 2; f++) zips["f" + f] = Zip(("Cape/manifest.json", "{\"Name\": \"Cape Colours\"}"), ("Cape/" + A + ".patch_0", "cape v" + f));
        }

        static byte[] Zip(params (string Name, string Body)[] entries)
        {
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var (name, body) in entries)
                    using (var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false))) w.Write(body);
            return ms.ToArray();
        }

        // the fake's part for what only our own builds send, in private/ (not in the repository)
        partial void OurAuth(System.Net.HttpListenerRequest request, ref bool authed);
        partial void OurAnswer(string path, ref byte[] body);

        void Serve()
        {
            while (server.IsListening)
            {
                System.Net.HttpListenerContext c;
                try { c = server.GetContext(); }
                catch (Exception) { return; }
                var path = c.Request.Url.AbsolutePath;
#if HAS_NEXUS
                lock (Seen) Seen.Add(path + " " + c.Request.Headers["Application-Name"]);
                bool authed = (c.Request.Headers["Authorization"] ?? "").StartsWith("Bearer ey");
                OurAuth(c.Request, ref authed);
                byte[] ours = null;
                if (authed) OurAnswer(path, ref ours);
                var m = Regex.Match(path, @"^/v1/games/helldivers2/mods/77/files/(\d+)(/download_link)?\.json$");
#endif
                int status = 200;
                byte[] body;
                if (path.StartsWith("/cdn/")) body = zips[path.Substring(5).Replace(".zip", "")];
                // GitHub: a repository whose latest release is v2.0 with a zip
                else if (path == "/gh/repos/someone/cape/releases/latest")
                    body = Bytes("{\"tag_name\": \"v2.0\", \"html_url\": \"https://github.com/someone/cape/releases/tag/v2.0\", " +
                                 "\"assets\": [{\"browser_download_url\": \"" + baseUrl + "cdn/f2.zip\"}]}");
                // this program's own releases: a draft, a pre-release, a release, an old one
                else if (path == "/gh/repos/pat-pat-patrick/hd2-mod-cli/releases")
                    body = Bytes("[{\"tag_name\": \"v9.0.0\", \"draft\": true, \"prerelease\": false, \"html_url\": \"d\"}," +
                                 " {\"tag_name\": \"v0.99.0\", \"draft\": false, \"prerelease\": true, \"html_url\": \"https://github.com/pat-pat-patrick/hd2-mod-cli/releases/tag/v0.99.0\"}," +
                                 " {\"tag_name\": \"v0.98.0\", \"draft\": false, \"prerelease\": false, \"html_url\": \"https://github.com/pat-pat-patrick/hd2-mod-cli/releases/tag/v0.98.0\"}," +
                                 " {\"tag_name\": \"v0.1.0\", \"draft\": false, \"prerelease\": false, \"html_url\": \"o\"}]");
                else if (path.StartsWith("/gh/")) { status = 404; body = Bytes("{}"); }
#if HAS_NEXUS
                // Nexus: the game's mods updated in the last month: 77, at its file 2's time
                else if (path == "/v1/games/helldivers2/mods/updated.json" && authed)
                    body = Bytes("[{\"mod_id\": 77, \"latest_file_update\": 1750000000, \"latest_mod_activity\": 1750000000}]");
                // Nexus: mod 77's files: 1 (old), replaced by 2 (main)
                else if (path == "/oauth/token" && c.Request.HttpMethod == "POST")
                {
                    var form = new StreamReader(c.Request.InputStream).ReadToEnd().Split('&').Select(p => p.Split('='))
                                   .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : ""));
                    (status, body) = OAuthToken(form);
                }
                // Nexus: the public GraphQL API (v2), asked without signing in
                else if (path == "/v2/graphql" && c.Request.HttpMethod == "POST")
                    body = Bytes(GraphQLAnswer(new StreamReader(c.Request.InputStream).ReadToEnd()));
                else if (path == "/v1/games/helldivers2/mods/77/files.json" && authed)
                    body = Bytes("{\"files\": [{\"file_id\": 1, \"version\": \"1.0\", \"category_name\": \"OLD_VERSION\", \"uploaded_timestamp\": 1700000000}, " +
                                 "{\"file_id\": 2, \"version\": \"2.0\", \"category_name\": \"MAIN\", \"uploaded_timestamp\": 1750000000}], " +
                                 "\"file_updates\": [{\"old_file_id\": 1, \"new_file_id\": 2}]}");
                else if (!authed) { status = 401; body = Bytes("{\"message\":\"bad key\"}"); }
                // mods 500 and up: the rate limit is used up
                else if (Regex.IsMatch(path, @"^/v1/games/helldivers2/mods/5\d\d/")) { status = 429; body = Bytes("{\"message\":\"rate limited\"}"); }
                else if (ours != null) body = ours;
                else if (path == "/v1/games/helldivers2/mods/77.json")
                    body = Bytes("{\"name\": \"Cape Colours on Nexus\", \"author\": \"Capey\", \"summary\": \"Capes in every colour.\", \"version\": \"2.0\"}");
                else if (m.Success && m.Groups[2].Success)
                {
                    if (c.Request.QueryString["key"] == null || c.Request.QueryString["expires"] == null) { status = 403; body = Bytes("{\"message\":\"premium only\"}"); }
                    else body = Bytes("[{\"name\": \"Nexus CDN\", \"URI\": \"" + baseUrl + "cdn/f" + m.Groups[1].Value + ".zip\"}]");
                }
                else if (m.Success) body = Bytes("{\"file_name\": \"Cape Colours-77-" + m.Groups[1].Value + ".zip\", \"name\": \"Cape\"}");
#endif
                else { status = 404; body = Bytes("{}"); }
                c.Response.StatusCode = status;
                c.Response.OutputStream.Write(body, 0, body.Length);
                c.Response.Close();
            }
        }

        public override void Dispose()
        {
            server.Stop();
            base.Dispose();
        }

#if HAS_NEXUS
        static string Link(int file, bool withKey = true) =>
            "nxm://helldivers2/mods/77/files/" + file + (withKey ? "?key=abc%2Bd&expires=1700000000&user_id=5" : "");

        string lastGraphQL;

        /// <summary>The fake GraphQL API: mod 77 is there, mod 14 is there but deleted, mod 13 makes an error, no other mod is.</summary>
        string GraphQLAnswer(string request)
        {
            lastGraphQL = request;
            var ids = Json.List(Json.Obj(Json.Parse(request) as Dictionary<string, object>, "variables"), "ids").OfType<Dictionary<string, object>>().ToList();
            long id = Json.Long(ids.FirstOrDefault(), "modId");
            if (id == 13) return "{\"errors\": [{\"message\": \"Cannot query field \\\"x\\\" on type \\\"Mod\\\".\"}]}";
            if (id == 14)
                return "{\"data\": {\"legacyModsByDomain\": {\"nodes\": [{\"modId\": 14, \"name\": \"Walker - DELETED\", \"status\": \"wastebinned\"}]}}}";
            var nodes = id != 77 ? "[]" :
                "[{\"modId\": 77, \"name\": \"Cape Colours on Nexus\", \"summary\": \"Capes in every colour.<br />Two lines.\", \"version\": \"2.0\", " +
                "\"author\": \"Capey\", \"uploader\": {\"name\": \"CapeUploader\"}, \"pictureUrl\": \"https://example.invalid/cape.png\", " +
                "\"updatedAt\": \"2026-10-01T12:00:00Z\", \"endorsements\": 1234, \"downloads\": 56789, \"adultContent\": false, \"status\": \"published\"}]";
            return "{\"data\": {\"legacyModsByDomain\": {\"nodes\": " + nodes + "}}}";
        }

        /// <summary>A mod's number as players pass it around, or its page (the user, 2026-10-29).</summary>
        [Fact]
        public void ANexusNumberIsFoundInWhatIsTyped()
        {
            foreach (var typed in new[] { "16662", " 16662 ", "#16662", "N站16662", "N站 16662", "编号16662", "16662号",
                                          "https://www.nexusmods.com/helldivers2/mods/16662", "https://www.nexusmods.com/helldivers2/mods/16662?tab=files",
                                          "nexusmods.com/HellDivers2/mods/16662" })
                Assert.Equal(16662L, Nexus.ModNumber(typed));
            foreach (var typed in new[] { "", "   ", "abc", "12 34", "0", "v1.2", "https://www.nexusmods.com/helldivers2" })
                Assert.Null(Nexus.ModNumber(typed));
            Assert.Equal("other_game", Assert.Throws<ModError>(() => Nexus.ModNumber("https://www.nexusmods.com/skyrimspecialedition/mods/266")).Kind);
        }

        [Fact]
        public void ANumberIsLookedUpWithoutSigningIn()
        {
            Assert.Null(Nexus.Credential());
            lock (Seen) Seen.Clear();
            var m = Nexus.LookUp(77);
            Assert.Equal(("Cape Colours on Nexus", "Capey", "CapeUploader", "2.0"), (m.Name, m.Author, m.Uploader, m.Version));
            Assert.Equal("Capes in every colour.\nTwo lines.", m.Summary);
            Assert.Equal((56789L, 1234L, false, true), (m.Downloads, m.Endorsements, m.Adult, m.Available));
            Assert.Equal(DateTimeOffset.Parse("2026-10-01T12:00:00Z").ToUnixTimeSeconds(), m.Updated);
            Assert.Equal("https://www.nexusmods.com/helldivers2/mods/77?tab=files", m.FilesPage);
            Assert.Contains("\"gameDomain\":\"helldivers2\"", lastGraphQL.Replace(" ", ""));
            lock (Seen) Assert.Contains("/v2/graphql " + Nexus.AppName, Seen);

            var said = Cli("nexus-info", "N站77");
            Assert.Contains("Cape Colours on Nexus  (Nexus 77)", said);
            Assert.Contains("by Capey (uploaded by CapeUploader) · version 2.0", said);
            Assert.Contains("56,789 downloads · 1,234 endorsements", said);
            Assert.Contains("files: https://www.nexusmods.com/helldivers2/mods/77?tab=files", said);
            Assert.Contains("not in the library", said);
            var lib = Library.Load();
            lib.Mods.Add(UpdatesMod());
            lib.Save();
            Assert.Contains("in the library: Cape", Cli("nexus-info", "https://www.nexusmods.com/helldivers2/mods/77"));
        }

        [Fact]
        public void ALookUpSaysWhyItFoundNothing()
        {
            Assert.Equal("nexus_missing", Refused("nexus-info", "99"));
            Assert.Equal("not_number", Refused("nexus-info", "cape"));
            // there, but not to be downloaded: said in words (Nexus Mods' own: wastebinned)
            Assert.Contains("not available: deleted", Cli("nexus-info", "14"));
            Assert.False(Nexus.LookUp(14).Available);
            Assert.Equal("other_game", Refused("nexus-info", "https://www.nexusmods.com/skyrimspecialedition/mods/266"));
            // not signed in, so nothing to fall back on: Nexus Mods' own words
            var e = Assert.Throws<ModError>(() => Nexus.LookUp(13));
            Assert.Equal("nexus_error", e.Kind);
            Assert.Contains("Cannot query field", e.Message);
        }

        [Fact]
        public void LinkIsRead()
        {
            var l = NxmLink.Parse("nxm://HellDivers2/mods/77/files/1234?key=abc%2Bd&expires=1700000000&user_id=5");
            Assert.Equal("helldivers2", l.Game);
            Assert.Equal(77, l.Mod);
            Assert.Equal(1234, l.File);
            Assert.Equal("abc+d", l.Key);
            Assert.Equal("1700000000", l.Expires);
            Assert.Throws<ModError>(() => NxmLink.Parse("https://www.nexusmods.com/helldivers2/mods/77"));
        }

        // ------------------------------------------------------------ OAuth 2.0 with PKCE (Nexus Mods' guide)

        // the fake sign-in service's own key, the challenge it was shown, the refresh tokens it honours
        static readonly System.Security.Cryptography.RSACryptoServiceProvider SigningKey = new System.Security.Cryptography.RSACryptoServiceProvider(2048);
        string shownChallenge;
        readonly HashSet<string> refreshable = new HashSet<string>();
        int issued;

        static string B64Url(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        static string Jwt(string user, bool premium)
        {
            var head = B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            var body = B64Url(Encoding.UTF8.GetBytes("{\"user\":{\"username\":\"" + user + "\",\"membership_roles\":[\"member\"" + (premium ? ",\"premium\"" : "") + "]}}"));
            var sig = SigningKey.SignData(Encoding.ASCII.GetBytes(head + "." + body), "SHA256");
            return head + "." + body + "." + B64Url(sig);
        }

        /// <summary>The test key as a SubjectPublicKeyInfo, as Nexus Mods publishes its own.</summary>
        static string Spki(System.Security.Cryptography.RSAParameters k)
        {
            byte[] Der(byte tag, byte[] v)
            {
                var len = v.Length < 0x80 ? new[] { (byte)v.Length } : v.Length < 0x100 ? new byte[] { 0x81, (byte)v.Length } : new byte[] { 0x82, (byte)(v.Length >> 8), (byte)v.Length };
                return new[] { tag }.Concat(len).Concat(v).ToArray();
            }
            byte[] Int(byte[] v) => Der(0x02, (v[0] & 0x80) != 0 ? new byte[] { 0 }.Concat(v).ToArray() : v);
            var rsa = Der(0x30, Int(k.Modulus).Concat(Int(k.Exponent)).ToArray());
            var alg = Der(0x30, new byte[] { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01, 0x05, 0x00 });
            return Convert.ToBase64String(Der(0x30, alg.Concat(Der(0x03, new byte[] { 0 }.Concat(rsa).ToArray())).ToArray()));
        }

        (int, byte[]) OAuthToken(Dictionary<string, string> form)
        {
            if (form["grant_type"] == "authorization_code")
            {
                // the code this sign-in sent, with the verifier whose challenge was shown
                if (form["code"] != "the-code" || NexusOAuth.Challenge(form["code_verifier"]) != shownChallenge || form["client_id"] != "test-client")
                    return (400, Bytes("{\"error\":\"invalid_grant\"}"));
            }
            else if (!refreshable.Remove(form["refresh_token"])) return (400, Bytes("{\"error\":\"invalid_grant\"}"));
            var refresh = "r" + (++issued);
            refreshable.Add(refresh);
            return (200, Bytes("{\"access_token\":\"" + Jwt("tester", true) + "\",\"refresh_token\":\"" + refresh + "\",\"expires_in\":21600}"));
        }

        /// <summary>RFC 7636's own example: the challenge of its verifier.</summary>
        [Fact]
        public void PkceChallengeIsTheRfcs()
        {
            Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", NexusOAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
            var v = NexusOAuth.NewVerifier();
            Assert.InRange(v.Length, 43, 128);
            Assert.NotEqual(v, NexusOAuth.NewVerifier());
        }

        /// <summary>The key Nexus Mods publishes for its tokens is read: a 2048-bit modulus, exponent 65537.</summary>
        [Fact]
        public void NexusModsKeyIsRead()
        {
            var k = NexusOAuth.KeyOf(NexusOAuth.PublicKeyPem);
            Assert.Equal(256, k.Modulus.Length);
            Assert.Equal(new byte[] { 1, 0, 1 }, k.Exponent);
        }

        /// <summary>Signing in as Nexus Mods' guide has it: the browser sent to the authorize page with
        /// an S256 challenge; a request with another state (or another path) waited past; the code
        /// exchanged with the verifier; the token verified and kept; requests then carry it; an ended
        /// token renewed; a renewal refused signs out (the user, 2026-10-27).</summary>
        [Fact]
        public void SignInWithPkce()
        {
            var keep = (NexusOAuth.PublicKeyPem, NexusOAuth.UsersBase, NexusOAuth.Port, NexusOAuth.Now);
            NexusOAuth.ForTests = "test-client";
            try
            {
                NexusOAuth.PublicKeyPem = Spki(SigningKey.ExportParameters(false));
                NexusOAuth.UsersBase = Nexus.ApiBase;
                var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                probe.Start();
                NexusOAuth.Port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                Uri opened = null;
                var (name, premium) = NexusOAuth.Login(url =>
                {
                    opened = new Uri(url);
                    var q = opened.Query.TrimStart('?').Split('&').Select(p => p.Split(new[] { '=' }, 2))
                                   .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));
                    shownChallenge = q["code_challenge"];
                    Assert.Equal("S256", q["code_challenge_method"]);
                    Assert.Equal("test-client", q["client_id"]);
                    Assert.Equal(NexusOAuth.RedirectUri, q["redirect_uri"]);
                    var back = q["redirect_uri"];
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        using (var h = new System.Net.Http.HttpClient())
                        {
                            Assert.Equal(System.Net.HttpStatusCode.NotFound, h.GetAsync(back.Replace("/callback", "/favicon.ico")).Result.StatusCode);
                            h.GetAsync(back + "?code=the-code&state=not-this-one").Wait();
                            h.GetAsync(back + "?code=the-code&state=" + q["state"]).Wait();
                        }
                    });
                }, TimeSpan.FromSeconds(20));
                Assert.Equal("/oauth/authorize", opened.AbsolutePath);
                Assert.Equal(("tester", true), (name, premium));
                Assert.StartsWith("Bearer ey", Nexus.Credential());
                Assert.DoesNotContain("tester", File.ReadAllText(Store.PathOf("nexus.json")));   // kept encrypted
                // requests carry it
                Assert.Equal("update", Updates.One(UpdatesMod()).Status);
                // six hours on: renewed with the refresh token, still signed in
                var first = Nexus.Credential();
                NexusOAuth.Now = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 21600;
                Assert.StartsWith("Bearer ey", Nexus.Credential());
                Assert.Equal(2, issued);
                // the access taken back on Nexus Mods: the renewal is refused, and it is signed out here
                refreshable.Clear();
                NexusOAuth.Now = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2 * 21600;
                var e = Assert.Throws<ModError>(() => Nexus.Credential());
                Assert.Equal("nexus_login", e.Kind);
                Assert.False(NexusOAuth.SignedIn);
                Assert.NotNull(first);
            }
            finally
            {
                (NexusOAuth.PublicKeyPem, NexusOAuth.UsersBase, NexusOAuth.Port, NexusOAuth.Now) = keep;
                NexusOAuth.ForTests = null;
            }
        }

        /// <summary>A mod from Nexus mod 77, file 1 (replaced by file 2 there).</summary>
        static Mod UpdatesMod() => new Mod { Id = "m77", Name = "Cape", Nexus = new NexusRef { Game = "helldivers2", Mod = 77, File = 1 } };
#endif

        /// <summary>Steam somewhere no guess reaches: found through SteamPath, which Steam writes
        /// with / and in lower case, and the game in a library only its libraryfolders.vdf names.</summary>
        [Fact]
        public void TheGameIsFoundThroughSteamsRegistryKey()
        {
            // a key of the test's own: the real Software\Valve\Steam is never touched
            var real = Ops.SteamKey;
            var testKey = @"Software\hd2qd-tests\steam-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Ops.SteamKey = testKey;
            try
            {
                var steam = Path.Combine(Root, "Odd Place", "Steam");
                var lib = Path.Combine(Root, "Elsewhere");
                var game = Path.Combine(lib, "steamapps", "common", "Helldivers 2");
                Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
                Directory.CreateDirectory(Path.Combine(game, "data"));
                File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                    "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace(@"\", @"\\") + "\"\n\t}\n" +
                    "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + lib.Replace(@"\", @"\\") + "\"\n\t}\n}\n");
                Assert.NotEqual(game, Ops.FindInSteam());                        // no key yet: not found there
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testKey))
                    k.SetValue("SteamPath", steam.Replace('\\', '/').ToLowerInvariant());
                Assert.Equal(game, Ops.FindInSteam(), StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                Ops.SteamKey = real;
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKey.Substring(0, testKey.LastIndexOf('\\')), false);
            }
        }







#if HAS_NEXUS
        [Fact]
        public void TheButtonIsTakenAndGivenBack()
        {
            // a key of the test's own: the real Software\Classes\nxm is never touched
            var real = Nexus.Classes;
            var testKey = @"Software\hd2qd-tests\nxm-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Nexus.Classes = testKey;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testKey + @"\shell\open\command"))
                    k.SetValue("", "\"C:\\Program Files\\Other\\Other.exe\" \"%1\"");
                // the window's exe, and a copy of it elsewhere (a new build, a release folder)
                var exe = Path.Combine(Root, "release", "HD2 Mod Quick Deploy.exe");
                var copy = Path.Combine(Root, "bin", "HD2 Mod Quick Deploy.exe");
                foreach (var p in new[] { exe, copy }) { Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, "exe"); }
                Assert.False(Nexus.IsOurs(exe));
                Nexus.TakeOver(exe);
                Assert.True(Nexus.IsOurs(exe));
                Assert.True(Nexus.IsOurs(copy));                                  // the same program, run from elsewhere
                Assert.Equal("\"" + exe + "\" \"%1\"", Nexus.Handler());
                Assert.Equal("\"C:\\Program Files\\Other\\Other.exe\" \"%1\"", Nexus.Previous());
                Nexus.TakeOver(exe);                                             // again: the one before is still Other
                Assert.Equal("\"C:\\Program Files\\Other\\Other.exe\" \"%1\"", Nexus.Previous());
                File.Delete(exe);
                Assert.False(Nexus.IsOurs(copy));                                 // the registered one is gone: not ours
                Nexus.GiveBack();
                Assert.Equal("\"C:\\Program Files\\Other\\Other.exe\" \"%1\"", Nexus.Handler());
            }
            finally
            {
                Nexus.Classes = real;
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKey.Substring(0, testKey.LastIndexOf('\\')), false);
            }
        }
#endif


        /// <summary>A mod from GitHub: checked against the repository's latest release, and
        /// updated in place with one command.</summary>
        [Fact]
        public void GitHubUpdatesAreCheckedAndApplied()
        {
            var gh = Path.Combine(Root, "gh");
            File.WriteAllBytes(gh + ".zip", zips["f1"]);
            Cli("add", gh + ".zip", "--name", "From GitHub");
            Cli("source", "From GitHub", "https://github.com/someone/cape/releases/tag/v1.0");
            Cli("add", Mod("Nowhere", Files((B + ".patch_0", "x"))));
            var r = Updates.Check(Library.Load()).ToDictionary(x => x.Name);
            Assert.Equal("update", r["From GitHub"].Status);
            Assert.Equal("v1.0", r["From GitHub"].Current);
            Assert.Equal("v2.0", r["From GitHub"].Latest);
            Assert.Equal("no_source", r["Nowhere"].Status);
            Assert.Contains("updated: Cape Colours v2.0", Cli("update", "From GitHub"));
            var updated = Library.Load().Mods.First(m => m.GitHub != null);
            Assert.Equal("v2.0", updated.GitHub.Tag);
            Assert.Equal("cape v2", File.ReadAllText(Path.Combine(updated.Path, A + ".patch_0")));
            Assert.Equal("current", Updates.Check(Library.Load()).First(x => x.ModId == updated.Id).Status);
        }

        /// <summary>This program's own updates (the user, 2026-10-25): the development build is
        /// published as pre-releases, the official build as releases. The official build is never
        /// offered a pre-release (a build without Nexus Mods); a draft counts for neither.</summary>
        [Fact]
        public void ThisProgramsOwnReleasesAreCheckedByBuild()
        {
            var list = ((List<object>)Json.Parse(System.Text.Encoding.UTF8.GetString(new System.Net.WebClient().DownloadData(
                Updates.GitHubApi + "/repos/" + SelfUpdate.Repo + "/releases")))).OfType<Dictionary<string, object>>().ToList();
            var official = SelfUpdate.Pick(list, "0.8.0", true);
            Assert.Equal("v0.98.0", official.Latest);
            Assert.False(official.Prerelease);
            Assert.True(official.Newer);
            var dev = SelfUpdate.Pick(list, "0.8.0", false);
            Assert.Equal("v0.99.0", dev.Latest);
            Assert.True(dev.Prerelease);
            // the version running is the newest: nothing to offer
            Assert.False(SelfUpdate.Pick(list, "0.99.0", false).Newer);
            Assert.False(SelfUpdate.Pick(list, "1.0", true).Newer);
            Assert.Null(SelfUpdate.Pick(new List<Dictionary<string, object>>(), "0.8.0", true).Latest);

            // checked through the command line: the build's own rule, the result kept
            Assert.Equal(0, SelfUpdate.Last().CheckedAt);
            Assert.True(SelfUpdate.Due());
            Assert.Contains((SelfUpdate.Official ? "v0.98.0 is out: " : "v0.99.0 is out (pre-release): ") + "https://github.com/", Cli("self-update"));
            var last = SelfUpdate.Last();
            Assert.Equal(SelfUpdate.Official ? "v0.98.0" : "v0.99.0", last.Latest);
            Assert.True(last.Newer);
            Assert.True(last.CheckedAt > 0);
            Assert.False(SelfUpdate.Due());                  // once a day
            // a pre-release a development build kept in the same folder: nothing to the official
            // build, which checks again rather than wait a day
            Store.Save("self_update.json", new Dictionary<string, object> { ["checked_at"] = last.CheckedAt, ["latest"] = "v5.0.0", ["prerelease"] = true, ["page"] = "p" });
            Assert.Equal(!SelfUpdate.Official, SelfUpdate.Last().Newer);
            Assert.Equal(SelfUpdate.Official, SelfUpdate.Due());
            // the automatic check can be turned off; on by default
            SelfUpdate.Auto = false;
            Assert.False(SelfUpdate.Auto);
            SelfUpdate.Auto = true;
            Assert.True(SelfUpdate.Auto);
        }

        [Fact]
        public void AModsVersionIsTheRecordedOneElseOneInItsNames()
        {
            (string, string) Of(string name, string path, NexusRef nx = null, GitHubRef gh = null) =>
                Origins.VersionOf(new Mod { Name = name, Path = path, Nexus = nx, GitHub = gh });
            Assert.Equal(("4", "nexus"), Of("ESC Squad Ping v0.3.6", @"X:\m\a", new NexusRef { Version = "4", File = 9 }));  // what was downloaded
            // a Nexus version with no file and no time was the page's newest, written in by an
            // older match: not the installed version
            Assert.Equal(("0.3.6", "name"), Of("ESC Squad Ping v0.3.6", @"X:\m\a", new NexusRef { Version = "4" }));
            Assert.Equal(("v2.0", "github"), Of("Cape", @"X:\m\a", gh: new GitHubRef { Tag = "v2.0" }));
            Assert.Equal(("11", "file"), Of("HD2 SmoothBoot", @"X:\m\HD2 SmoothBoot 3.0.50 16727 11 2026-10-11T07-02Z abc"));
            Assert.Equal(("1.136.1", "name"), Of("Super Holo HUD v1.136.1", @"X:\m\Super Holo HUD v1.136.1_cdae72a9f188"));
            Assert.Equal(("4.9.28", "folder"), Of("战备冷却", @"X:\m\StratagemCooldown-4.9.28_AR129985"));
            Assert.Equal(("0.6.1", "folder"), Of("Native CarpetBomb", @"X:\m\NativeCarpetBomb_v0.6.1_AR176602"));
            Assert.Equal(((string)null, (string)null), Of("[Violet] Ines-24", @"X:\m\[Violet] Ines-24 (ver.28-4)_AR825256"));   // not a version
            Assert.Equal(((string)null, (string)null), Of("Fog 2026-07-27", @"X:\m\Leveller 3D Reticle 1_AR871371"));
            // the files replaced since the download: the folder's version, not the record's
            Assert.Equal(("0.9.0", "folder"), Of("BML", @"X:\m\Boot Mod Loader v0.9.0_4c5d85be41af", new NexusRef { Version = "0.7.0", File = 9 }));
            // the same version written two ways is no replacement
            Assert.Equal(("v2.0", "github"), Of("Cape", @"X:\m\Cape 2.0_0123456789ab", gh: new GitHubRef { Tag = "v2.0" }));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        /// <summary>A file's Zone.Identifier, as a browser writes it for a download.</summary>
        internal static void Zone(string file, string host, string referrer)
        {
            using (var h = CreateFile(file + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero))
            using (var fs = new FileStream(h, FileAccess.Write))
            using (var w = new StreamWriter(fs))
                w.Write("[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=" + referrer + "\r\nHostUrl=" + host + "\r\n");
        }

        [Fact]
        public void ADownloadSaysWhereItCameFrom()
        {
            var a = Path.Combine(Root, "Helmet Headlamp 16320 1.6.0 2026-10-23T09-47Z 5V4g.zip");
            File.WriteAllBytes(a, zips["f1"]);
            Zone(a, "https://cf-files.nexusmods.com/cdn/b6/48/ec/x?md5=1", "https://www.nexusmods.com/");
            var g = Path.Combine(Root, "release.zip");
            File.WriteAllBytes(g, zips["f2"]);
            Zone(g, "https://objects.githubusercontent.com/x", "https://github.com/someone/cape/releases/download/v1.5/release.zip");
            var w = Path.Combine(Root, "other.zip");
            File.WriteAllBytes(w, zips["f2"]);
            Zone(w, "https://ayakamods.com/mods/x.1/download", "https://ayakamods.com/mods/x.1/");
            var m1 = new Mod(); Origins.FromFile(m1, a);
            Assert.Equal(16320, m1.Nexus.Mod);
            Assert.Equal("1.6.0", m1.Nexus.Version);
            Assert.Equal(new DateTimeOffset(2026, 10, 23, 9, 47, 0, TimeSpan.Zero).ToUnixTimeSeconds(), m1.Nexus.Time);
            var m2 = new Mod(); Origins.FromFile(m2, g);
            Assert.Equal("someone/cape@v1.5", m2.GitHub.Owner + "/" + m2.GitHub.Repo + "@" + m2.GitHub.Tag);
            var m3 = new Mod(); Origins.FromFile(m3, w);
            Assert.Null(m3.Nexus);
            Assert.Null(m3.GitHub);
            Assert.Equal("https://ayakamods.com/mods/x.1/", m3.Page);
            var m4 = new Mod(); Origins.FromFile(m4, Path.Combine(Root, "no-such.zip"));
            Assert.Null(m4.Nexus ?? (object)m4.GitHub ?? m4.Page);
        }

        [Fact]
        public void SevenZipIsUnpackedByWindowsTar()
        {
            var src = Path.Combine(Root, "seven");
            Directory.CreateDirectory(Path.Combine(src, "Thing"));
            File.WriteAllText(Path.Combine(src, "Thing", A + ".patch_0"), "seven");
            var archive = Path.Combine(Root, "Thing.7z");
            var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tar, "--format 7zip -cf \"" + archive + "\" -C \"" + src + "\" Thing")
                   { UseShellExecute = false, CreateNoWindow = true }))
                p.WaitForExit();
            Assert.True(File.Exists(archive));
            Cli("add", archive);
            Assert.Equal(new[] { "seven" }, PlanList());
        }

        /// <summary>A 7z of incompressible bytes, so that cutting it cuts the data and the index.</summary>
        string SevenZip(string name)
        {
            var src = Path.Combine(Root, "seven-" + name);
            Directory.CreateDirectory(Path.Combine(src, "Thing"));
            var bytes = new byte[200000];
            new Random(7).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(src, "Thing", A + ".patch_0"), bytes);
            var archive = Path.Combine(Root, name + ".7z");
            var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tar, "--format 7zip -cf \"" + archive + "\" -C \"" + src + "\" Thing")
                   { UseShellExecute = false, CreateNoWindow = true }))
                p.WaitForExit();
            return archive;
        }

        /// <summary>Must-fail and must-pass (the user, 2026-11-01: 7z files failed to unpack now
        /// and then): a 7z cut short is refused as incomplete, before tar ("(null)") is asked;
        /// the whole one is unpacked.</summary>
        [Fact]
        public void A_cut_off_7z_is_refused_as_incomplete()
        {
            var whole = SevenZip("Whole");
            Assert.Null(Ops.Damaged(whole));
            var cut = Path.Combine(Root, "Cut.7z");
            var bytes = File.ReadAllBytes(whole);
            File.WriteAllBytes(cut, bytes.Take(bytes.Length / 2).ToArray());
            Assert.Contains("cut off", Ops.Damaged(cut));
            var e = new StringWriter();
            Assert.Equal(1, Commands.Run(new[] { "add", cut }, new StringWriter(), e));
            Assert.Contains("cut off", e.ToString());
            Assert.Empty(Library.Load().Mods);
            Assert.False(Directory.Exists(Store.ModsDir()) && Directory.GetDirectories(Store.ModsDir()).Length > 0, "nothing is unpacked");
            Cli("add", whole);
            Assert.Single(Library.Load().Mods);
        }

        /// <summary>A zip cut short has no directory: refused as incomplete, never handed to tar,
        /// which unpacks what it can read and says nothing (bsdtar 3.7.7, 2026-11-01).</summary>
        [Fact]
        public void A_cut_off_zip_is_refused_as_incomplete()
        {
            var whole = Path.Combine(Root, "Whole.zip");
            File.WriteAllBytes(whole, Zip((A + ".patch_0", new string('z', 50000)), (A + ".patch_1", "b")));
            Assert.Null(Ops.Damaged(whole));
            var cut = Path.Combine(Root, "Cut.zip");
            var bytes = File.ReadAllBytes(whole);
            File.WriteAllBytes(cut, bytes.Take(bytes.Length - 30).ToArray());
            Assert.NotNull(Ops.Damaged(cut));
            CliCode(1, "add", cut);
            Assert.Empty(Library.Load().Mods);
        }
    }

    public class LibraryOps : Fixture
    {
        [Fact]
        public void AddingANewVersionWithTheSameManifestIdentityReplacesTheEntry()
        {
            const string identity = "3f7a2c91-6d4e-4b58-9a0c-e15b82d4f6a7";
            var first = Path.Combine(Root, "loader-old.zip");
            var next = Path.Combine(Root, "loader-new.zip");
            Zip(first, ("manifest.json", "{\"Guid\":\"" + identity + "\",\"Name\":\"Loader v1\"}"), (A + ".patch_0", "old"));
            Zip(next, ("manifest.json", "{\"Guid\":\"" + identity + "\",\"Name\":\"Loader v2\"}"), (A + ".patch_0", "new"));
            Cli("add", first);
            var original = Library.Load().Mods.Single();
            Cli("rename", original.Id, "My loader");
            Cli("disable", original.Id);
            Cli("group-new", "Scripts");
            Cli("group-put", original.Id, "--in", "Scripts");
            Cli("tag", original.Id, "dev");
            var group = Library.Load().Mods.Single().Group;
            Assert.Contains("replaced:", Cli("add", next));
            var replaced = Assert.Single(Library.Load().Mods);
            Assert.Equal(original.Id, replaced.Id);
            Assert.Equal("My loader", replaced.Name);
            Assert.Equal("Loader v2", replaced.RealName);
            Assert.False(replaced.Enabled);
            Assert.Equal(group, replaced.Group);
            Assert.Contains("dev", replaced.Tags);
            Assert.Equal("new", File.ReadAllText(Path.Combine(replaced.Path, A + ".patch_0")));
            Cli("add", next, "--name", "Explicit variant");
            Assert.Equal(2, Library.Load().Mods.Count);
        }

        static void Zip(string path, params (string Name, string Body)[] entries)
        {
            using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var (name, body) in entries)
                    using (var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false)))
                        w.Write(body);
        }

        static string CamManifest(params string[] subs) =>
            "{\"Name\": \"Cam\", \"Options\": [{\"Name\": \"Look\", \"SubOptions\": [" +
            string.Join(", ", subs.Select(s => "{\"Name\": \"" + s + "\", \"Include\": [\"" + s.ToLowerInvariant() + "\"]}")) + "]}]}";

        [Fact]
        public void ZipWithATopFolderAndReplaceKeepsOptions()
        {
            var z = Path.Combine(Root, "Cam.zip");
            Zip(z, ("Cam/manifest.json", CamManifest("Near", "Far")), ("Cam/near/" + A + ".patch_0", "near"), ("Cam/far/" + A + ".patch_0", "far"));
            Cli("add", z);
            Cli("set", "Cam", "1.2", "on", "--only");
            var z2 = Path.Combine(Root, "Cam2.zip");
            Zip(z2, ("manifest.json", CamManifest("Near", "Far", "Mid")), ("near/" + A + ".patch_0", "near2"),
                ("far/" + A + ".patch_0", "far2"), ("mid/" + A + ".patch_0", "mid2"));
            Cli("replace", "Cam", z2);
            Assert.Equal(new[] { "far2" }, PlanList());   // the sub-option chosen before is kept by name
        }

        [Fact]
        public void ZipThatLeavesItsFolderIsRefused()
        {
            var z = Path.Combine(Root, "Evil.zip");
            Zip(z, ("../escaped.txt", "x"), (A + ".patch_0", "a"));
            CliCode(1, "add", z);
            Assert.False(File.Exists(Path.Combine(Store.ModsDir(), "escaped.txt")));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Store.ModsDir()), "escaped.txt")));
        }

        [Fact]
        public void ImportArsenal()
        {
            var one = Mod("One", Files((A + ".patch_0", "one")));
            var two = Mod("Two", Files(("a/" + A + ".patch_0", "two-a"), ("b/" + A + ".patch_0", "two-b")),
                          "{\"Name\": \"Two\", \"Options\": [{\"Name\": \"Pick\", \"SubOptions\": [" +
                          "{\"Name\": \"A\", \"Include\": [\"a\"]}, {\"Name\": \"B\", \"Include\": [\"b\"]}]}]}");
            var arsenal = Path.Combine(Root, "hd2a_data.json");
            var text = J(new Dictionary<string, object>
            {
                ["deployedProfile"] = "p", ["userGameDir"] = Game, ["modsList"] = new Dictionary<string, object>
                {
                    ["p"] = new Dictionary<string, object>
                    {
                        ["label"] = "Main", ["mods"] = new List<object>
                        {
                            new Dictionary<string, object> { ["uuid"] = "u1", ["label"] = "One", ["path"] = one, ["enabled"] = false, ["options"] = new List<object>() },
                            new Dictionary<string, object>
                            {
                                ["uuid"] = "u2", ["label"] = "Two", ["path"] = two, ["enabled"] = true, ["tags"] = new List<object> { "misc", "vfx", "vehicle sfx" },
                                ["nexusData"] = new Dictionary<string, object> { ["modId"] = "555", ["fileId"] = "9", ["version"] = "1.2", ["updateTimestamp"] = 1700000000L },
                                ["options"] = new List<object>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["name"] = "Pick", ["enabled"] = true, ["include"] = new List<object>(), ["suboptions"] = new List<object>
                                        {
                                            new Dictionary<string, object> { ["name"] = "A", ["enabled"] = false, ["include"] = new List<object> { "a" } },
                                            new Dictionary<string, object> { ["name"] = "B", ["enabled"] = true, ["include"] = new List<object> { "b" } },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            });
            File.WriteAllText(arsenal, text, new UTF8Encoding(false));
            Cli("import-arsenal", "--file", arsenal);
            Assert.Equal(new[] { "two-b" }, PlanList());
            var nx = Library.Load().Mods.First(m => m.Name == "Two").Nexus;   // HD2 Arsenal's nexusData comes along
            Assert.Equal("555/9/1.2/1700000000", nx.Mod + "/" + nx.File + "/" + nx.Version + "/" + nx.Time);
            // its tags, as Arsenal shows them (the user, 2026-10-25)
            Assert.Equal(new[] { "Misc", "VFX", "Vehicle SFX" }, Library.Load().Mods.First(m => m.Name == "Two").Tags.ToArray());
            Assert.Empty(Library.Load().Mods.First(m => m.Name == "One").Tags);
            Assert.Equal(text, File.ReadAllText(arsenal));   // Arsenal's file untouched
            // with Arsenal's "first mod highest priority" on, its top mod wins: here that is the
            // last one, so the list comes in reversed (proven on Arsenal 0.36.2, 2026-10-25)
            Assert.Equal(new[] { "One", "Two" }, Ops.ImportArsenal(arsenal).Lib.Mods.Select(m => m.Name).ToArray());
            File.WriteAllText(arsenal, text.Replace("\"deployedProfile\"", "\"setTopPriority\": true, \"deployedProfile\""), new UTF8Encoding(false));
            Assert.Equal(new[] { "Two", "One" }, Ops.ImportArsenal(arsenal).Lib.Mods.Select(m => m.Name).ToArray());
            // and the import says so (the user, 2026-10-25), only when it is on
            Assert.Contains("the list came in the other way round", Cli("import-arsenal", "--file", arsenal, "--replace"));
            File.WriteAllText(arsenal, text, new UTF8Encoding(false));
            Assert.DoesNotContain("other way round", Cli("import-arsenal", "--file", arsenal, "--replace"));
        }

        /// <summary>A manager's list found but not read is said, never left out as if there were none
        /// (the user, 2026-10-26: a newer Arsenal's list read as empty, and Arsenal was not offered).</summary>
        [Fact]
        public void AListFoundButNotReadIsSaid()
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            var roaming = Environment.GetEnvironmentVariable("APPDATA");
            try
            {
                Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(Root, "local"));
                Environment.SetEnvironmentVariable("APPDATA", Path.Combine(Root, "roaming"));
                var file = Path.Combine(Root, "local", "hd2arsenal", "hd2a_data.json");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                // a shape of the future: entries that name their mods some other way
                File.WriteAllText(file, J(new Dictionary<string, object>
                {
                    ["librarySystemVersion"] = 9L, ["deployedProfile"] = "p", ["modsList"] = new Dictionary<string, object>
                    {
                        ["p"] = new Dictionary<string, object> { ["label"] = "Main", ["mods"] = new List<object>
                        {
                            new Dictionary<string, object> { ["ref"] = "x1", ["enabled"] = true },
                            new Dictionary<string, object> { ["ref"] = "x2", ["enabled"] = true },
                        } },
                    },
                }), new UTF8Encoding(false));
                Assert.Equal(1, Commands.Run(new[] { "import-arsenal" }, new StringWriter(), new StringWriter(), out var refused));
                Assert.Equal("bad_format", refused.Kind);
                var found = Importers.Found().Single(x => x.Key == "arsenal");
                Assert.Equal("bad_format", found.ProblemKind);
                Assert.Equal(0, found.Mods);
                // a library folder that is Arsenal's store: the problem said, no file asked for, nothing loose
                var store = Path.Combine(Root, "Arsenal Mods");
                foreach (var n in new[] { "One_AR123456", "Two_AR654321" })
                {
                    Directory.CreateDirectory(Path.Combine(store, n));
                    File.WriteAllText(Path.Combine(store, n, A + ".patch_0"), n);
                }
                var finds = Storage.Look(store, Library.Load());
                Assert.Null(finds.NeedsConfig);
                Assert.Single(finds.Problems);
                Assert.Empty(finds.Loose);
                Assert.Contains("cannot be read", Cli("folder-add", store));
            }
            finally
            {
                Environment.SetEnvironmentVariable("LOCALAPPDATA", local);
                Environment.SetEnvironmentVariable("APPDATA", roaming);
            }
        }

        /// <summary>A folder of mods given as one mod (another manager's store) is refused as a
        /// library folder; --whole takes it as one all the same (players, 2026-10-26).</summary>
        [Fact]
        public void AFolderOfModsIsALibraryFolder()
        {
            var store = Path.Combine(Root, "store");
            foreach (var n in new[] { "One", "Two" })
            {
                Directory.CreateDirectory(Path.Combine(store, n));
                File.WriteAllText(Path.Combine(store, n, A + ".patch_0"), n);
            }
            Assert.Equal(1, Commands.Run(new[] { "add", store }, new StringWriter(), new StringWriter(), out var refused));
            Assert.Equal("library_folder", refused.Kind);
            Assert.Empty(Library.Load().Mods);
            Assert.Contains("added", Cli("add", store, "--whole"));
            // a mod with its options in folders is a mod, not a library folder
            var mod = Mod("Opts", Files(("a/" + A + ".patch_0", "a"), ("b/" + A + ".patch_0", "b")),
                          "{\"Name\": \"Opts\", \"Options\": [{\"Name\": \"A\", \"Include\": [\"a\"]}]}");
            Assert.Contains("added", Cli("add", mod));
        }

        /// <summary>Arsenal 0.37 (librarySystemVersion 1): the mods in modsLibrary, a profile naming
        /// them by uuid with its own on/off and choices (optionsConfig), and separators between them.
        /// Read as the old shape it gave no mods, so Arsenal was not offered at all (a player's file,
        /// 2026-10-26).</summary>
        [Fact]
        public void ImportArsenalLibraryFormat()
        {
            var one = Mod("One", Files((A + ".patch_0", "one")));
            var two = Mod("Two", Files(("a/" + A + ".patch_0", "two-a"), ("b/" + A + ".patch_0", "two-b")),
                          "{\"Name\": \"Two\", \"Options\": [{\"Name\": \"Pick\", \"SubOptions\": [" +
                          "{\"Name\": \"A\", \"Include\": [\"a\"]}, {\"Name\": \"B\", \"Include\": [\"b\"]}]}]}");
            object Sub(string name, bool on) => new Dictionary<string, object> { ["name"] = name, ["enabled"] = on };
            var arsenal = Path.Combine(Root, "hd2a_data.json");
            File.WriteAllText(arsenal, J(new Dictionary<string, object>
            {
                ["librarySystemVersion"] = 1L, ["deployedProfile"] = "default", ["userGameDir"] = Game,
                ["modsLibrary"] = new List<object>
                {
                    new Dictionary<string, object> { ["uuid"] = "u1", ["label"] = "One", ["path"] = one, ["tags"] = new List<object> { "misc" }, ["options"] = new List<object>() },
                    new Dictionary<string, object>
                    {
                        ["uuid"] = "u2", ["label"] = "Two", ["path"] = two,
                        ["nexusData"] = new Dictionary<string, object> { ["modId"] = "555", ["fileId"] = "9", ["version"] = "1.2", ["updateTimestamp"] = 1700000000L },
                        // the library's own defaults: A; the profile chooses B
                        ["options"] = new List<object> { new Dictionary<string, object> { ["name"] = "Pick", ["enabled"] = true, ["suboptions"] = new List<object> { Sub("A", true), Sub("B", false) } } },
                    },
                },
                ["modsList"] = new Dictionary<string, object>
                {
                    ["default"] = new Dictionary<string, object>
                    {
                        ["label"] = "Default Profile", ["mods"] = new List<object>
                        {
                            new Dictionary<string, object> { ["uuid"] = "separator-1", ["type"] = "separator", ["label"] = "Heading" },
                            new Dictionary<string, object> { ["uuid"] = "u1", ["enabled"] = false, ["optionsConfig"] = new List<object>() },
                            new Dictionary<string, object>
                            {
                                ["uuid"] = "u2", ["enabled"] = true,
                                ["optionsConfig"] = new List<object> { new Dictionary<string, object> { ["name"] = "Pick", ["enabled"] = true, ["suboptions"] = new List<object> { Sub("A", false), Sub("B", true) } } },
                            },
                            new Dictionary<string, object> { ["uuid"] = "gone", ["enabled"] = true },          // in no library: no mod
                        },
                    },
                },
            }), new UTF8Encoding(false));
            var read = Ops.ImportArsenal(arsenal).Lib.Mods;
            Assert.Equal(new[] { "One", "Two" }, read.Select(m => m.Name).ToArray());
            Assert.Equal(new[] { false, true }, read.Select(m => m.Enabled).ToArray());
            Cli("import-arsenal", "--file", arsenal);
            Assert.Equal(new[] { "two-b" }, PlanList());                        // the profile's choice, not the library's default
            var nx = Library.Load().Mods.First(m => m.Name == "Two").Nexus;
            Assert.Equal("555/9", nx.Mod + "/" + nx.File);
            Assert.Equal(new[] { "Misc" }, Library.Load().Mods.First(m => m.Name == "One").Tags.ToArray());
        }

        /// <summary>Every profile of another manager comes in as a profile of its own, with its
        /// mods, order and on/off (the user, 2026-10-25). The library's own mods stay a profile
        /// Default, which stays in use; a name taken already gets the manager's after it.</summary>
        [Fact]
        public void OtherManagersProfilesComeInAsProfiles()
        {
            Cli("add", Mod("Mine", Files(("1111222233334444.patch_0", "mine"))));
            var one = Mod("One", Files((A + ".patch_0", "one")));
            var two = Mod("Two", Files((B + ".patch_0", "two")));
            Dictionary<string, object> Entry(string uuid, string label, string path, bool on) =>
                new Dictionary<string, object> { ["uuid"] = uuid, ["label"] = label, ["path"] = path, ["enabled"] = on, ["options"] = new List<object>() };
            var arsenal = Path.Combine(Root, "hd2a_data.json");
            File.WriteAllText(arsenal, J(new Dictionary<string, object>
            {
                ["deployedProfile"] = "q", ["profileOrder"] = new List<object> { "p", "q" }, ["userGameDir"] = Game, ["modsList"] = new Dictionary<string, object>
                {
                    ["p"] = new Dictionary<string, object> { ["label"] = "Main", ["mods"] = new List<object> { Entry("u1", "One", one, true), Entry("u2", "Two", two, false) } },
                    ["q"] = new Dictionary<string, object> { ["label"] = "Bunny", ["mods"] = new List<object> { Entry("u3", "Two", two, true) } },
                },
            }), new UTF8Encoding(false));
            Cli("import-arsenal", "--file", arsenal, "--profiles");
            var lib = Library.Load();
            string Name(string id) => lib.Mods.First(m => m.Id == id).Name;
            string Show(string profile)
            {
                var p = lib.Profiles.First(x => x.Name == profile);
                return string.Join(" ", p.Order.Select(id => Name(id) + (p.On.Contains(id) ? "+" : "-")));
            }
            Assert.Equal(new[] { "Default", "Main", "Bunny" }, lib.Profiles.Select(p => p.Name).ToArray());
            Assert.Equal("Default", lib.Active);                       // an import never changes the game on its own
            Assert.Equal(new[] { "mine" }, PlanList());
            Assert.Equal("One+ Two-", Show("Main"));
            Assert.Equal("Two+", Show("Bunny"));
            Assert.Equal(3, lib.Mods.Count);                           // Two once, though in both profiles

            // Echelon: its "Main" meets ours
            var dir = Path.Combine(Root, "Echelon");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "library.json");
            var three = Mod("Three", Files(("5555666677778888.patch_0", "three")));
            string Mod_(string id, string name, string folder) => "{\"id\": \"" + id + "\", \"name\": \"" + name + "\", \"folder\": " +
                                                                  Json.Write(folder) + ", \"enabled\": true, \"deployed\": true, \"sets\": [], \"options\": []}";
            File.WriteAllText(file, "{\"format\": \"echelon-library\", \"version\": 1, \"mods\": [" +
                Mod_("e1", "One", one) + ", " + Mod_("e3", "Three", three) + "]}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "state.json"),
                "{\"version\": 2, \"active_profile\": \"p2\", \"profiles\": [" +
                "{\"id\": \"p1\", \"name\": \"Other\", \"entries\": [{\"type\": \"mod\", \"id\": \"e3\", \"enabled\": true}]}," +
                "{\"id\": \"p2\", \"name\": \"Main\", \"entries\": [{\"type\": \"mod\", \"id\": \"e3\", \"enabled\": false}, {\"type\": \"mod\", \"id\": \"e1\", \"enabled\": true}]}]}",
                new UTF8Encoding(false));
            Cli("import-echelon", "--file", file, "--profiles");
            lib = Library.Load();
            Assert.Equal(new[] { "Default", "Main", "Bunny", "Other", "Main (Echelon)" }, lib.Profiles.Select(p => p.Name).ToArray());
            Assert.Equal("Default", lib.Active);
            Assert.Equal("Three- One+", Show("Main (Echelon)"));       // One is the mod Arsenal brought, not a second copy
            Assert.Equal(4, lib.Mods.Count);
            // the mods the imports brought are in the library, not in the profile in use
            Assert.Equal(new[] { "Mine" }, lib.Mods.Where(m => Profiles.InProfile(lib, m)).Select(m => m.Name).ToArray());
        }

        /// <summary>Mods from another manager meet the ones already in the library for the first
        /// time: the import says how many pairs of them conflict, and only then (the user,
        /// 2026-10-25).</summary>
        [Fact]
        public void ImportSaysWhereManagersConflict()
        {
            Cli("add", Mod("One", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("one's own"), (1, 100), (1, 101)) }));
            string Echelon(string name, params (ulong, ulong)[] resources)
            {
                var folder = Mod(name, new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes(name + "'s own"), resources) });
                var dir = Path.Combine(Root, "Echelon-" + name);
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "library.json");
                File.WriteAllText(file, "{\"format\": \"echelon-library\", \"version\": 1, \"mods\": [{\"id\": \"e1\", \"name\": " + Json.Write(name) +
                    ", \"folder\": " + Json.Write(folder) + ", \"enabled\": true, \"deployed\": true, \"sets\": [{\"hash\": \"" + A +
                    "\", \"files\": [\"" + A + ".patch_0\"], \"deployed_as\": null}], \"options\": []}]}", new UTF8Encoding(false));
                return file;
            }
            // one resource in common with One, different bytes
            Assert.Contains("1 conflict between mods from Echelon and mods already in the library", Cli("import-echelon", "--file", Echelon("Two", (1, 101), (2, 7))));
            // nothing in common: nothing said
            Assert.DoesNotContain("conflict", Cli("import-echelon", "--file", Echelon("Three", (3, 1))));
        }

        [Fact]
        public void ConflictsNameTheWinner()
        {
            Cli("add", Mod("One", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("one's own"), (1, 100), (1, 101)) }));
            Cli("add", Mod("Two", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("two's own"), (1, 101), (2, 7)) }));
            Assert.Contains("1 resource(s) in One / Two -- Two wins", Cli("conflicts"));
        }

        /// <summary>A conflict says which resources it is about (the user, 2026-10-25): the type by
        /// the engine's name, and the name when HD2SDK's list is installed; without the list, the id.</summary>
        [Fact]
        public void ConflictsNameTheirResources()
        {
            const ulong Unit = 0xe0a48d0be9a7453f, Odd = 0x1234;
            Cli("add", Mod("One", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("one's own"), (Unit, 101), (Odd, 7)) }));
            Cli("add", Mod("Two", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(Bytes("two's own"), (Unit, 101), (Odd, 7)) }));
            var list = Path.Combine(Root, "friendlynames.txt");
            File.WriteAllText(list, "101 Laser Sight W/ Flashlight\n102 Something Else\nnot a line\n", new UTF8Encoding(false));
            ResourceNames.ListOverride = list;
            try
            {
                var said = Cli("conflicts", "-v");
                Assert.Contains("unit", said);
                Assert.Contains("Laser Sight W/ Flashlight", said);
                Assert.Contains("0000000000001234", said);                  // a type not known: its hash
                var c = ((List<object>)((Dictionary<string, object>)Json.Parse(Cli("state")))["conflicts"]).OfType<Dictionary<string, object>>().Single();
                var res = ((List<object>)c["resources"]).OfType<Dictionary<string, object>>().ToDictionary(r => (string)r["id"]);
                Assert.Equal("unit", res["0000000000000065"]["type"]);
                Assert.Equal("Laser Sight W/ Flashlight", res["0000000000000065"]["name"]);
                Assert.Null(res["0000000000000007"]["name"]);
                Assert.True((bool)((Dictionary<string, object>)Json.Parse(Cli("state")))["resource_names"]);
                // no list: the ids, and the state says names cannot be given
                ResourceNames.ListOverride = Path.Combine(Root, "missing.txt");
                Assert.DoesNotContain("Laser Sight", Cli("conflicts", "-v"));
                Assert.False((bool)((Dictionary<string, object>)Json.Parse(Cli("state")))["resource_names"]);
            }
            finally { ResourceNames.ListOverride = null; }
        }

        /// <summary>The same resource, byte for byte, in both mods (and at a different place in
        /// each archive): shared, not a conflict — whichever loads last, the game gets the same
        /// bytes (the user, 2026-10-24).</summary>
        [Fact]
        public void TheSameResourceInTwoModsIsNoConflict()
        {
            var shared = Bytes("the very same resource");
            Cli("add", Mod("One", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(shared, (1, 100), (1, 101)) }));
            Cli("add", Mod("Two", new Dictionary<string, byte[]> { [A + ".patch_0"] = Archive(shared, (1, 101), (2, 7)) }));
            var said = Cli("conflicts");
            Assert.Contains("1 resource(s) in One / Two -- the very same bytes", said);
            Assert.DoesNotContain("wins", said);
        }

        /// <summary>A name of the player's own (the user, 2026-10-24): the mod answers to it, the
        /// name it came with is kept, and giving that name back drops it again.</summary>
        [Fact]
        public void RenameKeepsTheNameItCameWith()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("rename", "One", "My HUD");
            var mod = Library.Load().Mods[0];
            Assert.Equal("My HUD", mod.Name);
            Assert.Equal("One", mod.RealName);
            Cli("rename", "My HUD", "One");
            Assert.Null(Library.Load().Mods[0].RealName);
        }

        /// <summary>Labels of the player's own: put on, renamed everywhere, taken off, deleted
        /// (the user, 2026-10-24).</summary>
        [Fact]
        public void LabelsAreThePlayersOwn()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", Files((B + ".patch_0", "two"))));
            Cli("tag", "One", "everyday");
            Cli("tag", "Two", "everyday", "to try");
            Assert.Contains("2  everyday", Cli("tags"));
            Assert.Contains("1  to try", Cli("tags"));
            Cli("tag-rename", "everyday", "daily");
            Assert.Equal(new[] { "daily" }, Library.Load().Mods[0].Tags.ToArray());
            Cli("untag", "Two", "to try");
            Assert.Equal(new[] { "daily" }, Library.Load().Mods[1].Tags.ToArray());
            Assert.Contains("0  to try", Cli("tags"));      // no mod carries it, the label stays
            Cli("tag-delete", "daily");
            Cli("tag-delete", "to try");
            Assert.Contains("no labels yet", Cli("tags"));
        }

        /// <summary>What the player chose cannot be downloaded again, so each save keeps the
        /// version before it beside the file as .bak (the user, 2026-10-24).</summary>
        [Fact]
        public void EverySaveKeepsTheVersionBeforeIt()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            var file = Store.PathOf("library.json");
            var bak = file + ".bak";
            var first = File.ReadAllText(file);
            Assert.DoesNotContain("Two", first);
            Cli("add", Mod("Two", Files((B + ".patch_0", "two"))));
            Assert.Contains("Two", File.ReadAllText(file));
            Assert.True(File.Exists(bak), "the version before the last save is kept");
            Assert.Equal(first, File.ReadAllText(bak));
            // and the file itself is whole, never half written
            Assert.False(File.Exists(file + ".tmp"));
        }

        /// <summary>Another manager's ids are its own: a mod that comes in with an id the library
        /// already uses is given a new one, so nothing that goes by id sees two of them (the user,
        /// 2026-10-24: the window came down while drawing the storage page).</summary>
        [Fact]
        public void AnImportNeverBringsAnIdTwice()
        {
            Cli("add", Mod("Mine", Files((A + ".patch_0", "mine"))));
            var lib = Library.Load();
            var taken = lib.Mods[0].Id;
            var theirs = new Library { ImportedFrom = "Another" };
            theirs.Mods.Add(new Mod { Id = taken, Name = "Theirs", Path = Mod("Theirs", Files((B + ".patch_0", "theirs"))), Enabled = true });
            var (added, _) = Importers.Merge(lib, theirs);
            Assert.Single(added);
            Assert.NotEqual(taken, added[0].Id);
            Assert.Equal(2, lib.Mods.Select(m => m.Id).Distinct().Count());
        }

        /// <summary>Echelon's own library.json (format "echelon-library", version 1), which it
        /// writes for other tools. Echelon keeps its copy of a mod as numbered set folders with no
        /// manifest.json, so its options are the only thing that says which folder belongs to which
        /// option: they come in as the mod's layout, and the deploy takes the chosen folders alone
        /// (the user, 2026-10-24: a tester saw the sub-options come in unchosen).</summary>
        [Fact]
        public void EchelonsListIsImported()
        {
            var cape = Mod("Cape (ab12)", Files(
                ("opt/0/" + A + ".patch_0", "core"),
                ("opt/1/" + A + ".patch_0", "blue"),
                ("opt/2/" + A + ".patch_0", "red")));
            Assert.False(File.Exists(Path.Combine(cape, "manifest.json")));
            var file = Path.Combine(Root, "Echelon", "library.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string Set(int i) => "{\"hash\": \"" + A + "\", \"files\": [\"opt/" + i + "/" + A + ".patch_0\"], \"deployed_as\": null}";
            File.WriteAllText(file, "{\"format\": \"echelon-library\", \"version\": 1, \"mods\": [" +
                "{\"id\": \"ab12\", \"name\": \"Cape Colours\", \"folder\": " + Json.Write(cape) + ", \"enabled\": true, \"deployed\": true," +
                " \"sets\": [" + Set(0) + ", " + Set(1) + ", " + Set(2) + "]," +
                " \"options\": [" +
                "{\"name\": \"Core\", \"enabled\": true, \"sets\": [0], \"choices\": [], \"selected\": null}," +
                "{\"name\": \"Colour\", \"enabled\": true, \"sets\": [], \"choices\": [{\"name\": \"Blue\", \"sets\": [1]}, {\"name\": \"Red\", \"sets\": [2]}], \"selected\": 0}]}]}",
                new UTF8Encoding(false));
            Assert.Contains("1 mods added from Echelon", Cli("import-echelon", "--file", file));
            var mod = Library.Load().Mods.Last();
            Assert.Equal("Cape Colours", mod.Name);
            Assert.Equal(cape, mod.Path);
            var chosen = Manifest.Merged(Manifest.Of(mod), mod.Options);
            Assert.Equal(new[] { "Core", "Colour" }, chosen.Select(x => x.Name).ToArray());
            Assert.True(chosen[0].Enabled);
            Assert.Equal(new[] { "Blue" }, chosen[1].Subs.Where(x => x.Enabled).Select(x => x.Name).ToArray());
            // the chosen folders only: opt/2 (Red) stays out
            Assert.Equal(new[] { "blue", "core" }, PlanList().OrderBy(x => x).ToArray());
            // its own file is left alone, and another format is refused
            Assert.Contains("echelon-library", File.ReadAllText(file));
            File.WriteAllText(file, "{\"format\": \"something-else\", \"version\": 1, \"mods\": []}", new UTF8Encoding(false));
            Assert.Throws<ModError>(() => Importers.ReadEchelon(file));
        }

        /// <summary>Echelon's library.json is in the order the mods were added; the load order is
        /// its active profile's, in its state.json (a tester, 2026-10-25: imported "乱序").</summary>
        [Fact]
        public void EchelonsLoadOrderIsItsProfiles()
        {
            var dir = Path.Combine(Root, "Echelon");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "library.json");
            string Entry(string id) => "{\"id\": \"" + id + "\", \"name\": \"Mod " + id + "\", \"folder\": " +
                                       Json.Write(Path.Combine(Root, id)) + ", \"enabled\": true, \"deployed\": true, \"sets\": [], \"options\": []}";
            File.WriteAllText(file, "{\"format\": \"echelon-library\", \"version\": 1, \"mods\": [" +
                Entry("a") + ", " + Entry("b") + ", " + Entry("c") + "]}", new UTF8Encoding(false));
            string Names() => string.Join(" ", Importers.ReadEchelon(file).Mods.Select(m => m.Name));
            // no state.json: library.json's order
            Assert.Equal("Mod a Mod b Mod c", Names());
            void State(int version) => File.WriteAllText(Path.Combine(dir, "state.json"),
                "{\"version\": " + version + ", \"active_profile\": \"p2\", \"profiles\": [" +
                "{\"id\": \"p1\", \"name\": \"Other\", \"entries\": [{\"type\": \"mod\", \"id\": \"b\"}]}," +
                "{\"id\": \"p2\", \"name\": \"Default\", \"entries\": [{\"type\": \"mod\", \"id\": \"c\", \"enabled\": true}," +
                " {\"type\": \"sep\", \"id\": \"s1\", \"title\": \"Bingus\"}, {\"type\": \"mod\", \"id\": \"a\", \"enabled\": true}]}]}",
                new UTF8Encoding(false));
            // the active profile's order; b, not in it, after
            State(2);
            Assert.Equal("Mod c Mod a Mod b", Names());
            // a state.json of a version not known here is not guessed at
            State(3);
            Assert.Equal("Mod a Mod b Mod c", Names());
        }

        /// <summary>The pictures and the words Echelon keeps beside its library, not in the mod's
        /// folder, come in too, copied (a tester, 2026-10-25: no cover, no option pictures).</summary>
        [Fact]
        public void EchelonsPicturesAndWordsComeIn()
        {
            var cape = Mod("Cape (ab12)", Files(
                ("opt/0/" + A + ".patch_0", "core"),
                ("opt/1/" + A + ".patch_0", "blue"),
                ("opt/2/" + A + ".patch_0", "red")));
            var dir = Path.Combine(Root, "Echelon");
            Directory.CreateDirectory(Path.Combine(dir, "images"));
            File.WriteAllBytes(Path.Combine(dir, "images", "ab12_cover.png"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(dir, "images", "ab12_blue.png"), new byte[] { 4, 5 });
            var file = Path.Combine(dir, "library.json");
            string Set(int i) => "{\"hash\": \"" + A + "\", \"files\": [\"opt/" + i + "/" + A + ".patch_0\"], \"deployed_as\": null}";
            File.WriteAllText(file, "{\"format\": \"echelon-library\", \"version\": 1, \"mods\": [" +
                "{\"id\": \"ab12\", \"name\": \"Cape Colours\", \"folder\": " + Json.Write(cape) + ", \"enabled\": true, \"deployed\": true," +
                " \"sets\": [" + Set(0) + ", " + Set(1) + ", " + Set(2) + "]," +
                " \"options\": [" +
                "{\"name\": \"Core\", \"enabled\": true, \"sets\": [0], \"choices\": [], \"selected\": null}," +
                "{\"name\": \"Colour\", \"enabled\": true, \"sets\": [], \"choices\": [{\"name\": \"Blue\", \"sets\": [1]}, {\"name\": \"Red\", \"sets\": [2]}], \"selected\": 0}]}]}",
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "state.json"),
                "{\"version\": 2, \"active_profile\": \"p\", \"profiles\": [{\"id\": \"p\", \"name\": \"Default\", \"entries\": [{\"type\": \"mod\", \"id\": \"ab12\"}]}]," +
                " \"library\": {\"ab12\": {\"id\": \"ab12\", \"description\": \"Capes in two colours.\", \"image\": \"ab12_cover.png\", \"nexus\": {\"mod_id\": 777, \"version\": \"\"}, \"tags\": [\"Cape\", \" Lua script \", \"cape\"], \"options\": [" +
                "{\"name\": \"Core\", \"description\": \"Required.\", \"image\": null, \"include\": [0], \"subs\": []}," +
                "{\"name\": \"Colour\", \"description\": \"\", \"image\": null, \"include\": [], \"subs\": [" +
                "{\"name\": \"Blue\", \"description\": \"Sky blue.\", \"image\": \"ab12_blue.png\", \"include\": [1]}," +
                "{\"name\": \"Red\", \"description\": \"\", \"image\": \"missing.png\", \"include\": [2]}]}]}}}",
                new UTF8Encoding(false));
            Cli("import-echelon", "--file", file);
            var mod = ((List<object>)((Dictionary<string, object>)Json.Parse(Cli("state")))["mods"]).OfType<Dictionary<string, object>>().Single(x => (string)x["name"] == "Cape Colours");
            Assert.Equal("Capes in two colours.", mod["description"]);
            // its source: the Nexus Mods page, with no version where Echelon left it blank
            Assert.Equal(777L, mod["nexus_mod"]);
            Assert.Null(Library.Load().Mods.Single().Nexus.Version);
            // its labels, each once
            Assert.Equal(new[] { "Cape", "Lua script" }, Library.Load().Mods.Single().Tags.ToArray());
            Assert.Equal(new[] { "Cape", "Lua script" }, Library.Load().Tags.ToArray());
            // copied into this program's own folder: Echelon may delete its copy
            var cover = (string)mod["icon"];
            Assert.StartsWith(Path.Combine(Store.Home, "pictures"), cover);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(cover));
            var options = ((List<object>)mod["options"]).OfType<Dictionary<string, object>>().ToList();
            Assert.Equal("Required.", options[0]["description"]);
            var subs = ((List<object>)options[1]["subs"]).OfType<Dictionary<string, object>>().ToList();
            Assert.Equal("Sky blue.", subs[0]["description"]);
            Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes((string)subs[0]["icon"]));
            Assert.StartsWith(Path.Combine(Store.Home, "pictures"), (string)subs[0]["icon"]);
            // a picture Echelon names but does not have is no picture
            Assert.Null(subs[1]["icon"]);
            // and it all stays after the library is read again
            Assert.Equal("Sky blue.", Library.Load().Mods.Single().Layout[1].SubOptions[0].Description);
        }

        /// <summary>A label made before any mod carries it is kept in the library, and stays there
        /// when the last mod lets it go; only deleting it takes it away (the user, 2026-10-24).</summary>
        [Fact]
        public void ALabelIsKeptBeforeAnyModCarriesIt()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("tag-new", "later");
            Assert.Contains("0  later", Cli("tags"));
            Assert.Contains("later", Library.Load().Tags);
            Cli("tag", "One", "later");
            Assert.Contains("1  later", Cli("tags"));
            Cli("untag", "One", "later");
            Assert.Contains("0  later", Cli("tags"));
            Cli("tag-delete", "later");
            Assert.DoesNotContain("later", Cli("tags"));
        }

        /// <summary>A patch file the library does not account for is the hardest kind of mistake
        /// to find: the game's own integrity check does not know these files and never removes
        /// them (HS历史学家 on bilibili, 2026-10-25). check says what is missing, what no mod
        /// wants, and what is not the file the library holds.</summary>
        [Fact]
        public void CheckSaysWhatTheGameFolderHasThatTheLibraryDoesNot()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", Files((B + ".patch_0", "two"))));
            Cli("deploy");
            Assert.Contains("6 files checked", Cli("check"));
            Assert.Contains("exactly what the library asks for", Cli("check"));

            // another manager's file, or one an interrupted deploy left behind
            File.WriteAllText(Path.Combine(Data, A + ".patch_7"), "stray");
            var spare = CliCode(1, "check");
            Assert.Contains("spare    " + A + ".patch_7", spare);
            Assert.Contains("that no mod wants", spare);
            Cli("deploy");
            Assert.Contains("exactly what the library asks for", Cli("check"));   // a deploy clears it

            // a file taken away behind the program's back
            File.Delete(Path.Combine(Data, A + ".patch_0"));
            Assert.Contains("missing  " + A + ".patch_0", CliCode(1, "check"));
            Cli("deploy");

            // a file under the right name that is not the mod's any more. It has to be put
            // there in place of the old one: writing into the deployed file would write into
            // the library's own copy, since the two names are one file
            File.Delete(Path.Combine(Data, A + ".patch_0"));
            File.WriteAllText(Path.Combine(Data, A + ".patch_0"), "something else entirely");
            Assert.Contains("differs  " + A + ".patch_0", CliCode(1, "check"));
            Cli("deploy");
            Assert.Contains("exactly what the library asks for", Cli("check"));
        }

        /// <summary>A mod replaced with a newer build of its own shows the build's version, not
        /// the one recorded when it was downloaded from Nexus Mods, and keeps the name the player
        /// gave it (the user, 2026-10-25: Boot Mod Loader 0.9.0 installed, 0.7.0 shown, and its
        /// own name gone).</summary>
        [Fact]
        public void AReplacedModShowsItsOwnVersionAndKeepsItsName()
        {
            var one = Path.Combine(Root, "Loader v0.7.0.zip");
            var two = Path.Combine(Root, "Loader v0.9.0.zip");
            Zip(one, ("manifest.json", "{\"Version\": 1, \"Name\": \"Loader v0.7.0\"}"), (A + ".patch_0", "seven"));
            Zip(two, ("manifest.json", "{\"Version\": 1, \"Name\": \"Loader v0.9.0\"}"), (A + ".patch_0", "nine"));
            Cli("add", one);
            var lib = Library.Load();
            lib.Mods[0].Nexus = new NexusRef { Game = "helldivers2", Mod = 17754, Version = "0.7.0" };
            lib.Save();
            Cli("rename", "Loader v0.7.0", "Loader");
            Cli("replace", "Loader", two);
            var m = Library.Load().Mods[0];
            Assert.Equal("Loader", m.Name);                       // the player's name stays
            Assert.Equal("Loader v0.9.0", m.RealName);            // the name it came with is the new one
            Assert.Equal("0.9.0", Origins.VersionOf(m).Version);  // what is installed, not the Nexus record
            Assert.Equal(17754, m.Nexus.Mod);                     // the page it comes from is kept
        }

        /// <summary>The library lives outside AppData, where a desktop AI assistant's writes are
        /// not sent anywhere else (the user, 2026-10-25).</summary>
        [Fact]
        public void TheHomeIsNotInAppData()
        {
            var keep = Store.HomeOverride;
            var env = Environment.GetEnvironmentVariable("HD2MOD_HOME");
            Store.HomeOverride = null;
            Environment.SetEnvironmentVariable("HD2MOD_HOME", null);
            try
            {
                var home = Store.Home;
                Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hd2-mod-cli"), home);
                Assert.False(home.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase));
                Assert.False(home.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                Store.HomeOverride = keep;
                Environment.SetEnvironmentVariable("HD2MOD_HOME", env);
            }
        }

        /// <summary>Started from inside a packaged app, a program's writes to AppData can be sent
        /// to that app's own copy, which nothing else reads: the library splits in two and a
        /// deploy made from the wrong half rearranges the real game folder (the user,
        /// 2026-10-25). A command that writes refuses before it writes anything; one that only
        /// reads goes on.</summary>
        [Fact]
        public void NoWritesThatWouldLandInAnotherAppsCopy()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            var before = File.ReadAllText(Store.PathOf("library.json"));
            Store.Redirected = () => @"C:\elsewhere\LocalCache\Roaming\hd2-mod-cli";
            try
            {
                CliCode(1, "tag-new", "anything");
                CliCode(1, "deploy");
                Assert.Equal(before, File.ReadAllText(Store.PathOf("library.json")));
                Assert.Empty(Deployed());
                Assert.Contains("One", Cli("list"));          // reading is fine
                Cli("status");
            }
            finally { Store.Redirected = () => null; }
            Cli("deploy");
            Assert.NotEmpty(Deployed());
        }

        /// <summary>While one program writes to the game folder, another must not: two deploys
        /// at once each undo the other, and anything read in the middle is about a state on its
        /// way somewhere (the user, 2026-10-25). The mark is ignored once the program that left
        /// it is gone, so a deploy that was killed does not block the next one for ever.</summary>
        [Fact]
        public void OneDeployAtATime()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            // a mark from a program that is running and is not this one
            Deploy.Pid = () => 4242;
            Store.Save("deploying.json", new Dictionary<string, object>
            {
                ["pid"] = (long)System.Diagnostics.Process.GetCurrentProcess().Id,
                ["since"] = DateTime.UtcNow.Ticks,
                ["game"] = Game,
            });
            CliCode(1, "deploy");          // refused: the message goes to the error output
            Assert.Contains("\"deploying\"", Cli("state"));
            Assert.Empty(Deployed());

            // the same mark, from a program that is no longer there
            Store.Save("deploying.json", new Dictionary<string, object>
            {
                ["pid"] = 999999L, ["since"] = DateTime.UtcNow.Ticks, ["game"] = Game,
            });
            Cli("deploy");
            Assert.NotEmpty(Deployed());

            // and a deploy takes its own mark away when it is done
            Assert.False(File.Exists(Store.PathOf("deploying.json")));
            Deploy.Pid = () => System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        /// <summary>A library waiting to be deployed is not a fault. Putting a mod in front moves
        /// the numbering of everything behind it, so most of the game folder stops matching until
        /// the next deploy -- the window must not call that a problem, it already counts it as
        /// work to do (the user, 2026-10-25: the line said 135 files did not match, and all of
        /// them were simply not deployed yet).</summary>
        [Fact]
        public void WorkStillToDoIsNotCalledAFault()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", Files((A + ".patch_0", "two"))));
            Cli("deploy");
            Cli("add", Mod("Three", Files((A + ".patch_0", "three"))));
            Cli("move", "Three", "--top");
            var out_ = CliCode(1, "check");
            Assert.Contains("differ", out_);                       // the folder really does not match
            Assert.Contains("none of it is a file a deploy would leave alone", out_);
            Cli("deploy");
            Assert.Contains("exactly what the library asks for", Cli("check"));
        }

        /// <summary>Making copies into hard links must not record a file as a mod's when the file
        /// under that name is another mod's: numbering moves everything after a mod that was put
        /// in front, and a record like that makes the next deploy skip the write (the user,
        /// 2026-10-25: the window said the loader was deployed while the game ran the old one).
        /// Both bodies are the same length on purpose, since a size that differs hides it.</summary>
        [Fact]
        public void LinkingNeverRecordsAFileAsAModItIsNot()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("deploy", "--copy");          // copies, so there is something for relink to do
            // another manager's file, or one an interrupted deploy left, under a name this
            // library has not deployed yet: the same length as what belongs there
            File.WriteAllText(Path.Combine(Data, A + ".patch_1"), "old");
            Cli("add", Mod("Two", Files((A + ".patch_0", "two"))));
            Cli("relink");
            Cli("deploy");
            Assert.Equal("one", Body(A + ".patch_0"));
            Assert.Equal("two", Body(A + ".patch_1"));
        }

        /// <summary>A new version takes the old one's place on the disk too: an armour pack
        /// leaves hundreds of megabytes behind otherwise (the user, 2026-10-25).</summary>
        [Fact]
        public void ANewVersionTakesTheOldOnesFolderWithIt()
        {
            var one = Path.Combine(Root, "Cape v1.zip");
            var two = Path.Combine(Root, "Cape v2.zip");
            Zip(one, ("Cape/" + A + ".patch_0", "cape v1"));
            Zip(two, ("Cape/" + A + ".patch_0", "cape v2"));
            Cli("add", one, "--name", "Cape");
            var first = Library.Load().Mods[0].Path;
            Assert.True(Directory.Exists(first));
            Cli("replace", "Cape", two);
            var now = Library.Load().Mods[0].Path;
            Assert.NotEqual(first, now);
            Assert.False(Directory.Exists(first), "the old version's folder is still there");
            Assert.True(Directory.Exists(now));
            Assert.Contains("no folders of old versions are left over", Cli("leftovers"));
            // --keep-old keeps it, and then it is a leftover the player can delete
            Cli("replace", "Cape", one, "--keep-old");
            Assert.True(Directory.Exists(now));
            Assert.Contains(Path.GetFileName(now), Cli("leftovers"));
            Cli("leftovers", "--delete", "--all");
            Assert.False(Directory.Exists(now));
        }

        /// <summary>The library folder is often another manager's own (HD2 Arsenal's), so only the
        /// folders this program unpacked may ever be deleted.</summary>
        [Fact]
        public void LeftoversNeverTouchWhatThisProgramDidNotUnpack()
        {
            var mods = Path.Combine(Root, "library");
            Directory.CreateDirectory(Path.Combine(mods, "Someone Elses Mod_AR123456"));
            Directory.CreateDirectory(Path.Combine(mods, "A folder of my own"));
            Directory.CreateDirectory(Path.Combine(mods, "Old Cape_0123456789ab"));
            var out_ = Cli("leftovers");
            Assert.Contains("Old Cape_0123456789ab", out_);
            Assert.DoesNotContain("_AR123456", out_);
            Assert.DoesNotContain("A folder of my own", out_);
            Cli("leftovers", "--delete", "--all");
            Assert.False(Directory.Exists(Path.Combine(mods, "Old Cape_0123456789ab")));
            Assert.True(Directory.Exists(Path.Combine(mods, "Someone Elses Mod_AR123456")));
            Assert.True(Directory.Exists(Path.Combine(mods, "A folder of my own")));
            CliCode(1, "leftovers", "--delete");      // neither folders nor --all: refused
        }

        /// <summary>One label onto several mods in one call (the user's tester, 2026-10-25).</summary>
        [Fact]
        public void OneLabelGoesOntoSeveralModsAtOnce()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", Files((B + ".patch_0", "two"))));
            Cli("add", Mod("Three", Files(("3a1c2d3e4f506172.patch_0", "three"))));
            Cli("tag", "Three", "armour");
            Cli("tag-many", "armour", "One", "Two");
            Assert.Contains("3  armour", Cli("tags"));
            Assert.Equal(new[] { "armour" }, Library.Load().Mods[0].Tags.ToArray());
            Cli("tag-many", "armour", "One");           // again: the label is not doubled
            Assert.Equal(new[] { "armour" }, Library.Load().Mods[0].Tags.ToArray());
            CliCode(1, "tag-many", "armour");   // a label with no mod behind it is refused
        }

        /// <summary>A group is only a name in the library (the user, 2026-10-27): putting mods in
        /// groups moves no folder and changes nothing a deploy would do.</summary>
        [Fact]
        public void GroupsAreShownOnlyNothingMoves()
        {
            var one = Mod("One", Files((A + ".patch_0", "one")));
            var two = Mod("Two", Files((B + ".patch_0", "two")));
            Cli("add", one);
            Cli("add", two);
            Cli("deploy");
            var before = Deployed();
            Cli("group-new", "Looks");
            Cli("group-new", "Armour", "--in", "Looks");
            Cli("group-put", "One", "Two", "--in", "Looks/Armour");
            Assert.True(Directory.Exists(one) && Directory.Exists(two));       // the folders stay where they are
            Assert.Contains("move 0, write 0, delete 0", Cli("status"));
            Assert.Equal(before, Deployed());
            var lib = Library.Load();
            Assert.Equal("Looks/Armour", Groups.PathText(lib, lib.Mods[0].Group));
            Assert.Contains("Armour/  (2 mods)", Cli("groups"));
            Assert.Contains("\"group\"", Cli("state"));
            Cli("group-put", "One", "--top");
            Assert.Null(Library.Load().Mods[0].Group);
        }

        /// <summary>The groups' rules: three levels at most, no group inside itself, no two of one
        /// name side by side; deleting one hands what it held to the group around it.</summary>
        [Fact]
        public void GroupsKeepTheirRules()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("group-new", "A");
            Cli("group-new", "B", "--in", "A");
            Cli("group-new", "C", "--in", "A/B");
            Assert.Equal("group_depth", Refused("group-new", "D", "--in", "A/B/C"));
            Assert.Equal("group_exists", Refused("group-new", "b", "--in", "A"));
            Assert.Equal("group_inside", Refused("group-move", "A", "--in", "A/B"));
            Cli("group-new", "Other");
            Assert.Equal("group_depth", Refused("group-move", "A", "--in", "Other"));    // A holds two levels
            Cli("group-new", "B", "--in", "Other");
            Assert.Equal("group_ambiguous", Refused("group-put", "One", "--in", "B"));  // two groups named B: the path tells them apart
            Cli("group-put", "One", "--in", "A/B/C");
            Cli("group-delete", "A/B");
            var lib = Library.Load();
            Assert.Equal("A/C", Groups.PathText(lib, Groups.Pick(lib, "C").Id));     // C came up into A
            Assert.Equal("A/C", Groups.PathText(lib, lib.Mods[0].Group));
            Cli("group-rename", "A/C", "Kept");
            Assert.Equal("A/Kept", Groups.PathText(Library.Load(), Library.Load().Mods[0].Group));
            CliCode(1, "group-move", "Other");                                          // --in or --top
        }

        /// <summary>A library.json written by hand or by an older build: a group under one that is
        /// gone, a loop, or a mod in a missing group come back to the top, not as an error.</summary>
        [Fact]
        public void BrokenGroupsGoToTheTop()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            var path = Path.Combine(Store.Home, "library.json");
            var d = (Dictionary<string, object>)Json.Parse(File.ReadAllText(path));
            d["groups"] = new List<object>
            {
                new Dictionary<string, object> { ["id"] = "x", ["name"] = "X", ["parent"] = "y" },
                new Dictionary<string, object> { ["id"] = "y", ["name"] = "Y", ["parent"] = "x" },
                new Dictionary<string, object> { ["id"] = "z", ["name"] = "Z", ["parent"] = "gone" },
            };
            ((Dictionary<string, object>)((List<object>)d["mods"])[0])["group"] = "missing";
            File.WriteAllText(path, Json.Write(d));
            var lib = Library.Load();
            Assert.All(lib.Groups, g => Assert.True(Groups.Level(lib, g.Id) <= Groups.Depth));
            Assert.Null(Groups.Of(lib, "z").Parent);
            Assert.Null(lib.Mods[0].Group);
            Assert.Contains("Z/", Cli("groups"));
        }


        /// <summary>A mod packs its picture under whatever name the author chose, and Arsenal shows
        /// it; so does this (the user's tester, 2026-10-25).</summary>
        [Fact]
        public void AModsPictureIsFoundUnderAnyOfItsNames()
        {
            var named = Mod("One", Files((A + ".patch_0", "one"), ("cover.jpg", "a picture")));
            var alone = Mod("Two", Files((B + ".patch_0", "two"), ("screenshot_01.png", "a picture")));
            var many = Mod("Three", Files(("3a1c2d3e4f506172.patch_0", "three"), ("a.png", "one"), ("b.png", "two")));
            Cli("add", named);
            Cli("add", alone);
            Cli("add", many);
            var state = Cli("state");
            Assert.Contains("cover.jpg", state);        // one of the names a picture goes by
            Assert.Contains("screenshot_01.png", state);// the only image in the folder
            Assert.DoesNotContain("a.png", state);      // two images and no usual name: neither
        }

        /// <summary>A mod that ships one of the scripts a loader replaces is marked as one.</summary>
        [Fact]
        public void LoadersAreMarked()
        {
            byte[] Lua(ulong id)
            {
                var a = Archive(Bytes("-- a script"), (Deploy.LuaType, id));
                BitConverter.GetBytes(Deploy.LuaType).CopyTo(a, 0x48 + 8);
                return a;
            }
            // a loader is one the Lua scan names (the user, 2026-10-26): a boot of its own is not enough
            Cli("add", Mod("Loader", new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "-- Boot Mod Loader\nlocal MARKER = \"boot-mod-loader-0.12.1\"\n")) }));
            Cli("add", Mod("Own boot", new Dictionary<string, byte[]> { [A + ".patch_0"] = LuaArchive((LuaMods.BootId, "function init() end\n")) }));
            Cli("add", Mod("Plain", new Dictionary<string, byte[]> { [A + ".patch_0"] = Lua(0x1234567890ABCDEF) }));
            var st = (Dictionary<string, object>)Json.Parse(Cli("state"));
            var loader = Json.List(st, "mods").Cast<Dictionary<string, object>>().ToDictionary(m => (string)m["name"], m => (bool)m["loader"]);
            Assert.True(loader["Loader"]);
            Assert.False(loader["Own boot"]);
            Assert.False(loader["Plain"]);
            Assert.Equal("bml", Json.List(st, "mods").Cast<Dictionary<string, object>>().Single(m => (string)m["name"] == "Loader")["loader_kind"]);
        }

        [Fact]
        public void LuaModsAreMarked()
        {
            byte[] WithType(ulong type)
            {
                var a = Archive((type, 1));
                BitConverter.GetBytes(type).CopyTo(a, 0x48 + 8);     // the type table's row: the type at +8
                return a;
            }
            Cli("add", Mod("Script", new Dictionary<string, byte[]> { ["opt/" + A + ".patch_0"] = WithType(Deploy.LuaType) },
                           "{\"Name\": \"Script\", \"Options\": [{\"Name\": \"X\", \"Include\": [\"opt\"]}]}"));
            Cli("add", Mod("Texture", new Dictionary<string, byte[]> { [A + ".patch_0"] = WithType(0xCD4238C6A0C69E32) }));
            Cli("set", "Script", "1", "off");    // off and not deployed: still marked, from its folder
            var st = (Dictionary<string, object>)Json.Parse(Cli("state"));
            var lua = Json.List(st, "mods").Cast<Dictionary<string, object>>().ToDictionary(m => (string)m["name"], m => (bool)m["lua"]);
            Assert.True(lua["Script"]);
            Assert.False(lua["Texture"]);
        }

        [Fact]
        public void Which()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Cli("add", Mod("Two", Files((A + ".patch_0", "two"))));
            Assert.Contains("Two", Cli("which", A + ".patch_1"));
        }

        /// <summary>Several mods move as one block, in the order they have, wherever they are
        /// sent (the user, 2026-10-25: dragging the mods taken together moved only one).</summary>
        [Fact]
        public void SeveralModsMoveAsOneBlock()
        {
            foreach (var n in new[] { "One", "Two", "Three", "Four", "Five" }) Cli("add", Mod(n, Files((A + ".patch_0", n))));
            string Order() => string.Join(" ", Library.Load().Mods.Select(m => m.Name));
            Cli("move", "Four", "Two", "--after", "Five");         // given out of order: they keep theirs
            Assert.Equal("One Three Five Two Four", Order());
            Cli("move", "5", "4", "--top");                         // by place, read before anything moves
            Assert.Equal("Two Four One Three Five", Order());
            Cli("move", "One", "Three", "--to", "1");
            Assert.Equal("One Three Two Four Five", Order());
            Cli("move", "Two", "One", "--before", "Five");
            Assert.Equal("Three Four One Two Five", Order());
            Cli("move", "Three", "Four", "--bottom");
            Assert.Equal("One Two Five Three Four", Order());
            // next to one of themselves: refused, nothing moved
            CliCode(1, "move", "One", "Two", "--after", "Two");
            Assert.Equal("One Two Five Three Four", Order());
            // one mod moves as before
            Cli("move", "Five", "--top");
            Assert.Equal("Five One Two Three Four", Order());
        }

        [Fact]
        public void WrongCommandLines()
        {
            CliCode(2, "move", "One", "--top", "--bottom");
            CliCode(2, "set", "One", "1", "maybe");
            CliCode(2, "nonsense");
            CliCode(2, "add");
        }

        [Fact]
        public void GameFolderFromWhatAPlayerPicks()
        {
            Directory.CreateDirectory(Path.Combine(Game, "bin"));
            Assert.Equal(Game, Ops.GameFolderOf(Game));
            Assert.Equal(Game, Ops.GameFolderOf(Data));
            Assert.Equal(Game, Ops.GameFolderOf(Path.Combine(Game, "bin")));
            Assert.Equal(Game, Ops.GameFolderOf(Game + "\\"));
            Assert.Null(Ops.GameFolderOf(Root));
            Assert.Null(Ops.GameFolderOf(Path.Combine(Root, "nothing", "data")));
            Assert.Null(Ops.GameFolderOf(""));
        }

        [Fact]
        public void InitTakesDataAndRefusesAWrongFolderForTheWindow()
        {
            Store.Save("config.json", new Dictionary<string, object>());
            Cli("init", "--game", Data);
            Assert.Equal(Game, Store.GameDir());
            var e = new StringWriter();
            Assert.Equal(1, Commands.Run(new[] { "init", "--game", Root }, new StringWriter(), e, out var refused));
            Assert.Equal("no_game", refused.Kind);
            Assert.Equal(Game, Store.GameDir());     // the folder set before stays
        }

        [Fact]
        public void ADuplicateNameIsRefusedWithItsName()
        {
            Cli("add", Mod("One", Files((A + ".patch_0", "one"))));
            Assert.Equal(1, Commands.Run(new[] { "add", Mod("One again", Files((A + ".patch_0", "x"))), "--name", "One" },
                                         new StringWriter(), new StringWriter(), out var refused));
            Assert.Equal("duplicate", refused.Kind);
            Assert.Equal("One", refused.Arg);
        }

        [Fact]
        public void TheModFolderIsNeverAppData()
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            try
            {
                Store.Save("config.json", new Dictionary<string, object> { ["game_dir"] = Game });
                Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(Root, "local"));     // no HD2 Arsenal
                Assert.Equal(Path.Combine(Path.GetPathRoot(Game), "HD2 Mods"), Store.ModsDir());
                var arsenalMods = Path.Combine(Root, "Arsenal Mods");
                Directory.CreateDirectory(arsenalMods);
                Directory.CreateDirectory(Path.Combine(Root, "local", "hd2arsenal"));
                File.WriteAllText(Path.Combine(Root, "local", "hd2arsenal", "hd2a_data.json"), J(new Dictionary<string, object> { ["userModsDir"] = arsenalMods }));
                Assert.Equal(arsenalMods, Store.ModsDir());                                        // HD2 Arsenal's
                Cli("init", "--mods-dir", Path.Combine(Root, "chosen"));
                Assert.Equal(Path.Combine(Root, "chosen"), Store.ModsDir());                       // the one set
                Assert.False(Store.ModsDir().StartsWith(Store.Home, StringComparison.OrdinalIgnoreCase));
            }
            finally { Environment.SetEnvironmentVariable("LOCALAPPDATA", local); }
        }

        /// <summary>A fake HD2ModManager under a fake LOCALAPPDATA: C (object options), A (none),
        /// B (folder-name options), listed in enabled.json in the order C, A, B.</summary>
        string FakeHd2mm()
        {
            var dir = Path.Combine(Root, "local", "Helldivers2ModManager");
            string M(string name) => Path.Combine(dir, "Mods", name);
            Directory.CreateDirectory(M("A"));
            File.WriteAllText(Path.Combine(M("A"), A + ".patch_0"), "a");
            File.WriteAllText(Path.Combine(M("A"), "manifest.json"), "{\"Guid\":\"g-a\",\"Name\":\"Mod A\"}");
            foreach (var f in new[] { "x", "y" }) { Directory.CreateDirectory(Path.Combine(M("B"), f)); File.WriteAllText(Path.Combine(M("B"), f, A + ".patch_0"), "b-" + f); }
            File.WriteAllText(Path.Combine(M("B"), "manifest.json"), "{\"Guid\":\"g-b\",\"Name\":\"Mod B\",\"Options\":[\"x\",\"y\"]}");
            foreach (var f in new[] { "base", "s1", "s2" }) { Directory.CreateDirectory(Path.Combine(M("C"), f)); File.WriteAllText(Path.Combine(M("C"), f, A + ".patch_0"), "c-" + f); }
            File.WriteAllText(Path.Combine(M("C"), "manifest.json"),
                "{\"Version\":1,\"Guid\":\"g-c\",\"Name\":\"Mod C\",\"Options\":[{\"Name\":\"Pick\",\"SubOptions\":[{\"Name\":\"S1\",\"Include\":[\"s1\"]},{\"Name\":\"S2\",\"Include\":[\"s2\"]}]},{\"Name\":\"Base\",\"Include\":[\"base\"]}]}");
            File.WriteAllText(Path.Combine(dir, "enabled.json"),
                "[{\"Guid\":\"g-c\",\"Enabled\":true,\"Toggled\":[true,false],\"Selected\":[1,0]},{\"Guid\":\"g-a\",\"Enabled\":false,\"Toggled\":[],\"Selected\":[0]},{\"Guid\":\"g-b\",\"Enabled\":true,\"Toggled\":[],\"Selected\":[1]}]");
            return dir;
        }

        [Fact]
        public void Hd2ModManagerIsImportedWithItsOrderAndChoices()
        {
            var dir = FakeHd2mm();
            Assert.Contains("3 mods added from HD2ModManager (2 on), 0 already", Cli("import-hd2mm", "--dir", dir));
            var lib = Library.Load();
            Assert.Equal(new[] { "Mod C", "Mod A", "Mod B" }, lib.Mods.Select(m => m.Name));
            Assert.Equal(new[] { true, false, true }, lib.Mods.Select(m => m.Enabled));
            // C: S2 chosen, Base off; B: its second folder
            Assert.Equal(new[] { "c-s2", "b-y" }, PlanList());
        }

        /// <summary>A library folder is looked into, as Steam looks into a library folder it is
        /// given (the user, 2026-10-25): another manager's list kept there is offered as an import,
        /// HD2ModManager's even when its storage was moved (found through the settings.json beside
        /// its program); other mod folders are offered as they stand; our own older versions are not.</summary>
        [Fact]
        public void ALibraryFolderSaysWhatItHolds()
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            var roaming = Environment.GetEnvironmentVariable("APPDATA");
            try
            {
                // no manager in its usual place
                Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(Root, "empty-local"));
                Environment.SetEnvironmentVariable("APPDATA", Path.Combine(Root, "empty-roaming"));
                // HD2ModManager's storage moved by its own setting, named in settings.json beside its program
                var moved = Path.Combine(Root, "Moved Storage");
                Directory.Move(FakeHd2mm(), moved);
                var program = Path.Combine(Root, "HD2ModManager 1.2");
                Directory.CreateDirectory(program);
                File.WriteAllText(Path.Combine(program, "Helldivers2ModManager.exe"), "");
                File.WriteAllText(Path.Combine(program, "settings.json"), "{\"StorageDirectory\": " + Json.Write(moved) + "}");
                var said = Cli("folder-add", program);
                Assert.Contains("HD2ModManager's list, 3 mods not in the library: hd2qd import-hd2mm --dir", said);
                // the storage folder, and its Mods folder, are known as well
                Assert.Contains("HD2ModManager's list, 3 mods", Cli("folder-add", moved));
                Assert.Contains("HD2ModManager's list, 3 mods", Cli("folder-add", Path.Combine(moved, "Mods")));
                // --import brings the list in, with its order and on/off
                Cli("folder-add", program, "--import");
                Assert.Equal(new[] { "Mod C", "Mod A", "Mod B" }, Library.Load().Mods.Select(m => m.Name));
                Assert.Equal(new[] { true, false, true }, Library.Load().Mods.Select(m => m.Enabled));
                Assert.DoesNotContain("HD2ModManager's list", Cli("folder-add", program));   // nothing left to bring

                // a folder of mods no manager lists: a Mods folder of manifests is not HD2ModManager's
                var plain = Path.Combine(Root, "Plain", "Mods");
                foreach (var n in new[] { "One", "Two" })
                {
                    Directory.CreateDirectory(Path.Combine(plain, n, "sub"));
                    File.WriteAllText(Path.Combine(plain, n, "sub", A + ".patch_0"), n);
                    File.WriteAllText(Path.Combine(plain, n, "manifest.json"), "{\"Name\": \"" + n + "\"}");
                }
                Directory.CreateDirectory(Path.Combine(plain, "Not a mod"));
                Cli("add", Path.Combine(plain, "One"));
                said = Cli("folder-add", plain);
                Assert.DoesNotContain("HD2ModManager's list", said);
                Assert.Contains("1 mod folders no manager lists", said);
                Assert.Contains("1 mod folders added where they are", Cli("add-loose", plain));
                var two = Library.Load().Mods.Single(m => m.Name == "Two");
                Assert.Equal(Path.Combine(plain, "Two"), two.Path);                        // where it is, not copied
                Assert.DoesNotContain("mod folders", Cli("folder-add", plain));

                // a manager's store whose list is not where it usually is: nothing loose, its list asked for
                var arsenalStore = Path.Combine(Root, "Arsenal Mods");
                foreach (var n in new[] { "One_AR123456", "Two_AR654321" })
                {
                    Directory.CreateDirectory(Path.Combine(arsenalStore, n));
                    File.WriteAllText(Path.Combine(arsenalStore, n, A + ".patch_0"), n);
                }
                var store = Storage.Look(arsenalStore, Library.Load());
                Assert.Equal("arsenal", store.NeedsConfig);
                Assert.Empty(store.Loose);
                Assert.Contains("HD2 Arsenal's mods, but its list (hd2a_data.json) was not found", Cli("folder-add", arsenalStore));
                // the list the player chooses: read, and it must list mods in this folder
                var list = Path.Combine(Root, "elsewhere", "hd2a_data.json");
                Directory.CreateDirectory(Path.GetDirectoryName(list));
                File.WriteAllText(list, J(new Dictionary<string, object>
                {
                    ["deployedProfile"] = "p", ["modsList"] = new Dictionary<string, object>
                    {
                        ["p"] = new Dictionary<string, object> { ["label"] = "Main", ["mods"] = new List<object>
                        {
                            new Dictionary<string, object> { ["uuid"] = "u1", ["label"] = "One", ["path"] = Path.Combine(arsenalStore, "One_AR123456"), ["enabled"] = true },
                            new Dictionary<string, object> { ["uuid"] = "u2", ["label"] = "Two", ["path"] = Path.Combine(arsenalStore, "Two_AR654321"), ["enabled"] = false },
                        } },
                    },
                }), new UTF8Encoding(false));
                var src = Importers.FromChosenFile("arsenal", list, arsenalStore);
                Assert.Equal(2, src.Mods);
                Assert.Equal(list, src.File);
                Assert.Equal(0, Importers.FromChosenFile("arsenal", list, plain).Mods);              // another folder's list
                // an Echelon store: named "(12 hex)", the files in numbered set folders, no manifest
                var echelonStore = Path.Combine(Root, "Echelon mods");
                foreach (var n in new[] { "One (0123456789ab)", "Two (ba9876543210)" })
                {
                    Directory.CreateDirectory(Path.Combine(echelonStore, n, "opt", "0"));
                    File.WriteAllText(Path.Combine(echelonStore, n, "opt", "0", A + ".patch_0"), n);
                }
                Assert.Equal("echelon", Storage.Look(echelonStore, Library.Load()).NeedsConfig);
                // HD2ModManager's Mods folder away from its enabled.json: known by the manifests it wrote
                var hd2mmMods = Path.Combine(Root, "Copied", "Mods");
                foreach (var (n, text) in new[] { ("One", "A locally imported mod."), ("Two", "Its own text.") })
                {
                    Directory.CreateDirectory(Path.Combine(hd2mmMods, n));
                    File.WriteAllText(Path.Combine(hd2mmMods, n, A + ".patch_0"), n);
                    File.WriteAllText(Path.Combine(hd2mmMods, n, "manifest.json"), "{\"Guid\": \"" + Guid.NewGuid() + "\", \"Name\": \"" + n + "\", \"Description\": \"" + text + "\"}");
                }
                Assert.Equal("hd2mm", Storage.Look(hd2mmMods, Library.Load()).NeedsConfig);
                // a plain folder of mods with manifests of their own is nobody's store
                Assert.Null(Importers.StoreOf(plain));

                // our own older version in the default folder is a leftover, not a mod to add
                var mods = Path.Combine(Root, "Default Mods");
                Cli("init", "--mods-dir", mods);
                Directory.CreateDirectory(Path.Combine(mods, "Old Thing v1_0123456789ab"));
                File.WriteAllText(Path.Combine(mods, "Old Thing v1_0123456789ab", B + ".patch_0"), "old");
                Assert.DoesNotContain("mod folders", Cli("folder-add", mods));
            }
            finally
            {
                Environment.SetEnvironmentVariable("LOCALAPPDATA", local);
                Environment.SetEnvironmentVariable("APPDATA", roaming);
            }
        }

        /// <summary>A library folder moved by hand (or given another drive letter): changing its path
        /// takes the list, the default and its mods along; a mod not at the new place stays where it
        /// was and is counted (the user, 2026-10-25).</summary>
        [Fact]
        public void ALibraryFolderPathCanChange()
        {
            var old = Path.Combine(Root, "Old Place");
            foreach (var n in new[] { "One", "Two", "Three" })
            {
                Directory.CreateDirectory(Path.Combine(old, n));
                File.WriteAllText(Path.Combine(old, n, A + ".patch_0"), n);
                Cli("add", Path.Combine(old, n));
            }
            Cli("folder-add", old);
            Cli("init", "--mods-dir", old);
            var now = Path.Combine(Root, "New Place");
            Directory.Move(old, now);
            Directory.Delete(Path.Combine(now, "Three"), true);                 // lost on the way
            Assert.Contains("2 mods now in " + now + "; 1 not found there", Cli("folder-path", old, now));
            var mods = Library.Load().Mods.ToDictionary(m => m.Name, m => m.Path);
            Assert.Equal(Path.Combine(now, "One"), mods["One"]);
            Assert.Equal(Path.Combine(old, "Three"), mods["Three"]);
            Assert.Contains(now, Storage.Registered());
            Assert.DoesNotContain(old, Storage.Registered());
            Assert.Equal(now, Store.ModsDir());                                  // the default follows
            Assert.Throws<ModError>(() => Storage.ChangeFolder(Library.Load(), now, Path.Combine(Root, "Nowhere")));
        }

        [Fact]
        public void ImportsMergeWithoutDuplicates()
        {
            var dir = FakeHd2mm();
            Cli("add", Mod("Mine", Files((B + ".patch_0", "mine"))));
            Cli("import-hd2mm", "--dir", dir);
            Assert.Contains("0 mods added from HD2ModManager (0 on), 3 already", Cli("import-hd2mm", "--dir", dir));
            Assert.Equal(new[] { "Mine", "Mod C", "Mod A", "Mod B" }, Library.Load().Mods.Select(m => m.Name));
            Cli("import-hd2mm", "--dir", dir, "--replace");
            Assert.Equal(new[] { "Mod C", "Mod A", "Mod B" }, Library.Load().Mods.Select(m => m.Name));
        }

        [Fact]
        public void ModsMoveBetweenFoldersAsSteamMovesGames()
        {
            var one = Mod("One", Files((A + ".patch_0", "one")));
            var two = Mod("Two", Files((B + ".patch_0", "two")));
            Cli("add", one);
            Cli("add", two);
            var target = Path.Combine(Root, "Second Library");
            Cli("folder-add", target);
            var folders = Storage.Folders(Library.Load(), Game);
            Assert.Equal(Path.Combine(Root, "library"), folders[0].Path);                // the default first
            Assert.True(folders[0].IsDefault);
            Assert.Contains(folders, f => f.Path == target && f.Registered && f.Mods.Count == 0);
            Assert.Contains(folders, f => f.Path == Path.Combine(Root, "mods") && f.Mods.Count == 2);

            Assert.Contains("moved One", Cli("relocate", "One", "--into", target));
            Assert.False(Directory.Exists(one));
            Assert.Equal(Path.Combine(target, "One"), Library.Load().Mods[0].Path);
            Assert.Contains("copied Two", Cli("relocate", "Two", "--into", target, "--keep"));
            Assert.True(Directory.Exists(two), "kept for the manager that had it");
            Assert.Equal(new[] { "one", "two" }, PlanList());

            // a library folder is not a mod's own folder: never moved
            Cli("add", target, "--name", "The whole folder", "--whole");
            Assert.Equal(1, Commands.Run(new[] { "relocate", "The whole folder", "--into", Path.Combine(Root, "x") }, new StringWriter(), new StringWriter(), out var refused));
            Assert.Equal("protected", refused.Kind);
        }

        [Fact]
        public void NormMatchesPython()
        {
            Assert.Equal(@"C:\a\c", Paths.Norm(@"C:\a\b\..\c\"));
            Assert.Equal(@"C:\a\b", Paths.Norm("C:/a/./b//"));
            Assert.Equal(@"C:\x", Paths.Norm(@"C:\..\x"));
            Assert.Equal(@"..\x", Paths.Norm(@"..\x"));
            Assert.Equal(@"\\srv\share\x", Paths.Norm(@"\\srv\share\y\..\x"));
            Assert.Equal(@"C:\a\b", Paths.Join(@"C:\a", "b"));
            Assert.Equal("C:/a/b", Paths.Join("C:/a/", "b"));
        }
    }
}
