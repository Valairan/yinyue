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
        private readonly NSTextField _text;
        private readonly NSProgressIndicator _level;

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
            ContentView!.AddSubview(_text);

            _level = new NSProgressIndicator(new CGRect(area.X, area.Y + 4, area.Width, 4))
            {
                Style = NSProgressIndicatorStyle.Bar,
                Indeterminate = false,
                MinValue = 0,
                MaxValue = 1,
                Hidden = true,
            };
            ContentView.AddSubview(_level);

            OrderOut(null);
        }

        public ToastRole Role { get; }

        /// <summary>
        /// Shows or updates the toast. A repeat call swaps the text and nothing else — no
        /// re-entrance, no reposition, no opacity reset.
        /// </summary>
        public void Show(string message, double? level = null)
        {
            _text.StringValue = message;

            _level.Hidden = level is null;
            if (level is { } value) _level.DoubleValue = Math.Clamp(value, 0, 1);

            // A fade may be running from a previous toast; claim the window back from it.
            _generation++;
            AlphaValue = 1;

            if (!IsVisible)
            {
                // Positioned only on the way in. A repeat call must not reposition: the
                // window is re-placed solely when its height changes, which nothing here
                // does, and re-running the entrance on every call is what made rapid volume
                // steps strobe on Windows.
                OverlayPositioner.PositionToastRow(this, _config, (int)Role);
                OrderFrontRegardless();
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
                    OrderOut(null);
                    return;
                }

                NSAnimationContext.RunAnimation(context =>
                {
                    context.Duration = seconds;
                    ((NSWindow)Animator).AlphaValue = 0;
                }, () =>
                {
                    if (generation == _generation) OrderOut(null);
                });
            });
        }
    }
}
