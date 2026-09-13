using AppKit;
using CoreGraphics;
using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue.UI
{
    /// <summary>
    /// The search results, stacked directly above the search bar.
    ///
    /// A row must be <b>pickable</b>, which is why it carries the album and not just the
    /// title. Measured on Windows across six realistic searches on a real library: 14 of 45
    /// rows had an identical twin on screen — the studio cut, the live version and the
    /// compilation all read "Hells Bells — AC-DC". Duration does not separate them either,
    /// since two pressings of one recording share a running time. `Track.SearchSubtitle` is
    /// what carries the album, and `TrackCollection` mirrors it so one row renders either.
    /// </summary>
    public sealed class SearchResultsSection : OverlaySection
    {
        private readonly NSView _rows;
        private readonly NSTextField _message;

        private readonly List<object> _items = new();
        private readonly List<NSView> _rowViews = new();

        private int _selected = -1;

        public SearchResultsSection(OverlayConfig config)
            : base(config, OverlayMetrics.ResultRowHeight + Inset * 2)
        {
            _rows = new NSView(ContentArea);
            AddSubview(_rows);

            _message = new NSTextField
            {
                Frame = ContentArea,
                Editable = false,
                Selectable = false,
                Bezeled = false,
                DrawsBackground = false,
                TextColor = Theme.Subtext,
                Font = NSFont.SystemFontOfSize((nfloat)OverlayMetrics.ResultTitleFontSize)
                       ?? NSFont.SystemFontOfSize(NSFont.SystemFontSize)!,
                StringValue = string.Empty,
            };
            AddSubview(_message);
        }

        private static double Inset => OverlayMetrics.RootPadding + OverlayMetrics.RootBorderThickness;

        public int Count => _items.Count;

        /// <summary>The highlighted row, or null when there is nothing to act on.</summary>
        public object? Selected => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

        /// <summary>
        /// Shows a message instead of rows — "searching…", an error, or which kind of thing
        /// a scoped search found nothing of. An empty scoped result says <i>which</i> kind
        /// found nothing, so the term need not be retyped to find out.
        /// </summary>
        public void ShowMessage(string text)
        {
            _items.Clear();
            ClearRows();
            _selected = -1;

            _message.Hidden = false;
            _message.StringValue = text;

            SetSectionHeight(OverlayMetrics.ResultRowHeight + Inset * 2);
            _message.Frame = ContentArea;
        }

        public void Show(IReadOnlyList<Track> tracks, IReadOnlyList<TrackCollection> collections)
        {
            _items.Clear();

            // Collections lead, as they do on Windows: albums before playlists, both before
            // tracks, because an album row is the one worth seeing when a library imports
            // every album as a playlist too.
            foreach (var c in collections.Where(c => c.Kind == CollectionKind.Album)) _items.Add(c);
            foreach (var c in collections.Where(c => c.Kind != CollectionKind.Album)) _items.Add(c);
            foreach (var t in tracks) _items.Add(t);

            ClearRows();
            _message.Hidden = true;

            int visible = Math.Min(_items.Count, OverlayMetrics.MaxVisibleResults);
            SetSectionHeight(Math.Max(1, visible) * OverlayMetrics.ResultRowHeight + Inset * 2);

            var area = ContentArea;
            _rows.Frame = area;

            for (int i = 0; i < visible; i++)
            {
                // AppKit's y grows upward, so the first row sits at the top of the panel.
                double y = area.Height - (i + 1) * OverlayMetrics.ResultRowHeight;
                var row = BuildRow(_items[i], new CGRect(0, y, area.Width, OverlayMetrics.ResultRowHeight));

                _rowViews.Add(row);
                _rows.AddSubview(row);
            }

            _selected = _items.Count > 0 ? 0 : -1;
            Highlight();
        }

        /// <summary>True when the highlight is on the first row, or there is nothing to move.</summary>
        public bool IsAtFirst => _selected <= 0;

        public void MoveSelection(int delta)
        {
            if (_items.Count == 0) return;

            _selected = Math.Clamp(_selected + delta, 0, Math.Min(_items.Count, _rowViews.Count) - 1);
            Highlight();
        }

        private void Highlight()
        {
            for (int i = 0; i < _rowViews.Count; i++)
            {
                _rowViews[i].Layer!.BackgroundColor =
                    (i == _selected ? Theme.Surface0 : NSColor.Clear).CGColor;
            }
        }

        private void ClearRows()
        {
            foreach (var row in _rowViews) row.RemoveFromSuperview();
            _rowViews.Clear();
        }

        private static NSView BuildRow(object item, CGRect frame)
        {
            var row = new NSView(frame) { WantsLayer = true };
            row.Layer!.CornerRadius = 4;

            string title, subtitle, trailing;

            if (item is Track track)
            {
                title = track.Title;
                subtitle = track.SearchSubtitle;
                trailing = track.DurationText;
            }
            else
            {
                var collection = (TrackCollection)item;
                title = collection.Title;

                // An album row leads with its artist, a playlist row with its size: an artist
                // is what tells two same-named albums apart, and a playlist has no single one.
                subtitle = collection.DisplayArtist;
                trailing = collection.Kind == CollectionKind.Album ? "Album" : "Playlist";
            }

            const double pad = 6;
            double trailingWidth = 60;
            double textWidth = frame.Width - pad * 2 - trailingWidth;

            row.AddSubview(RowLabel(title, OverlayMetrics.ResultTitleFontSize, Theme.Text,
                new CGRect(pad, frame.Height / 2 - 1, textWidth, 16)));

            row.AddSubview(RowLabel(subtitle, OverlayMetrics.ResultSubtitleFontSize, Theme.Subtext,
                new CGRect(pad, frame.Height / 2 - 16, textWidth, 14)));

            var trail = RowLabel(trailing, OverlayMetrics.ResultSubtitleFontSize, Theme.Subtext,
                new CGRect(frame.Width - pad - trailingWidth, frame.Height / 2 - 8, trailingWidth, 14));
            trail.Alignment = NSTextAlignment.Right;
            row.AddSubview(trail);

            return row;
        }

        private static NSTextField RowLabel(string text, double size, NSColor colour, CGRect frame) => new()
        {
            Frame = frame,
            StringValue = text,
            Editable = false,
            Selectable = false,
            Bezeled = false,
            DrawsBackground = false,
            TextColor = colour,
            Font = NSFont.SystemFontOfSize((nfloat)size) ?? NSFont.SystemFontOfSize(NSFont.SystemFontSize)!,
            LineBreakMode = NSLineBreakMode.TruncatingTail,
            Cell = { UsesSingleLineMode = true },
        };
    }
}
