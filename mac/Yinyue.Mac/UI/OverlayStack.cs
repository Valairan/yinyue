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
        private readonly SearchBarSection _searchBar;
        private readonly SearchResultsSection _results;
        private readonly QueueSection _queue;

        // Two toasts, not one: a track change can land while a hold is in progress and one
        // window cannot occupy two rows at once.
        private readonly ToastPanel _message;
        private readonly ToastPanel _hold;

        private NSTimer? _debounce;
        private CancellationTokenSource? _search;

        public OverlayStack(OverlayConfig config, MusicLibrary library, PlaybackService playback,
                            OverlayPanel applet)
        {
            _config = config;
            _library = library;
            _playback = playback;
            _applet = applet;

            _searchBar = new SearchBarSection(config);
            _results = new SearchResultsSection(config);
            _queue = new QueueSection(config, playback);

            _message = new ToastPanel(config, ToastRole.Message);
            _hold = new ToastPanel(config, ToastRole.Hold);

            _searchBar.QueryChanged += (_, text) => OnQueryChanged(text);

            // Sections, not child windows. One window means one glass view behind the whole
            // stack, which is the only way adjacent glass merges into a single material.
            foreach (var section in Sections)
            {
                _applet.StackRoot.AddSubview(section);
                section.HeightChanged += (_, _) => Layout();
                section.Interacted += (_, _) => _applet.RestartAutoHide();
            }

            // The toasts stay separate windows: a toast exists to be seen while the overlay
            // is hidden, and anything inside the overlay's window goes with it.

            // The stack owns its relationship to the applet rather than having the delegate
            // wire it: a stack that has not been laid out sits at the window origin, and
            // making that someone else's job is how it ends up unlaid in one path and not
            // the other.
            _applet.Shown += (_, _) => Show();
            _applet.Hidden += (_, _) => Hide();

            _results.Shown = false;
            _queue.Shown = false;
            Layout();
        }

        public SearchBarSection SearchBar => _searchBar;

        /// <summary>Every surface in the stack, for the alignment checks in the suite.</summary>
        /// <summary>Every section in the stack, for the suite.</summary>
        public IReadOnlyList<(string Name, OverlaySection Section)> SectionsForTest =>
            new (string, OverlaySection)[]
            {
                ("search bar", _searchBar),
                ("results", _results),
                ("queue", _queue),
            };

        /// <summary>The toasts, which remain separate windows.</summary>
        public IReadOnlyList<(string Name, NSWindow Panel)> ToastsForTest => new (string, NSWindow)[]
        {
            ("toast", _message),
            ("hold", _hold),
        };

        /// <summary>
        /// Stacks the sections bottom-up inside the window and sizes the window to fit.
        ///
        /// The applet sits at the bottom and keeps its place; the window grows upward as
        /// panels open, which is what a bottom anchor already expects. A hidden section is
        /// skipped rather than removed, so it leaves no gap and keeps its contents.
        /// </summary>
        public void Layout()
        {
            double y = 0;

            // The applet's own view is placed by the window; the sections stack above it.
            double appletHeight = OverlayMetrics.AppletHeight;
            _appletView?.SetFrameOrigin(new CGPoint(0, 0));
            y += appletHeight;

            foreach (var section in Sections)
            {
                if (!section.Shown) continue;

                y += OverlayMetrics.SideGap;
                section.Frame = new CGRect(0, y, OverlayMetrics.PanelWidth, section.Frame.Height);
                y += section.Frame.Height;
            }

            _applet.SetStackHeight(y);
        }

        /// <summary>Bottom-up: the search bar sits on the applet, the results above it, the queue beyond.</summary>
        private IEnumerable<OverlaySection> Sections
        {
            get
            {
                yield return _searchBar;
                yield return _results;
                yield return _queue;
            }
        }

        private AppletView? _appletView;

        /// <summary>
        /// The applet is a section like the others — it carries the same background, border
        /// and tint — but it is the anchor rather than part of the stack, so it is laid out
        /// separately and never hidden.
        /// </summary>
        public void SetAppletView(AppletView view)
        {
            _appletView = view;
            view.Interacted += (_, _) => _applet.RestartAutoHide();
        }

        /// <summary>Re-applies the background tint across the whole stack.</summary>
        public void ApplyBackgroundOpacity()
        {
            _applet.ApplyBackgroundOpacity();

            _appletView?.ApplyBackgroundOpacity();
            foreach (var section in Sections) section.ApplyBackgroundOpacity();

            _message.ApplyBackgroundOpacity();
            _hold.ApplyBackgroundOpacity();
        }

        public void Show()
        {
            _searchBar.Shown = true;
            Layout();
        }

        /// <summary>
        /// Hiding the overlay takes the whole stack with it — the queue included, which it
        /// did not at first. Windows closes all three popups in ClosePanels for the same
        /// reason: a panel left behind by a dismissed overlay is stranded, with no window to
        /// belong to and no keys routed to it.
        /// </summary>
        public void Hide()
        {
            _searchBar.Shown = false;
            _results.Shown = false;
            _queue.Shown = false;
        }

        /// <summary>Puts the caret in the box. The box was already there; nothing is revealed.</summary>
        public void FocusSearch()
        {
            _searchBar.Shown = true;
            Layout();
            _searchBar.FocusBox();
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

        /// <summary>
        /// The arrows belong to whichever list is in front: the queue if it is open, the
        /// results otherwise. Reserved for navigation and nothing else — they used to seek
        /// and change volume on Windows, which fought with the lists and made the panel
        /// unpredictable to move around.
        /// </summary>
        public void MoveSelection(int delta)
        {
            if (_queue.Shown)
            {
                _queue.MoveSelection(delta);
                return;
            }

            // Arrowing off the top returns to typing rather than sticking at the first row.
            // The caret goes to the end, so the term is still there to be edited.
            if (delta < 0 && _results.IsAtFirst)
            {
                FocusSearch();
                return;
            }

            _results.MoveSelection(delta);
        }

        public bool QueueIsOpen => _queue.Shown;

        public void ToggleQueue()
        {
            if (_queue.Shown)
            {
                // Closing commits rather than reverts: the moves are already applied, and
                // undoing them behind a closed panel would be a surprise.
                _queue.Shown = false;
            }
            else
            {
                _queue.Open();
            }

            Layout();
        }

        public void GrabQueueEntry() => _queue.ToggleGrab();

        public void JumpToQueueSelection() => _queue.JumpToSelected();

        /// <summary>Returns true if a held entry was put back, so Escape stops there.</summary>
        public bool CancelQueueGrab()
        {
            if (!_queue.IsHolding) return false;

            _queue.CancelGrab();
            return true;
        }

        public void RemoveQueueEntry() => _queue.RemoveSelected();

        public Task ClearQueueAsync() => _playback.ClearQueueAsync();

        /// <summary>
        /// Queues the highlighted result at the end, or plays it next when held. A collection
        /// row queues all of it.
        /// </summary>
        public void AddSelectedToQueue(bool next)
        {
            switch (_results.Selected)
            {
                case Track track:
                    if (next) _playback.InsertNext(track);
                    else _playback.Enqueue(track);
                    break;

                case TrackCollection collection:
                    _ = QueueCollectionAsync(collection, next);
                    break;
            }
        }

        private async Task QueueCollectionAsync(TrackCollection collection, bool next)
        {
            var result = await _library.GetCollectionTracksAsync(collection, 1000, CancellationToken.None)
                                       .ConfigureAwait(false);

            if (result.Tracks.Count == 0) return;

            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                if (!next)
                {
                    _playback.EnqueueRange(result.Tracks);
                    return;
                }

                // Walked backwards, because each insert lands directly after the current
                // track — forwards would reverse the playlist.
                foreach (var track in result.Tracks.Reverse()) _playback.InsertNext(track);
            });
        }

        // ---------------------------------------------------------------- toasts

        /// <summary>
        /// The single entry point for transient feedback, and it shows <b>only while the
        /// overlay is hidden</b>: with the overlay open the applet already says the same
        /// thing, and two readouts of one change is noise.
        /// </summary>
        public void Toast(string message, string? icon = null, double? level = null,
                          bool evenWhileOverlayShown = false)
        {
            if (!evenWhileOverlayShown && _applet.IsVisible) return;

            _message.Show(message, icon, level);
            _message.Dismiss(ToastPanel.VisibleFor);
        }

        public void ShowHold(string message, double progress) => _hold.ShowHold(message, progress);

        public void EndHold() => _hold.EndHold();

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

            // A prefix with nothing after it is intent without a subject. Searching for the
            // empty string would return an arbitrary slice of the whole library, so the
            // overlay says what the prefix does instead and waits for a term.
            var parsed = SearchQuery.Parse(text);
            if (parsed.IsScoped && parsed.IsEmpty)
            {
                ShowResultsMessage($"Searching {parsed.Noun} — type something to look for.");
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
            _results.Shown = true;
            Layout();
        }

        private void ShowResultsMessage(string message)
        {
            _results.ShowMessage(message);
            _results.Shown = true;
            Layout();
        }

        private void CloseResults()
        {
            _search?.Cancel();
            _debounce?.Invalidate();
            _debounce = null;

            _results.Shown = false;
            _queue.Shown = false;
            Layout();
        }

        public void Dispose()
        {
            _debounce?.Invalidate();
            _search?.Cancel();
            _search?.Dispose();

            _results.RemoveFromSuperview();
            _searchBar.RemoveFromSuperview();
            _queue.RemoveFromSuperview();
        }
    }
}
