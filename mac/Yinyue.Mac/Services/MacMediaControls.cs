using AppKit;
using Foundation;
using MediaPlayer;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Media keys and the Now Playing panel — product requirement 3, and the counterpart to
    /// SmtcService on Windows. Two halves, mirroring SMTC exactly: the command centre
    /// receives key presses, the info centre publishes state.
    ///
    /// This belongs to a long-lived service and never to the overlay, because the overlay is
    /// hidden almost all the time and the media keys must work regardless. It drives
    /// <see cref="PlaybackService"/> directly.
    /// </summary>
    public sealed class MacMediaControls : IDisposable
    {
        private readonly PlaybackService _playback;

        public MacMediaControls(PlaybackService playback)
        {
            _playback = playback;

            WireCommands();

            _playback.TrackChanged += (_, _) => Publish();
            _playback.PlayingStateChanged += (_, _) => Publish();

            Publish();
        }

        private void WireCommands()
        {
            var centre = MPRemoteCommandCenter.Shared;

            centre.PlayCommand.AddTarget(_ => Run(() => _playback.PlayAsync()));
            centre.PauseCommand.AddTarget(_ => Run(() => _playback.PauseAsync()));
            centre.TogglePlayPauseCommand.AddTarget(_ => Run(() => _playback.TogglePlayPauseAsync()));
            centre.NextTrackCommand.AddTarget(_ => Run(() => _playback.NextAsync()));
            centre.PreviousTrackCommand.AddTarget(_ => Run(() => _playback.PreviousAsync(true)));

            centre.ChangePlaybackPositionCommand.AddTarget(e =>
            {
                if (e is not MPChangePlaybackPositionCommandEvent position)
                    return MPRemoteCommandHandlerStatus.CommandFailed;

                _playback.Seek(TimeSpan.FromSeconds(position.PositionTime));
                Publish();
                return MPRemoteCommandHandlerStatus.Success;
            });

            // An enabled command we do not handle leaves a dead button in Control Center, so
            // everything Yinyue does not implement is switched off rather than left to fail.
            centre.SeekForwardCommand.Enabled = false;
            centre.SeekBackwardCommand.Enabled = false;
            centre.SkipForwardCommand.Enabled = false;
            centre.SkipBackwardCommand.Enabled = false;
            centre.RatingCommand.Enabled = false;
            centre.LikeCommand.Enabled = false;
            centre.DislikeCommand.Enabled = false;
            centre.BookmarkCommand.Enabled = false;
            centre.ChangeRepeatModeCommand.Enabled = false;
            centre.ChangeShuffleModeCommand.Enabled = false;
            centre.ChangePlaybackRateCommand.Enabled = false;
        }

        /// <summary>
        /// Runs a transport action and reports honestly.
        ///
        /// The status matters more than it looks: returning failure makes macOS consider
        /// routing the key press to another application, so a lie here hands the user's media
        /// keys to whatever is playing next. Fire-and-forget, because nothing on the main
        /// thread may block on a Core task.
        /// </summary>
        private MPRemoteCommandHandlerStatus Run(Func<Task> action)
        {
            try
            {
                _ = action();
                return MPRemoteCommandHandlerStatus.Success;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Media] {ex.Message}");
                return MPRemoteCommandHandlerStatus.CommandFailed;
            }
        }

        /// <summary>
        /// Republished on state and track changes, not on the 250 ms progress tick.
        /// macOS extrapolates the playhead from the elapsed time and the rate, so pushing it
        /// four times a second would be pure overhead against the idle-CPU budget and would
        /// gain nothing. Only a seek needs an out-of-band update, since extrapolation cannot
        /// predict one — which is why the seek handler calls this and the progress tick does
        /// not.
        /// </summary>
        private void Publish()
        {
            var track = _playback.CurrentTrack;

            // MPNowPlayingInfo is a plain object of fields in this binding, not the
            // dictionary the Objective-C API takes; the projection builds the dictionary.
            var info = new MPNowPlayingInfo
            {
                Title = track?.Title,
                Artist = track?.DisplayArtist,
                AlbumTitle = string.IsNullOrWhiteSpace(track?.Album) ? null : track!.Album,
                ElapsedPlaybackTime = _playback.Position.TotalSeconds,

                // The rate is what tells macOS whether to run the scrubber: 1 while playing,
                // 0 while paused. Wrong here and the panel shows a stalled playhead.
                PlaybackRate = _playback.IsPlaying ? 1.0 : 0.0,
            };

            if (track is { Duration.TotalSeconds: > 0 })
                info.PlaybackDuration = track.Duration.TotalSeconds;

            var centre = MPNowPlayingInfoCenter.DefaultCenter;
            centre.NowPlaying = info;

            // Set explicitly rather than inferred from the rate: media keys route to the app
            // holding the active now-playing session, and the session follows this property.
            centre.PlaybackState = track is null
                ? MPNowPlayingPlaybackState.Stopped
                : _playback.IsPlaying
                    ? MPNowPlayingPlaybackState.Playing
                    : MPNowPlayingPlaybackState.Paused;
        }

        public void Dispose()
        {
            var centre = MPNowPlayingInfoCenter.DefaultCenter;
            centre.NowPlaying = new MPNowPlayingInfo();
            centre.PlaybackState = MPNowPlayingPlaybackState.Stopped;
        }
    }
}
