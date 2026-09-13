using AppKit;
using CoreGraphics;
using Foundation;
using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue.UI
{
    /// <summary>
    /// Owns the vertical stack and the search it drives.
    ///
    /// Bottom upwards: the applet, then the search bar, then the results. Everything is
    /// PanelWidth wide, left-aligned with the applet and separated by SideGap, so the stack
    /// reads as one surface.
    ///
    /// The panels are child windows of the applet, so they follow it when the anchor or the
    /// monitor changes — the Windows build has to nudge each Popup back by hand on
    /// LocationChanged, and that entire problem is absent here.
    /// </summary>
    public sealed class OverlayStack : IDisposable
    {
        private const int SearchLimit = 20;

        /// <summary>
        /// Typing is not a search; a pause is. Without this every keystroke starts a server
        /// round trip and the results flicker through partial words.
        /// </summary>
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

        private readonly OverlayConfig _config;
        private readonly MusicLibrary _library;
        private readonly PlaybackService _playback;

        private readonly OverlayPanel _applet;
        private readonly SearchBarPanel _searchBar;
        private readonly SearchResultsPanel _results;

        private NSTimer? _debounce;
        private CancellationTokenSource? _search;

        public OverlayStack(OverlayConfig config, MusicLibrary library, PlaybackService playback,
                            OverlayPanel applet)
        {
            _config = config;
            _library = library;
            _playback = playback;
            _applet = applet;

            _searchBar = new SearchBarPanel(config);
            _results = new SearchResultsPanel(config);

            _searchBar.QueryChanged += (_, text) => OnQueryChanged(text);

            // Child windows follow the parent, which is what makes the stack hold together.
            _applet.AddChildWindow(_searchBar, NSWindowOrderingMode.Above);
            _applet.AddChildWindow(_results, NSWindowOrderingMode.Above);

            // The stack owns its relationship to the applet rather than having the delegate
            // wire it: a stack that has not been laid out sits at the window origin, and
            // making that someone else's job is how it ends up unlaid in one path and not
            // the other.
            _applet.Shown += (_, _) => Show();
            _applet.Hidden += (_, _) => Hide();

            _results.OrderOut(null);
            Layout();
        }

        public SearchBarPanel SearchBar => _searchBar;

        /// <summary>
        /// Places every panel above the applet, outward, so each clears everything between it
        /// and the applet and a closed panel leaves no hole.
        /// </summary>
        public void Layout()
        {
            var anchor = _applet.Frame;
            double y = anchor.Y + anchor.Height + OverlayMetrics.SideGap;

            _searchBar.SetFrameOrigin(new CGPoint(anchor.X, y));
            y += _searchBar.Frame.Height + OverlayMetrics.SideGap;

            if (_results.IsVisible)
                _results.SetFrameOrigin(new CGPoint(anchor.X, y));
        }

        public void Show()
        {
            _searchBar.OrderFrontRegardless();
            Layout();
        }

        public void Hide()
        {
            _searchBar.OrderOut(null);
            _results.OrderOut(null);
        }

        /// <summary>Puts the caret in the box. The box was already there; nothing is revealed.</summary>
        public void FocusSearch()
        {
            _searchBar.OrderFrontRegardless();
            _searchBar.MakeKeyAndOrderFront(null);
            _searchBar.FocusBox();
            Layout();
        }

        /// <summary>
        /// Escape decides on the box's contents: a search in progress is cleared, an already
        /// empty box means the caller should dismiss the overlay. Returns true when it
        /// handled the key itself.
        /// </summary>
        public bool HandleEscape()
        {
            if (!_searchBar.HasText) return false;

            _searchBar.Clear();
            CloseResults();
            return true;
        }

        public void MoveSelection(int delta) => _results.MoveSelection(delta);

        /// <summary>Plays the highlighted row, or the top one when nothing is highlighted.</summary>
        public void PlaySelected()
        {
            switch (_results.Selected)
            {
                case Track track:
                    // Only the tracks from the results are queued: a collection row cannot be
                    // played by Next, and leaving it in the queue would make a skip appear to
                    // do nothing.
                    _ = _playback.PlayNowAsync(track);
                    break;

                case TrackCollection collection:
                    _ = PlayCollectionAsync(collection);
                    break;
            }
        }

        private async Task PlayCollectionAsync(TrackCollection collection)
        {
            var result = await _library.GetCollectionTracksAsync(collection, 1000, CancellationToken.None)
                                       .ConfigureAwait(false);

            if (result.Tracks.Count == 0) return;

            await _playback.PlayQueueAsync(result.Tracks, 0).ConfigureAwait(false);
        }

        private void OnQueryChanged(string text)
        {
            _debounce?.Invalidate();

            if (string.IsNullOrWhiteSpace(text))
            {
                CloseResults();
                return;
            }

            _debounce = NSTimer.CreateScheduledTimer(Debounce.TotalSeconds, _ => RunSearch(text));
        }

        private void RunSearch(string text)
        {
            _search?.Cancel();
            _search = new CancellationTokenSource();
            var token = _search.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _library.SearchAsync(text, SearchLimit, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;

                    NSApplication.SharedApplication.BeginInvokeOnMainThread(() => ShowResults(text, result));
                }
                catch (OperationCanceledException)
                {
                    // A newer keystroke won; nothing to report.
                }
                catch (Exception ex)
                {
                    NSApplication.SharedApplication.BeginInvokeOnMainThread(
                        () => ShowResultsMessage(ex.Message));
                }
            }, token);
        }

        private void ShowResults(string term, SearchResult result)
        {
            if (result.Error is { } error)
            {
                ShowResultsMessage(error);
                return;
            }

            if (result.Tracks.Count == 0 && result.Collections.Count == 0)
            {
                ShowResultsMessage($"Nothing found for “{term}”.");
                return;
            }

            _results.Show(result.Tracks, result.Collections);
            _results.OrderFrontRegardless();
            Layout();
        }

        private void ShowResultsMessage(string message)
        {
            _results.ShowMessage(message);
            _results.OrderFrontRegardless();
            Layout();
        }

        private void CloseResults()
        {
            _search?.Cancel();
            _debounce?.Invalidate();
            _debounce = null;

            _results.OrderOut(null);
            Layout();
        }

        public void Dispose()
        {
            _debounce?.Invalidate();
            _search?.Cancel();
            _search?.Dispose();

            _results.Close();
            _searchBar.Close();
        }
    }
}
