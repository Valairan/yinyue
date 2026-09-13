using AppKit;
using CoreGraphics;
using Foundation;
using Yinyue.Models;

namespace Yinyue.UI
{
    /// <summary>Which reserved row a toast occupies. See <see cref="ToastPanel"/>.</summary>
    public enum ToastRole
    {
        /// <summary>Volume, loop, shuffle, track changes.</summary>
        Message,

        /// <summary>The filling dial shown while a tap/hold gesture is held.</summary>
        Hold,
    }

    /// <summary>
    /// A small transient readout for changes made by global shortcuts while the overlay is
    /// hidden — volume, loop mode, shuffle, and the track a skip landed on.
    ///
    /// Requirements, all load-bearing and all carried over from the Windows toast:
    ///
    /// · <b>Never takes focus.</b> `NonactivatingPanel` and ordered in without activating.
    ///   This is the whole reason it exists rather than briefly showing the overlay, which
    ///   would steal focus from whatever the user is typing into.
    /// · <b>Hide, never close.</b> Holding a volume key fires several times a second;
    ///   recreating the window each time would churn native resources.
    /// · <b>Repeat calls only swap the text.</b> They must not re-run the entrance or
    ///   reposition — re-running it on every call is what made rapid volume steps strobe.
    /// · <b>Two instances, not one.</b> A track change can land while a hold is in progress,
    ///   and one window cannot occupy two rows at once. <see cref="ToastRole"/> decides which.
    /// · <b>A fade-out in progress is reversible</b>, so a toast raised mid-fade is not
    ///   hidden a moment later by the old animation finishing.
    /// </summary>
    /// <remarks>
    /// <b>Not a child window of the applet</b>, and that is the load-bearing part. A child
    /// window is hidden whenever its parent is, and a toast's entire purpose is to appear
    /// while the overlay is <i>down</i> — a volume key pressed inside another app, a track
    /// change during a skip. Making it a child both showed it when nothing had been said and
    /// would have hidden it at the only moment it matters. It positions itself against the
    /// anchor instead, exactly as the Windows toast does.
    /// </remarks>
    public sealed class ToastSection : OverlaySection
    {

        private readonly OverlayConfig _config;
        private readonly NSImageView _glyph;
        private readonly NSTextField _text;
        private readonly NSProgressIndicator _level;
        private readonly HoldDialView _dial;

        /// <summary>
        /// Invalidates a pending fade completion. Without it, a toast raised while an earlier
        /// fade is running gets hidden when that older animation finishes.
        /// </summary>
        private int _generation;

        public ToastSection(OverlayConfig config, ToastRole role)
            : base(config, OverlayMetrics.ToastRowHeight)
        {
            _config = config;
            Role = role;

            var area = ContentArea;

            // The mark's cell is RESERVED, exactly as it is on Windows: one fixed 22-unit
            // column that holds either the glyph or the dial, never both, and keeps its width
            // when it holds neither. The text column starts at a constant x and therefore
            // never moves — a readout that shifted as the dial appeared would be worse than
            // one with no dial at all.
            double markY = area.Y + (area.Height - MarkSize) / 2;

            _glyph = new NSImageView
            {
                Frame = new CGRect(area.X, markY, MarkSize, MarkSize),
                ImageScaling = NSImageScale.ProportionallyDown,
            };
            AddSubview(_glyph);

            _text = new NSTextField
            {
                Frame = TextFrame(withLevel: false),
                Editable = false,
                Selectable = false,
                Bezeled = false,
                DrawsBackground = false,
                TextColor = Theme.Text,
                Font = NSFont.SystemFontOfSize(12) ?? NSFont.SystemFontOfSize(NSFont.SystemFontSize)!,
                LineBreakMode = NSLineBreakMode.TruncatingTail,
                StringValue = string.Empty,
                Cell = { UsesSingleLineMode = true },
            };
            AddSubview(_text);

            _level = new NSProgressIndicator(new CGRect(area.X, LevelY, area.Width, LevelHeight))
            {
                Style = NSProgressIndicatorStyle.Bar,
                Indeterminate = false,
                MinValue = 0,
                MaxValue = 1,
                Hidden = true,
            };
            AddSubview(_level);

            // The same reserved cell the glyph uses, so swapping one for the other moves
            // nothing.
            _dial = new HoldDialView(new CGRect(area.X, markY, MarkSize, MarkSize))
            {
                Hidden = true,
            };
            AddSubview(_dial);

            Shown = false;
        }

        public ToastRole Role { get; }

        /// <summary>For the suite: where the text actually is, so drift can be asserted.</summary>
        public CGRect TextFrameForTest => _text.Frame;

        /// <summary>For the suite: the vertical centre the text should sit on.</summary>
        public double ContentMidY => ContentArea.Y + ContentArea.Height / 2;

        /// <summary>
        /// Shows or updates the toast. A repeat call swaps the text and nothing else — no
        /// re-entrance, no reposition, no opacity reset.
        /// </summary>
        public void Show(string message, string? icon = null, double? level = null)
        {
            _dial.Hidden = true;

            _glyph.Hidden = icon is null;
            if (icon is not null) _glyph.Image = Icon.Make(icon, MarkSize, Theme.Text);

            // The text moves only when the level bar appears, and then only because the pair
            // is centred as a group — it never moves for the mark, which has its own cell.
            _text.Frame = TextFrame(withLevel: level is not null);
            _text.StringValue = message;

            _level.Hidden = level is null;
            if (level is { } value) _level.DoubleValue = Math.Clamp(value, 0, 1);

            // A fade may be running from a previous toast; claim the window back from it.
            _generation++;
            AlphaValue = 1;

            // Positioned and faded in only on the way in. A repeat call must not reposition
            // or re-run the entrance -- doing that on every call is what made rapid volume
            // steps strobe on Windows.
            if (!Shown) Enter();
        }

