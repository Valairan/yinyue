using AppKit;
using CoreGraphics;
using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue.UI
{
    /// <summary>
    /// What is actually coming next, in play order. Sits above the search bar when both are
    /// open and drops against the applet when search closes.
    ///
    /// The queue's actions are <b>global shortcuts</b>, not keys this panel handles. On
    /// Windows that is because the queue lives in a Popup with its own visual tree where
    /// routed key events cannot reach; here the reason is the same in a different shape —
    /// this is a separate window that deliberately never becomes key, so no keystroke is ever
    /// delivered to it. Either way the grab has to come from outside.
    /// </summary>
    public sealed class QueuePanel : StackedPanel
    {
        private readonly PlaybackService _playback;

        private readonly NSTextField _header;
        private readonly NSTextField _hint;
        private readonly NSView _rows;

        private readonly List<NSView> _rowViews = new();
        private List<Track> _tracks = new();

        private int _selected = -1;

        /// <summary>
        /// The entry picked up for moving, or -1. While one is held the arrows move it rather
        /// than the selection, and every other key is swallowed so a stray Delete cannot act
        /// on a queue mid-rearrangement.
        /// </summary>
        private int _held = -1;

        private int _heldOrigin = -1;

        public QueuePanel(OverlayConfig config, PlaybackService playback)
            : base(config, HeightFor(0))
        {
            _playback = playback;

            var area = ContentArea;

            _header = Label(area.Y + area.Height - 16, OverlayMetrics.ResultTitleFontSize, Theme.Text, bold: true);
            _hint = Label(area.Y + area.Height - 30, OverlayMetrics.ResultSubtitleFontSize, Theme.Subtext);

            ContentView!.AddSubview(_header);
            ContentView.AddSubview(_hint);

            _rows = new NSView(new CGRect(area.X, area.Y, area.Width, area.Height - HeaderHeight));
            ContentView.AddSubview(_rows);

            _playback.QueueChanged += (_, _) =>
                NSApplication.SharedApplication.BeginInvokeOnMainThread(Refresh);
        }

        private const double HeaderHeight = 34;

        private static double Inset => OverlayMetrics.RootPadding + OverlayMetrics.RootBorderThickness;

        private static double HeightFor(int rows) =>
            HeaderHeight + Math.Max(1, Math.Min(rows, OverlayMetrics.MaxVisibleResults))
                * OverlayMetrics.ResultRowHeight + Inset * 2;

        /// <summary>True while an entry is held, so the owner can route the arrows correctly.</summary>
        public bool IsHolding => _held >= 0;

        /// <summary>
        /// Opening is when the playing track gets the highlight; a later refresh must not
        /// take it back, because the user's selection wins from then on.
        /// </summary>
        public void Open()
        {
            _selected = -1;
            Refresh();
            OrderFrontRegardless();
        }

        public void Refresh()
        {
            var snapshot = _playback.Snapshot();
            _tracks = snapshot.Tracks.ToList();

            // The user's selection wins over the playhead. Snapping back to the playing track
            // on every refresh is actively hostile after a removal: the highlight lands on the
            // one entry RemoveAt refuses, so the next press does nothing. Only a selection
            // that no longer exists falls back.
            if (_selected < 0 || _selected >= _tracks.Count)
                _selected = Math.Clamp(snapshot.Position, -1, _tracks.Count - 1);

            _header.StringValue = _tracks.Count == 0 ? "Queue" : $"Queue — {_tracks.Count}";
            _hint.StringValue = _held >= 0
                ? "Arrows move it · Enter confirms · Esc puts it back"
                : "Nothing queued";

            if (_tracks.Count > 0 && _held < 0) _hint.StringValue = string.Empty;

            SetHeight(HeightFor(_tracks.Count));

            var area = ContentArea;
            _header.SetFrameOrigin(new CGPoint(area.X, area.Y + area.Height - 16));
            _hint.SetFrameOrigin(new CGPoint(area.X, area.Y + area.Height - 30));
            _rows.Frame = new CGRect(area.X, area.Y, area.Width, area.Height - HeaderHeight);

            BuildRows(snapshot.Position);
        }

        private void BuildRows(int playing)
        {
            foreach (var row in _rowViews) row.RemoveFromSuperview();
            _rowViews.Clear();

            int visible = Math.Min(_tracks.Count, OverlayMetrics.MaxVisibleResults);

            // Scroll the window so the selection stays on screen without a scroll view.
            int first = Math.Max(0, Math.Min(_selected - visible + 2, _tracks.Count - visible));
            if (first < 0) first = 0;

            for (int i = 0; i < visible; i++)
            {
                int index = first + i;
                double y = _rows.Frame.Height - (i + 1) * OverlayMetrics.ResultRowHeight;

                var row = BuildRow(_tracks[index], index, index == playing, index == _selected,
                    new CGRect(0, y, _rows.Frame.Width, OverlayMetrics.ResultRowHeight));

                _rowViews.Add(row);
                _rows.AddSubview(row);
            }
        }

        private NSView BuildRow(Track track, int index, bool playing, bool selected, CGRect frame)
        {
            var row = new NSView(frame) { WantsLayer = true };
            row.Layer!.CornerRadius = 4;
            row.Layer.BackgroundColor = (selected ? Theme.Surface0 : NSColor.Clear).CGColor;

            // The held row gains an accent rule, so the mode is visible without a caption.
            if (index == _held)
            {
                row.Layer.BorderWidth = 1;
                row.Layer.BorderColor = Theme.Accent.CGColor;
            }

            const double pad = 6;
            double width = frame.Width - pad * 2 - 60;

            var title = RowLabel(track.Title, OverlayMetrics.ResultTitleFontSize,
                playing ? Theme.Accent : Theme.Text,
                new CGRect(pad, frame.Height / 2 - 1, width, 16));
            row.AddSubview(title);

            row.AddSubview(RowLabel(track.SearchSubtitle, OverlayMetrics.ResultSubtitleFontSize,
                Theme.Subtext, new CGRect(pad, frame.Height / 2 - 16, width, 14)));

            var duration = RowLabel(track.DurationText, OverlayMetrics.ResultSubtitleFontSize,
                Theme.Subtext, new CGRect(frame.Width - pad - 60, frame.Height / 2 - 8, 60, 14));
            duration.Alignment = NSTextAlignment.Right;
            row.AddSubview(duration);

            return row;
        }

        // ---------------------------------------------------------------- actions

        public void MoveSelection(int delta)
        {
            if (_tracks.Count == 0) return;

            if (_held >= 0)
            {
                // Moving the entry itself, not the highlight. The pointer follows the track
                // rather than the index, which PlaybackService.MoveTo handles.
                int target = Math.Clamp(_held + delta, 0, _tracks.Count - 1);
                if (target == _held) return;

                if (_playback.MoveTo(_held, target))
                {
                    _held = target;
                    _selected = target;
                    Refresh();
                }

                return;
            }

            _selected = Math.Clamp(_selected + delta, 0, _tracks.Count - 1);
            Refresh();
        }

        /// <summary>Picks the highlighted entry up, or puts it down where it now sits.</summary>
        public void ToggleGrab()
        {
            if (_tracks.Count == 0) return;

            if (_held >= 0)
            {
                _held = -1;
                _heldOrigin = -1;
            }
            else
            {
                _held = _selected;
                _heldOrigin = _selected;
            }

            Refresh();
        }

        /// <summary>Puts a held entry back where it was picked up.</summary>
        public void CancelGrab()
        {
            if (_held < 0) return;

            if (_held != _heldOrigin) _playback.MoveTo(_held, _heldOrigin);

            _selected = _heldOrigin;
            _held = -1;
            _heldOrigin = -1;
            Refresh();
        }

        public void JumpToSelected()
        {
            if (_selected < 0 || _selected >= _tracks.Count) return;
            _ = _playback.JumpToAsync(_selected);
        }

        /// <summary>
        /// Removing keeps the highlight where the removed row was, so a run of removals keeps
        /// working rather than stranding the cursor on the one entry RemoveAt refuses.
        /// </summary>
        public void RemoveSelected()
        {
            if (_held >= 0) return;   // never act on a queue mid-rearrangement
            if (_selected < 0 || _selected >= _tracks.Count) return;

            int at = _selected;
            if (_playback.RemoveAt(at)) _selected = Math.Min(at, _tracks.Count - 2);

            Refresh();
        }

        private static NSTextField Label(double y, double size, NSColor colour, bool bold = false) => new()
        {
            Frame = new CGRect(0, y, OverlayMetrics.PanelWidth - Inset * 2, 16),
            Editable = false,
            Selectable = false,
            Bezeled = false,
            DrawsBackground = false,
            TextColor = colour,
            Font = (bold ? NSFont.BoldSystemFontOfSize((nfloat)size) : NSFont.SystemFontOfSize((nfloat)size))
                   ?? NSFont.SystemFontOfSize(NSFont.SystemFontSize)!,
            StringValue = string.Empty,
        };

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
