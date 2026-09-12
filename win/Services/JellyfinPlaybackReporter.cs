using System;
using System.Threading;
using System.Threading.Tasks;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Reports playback to Jellyfin so the server can drive resume points, play counts, and
    /// its now-playing dashboard.
    ///
    /// Deliberately a separate listener rather than logic inside PlaybackService: playback
    /// is source-neutral and must not grow Jellyfin-shaped branches. Everything here is
    /// fire-and-forget — a failed report must never interrupt what the user is hearing.
    /// </summary>
    public class JellyfinPlaybackReporter : IDisposable
    {
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

        private readonly PlaybackService _playback;
        private readonly JellyfinApiClient _api;

        private Track? _reportedTrack;
        private DateTime _lastProgressReport = DateTime.MinValue;
        private bool _lastKnownPlaying;

        public JellyfinPlaybackReporter(PlaybackService playback, JellyfinApiClient api)
        {
            _playback = playback;
            _api = api;

            _playback.TrackChanged += OnTrackChanged;
            _playback.PlayingStateChanged += OnPlayingStateChanged;
            _playback.ProgressUpdated += OnProgressUpdated;
        }

        private bool ShouldReport(Track? track) =>
            track is { Source: TrackSource.Jellyfin } && _api.IsAuthenticated;

        private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
        {
            var newTrack = e.Track;

            // TrackChanged fires twice per track — once immediately, once after artwork
            // resolves. Only the first transition is a real track change.
            if (newTrack != null && newTrack.Equals(_reportedTrack)) return;

            var previous = _reportedTrack;
            _reportedTrack = newTrack;
            _lastProgressReport = DateTime.MinValue;

            if (ShouldReport(previous))
                FireAndForget(_api.ReportPlaybackStoppedAsync(previous!.Id, _playback.Position));

            if (ShouldReport(newTrack))
                FireAndForget(_api.ReportPlaybackStartAsync(newTrack!.Id));
        }

        private void OnPlayingStateChanged(object? sender, bool isPlaying)
        {
            _lastKnownPlaying = isPlaying;

            // Pause and resume are worth reporting immediately: they change what the
            // server dashboard shows right now.
            if (ShouldReport(_reportedTrack))
            {
                FireAndForget(_api.ReportPlaybackProgressAsync(
                    _reportedTrack!.Id, _playback.Position, !isPlaying));
            }
        }

        /// <summary>
        /// Throttled hard. The source event fires four times a second; anything close to
        /// that rate would be pure network overhead against the idle-CPU budget.
        /// </summary>
        private void OnProgressUpdated(object? sender, AudioProgressEventArgs e)
        {
            if (!ShouldReport(_reportedTrack)) return;

            var now = DateTime.UtcNow;
            if (now - _lastProgressReport < ProgressInterval) return;
            _lastProgressReport = now;

            FireAndForget(_api.ReportPlaybackProgressAsync(
                _reportedTrack!.Id, e.CurrentTime, !_lastKnownPlaying));
        }

        private static void FireAndForget(Task task)
        {
            _ = task.ContinueWith(
                t => System.Diagnostics.Debug.WriteLine($"[Jellyfin] Report failed: {t.Exception?.Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        public void Dispose()
        {
            _playback.TrackChanged -= OnTrackChanged;
            _playback.PlayingStateChanged -= OnPlayingStateChanged;
            _playback.ProgressUpdated -= OnProgressUpdated;

            // Close out the session so the server does not show us playing forever.
            if (ShouldReport(_reportedTrack))
            {
                try
                {
                    _api.ReportPlaybackStoppedAsync(_reportedTrack!.Id, _playback.Position)
                        .Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Shutdown path — nothing useful to do with a failure here.
                }
            }
        }
    }
}
