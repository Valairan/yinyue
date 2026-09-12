using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Yinyue.Models;
using Yinyue.Services;

// UseWindowsForms (on for the tray NotifyIcon) adds implicit global usings for
// System.Windows.Forms and System.Drawing, which collide with the WPF types of the same
// name. Alias the WPF ones explicitly so this file is unambiguous.
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonEventHandler = System.Windows.Input.MouseButtonEventHandler;

namespace Yinyue
{
    /// <summary>
    /// The summon overlay. A view over PlaybackService — it owns no playback state of its
    /// own, because it spends most of its life hidden while playback continues.
    /// </summary>
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int SearchDebounceMs = 250;
        private const int SearchLimit = 20;

        /// <summary>Ceiling on how many favourites one shuffle will pull down.</summary>
        private const int FavoritesCap = 1000;

        private readonly ConfigService _config;
        private readonly MusicLibrary _library;
        private readonly PlaybackService _playback;
        private readonly Func<SettingsWindow> _settingsFactory;

        /// <summary>
        /// Search rows: a <see cref="Track"/> or a <see cref="TrackCollection"/>. They share the
        /// row template through matching property names, and the code that acts on a row
        /// switches on the type — playing a playlist means expanding it first.
        /// </summary>
        private readonly ObservableCollection<object> _searchResults = new();

        /// <summary>Ceiling on how much of a collection is pulled into the queue.</summary>
        private const int CollectionTrackCap = 1000;

        /// <summary>
        /// The query the current results came from. Kept because acting on a row depends on
        /// it: a hit from <c>queue:</c> is already in the queue, so Enter should jump to it
        /// rather than rebuild the queue around it.
        /// </summary>
        private SearchQuery _resultsQuery = SearchQuery.Parse(string.Empty);
        private readonly ObservableCollection<QueueEntry> _queueEntries = new();

        private CancellationTokenSource? _searchCts;
        private SettingsWindow? _settingsWindow;
        private BitmapImage? _placeholderArt;

        private bool _isUserDraggingSlider;
        private bool _favoritesLoading;

        private System.Windows.Threading.DispatcherTimer? _holdTimer;
        private HotkeyBinding _holdBinding;
        private DateTime _holdStart;
        private bool _holdFired;
        private string _holdLabel = string.Empty;
        private Action? _holdTapAction;
        private Action? _holdHoldAction;
        private bool _holdRepeats;

        /// <summary>
        /// Tracks intent separately from IsVisible: during deactivation IsVisible can still
        /// be true, which would make a toggle hotkey press get swallowed.
        /// </summary>
        private bool _isShown;

        /// <summary>Invalidates a pending fade-out when the overlay is summoned again.</summary>
        private int _hideGeneration;

        /// <summary>Fixed at construction; see OverlayConfig.Animations.</summary>
        private readonly bool _animate;

        private readonly System.Windows.Threading.DispatcherTimer _autoHideTimer = new();

        /// <summary>Read at use rather than cached, so the setting takes effect at once.</summary>
        private TimeSpan FadeDuration =>
            TimeSpan.FromMilliseconds(_config.Current.Overlay.AnimationMilliseconds);

        /// <summary>True while the overlay is on screen, so callers can tell whether the
        /// status line is a usable feedback surface.</summary>
        public bool IsOverlayShown => _isShown;

        /// <summary>
        /// Progress of a pending tap-or-hold, as a label and a 0-1 fraction. Raised rather
        /// than written to the status line because the overlay is often hidden when a hold
        /// begins — play/pause deliberately does not summon it.
        /// </summary>
        public event Action<string, double>? HoldProgressed;

        public event Action? HoldFinished;

