using AppKit;
using CoreGraphics;
using Yinyue.Models;

namespace Yinyue.UI
{
    /// <summary>
    /// A panel in the overlay's vertical stack — the search bar, the results, the queue.
    /// Each is PanelWidth wide and carries the applet's background, border and radius, so it
    /// reads as an extension of the applet rather than as a separate popup.
    ///
    /// <b>These are child windows, and that is the important difference from Windows.</b>
    /// WPF uses Popups, which are placed once when they open and never again — so moving the
    /// overlay strands every panel where the overlay used to be, and MainWindow has to nudge
    /// each one back on LocationChanged. AppKit child windows move with their parent by
    /// construction, so that whole class of bug does not exist here and there is no
    /// ReplacePanels equivalent to write.
    ///
    /// They also inherit the parent's level and ordering, which is what keeps the stack
    /// together above other applications.
    /// </summary>
    public class StackedPanel : NSPanel
    {
        private readonly OverlayConfig _config;

        protected StackedPanel(OverlayConfig config, double height)
            : base(new CGRect(0, 0, OverlayMetrics.PanelWidth, height),
                   NSWindowStyle.Borderless | NSWindowStyle.NonactivatingPanel | NSWindowStyle.Utility,
                   NSBackingStore.Buffered,
                   deferCreation: false)
        {
            _config = config;

            Level = NSWindowLevel.Floating;
            HidesOnDeactivate = false;
            IsOpaque = false;
            BackgroundColor = NSColor.Clear;
            HasShadow = true;
            MovableByWindowBackground = false;

            CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces
                               | NSWindowCollectionBehavior.FullScreenAuxiliary
                               | NSWindowCollectionBehavior.IgnoresCycle;

            ContentView = BuildRoot(height);
        }

        /// <summary>
        /// Only the search bar takes keys; the others are read and acted on through global
        /// shortcuts, so they must never pull focus from the box.
        /// </summary>
        public override bool CanBecomeKeyWindow => false;

        /// <summary>
        /// Raised on any input to this panel. The auto-hide countdown belongs to the applet,
        /// but the input often lands on a panel above it — typing goes to the search box, not
        /// to the overlay — so every panel has to report it or the overlay hides mid-search.
        /// </summary>
        public event EventHandler? Interacted;

        public override void SendEvent(NSEvent theEvent)
        {
            switch (theEvent.Type)
            {
                case NSEventType.KeyDown:
                case NSEventType.LeftMouseDown:
                case NSEventType.RightMouseDown:
                case NSEventType.MouseMoved:
                case NSEventType.ScrollWheel:
                    Interacted?.Invoke(this, EventArgs.Empty);
                    break;
            }

            base.SendEvent(theEvent);
        }

        public override bool CanBecomeMainWindow => false;

        /// <summary>The area inside the border and padding, where content goes.</summary>
        protected CGRect ContentArea
        {
            get
            {
                double inset = OverlayMetrics.RootPadding + OverlayMetrics.RootBorderThickness;
                return new CGRect(inset, inset,
                    OverlayMetrics.PanelWidth - inset * 2,
                    Frame.Height - inset * 2);
            }
        }

        private NSView BuildRoot(double height)
        {
            var root = new NSView(new CGRect(0, 0, OverlayMetrics.PanelWidth, height))
            {
                WantsLayer = true,
            };

            // Held separately: once glass is on, ContentView is the wrapper, and the tint and
            // the applet both belong to the view inside it.
            _panelRoot = root;

            var layer = root.Layer!;
            layer.CornerRadius = (nfloat)OverlayMetrics.RootCornerRadius;
            layer.BorderWidth = (nfloat)OverlayMetrics.RootBorderThickness;
            layer.BorderColor = Theme.Surface0.CGColor;
            layer.MasksToBounds = true;

            ApplyBackgroundOpacity(root);
            return root;
        }

        /// <summary>
        /// Re-reads the tint and applies it.
        ///
        /// Called on every config change rather than cached at construction, so the setting
        /// takes effect without a restart — the same rule the Windows overlay follows. The
        /// animations switch is the exception on both platforms, because transparency cannot
        /// be changed once a window is on screen.
        /// </summary>
        private NSView? _panelRoot;

        /// <summary>The view that carries the tint and the content, inside any glass wrapper.</summary>
        protected NSView PanelRoot => _panelRoot ?? ContentView!;

        public void ApplyBackgroundOpacity(NSView? root = null)
        {
            // With glass on, the panel's own fill would sit in front of the material and
            // hide it. The glass provides the background; the tint steps aside.
            var view = root ?? _panelRoot;
            if (view?.Layer is not { } layer) return;


            layer.BackgroundColor = Theme.Base
                .ColorWithAlphaComponent((nfloat)_config.BackgroundOpacity).CGColor;
        }

        /// <summary>
        /// Resizes without moving the top edge, so a panel that grows extends downward into
        /// the gap rather than shifting the stack above it. The stack is re-laid out by the
        /// owner afterwards.
        /// </summary>
        protected void SetHeight(double height)
        {
            var frame = Frame;
            SetFrame(new CGRect(frame.X, frame.Y, OverlayMetrics.PanelWidth, height), true);

            if (ContentView is { } view)
                view.Frame = new CGRect(0, 0, OverlayMetrics.PanelWidth, height);

            if (_panelRoot is { } inner)
                inner.Frame = new CGRect(0, 0, OverlayMetrics.PanelWidth, height);
        }
    }
}
