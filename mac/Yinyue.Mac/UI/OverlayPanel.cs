using AppKit;
using CoreGraphics;
using Yinyue.Models;

namespace Yinyue.UI
{
    /// <summary>
    /// The summon overlay — product requirement 1. A frameless panel that appears on a global
    /// hotkey, is acted on, and dismisses. No Dock icon and no app-switcher entry; those come
    /// from LSUIElement in Info.plist, which is the AppKit equivalent of ShowInTaskbar="False"
    /// plus the tray icon.
    ///
    /// Three traits are load-bearing and each is easy to lose:
    ///
    /// · <b>NonactivatingPanel</b> — the panel can take key input without activating the
    ///   application. Without it, summoning the overlay pulls focus out of whatever the user
    ///   was typing into, which defeats the point of a summon overlay entirely.
    /// · <b>Floating level</b> — it stays above ordinary windows. `Floating` rather than
    ///   anything higher: `ScreenSaver` level would cover the menu bar and full-screen apps,
    ///   which is antisocial for a panel that lives all day.
    /// · <b>HidesOnDeactivate = false</b> — AppKit would otherwise hide the panel the moment
    ///   the app stops being active, and for an LSUIElement app that is almost immediately.
    ///
    /// Hide, never close. The window is summoned constantly, and recreating it each time
    /// would churn native resources for no gain — the same rule the Windows overlay follows.
    /// </summary>
    public sealed class OverlayPanel : NSPanel
    {
        private readonly OverlayConfig _config;

        /// <summary>The applet fills the panel; the stack above it comes later.</summary>
        public NSView? Content { get; private set; }

        public OverlayPanel(OverlayConfig config, double height)
            : base(new CGRect(0, 0, Theme.PanelWidth, height),
                   // Borderless for the frameless look; Nonactivating so it never steals
                   // focus. Utility keeps it out of the window menu.
                   NSWindowStyle.Borderless | NSWindowStyle.NonactivatingPanel | NSWindowStyle.Utility,
                   NSBackingStore.Buffered,
                   deferCreation: false)
        {
            _config = config;

            Level = NSWindowLevel.Floating;
            HidesOnDeactivate = false;

            // The rounded corners come from the content view's layer, so the window itself
            // must not paint a square background behind them.
            IsOpaque = false;
            BackgroundColor = NSColor.Clear;
            HasShadow = true;

            // Keep it off every Space-switching and screenshot surface a resident panel has
            // no business appearing in, and let it follow the user between Spaces.
            CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces
                               | NSWindowCollectionBehavior.FullScreenAuxiliary
                               | NSWindowCollectionBehavior.IgnoresCycle;

            // A borderless window is not movable by its background by default, and should not
            // be: the anchor decides where it sits, not the pointer.
            MovableByWindowBackground = false;

            ContentView = BuildRoot(height);
        }

        /// <summary>
        /// Places the applet inside the rounded root. Added rather than replacing the root,
        /// so the corner radius and border survive.
        /// </summary>
        public void SetContent(NSView view)
        {
            Content?.RemoveFromSuperview();
            Content = view;
            ContentView!.AddSubview(view);
        }

        /// <summary>
        /// A borderless NSWindow refuses key status unless it says otherwise, which would
        /// leave the search box unable to receive a single keystroke.
        /// </summary>
        public override bool CanBecomeKeyWindow => true;

        /// <summary>
        /// False deliberately, and it is the counterpart to NonactivatingPanel: becoming main
        /// is what would activate the app and pull focus from the user's real work.
        /// </summary>
        public override bool CanBecomeMainWindow => false;

        private NSView BuildRoot(double height)
        {
            var root = new NSView(new CGRect(0, 0, Theme.PanelWidth, height))
            {
                WantsLayer = true,
            };

            var layer = root.Layer!;
            layer.BackgroundColor = Theme.Base.WithAlpha(_config.BackgroundOpacity).CGColor;
            layer.CornerRadius = (nfloat)Theme.CornerRadius;
            layer.BorderWidth = 1;
            layer.BorderColor = Theme.Surface0.CGColor;

            // Corners have to be clipped for children to respect the radius.
            layer.MasksToBounds = true;

            return root;
        }

        /// <summary>
        /// Summons the panel at its anchor. OrderFrontRegardless rather than MakeKeyAndOrderFront
        /// so the app is not activated; the panel still accepts keys because it can become key
        /// without becoming main.
        /// </summary>
        public void ShowOverlay()
        {
            OverlayPositioner.PositionApplet(this, _config);
            OrderFrontRegardless();
            MakeKeyWindow();
        }

        public void HideOverlay() => OrderOut(this);

        public bool IsShown => IsVisible;

        public void ToggleOverlay()
        {
            if (IsShown) HideOverlay();
            else ShowOverlay();
        }
    }

    internal static class ColorAlpha
    {
        public static NSColor WithAlpha(this NSColor c, double alpha) =>
            c.ColorWithAlphaComponent((nfloat)alpha);
    }
}
