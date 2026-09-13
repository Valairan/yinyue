using AppKit;
using CoreGraphics;
using Foundation;
using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue.UI
{
    /// <summary>
    /// The applet: artwork and status, then title and artist, then the seek row, then
    /// transport. The same four rows the Windows overlay carries, in the same order, because
    /// the two apps are meant to look alike by design even though they share no UI code.
    ///
    /// It is a <i>view</i> of playback and owns none of it. Every piece of state here comes
    /// from <see cref="PlaybackService"/> events and nothing is cached beyond what is being
    /// displayed — the same rule as MainWindow on Windows, and for the same reason: the panel
    /// spends most of its life hidden while playback carries on without it.
    /// </summary>
    public sealed class AppletView : NSView
    {
        private const double Pad = 12;
        private const double ArtSize = 56;

        private readonly PlaybackService _playback;

        private readonly NSImageView _art = new();
        private readonly NSTextField _status = Label(11, dim: true);
        private readonly NSTextField _title = Label(13, bold: true);
        private readonly NSTextField _artist = Label(11, dim: true);
        private readonly NSTextField _elapsed = Label(10, dim: true);
        private readonly NSTextField _total = Label(10, dim: true);
        private readonly NSSlider _seek = new();

        private readonly NSButton _previous;
        private readonly NSButton _playPause;
        private readonly NSButton _next;
        private readonly NSButton _shuffle;
        private readonly NSButton _loop;
        private readonly NSButton _settings;

        /// <summary>
        /// True while the user is dragging the seek handle. Progress events keep arriving
        /// four times a second and would otherwise yank the handle back under the pointer.
        /// </summary>
        private bool _scrubbing;

        public event EventHandler? SettingsRequested;

        public AppletView(CGRect frame, PlaybackService playback) : base(frame)
        {
            _playback = playback;

            WantsLayer = true;

            _previous = Button("backward.end.fill", "Previous", 16, OnPrevious);
            _playPause = Button("play.fill", "Play / pause", 16, OnPlayPause);
            _next = Button("forward.end.fill", "Next", 16, OnNext);
            _shuffle = Button("shuffle", "Shuffle", 12, OnShuffle);
            _loop = Button("repeat", "Loop", 12, OnLoop);
            _settings = Button("gearshape.fill", "Settings", 12,
                (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));

            BuildLayout();
            Subscribe();
            Refresh();
        }

        // ---------------------------------------------------------------- layout

        private void BuildLayout()
        {
            double w = Frame.Width;
            double inner = w - Pad * 2;

            _art.Frame = new CGRect(Pad, Frame.Height - Pad - ArtSize, ArtSize, ArtSize);
            _art.WantsLayer = true;
            _art.Layer!.CornerRadius = 6;
            _art.Layer.MasksToBounds = true;
            _art.Layer.BackgroundColor = Theme.Surface0.CGColor;
            _art.ImageScaling = NSImageScale.ProportionallyUpOrDown;
            AddSubview(_art);

            double textLeft = Pad + ArtSize + 10;
            double textWidth = w - textLeft - Pad - 28;

            // The settings button sits in its own column and is never collapsed. On Windows
            // hiding it once locked users out of the app entirely: the overlay opens into
            // search when nothing is playing, and a fresh install had no reachable way to add
            // a library, so nothing could ever play. Keep it visible unconditionally.
            _settings.Frame = new CGRect(w - Pad - 24, Frame.Height - Pad - 24, 24, 24);
            AddSubview(_settings);

            _status.Frame = new CGRect(textLeft, Frame.Height - Pad - 18, textWidth, 16);
            AddSubview(_status);

            _title.Frame = new CGRect(textLeft, Frame.Height - Pad - 38, textWidth, 18);
            AddSubview(_title);

            _artist.Frame = new CGRect(textLeft, Frame.Height - Pad - 56, textWidth, 16);
            AddSubview(_artist);

            double seekY = Pad + 44;
            _elapsed.Frame = new CGRect(Pad, seekY, 38, 14);
            _elapsed.Alignment = NSTextAlignment.Left;
            AddSubview(_elapsed);

            _total.Frame = new CGRect(w - Pad - 38, seekY, 38, 14);
            _total.Alignment = NSTextAlignment.Right;
            AddSubview(_total);

            _seek.Frame = new CGRect(Pad + 44, seekY - 2, inner - 88, 18);
            _seek.MinValue = 0;
            _seek.MaxValue = 100;
            _seek.DoubleValue = 0;
            _seek.Continuous = true;
            _seek.Activated += OnSeekChanged;
            AddSubview(_seek);

            // Centred transport row. The buttons are fixed-width so swapping the play glyph
            // for pause cannot shift the row — the same reason MediaBtnStyle carries a
            // MinWidth on Windows, where the play triangle measures 8px narrower than
            // everything beside it and made the whole row appear to jump.
            NSButton[] transport = { _previous, _playPause, _next };
            const double btn = 34, gap = 6;
            double totalW = transport.Length * btn + (transport.Length - 1) * gap;
            double x = (w - totalW) / 2;

            foreach (var b in transport)
            {
                b.Frame = new CGRect(x, Pad, btn, btn);
                AddSubview(b);
                x += btn + gap;
            }

            _shuffle.Frame = new CGRect(w - Pad - 28 - 6 - 28, Pad + 4, 28, 26);
            _loop.Frame = new CGRect(w - Pad - 28, Pad + 4, 28, 26);
            AddSubview(_shuffle);
            AddSubview(_loop);
        }

        private static NSTextField Label(double size, bool bold = false, bool dim = false)
        {
            var f = new NSTextField
            {
                Editable = false,
                Selectable = false,
                Bezeled = false,
                DrawsBackground = false,
                TextColor = dim ? Theme.Subtext : Theme.Text,
                Font = SystemFont(size, bold),
                LineBreakMode = NSLineBreakMode.TruncatingTail,
                StringValue = string.Empty,
            };
            return f;
        }

        /// <summary>
        /// System fonts are always present, but the binding types them as nullable. Asserted
        /// in one place rather than scattering null-forgiving operators through the layout.
        /// </summary>
        private static NSFont SystemFont(double size, bool bold = false) =>
            (bold ? NSFont.BoldSystemFontOfSize((nfloat)size) : NSFont.SystemFontOfSize((nfloat)size))
            ?? NSFont.SystemFontOfSize(NSFont.SystemFontSize)!;

        /// <summary>
        /// SF Symbols by name, not by literal glyph. The private-use codepoints render only
        /// in SF Pro and turn into tofu anywhere else — including in this source file — while
        /// the named API scales with the button, follows ContentTintColor, and is what
        /// VoiceOver reads. The accessibility description is the tooltip for the same reason.
        /// </summary>
        private NSButton Button(string symbol, string tip, double size, EventHandler handler)
        {
            var image = NSImage.GetSystemSymbol(symbol, tip);

            var b = new NSButton
            {
                Bordered = false,
                ToolTip = tip,
                ContentTintColor = Theme.Text,
                ImageScaling = NSImageScale.ProportionallyDown,
                Title = string.Empty,
            };

            if (image is not null)
            {
                b.Image = image;

                // Configured on the button rather than baked into the image, so the glyph
                // re-renders at the right weight when the button's state or tint changes.
                b.SymbolConfiguration =
                    NSImageSymbolConfiguration.Create((nfloat)size, (double)NSFontWeight.Regular);
            }
            else
            {
                // A missing symbol should not produce an invisible, unclickable button.
                b.Title = "?";
                b.Font = SystemFont(size);
            }

            b.SetButtonType(NSButtonType.MomentaryChange);
            b.Activated += handler;
            return b;
        }

        // ---------------------------------------------------------------- events

        /// <summary>
        /// Every one of these arrives off the main thread — progress from the engine's timer,
        /// the rest from whatever task advanced the queue. AppKit must be touched on the main
        /// thread only, so each hops before doing anything.
        ///
        /// <see cref="NSObject.BeginInvokeOnMainThread"/> and not the blocking form: blocking
        /// the main thread on macOS is how the self-test deadlocked, and a progress event
        /// fires four times a second.
        /// </summary>
        private void Subscribe()
        {
            _playback.TrackChanged += (_, _) => BeginInvokeOnMainThread(Refresh);
            _playback.PlayingStateChanged += (_, _) => BeginInvokeOnMainThread(RefreshPlayGlyph);
            _playback.ModesChanged += (_, _) => BeginInvokeOnMainThread(RefreshModes);
            _playback.ProgressUpdated += (_, e) => BeginInvokeOnMainThread(() => RefreshProgress(e));
            _playback.PlaybackFailed += (_, m) => BeginInvokeOnMainThread(() => _status.StringValue = m);
        }

        public void Refresh()
        {
            var track = _playback.CurrentTrack;

            _title.StringValue = track?.Title ?? "Nothing playing";
            _artist.StringValue = track?.SearchSubtitle ?? string.Empty;
            _total.StringValue = track?.DurationText ?? "00:00";

            if (track is null)
            {
                _elapsed.StringValue = "00:00";
                _seek.DoubleValue = 0;
            }

            RefreshPlayGlyph();
            RefreshModes();
        }

                private void RefreshPlayGlyph() => SetSymbol(_playPause, _playback.IsPlaying ? "pause.fill" : "play.fill", 16);

        private static void SetSymbol(NSButton button, string symbol, double size)
        {
            var image = NSImage.GetSystemSymbol(symbol, button.ToolTip ?? symbol);
            if (image is null) return;

            button.Image = image;
            button.SymbolConfiguration =
                NSImageSymbolConfiguration.Create((nfloat)size, (double)NSFontWeight.Regular);
        }

        private void RefreshModes()
        {
            _shuffle.ContentTintColor = _playback.Shuffle ? Theme.Accent : Theme.Subtext;

            _loop.ContentTintColor = _playback.Loop == LoopMode.Off ? Theme.Subtext : Theme.Accent;
            SetSymbol(_loop, _playback.Loop == LoopMode.Track ? "repeat.1" : "repeat", 12);
            _loop.ToolTip = $"Loop: {_playback.Loop}";
        }

        private void RefreshProgress(AudioProgressEventArgs e)
        {
            _elapsed.StringValue = Format(e.CurrentTime);
            if (e.TotalTime > TimeSpan.Zero) _total.StringValue = Format(e.TotalTime);

            if (!_scrubbing) _seek.DoubleValue = e.ProgressPercentage;
        }

        private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

        // ---------------------------------------------------------------- actions

        private void OnPlayPause(object? s, EventArgs e) => Fire(_playback.TogglePlayPauseAsync());
        private void OnNext(object? s, EventArgs e) => Fire(_playback.NextAsync());
        private void OnPrevious(object? s, EventArgs e) => Fire(_playback.PreviousAsync());

        private void OnShuffle(object? s, EventArgs e)
        {
            _playback.ToggleShuffle();
            RefreshModes();
        }

        private void OnLoop(object? s, EventArgs e)
        {
            _playback.CycleLoop();
            RefreshModes();
        }

        private void OnSeekChanged(object? s, EventArgs e)
        {
            // NSSlider is continuous, so this fires throughout the drag. Seek on every tick
            // rather than only on mouse-up: it matches the Windows behaviour and AVPlayer
            // coalesces seeks issued faster than it can service them.
            _scrubbing = true;
            _playback.SeekPercent(_seek.DoubleValue);
            _scrubbing = false;
        }

        /// <summary>
        /// Transport actions are fire-and-forget from a click. Nothing on the main thread may
        /// wait on them — that is the deadlock the self-test found — and a failure surfaces
        /// through PlaybackFailed rather than through this task.
        /// </summary>
        private static void Fire(Task work) =>
            _ = work.ContinueWith(t => System.Diagnostics.Debug.WriteLine($"[Applet] {t.Exception}"),
                TaskContinuationOptions.OnlyOnFaulted);
    }
}
