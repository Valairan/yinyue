using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Yinyue.Models;
using Yinyue.Services;

// UseWindowsForms adds implicit global usings for System.Windows.Forms, which collides
// with the WPF controls of the same name.
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Yinyue
{
    /// <summary>
    /// One editable shortcut. Bound to a generated row, so the settings window never needs
    /// hand-written markup per action.
    /// </summary>
    public class HotkeyRow : INotifyPropertyChanged
    {
        private string _keys = string.Empty;
        private bool _hold;

        public string Action { get; init; } = string.Empty;
        public string Label => HotkeyActions.Describe(Action);

        public string Keys
        {
            get => _keys;
            set { _keys = value; Raise(); }
        }

        public bool Hold
        {
            get => _hold;
            set { _hold = value; Raise(); }
        }

        /// <summary>False where a hold gesture already means something else.</summary>
        public bool SupportsHold => HotkeyActions.SupportsHoldToggle(Action);

        public string? Note => HotkeyActions.HoldNote(Action);

        public Visibility NoteVisibility =>
            string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;

        private string? _conflict;

        /// <summary>
        /// Names the other actions sharing this combination, or null when it is unique.
        ///
        /// Shown as the user chooses rather than after saving: a clash disables *both*
        /// shortcuts, and finding that out from a tray balloon once the window has closed
        /// makes the cause hard to connect to the change that caused it.
        /// </summary>
        public string? Conflict
        {
            get => _conflict;
            set
            {
                if (_conflict == value) return;
                _conflict = value;
                Raise();
                Raise(nameof(ConflictVisibility));
            }
        }

        public Visibility ConflictVisibility =>
            string.IsNullOrEmpty(Conflict) ? Visibility.Collapsed : Visibility.Visible;

        public string HoldTooltip => SupportsHold
            ? "Require the keys to be held before this fires"
            : "This shortcut already uses hold for a second action";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// A streaming quality choice. Stored as bits per second; 0 means no ceiling, which
    /// lets the server direct-play the original file.
    /// </summary>
    public record QualityOption(string Label, int Bitrate);

    /// <summary>
    /// A normal window rather than a popup on the overlay: the overlay hides on
    /// deactivation, so a child popup would vanish the moment settings took focus.
    /// Reachable from the overlay cog, the tray menu, and Ctrl+Alt+I.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly ConfigService _config;
        private readonly JellyfinApiClient _jellyfin;
        private readonly LibraryIndexerService _indexer;

        private readonly ObservableCollection<HotkeyRow> _hotkeyRows = new();

        /// <summary>
        /// Animations are fixed when the windows are built, so a change only lands on the
        /// next launch. Remembering the loaded value is how we know to say so.
        /// </summary>
        private bool _animationsAtLoad;

        private bool _isScanning;
        private DateTime _lastProgressPush = DateTime.MinValue;

        public SettingsWindow(ConfigService config, JellyfinApiClient jellyfin, LibraryIndexerService indexer)
        {
            InitializeComponent();

            _config = config;
            _jellyfin = jellyfin;
            _indexer = indexer;

            LstHotkeys.ItemsSource = _hotkeyRows;

            _indexer.OnScanProgress += OnScanProgress;
            Closed += (_, _) => _indexer.OnScanProgress -= OnScanProgress;

            PopulateCombos();
            LoadFromConfig();
        }

        private static readonly QualityOption[] QualityOptions =
        {
            new("Original (no limit)", 0),
            new("320 kbps", 320_000),
            new("256 kbps", 256_000),
            new("192 kbps", 192_000),
            new("128 kbps", 128_000),
            new("96 kbps", 96_000),
        };

        private void PopulateCombos()
        {
            CmbAnchor.ItemsSource = Enum.GetValues<OverlayAnchor>();
            CmbMonitor.ItemsSource = Enum.GetValues<MonitorSelection>();
            CmbQuality.ItemsSource = QualityOptions;
        }

        private void LoadFromConfig()
        {
            var cfg = _config.Current;

            TxtServerUrl.Text = cfg.Jellyfin.ServerUrl;
            TxtUsername.Text = cfg.Jellyfin.Username;

            // An unrecognised stored bitrate falls back to Original rather than showing
            // an empty box.
            CmbQuality.SelectedItem =
                QualityOptions.FirstOrDefault(q => q.Bitrate == cfg.Jellyfin.MaxStreamingBitrate)
                ?? QualityOptions[0];

            ChkPrebuffer.IsChecked = cfg.Jellyfin.PrebufferNext;

            LstFolders.ItemsSource = null;
            LstFolders.ItemsSource = cfg.Library.Folders.ToList();
            ChkScanOnStartup.IsChecked = cfg.Library.ScanOnStartup;

            CmbAnchor.SelectedItem = cfg.Overlay.Anchor;
            CmbMonitor.SelectedItem = cfg.Overlay.Monitor;
            TxtMarginX.Text = cfg.Overlay.MarginX.ToString(CultureInfo.InvariantCulture);
            TxtMarginY.Text = cfg.Overlay.MarginY.ToString(CultureInfo.InvariantCulture);
            ChkAnimations.IsChecked = cfg.Overlay.Animations;
            TxtAnimationMs.Text = cfg.Overlay.AnimationMilliseconds.ToString(CultureInfo.InvariantCulture);
            TxtBackgroundOpacity.Text =
                cfg.Overlay.BackgroundOpacity.ToString("0.##", CultureInfo.InvariantCulture);
            _animationsAtLoad = cfg.Overlay.Animations;
            ChkHideOnFocusLoss.IsChecked = cfg.Overlay.HideOnFocusLoss;
            ChkAutoHide.IsChecked = cfg.Overlay.AutoHide;

            ChkSleepTimer.IsChecked = cfg.SleepTimer.Enabled;
            TxtSleepSteps.Text = string.Join(", ", cfg.SleepTimer.Steps);
            UpdateSleepStepsNote(cfg.SleepTimer.Steps);
            TxtAutoHideSeconds.Text =
                cfg.Overlay.AutoHideSeconds.ToString("0.#", CultureInfo.InvariantCulture);

            // Read from the registry rather than config: the user may have removed the
            // entry by hand, and the toggle should reflect what is actually there.
            ChkRunAtLogin.IsChecked = StartupService.IsEnabled;
            UpdateStartupStatus();
            ChkOfflineMode.IsChecked = cfg.OfflineMode;

            LoadHotkeys();

            UpdateJellyfinStatus();
            UpdateScanSummary();
        }

        private void UpdateJellyfinStatus()
        {
            if (_config.Current.Jellyfin.IsConfigured && _config.GetAccessToken() != null)
            {
                SetStatus(TxtJellyfinStatus,
                    $"Connected as {_config.Current.Jellyfin.Username}.", "SuccessBrush");
            }
            else if (!string.IsNullOrWhiteSpace(_config.Current.Jellyfin.ServerUrl))
            {
                SetStatus(TxtJellyfinStatus, "Saved, but not signed in.", "WarningBrush");
            }
            else
            {
                SetStatus(TxtJellyfinStatus, "Not connected.", "SubtextBrush");
            }
        }

        /// <summary>
        /// Reports what is actually in the index. A scan of a few files finishes faster
        /// than the eye can follow, so a persistent track count is the only durable proof
        /// that anything happened.
        /// </summary>
        private async void UpdateScanSummary()
        {
            int folders = _config.Current.Library.Folders.Count;

            // ConfigureAwait(false) plus an explicit marshal back, rather than relying on
            // the ambient context: this is an async void that touches UI, and if the
            // continuation ever resumes off the dispatcher the assignment throws where
            // nothing can catch it.
            int tracks = await _indexer.GetTrackCountAsync().ConfigureAwait(false);
            var last = _config.Current.Library.LastScanUtc;

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (folders == 0 && tracks == 0)
                {
                    SetStatus(TxtScanStatus, "No folders added yet.", "SubtextBrush");
                    return;
                }

                string when = last.HasValue
                    ? $"last scanned {last.Value.ToLocalTime():g}"
                    : "never scanned";

                SetStatus(TxtScanStatus,
                    $"{tracks:N0} track(s) indexed from {folders} folder(s) — {when}.",
                    tracks > 0 ? "SuccessBrush" : "WarningBrush");
            });
        }

        private void UpdateStartupStatus()
        {
            if (StartupService.IsEnabled && StartupService.IsStale())
            {
                SetStatus(TxtStartupStatus,
                    "The registered path is out of date — save to point it at this build.",
                    "WarningBrush");
                return;
            }

            TxtStartupStatus.Text = string.Empty;
        }

        private void SetStatus(TextBlock target, string message, string brushKey)
        {
            target.Text = message;
            target.Foreground = (System.Windows.Media.Brush)FindResource(brushKey);
        }

        #region Jellyfin

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtServerUrl.Text.Trim();
            string user = TxtUsername.Text.Trim();
            string password = TxtPassword.Password;

            if (string.IsNullOrWhiteSpace(url))
            {
                SetStatus(TxtJellyfinStatus, "Enter a server URL first.", "DangerBrush");
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                SetStatus(TxtJellyfinStatus,
                    "That URL does not look right. Include http:// or https://.", "DangerBrush");
                return;
            }

            BtnConnect.IsEnabled = false;
            SetStatus(TxtJellyfinStatus, "Connecting…", "SubtextBrush");

            try
            {
                var result = await _jellyfin.AuthenticateAsync(url, user, password);

                if (!result.Succeeded)
                {
                    // The client distinguishes unreachable from rejected, so say which.
                    SetStatus(TxtJellyfinStatus, result.Message,
                        result.Status == JellyfinAuthStatus.Unreachable ? "WarningBrush" : "DangerBrush");
                    return;
                }

                _config.Current.Jellyfin.ServerUrl = url.TrimEnd('/');
                _config.Current.Jellyfin.Username = user;
                _config.Current.Jellyfin.UserId = _jellyfin.UserId;
                _config.SetAccessToken(_jellyfin.AccessToken);
                _config.Save();

                // The password has done its job; do not leave it sitting in the UI.
                TxtPassword.Clear();

                UpdateJellyfinStatus();
            }
            catch (Exception ex)
            {
                SetStatus(TxtJellyfinStatus, $"Connection error: {ex.Message}", "DangerBrush");
            }
            finally
            {
                BtnConnect.IsEnabled = true;
            }
        }

        private void BtnSignOut_Click(object sender, RoutedEventArgs e)
        {
            _config.ClearCredentials();
            _jellyfin.SignOut();
            TxtPassword.Clear();
            UpdateJellyfinStatus();
        }

        #endregion

        #region Library folders

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose a music folder",
                Multiselect = true
            };

            if (dialog.ShowDialog(this) != true) return;

            var folders = _config.Current.Library.Folders;
            foreach (string folder in dialog.FolderNames)
            {
                if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    folders.Add(folder);
            }

            // Persist straight away. Folders are useless unsaved, and expecting the user to
            // remember a separate Save before scanning is a trap.
            _config.Save();

            LstFolders.ItemsSource = null;
            LstFolders.ItemsSource = folders.ToList();
            UpdateScanSummary();
        }

        private void BtnRemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (LstFolders.SelectedItem is not string selected) return;

            _config.Current.Library.Folders.RemoveAll(f =>
                string.Equals(f, selected, StringComparison.OrdinalIgnoreCase));
            _config.Save();

            LstFolders.ItemsSource = null;
            LstFolders.ItemsSource = _config.Current.Library.Folders.ToList();

            // The rows survive until a scan prunes them, so say so rather than implying
            // the tracks have already gone.
            TxtScanStatus.Text = "Folder removed. Scan to drop its tracks from search.";
        }

        private async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning) return;

            var folders = _config.Current.Library.Folders.Where(Directory.Exists).ToList();

            if (folders.Count == 0)
            {
                SetStatus(TxtScanStatus, "Add at least one folder that exists.", "DangerBrush");
                return;
            }

            _isScanning = true;
            BtnScan.IsEnabled = false;
            ScanProgress.Visibility = Visibility.Visible;
            ScanProgress.Value = 0;
            SetStatus(TxtScanStatus, "Scanning…", "SubtextBrush");

            var stopwatch = Stopwatch.StartNew();

            try
            {
                int indexed = 0;
                foreach (string folder in folders)
                    indexed += await _indexer.IndexDirectoryAsync(folder);

                // Drop tracks from folders that are no longer configured, and files that
                // have since been deleted. The scan itself only ever adds.
                int pruned = await _indexer.PruneAsync(_config.Current.Library.Folders);

                _config.Current.Library.LastScanUtc = DateTime.UtcNow;
                _config.Save();

                int total = await _indexer.GetTrackCountAsync();

                SetStatus(TxtScanStatus,
                    indexed == 0
                        ? $"Scan finished in {stopwatch.Elapsed.TotalSeconds:F1}s, but found no readable audio files. " +
                          "Supported types: mp3, flac, wav, m4a, ogg."
                        : $"Indexed {indexed:N0} track(s) from {folders.Count} folder(s) in " +
                          $"{stopwatch.Elapsed.TotalSeconds:F1}s" +
                          (pruned > 0 ? $", removed {pruned:N0} stale" : string.Empty) +
                          $". Library now holds {total:N0}.",
                    indexed == 0 ? "WarningBrush" : "SuccessBrush");
            }
            catch (Exception ex)
            {
                SetStatus(TxtScanStatus, $"Scan failed: {ex.Message}", "DangerBrush");
            }
            finally
            {
                _isScanning = false;
                BtnScan.IsEnabled = true;
                ScanProgress.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Throttled again on top of the indexer's own throttle: that one limits event
        /// volume, this one limits dispatcher pressure.
        /// </summary>
        private void OnScanProgress(int scanned, int total)
        {
            var now = DateTime.UtcNow;
            bool isLast = scanned >= total;

            if (!isLast && (now - _lastProgressPush).TotalMilliseconds < 50) return;
            _lastProgressPush = now;

            Dispatcher.BeginInvoke(() =>
            {
                ScanProgress.Value = total > 0 ? scanned * 100.0 / total : 0;
                TxtScanStatus.Text = $"Scanning… {scanned:N0} of {total:N0}";
            });
        }

        #endregion

        #region Shortcuts

        private void LoadHotkeys()
        {
            var keys = _config.Current.Hotkeys;

            _hotkeyRows.Clear();
            foreach (string action in HotkeyActions.All)
            {
                var entry = keys.For(action);
                var row = new HotkeyRow
                {
                    Action = action,
                    Keys = entry.Keys,
                    Hold = entry.Hold && HotkeyActions.SupportsHoldToggle(action)
                };

                // Recheck whenever a combination changes, whatever changed it. Driving this
                // from the row rather than from the capture handler means a clash cannot
                // survive some future path that sets Keys another way.
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(HotkeyRow.Keys)) RefreshHotkeyConflicts();
                };

                _hotkeyRows.Add(row);
            }

            TxtHoldDelay.Text = keys.HoldDelaySeconds.ToString("0.###", CultureInfo.InvariantCulture);

            RefreshHotkeyConflicts();
        }

        /// <summary>
        /// Marks every row that shares its combination with another.
        ///
        /// Recomputed wholesale rather than incrementally: clearing a clash has to update the
        /// row that was previously flagged as well as the one just changed, and a full pass
        /// over seventeen rows is free at this size.
        /// </summary>
        private void RefreshHotkeyConflicts()
        {
            var byKeys = _hotkeyRows
                .Where(r => !string.IsNullOrWhiteSpace(r.Keys))
                .GroupBy(r => r.Keys, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var row in _hotkeyRows) row.Conflict = null;

            foreach (var group in byKeys.Where(g => g.Count() > 1))
            {
                foreach (var row in group)
                {
                    string others = string.Join(", ", group
                        .Where(r => r != row)
                        .Select(r => r.Label));

                    row.Conflict = $"Clashes with {others} — both would stop working";
                }
            }
        }

        private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box) box.SelectAll();
            SetStatus(TxtHotkeyStatus, "Press a combination…", "SubtextBrush");
        }

        /// <summary>
        /// Captures a chord. Runs on PreviewKeyDown so the key never reaches the TextBox as
        /// text — these boxes display a binding, they are not editable.
        /// </summary>
        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box || box.DataContext is not HotkeyRow row) return;

            // Leave Tab alone so keyboard navigation through the form still works.
            if (e.Key == Key.Tab) return;

            e.Handled = true;

            // Alt-combinations arrive as Key.System with the real key in SystemKey.
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                LoadHotkeys();
                SetStatus(TxtHotkeyStatus, "Cancelled.", "SubtextBrush");
                Keyboard.ClearFocus();
                return;
            }

            // Wait for a real key — the user is still holding modifiers down.
            if (HotkeyBinding.IsModifierKey(key)) return;

            var binding = new HotkeyBinding(Keyboard.Modifiers, key);

            if (!binding.IsValid)
            {
                // A global hotkey with no modifier would swallow that key system-wide.
                SetStatus(TxtHotkeyStatus,
                    "That needs at least one of Ctrl, Alt, Shift or Win.", "DangerBrush");
                return;
            }

            row.Keys = binding.ToString();

            if (row.Conflict != null)
            {
                SetStatus(TxtHotkeyStatus,
                    $"{binding} is already taken. Pick another, or the clash disables both.",
                    "DangerBrush");
            }
            else
            {
                SetStatus(TxtHotkeyStatus,
                    $"{row.Label} set to {binding}. Save to apply.", "SubtextBrush");
            }
        }

        /// <summary>
        /// Reads a comma-separated list of minutes. Anything unparseable is dropped rather
        /// than rejecting the whole line: the list is hand-typed, and losing four good values
        /// to one stray character would be the worse outcome.
        /// </summary>
        private static List<int> ParseSleepSteps(string text)
        {
            var minutes = new List<int>();

            foreach (string part in (text ?? string.Empty)
                         .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int value))
                {
                    minutes.Add(value);
                }
            }

            return SleepTimerConfig.Sanitise(minutes);
        }

        /// <summary>
        /// Spells the cycle out, so the effect of the list is visible without pressing the
        /// shortcut five times to find out what it does.
        /// </summary>
        private void UpdateSleepStepsNote(IReadOnlyList<int> steps)
        {
            string cycle = string.Join(" · ", new[] { "off" }
                .Concat(steps.Select(Describe)));

            TxtSleepStepsNote.Text =
                $"The shortcut cycles: {cycle} · back to off. " +
                $"Whole minutes, up to {SleepTimerConfig.MaxStepMinutes / 60} hours, " +
                $"{SleepTimerConfig.MaxSteps} steps at most.";

            static string Describe(int minutes)
            {
                if (minutes < 60) return $"{minutes}m";

                int hours = minutes / 60;
                int rest = minutes % 60;
                return rest == 0 ? $"{hours}h" : $"{hours}h{rest}m";
            }
        }

        private void BtnResetHotkeys_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _hotkeyRows)
            {
                row.Keys = HotkeyConfig.DefaultKeysFor(row.Action);
                row.Hold = false;
            }

            TxtHoldDelay.Text = HotkeyConfig.DefaultHoldDelaySeconds
                .ToString("0.###", CultureInfo.InvariantCulture);

            SetStatus(TxtHotkeyStatus, "Defaults restored. Save to apply.", "SubtextBrush");
        }

        /// <summary>
        /// Refuses to save a shortcut set that cannot work. Catching duplicates here beats
        /// letting Windows silently drop the second registration.
        /// </summary>
        private bool TryCollectHotkeys(out HotkeyConfig keys)
        {
            keys = new HotkeyConfig
            {
                Bindings = new Dictionary<string, HotkeyBindingConfig>(StringComparer.OrdinalIgnoreCase)
            };

            if (!TryParseHoldDelay(out double delaySeconds)) return false;
            keys.HoldDelaySeconds = delaySeconds;

            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in _hotkeyRows)
            {
                if (!HotkeyBinding.TryParse(row.Keys, out var binding))
                {
                    SetStatus(TxtHotkeyStatus,
                        $"\"{row.Keys}\" is not a usable shortcut for {row.Label}.", "DangerBrush");
                    return false;
                }

                string text = binding.ToString();

                if (seen.TryGetValue(text, out string? other))
                {
                    SetStatus(TxtHotkeyStatus,
                        $"{text} is assigned twice — {row.Label} and {other}.", "DangerBrush");
                    return false;
                }

                seen[text] = row.Label;

                keys.Bindings[row.Action] = new HotkeyBindingConfig
                {
                    Keys = text,
                    Hold = row.Hold && row.SupportsHold
                };
            }

            return true;
        }

        private bool TryParseHoldDelay(out double seconds)
        {
            seconds = HotkeyConfig.DefaultHoldDelaySeconds;

            string text = TxtHoldDelay.Text.Trim();

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                SetStatus(TxtHotkeyStatus, $"\"{text}\" is not a number of seconds.", "DangerBrush");
                TxtHoldDelay.Focus();
                return false;
            }

            if (parsed < HotkeyConfig.MinHoldDelaySeconds || parsed > HotkeyConfig.MaxHoldDelaySeconds)
            {
                SetStatus(TxtHotkeyStatus,
                    $"Hold delay must be between {HotkeyConfig.MinHoldDelaySeconds:0.###} and " +
                    $"{HotkeyConfig.MaxHoldDelaySeconds:0.###} seconds.", "DangerBrush");
                TxtHoldDelay.Focus();
                return false;
            }

            seconds = parsed;
            return true;
        }

        #endregion

        #region Save / close

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Validate shortcuts before touching anything — a half-applied save is worse
            // than a refused one.
            if (!TryCollectHotkeys(out var hotkeys)) return;

            var cfg = _config.Current;
            cfg.Hotkeys = hotkeys;

            cfg.Jellyfin.ServerUrl = TxtServerUrl.Text.Trim().TrimEnd('/');
            cfg.Jellyfin.Username = TxtUsername.Text.Trim();

            if (CmbQuality.SelectedItem is QualityOption quality)
                cfg.Jellyfin.MaxStreamingBitrate = quality.Bitrate;

            cfg.Jellyfin.PrebufferNext = ChkPrebuffer.IsChecked == true;

            cfg.Library.ScanOnStartup = ChkScanOnStartup.IsChecked == true;

            if (CmbAnchor.SelectedItem is OverlayAnchor anchor) cfg.Overlay.Anchor = anchor;
            if (CmbMonitor.SelectedItem is MonitorSelection monitor) cfg.Overlay.Monitor = monitor;

            cfg.Overlay.MarginX = ParseMargin(TxtMarginX.Text, cfg.Overlay.MarginX);
            cfg.Overlay.MarginY = ParseMargin(TxtMarginY.Text, cfg.Overlay.MarginY);
            cfg.Overlay.Animations = ChkAnimations.IsChecked == true;

            // Both properties clamp, so an out-of-range entry is corrected rather than refused.
            if (int.TryParse(TxtAnimationMs.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int ms))
            {
                cfg.Overlay.AnimationMilliseconds = ms;
            }

            if (double.TryParse(TxtBackgroundOpacity.Text.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double opacity))
            {
                cfg.Overlay.BackgroundOpacity = opacity;
            }
            cfg.Overlay.HideOnFocusLoss = ChkHideOnFocusLoss.IsChecked == true;
            cfg.Overlay.AutoHide = ChkAutoHide.IsChecked == true;

            cfg.SleepTimer.Enabled = ChkSleepTimer.IsChecked == true;

            // The setter sanitises, so a mistyped list is corrected rather than refused. The
            // box is rewritten from the result so the user can see what was actually kept.
            cfg.SleepTimer.Steps = ParseSleepSteps(TxtSleepSteps.Text);
            TxtSleepSteps.Text = string.Join(", ", cfg.SleepTimer.Steps);
            UpdateSleepStepsNote(cfg.SleepTimer.Steps);

            // The property clamps, so an out-of-range entry is corrected rather than refused.
            if (double.TryParse(TxtAutoHideSeconds.Text.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double idle))
            {
                cfg.Overlay.AutoHideSeconds = idle;
            }

            cfg.OfflineMode = ChkOfflineMode.IsChecked == true;

            // The registry, not config — so a failure is reported rather than silently
            // leaving a toggle that claims something untrue.
            bool wantStartup = ChkRunAtLogin.IsChecked == true;
            if (!StartupService.SetEnabled(wantStartup))
            {
                SetStatus(TxtStartupStatus,
                    "Windows refused the startup entry. Check your registry permissions.",
                    "DangerBrush");
            }
            else
            {
                UpdateStartupStatus();
            }

            _config.Save();

            // Reflect any values that were clamped or rejected.
            TxtMarginX.Text = cfg.Overlay.MarginX.ToString(CultureInfo.InvariantCulture);
            TxtMarginY.Text = cfg.Overlay.MarginY.ToString(CultureInfo.InvariantCulture);
            TxtHoldDelay.Text = cfg.Hotkeys.HoldDelaySeconds.ToString("0.###", CultureInfo.InvariantCulture);
            TxtAutoHideSeconds.Text =
                cfg.Overlay.AutoHideSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            TxtAnimationMs.Text = cfg.Overlay.AnimationMilliseconds.ToString(CultureInfo.InvariantCulture);
            TxtBackgroundOpacity.Text =
                cfg.Overlay.BackgroundOpacity.ToString("0.##", CultureInfo.InvariantCulture);

            TxtSaveStatus.Text = cfg.Overlay.Animations == _animationsAtLoad
                ? $"Saved at {DateTime.Now:t}"
                : $"Saved at {DateTime.Now:t} — restart Yinyue to apply the animation change";

            _animationsAtLoad = cfg.Overlay.Animations;
            SetStatus(TxtHotkeyStatus, "Shortcuts applied.", "SuccessBrush");
        }

        private static double ParseMargin(string text, double fallback) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? Math.Clamp(value, 0, 2000)
                : fallback;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        #endregion
    }
}
