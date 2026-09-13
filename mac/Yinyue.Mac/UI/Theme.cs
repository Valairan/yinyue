using AppKit;

namespace Yinyue.UI
{
    /// <summary>
    /// Catppuccin Mocha, defined once — the AppKit counterpart to the palette in App.xaml.
    /// The two apps share no UI code, so this is the one place the values are duplicated, and
    /// it is deliberate: a shared resource file would have to be parsed by both toolkits.
    ///
    /// Same rule as on Windows applies here: <b>do not put hex literals in a view.</b> Every
    /// colour comes from this class or theming becomes a find-and-replace job.
    /// </summary>
    public static class Theme
    {
        public static readonly NSColor Crust = Hex(0x11111B);
        public static readonly NSColor Mantle = Hex(0x181825);
        public static readonly NSColor Base = Hex(0x1E1E2E);
        public static readonly NSColor Surface0 = Hex(0x313244);
        public static readonly NSColor Surface1 = Hex(0x45475A);
        public static readonly NSColor Surface2 = Hex(0x585B70);
        public static readonly NSColor Text = Hex(0xCDD6F4);
        public static readonly NSColor Subtext = Hex(0xA6ADC8);
        public static readonly NSColor Accent = Hex(0x89B4FA);
        public static readonly NSColor Success = Hex(0xA6E3A1);
        public static readonly NSColor Danger = Hex(0xF38BA8);
        public static readonly NSColor Warning = Hex(0xF9E2AF);

        // Measurements are NOT here. They live in OverlayMetrics in Core, which both apps
        // read, so the two overlays cannot drift apart. This class is colour only.

        private static NSColor Hex(int rgb) => NSColor.FromSrgb(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8) & 0xFF) / 255f,
            (rgb & 0xFF) / 255f,
            1f);
    }
}
