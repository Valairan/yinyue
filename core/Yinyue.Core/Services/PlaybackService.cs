using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yinyue.Models;

namespace Yinyue.Services
{
    public enum LoopMode
    {
        Off,
        Queue,
        Track
    }

    public class TrackChangedEventArgs : EventArgs
    {
        public Track? Track { get; init; }
        public string? ArtworkPath { get; init; }

        /// <summary>
        /// True when the queue advanced on its own at the end of a track, rather than
        /// because the user asked. Lets listeners stay quiet about routine progress while
        /// still reporting a deliberate skip.
        /// </summary>
        public bool Automatic { get; init; }
    }

    /// <summary>
    /// Owns playback: the queue, the current track, shuffle and loop state, and the audio
    /// engine. Deliberately outlives MainWindow — the overlay spends most of its life
    /// hidden, and media keys must keep working while it is.
    ///
    /// All public members are safe to call from any thread. Events may fire on a
    /// threadpool thread; UI subscribers must marshal to the dispatcher themselves.
    /// </summary>
    public class PlaybackService : IDisposable
    {
        private readonly IAudioPlayer _audio;
        private readonly MusicLibrary _library;
        private readonly object _gate = new();
        private readonly Random _rng = new();

        private readonly List<Track> _queue = new();
        private List<int> _order = new();   // indices into _queue
        private int _orderPosition = -1;

        private CancellationTokenSource? _loadCts;
        private CancellationTokenSource? _prebufferCts;

        // Guards against a broken queue racing through every entry. Reset by any successful
        // start, so an isolated failure never counts toward the limit.
        private int _consecutiveFailures;
        private const int MaxConsecutiveFailures = 3;
        private Track? _retryingTrack;

        // Resume position from a restored queue, and the track it belongs to. Without the
        // track, playing anything else first would seek that track to the saved offset.
        private Track? _resumeFor;
        private TimeSpan _resumeAt;

        public event EventHandler<TrackChangedEventArgs>? TrackChanged;
        public event EventHandler<bool>? PlayingStateChanged;
        public event EventHandler<AudioProgressEventArgs>? ProgressUpdated;
        public event EventHandler<string>? PlaybackFailed;

        /// <summary>Raised when the queue contents or the current position within it change.</summary>
        public event EventHandler? QueueChanged;

        public Track? CurrentTrack { get; private set; }
        public string? CurrentArtworkPath { get; private set; }
        public bool IsPlaying => _audio.IsPlaying;
        public TimeSpan Position => _audio.Position;
        public TimeSpan Duration => _audio.Duration;

        public event EventHandler<double>? VolumeChanged;

        /// <summary>
        /// Shuffle or loop changed. Both are reachable by global hotkey, so the overlay
        /// cannot assume it was the one that changed them — without this, the buttons show
        /// stale state and a three-way loop cycle gives no clue which mode you landed on.
        /// </summary>
        public event EventHandler? ModesChanged;

        /// <summary>Output level, 0.0 to 1.0. Setting anything audible clears mute.</summary>
        public double Volume
        {
            get => _audio.Volume;
            set
            {
                // Rounded because AdjustVolume accumulates: repeated 0.05 steps drift to
                // values like 0.6499999999999997, which then land in config.json.
                double clamped = Math.Round(Math.Clamp(value, 0.0, 1.0), 3);

                // Any audible level means we are no longer muted, however it was reached.
                if (clamped > 0) IsMuted = false;

                if (Math.Abs(clamped - _audio.Volume) < 0.0001) return;

                _audio.Volume = clamped;
                VolumeChanged?.Invoke(this, clamped);
            }
        }

        public bool IsMuted { get; private set; }

        private double _volumeBeforeMute = 1.0;

        /// <summary>
        /// The level to persist and to restore on unmute. Saving the raw Volume while muted
        /// would write 0, and the app would come back silent with nothing explaining why.
        /// </summary>
        public double EffectiveVolume => IsMuted ? _volumeBeforeMute : Volume;

