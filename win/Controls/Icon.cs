using System.Windows;
using System.Windows.Media;

// System.Windows.Controls is deliberately not imported: the project enables WinForms for the
// tray icon, whose implicit usings make the bare name Control ambiguous.

namespace Yinyue.Controls
{
    /// <summary>
    /// One Lucide icon, drawn as a stroke in the inherited <see cref="Control.Foreground"/>.
    ///
    /// <see cref="Kind"/> names a geometry in Icons.xaml ("Play", "Repeat1", "HeartShuffle");
    /// the template scales the icon's 24-unit box to <see cref="Size"/>, so the stroke scales
    /// with it and every icon shares one visual weight regardless of how much of its box it
    /// fills. That is the property text glyphs never had: a play triangle and a pause pair
    /// measured differently, and emoji came from a different font again.
    ///
    /// Because the colour is the inherited Foreground, the states that used to recolour a
    /// text glyph — the red favourite, the accent loop, the hover tint — work unchanged when
    /// the icon sits inside a button. <see cref="Filled"/> additionally fills the shape with
    /// the same brush, for the "is a favourite" heart.
    ///
    /// An unknown kind draws nothing rather than throwing, so a typo cannot take a window
    /// down; the suite checks every kind the app names against the generated dictionary.
    /// </summary>
    public class Icon : System.Windows.Controls.Control
    {
        static Icon()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(typeof(Icon)));
        }

        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            nameof(Kind), typeof(string), typeof(Icon),
            new PropertyMetadata(string.Empty, (d, _) => ((Icon)d).Resolve()));

        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            nameof(Data), typeof(Geometry), typeof(Icon));

        public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
            nameof(Size), typeof(double), typeof(Icon), new PropertyMetadata(18.0));

        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            nameof(StrokeThickness), typeof(double), typeof(Icon), new PropertyMetadata(2.0));

        public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
            nameof(Filled), typeof(bool), typeof(Icon), new PropertyMetadata(false));

        /// <summary>The icon's name in PascalCase, as the resource key after "Icon.".</summary>
        public string Kind
        {
            get => (string)GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        /// <summary>The resolved geometry. Set from <see cref="Kind"/>; null draws nothing.</summary>
        public Geometry? Data
        {
            get => (Geometry?)GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        /// <summary>Rendered width and height in DIPs. The 24-unit box scales to this.</summary>
        public double Size
        {
            get => (double)GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }

        /// <summary>Stroke in 24-unit box units. Lucide's own is 2.</summary>
        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        /// <summary>Fill the shape with the Foreground as well as stroking it.</summary>
        public bool Filled
        {
            get => (bool)GetValue(FilledProperty);
            set => SetValue(FilledProperty, value);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            Resolve();
        }

        private void Resolve()
        {
            string kind = Kind;
            if (string.IsNullOrEmpty(kind))
            {
                Data = null;
                return;
            }

            string key = "Icon." + kind;

            // TryFindResource walks up to the application's resources, which is where the
            // merged Icons.xaml lives; the explicit fallback covers an element that is not in
            // a tree yet, as in the suite.
            Data = TryFindResource(key) as Geometry
                   ?? System.Windows.Application.Current?.TryFindResource(key) as Geometry;

            if (Data == null)
                System.Diagnostics.Debug.WriteLine($"[Icon] No geometry for '{kind}'.");
        }
    }
}
