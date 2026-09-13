using AppKit;
using Foundation;
using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue.UI
{
    /// <summary>
    /// The menu-bar presence and the app's lifetime — the counterpart to the tray icon and
    /// single-instance handling in App.xaml.cs.
    ///
    /// The status item is not decoration. With LSUIElement there is no Dock icon, no Dock
    /// menu and no Cmd-Q target, so this menu is the only route into the app that does not
    /// depend on a hotkey being registered successfully. The Windows tray menu carries its
    /// own Settings item for exactly this reason: the overlay can auto-hide at an
    /// inconvenient moment, and must never be the only door.
    /// </summary>
    public sealed class AppDelegate : NSApplicationDelegate
    {
        private readonly ConfigService _config;
        private readonly PlaybackService _playback;

        private NSStatusItem? _statusItem;
        private OverlayPanel? _overlay;
        private MacHotkeyManager? _hotkeys;
        private MacMediaControls? _media;
        private OverlayStack? _stack;
        private MacSleepTimer? _sleep;
        private SettingsWindow? _settings;
        private NSStatusBarButton? _statusButton;
        private AppletView? _applet;
        private JellyfinPlaybackReporter? _reporter;
        private NSObject? _keyMonitor;
        private QueueStore? _queueStore;
        private NSTimer? _queueSave;
        private NSTimer? _volumeSave;

        /// <summary>
        /// Suppresses toasts until startup finishes. Restoring a queue and applying the saved
        /// volume both raise change events, and neither is something the user did.
        /// </summary>
        private bool _ready;

        private readonly MusicLibrary _library;
        private readonly JellyfinApiClient _jellyfin;
        private readonly LibraryIndexerService _indexer;
        private readonly ArtworkCache _artwork;

        public AppDelegate(ConfigService config, PlaybackService playback, MusicLibrary library,
                           JellyfinApiClient jellyfin, LibraryIndexerService indexer,
                           ArtworkCache artwork)
        {
            _config = config;
            _playback = playback;
            _library = library;
            _jellyfin = jellyfin;
            _indexer = indexer;
            _artwork = artwork;
        }

        /// <summary>
        /// Created lazily and kept, so reopening is instant and any typing survives a close.
        /// Activates the app, unlike everything else here: settings is a window you work in,
        /// not a surface you glance at, and it needs the keyboard.
        /// </summary>
        public void ShowSettings()
        {
            try
            {
                _settings ??= new SettingsWindow(_config, _jellyfin, _indexer);
            }
            catch (Exception ex)
            {
                // A menu item that silently does nothing is the worst failure mode there is,
                // and settings is the only route into the app on a fresh install.
                Console.Error.WriteLine($"[Settings] {ex}");
                _stack?.Toast("Settings could not be opened.", Icons.Info,
                    evenWhileOverlayShown: true);
                return;
            }

            // Activate() is the macOS 14 spelling and ActivateIgnoringOtherApps is obsolete
            // from 14 — but the deployment target is 13, so both are needed.
            if (OperatingSystem.IsMacOSVersionAtLeast(14))
                NSApplication.SharedApplication.Activate();
            else
#pragma warning disable CA1422   // obsolete from 14, which the branch above handles
                NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
#pragma warning restore CA1422
            _settings.MakeKeyAndOrderFront(null);
        }

        /// <summary>
        /// Keys belonging to the overlay rather than to the text box.
        ///
        /// Returns null to swallow the event, or the event itself to let it reach the box.
        /// Only runs while one of the overlay's own windows is key, so it never touches the
        /// settings window or anything else.
        /// </summary>
        private NSEvent? HandleKey(NSEvent e)
        {
            if (_stack is null || _overlay is null) return e;
            if (!_overlay.IsVisible) return e;

            // Settings is a real window with its own text fields; it must keep every key.
            if (_settings is { IsKeyWindow: true }) return e;

            switch (e.KeyCode)
            {
                case 53:   // Escape
                    // A held queue entry goes back where it was picked up. Otherwise the
                    // box's CONTENTS decide, exactly as on Windows: a search in progress is
                    // cleared, and only an already-empty box dismisses the overlay.
                    if (_stack.CancelQueueGrab()) return null;
                    if (_stack.QueueIsOpen) { _stack.ToggleQueue(); return null; }
                    if (!_stack.HandleEscape()) _overlay.HideOverlay();
                    return null;

                case 36:   // Return
                    if (_stack.QueueIsOpen) _stack.JumpToQueueSelection();
                    else _stack.PlaySelected();
                    return null;

                case 126:  // Up
                    _stack.MoveSelection(-1);
                    return null;

                case 125:  // Down
                    _stack.MoveSelection(1);
                    return null;

                case 49 when !IsTyping:   // Space, only when the caret is not in the box
                    Fire(_playback.TogglePlayPauseAsync());
                    return null;

                default:
                    // Any printable character opens search and keeps the character, so the
                    // overlay can be typed into without aiming at the box first.
                    if (!IsTyping && e.Characters is { Length: > 0 } typed
                        && !char.IsControl(typed[0]) && (e.ModifierFlags & ModifierKeys) == 0)
                    {
                        _stack.FocusSearch();
                        _stack.SearchBar.AppendTyped(typed);
                        return null;
                    }

                    return e;
            }
        }

        /// <summary>Modifiers that mean a keystroke is a command, not typing.</summary>
        private const NSEventModifierMask ModifierKeys =
            NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask
            | NSEventModifierMask.AlternateKeyMask;

        /// <summary>
        /// "Is the user typing" is a focus question, not a visibility one — the box is always
        /// on screen, so testing visibility would be permanently true and Space would never
        /// reach play/pause again.
        /// </summary>
        private bool IsTyping => _stack?.SearchBar.IsKeyWindow == true;

        public override void DidFinishLaunching(NSNotification notification)
        {
            // Accessory, matching LSUIElement. Set in code as well as in the plist because a
            // debug launch from a bare binary does not always read the bundle.
            NSApplication.SharedApplication.ActivationPolicy =
                NSApplicationActivationPolicy.Accessory;

            BuildStatusItem();

            // Height is provisional until the applet's contents exist; the stack and its
            // reserved toast rows are the next piece of work.
            _overlay = new OverlayPanel(_config.Current.Overlay, OverlayMetrics.AppletHeight);

            var applet = new AppletView(_playback);
            applet.ShowArtwork(_playback.CurrentArtworkPath);
            applet.SettingsRequested += (_, _) => ShowSettings();
            applet.QueueRequested += (_, _) => _stack?.ToggleQueue();
            applet.OfflineRequested += (_, _) => ToggleOffline();
            applet.ShuffleFavoritesRequested += (_, _) => _ = ShuffleFavoritesAsync();
            applet.FavoriteRequested += (_, _) => _ = ToggleFavoriteAsync();
            _applet = applet;
            _overlay.SetContent(applet);

            // The stack subscribes to the applet's own Shown/Hidden, so there is nothing to
            // wire here beyond the keys.
            _stack = new OverlayStack(_config.Current.Overlay, _library, _playback, _overlay);

            // An open search or queue, or settings in front, means the user is reading rather
            // than idling — so the countdown is suspended rather than reset.
            _overlay.SuspendAutoHide = () =>
                _stack?.QueueIsOpen == true
                || _stack?.SearchBar.HasText == true
                || _settings is { IsVisible: true };

            // A local monitor rather than the panel's KeyDown.
            //
            // The search box is the only panel that can become key, so once the caret is in
            // it every keystroke goes there and the overlay's own KeyDown is never called --
            // Escape, Return and the arrows all went nowhere. A local monitor sees key events
            // before they are dispatched to any window in this app, which is the only place
            // that can arbitrate between the box and the panels around it.
            _keyMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(
                NSEventMask.KeyDown, e => HandleKey(e)!);

            BuildSleepTimer();
            BuildHotkeys();

            // Media keys and the Now Playing panel. Owned here rather than by the overlay,
            // because the overlay is hidden almost all the time and these must work anyway.
            _media = new MacMediaControls(_playback);
            UpdateTooltip();

            // Start and progress reporting, so the server knows what is playing: resume
            // points, play counts, and "now playing" in other clients all come from this.
            _reporter = new JellyfinPlaybackReporter(_playback, _jellyfin);

            applet.ShowOffline(_config.Current.OfflineMode);

            // After the overlay exists, so its handlers see the restored state.
            RestoreQueue();
            SetUpPersistence();

            // Deferred so none of it is on the startup path.
            if (_config.Current.Library.ScanOnStartup) _ = Task.Run(ScanLibraryAsync);
            _ = _artwork.PruneAsync();

            // Everything from here is the user's doing, so it may announce itself.
            _ready = true;

            // Only deliberate changes are announced. An automatic advance at the end of a
            // track would fire all day for something the user never asked for.
            _playback.TrackChanged += (_, args) => NSApplication.SharedApplication
                .BeginInvokeOnMainThread(() => _applet?.ShowFavorite(args.Track?.IsFavorite == true));

            _playback.TrackChanged += (_, args) =>
            {
                if (!_ready || args.Automatic || args.Track is null) return;

                NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                    _stack?.Toast($"{args.Track.Title} — {args.Track.DisplayArtist}", Icons.Music));
            };

            if (Environment.GetCommandLineArgs().Contains("--show")) _overlay.ShowOverlay();
        }

        /// <summary>
        /// Brings back what was playing, armed but silent: the queue and the position are
        /// restored, and nothing starts until the user asks.
        /// </summary>
        private void RestoreQueue()
        {
            _queueStore = new QueueStore();

            if (_queueStore.Load() is not { } state) return;

            _playback.RestoreQueue(state.Tracks, state.Position,
                TimeSpan.FromSeconds(state.TrackPositionSeconds), state.Shuffle, state.Loop);

            _playback.Volume = _config.Current.Playback.Volume;
        }

        /// <summary>
        /// Both saves are debounced. The queue churns on every skip and the volume walks a
        /// step at a time while a key is held; writing on each change would rewrite the files
        /// dozens of times for one gesture.
        /// </summary>
        private void SetUpPersistence()
        {
            _playback.QueueChanged += (_, _) => Debounce(ref _queueSave, 2.0, SaveQueue);
            _playback.ModesChanged += (_, _) => Debounce(ref _queueSave, 2.0, SaveQueue);
            _playback.TrackChanged += (_, _) => Debounce(ref _queueSave, 2.0, SaveQueue);

            _playback.VolumeChanged += (_, _) => Debounce(ref _volumeSave, 1.0, SaveVolume);
        }

        private static void Debounce(ref NSTimer? timer, double seconds, Action work)
        {
            timer?.Invalidate();
            timer = NSTimer.CreateScheduledTimer(seconds, _ => work());
        }

        private void SaveQueue()
        {
            if (_queueStore is null) return;
            _ = _queueStore.SaveAsync(_playback.Snapshot());
        }

        private void SaveVolume()
        {
            // EffectiveVolume, not Volume: saving the raw level while muted would write 0 and
            // the app would come back silent with nothing explaining why.
            _config.Current.Playback.Volume = _playback.EffectiveVolume;
            _config.Save();
        }

        private async Task ScanLibraryAsync()
        {
            try
            {
                foreach (var folder in _config.Current.Library.Folders)
                    await _indexer.IndexDirectoryAsync(folder).ConfigureAwait(false);

                await _indexer.PruneAsync(_config.Current.Library.Folders).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Scan] {ex.Message}");
            }
        }

        /// <summary>Summons the overlay — used by the tray menu and by a second launch.</summary>
        public void ShowOverlay() => _overlay?.ShowOverlay();

        /// <summary>
        /// The menu-bar tooltip is the one surface always available while the overlay is
        /// hidden, so it carries the volume and the sleep timer's remaining time. Written
        /// from one place because it has two sources and they would otherwise overwrite each
        /// other — the Windows tray tooltip has the same problem and the same fix.
        /// </summary>
        private void UpdateTooltip()
        {
            if (_statusButton is null) return;

            var parts = new List<string> { "Yinyue" };

            if (_playback.CurrentTrack is { } track)
                parts.Add($"{track.Title} — {track.DisplayArtist}");

            parts.Add(_playback.IsMuted ? "Muted" : $"Volume {Math.Round(_playback.Volume * 100)}%");

            if (_sleep is { IsRunning: true } sleep)
                parts.Add($"Sleep in {MacSleepTimer.Describe(sleep.Remaining)}");

            _statusButton.ToolTip = string.Join("  ·  ", parts);
        }

        private void BuildSleepTimer()
        {
            _sleep = new MacSleepTimer
            {
                Enabled = _config.Current.SleepTimer.Enabled,
            };

            _sleep.SetSteps(_config.Current.SleepTimer.Steps);
            _sleep.Elapsed += () =>
            {
                Fire(_playback.PauseAsync());

                NSApplication.SharedApplication.BeginInvokeOnMainThread(
                    () => _stack?.Toast("Sleep timer finished — paused", Icons.Moon));
            };

            _sleep.Changed += _ => NSApplication.SharedApplication.BeginInvokeOnMainThread(UpdateTooltip);

            _playback.VolumeChanged += (_, _) =>
                NSApplication.SharedApplication.BeginInvokeOnMainThread(UpdateTooltip);

            _playback.TrackChanged += (_, _) =>
                NSApplication.SharedApplication.BeginInvokeOnMainThread(UpdateTooltip);
        }

        private void BuildHotkeys()
        {
            _hotkeys = new MacHotkeyManager();
            _hotkeys.Triggered += OnHotkey;

            // The dial is hosted by the toast rather than the overlay, because the overlay is
            // usually hidden when these gestures are made — play/pause and restart
            // deliberately do not summon it.
            _hotkeys.HoldProgress += (_, e) =>
                _stack?.ShowHold(HoldLabel(e.Action), e.Fraction);

            _hotkeys.HoldEnded += (_, _) => _stack?.EndHold();

            var refused = _hotkeys.Apply(_config.Current.Hotkeys);
            if (refused.Count == 0) return;

            // A combination another application already owns cannot be detected until
            // registration is attempted -- the same limitation Windows has. Say so rather
            // than leaving a shortcut silently dead.
            Console.Error.WriteLine("Some shortcuts could not be registered:");
            foreach (var line in refused) Console.Error.WriteLine($"  {line}");
        }

        /// <summary>
        /// Global shortcuts act on <see cref="PlaybackService"/>, never on the overlay's
        /// state, because the overlay is usually hidden when they are pressed. Only the ones
        /// that are about the window itself touch the window.
        /// </summary>
        private void OnHotkey(object? sender, HotkeyTriggeredEventArgs e)
        {
            switch (e.Action)
            {
                case HotkeyActions.ToggleOverlay:
                    _overlay?.ToggleOverlay();
                    break;

                case HotkeyActions.PlayPause:
                    // Tap plays or pauses; hold walks the queue forward, once per interval.
                    // The one escalation that deliberately does NOT summon the overlay, since
                    // play/pause is used while working in another window.
                    if (e.Held) Fire(_playback.NextAsync());
                    else Fire(_playback.TogglePlayPauseAsync());
                    break;

                case HotkeyActions.RestartOrPrevious:
                    if (e.Held) Fire(_playback.PreviousAsync(alwaysChangeTrack: true));
                    else _playback.RestartTrack();
                    break;

                case HotkeyActions.ToggleShuffle:
                    _playback.ToggleShuffle();
                    AnnounceModes();
                    break;

                case HotkeyActions.CycleLoop:
                    _playback.CycleLoop();
                    AnnounceModes();
                    break;

                case HotkeyActions.VolumeUp:
                    AnnounceVolume(_playback.AdjustVolume(VolumeStep));
                    break;

                case HotkeyActions.VolumeDown:
                    AnnounceVolume(_playback.AdjustVolume(-VolumeStep));
                    break;

                case HotkeyActions.Mute:
                    _playback.ToggleMute();
                    AnnounceVolume(_playback.IsMuted ? 0 : _playback.Volume);
                    break;

                case HotkeyActions.OpenQueue:
                    _overlay?.ShowOverlay();
                    _stack?.ToggleQueue();
                    break;

                case HotkeyActions.GrabQueueEntry:
                    _stack?.GrabQueueEntry();
                    break;

                case HotkeyActions.AddToQueue:
                    // Tap queues at the end, hold plays it next.
                    _stack?.AddSelectedToQueue(next: e.Held);
                    break;

                case HotkeyActions.RemoveFromQueue:
                    // Tap removes the selected entry; the hold clears the queue, and that
                    // hold is the only guard on an action that cannot be undone.
                    if (e.Held) _ = _stack?.ClearQueueAsync();
                    else _stack?.RemoveQueueEntry();
                    break;

                case HotkeyActions.QuickSearch:
                    // Summons the overlay and moves the caret. It reveals nothing: the box is
                    // part of the overlay, not something that gets opened.
                    _overlay?.ShowOverlay();
                    _stack?.FocusSearch();
                    break;

                case HotkeyActions.OfflineMode:
                    ToggleOffline();
                    break;

                case HotkeyActions.ShuffleFavorites:
                    _ = ShuffleFavoritesAsync();
                    break;

                case HotkeyActions.SleepTimer:
                    // Pressing it while disabled says so. Silence would read as a broken
                    // shortcut.
                    if (_sleep is null || !_sleep.Enabled)
                    {
                        _stack?.Toast("Sleep timer is turned off in settings", Icons.Moon);
                        break;
                    }

                    int minutes = _sleep.Cycle();
                    _stack?.Toast(minutes == 0
                        ? "Sleep timer off"
                        : $"Sleep timer {MacSleepTimer.Describe(TimeSpan.FromMinutes(minutes))}",
                        Icons.Moon);
                    break;

                case HotkeyActions.OpenSettings:
                    ShowSettings();
                    break;
            }
        }

        /// <summary>
        /// What the dial says while the key is held. Names the bigger action, since that is
        /// what continuing to hold will do — the tap has already been given up by then.
        /// </summary>
        private static string HoldLabel(string action) => action switch
        {
            HotkeyActions.PlayPause => "Keep holding to skip",
            HotkeyActions.RestartOrPrevious => "Keep holding to step back",
            HotkeyActions.AddToQueue => "Keep holding to play next",
            HotkeyActions.RemoveFromQueue => "Keep holding to clear the queue",
            _ => HotkeyActions.Describe(action),
        };

        /// <summary>How much one press of the volume shortcuts moves the level.</summary>
        private const double VolumeStep = 0.05;

        /// <summary>
        /// Shuffle-favourites caps at 1000 and holds them in memory, as on Windows. Only
        /// Jellyfin implements favourites, so this says so while offline rather than showing
        /// an empty queue.
        /// </summary>
        private const int FavoritesCap = 1000;

        /// <summary>
        /// Volume is the one change that toasts even while the overlay is showing. It used to
        /// switch surfaces with the overlay's state, and the same gesture reading two
        /// different ways was less polished than one consistent readout with a level bar.
        /// The toast has its own reserved row, so it never covers anything.
        /// </summary>
        private void AnnounceVolume(double level)
        {
            bool muted = _playback.IsMuted || level <= 0.0001;

            _stack?.Toast(muted ? "Muted" : $"Volume {Math.Round(level * 100)}%",
                muted ? Icons.VolumeX : Icons.Volume2,
                level, evenWhileOverlayShown: true);
        }

        /// <summary>
        /// Loop and shuffle share one toast, as on Windows. They are one idea — how the queue
        /// is being played — and two readouts arriving together would fight for the same row.
        /// </summary>
        private void AnnounceModes()
        {
            string loop = _playback.Loop switch
            {
                LoopMode.Track => "Repeat track",
                LoopMode.Queue => "Repeat queue",
                _ => "Repeat off",
            };

            string icon = _playback.Loop switch
            {
                LoopMode.Track => Icons.Repeat1,
                LoopMode.Queue => Icons.Repeat,
                _ => Icons.RepeatOff,
            };

            _stack?.Toast(_playback.Shuffle ? $"{loop} · shuffle on" : loop, icon);
        }

        private void ToggleOffline()
        {
            _config.Current.OfflineMode = !_config.Current.OfflineMode;
            _config.Save();

            _applet?.ShowOffline(_config.Current.OfflineMode);

            _stack?.Toast(_config.Current.OfflineMode ? "Offline mode on" : "Offline mode off",
                _config.Current.OfflineMode ? Icons.CloudOff : Icons.Cloud);
        }

        /// <summary>
        /// Only Jellyfin implements favourites — the local index has no such concept — so
        /// this says so rather than appearing to do nothing.
        /// </summary>
        private async Task ToggleFavoriteAsync()
        {
            if (_playback.CurrentTrack is not { } track)
            {
                _stack?.Toast("Nothing playing.", Icons.Music, evenWhileOverlayShown: true);
                return;
            }

            var source = _library.Sources.OfType<ISupportsFavorites>().FirstOrDefault();
            if (source is null)
            {
                _stack?.Toast("No source provides favourites.", Icons.Heart,
                    evenWhileOverlayShown: true);
                return;
            }

            bool wanted = !track.IsFavorite;
            bool ok = await source.SetFavoriteAsync(track, wanted, CancellationToken.None)
                                  .ConfigureAwait(false);

            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                if (!ok)
                {
                    _stack?.Toast("Could not update favourites.", Icons.Heart,
                        evenWhileOverlayShown: true);
                    return;
                }

                track.IsFavorite = wanted;
                _applet?.ShowFavorite(wanted);

                _stack?.Toast(wanted ? "Added to favourites" : "Removed from favourites",
                    wanted ? Icons.Heart : Icons.HeartPlus);
            });
        }

        private async Task ShuffleFavoritesAsync()
        {
            var source = _library.Sources.OfType<ISupportsFavorites>().FirstOrDefault();
            if (source is null)
            {
                _stack?.Toast("No source provides favourites.", Icons.Heart);
                return;
            }

            var result = await source.GetFavoritesAsync(FavoritesCap, CancellationToken.None)
                                     .ConfigureAwait(false);

            if (result.Tracks.Count == 0)
            {
                _stack?.Toast(result.Error ?? "No favourites found.", Icons.Heart);
                return;
            }

            if (!_playback.Shuffle) _playback.ToggleShuffle();
            await _playback.PlayQueueAsync(result.Tracks, 0).ConfigureAwait(false);
        }

        private static void Fire(Task work) =>
            _ = work.ContinueWith(t => Console.Error.WriteLine($"[Hotkey] {t.Exception}"),
                TaskContinuationOptions.OnlyOnFaulted);

        private void BuildStatusItem()
        {
            _statusItem = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Square);

            if (_statusItem.Button is { } button)
            {
                // The same 音樂 mark the Windows tray shows, from the same master in Common/.
                //
                // ONE asset here, not the pair Windows needs. A template image is a mask:
                // AppKit reads only its alpha and draws it in whatever colour the menu bar
                // wants, inverting automatically between light and dark. So there is no
                // tray-light/tray-dark choice to make and nothing to watch for a theme
                // change -- which is App.ApplyTrayIcon's entire job on Windows.
                var mark = NSImage.ImageNamed("menubar");
                if (mark is not null)
                {
                    mark.Template = true;
                    button.Image = mark;
                }
                else
                {
                    button.Title = "Yinyue";
                }

                button.ToolTip = "Yinyue";
                _statusButton = button;
            }

            var menu = new NSMenu();
            menu.AddItem(Item("Show Yinyue", (_, _) => _overlay?.ToggleOverlay()));
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(Item("Settings…", (_, _) => ShowSettings()));
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(Item("Quit Yinyue", (_, _) => NSApplication.SharedApplication.Terminate(this)));

            _statusItem.Menu = menu;
        }

        private static NSMenuItem Item(string title, EventHandler handler)
        {
            var item = new NSMenuItem(title);
            item.Activated += handler;
            return item;
        }

        public override void WillTerminate(NSNotification notification)
        {
            // Before playback, so the Now Playing panel is cleared while there is still a
            // service to read state from.
            _reporter?.Dispose();
            _media?.Dispose();
            _hotkeys?.Dispose();
            // Flush rather than lose whatever the debounce was still holding.
            _queueSave?.Invalidate();
            _volumeSave?.Invalidate();
            SaveQueue();
            SaveVolume();

            if (_keyMonitor is not null) NSEvent.RemoveMonitor(_keyMonitor);
            _stack?.Dispose();
            _sleep?.Dispose();
            _playback.Dispose();
            _statusItem?.Dispose();
        }
    }
}
