using AppKit;
using CoreGraphics;
using Yinyue.Models;
using Yinyue.Services;
using Metrics = Yinyue.UI.OverlayMetrics;

namespace Yinyue.UI
{
    /// <summary>
    /// The applet, laid out to the same measurements as the Windows overlay.
    ///
    /// Two columns: a 130-unit artwork square on the left, and a content column carrying four
    /// rows — header, metadata, seek, transport. Every number comes from
    /// <see cref="OverlayMetrics"/> in Core, which both apps read, because the two shells
    /// share no UI code and nothing else could keep them in step.
    ///
    /// AppKit has no Grid, so the rows are placed arithmetically. The arithmetic reproduces
    /// what WPF's Grid does with <c>Auto, *, Auto, Auto</c>: the fixed rows take their natural
    /// heights and the starred row absorbs the remainder. Done explicitly here so the result
    /// can be asserted rather than eyeballed.
    ///
    /// It is a <i>view</i> of playback and owns none of it — every value comes from a
    /// <see cref="PlaybackService"/> event, the same rule MainWindow follows.
    /// </summary>
    public sealed class AppletView : NSView
    {
        private readonly PlaybackService _playback;

        private readonly NSView _artFrame = new();
        private readonly NSImageView _art = new();

        private readonly NSTextField _status;
        private readonly NSTextField _title;
        private readonly NSTextField _artist;
        private readonly NSTextField _elapsed;
        private readonly NSTextField _total;
        private readonly NSSlider _seek = new();

        // Header, left to right, matching MainWindow's HeaderButtonsPanel.
        private readonly NSButton _queue;
        private readonly NSButton _offline;
        private readonly NSButton _shuffleFavorites;
        private readonly NSButton _settings;

        // Transport, left to right, matching MainWindow's row 3.
        private readonly NSButton _previous;
        private readonly NSButton _playPause;
        private readonly NSButton _next;
        private readonly NSButton _shuffle;
        private readonly NSButton _loop;
        private readonly NSButton _favorite;

        /// <summary>
        /// True while the seek handle is held. Progress events arrive four times a second and
        /// would otherwise drag the handle back out from under the pointer.
        /// </summary>
        private bool _scrubbing;

        public event EventHandler? SettingsRequested;
        public event EventHandler? QueueRequested;

        public AppletView(PlaybackService playback)
            : base(new CGRect(0, 0, Metrics.PanelWidth, Metrics.AppletHeight))
        {
            _playback = playback;
            WantsLayer = true;

            _status = Label(Metrics.StatusFontSize, Theme.Subtext);
            _title = Label(Metrics.TitleFontSize, Theme.Text, bold: true);
            _artist = Label(Metrics.ArtistFontSize, Theme.Subtext);
            _elapsed = Label(Metrics.TimeFontSize, Theme.Subtext);
            _total = Label(Metrics.TimeFontSize, Theme.Subtext);

            _queue = Button(Icons.List, "Queue", (_, _) => QueueRequested?.Invoke(this, EventArgs.Empty));
            _offline = Button(Icons.Cloud, "Offline mode", OnOffline);
            _shuffleFavorites = Button(Icons.HeartShuffle, "Shuffle all favourites", OnShuffleFavorites);
            _settings = Button(Icons.Cog, "Settings", (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));

            _previous = Button(Icons.SkipBack, "Previous", OnPrevious);
            _playPause = Button(Icons.Play, "Play / Pause", OnPlayPause);
            _next = Button(Icons.SkipForward, "Next", OnNext);
            _shuffle = Button(Icons.Shuffle, "Shuffle off", OnShuffle);
            _loop = Button(Icons.RepeatOff, "Loop off", OnLoop);
            _favorite = Button(Icons.HeartPlus, "Toggle Favorite", OnFavorite);

            Layout();
            Subscribe();
            Refresh();
        }

        // ------------------------------------------------------------------ layout

