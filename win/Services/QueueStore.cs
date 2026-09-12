using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// What was playing, so a restart does not lose your place.
    /// </summary>
    public class QueueState
    {
        public List<Track> Tracks { get; set; } = new();

        /// <summary>Index into <see cref="Tracks"/>, which is stored in play order.</summary>
        public int Position { get; set; } = -1;

        public double TrackPositionSeconds { get; set; }

        public bool Shuffle { get; set; }
        public LoopMode Loop { get; set; }

        public DateTime SavedUtc { get; set; }
    }

    /// <summary>
    /// Persists the queue to %APPDATA%\Yinyue\queue.json.
    ///
    /// Kept out of config.json deliberately: config is small and meant to be hand-editable,
    /// while a queue can run to hundreds of entries and churns constantly.
    /// </summary>
    public class QueueStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// A queue older than this is not worth restoring — whatever you were listening to
        /// a week ago is not what you want resumed today.
        /// </summary>
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        private readonly string _path;

        public QueueStore()
        {
            _path = Path.Combine(ConfigService.AppDataFolder, "queue.json");
        }

        public async Task SaveAsync(QueueState state)
        {
            try
            {
                state.SavedUtc = DateTime.UtcNow;

                string json = JsonSerializer.Serialize(state, Options);

                // Same temp-then-move as ConfigService: an interrupted write must not leave
                // a truncated file behind.
                string temp = _path + ".tmp";
                await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Queue] Save failed: {ex.Message}");
            }
        }

        public QueueState? Load()
        {
            try
            {
                if (!File.Exists(_path)) return null;

                var state = JsonSerializer.Deserialize<QueueState>(File.ReadAllText(_path), Options);

                if (state == null || state.Tracks.Count == 0) return null;
                if (DateTime.UtcNow - state.SavedUtc > MaxAge) return null;

                return state;
            }
            catch (Exception ex)
            {
                // A corrupt queue is not worth surfacing — start empty and move on.
                System.Diagnostics.Debug.WriteLine($"[Queue] Load failed: {ex.Message}");
                return null;
            }
        }

        public void Delete()
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
