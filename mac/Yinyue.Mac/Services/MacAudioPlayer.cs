using System.Diagnostics;
using AVFoundation;
using CoreMedia;
using Foundation;

namespace Yinyue.Services
{
    /// <summary>
    /// The macOS audio engine: <see cref="IAudioPlayer"/> over <see cref="AVPlayer"/>, which
    /// handles local files and HTTP streams alike, FLAC included since 10.13.
    ///
    /// The interface is the whole contract. Everything above it — queue, play order, shuffle,
    /// loop, retry, prebuffer, resume points — is <see cref="PlaybackService"/> in Core, the
    /// same code the Windows app runs. Nothing here knows about any of it.
    /// </summary>
    public sealed class MacAudioPlayer : IAudioPlayer
    {
        /// <summary>
        /// Measured with AVURLAsset.audiovisualTypes() on macOS 26 — see
        /// mac/spikes/avcaps/FINDINGS.md — not copied from the Windows list.
        ///
        /// `alac` is here deliberately and is not a file extension: ALAC is a codec inside an
        /// .m4a container, so nothing on disk carries it. This list is matched against
        /// Jellyfin's *container* names as well as against extensions, and Jellyfin reports
        /// alac — dropping it would silently transcode every ALAC track on the server.
        ///
        /// opus and ogg are the reverse case: they direct-play here and transcode on Windows.
        /// </summary>
        private static readonly string[] Containers =
        {
            "mp3", "aac", "m4a", "flac", "alac", "wav",
            "aiff", "aif", "caf", "opus", "ogg", "oga",
            "ac3", "eac3", "mp2", "m4b",
        };

        private readonly AVPlayer _player = new();

        /// <summary>
        /// Fires every 250 ms while playing, matching the Windows cadence. AVFoundation calls
        /// the observer on the queue we name; the interface documents that progress arrives
        /// off the UI thread on both platforms, so the shell marshals, not us.
        /// </summary>
        private NSObject? _timeObserver;

        private NSObject? _endObserver;
        private NSObject? _failObserver;

        /// <summary>An item opened ahead of time for the track we expect to play next.</summary>
        private AVPlayerItem? _prepared;
        private string? _preparedUrl;

        /// <summary>
        /// A resume point known before the media is ready to honour it. AVPlayer silently
        /// drops a seek issued against an item that has not loaded, so it is held here and
        /// applied once the item reports ready.
        /// </summary>
        private TimeSpan? _pendingSeek;

        private double _volume = 1.0;

        public event EventHandler<AudioProgressEventArgs>? ProgressUpdated;
        public event EventHandler? PlaybackEnded;
        public event EventHandler<string>? MediaFailed;

        public IReadOnlyCollection<string> SupportedContainers => Containers;

        public MacAudioPlayer()
        {
            // Every 250 ms, on the main queue. AVPlayer requires a serial queue here and the
            // main one is the only queue guaranteed to outlive the player.
            _timeObserver = (NSObject)_player.AddPeriodicTimeObserver(
                CMTime.FromSeconds(0.25, 1000),
                CoreFoundation.DispatchQueue.MainQueue,
                _ => OnProgressTick());
        }

        /// <summary>
        /// Anything but Paused counts as playing.
        ///
        /// AVPlayer reaches <c>Playing</c> asynchronously: immediately after Play() it sits in
        /// <c>WaitingToPlayAtSpecifiedRate</c> while it fills its buffer. Testing for Playing
        /// alone means the state read back straight after a transport call is still the old
        /// one — which showed up as the play glyph not changing when playback started.
        /// </summary>
        public bool IsPlaying => _player.TimeControlStatus != AVPlayerTimeControlStatus.Paused;

        public TimeSpan Position
        {
            get
            {
                var t = _player.CurrentTime;
                return t.IsInvalid || t.IsIndefinite ? TimeSpan.Zero : TimeSpan.FromSeconds(t.Seconds);
            }
        }

        public TimeSpan Duration
        {
            get
            {
                var d = _player.CurrentItem?.Duration ?? CMTime.Invalid;
                return d.IsInvalid || d.IsIndefinite ? TimeSpan.Zero : TimeSpan.FromSeconds(d.Seconds);
            }
        }

