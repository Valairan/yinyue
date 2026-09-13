namespace Yinyue.UI
{
    /// <summary>
    /// The overlay's measurements, in device-independent units, shared by both shells.
    ///
    /// The two apps must lay out identically. They share no UI code and never will — WPF is
    /// declarative and AppKit imperative — so the only thing that can keep them in step is a
    /// single set of numbers that both read. This is that set, and it is the same argument as
    /// Common/Icons: one source, or they drift the first time either is touched.
    ///
    /// These are design constants, not platform facts, which is why they can live in Core
    /// without breaking its rule. Nothing here references a toolkit.
    ///
    /// <b>Windows is the reference.</b> Every value was measured from MainWindow.xaml, and
    /// where a number here disagrees with that file, this file is wrong. Change both, in one
    /// commit, or the guarantee is gone.
    /// </summary>
    public static class OverlayMetrics
    {
        // ---- the panel itself ----

        /// <summary>Shared with the toast and the stacked panels, all of which match it.</summary>
        public const double PanelWidth = 420;

        /// <summary>
        /// 148 of content inside 10 of padding and 1 of border on each side. The artwork
        /// column's height follows from this, not the other way round.
        /// </summary>
        public const double AppletHeight = 170;

        public const double RootCornerRadius = 8;
        public const double RootBorderThickness = 1;
        public const double RootPadding = 10;

        /// <summary>Height available inside the padding and border: 170 − 2×10 − 2×1.</summary>
        public const double ContentHeight =
            AppletHeight - RootPadding * 2 - RootBorderThickness * 2;

        public const double ContentWidth =
            PanelWidth - RootPadding * 2 - RootBorderThickness * 2;

        // ---- the stack ----
        //
        // Everything lives in one vertical stack and every surface has a reserved slot:
        // bottom upwards, the hold dial, the message toast, the applet, the search bar, the
        // results, the queue. All are PanelWidth wide and separated by SideGap.

        /// <summary>Gap between stacked surfaces.</summary>
        public const double SideGap = 6;

        /// <summary>
        /// A toast row is a fixed height, so a toast gaining or losing its level bar cannot
        /// resize the window and shift everything above it.
        ///
        /// This number used to live twice on Windows — once as a resource in App.xaml sizing
        /// the window, once as a constant in OverlayPositioner spacing the rows — with a
        /// comment on each saying they must match. One definition is the fix.
        /// </summary>
        public const double ToastRowHeight = 60;

        public const int ToastRows = 2;

        /// <summary>
        /// Reserved permanently, showing or not. Toasts arrive unbidden — on a track change,
        /// or a volume key pressed inside another app — and a panel that jumped upward
        /// mid-interaction would move the thing being read. A fixed slot is worth the pixels.
        /// </summary>
        public const double ReservedForToasts = ToastRows * (ToastRowHeight + SideGap);

        // ---- artwork ----

        /// <summary>
        /// A square centred in its column, not a box the full height of the panel.
        /// UniformToFill into a 148-tall frame scaled the art to 148 and clipped the excess
        /// off the right edge only, so the mark sat visibly left of centre. A square frame
        /// has nothing to clip for square art.
        /// </summary>
        public const double ArtSize = 130;

        public const double ArtCornerRadius = 6;

        /// <summary>Gap between the artwork column and the content column.</summary>
        public const double ArtToContentGap = 10;

        // ---- text ----

        public const double StatusFontSize = 11;
        public const double TitleFontSize = 13;
        public const double ArtistFontSize = 11;
        public const double TimeFontSize = 10;

        /// <summary>Trailing gap on the status line, so it cannot run into the header buttons.</summary>
        public const double StatusRightGap = 6;

        // ---- rows ----

        public const double MetadataTopMargin = 4;
        public const double MetadataBottomMargin = 2;
        public const double SeekRowMargin = 2;
        public const double TransportTopMargin = 4;

        public const double SeekBarHeight = 12;

        /// <summary>Gap between the elapsed/total labels and the seek bar.</summary>
        public const double SeekLabelGap = 6;

        // ---- buttons ----

        /// <summary>
        /// The 24-unit icon box is drawn at this size on both platforms, so the stroke
        /// weight matches too.
        /// </summary>
        public const double IconSize = 18;

        /// <summary>
        /// A button must not resize when its icon changes, or the centred row reflows and
        /// every button appears to jump. With text glyphs that was a live problem — the play
        /// triangle measured 20.9px against 28.6px for every other glyph. With a fixed icon
        /// box it is a guard rather than a fix, and it keeps the row's spacing where it was.
        /// </summary>
        public const double ButtonMinWidth = 30;

        public const double ButtonPadding = 4;
        public const double ButtonCornerRadius = 4;

        /// <summary>Horizontal margin either side of a transport button.</summary>
        public const double TransportButtonMargin = 4;

        /// <summary>
        /// Extra space before the shuffle button, separating the three transport controls
        /// from the three mode toggles without a divider.
        /// </summary>
        public const double ModeGroupGap = 12;

        /// <summary>
        /// Header buttons are the transport style with no horizontal margin, because the
        /// header shares its row with the status line and that line needs the room. They are
        /// otherwise identical, so the four icons above the title measure exactly what the
        /// six below the seek bar do.
        /// </summary>
        public const double HeaderButtonMargin = 0;
    }
}
