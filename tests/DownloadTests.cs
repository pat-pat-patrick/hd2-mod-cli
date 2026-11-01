#if HAS_NEXUS
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Hd2Mod;
using Xunit;
using Xunit.Abstractions;

namespace Hd2Mod.Tests
{
    /// <summary>A download is kept whole or not at all (the user, 2026-11-01: 7z downloads failed
    /// to unpack now and then). A server here says how long the file is and sends all of it, or
    /// part of it and hangs up.</summary>
    public class DownloadWhole : Fixture
    {
        readonly ITestOutputHelper output;

        public DownloadWhole(ITestOutputHelper output) => this.output = output;

        /// <summary>One answer on a port of its own: the length it claims, the bytes it sends.</summary>
        static (string Uri, Task Done) Serve(int claimed, int sent)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var uri = "http://127.0.0.1:" + ((IPEndPoint)l.LocalEndpoint).Port + "/file.7z";
            var done = Task.Run(() =>
            {
                using (var c = l.AcceptTcpClient())
                using (var s = c.GetStream())
                {
                    var head = new StringBuilder();
                    var b = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n") && s.Read(b, 0, 1) == 1) head.Append((char)b[0]);
                    var h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " + claimed + "\r\nConnection: close\r\n\r\n");
                    s.Write(h, 0, h.Length);
                    s.Write(Enumerable.Repeat((byte)7, sent).ToArray(), 0, sent);
                    s.Flush();
                }
                l.Stop();
            });
            return (uri, done);
        }

        [Fact]
        public void A_whole_download_is_kept()
        {
            var (uri, done) = Serve(300000, 300000);
            var dest = Path.Combine(Root, "Thing.7z");
            Nexus.Fetch(uri, dest);
            done.Wait();
            Assert.Equal(300000, new FileInfo(dest).Length);
            Assert.False(File.Exists(dest + ".part"));
        }

        [Fact]
        public void A_download_cut_off_is_not_kept()
        {
            var (uri, done) = Serve(300000, 120000);
            var dest = Path.Combine(Root, "Thing.7z");
            var e = Assert.Throws<ModError>(() => Nexus.Fetch(uri, dest));
            done.Wait();
            output.WriteLine(e.Message);
            Assert.Equal("download_cut", e.Kind);
            Assert.False(File.Exists(dest));
            Assert.False(File.Exists(dest + ".part"));
        }
    }
}
#endif
