using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Forms;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Places the overlay against a monitor work area.
    ///
    /// Works in physical pixels through SetWindowPos rather than WPF's Left/Top. Under
    /// Per-Monitor-V2 DPI the mapping between device-independent units and a secondary
    /// monitor running a different scale factor is a reliable source of off-by-a-scale
    /// bugs; Screen.WorkingArea is already in physical pixels, so this needs no conversion
    /// of the target rectangle and mixed-DPI setups work by construction.
    /// </summary>
    public static class OverlayPositioner
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("Shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType,
            out uint dpiX, out uint dpiY);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const int MDT_EFFECTIVE_DPI = 0;

        /// <summary>
        /// Recomputes and applies the overlay position. Call on every show rather than
        /// caching: resolution changes, DPI changes, docking, and taskbar moves all
        /// invalidate the result, and recomputing costs microseconds.
        /// </summary>
        public static void Position(Window window, OverlayConfig config) =>
            Position(window, config, 0);

        /// <param name="liftDip">
        /// Shifts the window up from where the anchor would put it, to keep space free
        /// beneath. Clamped like everything else, so it can never push it off screen.
        /// </param>
        public static void Position(Window window, OverlayConfig config, double liftDip)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            Screen screen = ResolveScreen(config);

            // WorkingArea, never Bounds — Bounds includes the taskbar, so a bottom-right
            // overlay would be drawn underneath it.
            var work = screen.WorkingArea;
            double scale = GetScaleFor(screen);

            double dipWidth = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            double dipHeight = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

            int width = (int)Math.Round(dipWidth * scale);
            int height = (int)Math.Round(dipHeight * scale);
            int marginX = (int)Math.Round(config.MarginX * scale);
            int marginY = (int)Math.Round(config.MarginY * scale);

            int x = AnchorX(config.Anchor, work, width, marginX);
            int y = AnchorY(config.Anchor, work, height, marginY)
                    - (int)Math.Round(liftDip * scale);

            // Clamp so a large margin or an undersized work area can never push the
            // overlay off screen entirely.
            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
            y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>Gap between the overlay and anything stacked with it, in DIPs.</summary>
        public const double SideGap = 6;

        /// <summary>Height of one toast row, in DIPs. Fixed so the stack never shifts.</summary>
        public const double ToastRowHeight = 60;

        /// <summary>
        /// How many toast rows sit below the overlay: the title toast, and the hold dial
        /// beneath it.
        /// </summary>
        public const int ToastRows = 2;

        /// <summary>
        /// Space kept free beneath the overlay for the toast rows.
        ///
        /// Reserved permanently rather than made room for on demand: a panel that moves out
        /// of the way when a toast appears would shift under the user mid-interaction, and
        /// the toasts arrive unbidden — on a track change, or a volume key pressed in another
        /// app. A fixed slot for everything is worth the pixels.
        /// </summary>
        public static double ReservedForToasts => ToastRows * (ToastRowHeight + SideGap);

        /// <summary>
        /// Anchors the overlay, leaving the toast rows their space beneath it.
        ///
        /// Only bottom anchors need lifting. Anywhere else there is already room below, so
        /// the overlay stays where the anchor puts it and the rows simply follow.
        /// </summary>
        public static void PositionApplet(Window window, OverlayConfig config) =>
            Position(window, config, IsBottomAnchored(config.Anchor) ? ReservedForToasts : 0);

        /// <summary>
        /// Places a toast row beneath the overlay's slot, left edges aligned.
        /// </summary>
        /// <param name="row">0 is immediately below the overlay; 1 is below that.</param>
        public static void PositionToastRow(Window window, OverlayConfig config,
            int row, double appletDipWidth, double appletDipHeight)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            Screen screen = ResolveScreen(config);
            var work = screen.WorkingArea;
            double scale = GetScaleFor(screen);

            int width = (int)Math.Round(
                (window.ActualWidth > 0 ? window.ActualWidth : window.Width) * scale);
            int height = (int)Math.Round(
                (window.ActualHeight > 0 ? window.ActualHeight : window.Height) * scale);

            int appletWidth = (int)Math.Round(appletDipWidth * scale);
            int appletHeight = (int)Math.Round(appletDipHeight * scale);

            int marginX = (int)Math.Round(config.MarginX * scale);
            int marginY = (int)Math.Round(config.MarginY * scale);
            int gap = (int)Math.Round(SideGap * scale);
            int rowPitch = (int)Math.Round((ToastRowHeight + SideGap) * scale);

            // The overlay's own slot, including the lift that reserved this space.
            int lift = IsBottomAnchored(config.Anchor)
                ? (int)Math.Round(ReservedForToasts * scale)
                : 0;

            int appletX = Math.Clamp(
                AnchorX(config.Anchor, work, appletWidth, marginX),
                work.Left, Math.Max(work.Left, work.Right - appletWidth));

            int appletY = Math.Clamp(
                AnchorY(config.Anchor, work, appletHeight, marginY) - lift,
                work.Top, Math.Max(work.Top, work.Bottom - appletHeight));

            int x = appletX;
            int y = appletY + appletHeight + gap + row * rowPitch;

            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
            y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private static bool IsBottomAnchored(OverlayAnchor anchor) =>
            anchor is OverlayAnchor.BottomLeft or OverlayAnchor.BottomCenter
                or OverlayAnchor.BottomRight;

        private static int AnchorX(OverlayAnchor anchor, Rectangle work, int width, int marginX) =>
            anchor switch
            {
                OverlayAnchor.TopLeft or OverlayAnchor.BottomLeft => work.Left + marginX,
                OverlayAnchor.TopRight or OverlayAnchor.BottomRight => work.Right - width - marginX,
                _ => work.Left + (work.Width - width) / 2
            };

        private static int AnchorY(OverlayAnchor anchor, Rectangle work, int height, int marginY) =>
            anchor switch
            {
                OverlayAnchor.TopLeft or OverlayAnchor.TopCenter or OverlayAnchor.TopRight
                    => work.Top + marginY,
                OverlayAnchor.BottomLeft or OverlayAnchor.BottomCenter or OverlayAnchor.BottomRight
                    => work.Bottom - height - marginY,
                _ => work.Top + (work.Height - height) / 2
            };

        private static Screen ResolveScreen(OverlayConfig config)
        {
            switch (config.Monitor)
            {
                case MonitorSelection.Cursor:
                    return Screen.FromPoint(Control.MousePosition);

                case MonitorSelection.Active:
                    IntPtr foreground = GetForegroundWindow();
                    return foreground != IntPtr.Zero
                        ? Screen.FromHandle(foreground)
                        : Screen.PrimaryScreen ?? Screen.AllScreens[0];

                case MonitorSelection.Specific:
                    // People unplug docks. A missing monitor must not mean an overlay
                    // drawn off-screen, so fall back to primary.
                    var match = Screen.AllScreens.FirstOrDefault(s =>
                        string.Equals(s.DeviceName, config.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
                    return match ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];

                default:
                    return Screen.PrimaryScreen ?? Screen.AllScreens[0];
            }
        }

        private static double GetScaleFor(Screen screen)
        {
            try
            {
                var point = new POINT
                {
                    X = screen.Bounds.Left + screen.Bounds.Width / 2,
                    Y = screen.Bounds.Top + screen.Bounds.Height / 2
                };

                IntPtr monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero &&
                    GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 &&
                    dpiX > 0)
                {
                    return dpiX / 96.0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Overlay] DPI query failed: {ex.Message}");
            }

            return 1.0;
        }
    }
}
