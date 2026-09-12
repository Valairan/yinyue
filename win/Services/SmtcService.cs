using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Storage.Streams;

namespace Yinyue.Services
{
    /// <summary>
    /// Windows System Media Transport Controls: the media keys, the volume-flyout now
    /// playing panel, and the lock screen.
    ///
    /// Bound to the overlay's HWND via SystemMediaTransportControlsInterop, which is the
    /// desktop entry point — the UWP GetForCurrentView() does not apply here.
    /// </summary>
    public class SmtcService : IDisposable
    {
        private SystemMediaTransportControls? _smtc;
        private SystemMediaTransportControlsDisplayUpdater? _updater;
        private bool _isInitialized;

        /// <summary>Keeps the thumbnail stream alive for as long as SMTC may read it.</summary>
        private InMemoryRandomAccessStream? _thumbnailStream;

        private TimeSpan _lastReportedPosition = TimeSpan.MinValue;

        public event EventHandler? PlayRequested;
        public event EventHandler? PauseRequested;
        public event EventHandler? NextRequested;
        public event EventHandler? PreviousRequested;

        /// <summary>The user scrubbed in the Windows media flyout.</summary>
        public event EventHandler<TimeSpan>? SeekRequested;

        public void Initialize(IntPtr windowHandle)
        {
            if (_isInitialized) return;

            try
            {
                _smtc = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
                _smtc.IsEnabled = true;
                _smtc.IsPlayEnabled = true;
                _smtc.IsPauseEnabled = true;
                _smtc.IsNextEnabled = true;
                _smtc.IsPreviousEnabled = true;

                // There is no "enable position" switch: the scrubber appears once timeline
                // properties are supplied, and drags come back via
                // PlaybackPositionChangeRequested.

                // Windows only shows the flyout once PlaybackStatus is something other than
                // Closed, so an unset status looks like SMTC silently not working.
                _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;

                _smtc.ButtonPressed += OnSmtcButtonPressed;
                _smtc.PlaybackPositionChangeRequested += OnPositionChangeRequested;

                _updater = _smtc.DisplayUpdater;
                _updater.Type = MediaPlaybackType.Music;
                _updater.Update();

                _isInitialized = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC] Failed to initialize: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the track shown on the OS surfaces.
        /// </summary>
        public async Task UpdateMetadataAsync(string title, string artist,
            string? albumArtPath = null, byte[]? albumArtBytes = null)
        {
            if (!_isInitialized || _updater == null) return;

            // Type must be set before the music properties are written, and ClearAll resets
            // it, so re-assert it every time rather than only at startup.
            _updater.Type = MediaPlaybackType.Music;
            _updater.MusicProperties.Title = title;
            _updater.MusicProperties.Artist = artist;

            await SetThumbnailAsync(albumArtPath, albumArtBytes).ConfigureAwait(true);

            _updater.Update();
        }

        private async Task SetThumbnailAsync(string? albumArtPath, byte[]? albumArtBytes)
        {
            if (_updater == null) return;

            DisposeThumbnail();

            if (albumArtBytes is { Length: > 0 })
            {
                try
                {
                    var stream = new InMemoryRandomAccessStream();

                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(albumArtBytes);
                        await writer.StoreAsync();
                        await writer.FlushAsync();

                        // Without this the writer closes the stream it wraps, and SMTC is
                        // handed a dead stream — art that works only intermittently.
                        writer.DetachStream();
                    }

                    _thumbnailStream = stream;
                    _updater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SMTC] Stream thumbnail failed: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(albumArtPath) && File.Exists(albumArtPath))
            {
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(albumArtPath);
                    _updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SMTC] File thumbnail failed: {ex.Message}");
                }
            }

            _updater.Thumbnail = null;
        }

        /// <summary>
        /// Reports the real transport state. Collapsing everything into playing/paused
        /// leaves the flyout claiming a session exists after the queue has finished.
        /// </summary>
        public void UpdatePlaybackStatus(MediaPlaybackStatus status)
        {
            if (!_isInitialized || _smtc == null) return;
            _smtc.PlaybackStatus = status;
        }

        public void UpdatePlaybackStatus(bool isPlaying) =>
            UpdatePlaybackStatus(isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused);

        /// <summary>
        /// Feeds the flyout's scrubber.
        ///
        /// Throttled to roughly once a second by the caller's own timing plus the guard
        /// here: this crosses a process boundary, and the progress tick fires four times a
        /// second against an idle-CPU budget.
        /// </summary>
        public void UpdateTimeline(TimeSpan position, TimeSpan duration)
        {
            if (!_isInitialized || _smtc == null) return;
            if (duration <= TimeSpan.Zero) return;

            if (_lastReportedPosition != TimeSpan.MinValue &&
                Math.Abs((position - _lastReportedPosition).TotalMilliseconds) < 900)
            {
                return;
            }

            _lastReportedPosition = position;

            try
            {
                _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    MinSeekTime = TimeSpan.Zero,
                    Position = position,
                    MaxSeekTime = duration,
                    EndTime = duration
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC] Timeline update failed: {ex.Message}");
            }
        }

        /// <summary>Clears the session so the flyout stops advertising us.</summary>
        public void ClearSession()
        {
            if (!_isInitialized) return;

            _lastReportedPosition = TimeSpan.MinValue;
            UpdatePlaybackStatus(MediaPlaybackStatus.Closed);

            try
            {
                _updater?.ClearAll();
                _updater?.Update();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC] Clear failed: {ex.Message}");
            }

            DisposeThumbnail();
        }

        private void OnSmtcButtonPressed(SystemMediaTransportControls sender,
            SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    PlayRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    PauseRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Next:
                    NextRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    PreviousRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        private void OnPositionChangeRequested(SystemMediaTransportControls sender,
            PlaybackPositionChangeRequestedEventArgs args) =>
            SeekRequested?.Invoke(this, args.RequestedPlaybackPosition);

        private void DisposeThumbnail()
        {
            _thumbnailStream?.Dispose();
            _thumbnailStream = null;
        }

        public void Dispose()
        {
            if (_smtc != null)
            {
                _smtc.ButtonPressed -= OnSmtcButtonPressed;
                _smtc.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
                _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                _smtc.IsEnabled = false;
                _smtc = null;
            }

            DisposeThumbnail();
            _updater = null;
        }
    }
}
