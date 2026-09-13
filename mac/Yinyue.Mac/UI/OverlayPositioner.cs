using AppKit;
using CoreGraphics;
using Yinyue.Models;

namespace Yinyue.UI
{
    /// <summary>
    /// Anchors the overlay to the work area of a chosen screen — product requirement 2, and
    /// the same seven anchors, margins and monitor rules the Windows app honours through
    /// OverlayConfig.
    ///
    /// <b>The coordinate system is the trap.</b> Win32 measures from the top-left with y
    /// growing downward; AppKit measures from the bottom-left with y growing <i>upward</i>.
    /// Porting the Windows arithmetic as written silently swaps every top anchor with its
    /// bottom counterpart — and it looks correct on a single screen with a centred window,
    /// which is exactly when it would be tested.
    ///
    /// DPI needs no handling at all, unlike Win32. AppKit works in points throughout and the
    /// backing scale is applied below this layer, so Retina and mixed-density setups are
    /// correct by construction. The Windows positioner needs per-monitor DPI queries; this
    /// one does not, and that asymmetry is not an oversight.
    /// </summary>
    public static class OverlayPositioner
    {
        // The stack's measurements come from OverlayMetrics in Core, like every other
        // number in the overlay. They were briefly redeclared here, which is exactly the
        // drift that file exists to prevent.
        public static double ReservedForToasts => OverlayMetrics.ReservedForToasts;

        /// <summary>
        /// Places the overlay itself, lifting it clear of the reserved toast rows when it is
        /// bottom-anchored. Only bottom anchors need the lift: anywhere else there is already
        /// room below, so the rows simply follow beneath.
        /// </summary>
        public static void PositionApplet(NSWindow window, OverlayConfig config) =>
            Position(window, config, IsBottomAnchored(config.Anchor) ? ReservedForToasts : 0);

        /// <summary>
        /// Places a window of a known height. The overlay now holds the whole stack, so it
        /// grows and shrinks as panels open — and a bottom-anchored overlay has to stay put
        /// at the bottom while it does, which means re-anchoring on every size change.
        /// </summary>
        public static void PositionApplet(NSWindow window, OverlayConfig config, double height)
        {
            var work = TargetScreen(config).VisibleFrame;
            double lift = IsBottomAnchored(config.Anchor) ? ReservedForToasts : 0;

            double x = HorizontalOrigin(config.Anchor, work, OverlayMetrics.PanelWidth, config.MarginX);
            double y = VerticalOrigin(config.Anchor, work, height, config.MarginY, lift);

            window.SetFrameOrigin(new CGPoint(x, y));
        }

        /// <summary>
        /// Places a toast in its reserved row beneath the applet's slot. <paramref name="row"/>
        /// is 0 for the message and 1 for the hold dial, so the two can both be on screen
        /// without colliding and neither can land on the applet.
        ///
        /// Computed from the applet's <i>slot</i> rather than from the applet window, because
        /// a toast's whole job is to appear while the overlay is hidden — there may be no
        /// applet on screen to measure.
        /// </summary>
        public static void PositionToastRow(NSWindow window, OverlayConfig config, int row)
        {
            var screen = TargetScreen(config);
            var work = screen.VisibleFrame;

            double appletHeight = OverlayMetrics.AppletHeight;
            double lift = IsBottomAnchored(config.Anchor) ? OverlayMetrics.ReservedForToasts : 0;

            double appletX = HorizontalOrigin(config.Anchor, work, OverlayMetrics.PanelWidth, config.MarginX);
            double appletY = VerticalOrigin(config.Anchor, work, appletHeight, config.MarginY, lift);

            // Rows descend from just under the applet. AppKit's y grows upward, so "below"
            // means subtracting.
            double pitch = OverlayMetrics.ToastRowHeight + OverlayMetrics.SideGap;
            double y = appletY - OverlayMetrics.SideGap - OverlayMetrics.ToastRowHeight - row * pitch;

            window.SetFrameOrigin(new CGPoint(appletX, y));
        }

