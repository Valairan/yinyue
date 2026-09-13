using System.Globalization;
using AppKit;
using CoreGraphics;

namespace Yinyue.UI
{
    /// <summary>
    /// Draws the shared Lucide marks — the AppKit counterpart to win/Controls/Icon.cs, and
    /// the reason the two apps show the same icons rather than merely similar ones.
    ///
    /// Both platforms are generated from the SVGs in Common/Icons by one script, so an icon
    /// added there appears on both or on neither. AppKit has no SVG renderer and SF Symbols
    /// were the tempting shortcut here — they are also macOS-only and drawn to Apple's
    /// metrics, which would have made the Mac overlay quietly different from the Windows one.
    ///
    /// Lucide's geometry is fixed: a 24-unit box, a 2-unit stroke, round caps and joins, no
    /// fill. That is applied here once rather than carried in the path data, exactly as the
    /// WPF control does it.
    /// </summary>
    public static class Icon
    {
        public const double ViewBox = 24.0;
        public const double StrokeUnits = 2.0;

        /// <summary>
        /// An icon at <paramref name="size"/> points, stroked in <paramref name="color"/>.
        ///
        /// Not a template image. A template would let AppKit tint it automatically, but it
        /// would tint to the system's colours rather than to Catppuccin, and the palette is
        /// the thing the two apps share. The menu-bar mark is the one exception — see
        /// <see cref="Template"/>.
        /// </summary>
        public static NSImage Make(string pathData, double size, NSColor color)
        {
            var image = NSImage.ImageWithSize(new CGSize(size, size), false, _ =>
            {
                color.Set();
                Build(pathData, size).Stroke();
                return true;
            });

            return image!;
        }

        /// <summary>
        /// A template image, which AppKit inverts automatically against light and dark menu
        /// bars. This is the macOS answer to the tray-light/tray-dark pair on Windows: one
        /// asset instead of two, and no need to watch for a theme change at all.
        /// </summary>
        public static NSImage Template(string pathData, double size)
        {
            var image = Make(pathData, size, NSColor.Black);
            image.Template = true;
            return image;
        }

        /// <summary>
        /// Parses the generated path data into a stroked path, scaled into the target size
        /// and flipped.
        ///
        /// <b>The flip is required, not cosmetic.</b> SVG's y grows downward and AppKit's
        /// grows upward, so a mark drawn without it is upside down — which for several of
        /// these (skip-back, repeat, the heart) is wrong in a way that reads as a different
        /// icon rather than as an obvious bug.
        ///
        /// Only M, L, C and Z appear: the generator reduces H and V to lines and converts
        /// arcs to cubics, so there is no arc arithmetic at runtime.
        /// </summary>
        private static NSBezierPath Build(string pathData, double size)
        {
            double scale = size / ViewBox;

            var path = new NSBezierPath
            {
                // Scaled with the icon, so a 12pt icon is not drawn with a 16pt icon's stroke.
                LineWidth = (nfloat)(StrokeUnits * scale),
                LineCapStyle = NSLineCapStyle.Round,
                LineJoinStyle = NSLineJoinStyle.Round,
            };

            CGPoint Map(double x, double y) =>
                new((nfloat)(x * scale), (nfloat)((ViewBox - y) * scale));

            foreach (var segment in pathData.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                char command = segment[0];
                var n = ParseNumbers(segment[1..]);

                switch (command)
                {
                    case 'M': path.MoveTo(Map(n[0], n[1])); break;
                    case 'L': path.LineTo(Map(n[0], n[1])); break;
                    case 'C': path.CurveTo(Map(n[4], n[5]), Map(n[0], n[1]), Map(n[2], n[3])); break;
                    case 'Z': path.ClosePath(); break;
                }
            }

            return path;
        }

        /// <summary>
        /// InvariantCulture is not optional: the generator writes "12.5" and a machine in a
        /// comma-decimal locale would otherwise read that as two numbers, or fail outright.
        /// </summary>
        private static double[] ParseNumbers(string text)
        {
            if (text.Length == 0) return Array.Empty<double>();

            var parts = text.Split(',');
            var values = new double[parts.Length];

            for (int i = 0; i < parts.Length; i++)
                values[i] = double.Parse(parts[i], CultureInfo.InvariantCulture);

            return values;
        }
    }
}
