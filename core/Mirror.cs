using System;

namespace Hd2Mod
{
    /// <summary>The author's copy of what this program downloads from GitHub: the official plugin
    /// list and its translations, each official plugin's newest release, and this program's own
    /// releases, served from mods.patpatpatrick.com/dl (Cloudflare) for players who cannot reach
    /// GitHub (the user, 2026-11-24: mainland China). The copy is made from GitHub's own files.
    ///
    /// Lists are read from GitHub first, for at most ListWait, then from the copy: GitHub's is
    /// the newer when both answer. Files are fetched from the copy first, then GitHub: each is
    /// checked against the list's SHA-256, so the copy can only be missing or stale, never
    /// trusted on its word. HD2QD_MIRROR names another copy; empty turns it off.</summary>
    public static class Mirror
    {
        public static string Base = Environment.GetEnvironmentVariable("HD2QD_MIRROR") ?? "https://mods.patpatpatrick.com/dl";

        /// <summary>How long a list waits for GitHub before the copy is asked.</summary>
        public static TimeSpan ListWait = TimeSpan.FromSeconds(8);

        public static bool On => !string.IsNullOrEmpty(Base);

        /// <summary>A file of the copy, by its path there ("plugins/plugins.json").</summary>
        public static string Url(string path) => Base.TrimEnd('/') + "/" + path;

        /// <summary>A release file's place in the copy: as on GitHub, under github/.</summary>
        public static string ReleaseFile(string repository, string tag, string asset) =>
            Url("github/" + repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(asset));
    }
}
