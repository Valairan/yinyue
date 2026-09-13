using AppKit;
using CoreGraphics;

namespace Yinyue.UI
{
    /// <summary>
    /// A filling arc, drawn while a tap/hold gesture is held.
    ///
    /// An arc rather than a countdown, matching Windows: it reads at a glance, and it does
    /// not resize the row as digits change — a toast that changed width mid-hold would move
    /// the thing being looked at.
    /// </summary>
    public sealed class HoldDialView : NSView
    {
        private const double Radius = 10;
        private const double Thickness = 2.5;

        private double _fraction;

        public HoldDialView(CGRect frame) : base(frame) { }

        public double Fraction
        {
            get => _fraction;
            set
            {
                _fraction = Math.Clamp(value, 0, 1);
                NeedsDisplay = true;
            }
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            var centre = new CGPoint(Bounds.Width / 2, Bounds.Height / 2);

            // The unfilled ring, so the dial reads as a dial before it has filled at all.
            var track = new NSBezierPath { LineWidth = (nfloat)Thickness };
            track.AppendPathWithArc(centre, (nfloat)Radius, 0, 360);
            Theme.Surface1.Set();
            track.Stroke();

            if (_fraction <= 0) return;

            // Clockwise from twelve o'clock, which is how a filling gauge is read.
            // AppKit measures angles anticlockwise from three o'clock, hence the inversion.
            const double start = 90;
            double end = start - 360 * _fraction;

            var arc = new NSBezierPath
            {
                LineWidth = (nfloat)Thickness,
                LineCapStyle = NSLineCapStyle.Round,
            };

            arc.AppendPathWithArc(centre, (nfloat)Radius, (nfloat)start, (nfloat)end, true);
            Theme.Accent.Set();
            arc.Stroke();
        }
    }
}