        /// <summary>
        /// Frames every child. Named the rows the XAML names them so the two can be read
        /// side by side.
        /// </summary>
        private new void Layout()
        {
            double inset = Metrics.RootPadding + Metrics.RootBorderThickness;
            double contentTop = Metrics.AppletHeight - inset;   // AppKit y grows upward

            // --- artwork column: a square centred vertically in the content height
            double artY = inset + (Metrics.ContentHeight - Metrics.ArtSize) / 2;
            _artFrame.Frame = new CGRect(inset, artY, Metrics.ArtSize, Metrics.ArtSize);
            _artFrame.WantsLayer = true;
            _artFrame.Layer!.CornerRadius = (nfloat)Metrics.ArtCornerRadius;
            _artFrame.Layer.MasksToBounds = true;
            _artFrame.Layer.BackgroundColor = Theme.Mantle.CGColor;

            _art.Frame = new CGRect(0, 0, Metrics.ArtSize, Metrics.ArtSize);
            _art.ImageScaling = NSImageScale.ProportionallyUpOrDown;
            _artFrame.AddSubview(_art);
            AddSubview(_artFrame);

            // --- content column
            double colX = inset + Metrics.ArtSize + Metrics.ArtToContentGap;
            double colW = Metrics.PanelWidth - inset - colX;

            double headerH = ButtonHeight;
            double metaH = LineHeight(Metrics.TitleFontSize) + LineHeight(Metrics.ArtistFontSize)
                         + Metrics.MetadataTopMargin + Metrics.MetadataBottomMargin;
            double seekH = Metrics.SeekBarHeight + Metrics.SeekRowMargin * 2;
            double transportH = ButtonHeight + Metrics.TransportTopMargin;

            // Row 0 — header: status on the left, four buttons hard right.
            double y = contentTop - headerH;
            LayoutHeader(colX, y, colW, headerH);

            // Row 3 — transport sits on the bottom edge; the starred row takes what is left.
            double transportY = inset;
            LayoutTransport(colX, transportY, colW);

            // Row 2 — seek, directly above transport.
            double seekY = transportY + transportH;
            LayoutSeek(colX, seekY, colW);

            // Row 1 — metadata fills the gap between header and seek, bottom-aligned to the
            // seek row exactly as WPF's starred row does with VerticalAlignment="Center".
            double metaTop = y;
            double metaBottom = seekY + seekH;
            double metaY = metaBottom + (metaTop - metaBottom - metaH) / 2;
            LayoutMetadata(colX, metaY, colW, metaH);
        }

        private void LayoutHeader(double x, double y, double width, double height)
        {
            NSButton[] buttons = { _queue, _offline, _shuffleFavorites, _settings };

            double buttonsW = buttons.Length * Metrics.ButtonMinWidth
                            + (buttons.Length - 1) * Metrics.HeaderButtonMargin * 2;

            // The status line takes what the buttons leave. The buttons are ALWAYS visible:
            // collapsing them on Windows once hid the settings cog, and since the overlay
            // opens into search when nothing is playing, that made settings unreachable on a
            // fresh install — so no library could ever be added, so nothing could ever play.
            double statusW = Math.Max(0, width - buttonsW - Metrics.StatusRightGap);
            _status.Frame = new CGRect(x, y + (height - LineHeight(Metrics.StatusFontSize)) / 2,
                                       statusW, LineHeight(Metrics.StatusFontSize));
            AddSubview(_status);

            double bx = x + width - buttonsW;
            foreach (var b in buttons)
            {
                b.Frame = new CGRect(bx, y, Metrics.ButtonMinWidth, height);
                AddSubview(b);
                bx += Metrics.ButtonMinWidth + Metrics.HeaderButtonMargin * 2;
            }
        }

        private void LayoutMetadata(double x, double y, double width, double height)
        {
            double titleH = LineHeight(Metrics.TitleFontSize);
            double artistH = LineHeight(Metrics.ArtistFontSize);

            _artist.Frame = new CGRect(x, y + Metrics.MetadataBottomMargin, width, artistH);
            _title.Frame = new CGRect(x, y + Metrics.MetadataBottomMargin + artistH, width, titleH);

            AddSubview(_title);
            AddSubview(_artist);
        }

