using AppKit;
using CoreGraphics;
using Yinyue.Models;

namespace Yinyue.UI
{
    /// <summary>
    /// One surface in the overlay's vertical stack — the search bar, the results, the queue.
    /// Each is PanelWidth wide and carries the applet's background, border and radius, so it
    /// reads as an extension of the applet rather than as a separate popup.
    ///
    /// <b>A view, not a window, and that is what makes Liquid Glass possible.</b> These were
    /// separate child windows, which followed the applet for free and cost nothing until
    /// glass arrived. A glass view samples and refracts whatever is behind it, and
    /// <c>NSGlassEffectContainerView</c> — Apple's mechanism for making adjacent glass merge
    /// into one material — groups *sibling views*. It cannot span windows. So separate
    /// windows meant every panel was its own material sampling its own backdrop, and the
    /// stack read as several different surfaces sitting next to each other.
    ///
    /// As views inside one window they merge, and the things the child-window arrangement
    /// bought come free anyway: they move with the applet because they are inside it, and
    /// focus is an ordinary responder chain rather than a negotiation between windows.
    /// </summary>
    public class OverlaySection : NSView
    {
        private readonly OverlayConfig _config;

        protected OverlaySection(OverlayConfig config, double height)
            : base(new CGRect(0, 0, OverlayMetrics.PanelWidth, height))
        {
            _config = config;

            WantsLayer = true;

            var layer = Layer!;
            layer.CornerRadius = (nfloat)OverlayMetrics.RootCornerRadius;
            layer.BorderWidth = (nfloat)OverlayMetrics.RootBorderThickness;
            layer.BorderColor = Theme.Surface0.CGColor;
            layer.MasksToBounds = true;

            ApplyBackgroundOpacity();
        }

        private NSView? _glass;

        /// <summary>
        /// What the stack should add and position — this section, or the glass shape wrapped
        /// around it.
        ///
        /// Each section gets its OWN glass view rather than sharing one behind the stack.
        /// Sharing made the whole stack a single unbroken sheet, which lost the separation
        /// between the search bar and the applet that the design depends on. The shapes are
        /// grouped by an NSGlassEffectContainerView instead, so they still sample together
        /// and read as one material.
        /// </summary>
        public NSView Mounted => _glass ??= BuildGlass();

        private NSView BuildGlass()
        {
            if (!_config.LiquidGlass || !GlassEffect.IsAvailable) return this;

            return GlassEffect.WrapAndTint(this, OverlayMetrics.RootCornerRadius, TintColour);
        }

        private NSColor TintColour =>
            Theme.Base.ColorWithAlphaComponent((nfloat)_config.BackgroundOpacity);

        /// <summary>
        /// Whether this section is part of the stack right now. Hidden rather than removed,
        /// so its contents survive and the layout only has to skip it.
        /// </summary>
        public bool Shown
        {
            get => !Hidden;
            set => Hidden = !value;
        }

        /// <summary>The area inside the border and padding, where content goes.</summary>
        protected CGRect ContentArea
        {
            get
            {
                double inset = OverlayMetrics.RootPadding + OverlayMetrics.RootBorderThickness;
                return new CGRect(inset, inset,
                    OverlayMetrics.PanelWidth - inset * 2,
                    Frame.Height - inset * 2);
            }
        }

        /// <summary>
        /// Catppuccin Base at the configured strength.
        ///
        /// With glass on, the section's own fill steps aside and the colour goes to the
        /// material instead — the glass is one view behind the whole stack, so a section that
        /// painted its own background would punch an opaque hole through it.
        /// </summary>
        public void ApplyBackgroundOpacity()
        {
            if (Layer is not { } layer) return;

            bool glass = _config.LiquidGlass && GlassEffect.IsAvailable;

            // With glass the colour goes to the material rather than over it; the section's
            // own fill would sit in front and hide it.
            layer.BackgroundColor = glass ? NSColor.Clear.CGColor : TintColour.CGColor;

            // The border stays either way. Each section is its own surface — the separation
            // between the search bar and the applet is part of the design, not an artefact of
            // them having been separate windows.
            layer.BorderWidth = (nfloat)OverlayMetrics.RootBorderThickness;

            if (glass && _glass is { } wrapper) GlassEffect.Tint(wrapper, TintColour);
        }

        /// <summary>
        /// Grows or shrinks the section. The owner re-lays the stack afterwards, since every
        /// section above this one has to move.
        /// </summary>
        protected void SetSectionHeight(double height)
        {
            Frame = new CGRect(Frame.X, Frame.Y, OverlayMetrics.PanelWidth, height);
            HeightChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Raised when the section resizes, so the stack can re-lay itself.</summary>
        public event EventHandler? HeightChanged;

        /// <summary>
        /// Input anywhere in the stack counts as using the overlay. Reported upward so the
        /// applet's auto-hide countdown restarts — the countdown belongs to the window, but
        /// the input usually lands on a section inside it.
        /// </summary>
        public event EventHandler? Interacted;

        protected void ReportInteraction() => Interacted?.Invoke(this, EventArgs.Empty);
    }
}
