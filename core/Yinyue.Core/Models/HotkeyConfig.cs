using System;
using System.Collections.Generic;

namespace Yinyue.Models
{
    /// <summary>
    /// Well-known action names. These are the keys in <see cref="HotkeyConfig.Bindings"/>,
    /// the Tag on each settings row, and what <c>HotkeyManager.Triggered</c> reports — one
    /// vocabulary rather than three parallel ones.
    /// </summary>
    public static class HotkeyActions
    {
        public const string ToggleOverlay = "ToggleOverlay";
        public const string QuickSearch = "QuickSearch";
        public const string OfflineMode = "OfflineMode";
        public const string CycleLoop = "CycleLoop";
        public const string ToggleShuffle = "ToggleShuffle";
        public const string VolumeUp = "VolumeUp";
        public const string VolumeDown = "VolumeDown";
        public const string Mute = "Mute";
        public const string ShuffleFavorites = "ShuffleFavorites";
        public const string AddToQueue = "AddToQueue";
        public const string RemoveFromQueue = "RemoveFromQueue";
        public const string OpenQueue = "OpenQueue";
        public const string OpenSettings = "OpenSettings";
        public const string GrabQueueEntry = "GrabQueueEntry";
        public const string PlayPause = "PlayPause";
        public const string RestartOrPrevious = "RestartOrPrevious";
        public const string SleepTimer = "SleepTimer";

        /// <summary>In the order they appear in settings.</summary>
        public static readonly string[] All =
        {
            ToggleOverlay, QuickSearch, OpenQueue, GrabQueueEntry, OpenSettings,
            PlayPause, RestartOrPrevious,
            AddToQueue, RemoveFromQueue,
            ShuffleFavorites, ToggleShuffle, CycleLoop, OfflineMode,
            VolumeUp, VolumeDown, Mute,
            SleepTimer,
        };

        public static string Describe(string action) => action switch
        {
            ToggleOverlay => "Show / hide overlay",
            QuickSearch => "Search",
            OfflineMode => "Toggle offline mode",
            CycleLoop => "Cycle loop mode",
            ToggleShuffle => "Toggle shuffle",
            VolumeUp => "Volume up",
            VolumeDown => "Volume down",
            Mute => "Mute / unmute",
            ShuffleFavorites => "Shuffle favourites",
            AddToQueue => "Add to queue",
            RemoveFromQueue => "Remove from queue",
            OpenQueue => "Open queue",
            OpenSettings => "Open settings",
            GrabQueueEntry => "Move a queue entry",
            PlayPause => "Play / pause",
            RestartOrPrevious => "Restart / previous",
            SleepTimer => "Sleep timer",
            _ => action,
        };

        /// <summary>
        /// Actions whose hold gesture is already spoken for, so the generic
        /// hold-to-activate toggle would contradict them.
        ///
        /// Every tap/hold pair is such a case: a tap does the ordinary thing and a hold does
        /// the bigger version. Letting the user also demand a hold before the tap fires
        /// would leave no gesture meaning "just do the ordinary thing".
        /// </summary>
        public static bool SupportsHoldToggle(string action) =>
            action != RemoveFromQueue && action != AddToQueue &&
            action != PlayPause && action != RestartOrPrevious &&
            action != ShuffleFavorites;

        public static string? HoldNote(string action) => action switch
        {
            RemoveFromQueue => "Tap removes one · hold clears the queue",
            AddToQueue => "Tap queues at the end · hold plays it next",
            PlayPause => "Tap plays or pauses · hold skips forward, repeatedly",
            RestartOrPrevious => "Tap restarts the track · hold steps back, repeatedly",
            ShuffleFavorites => "Tap plays the favourites shuffled · hold adds them to the queue instead",
            SleepTimer => "Cycles off · 15 · 30 · 45 · 60 minutes",
            _ => null,
        };
    }

    public class HotkeyBindingConfig
    {
        public string Keys { get; set; } = string.Empty;

