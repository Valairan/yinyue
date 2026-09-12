using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue.Tests;

public static class VolumeTests
{
    public static void Run()
    {
        Check.Group("volume and mute", () =>
        {
            var (library, _) = Make.Library(new FakeSource("Fake", TrackSource.Local, Make.Tracks("t0")));
            var playback = new PlaybackService(new SilentAudioPlayer(), library);

            playback.Volume = 2;
            Check.Equal("clamped to the ceiling", 1.0, playback.Volume);
            playback.Volume = -1;
            Check.Equal("clamped to the floor", 0.0, playback.Volume);

            playback.Volume = 0.65;
            Check.That("mute reports muted", playback.ToggleMute());
            Check.Equal("engine silenced", 0.0, playback.Volume);
            Check.Equal("but the level is remembered", 0.65, playback.EffectiveVolume);

            Check.That("unmute reports unmuted", !playback.ToggleMute());
            Check.Equal("restored", 0.65, playback.Volume);

            playback.Volume = 0.4;
            playback.ToggleMute();
            Check.Equal("volume up restores rather than creeping", 0.4, playback.AdjustVolume(0.05));
            Check.That("and clears mute", !playback.IsMuted);

            playback.Volume = 0.5;
            playback.ToggleMute();
            playback.Volume = 0.3;
            Check.That("any audible level clears mute", !playback.IsMuted);

            playback.Volume = 0.0;
            playback.ToggleMute();
            playback.ToggleMute();
            Check.That("unmuting from silence gives something audible", playback.Volume > 0);

            // Repeated 0.05 steps otherwise drift to values like 0.6499999999999997.
            playback.Volume = 0.5;
            for (int i = 0; i < 3; i++) playback.AdjustVolume(0.05);
            Check.Equal("steps stay on clean values", 0.65, playback.Volume);

            playback.Dispose();
        });
    }
}

