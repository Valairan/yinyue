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
        private readonly QueuePanel _queue;

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

            _searchBar = new SearchBarPanel(config);
            _results = new SearchResultsPanel(config);
            _queue = new QueuePanel(config, playback);

            _message = new ToastPanel(config, ToastRole.Message);
            _hold = new ToastPanel(config, ToastRole.Hold);

            _searchBar.QueryChanged += (_, text) => OnQueryChanged(text);

            // Child windows follow the parent, which is what makes the stack hold together.
            _applet.AddChildWindow(_searchBar, NSWindowOrderingMode.Above);
            _applet.AddChildWindow(_results, NSWindowOrderingMode.Above);
            _applet.AddChildWindow(_queue, NSWindowOrderingMode.Above);

            // The toasts are deliberately NOT children: a child window is hidden with its
            // parent, and a toast exists to be seen while the overlay is hidden. They place
            // themselves in their reserved rows when shown.

            // The stack owns its relationship to the applet rather than having the delegate
            // wire it: a stack that has not been laid out sits at the window origin, and
            // making that someone else's job is how it ends up unlaid in one path and not
            // the other.
            _applet.Shown += (_, _) => Show();
            _applet.Hidden += (_, _) => Hide();

            // Input anywhere in the stack counts as using the overlay.
            foreach (var (_, panel) in PanelsForTest)
                if (panel is StackedPanel stacked)
                    stacked.Interacted += (_, _) => _applet.RestartAutoHide();

            _results.OrderOut(null);
            _queue.OrderOut(null);
            Layout();
        }

        public SearchBarPanel SearchBar => _searchBar;

        /// <summary>
        /// Writes to the applet's status line. A hook rather than a reference to the applet,
        /// because the stack is about the panels around it and should not reach inside it.
        /// </summary>
        public Action<string>? Status { get; set; }

        private void ShowStatus(string message) => Status?.Invoke(message);

        /// <summary>Every surface in the stack, for the alignment checks in the suite.</summary>
        public IReadOnlyList<(string Name, NSWindow Panel)> PanelsForTest => new (string, NSWindow)[]
        {
            ("search bar", _searchBar),
            ("results", _results),
            ("queue", _queue),
            ("toast", _message),
            ("hold", _hold),
        };

        /// <summary>
        /// Accumulates outward from the applet over an ordered list, so each panel clears
        /// everything between it and the applet and a closed panel leaves no hole. Offsets
        /// come from each panel's own height, because they grow and shrink with their
        /// contents — a results panel showing one row and one showing seven push the queue up
        /// by different amounts.
        /// </summary>
        public void Layout()
        {
            var anchor = _applet.Frame;

            // Upward: search bar, then results, then the queue beyond them.
            double y = anchor.Y + anchor.Height + OverlayMetrics.SideGap;

            foreach (var panel in new NSWindow[] { _searchBar, _results, _queue })
            {
                // Every panel is placed, including hidden ones: a panel that has never been
                // positioned sits at the screen origin and flashes there for a frame when it
                // opens. Only a visible panel advances the offset, so a closed one leaves no
                // hole in the stack.
                panel.SetFrameOrigin(new CGPoint(anchor.X, y));

                if (panel.IsVisible) y += panel.Frame.Height + OverlayMetrics.SideGap;
            }

            // The toast rows are not laid out here. The space below the applet is reserved
            // for them by the bottom-anchor lift in PositionApplet, but the windows place
            // themselves when shown — they have to work with no applet on screen at all.
        }

        /// <summary>Re-applies the background tint across the whole stack.</summary>
        public void ApplyBackgroundOpacity()
        {
            _applet.ApplyBackgroundOpacity();

            foreach (var (_, panel) in PanelsForTest)
                if (panel is StackedPanel stacked)
                    stacked.ApplyBackgroundOpacity();
        }

        public void Show()
        {
            _searchBar.OrderFrontRegardless();
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
            _searchBar.OrderOut(null);
            _results.OrderOut(null);
            _queue.OrderOut(null);
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
            CloseQueueOpenedBySearch();
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
            if (_queue.IsVisible)
            {
                // Down off the bottom of a queue the search opened returns to typing, so the
                // term can be edited without reaching for the mouse.
                if (delta > 0 && _queueOpenedBySearch && _queue.IsAtLast)
                {
                    FocusSearch();
                    return;
                }

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

        public bool QueueIsOpen => _queue.IsVisible;

        public void ToggleQueue()
        {
            if (_queue.IsVisible)
            {
                // Closing commits rather than reverts: the moves are already applied, and
                // undoing them behind a closed panel would be a surprise.
                _queue.OrderOut(null);
            }
            else
            {
                _queue.Open();
            }

            Layout();
        }

        public void GrabQueueEntry() => _queue.ToggleGrab();

        /// <summary>
        /// Enter on a queue row jumps to it. A queue the search opened closes with the
        /// search; one the user had open stays, because closing it would undo something they
        /// did rather than something the search did.
        /// </summary>
        public void JumpToQueueSelection()
        {
            _queue.JumpToSelected();

            if (!_queueOpenedBySearch) return;

            _searchBar.Clear();
            CloseQueueOpenedBySearch();
        }

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
        /// <summary>What produced the rows now showing, so Enter can act on them correctly.</summary>
        private SearchQuery _resultsQuery = SearchQuery.Parse(string.Empty);

        public void PlaySelected()
        {
            // A hit from queue: is already queued, so Enter JUMPS to it. Rebuilding the queue
            // from the results would throw away everything that did not match the search.
            if (_resultsQuery.Target == SearchTarget.Queue && _results.Selected is Track queued)
            {
                int position = _playback.PlayOrder.ToList().IndexOf(queued);
                CloseResults();

                if (position >= 0) _ = _playback.JumpToAsync(position);
                return;
            }

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

            // queue: does not list its hits. It brings the QUEUE up with one entry
            // highlighted, because listing them means reading a second list to find something
            // already visible in the first. In memory, so no debounce — it runs on every
            // keystroke.
            if (parsed.Target == SearchTarget.Queue)
            {
                SearchTheQueue(parsed);
                return;
            }

            _debounce = NSTimer.CreateScheduledTimer(Debounce.TotalSeconds, _ => RunSearch(text));
        }

        /// <summary>
        /// Opens the queue and highlights the entry the term most plausibly means.
        ///
        /// The ranking is <see cref="QueueSearch.BestMatch"/> in Core, so both apps agree
        /// about which row a term picks out: title over artist over album, exact over leading
        /// over containing, ties to queue order.
        /// </summary>
        private void SearchTheQueue(SearchQuery query)
        {
            CloseResults();

            var order = _playback.PlayOrder;
            if (order.Count == 0)
            {
                ShowStatus("The queue is empty");
                return;
            }

            if (!_queue.IsVisible)
            {
                _queue.Open();
                _queueOpenedBySearch = true;
                Layout();
            }

            if (query.IsEmpty)
            {
                // "queue:" alone opens the queue at the playing track and says what to do next.
                _queue.Select(Math.Max(0, _playback.CurrentOrderPosition));
                ShowStatus("Type to find a queued track · ↑ moves onto it");
                return;
            }

            int best = QueueSearch.BestMatch(order, query.Term);
            if (best < 0)
            {
                _queue.Select(-1);
                ShowStatus($"Nothing queued matches “{query.Term}”");
                return;
            }

            _queue.Select(best);
            ShowStatus($"↑ moves onto “{order[best].Title}” · Enter jumps to it");
        }

        /// <summary>
        /// A queue the search opened closes with the search; one the user opened stays.
        /// </summary>
        private bool _queueOpenedBySearch;

        public void CloseQueueOpenedBySearch()
        {
            if (!_queueOpenedBySearch) return;

            _queueOpenedBySearch = false;
            _queue.OrderOut(null);
            Layout();
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

                    NSApplication.SharedApplication.BeginInvokeOnMainThread(
                        () => ShowResults(SearchQuery.Parse(text), result));
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

        private void ShowResults(SearchQuery query, SearchResult result)
        {
            _resultsQuery = query;

            if (result.Error is { } error)
            {
                ShowResultsMessage(error);
                return;
            }

            if (result.Tracks.Count == 0 && result.Collections.Count == 0)
            {
                // A scoped search says WHICH kind found nothing, so the term need not be
                // retyped to find out.
                ShowResultsMessage(query.IsScoped
                    ? $"No {query.Noun} match “{query.Term}”."
                    : $"Nothing found for “{query.Term}”.");
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
            _queue.OrderOut(null);
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
