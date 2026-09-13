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
            : base(new CGRect(0, 0, OverlayMetrics.PanelWidth, height),
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
            var root = new NSView(new CGRect(0, 0, OverlayMetrics.PanelWidth, height))
            {
                WantsLayer = true,
            };

            var layer = root.Layer!;
            layer.CornerRadius = (nfloat)OverlayMetrics.RootCornerRadius;
            layer.BorderWidth = (nfloat)OverlayMetrics.RootBorderThickness;
            layer.BorderColor = Theme.Surface0.CGColor;

            // Corners have to be clipped for children to respect the radius.
            layer.MasksToBounds = true;

            ApplyBackgroundOpacity(root);
            return root;
        }

        /// <summary>
        /// Summons the panel at its anchor. OrderFrontRegardless rather than MakeKeyAndOrderFront
        /// so the app is not activated; the panel still accepts keys because it can become key
        /// without becoming main.
        /// </summary>
        /// <summary>
        /// Re-reads the tint. Applied on every config change rather than cached, so the
        /// setting takes effect without a restart — the same rule the Windows overlay
        /// follows, and the reason it is a panel tint rather than window opacity: fading the
        /// window would take the text and the artwork with it.
        /// </summary>
        public void ApplyBackgroundOpacity(NSView? root = null)
        {
            var view = root ?? ContentView;
            if (view?.Layer is not { } layer) return;

            layer.BackgroundColor = Theme.Base.WithAlpha(_config.BackgroundOpacity).CGColor;
        }

        /// <summary>Raised after the panel is placed and shown, so the stack can follow it.</summary>
        public event EventHandler? Shown;

        public event EventHandler? Hidden;

        /// <summary>A key that reached the overlay rather than the search box.</summary>
        public event EventHandler<NSEvent>? KeyReceived;

        public void ShowOverlay()
        {
            OverlayPositioner.PositionApplet(this, _config);
            OrderFrontRegardless();
            MakeKeyWindow();
            Shown?.Invoke(this, EventArgs.Empty);
            RestartAutoHide();
        }

        // ------------------------------------------------------------------ auto-hide

        private NSTimer? _autoHide;

        /// <summary>
        /// Restarts the idle countdown. Called from every interaction, so the overlay only
        /// disappears when it is genuinely left alone.
        ///
        /// <b>Nothing suspends it.</b> Windows keeps the overlay up while a search or queue
        /// is open, on the argument that reading is not idling — but an open panel with no
        /// input for ten seconds is still an overlay nobody is using, and leaving it up was
        /// the more annoying half of the trade. Idle means idle here; any key, click, scroll
        /// or pointer movement anywhere in the stack restarts the clock.
        ///
        /// This is <b>not</b> the sleep timer. They are the only two timers here and are
        /// easily confused: this hides a window after a few seconds and any input restarts
        /// it; the sleep timer pauses playback after a long absolute interval and nothing
        /// resets it.
        /// </summary>
        public void RestartAutoHide()
        {
            _autoHide?.Invalidate();
            _autoHide = null;

            if (!_config.AutoHide || !IsVisible) return;

            _autoHide = NSTimer.CreateScheduledTimer(_config.AutoHideSeconds, _ => HideOverlay());
        }

        /// <summary>
        /// Any key, click or pointer movement counts as interaction. Routed through here
        /// rather than scattered through the handlers so nothing can forget to restart it.
        /// </summary>
        public override void SendEvent(NSEvent theEvent)
        {
            switch (theEvent.Type)
            {
                case NSEventType.KeyDown:
                case NSEventType.LeftMouseDown:
                case NSEventType.RightMouseDown:
                case NSEventType.MouseMoved:
                case NSEventType.ScrollWheel:
                    RestartAutoHide();
                    break;
            }

            base.SendEvent(theEvent);
        }

        /// <summary>
        /// Dismisses when the user goes to another application, if they asked for that.
        ///
        /// Watches the <b>application</b> deactivating, not this window resigning key status.
        /// Key status moves between our own windows constantly — the search box takes it the
        /// instant the search shortcut is pressed — so hanging this off ResignKeyWindow made
        /// the overlay vanish the moment it was summoned into search. Application
        /// deactivation cannot fire for an internal focus move, so the distinction is
        /// structural rather than a guard that has to be got right.
        /// </summary>
        public void WatchForFocusLoss()
        {
            _deactivated = NSNotificationCenter.DefaultCenter.AddObserver(
                NSApplication.DidResignActiveNotification, _ =>
                {
                    if (_config.HideOnFocusLoss && IsVisible) HideOverlay();
                });
        }

        private NSObject? _deactivated;

        public void HideOverlay()
        {
            _autoHide?.Invalidate();
            _autoHide = null;

            Hidden?.Invoke(this, EventArgs.Empty);
            OrderOut(this);
        }

        /// <summary>
        /// Every key that lands on the panel. Handled here rather than by a responder chain
        /// because the stack's panels are separate windows and a routed event cannot cross
        /// between them.
        /// </summary>
        public override void KeyDown(NSEvent theEvent) => KeyReceived?.Invoke(this, theEvent);

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