        /// <summary>
        /// Require the combination to be held for <see cref="HotkeyConfig.HoldDelayMs"/>
        /// before it fires. A deliberate speed bump for destructive or disruptive actions.
        /// </summary>
        public bool Hold { get; set; }
    }

    /// <summary>
    /// Global hotkeys. Stored as an action-keyed map so adding an action does not mean
    /// touching the config schema, and so the settings UI can be generated from it.
    /// </summary>
    public class HotkeyConfig
    {
        public const string DefaultToggleOverlay = "Ctrl+Alt+Space";
        public const string DefaultQuickSearch = "Ctrl+Alt+S";
        public const string DefaultOfflineMode = "Ctrl+Alt+L";
        public const string DefaultCycleLoop = "Ctrl+Alt+R";
        public const string DefaultToggleShuffle = "Ctrl+Alt+X";
        public const string DefaultVolumeUp = "Ctrl+Alt+Plus";
        public const string DefaultVolumeDown = "Ctrl+Alt+Minus";
        public const string DefaultMute = "Ctrl+Alt+M";
        public const string DefaultShuffleFavorites = "Ctrl+Alt+F";
        public const string DefaultAddToQueue = "Ctrl+Alt+Pipe";
        public const string DefaultRemoveFromQueue = "Ctrl+Alt+Backspace";
        public const string DefaultOpenQueue = "Ctrl+Alt+Q";
        public const string DefaultOpenSettings = "Ctrl+Alt+I";
        public const string DefaultGrabQueueEntry = "Ctrl+Alt+G";
        public const string DefaultPlayPause = "Ctrl+Alt+P";
        public const string DefaultRestartOrPrevious = "Ctrl+Alt+O";
        public const string DefaultSleepTimer = "Ctrl+Alt+T";

        public const double DefaultHoldDelaySeconds = 0.8;
        public const double MinHoldDelaySeconds = 0.2;
        public const double MaxHoldDelaySeconds = 5.0;

        /// <summary>Millisecond precision, expressed in seconds.</summary>
        public const int HoldDelayDecimals = 3;

        private double _holdDelaySeconds = DefaultHoldDelaySeconds;

        /// <summary>
        /// How long a hold-enabled shortcut must be held, in seconds.
        ///
        /// Clamped and rounded on the way in: zero would make the toggle meaningless, a huge
        /// value would look like a hang, and more than millisecond precision is beyond both
        /// the poll interval and human reaction time.
        /// </summary>
        public double HoldDelaySeconds
        {
            get => _holdDelaySeconds;
            set => _holdDelaySeconds = Math.Round(
                Math.Clamp(double.IsFinite(value) ? value : DefaultHoldDelaySeconds,
                           MinHoldDelaySeconds, MaxHoldDelaySeconds),
                HoldDelayDecimals);
        }

        public Dictionary<string, HotkeyBindingConfig> Bindings { get; set; } = CreateDefaults();

        public static Dictionary<string, HotkeyBindingConfig> CreateDefaults() =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                [HotkeyActions.ToggleOverlay] = new() { Keys = DefaultToggleOverlay },
                [HotkeyActions.QuickSearch] = new() { Keys = DefaultQuickSearch },
                [HotkeyActions.OfflineMode] = new() { Keys = DefaultOfflineMode },
                [HotkeyActions.CycleLoop] = new() { Keys = DefaultCycleLoop },
                [HotkeyActions.ToggleShuffle] = new() { Keys = DefaultToggleShuffle },
                [HotkeyActions.VolumeUp] = new() { Keys = DefaultVolumeUp },
                [HotkeyActions.VolumeDown] = new() { Keys = DefaultVolumeDown },
                [HotkeyActions.Mute] = new() { Keys = DefaultMute },
                [HotkeyActions.ShuffleFavorites] = new() { Keys = DefaultShuffleFavorites },
                [HotkeyActions.AddToQueue] = new() { Keys = DefaultAddToQueue },
                [HotkeyActions.RemoveFromQueue] = new() { Keys = DefaultRemoveFromQueue },
                [HotkeyActions.OpenQueue] = new() { Keys = DefaultOpenQueue },
                [HotkeyActions.OpenSettings] = new() { Keys = DefaultOpenSettings },
                [HotkeyActions.GrabQueueEntry] = new() { Keys = DefaultGrabQueueEntry },
                [HotkeyActions.PlayPause] = new() { Keys = DefaultPlayPause },
                [HotkeyActions.RestartOrPrevious] = new() { Keys = DefaultRestartOrPrevious },
                [HotkeyActions.SleepTimer] = new() { Keys = DefaultSleepTimer },
            };