        public bool HasSource => _player.CurrentItem != null;

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0, 1.0);
                _player.Volume = (float)_volume;
            }
        }

        public async Task PlayAsync(string pathOrUrl)
        {
            var item = TakePreparedOrCreate(pathOrUrl);
            if (item is null)
            {
                MediaFailed?.Invoke(this, $"Could not open {pathOrUrl}");
                return;
            }

            Load(item);
            _player.Play();
            await Task.CompletedTask;
        }

        public Task PlayFileAsync(string filePath) => PlayAsync(filePath);

        public Task PlayAsync()
        {
            _player.Play();
            return Task.CompletedTask;
        }

        public Task PauseAsync()
        {
            _player.Pause();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Opens the next track's stream while the current one plays. Fire-and-forget by
        /// contract: a failure here is an optimisation not taken, never a playback error, so
        /// nothing raises MediaFailed.
        /// </summary>
        public Task PrepareAsync(string url, CancellationToken ct = default)
        {
            try
            {
                if (_preparedUrl == url && _prepared != null) return Task.CompletedTask;

                DiscardPrepared();

                var item = CreateItem(url);
                if (item is null) return Task.CompletedTask;

                _prepared = item;
                _preparedUrl = url;

                // Asking for the duration is what makes AVFoundation start filling the
                // buffer; without a load the item sits inert and the prebuffer buys nothing.
                item.Asset.LoadValuesAsynchronously(new[] { "duration", "playable" }, () => { });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] Prebuffer failed, ignoring: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public void DiscardPrepared()
        {
            _prepared?.Dispose();
            _prepared = null;
            _preparedUrl = null;
        }

        public void Seek(TimeSpan targetPosition) =>
            _player.Seek(CMTime.FromSeconds(targetPosition.TotalSeconds, 1000));

        public void Seek(double percentage)
        {
            var total = Duration;
            if (total <= TimeSpan.Zero) return;

            double clamped = Math.Clamp(percentage, 0.0, 100.0);
            Seek(TimeSpan.FromSeconds(total.TotalSeconds * clamped / 100.0));
        }

        public void SeekWhenReady(TimeSpan position) => _pendingSeek = position;

        // -------------------------------------------------------------------------------

        private AVPlayerItem? TakePreparedOrCreate(string pathOrUrl)
        {
            if (_preparedUrl == pathOrUrl && _prepared != null)
            {
                var ready = _prepared;
                _prepared = null;
                _preparedUrl = null;
                return ready;
            }

            DiscardPrepared();
            return CreateItem(pathOrUrl);
        }

        /// <summary>
        /// A local path and an HTTP URL need different NSUrl constructors, and handing a
        /// file path to the URL one yields a null that only surfaces later as silence.
        /// </summary>
        private static AVPlayerItem? CreateItem(string pathOrUrl)
        {
            NSUrl? url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new NSUrl(pathOrUrl)
                : NSUrl.FromFilename(pathOrUrl);

            if (url is null) return null;

            var asset = new AVUrlAsset(url);
            return new AVPlayerItem(asset);
        }

        private void Load(AVPlayerItem item)
        {
            DetachItemObservers();

            _endObserver = AVPlayerItem.Notifications.ObserveDidPlayToEndTime(
                item, (_, _) => PlaybackEnded?.Invoke(this, EventArgs.Empty));

            // AVFoundation reports a failed *item* rather than a failed player, and reports it
            // by notification rather than by throwing — so without this a dead stream is
            // simply silence, and PlaybackService never gets to retry or skip.
            _failObserver = AVPlayerItem.Notifications.ObserveItemFailedToPlayToEndTime(
                item, (_, e) => MediaFailed?.Invoke(this,
                    e.Error?.LocalizedDescription ?? "Playback failed."));

            _player.ReplaceCurrentItemWithPlayerItem(item);
            _player.Volume = (float)_volume;
        }

        private void OnProgressTick()
        {
            ApplyPendingSeekIfReady();

            var position = Position;
            var duration = Duration;
            if (duration <= TimeSpan.Zero) return;

            ProgressUpdated?.Invoke(this, new AudioProgressEventArgs(position, duration));
        }

        /// <summary>
        /// A seek issued before the item is ready is dropped by AVPlayer without error, so the
        /// resume point is held and applied on the first tick that reports readiness.
        /// </summary>
        private void ApplyPendingSeekIfReady()
        {
            if (_pendingSeek is not { } target) return;
            if (_player.CurrentItem?.Status != AVPlayerItemStatus.ReadyToPlay) return;

            _pendingSeek = null;
            Seek(target);
        }

        private void DetachItemObservers()
        {
            _endObserver?.Dispose();
            _endObserver = null;
            _failObserver?.Dispose();
            _failObserver = null;
        }

        public void Dispose()
        {
            if (_timeObserver != null)
            {
                _player.RemoveTimeObserver(_timeObserver);
                _timeObserver.Dispose();
                _timeObserver = null;
            }

            DetachItemObservers();
            DiscardPrepared();
            _player.Pause();
            _player.Dispose();
        }
    }
}