        /// <summary>
        /// Shows the filling dial. There is no ordinary auto-hide while the keys are down —
        /// but a dial that stops being updated must not stay up for good, so the dismissal
        /// runs as a watchdog at a short interval, restarted by every update. On Windows the
        /// hold-to-activate path once succeeded without telling the toast, and the dial
        /// stayed on screen permanently.
        /// </summary>
        public void ShowHold(string label, double fraction)
        {
            _dial.Hidden = false;
            _dial.Fraction = fraction;

            _glyph.Hidden = true;
            _level.Hidden = true;
            _text.Frame = TextFrame(withLevel: false);
            _text.StringValue = label;

            _holding = true;
            _generation++;
            AlphaValue = 1;

            if (!Shown) Enter();

            // No ordinary auto-hide while the keys are down, but a dial that stops being
            // updated must not stay up for good, so this runs as a watchdog at a short
            // interval and every update restarts it.
            Dismiss(HoldStale);
        }

        /// <summary>How long a dial may go without an update before it is assumed stale.</summary>
        private static readonly TimeSpan HoldStale = TimeSpan.FromMilliseconds(600);

        /// <summary>How long an ordinary toast stays up. Matches the Windows toast.</summary>
        public static readonly TimeSpan VisibleFor = TimeSpan.FromMilliseconds(1400);

        private bool _holding;

        /// <summary>
        /// Ends the dial without hiding the toast. The action that follows usually raises its
        /// own toast, which replaces this one; a cancelled hold just fades out after the
        /// ordinary interval. Hiding instantly would snatch the readout away at the very
        /// moment the user let go to see what happened.
        /// </summary>
        public void EndHold()
        {
            if (!_holding) return;

            _holding = false;
            _dial.Hidden = true;

            Dismiss(VisibleFor);
        }

        /// <summary>
        /// Fades in. The row's position is the stack's business now, not the toast's — it is
        /// a section in the same window as everything else, which is what makes the glass one
        /// material rather than one per window.
        /// </summary>
        private void Enter()
        {
            Shown = true;
            Appeared?.Invoke(this, EventArgs.Empty);

            double seconds = _config.Animations ? _config.AnimationMilliseconds / 1000.0 : 0;

            if (seconds <= 0)
            {
                AlphaValue = 1;
                return;
            }

            AlphaValue = 0;
            Fade(to: 1, seconds, onDone: null);
        }

        /// <summary>Raised when the row appears or goes, so the stack can re-lay itself.</summary>
        public event EventHandler? Appeared;

        /// <summary>
        /// Drives the fade from a timer rather than through AppKit's <c>Animator</c> proxy.
        ///
        /// The proxy marshals the window back into managed code to apply the animated value,
        /// and a subclass without a NativeHandle constructor cannot be reconstructed that way
        /// — it throws from inside AppKit, where nothing of ours can catch it. A stepped
        /// alpha over a 120 ms fade is indistinguishable and cannot fail like that.
        /// </summary>
        private void Fade(double to, double seconds, Action? onDone)
        {
            _fade?.Invalidate();

            double from = AlphaValue;
            var start = DateTime.UtcNow;

            _fade = NSTimer.CreateRepeatingScheduledTimer(1.0 / 60, timer =>
            {
                double t = Math.Clamp((DateTime.UtcNow - start).TotalSeconds / seconds, 0, 1);
                AlphaValue = (nfloat)(from + (to - from) * t);

                if (t < 1) return;

                timer.Invalidate();
                _fade = null;
                onDone?.Invoke();
            });
        }

        private NSTimer? _fade;

        private void Hide()
        {
            Shown = false;
            Appeared?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The reserved cell for the glyph or the dial, and the gap after it.</summary>
        private const double MarkSize = 22;
        private const double MarkGap = 10;

        private const double TextHeight = 18;
        private const double LevelHeight = 4;
        private const double LevelGap = 4;

        /// <summary>
        /// The text, vertically centred.
        ///
        /// With a level bar the two are centred as a pair, which is what the Windows toast
        /// does — a centred StackPanel holding the text row and then the bar. Without one the
        /// text alone is centred. The horizontal position never changes: the mark's column is
        /// reserved whether or not anything is in it.
        /// </summary>
        private CGRect TextFrame(bool withLevel)
        {
            var area = ContentArea;
            double left = area.X + MarkSize + MarkGap;

            double groupHeight = withLevel ? TextHeight + LevelGap + LevelHeight : TextHeight;
            double top = area.Y + (area.Height + groupHeight) / 2;

            return new CGRect(left, top - TextHeight, area.X + area.Width - left, TextHeight);
        }

        /// <summary>Directly under the text when both are centred as a pair.</summary>
        private double LevelY
        {
            get
            {
                var area = ContentArea;
                double groupHeight = TextHeight + LevelGap + LevelHeight;

                return area.Y + (area.Height - groupHeight) / 2;
            }
        }

        /// <summary>
        /// Fades out and hides. Hides rather than closes, and a later Show cancels this by
        /// bumping the generation.
        /// </summary>
        public void Dismiss(TimeSpan after)
        {
            int generation = _generation;

            NSTimer.CreateScheduledTimer(after.TotalSeconds, _ =>
            {
                if (generation != _generation) return;   // a newer toast took over

                double seconds = _config.Animations
                    ? _config.AnimationMilliseconds / 1000.0
                    : 0;

                if (seconds <= 0)
                {
                    Hide();
                    return;
                }

                Fade(to: 0, seconds, onDone: () =>
                {
                    if (generation == _generation) Hide();
                });
            });
        }
    }
}
