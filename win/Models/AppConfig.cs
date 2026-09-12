using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Yinyue.Models
{
    /// <summary>
    /// Where the overlay anchors itself within the target monitor work area.
    /// </summary>
    public enum OverlayAnchor
    {
        TopLeft,
        TopCenter,
        TopRight,
        Center,
        BottomLeft,
        BottomCenter,
        BottomRight
    }

    /// <summary>
    /// Which monitor the overlay appears on. Cursor and Active follow the user between
    /// displays; Specific pins to <see cref="OverlayConfig.MonitorDeviceName"/>.
    /// </summary>
    public enum MonitorSelection
    {
        Primary,
        Cursor,
        Active,
        Specific
    }

    public class OverlayConfig
    {
        public OverlayAnchor Anchor { get; set; } = OverlayAnchor.BottomRight;

        /// <summary>Gap from the work area edge, in device-independent pixels.</summary>
        public double MarginX { get; set; } = 16;
        public double MarginY { get; set; } = 16;

        public MonitorSelection Monitor { get; set; } = MonitorSelection.Primary;

        /// <summary>Only meaningful when <see cref="Monitor"/> is Specific.</summary>
        public string? MonitorDeviceName { get; set; }

        /// <summary>
        /// Fade the overlay and the toast in and out.
        ///
        /// The animation itself is nearly free; the real cost is that fading requires
        /// AllowsTransparency, which puts the window on a software-composited path. Turning
        /// this off drops transparency too, which is the saving worth having on a weak
        /// machine. Read once at construction, so it takes a restart.
        /// </summary>
        public bool Animations { get; set; } = true;

        public const int DefaultAnimationMilliseconds = 120;
        public const int MinAnimationMilliseconds = 0;
        public const int MaxAnimationMilliseconds = 1000;

        private int _animationMilliseconds = DefaultAnimationMilliseconds;

        /// <summary>
        /// How long the fade takes, each way. Zero is allowed and simply means the fade is
        /// instant, which is a different thing from turning animations off — that also
        /// drops transparency.
        /// </summary>
        public int AnimationMilliseconds
        {
            get => _animationMilliseconds;
            set => _animationMilliseconds =
                Math.Clamp(value, MinAnimationMilliseconds, MaxAnimationMilliseconds);
        }

        public const double DefaultBackgroundOpacity = 1.0;
        public const double MinBackgroundOpacity = 0.2;
        public const double MaxBackgroundOpacity = 1.0;

        private double _backgroundOpacity = DefaultBackgroundOpacity;

        /// <summary>
        /// Opacity of the panel behind the content, for the overlay and the toast only —
        /// never the settings window, which keeps its chrome and stays opaque.
        ///
        /// Applies to the background alone, so text and artwork stay fully legible. Floored
        /// well above zero: a panel you cannot see is indistinguishable from a broken one.
        /// Requires transparency, so it has no effect when Animations is off.
        /// </summary>
        public double BackgroundOpacity
        {
            get => _backgroundOpacity;
            set => _backgroundOpacity = Math.Round(
                Math.Clamp(double.IsFinite(value) ? value : DefaultBackgroundOpacity,
                           MinBackgroundOpacity, MaxBackgroundOpacity), 2);
        }

        /// <summary>Hide the overlay when it loses focus. The PowerToys Run behaviour.</summary>
        public bool HideOnFocusLoss { get; set; } = true;

        /// <summary>
        /// Dismiss the overlay on its own after a spell of no interaction. Any key, click
        /// or pointer movement restarts the clock, and an open search or queue suspends it
        /// entirely — reading is not idling.
        /// </summary>
        public bool AutoHide { get; set; } = true;

        private double _autoHideSeconds = DefaultAutoHideSeconds;

        public const double DefaultAutoHideSeconds = 10;
        public const double MinAutoHideSeconds = 2;
        public const double MaxAutoHideSeconds = 120;

        public double AutoHideSeconds
        {
            get => _autoHideSeconds;
            set => _autoHideSeconds = Math.Round(
                Math.Clamp(double.IsFinite(value) ? value : DefaultAutoHideSeconds,
                           MinAutoHideSeconds, MaxAutoHideSeconds), 1);
        }
    }

    public class JellyfinConfig
    {
        public string ServerUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// DPAPI-protected access token, base64 encoded. Never store the password itself.
        /// Written and read through ConfigService, which handles the protection.
        /// </summary>
        public string? ProtectedAccessToken { get; set; }

        public string? UserId { get; set; }

        /// <summary>
        /// Ceiling for streaming, in bits per second. 0 means no limit, which lets the
        /// server direct-play the original file untouched.
        /// </summary>
        public int MaxStreamingBitrate { get; set; } = 0;

        /// <summary>
        /// Open the next track's stream while the current one plays. Costs one extra
        /// connection and a little memory; buys an instant switch instead of a stall.
        /// </summary>
        public bool PrebufferNext { get; set; } = true;

        [JsonIgnore]
        public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl)
                                    && !string.IsNullOrWhiteSpace(ProtectedAccessToken);
    }

    public class LibraryConfig
    {
        /// <summary>Root folders scanned by LibraryIndexerService.</summary>
        public List<string> Folders { get; set; } = new();

        public DateTime? LastScanUtc { get; set; }

        /// <summary>Rescan configured folders on startup. Off by default to protect cold start.</summary>
        public bool ScanOnStartup { get; set; }
    }

    public class PlaybackConfig
    {
        /// <summary>Linear 0.0-1.0, as WinRT MediaPlayer expects.</summary>
        public double Volume { get; set; } = 1.0;
    }

    /// <summary>
    /// The sleep timer: how long until playback pauses itself.
    /// </summary>
    public class SleepTimerConfig
    {
        /// <summary>
        /// When false the shortcut does nothing and any running timer is cancelled.
        ///
        /// Worth having separately from "currently set to off": the timer is driven by a
        /// hotkey that cycles, so a mistaken press during a working day silently arms
        /// something that stops the music an hour later. This makes that unreachable.
        /// </summary>
        public bool Enabled { get; set; } = true;

        public static readonly int[] DefaultSteps = { 15, 30, 45, 60 };

        /// <summary>A step may not exceed a day; beyond that it is not a sleep timer.</summary>
        public const int MaxStepMinutes = 1440;

        /// <summary>
        /// How many steps the cycle may hold. The only way to reach a step is to press the
        /// shortcut until it comes round, so a long list makes the far end unusable.
        /// </summary>
        public const int MaxSteps = 8;

        private List<int> _steps = new(DefaultSteps);

        /// <summary>
        /// The durations the shortcut cycles through, in minutes. "Off" is not stored here —
        /// it is always the entry point and always reachable, so the cycle can be escaped.
        /// </summary>
        public List<int> Steps
        {
            get => _steps;
            set => _steps = Sanitise(value);
        }

        /// <summary>
        /// Keeps a hand-edited or mistyped list usable: drops anything out of range, removes
        /// duplicates, sorts, and caps the length. An empty result falls back to the defaults
        /// rather than leaving a cycle with nothing in it but "off".
        /// </summary>
        public static List<int> Sanitise(IEnumerable<int>? steps)
        {
            var cleaned = (steps ?? Array.Empty<int>())
                .Where(m => m > 0 && m <= MaxStepMinutes)
                .Distinct()
                .OrderBy(m => m)
                .Take(MaxSteps)
                .ToList();

            return cleaned.Count > 0 ? cleaned : new List<int>(DefaultSteps);
        }
    }

    public class AppConfig
    {
        /// <summary>
        /// Stable per-install identity. Jellyfin keys sessions off this, so it must survive
        /// restarts and must not collide between installs. Generated once on first run.
        /// </summary>
        public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

        public OverlayConfig Overlay { get; set; } = new();
        public JellyfinConfig Jellyfin { get; set; } = new();
        public LibraryConfig Library { get; set; } = new();
        public HotkeyConfig Hotkeys { get; set; } = new();
        public PlaybackConfig Playback { get; set; } = new();

        public SleepTimerConfig SleepTimer { get; set; } = new();

        /// <summary>Prefer the local index over remote sources even when Jellyfin is reachable.</summary>
        public bool OfflineMode { get; set; }
    }
}