        /// <summary>Silences playback, remembering the level to come back to.</summary>
        public bool ToggleMute()
        {
            if (IsMuted)
            {
                IsMuted = false;
                Volume = _volumeBeforeMute;
            }
            else
            {
                // Muting from an already-silent slider would otherwise restore to silence.
                _volumeBeforeMute = Volume > 0 ? Volume : 1.0;
                IsMuted = true;
                Volume = 0;
            }

            // Volume may not have moved — muting at 0 is a no-op for the audio engine — but
            // the mute state did, and listeners need to hear about it either way.
            VolumeChanged?.Invoke(this, Volume);
            return IsMuted;
        }

        /// <summary>Nudges the volume and returns the level actually applied.</summary>
        public double AdjustVolume(double delta)
        {
            // Turning it up while muted should give back the level you had, not creep up
            // from silence one step at a time.
            if (IsMuted && delta > 0)
            {
                ToggleMute();
                return Volume;
            }

            Volume += delta;
            return Volume;
        }

        public bool Shuffle { get; private set; }
        public LoopMode Loop { get; private set; } = LoopMode.Off;

        /// <summary>
        /// The queue in the order it will actually play — already shuffled when shuffle is
        /// on. This is what a queue view should show; the raw insertion order would be a
        /// lie about what comes next.
        /// </summary>
        public IReadOnlyList<Track> PlayOrder
        {
            get
            {
                lock (_gate)
                {
                    return _order
                        .Where(i => i >= 0 && i < _queue.Count)
                        .Select(i => _queue[i])
                        .ToList();
                }
            }
        }

        /// <summary>
        /// Open the next track's stream while the current one plays, so switching does not
        /// stall on a fresh connection. Only affects remote sources.
        /// </summary>
        public bool PrebufferNext { get; set; } = true;

        /// <summary>
        /// The track that would play next, without moving to it. Respects queue looping,
        /// and returns null at the end when looping is off — there is nothing to prepare.
        /// </summary>
        public Track? PeekNext()
        {
            lock (_gate) return NextUnderGate();
        }

        /// <summary>Body of <see cref="PeekNext"/>, for callers already holding the gate.</summary>
        private Track? NextUnderGate()
        {
            if (_order.Count == 0 || _orderPosition < 0) return null;

            int next = _orderPosition + 1;
            if (next >= _order.Count)
            {
                if (Loop != LoopMode.Queue) return null;
                next = 0;
            }

            int queueIndex = _order[next];
            return queueIndex >= 0 && queueIndex < _queue.Count ? _queue[queueIndex] : null;
        }

        /// <summary>Index into <see cref="PlayOrder"/> of the current track, or -1.</summary>
        public int CurrentOrderPosition
        {
            get { lock (_gate) return _orderPosition; }
        }

        public PlaybackService(IAudioPlayer audio, MusicLibrary library)
        {
            _audio = audio;
            _library = library;

            _audio.ProgressUpdated += (s, e) => ProgressUpdated?.Invoke(this, e);
            _audio.PlaybackEnded += OnPlaybackEnded;
            _audio.MediaFailed += OnMediaFailed;
        }

        #region Queue management

        /// <summary>Replaces the queue with a single track and plays it.</summary>
        public Task PlayNowAsync(Track track) => PlayQueueAsync(new[] { track }, 0);

