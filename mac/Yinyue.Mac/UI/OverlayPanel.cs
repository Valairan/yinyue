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

            // The whole stack lives in this one window. Each section carries its own glass
            // shape, and a container groups them so they sample together and read as one
            // material while staying visibly separate surfaces — the separation between the
            // search bar and the applet is part of the design.
            //
            // Spacing zero: shapes closer than the spacing merge into a single blob, which is
            // exactly what the stack must not do.
            _stackRoot = new NSView(new CGRect(0, 0, OverlayMetrics.PanelWidth, height));

            ContentView = _config.LiquidGlass
                ? GlassEffect.Container(_stackRoot, spacing: 0) ?? _stackRoot
                : _stackRoot;

            ApplyBackgroundOpacity();
        }

        private readonly NSView _stackRoot;

        /// <summary>Where the sections live. The applet is one of them.</summary>
        public NSView StackRoot => _stackRoot;

        private NSColor TintColour =>
            Theme.Base.ColorWithAlphaComponent((nfloat)_config.BackgroundOpacity);

        /// <summary>
        /// Resizes the window to fit the stack and re-anchors it.
        ///
        /// The sections are laid out from the bottom up, so the applet keeps its place and
        /// the window grows upward as panels open — which is what the anchor already expects
        /// for a bottom-anchored overlay.
        /// </summary>
        public void SetStackHeight(double height)
        {
            var frame = Frame;
            SetFrame(new CGRect(frame.X, frame.Y, OverlayMetrics.PanelWidth, height), true);

            _stackRoot.Frame = new CGRect(0, 0, OverlayMetrics.PanelWidth, height);

            if (ContentView is { } view && !ReferenceEquals(view, _stackRoot))
                view.Frame = new CGRect(0, 0, OverlayMetrics.PanelWidth, height);

            OverlayPositioner.PositionApplet(this, _config, height);
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


        /// <summary>
        /// Summons the panel at its anchor. OrderFrontRegardless rather than MakeKeyAndOrderFront
        /// so the app is not activated; the panel still accepts keys because it can become key
        /// without becoming main.
        /// </summary>
        /// <summary>
        /// Re-reads the tint. Applied on every config change rather than cached, so the
        /// setting takes effect without a restart.
        ///
        /// With glass on, the colour goes to the material rather than over it — the glass is
        /// one view behind the whole stack, so anything painting its own background would
        /// punch an opaque hole through it. BackgroundOpacity is the tint strength either
        /// way, so the control keeps one meaning: 1.0 is an opaque Catppuccin panel with or
        /// without glass, and lower values let progressively more material through.
        /// </summary>
        public void ApplyBackgroundOpacity()
        {
            if (_config.LiquidGlass && GlassEffect.IsAvailable)
            {
                if (ContentView is { } wrapper) GlassEffect.Tint(wrapper, TintColour);
                return;
            }

            // Without glass each section paints its own background and supplies the rounded
            // corners; the window's own root stays clear.
            _stackRoot.WantsLayer = true;
            if (_stackRoot.Layer is { } layer) layer.BackgroundColor = NSColor.Clear.CGColor;
        }

        /// <summary>Raised after the panel is placed and shown, so the stack can follow it.</summary>
        public event EventHandler? Shown;

        public event EventHandler? Hidden;

        /// <summary>A key that reached the overlay rather than the search box.</summary>
        public event EventHandler<NSEvent>? KeyReceived;

        /// <summary>
        /// Puts the window on screen without summoning the overlay or taking focus.
        ///
        /// A toast lives in this window now, and has to be visible while the overlay itself
        /// is dismissed — so "dismissed" means the applet and the panels above it are hidden,
        /// not that the window is gone. It stays up for as long as anything in it is showing.
        /// </summary>
        public void EnsureVisible()
        {
            if (!IsVisible) OrderFrontRegardless();
        }

        /// <summary>Orders the window out once nothing in it is left to see.</summary>
        public void HideIfEmpty(Func<bool> anythingShowing)
        {
            if (!anythingShowing()) OrderOut(this);
        }

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
