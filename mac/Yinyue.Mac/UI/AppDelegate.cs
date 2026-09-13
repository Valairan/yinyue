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

        private readonly MusicLibrary _library;

        public AppDelegate(ConfigService config, PlaybackService playback, MusicLibrary library)
        {
            _config = config;
            _playback = playback;
            _library = library;
        }

        /// <summary>
        /// Keys that reach the overlay rather than the search box.
        ///
        /// "Is the user typing" is a focus question, not a visibility one — the box is always
        /// on screen, so testing visibility would be permanently true and Space would never
        /// reach play/pause again.
        /// </summary>
        private void OnOverlayKey(object? sender, NSEvent e)
        {
            switch (e.KeyCode)
            {
                case 53:   // Escape: clear a search in progress, or dismiss the overlay
                    if (_stack?.HandleEscape() != true) _overlay?.HideOverlay();
                    break;

                case 36:   // Return: play the highlighted result
                    _stack?.PlaySelected();
                    break;

                case 126:  // Up
                    _stack?.MoveSelection(-1);
                    break;

                case 125:  // Down
                    _stack?.MoveSelection(1);
                    break;

                case 49 when !IsTyping:   // Space, only when the caret is not in the box
                    Fire(_playback.TogglePlayPauseAsync());
                    break;

                default:
                    // Any printable character opens search and keeps the character, so the
                    // overlay can be typed into without aiming at the box first.
                    if (!IsTyping && e.Characters is { Length: > 0 } typed
                        && !char.IsControl(typed[0]))
                    {
                        _stack?.FocusSearch();
                        _stack?.SearchBar.AppendTyped(typed);
                    }
                    break;
            }
        }

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
            applet.SettingsRequested += (_, _) => { /* settings window is not built yet */ };
            applet.QueueRequested += (_, _) => { /* the queue panel is not built yet */ };
            _overlay.SetContent(applet);

            // The stack subscribes to the applet's own Shown/Hidden, so there is nothing to
            // wire here beyond the keys.
            _stack = new OverlayStack(_config.Current.Overlay, _library, _playback, _overlay);
            _overlay.KeyReceived += OnOverlayKey;

            BuildHotkeys();

            // Media keys and the Now Playing panel. Owned here rather than by the overlay,
            // because the overlay is hidden almost all the time and these must work anyway.
            _media = new MacMediaControls(_playback);

            if (Environment.GetCommandLineArgs().Contains("--show")) _overlay.ShowOverlay();
        }

        /// <summary>Summons the overlay — used by the tray menu and by a second launch.</summary>
        public void ShowOverlay() => _overlay?.ShowOverlay();

        private void BuildHotkeys()
        {
            _hotkeys = new MacHotkeyManager();
            _hotkeys.Triggered += OnHotkey;

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
                    break;

                case HotkeyActions.CycleLoop:
                    _playback.CycleLoop();
                    break;

                case HotkeyActions.VolumeUp:
                    _playback.AdjustVolume(VolumeStep);
                    break;

                case HotkeyActions.VolumeDown:
                    _playback.AdjustVolume(-VolumeStep);
                    break;

                case HotkeyActions.Mute:
                    _playback.ToggleMute();
                    break;

                case HotkeyActions.QuickSearch:
                    // Summons the overlay and moves the caret. It reveals nothing: the box is
                    // part of the overlay, not something that gets opened.
                    _overlay?.ShowOverlay();
                    _stack?.FocusSearch();
                    break;

                case HotkeyActions.OpenSettings:
                case HotkeyActions.OpenQueue:
                case HotkeyActions.GrabQueueEntry:
                case HotkeyActions.AddToQueue:
                case HotkeyActions.RemoveFromQueue:
                case HotkeyActions.ShuffleFavorites:
                case HotkeyActions.OfflineMode:
                case HotkeyActions.SleepTimer:
                    // These need surfaces that do not exist yet: search, the queue panel,
                    // settings and the sleep timer. Registered now so the combinations are
                    // claimed and conflicts surface early, rather than appearing to work and
                    // then being taken by another app later.
                    break;
            }
        }

        /// <summary>How much one press of the volume shortcuts moves the level.</summary>
        private const double VolumeStep = 0.05;

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
            }

            var menu = new NSMenu();
            menu.AddItem(Item("Show Yinyue", (_, _) => _overlay?.ToggleOverlay()));
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(Item("Settings…", (_, _) => { /* settings window is not built yet */ }));
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
            _media?.Dispose();
            _hotkeys?.Dispose();
            _stack?.Dispose();
            _playback.Dispose();
            _statusItem?.Dispose();
        }
    }
}
