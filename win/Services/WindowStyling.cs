using System;
using System.Runtime.InteropServices;

namespace Yinyue.Services
{
    /// <summary>
    /// Win32 window traits that WPF does not expose. Shared by the overlay and the toast,
    /// which need overlapping but not identical sets of them.
    /// </summary>
    public static class WindowStyling
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        /// <summary>
        /// Drops transparency on a window whose XAML asked for it, for the case where the
        /// user has turned animations off and the software-composited path is not worth
        /// paying for. Must run before the handle exists, so the constructor is the only
        /// safe place — WPF refuses the change once the window has a source.
        ///
        /// Restores an opaque background and squares off the root border, since per-pixel
        /// corners stop working; DWM rounding takes over instead.
        /// </summary>
        public static void MakeOpaque(System.Windows.Window window,
            System.Windows.Media.Brush background,
            System.Windows.Controls.Border? rootBorder)
        {
            if (!window.AllowsTransparency) return;

            window.AllowsTransparency = false;
            window.Background = background;

            if (rootBorder != null)
                rootBorder.CornerRadius = new System.Windows.CornerRadius(0);
        }

        /// <summary>
        /// Tints a panel background to the requested opacity, leaving its content fully
        /// opaque. Returns the brush so callers can assign it where they like.
        ///
        /// Only meaningful on a transparent window: against an opaque one the alpha simply
        /// composites onto the window's own background and nothing appears to change.
        /// </summary>
        public static System.Windows.Media.Brush Translucent(
            System.Windows.Media.Color color, double opacity)
        {
            byte alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);

            var brush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(alpha, color.R, color.G, color.B));

            brush.Freeze();
            return brush;
        }

        /// <summary>Keeps a window out of the taskbar and the alt-tab list.</summary>
        public static void MakeToolWindow(IntPtr hwnd) => AddExStyle(hwnd, WS_EX_TOOLWINDOW);

        /// <summary>
        /// Stops a window ever taking focus, even when shown. Essential for a toast: the
        /// point of it is feedback without interrupting whatever you are typing into.
        /// WS_EX_TRANSPARENT also lets clicks fall through to the window underneath.
        /// </summary>
        public static void MakeNonActivating(IntPtr hwnd) =>
            AddExStyle(hwnd, WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);

        /// <summary>
        /// Asks DWM to round the window, which is how a borderless window gets its corners
        /// without AllowsTransparency and the software-composited layered path that implies.
        /// Windows 11 only — elsewhere the attribute is unknown and the window stays square.
        /// </summary>
        public static void ApplyRoundedCorners(IntPtr hwnd)
        {
            try
            {
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Window] Rounded corners unavailable: {ex.Message}");
            }
        }

        private static void AddExStyle(IntPtr hwnd, int style)
        {
            if (hwnd == IntPtr.Zero) return;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | style);
        }
    }
}
