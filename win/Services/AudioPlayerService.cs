using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Yinyue.Services
{
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

    public class AudioPlayerService : IDisposable
    {
        private readonly MediaPlayer _mediaPlayer;
        private readonly System.Timers.Timer _progressTimer;
        private TimeSpan _pendingSeek;

        // A MediaSource opened ahead of time for the track we expect to play next.
        private MediaSource? _prepared;
        private string? _preparedUri;

        public event EventHandler<AudioProgressEventArgs>? ProgressUpdated;
        public event EventHandler? PlaybackEnded;

        /// <summary>
        /// The engine could not play the current source — a dropped connection, an expired
        /// transcode session, a codec it cannot handle. Without this, a network blip simply
        /// stops the music with no error and no advance.
        /// </summary>
        public event EventHandler<string>? MediaFailed;

        public bool IsPlaying => _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        public TimeSpan Position => _mediaPlayer.PlaybackSession.Position;
        public TimeSpan Duration => _mediaPlayer.PlaybackSession.NaturalDuration;

        /// <summary>False after a restore, when a track is selected but never opened.</summary>
        public bool HasSource => _mediaPlayer.Source != null;

        /// <summary>
        /// Output level, 0.0 to 1.0. This is the app's own volume, independent of the
        /// Windows mixer — changing it here does not touch the system volume.
        /// </summary>
        public double Volume
        {
            get => _mediaPlayer.Volume;
            set => _mediaPlayer.Volume = Math.Clamp(value, 0.0, 1.0);
        }

        public AudioPlayerService()
        {
            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

            // MediaPlayer.CommandManager responds to transport commands on its own, which
            // would double-handle every media key press alongside SmtcService. Yinyue owns
            // transport itself because CommandManager knows nothing about our queue,
            // shuffle, or loop semantics.
            _mediaPlayer.CommandManager.IsEnabled = false;

            // Timer fires every 250ms on threadpool
            _progressTimer = new System.Timers.Timer(250);
            _progressTimer.Elapsed += (s, e) => OnProgressTick();
        }

        public async Task PlayAsync(string pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl))
                return;

            _progressTimer.Stop();
            _mediaPlayer.Pause();

            // Hand over the pre-opened source if this is the track we were expecting.
            MediaSource mediaSource;
            if (_prepared != null && string.Equals(_preparedUri, pathOrUrl, StringComparison.Ordinal))
            {
                mediaSource = _prepared;
                _prepared = null;
                _preparedUri = null;
            }
            else
            {
                DiscardPrepared();
                mediaSource = await CreateSourceAsync(pathOrUrl).ConfigureAwait(false);
            }

            _mediaPlayer.Source = mediaSource;
            _mediaPlayer.Play();
            _progressTimer.Start();
        }

        /// <summary>
        /// Opens the source for a track we expect to play next, so the switch does not pay
        /// for a fresh connection and the initial buffer.
        ///
        /// Only worth doing for streams: a local file opens instantly, and holding a second
        /// StorageFile open buys nothing.
        /// </summary>
        public async Task PrepareAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!IsRemote(url)) return;
            if (string.Equals(_preparedUri, url, StringComparison.Ordinal)) return;

            DiscardPrepared();

            try
            {
                var source = MediaSource.CreateFromUri(new Uri(url));
                await source.OpenAsync().AsTask(ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                {
                    source.Dispose();
                    return;
                }

                _prepared = source;
                _preparedUri = url;
            }
            catch (OperationCanceledException)
            {
                // Superseded; nothing to keep.
            }
            catch (Exception ex)
            {
                // Pre-buffering is an optimisation. A failure here must not stop the track
                // playing normally when its turn comes.
                System.Diagnostics.Debug.WriteLine($"[Audio] Prebuffer failed: {ex.Message}");
                DiscardPrepared();
            }
        }

        public void DiscardPrepared()
        {
            _prepared?.Dispose();
            _prepared = null;
            _preparedUri = null;
        }

        private static bool IsRemote(string pathOrUrl) =>
            Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        private static async Task<MediaSource> CreateSourceAsync(string pathOrUrl)
        {
            if (IsRemote(pathOrUrl))
                return MediaSource.CreateFromUri(new Uri(pathOrUrl));

            if (!File.Exists(pathOrUrl))
                throw new FileNotFoundException("Local audio file not found.", pathOrUrl);

            // Ensure file access doesn't block calling thread context
            var file = await Windows.Storage.StorageFile
                .GetFileFromPathAsync(pathOrUrl).AsTask().ConfigureAwait(false);

            return MediaSource.CreateFromStorageFile(file);
        }

        public async Task PlayAsync()
        {
            _mediaPlayer.Play();
            _progressTimer.Start();
            await Task.CompletedTask;
        }

        public async Task PauseAsync()
        {
            _mediaPlayer.Pause();
            _progressTimer.Stop();
            await Task.CompletedTask;
        }

        public async Task PlayFileAsync(string filePath)
        {
            await PlayAsync(filePath);
        }

        public void Seek(TimeSpan targetPosition)
        {
            var duration = Duration;
            if (duration.TotalSeconds > 0)
            {
                if (targetPosition < TimeSpan.Zero) targetPosition = TimeSpan.Zero;
                if (targetPosition > duration) targetPosition = duration;

                _mediaPlayer.PlaybackSession.Position = targetPosition;
            }
        }

        public void Seek(double percentage)
        {
            var duration = Duration;
            if (duration.TotalSeconds > 0)
            {
                double targetSeconds = (duration.TotalSeconds * Math.Clamp(percentage, 0.0, 100.0)) / 100.0;
                _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(targetSeconds);
            }
        }

        private void OnProgressTick()
        {
            var session = _mediaPlayer.PlaybackSession;
            if (session.PlaybackState == MediaPlaybackState.Playing || session.PlaybackState == MediaPlaybackState.Paused)
            {
                var current = session.Position;
                var total = session.NaturalDuration;

                // Fire event safely across delegates
                ProgressUpdated?.Invoke(this, new AudioProgressEventArgs(current, total));
            }
        }

        /// <summary>
        /// Queues a seek to apply once the media is actually open. Seeking immediately
        /// after Play does nothing useful — the natural duration is not known yet, so the
        /// position is clamped away. Used to resume a restored queue mid-track.
        /// </summary>
        public void SeekWhenReady(TimeSpan position)
        {
            _pendingSeek = position > TimeSpan.Zero ? position : TimeSpan.Zero;
        }

        private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            if (_pendingSeek <= TimeSpan.Zero) return;

            var target = _pendingSeek;
            _pendingSeek = TimeSpan.Zero;

            try
            {
                var duration = sender.PlaybackSession.NaturalDuration;
                if (duration > TimeSpan.Zero && target < duration)
                    sender.PlaybackSession.Position = target;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Audio] Resume seek failed: {ex.Message}");
            }
        }

        private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            _progressTimer.Stop();

            string detail = string.IsNullOrWhiteSpace(args.ErrorMessage)
                ? args.Error.ToString()
                : $"{args.Error}: {args.ErrorMessage}";

            MediaFailed?.Invoke(this, detail);
        }

        private void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
        {
            _progressTimer.Stop();
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _progressTimer.Stop();
            _progressTimer.Dispose();
            DiscardPrepared();
            _mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
            _mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
            _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
            _mediaPlayer.Dispose();
        }
    }
}