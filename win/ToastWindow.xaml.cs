using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

// UseWindowsForms pulls System.Drawing into scope, which has its own Point and Size.
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue
{
    /// <summary>
    /// A small transient readout for changes made by global hotkeys — loop mode, shuffle,
    /// volume — so they are not invisible while the overlay is hidden.
    ///
    /// It must never take focus. That is the whole reason it exists rather than briefly
    /// showing the overlay: feedback should not interrupt whatever you are typing into.
    /// </summary>
    public partial class ToastWindow : Window
    {
        private static readonly TimeSpan VisibleFor = TimeSpan.FromMilliseconds(1400);
        /// <summary>Read at use rather than cached, so the setting takes effect at once.</summary>
        private TimeSpan FadeDuration =>
            TimeSpan.FromMilliseconds(_config.Current.Overlay.AnimationMilliseconds);

        /// <summary>Bringing a fading toast back up should feel instant, not like a re-entry.</summary>
        private TimeSpan FadeBack => TimeSpan.FromMilliseconds(
            Math.Min(70, _config.Current.Overlay.AnimationMilliseconds));

        private readonly ConfigService _config;
        private readonly DispatcherTimer _hideTimer;

        private readonly bool _animate;

        /// <summary>Suspends the auto-hide while a hold is in progress.</summary>
        private bool _holding;

        private bool _onScreen;
        private bool _fadingOut;

        /// <summary>
        /// Invalidates the pending fade-out completion. Without it, a toast raised during a
        /// fade would be hidden a moment later by the old animation finishing.
        /// </summary>
        private int _fadeGeneration;

        /// <summary>
        /// Which reserved row a toast occupies, counting down from the overlay.
        ///
        /// Two instances rather than one that switches: a track change can land while a hold
        /// is in progress, and one window cannot be in two rows at once.
        /// </summary>
        public enum ToastRole
        {
            /// <summary>Immediately below the overlay: what is playing, volume, loop, shuffle.</summary>
            Message = 0,

            /// <summary>The bottom row, where a hold gesture fills its dial.</summary>
            Hold = 1,
        }

        public ToastRole Role { get; }

        public ToastWindow(ConfigService config, ToastRole role = ToastRole.Message)
        {
            InitializeComponent();

            _config = config;
            Role = role;

            // Before the handle exists: AllowsTransparency is fixed once a window has a source.
            _animate = config.Current.Overlay.Animations;
            if (!_animate)
            {
                WindowStyling.MakeOpaque(this,
                    (System.Windows.Media.Brush)FindResource("BaseBrush"), RootBorder);
            }

            _hideTimer = new DispatcherTimer { Interval = VisibleFor };
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer.Stop();
                FadeAway();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            // Corners come from the Border now that the window is transparent.
            WindowStyling.MakeToolWindow(hwnd);
            WindowStyling.MakeNonActivating(hwnd);

            if (_animate) Opacity = 0;
            else WindowStyling.ApplyRoundedCorners(hwnd);
        }

        /// <summary>
        /// Shows a message, replacing whatever was on screen and restarting the timer.
        /// <paramref name="level"/> adds a bar, for values like volume where a bar reads
        /// faster than a number.
        ///
        /// Repeat calls while already on screen only swap the text. They must not restart
        /// the fade or reposition the window: the volume keys fire this many times a
        /// second, and re-running the entrance each time reads as a flicker.
        /// </summary>
        /// <param name="glyph">An icon kind — "Volume2", "Music" — as Icons.xaml names them.</param>
        public void Show(string glyph, string message, double? level = null)
        {
            ApplyBackgroundOpacity();
            ShowDial(false);
            _holding = false;

            bool sizeChanged = ApplyContent(glyph, message, level);

            // Any pending fade-out completion is now stale.
            _fadeGeneration++;

            if (!_onScreen)
            {
                EnterFromHidden();
            }
            else if (_fadingOut)
            {
                // Caught mid-exit: come straight back from wherever the fade got to.
                _fadingOut = false;
                if (_animate)
                    BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(FadeBack)));
            }
            else if (sizeChanged)
            {
                // Only the level bar appearing or disappearing changes the height, and only
                // then does the anchor need recomputing.
                UpdateLayout();
                Position();
            }

            _onScreen = true;

            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void EnterFromHidden()
        {
            _fadingOut = false;

            // Show first, then measure. A window that has never been displayed has no
            // ActualHeight, and with SizeToContent its Height is NaN — positioning before
            // the first layout pass would place it using a bogus size.
            if (_animate)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
            }

            base.Show();

            UpdateLayout();
            Position();

            if (_animate)
                BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(FadeDuration)));
        }

        /// <summary>
        /// Tints the panel to the configured opacity. Re-applied on each show, which is
        /// often enough to pick up a settings change without watching for one.
        /// </summary>
        private void ApplyBackgroundOpacity()
        {
            if (!AllowsTransparency) return;

            var baseColor = ((System.Windows.Media.SolidColorBrush)FindResource("BaseBrush")).Color;
            RootBorder.Background =
                WindowStyling.Translucent(baseColor, _config.Current.Overlay.BackgroundOpacity);
        }

        /// <summary>
        /// Shows how far through a hold gesture the user is, as a filling dial.
        ///
        /// A dial rather than a countdown: it reads at a glance, needs no reading, and does
        /// not resize the toast as the digits change. It replaces the glyph in place so the
        /// panel keeps its shape.
        /// </summary>
        public void ShowHoldProgress(string label, double fraction)
        {
            ApplyBackgroundOpacity();

            _holding = true;
            TxtMessage.Text = label;
            IcoGlyph.Kind = string.Empty;

            ShowDial(true);
            HoldArc.Data = BuildArc(Math.Clamp(fraction, 0, 1), 10, 1.25);

            LevelBar.Visibility = Visibility.Collapsed;

            if (!_onScreen)
            {
                EnterFromHidden();
                _onScreen = true;
            }

            // No auto-hide while the keys are still down.
            _hideTimer.Stop();
        }

        /// <summary>
        /// Ends the dial. The action that follows usually raises its own toast, which
        /// replaces this one; a cancelled hold just fades out.
        /// </summary>
        public void EndHoldProgress()
        {
            if (!_holding) return;

            _holding = false;
            ShowDial(false);

            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void ShowDial(bool visible)
        {
            var state = visible ? Visibility.Visible : Visibility.Collapsed;
            HoldTrack.Visibility = state;
            HoldArc.Visibility = state;
            IcoGlyph.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// An arc from twelve o'clock, sweeping clockwise through <paramref name="fraction"/>
        /// of the circle. Inset by half the stroke so the line stays inside its box.
        /// </summary>
        private static Geometry BuildArc(double fraction, double radius, double inset)
        {
            double r = radius - inset;
            var centre = new Point(radius, radius);
            var start = new Point(centre.X, centre.Y - r);

            if (fraction <= 0.001) return Geometry.Empty;

            // A full sweep cannot be drawn as one arc — the endpoints coincide — so fall
            // back to a circle.
            if (fraction >= 0.999)
            {
                return new EllipseGeometry(centre, r, r);
            }

            double angle = fraction * 2 * Math.PI;
            var end = new Point(
                centre.X + r * Math.Sin(angle),
                centre.Y - r * Math.Cos(angle));

            var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(r, r),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = fraction > 0.5,
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        /// <summary>Returns true when the change alters the window's height.</summary>
        private bool ApplyContent(string glyph, string message, double? level)
        {
            IcoGlyph.Kind = glyph;
            TxtMessage.Text = message;

            var wanted = level.HasValue ? Visibility.Visible : Visibility.Collapsed;
            bool changed = LevelBar.Visibility != wanted;

            if (level.HasValue) LevelBar.Value = Math.Clamp(level.Value, 0, 1);
            LevelBar.Visibility = wanted;

            // The row is a fixed height, so nothing here can move the window any more.
            return false;
        }

        /// <summary>
        /// Follows the overlay's own anchor settings, so feedback appears where the user
        /// already looks for this app rather than in a corner of its own choosing.
        /// </summary>
        private void Position()
        {
            var overlay = _config.Current.Overlay;

            var anchor = new OverlayConfig
            {
                Anchor = overlay.Anchor,
                Monitor = overlay.Monitor,
                MonitorDeviceName = overlay.MonitorDeviceName,
                MarginX = overlay.MarginX,
                MarginY = overlay.MarginY,
            };

            // Each role owns a reserved row beneath the overlay, so the two toasts can both
            // be on screen without colliding and neither can land on the applet.
            OverlayPositioner.PositionToastRow(this, anchor, (int)Role, AppletWidth, AppletHeight);
        }

        private static double AppletWidth => (double)System.Windows.Application.Current.FindResource("PanelWidth");

        private static double AppletHeight => (double)System.Windows.Application.Current.FindResource("PanelHeight");

        private void FadeAway()
        {
            _fadingOut = true;
            int generation = ++_fadeGeneration;

            if (!_animate)
            {
                _onScreen = false;
                _fadingOut = false;
                Hide();
                return;
            }

            var fade = new DoubleAnimation(0, new Duration(FadeDuration));
            fade.Completed += (_, _) =>
            {
                // A newer Show() superseded this fade; leave the window alone.
                if (generation != _fadeGeneration) return;

                _onScreen = false;
                _fadingOut = false;

                // Hide, never close: recreating the window per toast would churn an HWND
                // several times a second while the volume keys are held.
                Hide();
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
            };

            BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>Takes the toast down immediately, without the fade.</summary>
        public void HideNow()
        {
            _hideTimer.Stop();
            _fadeGeneration++;
            _onScreen = false;
            _fadingOut = false;

            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Hide();
        }
    }
}
