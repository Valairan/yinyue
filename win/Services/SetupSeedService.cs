using System;
using System.IO;
using Microsoft.Win32;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Applies the choices made in the installer, once, on the first launch after it ran.
    ///
    /// The installer writes them to <c>HKCU\Software\Yinyue\Setup</c> and stops there. It
    /// deliberately does not write <c>config.json</c> itself: an MSI would need a custom
    /// action to do that, custom actions are the fragile part of any MSI, and the installer
    /// has no way to validate what it collected. Handing the values to the app instead keeps
    /// the package declarative — files, a shortcut, three registry values — and puts the
    /// logic somewhere it can be tested.
    ///
    /// The key is deleted after it is read, so a value applies exactly once. Re-running the
    /// installer writes it again and the choice takes effect again, which is what someone
    /// re-running setup and picking a different corner expects.
    /// </summary>
    public static class SetupSeedService
    {
        private const string KeyPath = @"Software\Yinyue\Setup";

        /// <summary>What the installer left behind, or null when it left nothing.</summary>
        public sealed class Seed
        {
            public OverlayAnchor? Anchor { get; init; }
            public bool? StartWithWindows { get; init; }
            public string? LibraryFolder { get; init; }

            /// <summary>False when every value was absent or unusable.</summary>
            public bool HasAnything =>
                Anchor.HasValue || StartWithWindows.HasValue || LibraryFolder != null;
        }

        /// <summary>
        /// Reads and removes the seed. Returns null when the installer left nothing — the
        /// normal case on every launch but the first after an install.
        /// </summary>
        public static Seed? Take()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                if (key == null) return null;

                var seed = Read(key);

                // Removed whether or not anything usable was found: a malformed seed that
                // stayed would be re-read, and re-rejected, on every launch forever.
                Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);

                return seed.HasAnything ? seed : null;
            }
            catch (Exception ex)
            {
                // A seed is a convenience. Failing to read it must never stop the app.
                System.Diagnostics.Debug.WriteLine($"[Setup] Seed read failed: {ex.Message}");
                return null;
            }
        }

        private static Seed Read(RegistryKey key)
        {
            OverlayAnchor? anchor = null;
            if (key.GetValue("OverlayAnchor") is string anchorText &&
                Enum.TryParse(anchorText, ignoreCase: true, out OverlayAnchor parsed))
            {
                anchor = parsed;
            }

            bool? startup = key.GetValue("StartWithWindows") is int flag ? flag != 0 : null;

            string? folder = null;
            if (key.GetValue("LibraryFolder") is string path && !string.IsNullOrWhiteSpace(path))
            {
                // The installer cannot know the folder still exists by the time the app runs,
                // and a phantom entry in the library list would be a puzzle to explain.
                try { if (Directory.Exists(path)) folder = path; }
                catch { /* An unreadable path is simply not offered. */ }
            }

            return new Seed { Anchor = anchor, StartWithWindows = startup, LibraryFolder = folder };
        }

        /// <summary>
        /// Folds a seed into the configuration. Returns true when something changed and the
        /// config needs saving.
        /// </summary>
        public static bool Apply(Seed seed, AppConfig config)
        {
            bool changed = false;

            if (seed.Anchor is { } anchor && config.Overlay.Anchor != anchor)
            {
                config.Overlay.Anchor = anchor;
                changed = true;
            }

            if (seed.LibraryFolder is { } folder &&
                !config.Library.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                // Added rather than assigned: re-running the installer must not throw away
                // folders added since.
                config.Library.Folders.Add(folder);
                changed = true;
            }

            if (seed.StartWithWindows is { } startup && StartupService.IsEnabled != startup)
            {
                // StartupService writes the Run key against its own executable path, so the
                // installer never has to guess where the app ended up.
                StartupService.SetEnabled(startup);
            }

            return changed;
        }
    }
}
