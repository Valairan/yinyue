using AppKit;
using CoreGraphics;
using Foundation;
using Yinyue.Models;

namespace Yinyue.UI
{
    /// <summary>
    /// The search box — its own row, directly above the applet, present for as long as the
    /// overlay is.
    ///
    /// It used to share the applet's status line on Windows, appearing on a shortcut and
    /// taking the status away while it was there. Now the box is simply always present, the
    /// status keeps its place, and the search shortcut only moves the caret — it reveals
    /// nothing. There is no lens button for the same reason: its only job was revealing the
    /// box, and a search box with placeholder text in it is a stronger hint than a lens icon
    /// anyway.
    /// </summary>
    public sealed class SearchBarSection : OverlaySection
    {
        private readonly NSTextField _box;
        private readonly NSTextField _placeholder;

        /// <summary>Raised as the text changes, already debounced by the owner.</summary>
        public event EventHandler<string>? QueryChanged;

        /// <summary>Enter, Escape, and the arrows the results list needs.</summary>
        public event EventHandler<NSEvent>? KeyPressed;

        public SearchBarSection(OverlayConfig config)
            : base(config, OverlayMetrics.SearchBarHeight)
        {
            var area = ContentArea;

            var icon = new NSImageView
            {
                Frame = new CGRect(area.X + 2,
                                   area.Y + (area.Height - OverlayMetrics.SearchIconSize) / 2,
                                   OverlayMetrics.SearchIconSize, OverlayMetrics.SearchIconSize),
                Image = Icon.Make(Icons.Search, OverlayMetrics.SearchIconSize, Theme.Subtext),
                ImageScaling = NSImageScale.ProportionallyDown,
            };
            AddSubview(icon);

            double boxX = area.X + 2 + OverlayMetrics.SearchIconSize + OverlayMetrics.SearchIconGap;
            double boxW = area.X + area.Width - boxX;
            double boxH = Math.Ceiling(OverlayMetrics.SearchFontSize * 1.4);
            var boxFrame = new CGRect(boxX, area.Y + (area.Height - boxH) / 2, boxW, boxH);

            // The placeholder sits behind the box rather than using AppKit's own, so the two
            // apps show the same string in the same place. It names the prefixes because
            // there is nowhere else they would ever be found: the search box is the only
            // place they work and the only place anyone looks.
            _placeholder = new NSTextField
            {
                Frame = boxFrame,
                Editable = false,
                Selectable = false,
                Bezeled = false,
                DrawsBackground = false,
                StringValue = "Search…  try  album:  fav:  queue:",
                TextColor = Theme.Surface2,
                Font = SearchFont,
            };
            AddSubview(_placeholder);

            _box = new NSTextField
            {
                Frame = boxFrame,
                Bezeled = false,
                DrawsBackground = false,
                Bordered = false,
                TextColor = Theme.Text,
                Font = SearchFont,
                StringValue = string.Empty,
            };

            _box.Changed += (_, _) =>
            {
                _placeholder.Hidden = _box.StringValue.Length > 0;
                QueryChanged?.Invoke(this, _box.StringValue);
            };

            AddSubview(_box);
        }

        /// <summary>
        /// System fonts are always present but the binding types them as nullable; asserted
        /// once here rather than at each use.
        /// </summary>
        private static NSFont SearchFont =>
            NSFont.SystemFontOfSize((nfloat)OverlayMetrics.SearchFontSize)
            ?? NSFont.SystemFontOfSize(NSFont.SystemFontSize)!;

        public string Query => _box.StringValue;

        public bool HasText => _box.StringValue.Length > 0;

        public void Clear()
        {
            _box.StringValue = string.Empty;
            _placeholder.Hidden = false;
            QueryChanged?.Invoke(this, string.Empty);
        }

        /// <summary>Puts the caret in the box. Reveals nothing — the box was already there.</summary>
        public void FocusBox()
        {
            Window?.MakeFirstResponder(_box);

            // Caret to the end rather than selecting everything, so a shortcut pressed
            // mid-search does not lose what was typed on the next keystroke.
            if (_box.CurrentEditor is { } editor)
                editor.SelectedRange = new NSRange(_box.StringValue.Length, 0);
        }

        /// <summary>
        /// Appends a character typed while the overlay had focus elsewhere, so typing
        /// anywhere in the overlay drops into search and keeps the character.
        /// </summary>
        public void AppendTyped(string characters)
        {
            _box.StringValue += characters;
            _placeholder.Hidden = _box.StringValue.Length > 0;
            FocusBox();
            QueryChanged?.Invoke(this, _box.StringValue);
        }

        public void RaiseKey(NSEvent e) => KeyPressed?.Invoke(this, e);
    }
}