        public MainWindow(ConfigService config,
                          MusicLibrary library,
                          PlaybackService playback,
                          Func<SettingsWindow> settingsFactory)
        {
            InitializeComponent();

            _config = config;
            _library = library;
            _playback = playback;
            _settingsFactory = settingsFactory;

            // Before the handle exists: AllowsTransparency cannot be changed afterwards.
            _animate = config.Current.Overlay.Animations;
            if (!_animate)
                WindowStyling.MakeOpaque(this, ThemeBrush("BaseBrush"), RootBorder);

            LstSearchResults.ItemsSource = _searchResults;
            LstQueue.ItemsSource = _queueEntries;
            _placeholderArt = TryLoadPlaceholder();

            MouseDown += MainWindow_MouseDown;

            // Any sign of life restarts the idle countdown.
            PreviewMouseMove += (_, _) => RestartAutoHide();
            PreviewMouseDown += (_, _) => RestartAutoHide();
            PreviewKeyUp += (_, _) => RestartAutoHide();

            _autoHideTimer.Tick += (_, _) =>
            {
                _autoHideTimer.Stop();
                HideOverlay();
            };
            Deactivated += MainWindow_Deactivated;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            PreviewTextInput += MainWindow_PreviewTextInput;

            BtnPrevious.Click += BtnPrevious_Click;
            BtnPlayPause.Click += BtnPlayPause_Click;
            BtnNext.Click += BtnNext_Click;
            BtnShuffle.Click += BtnShuffle_Click;
            BtnLoop.Click += BtnLoop_Click;
            BtnFavorite.Click += BtnFavorite_Click;

            SliderProgress.AddHandler(UIElement.MouseDownEvent, new MouseButtonEventHandler(Slider_MouseDown), true);
            SliderProgress.AddHandler(UIElement.MouseUpEvent, new MouseButtonEventHandler(Slider_MouseUp), true);

            SearchTextBox.TextChanged += SearchTextBox_TextChanged;

            // Offline mode can change from the overlay button, its global hotkey, or
            // the settings window. ConfigChanged is the one signal all three share.
            _config.ConfigChanged += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                ApplyShortcutHints();
                ApplyBackgroundOpacity();
            });
            ApplyShortcutHints();
            ApplyBackgroundOpacity();

            _playback.TrackChanged += OnTrackChanged;
            _playback.PlayingStateChanged += OnPlayingStateChanged;
            _playback.ProgressUpdated += OnProgressUpdated;
            _playback.PlaybackFailed += OnPlaybackFailed;

            // Volume can move from the global hotkeys while the overlay is hidden, so read
            // it from the event rather than from whatever changed it.
            _playback.VolumeChanged += (_, volume) => Dispatcher.BeginInvoke(() =>
            {
                TxtStatus.Text = _playback.IsMuted || volume <= 0.0001
                    ? "Muted"
                    : $"Volume {volume * 100:F0}%";
            });

            // Shuffle and loop can both change from global hotkeys while the overlay is
            // hidden, so the buttons follow the service rather than the click that caused it.
            _playback.ModesChanged += (_, _) => Dispatcher.BeginInvoke(OnModesChanged);
            UpdateShuffleButton();
            UpdateLoopButton();

            // Keep the queue view honest while it is open; skip the work when it is not.
            QueuePopup.Closed += (_, _) => CommitQueueGrab();

            // The queue sits above the search panel, so it has to move whenever the search
            // panel appears, disappears, or changes height as results come and go.
            SearchBarPanel.SizeChanged += (_, _) => UpdatePanelStack();
            SearchPanel.SizeChanged += (_, _) => UpdatePanelStack();
            QueuePanel.SizeChanged += (_, _) => UpdatePanelStack();
            SearchPopup.Opened += (_, _) => UpdatePanelStack();
            SearchPopup.Closed += (_, _) => UpdatePanelStack();
            QueuePopup.Opened += (_, _) => UpdatePanelStack();
            SearchBarPopup.Opened += (_, _) => UpdatePanelStack();

            // A Popup is placed when it opens and does not follow its window afterwards, so
            // moving the overlay — a changed anchor, a different monitor — would strand the
            // panels where the overlay used to be.
            LocationChanged += (_, _) => ReplacePanels();

            _playback.QueueChanged += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (QueuePopup.IsOpen) RefreshQueue();
            });
        }

        #region Window lifetime

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Tool-window style keeps the overlay out of alt-tab. ShowInTaskbar="False"
            // alone does not reliably do that.
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            WindowStyling.MakeToolWindow(hwnd);

            // With transparency on, the Border draws the corners. Without it, the Border
            // has been squared off and DWM does them instead.
            if (!_animate) WindowStyling.ApplyRoundedCorners(hwnd);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (_isShown) OverlayPositioner.PositionApplet(this, _config.Current.Overlay);
        }

        /// <summary>
        /// Shows the overlay at its configured anchor with the keyboard already where the
        /// user expects it. Never closes — the HWND must stay stable because global
        /// hotkeys and SMTC are bound to it.
        /// </summary>
        public void ShowOverlay()
        {
            OverlayPositioner.PositionApplet(this, _config.Current.Overlay);

            // Start invisible so the window is never seen at full opacity before the fade.
            if (_animate && !_isShown)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
            }

            _isShown = true;
            Show();
            Activate();

            // The bar belongs to the overlay: on screen for exactly as long as it is.
            SearchBarPopup.IsOpen = true;
            UpdatePanelStack();

            // Supersedes any fade-out in flight, picking up from wherever it reached.
            _hideGeneration++;

            if (_animate)
                BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(FadeDuration)));

            RestartAutoHide();

            // A hidden Topmost window does not reliably take focus on Show(): Windows
            // restricts foreground activation. Activate() alone loses the race often
            // enough that the user ends up typing into whatever was behind the overlay.
            SetForegroundWindow(new WindowInteropHelper(this).Handle);

            if (_playback.CurrentTrack == null && !HasConfiguredSource())
            {
                // Nothing to search yet. Opening into an empty search box would just look
                // broken, so point at the cog instead.
                ShowFirstRunHint();
            }
            else if (_playback.CurrentTrack == null)
            {
                // Open straight into search when there is nothing to look at, so the
                // summon hotkey behaves like PowerToys Run. Otherwise show now-playing;
                // typing any character from there switches to search anyway.
                OpenSearch();
            }
            else
            {
                Focus();
            }
        }

        private void ShowFirstRunHint()
        {
            CloseSearch();
            TxtStatus.Text = "Setup needed";
            TxtSongTitle.Text = "No music yet";
            TxtArtistName.Text = "Click ⚙ to add a folder or a Jellyfin server";
            Focus();
        }

        public void HideOverlay()
        {
            if (!_isShown) return;

            _isShown = false;
            _autoHideTimer.Stop();

            // Popups are separate windows and would hang in the air through the fade.
            QueuePopup.IsOpen = false;
            SearchPopup.IsOpen = false;
            SearchBarPopup.IsOpen = false;

            int generation = ++_hideGeneration;

            if (!_animate)
            {
                CloseSearch();
                Hide();
                return;
            }

            var fade = new DoubleAnimation(0, new Duration(FadeDuration));
            fade.Completed += (_, _) =>
            {
                // A re-summon during the fade supersedes this. Without the check it would
                // hide the window that was just asked for.
                if (generation != _hideGeneration) return;

                // Tear down only once invisible: clearing the search box mid-fade shows.
                CloseSearch();
                Hide();
            };

            BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>
        /// Restarts the idle countdown. Called from every interaction, so the overlay only
        /// disappears when genuinely left alone.
        /// </summary>
        private void RestartAutoHide()
        {
            _autoHideTimer.Stop();

            var overlay = _config.Current.Overlay;
            if (!_isShown || !overlay.AutoHide) return;

            // Reading is not idling: an open list, or settings in front, suspends it.
            if (SearchPopup.IsOpen || QueuePopup.IsOpen) return;
            if (_settingsWindow is { IsVisible: true }) return;

            _autoHideTimer.Interval = TimeSpan.FromSeconds(overlay.AutoHideSeconds);
            _autoHideTimer.Start();
        }

        public void ToggleOverlay()
        {
            if (_isShown) HideOverlay();
            else ShowOverlay();
        }

        private void MainWindow_Deactivated(object? sender, EventArgs e)
        {
            // Settings is a separate window; losing focus to it must not hide the overlay
            // out from under the user mid-configuration.
            if (_settingsWindow is { IsVisible: true }) return;

            if (_config.Current.Overlay.HideOnFocusLoss) HideOverlay();
        }

        private void MainWindow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        #endregion

        #region Keyboard

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            RestartAutoHide();

            if (HandleTransportKey(e))
            {
                e.Handled = true;
                return;
            }

            // Safety net for Enter when focus sits on neither the search box nor the
            // results list. Events raised inside the popup never reach here, so this
            // complements the popup's own handler rather than replacing it.
            if (e.Key == Key.Enter && SearchPopup.IsOpen && _searchResults.Count > 0)
            {
                _ = PlayFromResultsAsync(Math.Max(0, LstSearchResults.SelectedIndex));
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                // The box is always on screen now, so its contents decide: a search in
                // progress is cleared, and only an already-empty box dismisses the overlay.
                if (SearchTextBox.Text.Length > 0)
                    CloseSearch();
                else
                    HideOverlay();

                e.Handled = true;
            }
        }

        /// <summary>
        /// Transport from the keyboard while the overlay has focus and the user is not
        /// typing a search.
        ///
        /// **The arrow keys are reserved for navigation** — moving through search results
        /// and queue entries. They used to seek and change volume here, which fought with
        /// the lists and made the panel feel unpredictable to move around. Volume, seeking
        /// and skipping all have global shortcuts instead.
        /// </summary>
        private bool HandleTransportKey(KeyEventArgs e)
        {
            // Focus, not visibility: the box is always there, so what matters is whether the
            // user is typing into it. Otherwise Space would never reach play/pause again.
            if (SearchTextBox.IsKeyboardFocusWithin) return false;

            if (e.Key == Key.Space)
            {
                _ = _playback.TogglePlayPauseAsync();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Typing anywhere in the overlay drops into search and keeps the character, so the
        /// user never has to reach for the lens button first.
        /// </summary>
        private void MainWindow_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (SearchTextBox.IsKeyboardFocusWithin) return;
            if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;

            OpenSearch();
            SearchTextBox.Text = e.Text;
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
            e.Handled = true;
        }

        #endregion

        #region Search

        /// <summary>Entry point for the quick-search hotkey.</summary>
        public void FocusSearch() => OpenSearch();

        /// <summary>
        /// Swaps the status line for the search box. Deliberately does NOT touch
        /// HeaderButtonsPanel: collapsing it used to hide the settings cog, and because the
        /// overlay opens into search when nothing is playing, that left a fresh install
        /// with no reachable way to configure a library.
        /// </summary>
        /// <summary>
        /// Puts the caret in the search box. The box itself is always there, so this no
        /// longer reveals anything — it only moves focus.
        /// </summary>
        public void OpenSearch()
        {
            if (!SearchBarPopup.IsOpen) SearchBarPopup.IsOpen = true;

            SearchTextBox.Focus();
            Keyboard.Focus(SearchTextBox);
            UpdateSearchPlaceholder();
            RestartAutoHide();
        }

        /// <summary>
        /// Clears the search and puts the results away. The bar stays: it is part of the
        /// overlay now, not something that was opened.
        /// </summary>
        private void CloseSearch()
        {
            _searchCts?.Cancel();
            SearchTextBox.Clear();
            SearchPopup.IsOpen = false;
            _searchResults.Clear();
            ShowSearchMessage(null);
            UpdateSearchPlaceholder();
            RestartAutoHide();
        }

        /// <summary>
        /// Shows the placeholder only while the box is open and empty.
        ///
        /// It exists to teach the prefixes, which have nowhere else to be discovered: the
        /// search box is the only place they work and the only place anyone looks for them.
        /// </summary>
        private void UpdateSearchPlaceholder()
        {
            TxtSearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// Shows an inline note in the results popup. The status line is occupied by the
        /// search box while searching, so messages have nowhere else to go.
        /// </summary>
        private void ShowSearchMessage(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                TxtSearchMessage.Visibility = Visibility.Collapsed;
                return;
            }

            TxtSearchMessage.Text = message;
            TxtSearchMessage.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// An empty result set has several very different causes, and "No matches" is only
        /// one of them. Saying which avoids a configured-but-suppressed server looking like
        /// a broken integration.
        /// </summary>
        private string NoResultsMessage(SearchQuery? query = null)
        {
            if (!HasConfiguredSource())
                return "No music configured yet — use ⚙ to add a library folder or a Jellyfin server.";

            if (_config.Current.OfflineMode && _config.Current.Jellyfin.IsConfigured)
                return "No local matches. Offline mode is on, so Jellyfin is not being searched — press ✈ or "
                       + _config.Current.Hotkeys.For(HotkeyActions.OfflineMode).Keys
                       + " to go back online.";

            // A narrowed search finding nothing is ambiguous — no matches at all, or none of
            // that kind? Saying which avoids the user retyping the same term to find out.
            if (query is { IsScoped: true })
                return $"No {query.Noun} match \"{query.Term}\". Drop the \"{query.Prefix}:\" to search everything.";

            return "No matches.";
        }

        /// <summary>
        /// Matches the queue in memory. No source is consulted — the tracks are already
        /// here, and asking a server about them would be both slower and wrong.
        /// </summary>
        private SearchResult SearchTheQueue(SearchQuery query)
        {
            var matches = _playback.PlayOrder
                .Where(t => Matches(t, query.Term))
                .Take(SearchLimit)
                .ToList();

            return SearchResult.Ok(matches);
        }

        private static bool Matches(Track track, string term) =>
            track.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            track.Artist.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            track.Album.Contains(term, StringComparison.OrdinalIgnoreCase);

        private bool HasConfiguredSource() =>
            _config.Current.Library.Folders.Count > 0 || _config.Current.Jellyfin.IsConfigured;

        private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query = SearchQuery.Parse(SearchTextBox.Text);
            UpdateSearchPlaceholder();

            // Supersede the previous keystroke's search rather than racing it — otherwise
            // out-of-order responses make results flicker while typing.
            _searchCts?.Cancel();

            if (query.IsEmpty)
            {
                _searchResults.Clear();

                // Having typed "playlist:" and nothing else, the user is mid-thought. Say
                // what the prefix does rather than searching for the empty string, which
                // would return an arbitrary slice of the whole library.
                if (query.IsScoped)
                {
                    ShowSearchMessage($"Searching {query.Noun} only — type what to look for.");
                    SearchPopup.IsOpen = true;
                }
                else
                {
                    ShowSearchMessage(null);
                    SearchPopup.IsOpen = false;
                }

                return;
            }

            var cts = new CancellationTokenSource();
            _searchCts = cts;

            try
            {
                await Task.Delay(SearchDebounceMs, cts.Token);

                var result = query.Target == SearchTarget.Queue
                    ? SearchTheQueue(query)
                    : await _library.SearchAsync(query, SearchLimit, cts.Token);

                if (cts.Token.IsCancellationRequested) return;

                _resultsQuery = query;
                _searchResults.Clear();

                if (!result.Succeeded)
                {
                    ShowSearchMessage(result.Error ?? "Search failed.");
                    SearchPopup.IsOpen = true;
                    return;
                }

                // Collections first: they are the coarser match, and someone typing an album
                // name almost always wants the whole thing rather than one track from it.
                foreach (var collection in result.Collections) _searchResults.Add(collection);
                foreach (var track in result.Tracks) _searchResults.Add(track);

                ShowSearchMessage(_searchResults.Count > 0 ? null : NoResultsMessage(query));

                SearchPopup.IsOpen = true;
            }
            catch (OperationCanceledException)
            {
                // Replaced by a newer keystroke.
            }
            catch (Exception ex)
            {
                ShowSearchMessage($"Search error: {ex.Message}");
                SearchPopup.IsOpen = true;
            }
        }

        private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && SearchPopup.IsOpen && _searchResults.Count > 0)
            {
                LstSearchResults.Focus();
                LstSearchResults.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _searchResults.Count > 0)
            {
                // Nothing highlighted yet means "play the top hit".
                _ = PlayFromResultsAsync(Math.Max(0, LstSearchResults.SelectedIndex));
                e.Handled = true;
            }
        }

        /// <summary>
        /// Enter and Escape while the results list has focus.
        ///
        /// This handler is not optional. The list lives inside a Popup, which WPF hosts in
        /// its own HWND with its own visual tree, so key events raised in here never tunnel
        /// through MainWindow or reach the search box. Without it, arrowing down to a result
        /// and pressing Enter did nothing at all.
        /// </summary>
        private void LstSearchResults_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    if (LstSearchResults.SelectedIndex >= 0)
                    {
                        _ = PlayFromResultsAsync(LstSearchResults.SelectedIndex);
                        e.Handled = true;
                    }
                    break;

                case Key.Escape:
                    CloseSearch();
                    e.Handled = true;
                    break;

                case Key.Up when LstSearchResults.SelectedIndex == 0:
                    // Arrowing off the top returns to typing rather than sticking.
                    SearchTextBox.Focus();
                    Keyboard.Focus(SearchTextBox);
                    SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
                    e.Handled = true;
                    break;
            }
        }

        private void LstSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Selection alone must not start playback, or arrow-key navigation would play
            // every track it passes over. Enter and double-click are the commit gestures.
        }

        private void LstSearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstSearchResults.SelectedIndex >= 0)
                _ = PlayFromResultsAsync(LstSearchResults.SelectedIndex);
        }

        /// <summary>
        /// Plays the chosen result and makes the rest of the results the queue, so Next
        /// does something sensible immediately after a search.
        /// </summary>
        private async Task PlayFromResultsAsync(int index)
        {
            if (index < 0 || index >= _searchResults.Count) return;

            if (_searchResults[index] is TrackCollection collection)
            {
                await PlayCollectionAsync(collection);
                return;
            }

            // A hit from queue: is already queued. Rebuilding the queue from the results
            // would throw away everything that did not match the search.
            if (_resultsQuery.Target == SearchTarget.Queue)
            {
                int position = _playback.PlayOrder.ToList().IndexOf((Track)_searchResults[index]);
                CloseSearch();

                if (position >= 0) await _playback.JumpToAsync(position);
                return;
            }

            // Only the tracks become the queue. A playlist row cannot be played by Next, and
            // including it would leave a hole that skips silently.
            var queue = _searchResults.OfType<Track>().ToList();
            int trackIndex = queue.IndexOf((Track)_searchResults[index]);
            if (trackIndex < 0) return;

            CloseSearch();

            await _playback.PlayQueueAsync(queue, trackIndex);
        }

        /// <summary>
        /// Replaces the queue with a playlist's contents and starts it.
        ///
        /// The fetch happens before the search closes, so a slow or failed expansion leaves
        /// the user where they were rather than dropping them back to an unchanged overlay
        /// with no explanation.
        /// </summary>
        private async Task PlayCollectionAsync(TrackCollection playlist)
        {
            var tracks = await ExpandCollectionAsync(playlist);
            if (tracks == null) return;

            CloseSearch();
            await _playback.PlayQueueAsync(tracks, 0);
        }

        /// <summary>
        /// Fetches a playlist's tracks, reporting failure on the status line. Null means
        /// nothing usable came back and the caller should do nothing.
        /// </summary>
        private async Task<List<Track>?> ExpandCollectionAsync(TrackCollection playlist)
        {
            TxtStatus.Text = $"Opening \"{playlist.Title}\"…";

            try
            {
                var result = await _library.GetCollectionTracksAsync(
                    playlist, CollectionTrackCap, CancellationToken.None);

                if (!result.Succeeded)
                {
                    TxtStatus.Text = result.Error ?? $"Could not open \"{playlist.Title}\"";
                    return null;
                }

                if (result.Tracks.Count == 0)
                {
                    TxtStatus.Text = $"\"{playlist.Title}\" is empty";
                    return null;
                }

                return result.Tracks.ToList();
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Could not open \"{playlist.Title}\": {ex.Message}";
                return null;
            }
        }

        #endregion

        #region Header buttons

        private void BtnBurgerMenu_Click(object sender, RoutedEventArgs e)
        {
            if (QueuePopup.IsOpen)
            {
                QueuePopup.IsOpen = false;
                return;
            }

            RefreshQueue();
            QueuePopup.IsOpen = true;
        }

        /// <summary>
        /// Row shown in the queue list. Wraps Track rather than templating it directly so
        /// the now-playing marker and formatted duration stay out of the shared model.
        /// </summary>
        public class QueueEntry
        {
            public Track Track { get; init; } = new();
            public bool IsCurrent { get; init; }

            /// <summary>The entry the arrows are currently repositioning.</summary>
            public bool IsGrabbed { get; init; }

            public string Marker => IsCurrent ? "▶" : string.Empty;

            /// <summary>Deferred to the track so a queue row and a search row agree.</summary>
            public string DurationText => Track.DurationText;
        }

        private void RefreshQueue()
        {
            var order = _playback.PlayOrder;
            int current = _playback.CurrentOrderPosition;

            // Rebuilding the collection destroys the focused ListBoxItem, and WPF does not
            // reinstate focus when the focused element leaves the tree — the popup stays
            // open but keystrokes go nowhere. Both of these are restored at the end.
            bool hadFocus = QueuePopup.IsOpen && LstQueue.IsKeyboardFocusWithin;
            int previousSelection = LstQueue.SelectedIndex;

            _queueEntries.Clear();
            for (int i = 0; i < order.Count; i++)
            {
                _queueEntries.Add(new QueueEntry
                {
                    Track = order[i],
                    IsCurrent = i == current,
                    IsGrabbed = i == _grabbedIndex,
                });
            }

            bool empty = _queueEntries.Count == 0;
            TxtQueueEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            LstQueue.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            BtnClearQueue.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

            TxtQueueHeader.Text = empty
                ? "Queue"
                : $"Queue — {current + 1} of {_queueEntries.Count}";

            SelectInQueue(ChooseQueueSelection(previousSelection, current));

            if (hadFocus) RestoreQueueFocus();

            UpdateQueueHint();
        }

        /// <summary>
        /// Which row should be highlighted after a rebuild.
        ///
        /// The user's own position wins. Snapping back to the playing track on every refresh
        /// was actively hostile after a removal: the highlight jumped to the one entry that
        /// cannot be removed, so pressing the hotkey again did nothing. Only a selection that
        /// no longer exists falls back — and then to where the removed row was, not to the
        /// playhead, so a run of removals keeps working.
        /// </summary>
        private int ChooseQueueSelection(int previous, int current)
        {
            if (_queueEntries.Count == 0) return -1;

            // A grabbed entry owns the highlight; otherwise every move would lose it.
            if (IsGrabbing) return _grabbedIndex;

            // Nothing was selected, so this is an open rather than an update: start at the
            // track that is playing, which is what someone opening the queue wants to see.
            if (previous < 0) return current;

            // The list shrank under the selection. Staying put lands on whatever took the
            // removed row's place, which is the next thing the user would act on.
            return Math.Min(previous, _queueEntries.Count - 1);
        }

        private void SelectInQueue(int index)
        {
            if (index < 0 || index >= _queueEntries.Count) return;

            LstQueue.SelectedIndex = index;
            LstQueue.ScrollIntoView(_queueEntries[index]);
        }

        /// <summary>
        /// Puts keyboard focus back on the selected row.
        ///
        /// Deferred to Input priority because the containers are realised during layout: the
        /// item to focus does not exist yet at the point the collection is refilled.
        /// </summary>
        private void RestoreQueueFocus()
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (!QueuePopup.IsOpen || _queueEntries.Count == 0) return;

                int index = LstQueue.SelectedIndex;
                if (index < 0 || index >= _queueEntries.Count) return;

                if (LstQueue.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem row)
                    row.Focus();
                else
                    LstQueue.Focus();
            });
        }

        #region Moving a queue entry

        /// <summary>
        /// Where the grabbed entry currently sits, or -1 when nothing is grabbed.
        /// </summary>
        private int _grabbedIndex = -1;

        /// <summary>Where it sat when it was grabbed, so Escape can put it back.</summary>
        private int _grabbedOrigin = -1;

        private bool IsGrabbing => _grabbedIndex >= 0;

        /// <summary>
        /// Picks up the highlighted queue entry, or puts down the one already held.
        ///
        /// Deliberately a global hotkey rather than a key handled by the list: the queue
        /// lives in a Popup with its own visual tree, and this needs to work the moment the
        /// queue has focus without competing with the arrows that navigate it.
        /// </summary>
        public void ToggleQueueGrab()
        {
            if (IsGrabbing)
            {
                CommitQueueGrab();
                return;
            }

            if (!QueuePopup.IsOpen) ShowQueue();

            int index = LstQueue.SelectedIndex;
            if (index < 0 || index >= _queueEntries.Count)
            {
                TxtQueueHeader.Text = "Nothing to move";
                return;
            }

            _grabbedIndex = index;
            _grabbedOrigin = index;

            RefreshQueue();
            LstQueue.Focus();
        }

        /// <summary>Accepts the new position and releases the entry.</summary>
        private void CommitQueueGrab()
        {
            if (!IsGrabbing) return;

            _grabbedIndex = -1;
            _grabbedOrigin = -1;
            RefreshQueue();
        }

        /// <summary>Puts the entry back where it was picked up.</summary>
        private void CancelQueueGrab()
        {
            if (!IsGrabbing) return;

            if (_grabbedIndex != _grabbedOrigin)
                _playback.MoveTo(_grabbedIndex, _grabbedOrigin);

            _grabbedIndex = -1;
            _grabbedOrigin = -1;
            RefreshQueue();
        }

        /// <summary>
        /// Steps the grabbed entry one row. Stops at the ends rather than wrapping — a
        /// wrap would fling an entry across a long queue on one keypress.
        /// </summary>
        private void MoveGrabbed(int delta)
        {
            if (!IsGrabbing) return;

            int target = _grabbedIndex + delta;
            if (target < 0 || target >= _queueEntries.Count) return;

            if (_playback.MoveTo(_grabbedIndex, target))
            {
                _grabbedIndex = target;
                RefreshQueue();      // MoveTo also raises QueueChanged; this keeps it ordered.
            }
        }

        private void UpdateQueueHint()
        {
            // Clearing loses its mention here because the button beneath the list already
            // says "Clear queue", whereas moving an entry has no other signpost at all.
            TxtQueueHint.Text = IsGrabbing
                ? "↑↓ moves · Enter confirms · Esc cancels"
                : $"Enter plays · Del removes ·{KeyHint(HotkeyActions.GrabQueueEntry)} moves";
        }

        #endregion

        private void LstQueue_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstQueue.SelectedIndex >= 0)
                _ = _playback.JumpToAsync(LstQueue.SelectedIndex);
        }

        private void LstQueue_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            int index = LstQueue.SelectedIndex;

            // A grabbed entry takes over the arrows, Enter and Escape entirely. Anything
            // else is swallowed so a stray Delete cannot act on a queue mid-rearrangement.
            if (IsGrabbing)
            {
                switch (e.Key)
                {
                    case Key.Up: MoveGrabbed(-1); break;
                    case Key.Down: MoveGrabbed(+1); break;
                    case Key.Enter: CommitQueueGrab(); break;
                    case Key.Escape: CancelQueueGrab(); break;
                }

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && index >= 0)
            {
                _ = _playback.JumpToAsync(index);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && index >= 0)
            {
                // Refused for the playing track — RemoveAt says so by returning false.
                if (!_playback.RemoveAt(index))
                    TxtQueueHeader.Text = "Cannot remove the playing track";

                e.Handled = true;
            }
        }

        private void BtnClearQueue_Click(object sender, RoutedEventArgs e) => _ = ClearQueueAsync();

        /// <summary>
        /// Empties the queue and stops playback. Reports the count because there is no undo
        /// — the cleared queue is persisted a couple of seconds later and is then gone.
        /// </summary>
        public async Task ClearQueueAsync()
        {
            int count = _playback.PlayOrder.Count;

            if (count == 0)
            {
                TxtStatus.Text = "The queue is already empty";
                return;
            }

            await _playback.ClearQueueAsync();

            QueuePopup.IsOpen = false;
            TxtStatus.Text = $"Cleared {count} track(s)";
        }

        private void BtnOfflineToggle_Click(object sender, RoutedEventArgs e)
        {
            _config.Current.OfflineMode = !_config.Current.OfflineMode;
            _config.Save();   // ConfigChanged repaints the button

            TxtStatus.Text = _config.Current.OfflineMode ? "Offline Mode" : "Yinyue";
        }

        /// <summary>
        /// Paints the offline indicator from persisted config. This has to run at startup,
        /// not only on click: offline mode survives restarts, and an app that silently
        /// suppresses every remote source while looking completely normal is indistinguishable
        /// from a broken Jellyfin integration.
        /// </summary>
        /// <summary>
        /// Tints the panel to the configured opacity. A no-op without transparency, where
        /// the alpha would composite against the window's own opaque background.
        /// </summary>
        private void ApplyBackgroundOpacity()
        {
            if (!AllowsTransparency) return;

            var baseColor = ((SolidColorBrush)FindResource("BaseBrush")).Color;
            var tint = WindowStyling.Translucent(baseColor, _config.Current.Overlay.BackgroundOpacity);

            // The panels read as parts of the applet, so they take the same tint. A separate
            // brush per element rather than a shared one because each is frozen.
            RootBorder.Background = tint;
            SearchBarPanel.Background = tint;
            SearchPanel.Background = tint;
            QueuePanel.Background = tint;
        }

        /// <summary>Breathing room between the applet and the panels stacked above it.</summary>
        private const double PanelGap = 6;

        /// <summary>
        /// A panel's height, measuring it first if it has never been laid out.
        ///
        /// A Popup measures its child only once it opens, so the first placement would
        /// otherwise be computed from a height of zero and corrected a moment later — and
        /// **repositioning a popup that is already open can dismiss it**, which showed up as
        /// the queue flashing open and shutting again. Measuring up front means the first
        /// placement is the right one.
        /// </summary>
        private static double PanelHeight(FrameworkElement panel, double width)
        {
            if (panel.ActualHeight > 0) return panel.ActualHeight;

            panel.Measure(new System.Windows.Size(double.IsNaN(width) ? double.PositiveInfinity : width,
                                   double.PositiveInfinity));

            return panel.DesiredSize.Height;
        }

        /// <summary>
        /// Stacks the open panels upwards from the applet: search immediately above it, the
        /// queue above that.
        ///
        /// <c>Placement=Relative</c> rather than <c>Top</c>, and the offsets worked out here
        /// rather than left to WPF. <c>Top</c> looks like the obvious choice but **flips the
        /// panel below the target when there is no room above** — measured: with the overlay
        /// near the top of the screen both panels landed underneath the applet, overlapping
        /// it. Relative placement honours the offset it is given. Off the top of a small
        /// screen is the lesser fault, and only reachable with a top anchor and a long queue.
        ///
        /// Heights are measured, not assumed: both panels grow and shrink with their
        /// contents, which is why each one's SizeChanged runs this again.
        ///
        /// Offsets are negative because Relative measures down from the target's top-left,
        /// and everything here goes above it.
        /// </summary>
        /// <summary>
        /// Forces the open panels to work out where they belong again.
        ///
        /// Nudging the offset is the lever WPF offers: a Popup re-places when its placement
        /// properties change, and nothing else asks it to. Restoring the value immediately
        /// leaves the arithmetic in UpdatePanelStack as the only thing deciding position.
        /// </summary>
        private void ReplacePanels()
        {
            UpdatePanelStack();

            foreach (var popup in new[] { SearchBarPopup, SearchPopup, QueuePopup })
            {
                if (!popup.IsOpen) continue;

                double offset = popup.VerticalOffset;
                popup.VerticalOffset = offset + 1;
                popup.VerticalOffset = offset;
            }
        }

        private void UpdatePanelStack()
        {
            // Outward from the applet: the search bar, then its results, then the queue.
            var stack = new (System.Windows.Controls.Primitives.Popup Popup, Border Panel)[]
            {
                (SearchBarPopup, SearchBarPanel),
                (SearchPopup, SearchPanel),
                (QueuePopup, QueuePanel),
            };

            double total = stack
                .Where(entry => entry.Popup.IsOpen)
                .Sum(entry => PanelGap + PanelHeight(entry.Panel, entry.Popup.Width));

            // Relative placement honours the offset it is given, which is the point — but it
            // will also happily put a panel off the screen. The overlay's anchor is the
            // user's choice, so a top-anchored overlay has nothing above it to stack into.
            bool roomAbove = Top - total >= SystemParameters.WorkArea.Top;

            // Accumulated, so each panel clears everything between it and the applet however
            // tall those turn out to be. Closed panels take no space and leave no hole.
            double offset = roomAbove ? 0 : RootBorder.ActualHeight;

            foreach (var (popup, panel) in stack)
            {
                popup.HorizontalOffset = 0;

                double height = PanelHeight(panel, popup.Width);

                if (roomAbove)
                {
                    offset += PanelGap + height;
                    popup.VerticalOffset = -offset;
                }
                else
                {
                    // Mirrored below the applet rather than clamped on top of it. The order
                    // is preserved either way, just read downwards.
                    offset += PanelGap;
                    popup.VerticalOffset = offset;
                    offset += height;
                }

                // A closed panel is positioned but occupies nothing, so the next one up sits
                // where it would have been had this one never existed.
                if (!popup.IsOpen) offset -= roomAbove ? PanelGap + height : height + PanelGap;
            }
        }

        /// <summary>
        /// Shows how long the sleep timer has left, or nothing when it is not running.
        ///
        /// The timer's only previous surface was the toast it raised when cycled, so with the
        /// overlay hidden there was no way to check it without pressing the shortcut — which
        /// also changed the setting. Summoning the overlay now answers the question.
        /// </summary>
        public void ShowSleepRemaining(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero)
            {
                TxtSleepRemaining.Visibility = Visibility.Collapsed;
                return;
            }

            TxtSleepRemaining.Text = "⏻ " + SleepTimerService.Describe(remaining);
            TxtSleepRemaining.ToolTip = $"Playback pauses in {SleepTimerService.Describe(remaining)}";
            TxtSleepRemaining.Visibility = Visibility.Visible;
        }

        private void UpdateOfflineButton()
        {
            bool offline = _config.Current.OfflineMode;

            BtnOfflineToggle.Foreground = ThemeBrush(offline ? "WarningBrush" : "TextBrush");
            BtnOfflineToggle.Content = offline ? "✈" : "📶";
            BtnOfflineToggle.ToolTip = (offline
                ? "Offline mode is ON — only the local library is searched"
                : "Offline mode is off — all sources are searched") + KeyHint(HotkeyActions.OfflineMode);
        }

        /// <summary>
        /// The shortcut currently bound to an action, parenthesised for appending to a hint.
        ///
        /// Read from the config rather than written into the markup, because every one of
        /// these is user-configurable: a baked-in string is wrong the moment someone rebinds,
        /// and wrong again whenever a default moves. Empty when the action is unbound, so the
        /// hint simply omits it instead of promising a shortcut that does nothing.
        /// </summary>
        private string KeyHint(string action)
        {
            string keys = _config.Current.Hotkeys.For(action).Keys;
            return string.IsNullOrWhiteSpace(keys) ? string.Empty : $" ({keys})";
        }

        /// <summary>
        /// Restates every hint that names a shortcut. Called at startup and whenever the
        /// config changes, so rebinding updates the overlay without a restart.
        /// </summary>
        private void ApplyShortcutHints()
        {
            BtnShuffleFavorites.ToolTip = "Shuffle all favourites" + KeyHint(HotkeyActions.ShuffleFavorites);
            BtnSettingsGear.ToolTip = "Settings" + KeyHint(HotkeyActions.OpenSettings);

            UpdateOfflineButton();
            UpdateShuffleButton();
            UpdateLoopButton();
        }

        private void BtnSettingsGear_Click(object sender, RoutedEventArgs e) => ShowSettings();

        /// <summary>Opens the queue popup, refreshing it first. Entry point for the hotkey.</summary>
        public void ShowQueue()
        {
            // Clearing the selection first is what tells RefreshQueue this is an open rather
            // than an update, so it starts at the playing track instead of wherever the list
            // happened to be left last time.
            LstQueue.SelectedIndex = -1;

            RefreshQueue();
            QueuePopup.IsOpen = true;

            LstQueue.Focus();
            RestoreQueueFocus();

            RestartAutoHide();
        }

        /// <summary>
        /// Appends the highlighted search result to the queue instead of replacing it.
        ///
        /// Reached by global hotkey, which matters here: WM_HOTKEY goes to the overlay's
        /// message queue regardless of which window has focus, so this works even while
        /// focus sits inside the results popup — where routed key events cannot reach.
        /// </summary>
        public void AddHighlightedToQueue()
        {
            switch (HighlightedResult())
            {
                case Track track:
                {
                    int position = _playback.Enqueue(track);
                    TxtStatus.Text = $"Queued \"{track.Title}\" at #{position + 1}";
                    if (QueuePopup.IsOpen) RefreshQueue();
                    break;
                }

                case TrackCollection playlist:
                    _ = AddCollectionToQueueAsync(playlist);
                    break;
            }
        }

        /// <summary>Appends every track in a playlist, in playlist order.</summary>
        private async Task AddCollectionToQueueAsync(TrackCollection playlist)
        {
            var tracks = await ExpandCollectionAsync(playlist);
            if (tracks == null) return;

            foreach (var track in tracks) _playback.Enqueue(track);

            TxtStatus.Text = $"Queued {tracks.Count} from \"{playlist.Title}\"";
            if (QueuePopup.IsOpen) RefreshQueue();
        }

        /// <summary>Inserts the highlighted result right after the current track.</summary>
        public void InsertHighlightedNext()
        {
            switch (HighlightedResult())
            {
                case Track track:
                    _playback.InsertNext(track);
                    TxtStatus.Text = $"Playing \"{track.Title}\" next";
                    if (QueuePopup.IsOpen) RefreshQueue();
                    break;

                case TrackCollection playlist:
                    _ = InsertCollectionNextAsync(playlist);
                    break;
            }
        }

        /// <summary>
        /// Puts a whole playlist immediately after the current track, in playlist order.
        ///
        /// Inserted back to front: each insert goes directly after the current track, so
        /// walking the list backwards is what leaves it the right way round.
        /// </summary>
        private async Task InsertCollectionNextAsync(TrackCollection playlist)
        {
            var tracks = await ExpandCollectionAsync(playlist);
            if (tracks == null) return;

            for (int i = tracks.Count - 1; i >= 0; i--) _playback.InsertNext(tracks[i]);

            TxtStatus.Text = $"Playing {tracks.Count} from \"{playlist.Title}\" next";
            if (QueuePopup.IsOpen) RefreshQueue();
        }

        /// <summary>
        /// The result the user means: whatever is highlighted, or the top hit when the list
        /// has not been arrowed into.
        /// </summary>
        private object? HighlightedResult()
        {
            if (_searchResults.Count == 0)
            {
                TxtStatus.Text = "Search for something first";
                return null;
            }

            int index = Math.Max(0, LstSearchResults.SelectedIndex);
            return index < _searchResults.Count ? _searchResults[index] : null;
        }

        /// <summary>
        /// Runs <paramref name="onTap"/> on release, or <paramref name="onHold"/> once the
        /// combination has been held for the configured delay.
        ///
        /// The hold has to be detected by polling. Every hotkey registers with MOD_NOREPEAT,
        /// so Windows sends exactly one WM_HOTKEY on press and nothing on release — there is
        /// no second message to time against.
        ///
        /// The tap fires on *release* rather than on press. Acting on press would mean
        /// waiting out the hold threshold before every ordinary use, which feels broken;
        /// releasing is the moment the user has actually committed to "tap".
        /// </summary>
        private void BeginTapOrHold(HotkeyBinding binding, Action onTap, Action onHold,
            string holdLabel, bool repeats = false)
        {
            if (_holdTimer != null) return;   // already tracking this press

            if (!binding.IsValid)
            {
                // No binding to watch, so a hold cannot be detected. Degrade to a plain tap.
                onTap();
                return;
            }

            _holdBinding = binding;
            _holdStart = DateTime.UtcNow;
            _holdLabel = holdLabel;
            _holdTapAction = onTap;
            _holdHoldAction = onHold;
            _holdRepeats = repeats;
            _holdFired = false;

            _holdTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _holdTimer.Tick += HoldTick;
            _holdTimer.Start();
        }

        private void HoldTick(object? sender, EventArgs e)
        {
            var elapsed = DateTime.UtcNow - _holdStart;

            // Only the main key is checked. Letting go of Ctrl or Alt while keeping the key
            // down still reads as "holding", which is the forgiving reading.
            if (!IsKeyPhysicallyDown((int)_holdBinding.VirtualKey))
            {
                var tap = _holdTapAction;
                bool fired = _holdFired;
                StopHold();
                HoldFinished?.Invoke();

                if (!fired) tap?.Invoke();
                return;
            }

            if (elapsed >= HoldDelay)
            {
                var hold = _holdHoldAction;
                _holdFired = true;

                if (_holdRepeats)
                {
                    // Keep the timer running and start the clock again, so continuing to
                    // hold fires once per delay rather than once per press.
                    _holdStart = DateTime.UtcNow;
                    hold?.Invoke();
                    return;
                }

                StopHold();
                hold?.Invoke();
                return;
            }

            // Say what holding will do, so it is discoverable rather than a secret gesture.
            double fraction = HoldDelay > TimeSpan.Zero
                ? Math.Clamp(elapsed.TotalMilliseconds / HoldDelay.TotalMilliseconds, 0, 1)
                : 1;

            HoldProgressed?.Invoke(_holdLabel, fraction);
        }

        private void StopHold()
        {
            _holdTapAction = null;
            _holdHoldAction = null;
            _holdRepeats = false;

            if (_holdTimer == null) return;

            _holdTimer.Stop();
            _holdTimer.Tick -= HoldTick;
            _holdTimer = null;
        }

        /// <summary>
        /// Shared with the generic hold-to-activate toggle, so one setting governs every
        /// hold gesture in the app rather than each having its own private timing.
        /// </summary>
        private TimeSpan HoldDelay => TimeSpan.FromSeconds(_config.Current.Hotkeys.HoldDelaySeconds);

        private static bool IsKeyPhysicallyDown(int virtualKey) =>
            (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        /// <summary>Shows how long is left on a pending hold-to-activate shortcut.</summary>
        public void ShowHoldProgress(string actionName, double secondsRemaining)
        {
            if (!_isShown) ShowOverlay();
            TxtStatus.Text = $"Hold for {actionName}… {secondsRemaining:F1}s";
        }

        public void ClearHoldProgress()
        {
            if (TxtStatus.Text.StartsWith("Hold for ", StringComparison.Ordinal))
                TxtStatus.Text = "Yinyue";
        }

        /// <summary>
        /// Entry point for the restart hotkey: a tap sends the current track back to its
        /// start, a hold walks backwards through the queue once per delay.
        /// </summary>
        public void BeginRestartOrPreviousHold(HotkeyBinding binding)
        {
            BeginTapOrHold(binding,
                onTap: () => _playback.RestartTrack(),
                onHold: () => _ = _playback.PreviousAsync(alwaysChangeTrack: true),
                holdLabel: "go back",
                repeats: true);
        }

        /// <summary>
        /// Entry point for the play/pause hotkey: a tap toggles playback, a hold skips
        /// forward once per delay for as long as it is held.
        ///
        /// Deliberately does not summon the overlay. Play/pause is something you do while
        /// working in another window, and the toast reports where a skip landed.
        /// </summary>
        public void BeginPlayPauseHold(HotkeyBinding binding)
        {
            BeginTapOrHold(binding,
                onTap: () => _ = _playback.TogglePlayPauseAsync(),
                onHold: () => _ = _playback.NextAsync(),
                holdLabel: "skip",
                repeats: true);
        }

        /// <summary>
        /// Entry point for the add hotkey: a tap queues at the end, a hold plays it next.
        /// </summary>
        public void BeginAddToQueueHold(HotkeyBinding binding)
        {
            if (_searchResults.Count == 0)
            {
                TxtStatus.Text = "Search for something first";
                return;
            }

            BeginTapOrHold(binding, AddHighlightedToQueue, InsertHighlightedNext, "play it next");
        }

        /// <summary>
        /// Entry point for the remove hotkey: a tap removes one entry, a hold clears the queue.
        /// </summary>
        public void BeginRemoveOrClearHold(HotkeyBinding binding)
        {
            if (_playback.PlayOrder.Count == 0)
            {
                TxtStatus.Text = "The queue is empty";
                return;
            }

            if (!QueuePopup.IsOpen)
            {
                ShowQueue();
                TxtStatus.Text = "Pick an entry to remove, or hold to clear";
                return;
            }

            BeginTapOrHold(binding, RemoveSelectedFromQueue,
                () => _ = ClearQueueAsync(), "clear the queue");
        }

        /// <summary>
        /// Drops the selected queue entry. Needs a selection to act on, so it opens the
        /// queue rather than guessing which entry was meant.
        /// </summary>
        public void RemoveSelectedFromQueue()
        {
            if (_playback.PlayOrder.Count == 0)
            {
                TxtStatus.Text = "The queue is empty";
                return;
            }

            if (!QueuePopup.IsOpen || LstQueue.SelectedIndex < 0)
            {
                ShowQueue();
                TxtStatus.Text = "Pick an entry to remove";
                return;
            }

            int index = LstQueue.SelectedIndex;
            var entry = index < _queueEntries.Count ? _queueEntries[index].Track : null;

            if (_playback.RemoveAt(index))
            {
                TxtStatus.Text = entry != null
                    ? $"Removed \"{entry.Title}\""
                    : "Removed from queue";
            }
            else
            {
                TxtStatus.Text = "Cannot remove the playing track";
            }

            RefreshQueue();
        }

        private void BtnShuffleFavorites_Click(object sender, RoutedEventArgs e) =>
            _ = ShuffleFavoritesAsync();

        /// <summary>
        /// Loads every favourited track from the server and plays them in random order.
        ///
        /// Shuffle is forced on rather than assumed: the point of the action is a random
        /// walk through favourites, and silently playing them alphabetically because a
        /// toggle happened to be off would be the wrong answer.
        /// </summary>
        public async Task ShuffleFavoritesAsync()
        {
            if (_favoritesLoading) return;
            _favoritesLoading = true;

            BtnShuffleFavorites.IsEnabled = false;
            TxtStatus.Text = "Loading favourites…";

            try
            {
                var result = await _library.GetFavoritesAsync(FavoritesCap, CancellationToken.None);

                if (!result.Succeeded)
                {
                    TxtStatus.Text = result.Error ?? "Could not load favourites";
                    return;
                }

                if (result.Tracks.Count == 0)
                {
                    TxtStatus.Text = "No favourites yet — heart some tracks first";
                    return;
                }

                if (!_playback.Shuffle) _playback.ToggleShuffle();
                UpdateShuffleButton();

                // Start somewhere random. PlayQueueAsync keeps the chosen track first and
                // shuffles the rest, so without this the first song would always be the
                // alphabetically first favourite.
                int start = Random.Shared.Next(result.Tracks.Count);

                var tracks = result.Tracks.ToList();
                await _playback.PlayQueueAsync(tracks, start);

                TxtStatus.Text = $"Shuffling {tracks.Count} favourite(s)";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Favourites failed: {ex.Message}";
            }
            finally
            {
                _favoritesLoading = false;
                BtnShuffleFavorites.IsEnabled = true;
            }
        }

        public void ShowSettings()
        {
            if (_settingsWindow is { IsVisible: true })
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = _settingsFactory();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        #endregion

        #region Transport

        private void BtnPrevious_Click(object sender, RoutedEventArgs e) => _ = _playback.PreviousAsync();

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => _ = _playback.TogglePlayPauseAsync();

        private void BtnNext_Click(object sender, RoutedEventArgs e) => _ = _playback.NextAsync();

        private void BtnShuffle_Click(object sender, RoutedEventArgs e) => _playback.ToggleShuffle();

        /// <summary>
        /// Repaints both mode buttons and says what changed. The status line matters most
        /// for loop, which has three states — a colour alone cannot tell you which one.
        /// </summary>
        private void OnModesChanged()
        {
            UpdateShuffleButton();
            UpdateLoopButton();

            TxtStatus.Text = _playback.Shuffle
                ? $"{DescribeLoop(_playback.Loop)} · shuffle on"
                : DescribeLoop(_playback.Loop);
        }

        private void UpdateShuffleButton()
        {
            bool shuffle = _playback.Shuffle;
            BtnShuffle.Foreground = ThemeBrush(shuffle ? "AccentBrush" : "TextBrush");
            BtnShuffle.ToolTip = (shuffle ? "Shuffle on" : "Shuffle off")
                                 + KeyHint(HotkeyActions.ToggleShuffle);
        }

        private void BtnLoop_Click(object sender, RoutedEventArgs e) => _playback.CycleLoop();

        private void UpdateLoopButton()
        {
            var mode = _playback.Loop;

            BtnLoop.Content = mode == LoopMode.Track ? "🔂" : "🔁";
            BtnLoop.Foreground = ThemeBrush(mode == LoopMode.Off ? "TextBrush" : "AccentBrush");
            BtnLoop.ToolTip = DescribeLoop(mode) + KeyHint(HotkeyActions.CycleLoop);
        }

        private static string DescribeLoop(LoopMode mode) => mode switch
        {
            LoopMode.Queue => "Loop queue",
            LoopMode.Track => "Loop track",
            _ => "Loop off"
        };

        private async void BtnFavorite_Click(object sender, RoutedEventArgs e)
        {
            var track = _playback.CurrentTrack;
            if (track == null) return;

            if (!_library.SupportsFavorites(track))
            {
                TxtStatus.Text = "Favourites need a Jellyfin track";
                return;
            }

            BtnFavorite.IsEnabled = false;
            try
            {
                // Only repaint on a confirmed round trip — a heart that lights up on a
                // failed request is lying about what the server stored.
                bool ok = await _library.TrySetFavoriteAsync(track, !track.IsFavorite, CancellationToken.None);
                if (ok) UpdateFavoriteButton(track);
                else TxtStatus.Text = "Could not update favourite";
            }
            finally
            {
                BtnFavorite.IsEnabled = true;
            }
        }

        private void UpdateFavoriteButton(Track? track)
        {
            bool supported = track != null && _library.SupportsFavorites(track);

            BtnFavorite.Foreground = ThemeBrush(track?.IsFavorite == true ? "DangerBrush" : "TextBrush");
            BtnFavorite.Opacity = supported ? 1.0 : 0.4;
            BtnFavorite.ToolTip = supported
                ? (track!.IsFavorite ? "Remove from favourites" : "Add to favourites")
                : "Favourites are only available for Jellyfin tracks";
        }

        #endregion

        #region Progress slider

        private void Slider_MouseDown(object sender, MouseButtonEventArgs e) => _isUserDraggingSlider = true;

        private void Slider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isUserDraggingSlider = false;
            _playback.SeekPercent(SliderProgress.Value);
        }

        private void SliderProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isUserDraggingSlider) return;

            var total = _playback.Duration;
            if (total.TotalSeconds <= 0) return;

            var preview = TimeSpan.FromSeconds(total.TotalSeconds * e.NewValue / 100.0);
            TxtCurrentTime.Text = Format(preview);
        }

        #endregion

        #region Playback events

        private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var track = e.Track;
                TxtSongTitle.Text = track?.Title ?? "No Track Selected";
                TxtArtistName.Text = track?.DisplayArtist ?? "Unknown Artist";
                TxtTotalTime.Text = Format(track?.Duration ?? TimeSpan.Zero);
                ImgAlbumArt.Source = LoadArtwork(e.ArtworkPath);
                UpdateFavoriteButton(track);
            });
        }

        private void OnPlayingStateChanged(object? sender, bool isPlaying)
        {
            Dispatcher.BeginInvoke(() =>
            {
                BtnPlayPause.Content = isPlaying ? "⏸" : "▶";
                TxtStatus.Text = isPlaying ? "Playing" : "Paused";
            });
        }

        private void OnProgressUpdated(object? sender, AudioProgressEventArgs e)
        {
            // Fires four times a second on a threadpool thread. Skip entirely while hidden
            // — nothing can observe it, and the idle-CPU budget is the point of the app.
            if (!_isShown || _isUserDraggingSlider) return;

            Dispatcher.BeginInvoke(() =>
            {
                TxtCurrentTime.Text = Format(e.CurrentTime);
                TxtTotalTime.Text = Format(e.TotalTime);
                SliderProgress.Value = e.ProgressPercentage;
            });
        }

        private void OnPlaybackFailed(object? sender, string message)
        {
            Dispatcher.BeginInvoke(() => TxtStatus.Text = message);
        }

        #endregion

        #region Helpers

        private Brush ThemeBrush(string resourceKey) => (Brush)FindResource(resourceKey);

        private static string Format(TimeSpan value) =>
            value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");

        private ImageSource? LoadArtwork(string? path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                return _placeholderArt;

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                // Load fully up front so the file is not held open, and decode to the
                // display size rather than the source size.
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 260;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Overlay] Artwork load failed: {ex.Message}");
                return _placeholderArt;
            }
        }

        private static BitmapImage? TryLoadPlaceholder()
        {
            try
            {
                var image = new BitmapImage(
                    new Uri("pack://application:,,,/Yinyue;component/Resources/placeholder.png", UriKind.Absolute));
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Overlay] Placeholder missing: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