        public static string DefaultKeysFor(string action) => action switch
        {
            HotkeyActions.ToggleOverlay => DefaultToggleOverlay,
            HotkeyActions.QuickSearch => DefaultQuickSearch,
            HotkeyActions.OfflineMode => DefaultOfflineMode,
            HotkeyActions.CycleLoop => DefaultCycleLoop,
            HotkeyActions.ToggleShuffle => DefaultToggleShuffle,
            HotkeyActions.VolumeUp => DefaultVolumeUp,
            HotkeyActions.VolumeDown => DefaultVolumeDown,
            HotkeyActions.Mute => DefaultMute,
            HotkeyActions.ShuffleFavorites => DefaultShuffleFavorites,
            HotkeyActions.AddToQueue => DefaultAddToQueue,
            HotkeyActions.RemoveFromQueue => DefaultRemoveFromQueue,
            HotkeyActions.OpenQueue => DefaultOpenQueue,
            HotkeyActions.OpenSettings => DefaultOpenSettings,
            HotkeyActions.GrabQueueEntry => DefaultGrabQueueEntry,
            HotkeyActions.PlayPause => DefaultPlayPause,
            HotkeyActions.RestartOrPrevious => DefaultRestartOrPrevious,
            HotkeyActions.SleepTimer => DefaultSleepTimer,
            _ => string.Empty,
        };

        /// <summary>
        /// Shortcuts whose default has been reassigned, mapped to the value they used to
        /// hold. A config written before the change still carries the old value, which now
        /// collides with whatever took the key over — and the collision would disable both
        /// shortcuts rather than just one.
        ///
        /// Only a binding still sitting on its superseded default is moved. Anything the
        /// user chose deliberately is left alone, even if that means a conflict they can
        /// see and resolve themselves.
        /// </summary>
        private static readonly Dictionary<string, string> SupersededDefaults = new()
        {
            // Ctrl+Alt+O became restart/previous, pairing with Ctrl+Alt+P for play/pause.
            [HotkeyActions.OfflineMode] = "Ctrl+Alt+O",
        };

        /// <summary>
        /// Moves abandoned defaults onto their replacements. Returns true when something
        /// changed, so the caller knows to persist it.
        /// </summary>
        public bool MigrateSupersededDefaults()
        {
            bool changed = false;

            foreach (var (action, oldDefault) in SupersededDefaults)
            {
                if (!Bindings.TryGetValue(action, out var entry)) continue;
                if (!string.Equals(entry.Keys, oldDefault, StringComparison.OrdinalIgnoreCase)) continue;

                string replacement = DefaultKeysFor(action);
                if (string.Equals(entry.Keys, replacement, StringComparison.OrdinalIgnoreCase)) continue;

                entry.Keys = replacement;
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// The stored binding for an action, or its default. Never returns null, so a config
        /// missing an entry — an older file, or a hand-edit — still yields a working app.
        /// </summary>
        public HotkeyBindingConfig For(string action)
        {
            if (Bindings.TryGetValue(action, out var existing) &&
                !string.IsNullOrWhiteSpace(existing.Keys))
            {
                return existing;
            }

            var fallback = new HotkeyBindingConfig { Keys = DefaultKeysFor(action) };
            Bindings[action] = fallback;
            return fallback;
        }
    }
}
