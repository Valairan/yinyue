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
    public sealed class ToastPanel : StackedPanel
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

        public ToastPanel(OverlayConfig config, ToastRole role)
            : base(config, OverlayMetrics.ToastRowHeight)
        {
            _config = config;
            Role = role;

            var area = ContentArea;

            // Every toast carries a mark, as on Windows: volume, loop, shuffle, the track,
            // the sleep timer. It says at a glance which of them this is, before the text has
            // been read at all.
            _glyph = new NSImageView
            {
                Frame = new CGRect(area.X, area.Y + area.Height - 22, 18, 18),
                ImageScaling = NSImageScale.ProportionallyDown,
            };
            ContentView!.AddSubview(_glyph);

            _text = new NSTextField
            {
                Frame = new CGRect(area.X, area.Y + area.Height - 20, area.Width, 18),
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
            ContentView.AddSubview(_text);

            _level = new NSProgressIndicator(new CGRect(area.X, area.Y + 4, area.Width, 4))
            {
                Style = NSProgressIndicatorStyle.Bar,
                Indeterminate = false,
                MinValue = 0,
                MaxValue = 1,
                Hidden = true,
            };
            ContentView.AddSubview(_level);

            _dial = new HoldDialView(new CGRect(area.X, area.Y + (area.Height - 26) / 2, 26, 26))
            {
                Hidden = true,
            };
            ContentView.AddSubview(_dial);

            OrderOut(null);
        }

        public ToastRole Role { get; }

        /// <summary>
        /// Shows or updates the toast. A repeat call swaps the text and nothing else — no
        /// re-entrance, no reposition, no opacity reset.
        /// </summary>
        public void Show(string message, string? icon = null, double? level = null)
        {
            _dial.Hidden = true;

            _glyph.Hidden = icon is null;
            if (icon is not null) _glyph.Image = Icon.Make(icon, 18, Theme.Text);

            _text.Frame = TextFrame(withDial: false, withGlyph: icon is not null);
            _text.StringValue = message;

            _level.Hidden = level is null;
            if (level is { } value) _level.DoubleValue = Math.Clamp(value, 0, 1);

            // A fade may be running from a previous toast; claim the window back from it.
            _generation++;
            AlphaValue = 1;

            // Positioned and faded in only on the way in. A repeat call must not reposition
            // or re-run the entrance -- doing that on every call is what made rapid volume
            // steps strobe on Windows.
            if (!IsVisible) Enter();
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
            _text.Frame = TextFrame(withDial: true, withGlyph: false);
            _text.StringValue = label;

            _holding = true;
            _generation++;
            AlphaValue = 1;

            if (!IsVisible) Enter();

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
            _text.Frame = TextFrame(withDial: false, withGlyph: !_glyph.Hidden);

            Dismiss(VisibleFor);
        }

        private void Enter()
        {
            OverlayPositioner.PositionToastRow(this, _config, (int)Role);

            double seconds = _config.Animations ? _config.AnimationMilliseconds / 1000.0 : 0;

            if (seconds <= 0)
            {
                AlphaValue = 1;
                OrderFrontRegardless();
                return;
            }

            AlphaValue = 0;
            OrderFrontRegardless();
            Fade(to: 1, seconds, onDone: null);
        }

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

        private CGRect TextFrame(bool withDial, bool withGlyph)
        {
            var area = ContentArea;
            double left = withDial || withGlyph ? area.X + 26 : area.X;

            return new CGRect(left, area.Y + area.Height - 20, area.X + area.Width - left, 18);
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
                    OrderOut(null);
                    return;
                }

                Fade(to: 0, seconds, onDone: () =>
                {
                    if (generation == _generation) OrderOut(null);
                });
            });
        }
    }
}