        /// <summary>Replaces the queue and starts at <paramref name="startIndex"/>.</summary>
        public async Task PlayQueueAsync(IEnumerable<Track> tracks, int startIndex)
        {
            lock (_gate)
            {
                _queue.Clear();
                _queue.AddRange(tracks);
                RebuildOrder(startIndex);
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);
            await PlayCurrentAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Appends to the end of the play order without disturbing what is playing.
        /// Returns the position it landed at.
        ///
        /// If the queue was empty the track also becomes current — armed but silent, the
        /// same state a restored queue starts in. Adding to an empty queue and ending up
        /// with nothing selected would be a dead end.
        /// </summary>
        public int Enqueue(Track track)
        {
            bool becameCurrent = false;
            int position;

            lock (_gate)
            {
                _queue.Add(track);
                _order.Add(_queue.Count - 1);
                position = _order.Count - 1;

                if (_orderPosition < 0)
                {
                    _orderPosition = position;
                    becameCurrent = true;
                }
            }

            if (becameCurrent)
            {
                CurrentTrack = track;
                TrackChanged?.Invoke(this, new TrackChangedEventArgs { Track = track });
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);

            // What comes next may have changed.
            StartPrebuffer();
            return position;
        }

        /// <summary>
        /// Reinstates a saved queue without starting playback. The overlay shows the track
        /// as current and the transport is armed, but nothing makes noise until the user
        /// asks — an app that starts playing on its own at login is hostile.
        ///
        /// <paramref name="tracks"/> is already in play order, so it is adopted verbatim;
        /// re-deriving it would discard the shuffle the user was actually listening to.
        /// </summary>
        public void RestoreQueue(IReadOnlyList<Track> tracks, int position,
            TimeSpan resumeAt, bool shuffle, LoopMode loop)
        {
            if (tracks.Count == 0) return;

            lock (_gate)
            {
                _queue.Clear();
                _queue.AddRange(tracks);
                _order = Enumerable.Range(0, _queue.Count).ToList();
                _orderPosition = Math.Clamp(position, 0, _queue.Count - 1);
            }

            Shuffle = shuffle;
            Loop = loop;
            ModesChanged?.Invoke(this, EventArgs.Empty);

            CurrentTrack = CurrentFromOrder();

            _resumeFor = CurrentTrack;
            _resumeAt = resumeAt;

            TrackChanged?.Invoke(this, new TrackChangedEventArgs { Track = CurrentTrack });
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Snapshot for persistence. Taken under the lock so it cannot catch a half-applied
        /// queue edit.
        /// </summary>
        public QueueState Snapshot()
        {
            lock (_gate)
            {
                return new QueueState
                {
                    Tracks = _order
                        .Where(i => i >= 0 && i < _queue.Count)
                        .Select(i => _queue[i])
                        .ToList(),
                    Position = _orderPosition,
                    TrackPositionSeconds = _audio.Position.TotalSeconds,
                    Shuffle = Shuffle,
                    Loop = Loop
                };
            }
        }

        /// <summary>
        /// Inserts directly after the current track rather than at the end, so it plays
        /// next. Returns the position it landed at.
        ///
        /// Inserting *after* the current position leaves that index untouched, so what is
        /// playing is unaffected — inserting before it would silently shift the pointer.
        /// </summary>
        public int InsertNext(Track track)
        {
            bool becameCurrent = false;
            int position;

            lock (_gate)
            {
                _queue.Add(track);
                int queueIndex = _queue.Count - 1;

                if (_orderPosition < 0)
                {
                    // Nothing playing, so "next" is simply the start.
                    _order.Add(queueIndex);
                    position = _order.Count - 1;
                    _orderPosition = position;
                    becameCurrent = true;
                }
                else
                {
                    position = _orderPosition + 1;
                    _order.Insert(position, queueIndex);
                }
            }

            if (becameCurrent)
            {
                CurrentTrack = track;
                TrackChanged?.Invoke(this, new TrackChangedEventArgs { Track = track });
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);
            return position;
        }

        /// <summary>Plays the entry at <paramref name="orderPosition"/> in <see cref="PlayOrder"/>.</summary>
        public async Task JumpToAsync(int orderPosition)
        {
            lock (_gate)
            {
                if (orderPosition < 0 || orderPosition >= _order.Count) return;
                _orderPosition = orderPosition;
            }

            await PlayCurrentAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Drops an entry from the play order. Removing the currently playing track is
        /// refused — that would mean either stopping the music or silently skipping, and
        /// neither is what someone tidying a queue expects.
        /// </summary>
        public bool RemoveAt(int orderPosition)
        {
            lock (_gate)
            {
                if (orderPosition < 0 || orderPosition >= _order.Count) return false;
                if (orderPosition == _orderPosition) return false;

                // Only the order is edited. Removing from _queue would invalidate every
                // index held in _order, and _queue is never read except through it.
                _order.RemoveAt(orderPosition);

                if (orderPosition < _orderPosition) _orderPosition--;
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>
        /// Moves a queue entry to another position, reporting whether anything changed.
        ///
        /// Unlike <see cref="RemoveAt"/> this permits moving the playing track: relocating it
        /// changes what comes after it, and nothing about the audio in flight. The pointer
        /// follows the track it was on rather than the index it held, so playback is never
        /// reassigned to a different song by a reorder.
        /// </summary>
        public bool MoveTo(int from, int to)
        {
            bool nextChanged;

            lock (_gate)
            {
                if (from < 0 || from >= _order.Count) return false;

                to = Math.Clamp(to, 0, _order.Count - 1);
                if (from == to) return false;

                var before = NextUnderGate();

                // Only the order is edited, for the same reason RemoveAt gives: _queue holds
                // the tracks and _order is the arrangement, and reordering _queue would
                // invalidate every index in _order.
                int entry = _order[from];
                _order.RemoveAt(from);
                _order.Insert(to, entry);

                if (_orderPosition == from)
                {
                    // The playing track is the one being moved.
                    _orderPosition = to;
                }
                else if (from < _orderPosition && to >= _orderPosition)
                {
                    // Something ahead of the playhead moved to or past it, so everything
                    // between shifted back by one.
                    _orderPosition--;
                }
                else if (from > _orderPosition && to <= _orderPosition)
                {
                    _orderPosition++;
                }

                nextChanged = !Equals(before, NextUnderGate());
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);

            // Only when the answer actually changed: a reorder elsewhere in the queue must
            // not throw away a prepared stream, and moving an entry one row at a time would
            // otherwise reopen a connection on every keypress.
            if (nextChanged) StartPrebuffer();

            return true;
        }

        public async Task ClearQueueAsync()
        {
            await StopAsync().ConfigureAwait(false);

            lock (_gate)
            {
                _queue.Clear();
                _order.Clear();
                _orderPosition = -1;
            }

            CurrentTrack = null;
            CurrentArtworkPath = null;

            TrackChanged?.Invoke(this, new TrackChangedEventArgs());
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Rebuilds the play order. Must be called under _gate.
        /// When shuffling, the starting track stays first so the user hears what they picked.
        /// </summary>
        private void RebuildOrder(int startIndex)
        {
            _order = Enumerable.Range(0, _queue.Count).ToList();

            if (Shuffle && _queue.Count > 1)
            {
                // Fisher-Yates over everything except the chosen start track.
                _order.Remove(startIndex);
                for (int i = _order.Count - 1; i > 0; i--)
                {
                    int j = _rng.Next(i + 1);
                    (_order[i], _order[j]) = (_order[j], _order[i]);
                }
                _order.Insert(0, startIndex);
                _orderPosition = 0;
            }
            else
            {
                _orderPosition = _queue.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _queue.Count - 1);
            }
        }

        #endregion

        #region Transport

        private Track? CurrentFromOrder()
        {
            lock (_gate)
            {
                if (_orderPosition < 0 || _orderPosition >= _order.Count) return null;
                int queueIndex = _order[_orderPosition];
                return queueIndex >= 0 && queueIndex < _queue.Count ? _queue[queueIndex] : null;
            }
        }

        private async Task PlayCurrentAsync(bool automatic = false)
        {
            var track = CurrentFromOrder();
            if (track == null) return;

            // Position within the queue moved, so the queue view's highlight is stale.
            QueueChanged?.Invoke(this, EventArgs.Empty);

            // Cancel any in-flight load so rapid next-next-next does not race.
            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            try
            {
                string? uri = await _library.ResolvePlaybackUriAsync(track, cts.Token).ConfigureAwait(false);
                if (cts.Token.IsCancellationRequested) return;

                if (string.IsNullOrEmpty(uri))
                {
                    PlaybackFailed?.Invoke(this, $"Could not resolve \"{track.Title}\".");
                    return;
                }

                CurrentTrack = track;

                // A restored queue resumes where it was paused; otherwise fall back to the
                // server's own resume point, which is set for tracks abandoned part-way.
                bool isResume = _resumeFor != null && track.Equals(_resumeFor);
                var startAt = isResume ? _resumeAt : ResumePointFor(track);
                _audio.SeekWhenReady(startAt);
                _resumeFor = null;

                await _audio.PlayAsync(uri).ConfigureAwait(false);
                if (cts.Token.IsCancellationRequested) return;

                PlayingStateChanged?.Invoke(this, true);

                // Reaching playback clears the failure streak.
                _consecutiveFailures = 0;
                _retryingTrack = null;

                // Announce the track before artwork resolves so the UI updates immediately.
                TrackChanged?.Invoke(this, new TrackChangedEventArgs
                {
                    Track = track,
                    Automatic = automatic
                });

                string? art = await _library.ResolveArtworkPathAsync(track, cts.Token).ConfigureAwait(false);
                if (cts.Token.IsCancellationRequested) return;

                CurrentArtworkPath = art;
                TrackChanged?.Invoke(this, new TrackChangedEventArgs
                {
                    Track = track,
                    ArtworkPath = art,
                    Automatic = automatic
                });

                // Only after the current track is settled, so the prebuffer competes with
                // nothing that matters.
                StartPrebuffer();
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer load.
            }
            catch (Exception ex)
            {
                PlaybackFailed?.Invoke(this, $"Playback failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens the next track's stream in the background. Fire-and-forget by design: a
        /// failure here is an optimisation not taken, never a playback error.
        /// </summary>
        private void StartPrebuffer()
        {
            _prebufferCts?.Cancel();
            _prebufferCts?.Dispose();
            _prebufferCts = null;

            if (!PrebufferNext) return;

            var next = PeekNext();
            if (next == null) return;

            var cts = new CancellationTokenSource();
            _prebufferCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    string? uri = await _library.ResolvePlaybackUriAsync(next, cts.Token)
                        .ConfigureAwait(false);

                    if (string.IsNullOrEmpty(uri) || cts.Token.IsCancellationRequested) return;

                    await _audio.PrepareAsync(uri, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The queue moved on.
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Playback] Prebuffer failed: {ex.Message}");
                }
            }, cts.Token);
        }

        public async Task TogglePlayPauseAsync()
        {
            if (CurrentTrack == null) return;

            // A restored queue has a current track but nothing loaded into the engine yet.
            if (!_audio.HasSource)
            {
                await PlayCurrentAsync().ConfigureAwait(false);
                return;
            }

            if (_audio.IsPlaying)
            {
                await _audio.PauseAsync().ConfigureAwait(false);
                PlayingStateChanged?.Invoke(this, false);
            }
            else
            {
                await _audio.PlayAsync().ConfigureAwait(false);
                PlayingStateChanged?.Invoke(this, true);
            }
        }

        public async Task PlayAsync()
        {
            if (CurrentTrack == null) return;

            if (!_audio.HasSource)
            {
                await PlayCurrentAsync().ConfigureAwait(false);
                return;
            }

            await _audio.PlayAsync().ConfigureAwait(false);
            PlayingStateChanged?.Invoke(this, true);
        }

        public async Task PauseAsync()
        {
            await _audio.PauseAsync().ConfigureAwait(false);
            PlayingStateChanged?.Invoke(this, false);
        }

        public async Task NextAsync(bool automatic = false)
        {
            bool hasNext;
            lock (_gate)
            {
                if (_order.Count == 0) return;

                if (_orderPosition < _order.Count - 1)
                {
                    _orderPosition++;
                    hasNext = true;
                }
                else if (Loop == LoopMode.Queue)
                {
                    _orderPosition = 0;
                    hasNext = true;
                }
                else
                {
                    hasNext = false;
                }
            }

            if (hasNext) await PlayCurrentAsync(automatic).ConfigureAwait(false);
            else await StopAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Restarts the current track when more than three seconds in, otherwise steps back.
        /// This is the behaviour every media player has trained people to expect.
        /// </summary>
        /// <summary>
        /// Steps back. By default a press part-way into a track restarts it, which is what
        /// every player trains people to expect; <paramref name="alwaysChangeTrack"/> skips
        /// that rule, for holding the key to walk backwards through the queue.
        /// </summary>
        public async Task PreviousAsync(bool alwaysChangeTrack = false)
        {
            if (!alwaysChangeTrack && _audio.Position > TimeSpan.FromSeconds(3))
            {
                _audio.Seek(TimeSpan.Zero);
                return;
            }

            lock (_gate)
            {
                if (_order.Count == 0) return;

                if (_orderPosition > 0) _orderPosition--;
                else if (Loop == LoopMode.Queue) _orderPosition = _order.Count - 1;
                else
                {
                    _audio.Seek(TimeSpan.Zero);
                    return;
                }
            }

            await PlayCurrentAsync().ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            await _audio.PauseAsync().ConfigureAwait(false);
            _audio.Seek(TimeSpan.Zero);
            PlayingStateChanged?.Invoke(this, false);
        }

        public void Seek(TimeSpan position) => _audio.Seek(position);

        /// <summary>Sends the current track back to its start.</summary>
        public void RestartTrack() => _audio.Seek(TimeSpan.Zero);

        public void SeekPercent(double percent) => _audio.Seek(percent);

        #endregion

        #region Modes

        public bool ToggleShuffle()
        {
            lock (_gate)
            {
                Shuffle = !Shuffle;

                // Keep playing the current track; reorder what comes after it.
                int currentQueueIndex = _orderPosition >= 0 && _orderPosition < _order.Count
                    ? _order[_orderPosition]
                    : 0;

                if (_queue.Count > 0) RebuildOrder(currentQueueIndex);
            }

            // Shuffle rewrites what comes next, which is exactly what a queue view shows.
            QueueChanged?.Invoke(this, EventArgs.Empty);
            ModesChanged?.Invoke(this, EventArgs.Empty);
            return Shuffle;
        }

        public LoopMode CycleLoop()
        {
            Loop = Loop switch
            {
                LoopMode.Off => LoopMode.Queue,
                LoopMode.Queue => LoopMode.Track,
                _ => LoopMode.Off
            };

            ModesChanged?.Invoke(this, EventArgs.Empty);
            return Loop;
        }

        #endregion

        /// <summary>
        /// Only resume a track the server says was abandoned part-way through. Ignoring the
        /// last few seconds avoids "resuming" something that had effectively finished, and
        /// the lower bound keeps a brief skip past the start from being treated as progress.
        /// </summary>
        private static TimeSpan ResumePointFor(Track track)
        {
            var resume = track.ResumePosition;
            if (resume <= TimeSpan.FromSeconds(10)) return TimeSpan.Zero;

            if (track.Duration > TimeSpan.Zero &&
                resume >= track.Duration - TimeSpan.FromSeconds(10))
            {
                return TimeSpan.Zero;
            }

            return resume;
        }

        /// <summary>
        /// A track failed to play. Try it once more — a dropped connection or an expired
        /// transcode session usually recovers on a fresh request — then move on rather than
        /// leaving the user in silence.
        /// </summary>
        private void OnMediaFailed(object? sender, string detail)
        {
            var track = CurrentTrack;
            PlayingStateChanged?.Invoke(this, false);

            _ = Task.Run(async () =>
            {
                try
                {
                    if (track != null && !track.Equals(_retryingTrack))
                    {
                        // First failure for this track: re-resolve and retry once. The URL
                        // itself may be stale, so do not reuse the old one.
                        _retryingTrack = track;
                        _audio.DiscardPrepared();

                        PlaybackFailed?.Invoke(this, $"Retrying \"{track.Title}\"…");
                        await Task.Delay(400).ConfigureAwait(false);
                        await PlayCurrentAsync().ConfigureAwait(false);
                        return;
                    }

                    _consecutiveFailures++;
                    _retryingTrack = null;

                    if (_consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        PlaybackFailed?.Invoke(this,
                            $"Stopped after {_consecutiveFailures} tracks failed to play. {detail}");
                        _consecutiveFailures = 0;
                        await StopAsync().ConfigureAwait(false);
                        return;
                    }

                    PlaybackFailed?.Invoke(this,
                        $"Could not play \"{track?.Title ?? "track"}\" — skipping. {detail}");

                    await NextAsync(automatic: true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PlaybackFailed?.Invoke(this, $"Recovery failed: {ex.Message}");
                }
            });
        }

        private void OnPlaybackEnded(object? sender, EventArgs e)
        {
            // Fired on a WinRT thread. Do not block it.
            _ = Task.Run(async () =>
            {
                try
                {
                    if (Loop == LoopMode.Track)
                    {
                        _audio.Seek(TimeSpan.Zero);
                        await _audio.PlayAsync().ConfigureAwait(false);
                        PlayingStateChanged?.Invoke(this, true);
                    }
                    else
                    {
                        await NextAsync(automatic: true).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    PlaybackFailed?.Invoke(this, $"Advance failed: {ex.Message}");
                }
            });
        }

        public void Dispose()
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _prebufferCts?.Cancel();
            _prebufferCts?.Dispose();
            _audio.PlaybackEnded -= OnPlaybackEnded;
            _audio.MediaFailed -= OnMediaFailed;
            _audio.Dispose();
        }
    }
}