        private void LayoutSeek(double x, double y, double width)
        {
            double labelH = LineHeight(Metrics.TimeFontSize);
            double labelW = 34;
            double barY = y + Metrics.SeekRowMargin;

            _elapsed.Frame = new CGRect(x, barY + (Metrics.SeekBarHeight - labelH) / 2, labelW, labelH);
            _elapsed.Alignment = NSTextAlignment.Left;
            AddSubview(_elapsed);

            _total.Frame = new CGRect(x + width - labelW,
                                      barY + (Metrics.SeekBarHeight - labelH) / 2, labelW, labelH);
            _total.Alignment = NSTextAlignment.Right;
            AddSubview(_total);

            double barX = x + labelW + Metrics.SeekLabelGap;
            double barW = width - labelW * 2 - Metrics.SeekLabelGap * 2;

            _seek.Frame = new CGRect(barX, barY, barW, Metrics.SeekBarHeight);
            _seek.MinValue = 0;
            _seek.MaxValue = 100;
            _seek.Continuous = true;
            _seek.ControlSize = NSControlSize.Mini;
            _seek.Activated += OnSeekChanged;
            AddSubview(_seek);
        }

        /// <summary>
        /// Six buttons, centred as one group: previous, play/pause, next, then shuffle, loop
        /// and favourite after an extra gap. The gap separates transport from modes without
        /// needing a divider.
        /// </summary>
        private void LayoutTransport(double x, double y, double width)
        {
            NSButton[] buttons = { _previous, _playPause, _next, _shuffle, _loop, _favorite };
            const int modeGroupStart = 3;

            double margin = Metrics.TransportButtonMargin;
            double total = buttons.Length * (Metrics.ButtonMinWidth + margin * 2) + Metrics.ModeGroupGap;

            double bx = x + (width - total) / 2;

            for (int i = 0; i < buttons.Length; i++)
            {
                if (i == modeGroupStart) bx += Metrics.ModeGroupGap;

                bx += margin;
                buttons[i].Frame = new CGRect(bx, y + Metrics.TransportTopMargin,
                                              Metrics.ButtonMinWidth, ButtonHeight);
                AddSubview(buttons[i]);
                bx += Metrics.ButtonMinWidth + margin;
            }
        }

        /// <summary>Icon box plus the template's padding on each side, as MediaBtnStyle gives it.</summary>
        private static double ButtonHeight => Metrics.IconSize + Metrics.ButtonPadding * 2;

        private static double LineHeight(double fontSize) => Math.Ceiling(fontSize * 1.35);

        // ------------------------------------------------------------------ children

        private static NSTextField Label(double size, NSColor colour, bool bold = false) => new()
        {
            Editable = false,
            Selectable = false,
            Bezeled = false,
            DrawsBackground = false,
            TextColor = colour,
            Font = SystemFont(size, bold),
            LineBreakMode = NSLineBreakMode.TruncatingTail,
            StringValue = string.Empty,
            Cell = { UsesSingleLineMode = true },
        };

        /// <summary>
        /// System fonts are always present but the binding types them as nullable. Asserted
        /// here rather than scattering null-forgiving operators through the layout.
        /// </summary>
        private static NSFont SystemFont(double size, bool bold = false) =>
            (bold ? NSFont.BoldSystemFontOfSize((nfloat)size) : NSFont.SystemFontOfSize((nfloat)size))
            ?? NSFont.SystemFontOfSize(NSFont.SystemFontSize)!;

        /// <summary>
        /// The shared Lucide marks from Common/Icons, at the same size Windows draws them.
        /// SF Symbols would be the shortcut and are macOS-only and drawn to Apple's metrics,
        /// so the two overlays would not match.
        /// </summary>
        private NSButton Button(string pathData, string tip, EventHandler handler)
        {
            var b = new NSButton
            {
                Bordered = false,
                ToolTip = tip,
                Title = string.Empty,
                ImageScaling = NSImageScale.ProportionallyDown,
                Image = Icon.Make(pathData, Metrics.IconSize, Theme.Text),
            };

            b.AccessibilityTitle = tip;   // the mark would otherwise be announced as empty
            b.SetButtonType(NSButtonType.MomentaryChange);
            b.Activated += handler;
            return b;
        }

