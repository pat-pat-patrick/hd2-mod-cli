using System.Collections.Generic;
using System.Linq;
using Hd2Mod;
using Xunit;

namespace Hd2Mod.Tests
{
    /// <summary>The notes the window shows when a newer version of this program is out: every
    /// newer release's text, in the window's language, by itself once a version.</summary>
    public class UpdateNotes : Fixture
    {
        static Dictionary<string, object> Release(string tag, string body, bool pre = false) => new Dictionary<string, object>
        {
            ["tag_name"] = tag, ["html_url"] = "https://github.com/x/y/releases/tag/" + tag, ["prerelease"] = pre, ["draft"] = false, ["body"] = body,
        };

        static readonly List<Dictionary<string, object>> Releases = new List<Dictionary<string, object>>
        {
            Release("v0.16.0-dev", "dev only", true),
            Release("v0.15.0", "fifteen"),
            Release("v0.14.0", "fourteen"),
            Release("v0.13.0", "thirteen"),
        };

        [Fact]
        public void The_notes_are_every_release_newer_than_the_one_running_newest_first()
        {
            var dev = SelfUpdate.Pick(Releases, "0.13.0", false);
            Assert.Equal(new[] { "v0.16.0-dev", "v0.15.0", "v0.14.0" }, dev.Notes.Select(n => n.Tag));
            Assert.Equal("fifteen", dev.Notes[1].Body);
            var official = SelfUpdate.Pick(Releases, "0.13.0", true);
            Assert.Equal(new[] { "v0.15.0", "v0.14.0" }, official.Notes.Select(n => n.Tag));    // no pre-release for it
            Assert.Empty(SelfUpdate.Pick(Releases, "0.16.0", false).Notes);
        }

        [Fact]
        public void A_release_text_in_both_languages_gives_each_its_own()
        {
            var body = "## 0.15.0\n\n<!-- zh -->\n- 新功能\n<!-- en -->\n- New feature\n";
            var (zh, zhSame) = SelfUpdate.NotesIn(body, true);
            var (en, enSame) = SelfUpdate.NotesIn(body, false);
            Assert.Equal("## 0.15.0\n\n- 新功能", zh);
            Assert.Equal("## 0.15.0\n\n- New feature", en);
            Assert.False(zhSame);
            Assert.False(enSame);
            // English first, CRLF, spaces around the marker: the same
            Assert.Equal("- 新功能", SelfUpdate.NotesIn("  <!-- en -->  \r\n- New\r\n<!--zh-->\r\n- 新功能", true).Text);
            // a text with no markers is the same in both
            Assert.Equal(("- Only English", true), SelfUpdate.NotesIn("- Only English\n", true));
            // markers for one language only: the other gets all of it
            var (onlyZh, same) = SelfUpdate.NotesIn("<!-- zh -->\n- 新功能", false);
            Assert.Equal("- 新功能", onlyZh);
            Assert.True(same);
            // a marker inside a line is text, not a marker
            Assert.Equal("see <!-- zh --> here", SelfUpdate.NotesIn("see <!-- zh --> here", true).Text);
        }

        [Fact]
        public void The_notes_show_by_themselves_once_a_version_and_go_with_an_update()
        {
            Store.Save("self_update.json", new Dictionary<string, object>
            {
                ["checked_at"] = 1L, ["latest"] = "v99.0.0", ["page"] = "https://example.invalid/", ["prerelease"] = false,
                ["notes"] = new List<object>
                {
                    new Dictionary<string, object> { ["tag"] = "v99.0.0", ["page"] = "p99", ["body"] = "ninety-nine" },
                    new Dictionary<string, object> { ["tag"] = "v0.0.1", ["page"] = "p1", ["body"] = "older than any build" },
                },
            });
            var u = SelfUpdate.Last();
            Assert.True(u.Newer);
            Assert.Equal(new[] { "v99.0.0" }, u.Notes.Select(n => n.Tag));       // the one older than the build running is gone
            Assert.True(SelfUpdate.NotesDue(u));
            SelfUpdate.NotesShown(u.Latest);
            Assert.False(SelfUpdate.NotesDue(SelfUpdate.Last()));

            // a newer one again: due again
            var d = Store.Load("self_update.json");
            d["latest"] = "v100.0.0";
            Store.Save("self_update.json", d);
            Assert.True(SelfUpdate.NotesDue(SelfUpdate.Last()));

            // nothing newer than the build running: never due
            d["latest"] = "v0.0.1";
            Store.Save("self_update.json", d);
            Assert.False(SelfUpdate.NotesDue(SelfUpdate.Last()));
        }
    }
}
