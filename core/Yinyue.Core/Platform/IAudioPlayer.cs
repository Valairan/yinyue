using System;
using System.Threading;
using System.Threading.Tasks;

namespace Yinyue.Services
{
    /// <summary>
    /// Progress from the audio engine. Lives here rather than beside the engine because
    /// <see cref="PlaybackService"/> re-raises it and <c>JellyfinPlaybackReporter</c>
    /// consumes it — both of which are platform-neutral.
    /// </summary>
    public class AudioProgressEventArgs : EventArgs
    {
        public TimeSpan CurrentTime { get; }
        public TimeSpan TotalTime { get; }
        public double ProgressPercentage { get; }

        public AudioProgressEventArgs(TimeSpan current, TimeSpan total)
        {
            CurrentTime = current;
            TotalTime = total;
            ProgressPercentage = total.TotalSeconds > 0
                ? (current.TotalSeconds / total.TotalSeconds) * 100.0
                : 0.0;
        }
    }

    /// <summary>
    /// The audio engine, as <see cref="PlaybackService"/> needs it. Windows implements this
    /// over the WinRT <c>MediaPlayer</c>; macOS will implement it over <c>AVPlayer</c>.
    ///
    /// This is the whole surface — everything else about playback (queue, play order,
    /// shuffle, loop, retry, prebuffer) sits above it in <see cref="PlaybackService"/> and
    /// is shared. Resist widening it: an engine capability that only one platform has
    /// belongs behind a capability check, not in this interface.
    /// </summary>
    public interface IAudioPlayer : IDisposable
    {
        /// <summary>
        /// Fires on a timer while playing, off the UI thread on both platforms. Shells must
        /// marshal before touching UI.
        /// </summary>
        event EventHandler<AudioProgressEventArgs>? ProgressUpdated;

        /// <summary>The current track reached its end on its own.</summary>
        event EventHandler? PlaybackEnded;

        /// <summary>
        /// The engine could not play what it was given. <see cref="PlaybackService"/>
        /// re-resolves and retries once before skipping on, so this is not fatal.
        /// </summary>
        event EventHandler<string>? MediaFailed;

        bool IsPlaying { get; }
        TimeSpan Position { get; }
        TimeSpan Duration { get; }

        /// <summary>False before anything has been loaded, which gates play/pause.</summary>
        bool HasSource { get; }

        /// <summary>0.0–1.0, the app's own level, independent of the OS mixer.</summary>
        double Volume { get; set; }

        /// <summary>Load <paramref name="pathOrUrl"/> and begin playing it.</summary>
        Task PlayAsync(string pathOrUrl);

        /// <summary>
        /// Open a stream ahead of time without playing it — the prebuffer path. Remote
        /// sources only; a failure here is an optimisation not taken, never an error.
        /// </summary>
        Task PrepareAsync(string url, CancellationToken ct = default);

        /// <summary>Drop anything <see cref="PrepareAsync"/> opened, so it cannot go stale.</summary>
        void DiscardPrepared();

        Task PlayAsync();
        Task PauseAsync();
        Task PlayFileAsync(string filePath);

        void Seek(TimeSpan targetPosition);
        void Seek(double percentage);

        /// <summary>
        /// Seek as soon as the engine has enough of the stream to honour it. Used for
        /// resume points, which are known before the media opens.
        /// </summary>
        void SeekWhenReady(TimeSpan position);
    }
}