        public static void Position(NSWindow window, OverlayConfig config, double bottomInset)
        {
            var screen = TargetScreen(config);

            // visibleFrame, not frame: it already excludes the menu bar and the Dock, which
            // is what "never overlapping the taskbar" means here.
            var work = screen.VisibleFrame;
            var size = window.Frame.Size;

            double x = HorizontalOrigin(config.Anchor, work, size.Width, config.MarginX);
            double y = VerticalOrigin(config.Anchor, work, size.Height, config.MarginY, bottomInset);

            window.SetFrameOrigin(new CGPoint(x, y));
        }

        public static bool IsBottomAnchored(OverlayAnchor anchor) =>
            anchor is OverlayAnchor.BottomLeft or OverlayAnchor.BottomCenter
                or OverlayAnchor.BottomRight;

        private static double HorizontalOrigin(OverlayAnchor anchor, CGRect work, double width, double margin) =>
            anchor switch
            {
                OverlayAnchor.TopLeft or OverlayAnchor.BottomLeft => work.Left() + margin,
                OverlayAnchor.TopRight or OverlayAnchor.BottomRight => work.Right() - width - margin,
                _ => work.Left() + (work.Width - width) / 2,
            };

        /// <summary>
        /// Returns the origin of the window's <i>bottom</i> edge, because that is what AppKit
        /// means by a frame origin. A top anchor is therefore the far edge minus the height,
        /// which is the inverse of the Windows expression.
        /// </summary>
        private static double VerticalOrigin(
            OverlayAnchor anchor, CGRect work, double height, double margin, double bottomInset) =>
            anchor switch
            {
                OverlayAnchor.TopLeft or OverlayAnchor.TopCenter or OverlayAnchor.TopRight
                    => work.Top() - height - margin,

                OverlayAnchor.BottomLeft or OverlayAnchor.BottomCenter or OverlayAnchor.BottomRight
                    => work.Bottom() + margin + bottomInset,

                _ => work.Bottom() + (work.Height - height) / 2,
            };

        /// <summary>
        /// Cursor and Active follow the user between displays; Specific pins to a screen by
        /// its localized name. A pinned screen that has been unplugged falls back to the main
        /// one rather than placing the overlay off-screen.
        /// </summary>
        private static NSScreen TargetScreen(OverlayConfig config)
        {
            var screens = NSScreen.Screens;
            if (screens.Length == 0) return NSScreen.MainScreen;

            switch (config.Monitor)
            {
                case MonitorSelection.Cursor:
                {
                    var mouse = NSEvent.CurrentMouseLocation;
                    foreach (var s in screens)
                        if (s.Frame.Contains(mouse)) return s;
                    return NSScreen.MainScreen;
                }

                case MonitorSelection.Active:
                    // MainScreen is AppKit's "screen with the active window", despite the name.
                    return NSScreen.MainScreen;

                case MonitorSelection.Specific:
                {
                    if (!string.IsNullOrWhiteSpace(config.MonitorDeviceName))
                        foreach (var s in screens)
                            if (string.Equals(s.LocalizedName, config.MonitorDeviceName,
                                    StringComparison.OrdinalIgnoreCase))
                                return s;
                    return NSScreen.MainScreen;
                }

                default:
                    // Primary is the screen carrying the menu bar, which is screens[0].
                    return screens[0];
            }
        }
    }

    /// <summary>
    /// CGRect edges, named so the anchor arithmetic reads the same way it does on Windows
    /// without hiding that Top is the larger y here rather than the smaller.
    /// </summary>
    internal static class RectEdges
    {
        public static double Left(this CGRect r) => r.X;
        public static double Right(this CGRect r) => r.X + r.Width;
        public static double Bottom(this CGRect r) => r.Y;
        public static double Top(this CGRect r) => r.Y + r.Height;
    }
}
