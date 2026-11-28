using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>This program updating itself (SelfUpdate.Install; the user, 2026-11-26: downloading
    /// every update from GitHub by hand was a chore): a file is taken only when it is GitHub's
    /// (SHA-256) and of the release's version, and the old file is kept beside it until the next
    /// start. The "new version" here is a real assembly, xunit.assert.dll, under its own version.</summary>
    public class SelfUpdates : Fixture
    {
        static string ServeMany(int answers, Dictionary<string, byte[]> files)
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < answers; i++)
                    using (var c = l.AcceptTcpClient())
                    using (var s = c.GetStream())
                    {
                        var head = new System.Text.StringBuilder();
                        var b = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n") && s.Read(b, 0, 1) == 1) head.Append((char)b[0]);
                        var found = files.TryGetValue(head.ToString().Split(' ')[1], out var body);
                        body = body ?? new byte[0];
                        var h = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 " + (found ? "200 OK" : "404 Not Found") + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                        s.Write(h, 0, h.Length);
                        s.Write(body, 0, body.Length);
                    }
                l.Stop();
            });
            return "http://127.0.0.1:" + ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        }

        static string Sha(byte[] b) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }

        readonly byte[] fresh = File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xunit.assert.dll"));
        string Tag => "v" + System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xunit.assert.dll")).Version.ToString(3);

        string Window => Path.Combine(Root, "app", "HD2 Mod Quick Deploy.exe");
        string CommandLine => Path.Combine(Root, "app", "hd2qd.exe");

        void OldVersion()
        {
            Directory.CreateDirectory(Path.Combine(Root, "app"));
            File.WriteAllText(Window, "old window");
            File.WriteAllText(CommandLine, "old command line");
        }

        SelfUpdateInfo Out(string windowSha, string cliSha) => new SelfUpdateInfo
        {
            Latest = Tag, Newer = true,
            Files = new Dictionary<string, string>
            {
                [SelfUpdate.WindowAsset] = windowSha, [SelfUpdate.CliAsset] = cliSha,
            }.Where(f => f.Value != null).ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase),
        };

        string Asset(string asset) => "/dl/github/" + SelfUpdate.Repo + "/releases/download/" + Tag + "/" + asset;

        [Fact]
        public void An_update_replaces_the_window_and_the_command_line_and_keeps_the_old_files()
        {
            OldVersion();
            try
            {
                Mirror.Base = ServeMany(2, new Dictionary<string, byte[]> { [Asset(SelfUpdate.WindowAsset)] = fresh, [Asset(SelfUpdate.CliAsset)] = fresh }) + "/dl";
                Assert.Null(SelfUpdate.Install(Out(Sha(fresh), Sha(fresh)), Window, CommandLine));
            }
            finally { Mirror.Base = null; }
            Assert.Equal(fresh, File.ReadAllBytes(Window));
            Assert.Equal(fresh, File.ReadAllBytes(CommandLine));
            Assert.Equal("old window", File.ReadAllText(Window + ".old"));
            Assert.Equal("old command line", File.ReadAllText(CommandLine + ".old"));
            Assert.False(File.Exists(Window + ".new"));

            // the next start removes what the update left
            SelfUpdate.CleanOld(Window, CommandLine);
            Assert.Equal(new[] { "HD2 Mod Quick Deploy.exe", "hd2qd.exe" }, Directory.GetFiles(Path.Combine(Root, "app")).Select(Path.GetFileName).OrderBy(n => n));
        }

        [Fact]
        public void A_file_that_is_not_GitHubs_changes_nothing()
        {
            OldVersion();
            var other = fresh.Concat(new byte[] { 0 }).ToArray();
            try
            {
                // the copy and GitHub both give another file than the digest names
                Mirror.Base = ServeMany(1, new Dictionary<string, byte[]> { [Asset(SelfUpdate.WindowAsset)] = other }) + "/dl";
                PluginFiles.Downloads = ServeMany(1, new Dictionary<string, byte[]> { ["/" + SelfUpdate.Repo + "/releases/download/" + Tag + "/" + SelfUpdate.WindowAsset] = other });
                Assert.Equal("self_sha", Assert.Throws<ModError>(() => SelfUpdate.Install(Out(Sha(fresh), Sha(fresh)), Window, CommandLine)).Kind);
            }
            finally { Mirror.Base = null; PluginFiles.Downloads = "https://github.com"; }
            Assert.Equal("old window", File.ReadAllText(Window));
            Assert.Equal("old command line", File.ReadAllText(CommandLine));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(Root, "app")).Length);
        }

        [Fact]
        public void A_file_of_another_version_or_without_a_digest_changes_nothing()
        {
            OldVersion();
            try
            {
                // GitHub's very file, but not of the version the release says
                var info = Out(Sha(fresh), null);
                info.Latest = "v9.9.9";
                Mirror.Base = ServeMany(1, new Dictionary<string, byte[]> { ["/dl/github/" + SelfUpdate.Repo + "/releases/download/v9.9.9/" + SelfUpdate.WindowAsset] = fresh }) + "/dl";
                Assert.Equal("self_bad_file", Assert.Throws<ModError>(() => SelfUpdate.Install(info, Window, null)).Kind);
                // a release whose file has no digest is never downloaded
                Assert.Equal("self_no_digest", Assert.Throws<ModError>(() => SelfUpdate.Install(Out(null, null), Window, null)).Kind);
                // nothing newer: nothing to do
                var same = Out(Sha(fresh), null);
                same.Newer = false;
                Assert.Equal("self_none_newer", Assert.Throws<ModError>(() => SelfUpdate.Install(same, Window, null)).Kind);
            }
            finally { Mirror.Base = null; }
            Assert.Equal("old window", File.ReadAllText(Window));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(Root, "app")).Length);
        }

        /// <summary>A server that says a file is longer than it is and closes the connection
        /// after the first bytes: a download broken off on the way.</summary>
        static string ServeBroken()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            System.Threading.Tasks.Task.Run(() =>
            {
                using (var c = l.AcceptTcpClient())
                using (var s = c.GetStream())
                {
                    var head = new System.Text.StringBuilder();
                    var b = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n") && s.Read(b, 0, 1) == 1) head.Append((char)b[0]);
                    var h = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n0123456789");
                    s.Write(h, 0, h.Length);
                }
                l.Stop();
            });
            return "http://127.0.0.1:" + ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        }

        [Fact]
        public void A_copy_broken_off_on_the_way_is_taken_from_GitHub_instead()
        {
            OldVersion();
            try
            {
                // the author's copy breaks off; GitHub's comes whole (2026-11-26 review: a copy that
                // broke off or timed out escaped the fallback and stopped the window)
                Mirror.Base = ServeBroken() + "/dl";
                PluginFiles.Downloads = ServeMany(1, new Dictionary<string, byte[]> { ["/" + SelfUpdate.Repo + "/releases/download/" + Tag + "/" + SelfUpdate.WindowAsset] = fresh });
                Assert.Null(SelfUpdate.Install(Out(Sha(fresh), null), Window, null));
            }
            finally { Mirror.Base = null; PluginFiles.Downloads = "https://github.com"; }
            Assert.Equal(fresh, File.ReadAllBytes(Window));
        }

        [Fact]
        public void A_download_broken_off_on_the_way_is_offline_not_a_crash()
        {
            Assert.Equal("offline", Assert.Throws<ModError>(() => Updates.GetBytes(ServeBroken() + "/file")).Kind);
        }

        [Fact]
        public void The_check_keeps_each_files_digest_for_the_install()
        {
            var release = new Dictionary<string, object>
            {
                ["tag_name"] = "v9.0.0", ["html_url"] = "p", ["prerelease"] = false,
                ["assets"] = new List<object>
                {
                    new Dictionary<string, object> { ["name"] = SelfUpdate.WindowAsset, ["digest"] = "sha256:ABCD" },
                    new Dictionary<string, object> { ["name"] = "no digest.zip" },
                },
            };
            var u = SelfUpdate.Pick(new List<Dictionary<string, object>> { release }, "0.1.0", true);
            Assert.Equal(new Dictionary<string, string> { [SelfUpdate.WindowAsset] = "abcd" }, u.Files);
            Store.Save("self_update.json", new Dictionary<string, object>
            {
                ["checked_at"] = 1L, ["latest"] = "v9.0.0", ["page"] = "p", ["prerelease"] = false,
                ["files"] = new Dictionary<string, object> { [SelfUpdate.WindowAsset] = "abcd" },
            });
            Assert.Equal("abcd", SelfUpdate.Last().Files[SelfUpdate.WindowAsset]);
        }
    }
}
