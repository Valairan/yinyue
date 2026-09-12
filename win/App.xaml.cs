using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Forms;
using Microsoft.Win32;
using Yinyue.Models;
using Yinyue.Services;

using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Yinyue
{
    /// <summary>
    /// Composition root. Builds the service graph by hand — the app is small enough that a
    /// DI container would cost startup time for no benefit.
    /// </summary>
    public partial class App : Application
    {
        private const string AppGuid = "Yinyue-MusicDaemon-5A3E9C12-88B1-4A2D-B91F";

        private static Mutex? _mutex;

        /// <summary>
        /// Signalled by a second launch to ask the running instance to show itself.
        /// Re-running the app should summon it, not scold the user with a dialog.
        /// </summary>
        private const string SummonEventName = "Yinyue-Summon-5A3E9C12-88B1-4A2D-B91F";

        private EventWaitHandle? _summonSignal;
        private CancellationTokenSource? _summonWatch;

        private ConfigService? _config;
        private LibraryIndexerService? _indexer;
        private JellyfinApiClient? _jellyfin;
        private MusicLibrary? _library;
        private AudioPlayerService? _audio;
        private PlaybackService? _playback;
        private HotkeyManager? _hotkeys;
        private SmtcService? _smtc;
        private JellyfinPlaybackReporter? _reporter;
        private ArtworkCache? _artwork;
        private QueueStore? _queueStore;
        private System.Windows.Threading.DispatcherTimer? _queueSaveTimer;
        private System.Windows.Threading.DispatcherTimer? _volumeSaveTimer;

        /// <summary>How much one press of the volume hotkeys moves the level.</summary>
        private const double VolumeStep = 0.05;

        private NotifyIcon? _notifyIcon;
        private Icon? _trayIcon;
        private ToastWindow? _toast;
        private ToastWindow? _holdToast;
        private SleepTimerService? _sleepTimer;

        /// <summary>
        /// Suppresses toasts until startup finishes. Restoring a queue and applying the
        /// saved volume both raise change events, and neither is something the user did.
        /// </summary>
        private bool _ready;
        private MainWindow? _overlay;

        private IntPtr _overlayHwnd;
        private string _hotkeySignature = string.Empty;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, AppGuid, out bool isNewInstance);
            if (!isNewInstance)
            {
                // Hand off to the instance that is already running, then leave quietly.
                TrySummonRunningInstance();
                Shutdown();
                return;
            }

            base.OnStartup(e);

            DispatcherUnhandledException += OnUnhandledException;

            BuildServices();
            BuildOverlay();
            InitializeTrayIcon();
            ListenForSummon();

            // After the overlay exists, so its event handlers catch the restored state.
            RestoreQueue();

            // Deferred so none of this eats into the cold-start budget.
            if (_config!.Current.Library.ScanOnStartup) _ = Task.Run(ScanLibraryAsync);
            _ = _artwork!.PruneAsync();

            // Everything from here on is the user's doing, so it may announce itself.
            _ready = true;
        }

        #region Single instance

        private static void TrySummonRunningInstance()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(SummonEventName, out var signal))
                {
                    using (signal) signal.Set();
                }
            }
            catch (Exception ex)
            {
                Log($"Could not summon the running instance: {ex}");
            }
        }

        /// <summary>
        /// Waits for a second launch to signal us. A blocking wait on a background thread
        /// rather than a timer: it costs nothing while idle.
        /// </summary>
        private void ListenForSummon()
        {
            try
            {
                _summonSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SummonEventName);
                _summonWatch = new CancellationTokenSource();

                var token = _summonWatch.Token;
                var signal = _summonSignal;

                _ = Task.Run(() =>
                {
                    var handles = new WaitHandle[] { signal, token.WaitHandle };

                    while (!token.IsCancellationRequested)
                    {
                        if (WaitHandle.WaitAny(handles) != 0) return;

                        Dispatcher.BeginInvoke(() => _overlay?.ShowOverlay());
                    }
                }, token);
            }
            catch (Exception ex)
            {
                Log($"Summon listener failed to start: {ex}");
            }
        }

        #endregion

        #region Startup

        private void BuildServices()
        {
            _config = new ConfigService();

            _indexer = new LibraryIndexerService();
            _artwork = new ArtworkCache();

            _jellyfin = new JellyfinApiClient
            {
                DeviceId = _config.Current.DeviceId,
                MaxStreamingBitrate = _config.Current.Jellyfin.MaxStreamingBitrate
            };
            _jellyfin.Unauthorized += OnJellyfinUnauthorized;
            RestoreJellyfinSession();

            _library = new MusicLibrary(_config);

            // Registration order is result order: local tracks play instantly and cannot
            // fail mid-song, so they lead.
            _library.Register(new LocalMusicSource(_indexer, _artwork));
            _library.Register(new JellyfinMusicSource(_jellyfin, _artwork));

            _audio = new AudioPlayerService();
            _playback = new PlaybackService(_audio, _library)
            {
                PrebufferNext = _config.Current.Jellyfin.PrebufferNext
            };
            _playback.TrackChanged += OnTrackChangedForSmtc;
            _playback.TrackChanged += OnTrackChangedForToast;
            _playback.PlayingStateChanged += OnPlayingStateChangedForSmtc;

            _playback.Volume = _config.Current.Playback.Volume;
            _playback.VolumeChanged += OnVolumeChanged;
            _playback.ModesChanged += OnModesChanged;

            _reporter = new JellyfinPlaybackReporter(_playback, _jellyfin);

            _sleepTimer = new SleepTimerService();
            ApplySleepTimerSettings();
            _sleepTimer.Elapsed += () =>
            {
                _ = _playback!.PauseAsync();
                ShowToast("⏻", "Sleep timer finished — paused");
            };

            _queueStore = new QueueStore();
            SetUpQueuePersistence();
            SetUpVolumePersistence();
        }

        /// <summary>
        /// Volume feedback and persistence live here rather than in the overlay, because
        /// the volume hotkeys are global — they fire while the overlay is hidden, which is
        /// most of the time. The tray tooltip is the one surface always available then.
        /// </summary>
        private void OnVolumeChanged(object? sender, double volume)
        {
            Dispatcher.BeginInvoke(() =>
            {
                bool muted = _playback!.IsMuted || volume <= 0.0001;

                if (_notifyIcon != null)
                {
                    _notifyIcon.Text = muted
                        ? "Yinyue — muted"
                        : $"Yinyue — volume {volume * 100:F0}%";
                }

                _volumeSaveTimer?.Stop();
                _volumeSaveTimer?.Start();

                ShowToast(muted ? "🔇" : "🔊",
                    muted ? "Muted" : $"Volume {volume * 100:F0}%",
                    volume);
            });
        }

        /// <summary>
        /// Announces the track while the overlay is hidden — otherwise skipping by hotkey
        /// gives no clue what you landed on.
        ///
        /// Automatic advances are skipped deliberately: a toast every time a song ends on
        /// its own would fire all day for something the user did not ask for.
        /// </summary>
        private void OnTrackChangedForToast(object? sender, TrackChangedEventArgs e)
        {
            if (e.Track == null || e.Automatic) return;

            // TrackChanged fires twice per track, once before artwork resolves.
            if (e.Track.Equals(_lastToastedTrack)) return;
            _lastToastedTrack = e.Track;

            Dispatcher.BeginInvoke(() =>
                ShowToast("🎵", $"{e.Track.Title} — {e.Track.DisplayArtist}"));
        }

        private Track? _lastToastedTrack;

        private void OnModesChanged(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var playback = _playback!;

                string glyph = playback.Loop switch
                {
                    LoopMode.Track => "🔂",
                    LoopMode.Queue => "🔁",
                    _ => playback.Shuffle ? "🔀" : "🔁"
                };

                string loop = playback.Loop switch
                {
                    LoopMode.Queue => "Loop queue",
                    LoopMode.Track => "Loop track",
                    _ => "No loop"
                };

                ShowToast(glyph, playback.Shuffle ? $"{loop} · shuffle on" : loop);
            });
        }

        /// <summary>
        /// Shows the toast only while the overlay is hidden. With it open the status line
        /// says the same thing, and two readouts of one change is noise.
        /// </summary>
        private void ShowToast(string glyph, string message, double? level = null)
        {
            if (!_ready || _toast == null) return;
            if (_overlay?.IsOverlayShown == true) return;

            _toast.Show(glyph, message, level);
        }

        private void SetUpVolumePersistence()
        {
            // Debounced: holding the hotkey walks the level in 5% steps, and each one
            // would otherwise rewrite config.json.
            _volumeSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            _volumeSaveTimer.Tick += (_, _) =>
            {
                _volumeSaveTimer!.Stop();
                _config!.Current.Playback.Volume = _playback!.EffectiveVolume;
                _config.Save();
            };
        }

        /// <summary>
        /// Persists the queue a couple of seconds after it settles. Saving on every
        /// QueueChanged would write once per track skip; debouncing collapses a burst of
        /// skipping into one write.
        /// </summary>
        private void SetUpQueuePersistence()
        {
            _queueSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };

            _queueSaveTimer.Tick += (_, _) =>
            {
                _queueSaveTimer!.Stop();
                _ = _queueStore!.SaveAsync(_playback!.Snapshot());
            };

            _playback!.QueueChanged += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _queueSaveTimer.Stop();
                _queueSaveTimer.Start();
            });
        }

        /// <summary>
        /// Reinstates the last queue, armed but silent. Anything that fails to resolve
        /// later — a deleted file, a signed-out server — surfaces as a normal playback
        /// error when the user actually presses play.
        /// </summary>
        private void RestoreQueue()
        {
            var state = _queueStore!.Load();
            if (state == null) return;

            _playback!.RestoreQueue(
                state.Tracks,
                state.Position,
                TimeSpan.FromSeconds(state.TrackPositionSeconds),
                state.Shuffle,
                state.Loop);
        }

        /// <summary>
        /// Tokens survive until revoked server-side, so there is no refresh flow — a 401
        /// means the token is dead and the user has to sign in again. Clear it rather than
        /// retrying forever against a server that will keep saying no.
        /// </summary>
        private void OnJellyfinUnauthorized(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _config!.ClearCredentials();
                _jellyfin!.SignOut();

                Notify("Jellyfin sign-in expired",
                    "Yinyue was signed out. Open Settings to sign in again.");
            });
        }

        private void RestoreJellyfinSession()
        {
            string? token = _config!.GetAccessToken();
            var jf = _config.Current.Jellyfin;

            if (!string.IsNullOrEmpty(token) && !string.IsNullOrWhiteSpace(jf.ServerUrl))
                _jellyfin!.RestoreSession(jf.ServerUrl, token, jf.UserId);
        }

        private void BuildOverlay()
        {
            _overlay = new MainWindow(_config!, _library!, _playback!,
                () => new SettingsWindow(_config!, _jellyfin!, _indexer!));

            // The overlay's own tap-or-hold gestures report through the toast too, so the
            // feedback is identical whether the panel happens to be on screen.
            _overlay.HoldProgressed += (label, fraction) =>
                _holdToast?.ShowHoldProgress($"Keep holding to {label}", fraction);

            _overlay.HoldFinished += () => _holdToast?.EndHoldProgress();

            // Force the HWND into existence without showing the window. Global hotkeys and
            // SMTC both bind to this handle, and it must stay valid for the whole process
            // lifetime — which is why the overlay only ever hides, never closes.
            var helper = new WindowInteropHelper(_overlay);
            _overlayHwnd = helper.EnsureHandle();

            _toast = new ToastWindow(_config!, ToastWindow.ToastRole.Message);
            _holdToast = new ToastWindow(_config!, ToastWindow.ToastRole.Hold);

            // Force the handle now: the first toast should not pay for window creation.
            new WindowInteropHelper(_toast).EnsureHandle();
            new WindowInteropHelper(_holdToast).EnsureHandle();

            BuildHotkeys();
            ApplyHotkeys();
            InitializeSmtc(_overlayHwnd);

            // Rebinding from the settings window arrives through config, like every other
            // setting change.
            _config!.ConfigChanged += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                ApplyHotkeys();

                // Streaming quality and prebuffering both take effect on the next track
                // rather than needing a restart.
                _jellyfin!.MaxStreamingBitrate = _config.Current.Jellyfin.MaxStreamingBitrate;
                _playback!.PrebufferNext = _config.Current.Jellyfin.PrebufferNext;

                ApplySleepTimerSettings();
            });
        }

        /// <summary>
        /// Pushes the configured steps and the on/off switch into the timer.
        ///
        /// Order matters: the steps go in first, so that turning the feature off afterwards
        /// cancels whatever was running rather than the step change stranding it.
        /// </summary>
        /// <summary>
        /// Renders a step for a toast. Long steps are the point of making these configurable,
        /// and "Sleeping in 480 minutes" is arithmetic the reader should not have to do.
        /// </summary>
        private static string DescribeMinutes(int minutes)
        {
            if (minutes < 60) return $"{minutes} minutes";

            int hours = minutes / 60;
            int rest = minutes % 60;
            string hourPart = $"{hours} hour{(hours == 1 ? "" : "s")}";

            return rest == 0 ? hourPart : $"{hourPart} {rest} min";
        }

        private void ApplySleepTimerSettings()
        {
            if (_sleepTimer == null) return;

            var settings = _config!.Current.SleepTimer;

            _sleepTimer.SetSteps(settings.Steps);
            _sleepTimer.Enabled = settings.Enabled;
        }

        private void BuildHotkeys()
        {
            _hotkeys = new HotkeyManager();

            // One event, dispatched by action name — 12 separate events was a lot of
            // ceremony for a switch statement.
            _hotkeys.Triggered += OnHotkey;

            // A pending hold needs to say so, or holding a key looks like nothing happening.
            // The toast carries it rather than the overlay, which is often hidden.
            _hotkeys.HoldProgress += (action, fraction) => Dispatcher.BeginInvoke(() =>
                _holdToast?.ShowHoldProgress(HotkeyActions.Describe(action), fraction));

            _hotkeys.HoldCancelled += _ => Dispatcher.BeginInvoke(() => _holdToast?.EndHoldProgress());
        }

        private void OnHotkey(string action)
        {
            switch (action)
            {
                case HotkeyActions.ToggleOverlay:
                    _overlay!.ToggleOverlay();
                    break;

                case HotkeyActions.QuickSearch:
                    _overlay!.ShowOverlay();
                    _overlay.FocusSearch();
                    break;

                case HotkeyActions.OfflineMode:
                    _config!.Current.OfflineMode = !_config.Current.OfflineMode;
                    _config.Save();
                    break;

                case HotkeyActions.CycleLoop:
                    _playback!.CycleLoop();
                    break;

                case HotkeyActions.ToggleShuffle:
                    _playback!.ToggleShuffle();
                    break;

                case HotkeyActions.VolumeUp:
                    _playback!.AdjustVolume(VolumeStep);
                    break;

                case HotkeyActions.VolumeDown:
                    _playback!.AdjustVolume(-VolumeStep);
                    break;

                case HotkeyActions.Mute:
                    _playback!.ToggleMute();
                    break;

                case HotkeyActions.SleepTimer:
                {
                    // Silence would read as a broken shortcut. Say why nothing happened.
                    if (!_config!.Current.SleepTimer.Enabled)
                    {
                        ShowToast("⏻", "Sleep timer is turned off in settings");
                        break;
                    }

                    int minutes = _sleepTimer!.Cycle();
                    ShowToast("⏻", minutes == 0
                        ? "Sleep timer off"
                        : $"Sleeping in {DescribeMinutes(minutes)}");
                    break;
                }

                case HotkeyActions.ShuffleFavorites:
                    // Bring the overlay up: this starts a whole queue, and doing that with
                    // no visible confirmation would feel like the app ignored the key.
                    _overlay!.ShowOverlay();
                    _ = _overlay.ShuffleFavoritesAsync();
                    break;

                case HotkeyActions.AddToQueue:
                    _overlay!.ShowOverlay();

                    // Tap queues at the end, hold plays it next. Same bespoke escalation as
                    // RemoveFromQueue, which the generic hold toggle cannot express.
                    _overlay.BeginAddToQueueHold(
                        _hotkeys!.BindingFor(HotkeyActions.AddToQueue) ?? default);
                    break;

                case HotkeyActions.RemoveFromQueue:
                    _overlay!.ShowOverlay();

                    // This one keeps its own tap-versus-hold handling: a tap removes one
                    // entry and a hold clears the queue, which the generic hold cannot express.
                    _overlay.BeginRemoveOrClearHold(
                        _hotkeys!.BindingFor(HotkeyActions.RemoveFromQueue) ?? default);
                    break;

                case HotkeyActions.OpenQueue:
                    _overlay!.ShowOverlay();
                    _overlay.ShowQueue();
                    break;

                case HotkeyActions.GrabQueueEntry:
                    _overlay!.ShowOverlay();
                    _overlay.ToggleQueueGrab();
                    break;

                case HotkeyActions.OpenSettings:
                    _overlay!.ShowSettings();
                    break;

                case HotkeyActions.RestartOrPrevious:
                    // Like play/pause, this is used from another window, so it does not
                    // summon the overlay; the toast reports where a step back landed.
                    _overlay!.BeginRestartOrPreviousHold(
                        _hotkeys!.BindingFor(HotkeyActions.RestartOrPrevious) ?? default);
                    break;

                case HotkeyActions.PlayPause:
                    // No ShowOverlay: this is used while working elsewhere, and the toast
                    // reports what a skip landed on.
                    _overlay!.BeginPlayPauseHold(
                        _hotkeys!.BindingFor(HotkeyActions.PlayPause) ?? default);
                    break;
            }
        }

        /// <summary>
        /// (Re)binds global hotkeys from config. Skips the work when nothing changed, so the
        /// frequent saves from the offline toggle do not churn registrations — and do not
        /// open a window in which no hotkey is live.
        /// </summary>
        private void ApplyHotkeys()
        {
            if (_hotkeys == null || _overlayHwnd == IntPtr.Zero) return;

            var keys = _config!.Current.Hotkeys;

            // Covers keys, hold flags and the delay, so any of them changing rebinds.
            string signature = keys.HoldDelaySeconds.ToString("F3") + "|" + string.Join("|",
                HotkeyActions.All.Select(a =>
                {
                    var entry = keys.For(a);
                    return $"{a}:{entry.Keys}:{entry.Hold}";
                }));

            if (signature == _hotkeySignature) return;
            _hotkeySignature = signature;

            var failed = _hotkeys.Apply(_overlayHwnd, keys);

            // A silently dead hotkey is the worst outcome: the app looks broken with no
            // explanation. Another program already owning the combination is the usual cause.
            if (failed.Count > 0)
            {
                Notify("Shortcut conflict",
                    "Yinyue could not register these shortcuts:" +
                    Environment.NewLine + Environment.NewLine + "  " +
                    string.Join(Environment.NewLine + "  ", failed) +
                    Environment.NewLine + Environment.NewLine +
                    "Change them under Settings if another app owns them.");
            }
        }

        /// <summary>
        /// Shows a tray balloon, holding the message until the tray icon exists — hotkey
        /// registration runs before the icon is created during startup.
        /// </summary>
        private void Notify(string title, string message)
        {
            if (_notifyIcon == null)
            {
                _pendingBalloonTitle = title;
                _pendingBalloonText = message;
                return;
            }

            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.ShowBalloonTip(8000);
        }

        private string? _pendingBalloonTitle;
        private string? _pendingBalloonText;

        private void InitializeSmtc(IntPtr hwnd)
        {
            _smtc = new SmtcService();
            _smtc.Initialize(hwnd);

            _smtc.PlayRequested += (_, _) => _ = _playback!.PlayAsync();
            _smtc.PauseRequested += (_, _) => _ = _playback!.PauseAsync();
            _smtc.NextRequested += (_, _) => _ = _playback!.NextAsync();
            _smtc.PreviousRequested += (_, _) => _ = _playback!.PreviousAsync();
            _smtc.SeekRequested += (_, position) => _playback!.Seek(position);

            // Feeds the scrubber in the Windows 11 media flyout. SmtcService throttles this
            // to about once a second; the source event fires four times that often.
            _playback!.ProgressUpdated += (_, e) => _smtc!.UpdateTimeline(e.CurrentTime, e.TotalTime);
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new NotifyIcon
            {
                Text = "Yinyue",
                Visible = true
            };

            ApplyTrayIcon();

            // The mark is monochrome, so it has to invert when the taskbar theme changes —
            // otherwise it turns invisible the moment someone switches to light mode.
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            var menu = new ContextMenuStrip();
            string summon = _config!.Current.Hotkeys.For(HotkeyActions.ToggleOverlay).Keys;
            menu.Items.Add($"Show Yinyue ({summon})", null, (_, _) => _overlay!.ShowOverlay());
            menu.Items.Add("Settings…", null, (_, _) => _overlay!.ShowSettings());
            menu.Items.Add("-");
            menu.Items.Add("Exit", null, (_, _) => ExitApplication());

            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += (_, _) => _overlay!.ToggleOverlay();

            if (_pendingBalloonTitle != null)
            {
                _notifyIcon.BalloonTipTitle = _pendingBalloonTitle;
                _notifyIcon.BalloonTipText = _pendingBalloonText ?? string.Empty;
                _notifyIcon.ShowBalloonTip(8000);

                _pendingBalloonTitle = null;
                _pendingBalloonText = null;
            }
        }

        /// <summary>
        /// Picks the tray mark that will actually be visible: the dark glyph on a light
        /// taskbar, the light glyph on a dark one. The artwork is monochrome with a
        /// transparent background, so getting this backwards makes the icon disappear.
        /// </summary>
        private void ApplyTrayIcon()
        {
            if (_notifyIcon == null) return;

            string asset = IsTaskbarLight() ? "tray-dark.ico" : "tray-light.ico";

            try
            {
                var resource = GetResourceStream(new Uri($"pack://application:,,,/Yinyue;component/Assets/{asset}"));
                if (resource == null) return;

                using var stream = resource.Stream;

                // Ask for the exact frame Windows wants rather than letting it rescale a
                // larger one — the mark is dense, and a downscaled 256px frame turns to mush.
                var icon = new Icon(stream, SystemInformation.SmallIconSize);

                var previous = _trayIcon;
                _trayIcon = icon;
                _notifyIcon.Icon = icon;

                // Only after the NotifyIcon has let go of it.
                previous?.Dispose();
            }
            catch (Exception ex)
            {
                Log($"Tray icon load failed: {ex}");
            }
        }

        private static bool IsTaskbarLight()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

                return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
            }
            catch
            {
                // Absent on older builds; dark is the Windows 11 default.
                return false;
            }
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General)
                Dispatcher.BeginInvoke(ApplyTrayIcon);
        }

        private async Task ScanLibraryAsync()
        {
            try
            {
                foreach (string folder in _config!.Current.Library.Folders)
                {
                    if (Directory.Exists(folder))
                        await _indexer!.IndexDirectoryAsync(folder);
                }

                // Scanning only adds; pruning is what removes deleted files and tracks
                // left behind by a folder the user has since removed.
                await _indexer!.PruneAsync(_config.Current.Library.Folders);

                _config.Current.Library.LastScanUtc = DateTime.UtcNow;
                await Dispatcher.InvokeAsync(() => _config.Save());
            }
            catch (Exception ex)
            {
                Log($"Startup scan failed: {ex}");
            }
        }

        #endregion

        #region SMTC bridging

        private void OnTrackChangedForSmtc(object? sender, TrackChangedEventArgs e)
        {
            if (_smtc == null || e.Track == null) return;

            _ = _smtc.UpdateMetadataAsync(e.Track.Title, e.Track.DisplayArtist, e.ArtworkPath);
        }

        private void OnPlayingStateChangedForSmtc(object? sender, bool isPlaying)
        {
            if (_smtc == null) return;

            // Nothing loaded means no session at all, rather than "paused" — otherwise the
            // flyout keeps advertising us after the queue has run out.
            if (_playback?.CurrentTrack == null)
            {
                _smtc.ClearSession();
                return;
            }

            _smtc.UpdatePlaybackStatus(isPlaying);
        }

        #endregion

        #region Shutdown & diagnostics

        private void ExitApplication()
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            _trayIcon?.Dispose();
            _trayIcon = null;

            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

            _summonWatch?.Cancel();
            _summonWatch?.Dispose();
            _summonSignal?.Dispose();
            _sleepTimer?.Dispose();

            _toast?.HideNow();
            _holdToast?.HideNow();
            _hotkeys?.UnregisterAll();

            // Final snapshot: the debounce timer may still be pending.
            _queueSaveTimer?.Stop();
            if (_playback != null && _queueStore != null)
            {
                try
                {
                    _queueStore.SaveAsync(_playback.Snapshot()).Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Shutdown path; a lost queue is not worth blocking exit over.
                }
            }

            _reporter?.Dispose();
            _smtc?.Dispose();
            _playback?.Dispose();
            _notifyIcon?.Dispose();

            _mutex?.ReleaseMutex();
            _mutex?.Dispose();

            base.OnExit(e);
        }

        private void OnUnhandledException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            // WPF sends unhandled exception detail to the debugger, not to stdout, so a
            // crash from a terminal launch looks like silence. Persist it instead.
            Log($"Unhandled exception: {e.Exception}");

            MessageBox.Show(
                $"Yinyue hit an unexpected error and may not work correctly.\n\n{e.Exception.Message}\n\n" +
                $"Details were written to {Path.Combine(ConfigService.AppDataFolder, "yinyue.log")}",
                "Yinyue", MessageBoxButton.OK, MessageBoxImage.Warning);

            e.Handled = true;
        }

        private static void Log(string message)
        {
            try
            {
                string path = Path.Combine(ConfigService.AppDataFolder, "yinyue.log");
                File.AppendAllText(path, $"{DateTime.Now:u}  {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never be the thing that breaks the app.
            }
        }

        #endregion
    }
}
