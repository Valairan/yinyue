using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Yinyue.Services
{
    /// <summary>
    /// Disk cache for album art under %APPDATA%\Yinyue\art.
    ///
    /// Shared by every IMusicSource. SMTC needs a file or stream rather than a URL, and
    /// re-fetching or re-extracting art on every track change would show up as overlay
    /// latency, so everything lands here first.
    /// </summary>
    public class ArtworkCache
    {
        /// <summary>
        /// Cap on total cache size. Covers a few thousand covers at the sizes requested,
        /// which is far more than any realistic session touches.
        /// </summary>
        public const long DefaultMaxBytes = 50L * 1024 * 1024;

        public string Folder { get; }

        public ArtworkCache()
        {
            Folder = Path.Combine(ConfigService.AppDataFolder, "art");
            Directory.CreateDirectory(Folder);
        }

        /// <summary>
        /// Cache path for an item. <paramref name="prefix"/> namespaces by source so a
        /// local file and a Jellyfin item can never collide on id.
        /// </summary>
        public string PathFor(string prefix, string id) =>
            Path.Combine(Folder, $"{prefix}-{Sanitize(id)}.jpg");

        public static bool IsUsable(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Trims the cache to <paramref name="maxBytes"/>, evicting least-recently-written
        /// files first. Intended to run in the background at startup — it touches the disk
        /// and must not sit in the cold-start path.
        /// </summary>
        public Task<int> PruneAsync(long maxBytes = DefaultMaxBytes) => Task.Run(() =>
        {
            try
            {
                var files = new DirectoryInfo(Folder)
                    .GetFiles("*.jpg")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                long running = 0;
                int removed = 0;

                foreach (var file in files)
                {
                    running += file.Length;
                    if (running <= maxBytes) continue;

                    try
                    {
                        file.Delete();
                        removed++;
                    }
                    catch
                    {
                        // A file in use by the current SMTC thumbnail will refuse to go.
                        // Leave it; the next prune will catch it.
                    }
                }

                return removed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Artwork] Prune failed: {ex.Message}");
                return 0;
            }
        });

        /// <summary>Removes a partially written file so it cannot poison the cache.</summary>
        public static void Discard(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Best effort.
            }
        }

        private static string Sanitize(string id)
        {
            // Local ids are full file paths; hash anything that is not already filename-safe.
            if (id.Length <= 64 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
                return id;

            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(id.ToLowerInvariant()));
            return Convert.ToHexString(hash, 0, 12);
        }
    }
}
