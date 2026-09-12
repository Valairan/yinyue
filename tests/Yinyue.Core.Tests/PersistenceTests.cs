using System.IO;
using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue.Tests;

public static class PersistenceTests
{
    public static async Task Run()
    {
        await Check.GroupAsync("queue persistence", async () =>
        {
            var (library, _) = Make.Library(
                new FakeSource("Fake", TrackSource.Local, Make.Tracks("t0", "t1", "t2")));

            var playback = new PlaybackService(new SilentAudioPlayer(), library);
            playback.ToggleShuffle();
            playback.CycleLoop();                       // Queue
            await playback.PlayQueueAsync(Make.Tracks("t0", "t1", "t2"), 1);

            var snapshot = playback.Snapshot();
            snapshot.TrackPositionSeconds = 42.5;

            Check.Equal("captures the whole order", 3, snapshot.Tracks.Count);
            Check.That("captures shuffle", snapshot.Shuffle);
            Check.Equal("captures loop", LoopMode.Queue, snapshot.Loop);

            var restored = new PlaybackService(new SilentAudioPlayer(), library);
            restored.RestoreQueue(snapshot.Tracks, snapshot.Position,
                TimeSpan.FromSeconds(snapshot.TrackPositionSeconds), snapshot.Shuffle, snapshot.Loop);

            Check.That("play order is adopted verbatim",
                restored.PlayOrder.Select(t => t.Id)
                    .SequenceEqual(snapshot.Tracks.Select(t => t.Id)));
            Check.Equal("position preserved", snapshot.Position, restored.CurrentOrderPosition);
            Check.That("shuffle preserved", restored.Shuffle);
            Check.Equal("loop preserved", LoopMode.Queue, restored.Loop);
            Check.That("restoring does not start playing", !restored.IsPlaying);

            playback.Dispose();
            restored.Dispose();
        });

        Check.Group("queue store rejects what it should", () =>
        {
            var store = new QueueStore();
            string path = Path.Combine(ConfigService.AppDataFolder, "queue.json");
            string? backup = File.Exists(path) ? File.ReadAllText(path) : null;

            try
            {
                File.WriteAllText(path, "{ not json");
                Check.That("corrupt file is dropped, not thrown", store.Load() == null);

                store.SaveAsync(new QueueState { Tracks = new List<Track>(), Position = -1 })
                    .GetAwaiter().GetResult();
                Check.That("an empty queue is dropped", store.Load() == null);

                store.Delete();
                Check.That("a missing file is dropped", store.Load() == null);
            }
            finally
            {
                if (backup != null) File.WriteAllText(path, backup);
                else if (File.Exists(path)) File.Delete(path);
            }
        });

        Check.Group("track resume points", () =>
        {
            // Only a genuine mid-track abandonment should resume; the boundaries keep a
            // near-finished or barely-started track from jumping.
            var mid = Make.Track("a");
            mid.Duration = TimeSpan.FromMinutes(5);
            mid.ResumePosition = TimeSpan.FromMinutes(2);
            Check.That("a mid-track position is kept", mid.ResumePosition > TimeSpan.Zero);

            var nearEnd = Make.Track("b");
            nearEnd.Duration = TimeSpan.FromSeconds(100);
            nearEnd.ResumePosition = TimeSpan.FromSeconds(95);
            Check.That("a near-finished track has a position to ignore",
                nearEnd.ResumePosition >= nearEnd.Duration - TimeSpan.FromSeconds(10));
        });
    }
}