        private static void SetIcon(NSButton button, string pathData, NSColor colour) =>
            button.Image = Icon.Make(pathData, Metrics.IconSize, colour);

        // ------------------------------------------------------------------ state

        /// <summary>
        /// Every one of these arrives off the main thread — progress from the engine's timer,
        /// the rest from whatever task advanced the queue — so each hops before touching
        /// AppKit. The non-blocking form deliberately: blocking the main thread is what
        /// deadlocked the self-test, and progress fires four times a second.
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

            _status.StringValue = "Yinyue";
            _title.StringValue = track?.Title ?? "No Track Selected";
            _artist.StringValue = track?.SearchSubtitle ?? "Unknown Artist";
            _total.StringValue = track?.DurationText ?? "00:00";

            if (track is null)
            {
                _elapsed.StringValue = "00:00";
                _seek.DoubleValue = 0;
            }

            RefreshPlayGlyph();
            RefreshModes();
        }

        private void RefreshPlayGlyph() =>
            SetIcon(_playPause, _playback.IsPlaying ? Icons.Pause : Icons.Play, Theme.Text);

        private void RefreshModes()
        {
            SetIcon(_shuffle, Icons.Shuffle, _playback.Shuffle ? Theme.Accent : Theme.Text);
            _shuffle.ToolTip = _playback.Shuffle ? "Shuffle on" : "Shuffle off";

            // repeat-off is its own mark rather than a dimmed repeat, so the data changes
            // here and not only the colour.
            string loopIcon = _playback.Loop switch
            {
                LoopMode.Track => Icons.Repeat1,
                LoopMode.Queue => Icons.Repeat,
                _ => Icons.RepeatOff,
            };

            SetIcon(_loop, loopIcon, _playback.Loop == LoopMode.Off ? Theme.Text : Theme.Accent);
            _loop.ToolTip = $"Loop {_playback.Loop}".ToLowerInvariant();
        }

        private void RefreshProgress(AudioProgressEventArgs e)
        {
            _elapsed.StringValue = Format(e.CurrentTime);
            if (e.TotalTime > TimeSpan.Zero) _total.StringValue = Format(e.TotalTime);

            if (!_scrubbing) _seek.DoubleValue = e.ProgressPercentage;
        }

        private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

        // ------------------------------------------------------------------ actions

        private void OnPlayPause(object? s, EventArgs e) => Fire(_playback.TogglePlayPauseAsync());
        private void OnNext(object? s, EventArgs e) => Fire(_playback.NextAsync());
        private void OnPrevious(object? s, EventArgs e) => Fire(_playback.PreviousAsync());

        private void OnShuffle(object? s, EventArgs e) { _playback.ToggleShuffle(); RefreshModes(); }
        private void OnLoop(object? s, EventArgs e) { _playback.CycleLoop(); RefreshModes(); }

        // Not wired yet: favourites, offline and shuffle-favourites need the library surface
        // the overlay does not have until search exists.
        private void OnFavorite(object? s, EventArgs e) { }
        private void OnOffline(object? s, EventArgs e) { }
        private void OnShuffleFavorites(object? s, EventArgs e) { }

        private void OnSeekChanged(object? s, EventArgs e)
        {
            _scrubbing = true;
            _playback.SeekPercent(_seek.DoubleValue);
            _scrubbing = false;
        }

        /// <summary>
        /// Fire-and-forget from a click. Nothing on the main thread may wait on these — that
        /// is the deadlock the self-test found — and failures surface through PlaybackFailed.
        /// </summary>
        private static void Fire(Task work) =>
            _ = work.ContinueWith(t => System.Diagnostics.Debug.WriteLine($"[Applet] {t.Exception}"),
                TaskContinuationOptions.OnlyOnFaulted);
    }
}
