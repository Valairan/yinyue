using AppKit;
using CoreGraphics;
using Foundation;
using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue.UI
{
    /// <summary>
    /// Settings, in the same four tabs as the Windows window and carrying the same options:
    /// <b>Remote</b> (Jellyfin, streaming, offline), <b>Local</b> (folders and scanning),
    /// <b>General</b> (startup, position, animations, auto-hide, volume, sleep timer) and
    /// <b>Hotkeys</b>.
    ///
    /// Offline mode sits under Remote because it governs whether remote sources are consulted
    /// at all. Each tab scrolls independently so the long shortcut list does not push the
    /// other tabs around.
    ///
    /// A real titled window, not a panel: it keeps its chrome, takes focus, and is a thing you
    /// open and work in rather than glance at. It must always be reachable — a fresh install
    /// has no library, so the overlay opens into an empty search and this is the only way to
    /// give it something to find.
    /// </summary>
    public sealed class SettingsWindow : NSWindow
    {
        private readonly ConfigService _config;
        private readonly JellyfinApiClient _jellyfin;
        private readonly LibraryIndexerService _indexer;
        private readonly MacStartupService _startup = new();

        // Remote
        private readonly NSTextField _server, _username, _jellyfinStatus;
        private readonly NSSecureTextField _password;
        private readonly NSPopUpButton _quality;
        private readonly NSButton _prebuffer, _offline;

        // Local
        private readonly NSTableView _folders;
        private readonly FolderSource _folderSource = new();
        private readonly NSButton _scanOnStartup;
        private readonly NSTextField _scanStatus;

        // General
        private readonly NSButton _runAtLogin, _animations, _hideOnFocusLoss, _autoHide, _sleepEnabled;
        private readonly NSTextField _startupStatus, _marginX, _marginY, _animationMs,
                                     _backgroundOpacity, _autoHideSeconds, _volumeStep, _sleepSteps;
        private readonly NSPopUpButton _anchor, _monitor;

        // Hotkeys
        private readonly NSTextField _holdDelay, _hotkeyStatus;
        private readonly List<(string Action, NSTextField Keys, NSButton Hold)> _hotkeyRows = new();

        private readonly NSTextField _saveStatus;

        /// <summary>
        /// Streaming quality, as bitrates. 0 is Original — direct play, no transcode — which
        /// is why it leads rather than being buried under the numbers.
        /// </summary>
        private static readonly (string Label, int Bitrate)[] Qualities =
        {
            ("Original (no transcode)", 0),
            ("320 kbps", 320_000),
            ("256 kbps", 256_000),
            ("192 kbps", 192_000),
            ("128 kbps", 128_000),
        };

        public SettingsWindow(ConfigService config, JellyfinApiClient jellyfin,
                              LibraryIndexerService indexer)
            : base(new CGRect(0, 0, 600, 470),
                   NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable,
                   NSBackingStore.Buffered, false)
        {
            _config = config;
            _jellyfin = jellyfin;
            _indexer = indexer;

            Title = "Yinyue Settings";
            ReleaseWhenClosed(false);

            var overlay = _config.Current.Overlay;
            var jelly = _config.Current.Jellyfin;
            var playback = _config.Current.Playback;
            var sleep = _config.Current.SleepTimer;

            // ---------------------------------------------------------------- Remote
            var remote = new SettingsTab();
            remote.Heading("Jellyfin");

            _server = remote.Row("Server", Controls.Field(jelly.ServerUrl));
            _username = remote.Row("Username", Controls.Field(jelly.Username));
            _password = remote.Row("Password", new NSSecureTextField(new CGRect(0, 0, SettingsTab.FieldWidth, 22)));

            var buttons = new NSView(new CGRect(SettingsTab.FieldLeft, 0, SettingsTab.FieldWidth, 26));
            buttons.AddSubview(Controls.Action("Sign in", 90, (_, _) => _ = SignInAsync()));

            var signOut = Controls.Action("Sign out", 90, (_, _) => SignOut());
            signOut.Frame = new CGRect(98, 0, 90, 24);
            buttons.AddSubview(signOut);
            remote.Add(buttons, 26);

            _jellyfinStatus = remote.Note(DescribeSession());

            remote.Gap(10);
            remote.Heading("Streaming");
            _quality = remote.Row("Quality",
                Controls.Choice(Qualities.Select(q => q.Label), LabelForBitrate(jelly.MaxStreamingBitrate)), 24);

            _prebuffer = remote.Add(Controls.Check("Open the next track while the current one plays",
                jelly.PrebufferNext), 20);
            remote.Note("Remote sources only — a local file opens instantly and gains nothing.");

            remote.Gap(10);
            remote.Heading("Offline");
            _offline = remote.Add(Controls.Check("Offline mode — never consult remote sources",
                _config.Current.OfflineMode), 20);

            // ---------------------------------------------------------------- Local
            var local = new SettingsTab();
            local.Heading("Library folders");

            _folderSource.Folders = _config.Current.Library.Folders.ToList();
            _folders = new NSTableView(new CGRect(0, 0, SettingsTab.FieldWidth, 110)) { HeaderView = null };
            _folders.AddColumn(new NSTableColumn("path") { Width = (nfloat)(SettingsTab.FieldWidth - 10) });
            _folders.DataSource = _folderSource;
            _folders.Delegate = new FolderDelegate(_folderSource);

            var folderScroll = new NSScrollView(new CGRect(0, 0, SettingsTab.Width, 110))
            {
                DocumentView = _folders,
                HasVerticalScroller = true,
                BorderType = NSBorderType.BezelBorder,
            };
            local.Add(folderScroll, 110);

            var folderButtons = new NSView(new CGRect(0, 0, SettingsTab.Width, 26));
            folderButtons.AddSubview(Controls.Action("Add folder…", 110, (_, _) => AddFolder()));

            var remove = Controls.Action("Remove", 90, (_, _) => RemoveFolder());
            remove.Frame = new CGRect(118, 0, 90, 24);
            folderButtons.AddSubview(remove);

            var scan = Controls.Action("Scan library", 110, (_, _) => _ = ScanAsync());
            scan.Frame = new CGRect(216, 0, 110, 24);
            folderButtons.AddSubview(scan);
            local.Add(folderButtons, 26);

            _scanOnStartup = local.Add(Controls.Check("Scan at startup",
                _config.Current.Library.ScanOnStartup), 20);

            _scanStatus = local.Note("…");
            _ = RefreshCountAsync();

            // ---------------------------------------------------------------- General
            var general = new SettingsTab();
            general.Heading("Startup");

            _runAtLogin = general.Add(Controls.Check("Start Yinyue at sign-in", _startup.IsEnabled), 20);
            _runAtLogin.Activated += (_, _) => ApplyStartAtLogin();
            _startupStatus = general.Note(string.Empty);

            general.Gap(10);
            general.Heading("Overlay position");

            _anchor = general.Row("Position",
                Controls.Choice(Enum.GetNames<OverlayAnchor>(), overlay.Anchor.ToString()), 24);

            _monitor = general.Row("Monitor",
                Controls.Choice(Enum.GetNames<MonitorSelection>(), overlay.Monitor.ToString()), 24);

            _marginX = general.Row("Margin X", Controls.Field(overlay.MarginX.ToString(), 80));
            _marginY = general.Row("Margin Y", Controls.Field(overlay.MarginY.ToString(), 80));

            general.Gap(10);
            general.Heading("Appearance");

            _animations = general.Add(Controls.Check("Fade the overlay and toasts in and out",
                overlay.Animations), 20);
            general.Note("Takes a restart: transparency cannot be changed once a window is on screen.");

            _animationMs = general.Row("Fade (ms)", Controls.Field(overlay.AnimationMilliseconds.ToString(), 80));
            _backgroundOpacity = general.Row("Background opacity",
                Controls.Field(overlay.BackgroundOpacity.ToString("0.00"), 80));
            general.Note("Tints the panel behind the content, so text and artwork stay legible. Needs animations on.");

            general.Gap(10);
            general.Heading("Dismissing");

            _hideOnFocusLoss = general.Add(Controls.Check("Hide when the overlay loses focus",
                overlay.HideOnFocusLoss), 20);
            _autoHide = general.Add(Controls.Check("Hide after a period of inactivity", overlay.AutoHide), 20);
            _autoHideSeconds = general.Row("Inactivity (s)", Controls.Field(overlay.AutoHideSeconds.ToString(), 80));

            general.Gap(10);
            general.Heading("Playback");
            _volumeStep = general.Row("Volume step (%)", Controls.Field(playback.VolumeStepPercent.ToString(), 80));

            general.Gap(10);
            general.Heading("Sleep timer");
            general.Note("Pauses playback after a set time. Not auto-hide: nothing resets it, and it stops the music rather than the window.");

            _sleepEnabled = general.Add(Controls.Check("Enable the sleep timer", sleep.Enabled), 20);
            _sleepSteps = general.Row("Steps (minutes)",
                Controls.Field(string.Join(", ", sleep.Steps)));
            general.Note("Comma separated. The shortcut cycles through them, starting from off.");

            // ---------------------------------------------------------------- Hotkeys
            var hotkeys = new SettingsTab();
            hotkeys.Heading("Global shortcuts");
            hotkeys.Note("Work anywhere, whether or not the overlay is showing. A combination another app already owns cannot be detected until Yinyue tries to register it.");

            _holdDelay = hotkeys.Row("Hold delay (s)",
                Controls.Field(_config.Current.Hotkeys.HoldDelaySeconds.ToString("0.###"), 80));

            hotkeys.Gap(6);

            foreach (var action in HotkeyActions.All)
            {
                var entry = _config.Current.Hotkeys.For(action);

                var row = new NSView(new CGRect(0, 0, SettingsTab.Width, 24));

                var caption = Controls.Label(HotkeyActions.Describe(action));
                caption.Frame = new CGRect(0, 4, 210, 18);
                row.AddSubview(caption);

                var keys = Controls.Field(entry.Keys, 150);
                keys.Frame = new CGRect(214, 0, 150, 22);
                row.AddSubview(keys);

                // Withheld where the hold already means something: the queue actions and the
                // two transport escalations. HotkeyActions.SupportsHoldToggle is the single
                // place that decides, and registration enforces it again.
                var hold = Controls.Check("Hold to activate", entry.Hold);
                hold.Frame = new CGRect(374, 2, 170, 20);
                hold.Enabled = HotkeyActions.SupportsHoldToggle(action);
                hold.ToolTip = HotkeyActions.HoldNote(action) ?? "Fire only after the keys are held";
                row.AddSubview(hold);

                hotkeys.Add(row, 24, gapAfter: 2);
                _hotkeyRows.Add((action, keys, hold));
            }

            hotkeys.Gap(8);
            hotkeys.Add(Controls.Action("Reset to defaults", 150, (_, _) => ResetHotkeys()), 24);
            _hotkeyStatus = hotkeys.Note(string.Empty);

            // ---------------------------------------------------------------- chrome
            var tabs = new NSTabView(new CGRect(10, 56, 580, 400));
            tabs.Add(MakeTab("Remote", remote));
            tabs.Add(MakeTab("Local", local));
            tabs.Add(MakeTab("General", general));
            tabs.Add(MakeTab("Hotkeys", hotkeys));

            var content = new NSView(new CGRect(0, 0, 600, 470));
            content.AddSubview(tabs);

            _saveStatus = Controls.Label(string.Empty, size: 11, colour: Theme.Subtext);
            _saveStatus.Frame = new CGRect(16, 22, 340, 18);
            content.AddSubview(_saveStatus);

            var save = Controls.Action("Save", 90, (_, _) => Save());
            save.Frame = new CGRect(396, 18, 90, 26);
            save.KeyEquivalent = "\r";
            content.AddSubview(save);

            var close = Controls.Action("Close", 90, (_, _) => Close());
            close.Frame = new CGRect(494, 18, 90, 26);
            content.AddSubview(close);

            ContentView = content;
            Center();
        }

        private static NSTabViewItem MakeTab(string label, SettingsTab tab)
        {
            var item = new NSTabViewItem { Label = label, View = tab.Build() };
            return item;
        }

        // ---------------------------------------------------------------- Remote

        private string DescribeSession() =>
            _config.GetAccessToken() is null
                ? "Not signed in."
                : $"Signed in as {_config.Current.Jellyfin.Username}.";

        private static string LabelForBitrate(int bitrate) =>
            Qualities.FirstOrDefault(q => q.Bitrate == bitrate).Label ?? Qualities[0].Label;

        private async Task SignInAsync()
        {
            _jellyfinStatus.StringValue = "Signing in…";

            try
            {
                var result = await _jellyfin.AuthenticateAsync(
                    _server.StringValue.Trim(), _username.StringValue.Trim(),
                    _password.StringValue, CancellationToken.None).ConfigureAwait(false);

                NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                {
                    if (!result.Succeeded)
                    {
                        // The message distinguishes "unreachable" from "wrong password" —
                        // telling someone their credentials are wrong when the server is down
                        // is a genuinely bad experience.
                        _jellyfinStatus.StringValue = result.Message;
                        return;
                    }

                    _config.Current.Jellyfin.ServerUrl = _server.StringValue.Trim();
                    _config.Current.Jellyfin.Username = _username.StringValue.Trim();

                    // Never the password — only the token the server issued in exchange, and
                    // that goes to the Keychain rather than into config.json.
                    _config.SetAccessToken(_jellyfin.AccessToken);
                    _config.Current.Jellyfin.UserId = _jellyfin.UserId;
                    _config.Save();

                    _password.StringValue = string.Empty;
                    _jellyfinStatus.StringValue = DescribeSession();
                });
            }
            catch (Exception ex)
            {
                NSApplication.SharedApplication.BeginInvokeOnMainThread(
                    () => _jellyfinStatus.StringValue = ex.Message);
            }
        }

        private void SignOut()
        {
            _config.ClearCredentials();
            _password.StringValue = string.Empty;
            _jellyfinStatus.StringValue = DescribeSession();
        }

        // ---------------------------------------------------------------- Local

        private void AddFolder()
        {
            var picker = NSOpenPanel.OpenPanel;
            picker.CanChooseDirectories = true;
            picker.CanChooseFiles = false;
            picker.AllowsMultipleSelection = true;

            if (picker.RunModal() != 1) return;

            foreach (var url in picker.Urls)
            {
                if (url.Path is { } path && !_folderSource.Folders.Contains(path))
                    _folderSource.Folders.Add(path);
            }

            _folders.ReloadData();
        }

        private void RemoveFolder()
        {
            int row = (int)_folders.SelectedRow;
            if (row < 0 || row >= _folderSource.Folders.Count) return;

            _folderSource.Folders.RemoveAt(row);
            _folders.ReloadData();
        }

        private async Task ScanAsync()
        {
            _scanStatus.StringValue = "Scanning…";
            Save();

            try
            {
                foreach (var folder in _folderSource.Folders)
                    await _indexer.IndexDirectoryAsync(folder).ConfigureAwait(false);

                // Rows whose files or folders have gone are dropped, rather than left to
                // surface later as a playback error.
                await _indexer.PruneAsync(_folderSource.Folders).ConfigureAwait(false);
                await RefreshCountAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                NSApplication.SharedApplication.BeginInvokeOnMainThread(
                    () => _scanStatus.StringValue = ex.Message);
            }
        }

        private async Task RefreshCountAsync()
        {
            int count = await _indexer.GetTrackCountAsync().ConfigureAwait(false);

            NSApplication.SharedApplication.BeginInvokeOnMainThread(
                () => _scanStatus.StringValue = $"{count} tracks indexed");
        }

        // ---------------------------------------------------------------- General

        /// <summary>
        /// macOS can refuse this — the app may not be running from a bundle, or the user may
        /// have switched it off in System Settings, which an app cannot override. Report what
        /// happened and put the switch back rather than leaving it showing a lie.
        /// </summary>
        private void ApplyStartAtLogin()
        {
            bool wanted = _runAtLogin.State == NSCellStateValue.On;
            string? problem = _startup.SetEnabled(wanted);

            _startupStatus.StringValue = problem ?? string.Empty;

            if (problem is not null)
                _runAtLogin.State = _startup.IsEnabled ? NSCellStateValue.On : NSCellStateValue.Off;
        }

        // ---------------------------------------------------------------- Hotkeys

        private void ResetHotkeys()
        {
            var defaults = new HotkeyConfig();

            foreach (var (action, keys, hold) in _hotkeyRows)
            {
                keys.StringValue = defaults.For(action).Keys;
                hold.State = NSCellStateValue.Off;
            }

            _hotkeyStatus.StringValue = "Defaults restored — Save to apply.";
        }

        // ---------------------------------------------------------------- Save

        private void Save()
        {
            var config = _config.Current;

            config.Jellyfin.ServerUrl = _server.StringValue.Trim();
            config.Jellyfin.Username = _username.StringValue.Trim();
            config.Jellyfin.MaxStreamingBitrate =
                Qualities.FirstOrDefault(q => q.Label == _quality.TitleOfSelectedItem).Bitrate;
            config.Jellyfin.PrebufferNext = _prebuffer.State == NSCellStateValue.On;
            config.OfflineMode = _offline.State == NSCellStateValue.On;

            config.Library.Folders = _folderSource.Folders.ToList();
            config.Library.ScanOnStartup = _scanOnStartup.State == NSCellStateValue.On;

            if (Enum.TryParse<OverlayAnchor>(_anchor.TitleOfSelectedItem, out var anchor))
                config.Overlay.Anchor = anchor;

            if (Enum.TryParse<MonitorSelection>(_monitor.TitleOfSelectedItem, out var monitor))
                config.Overlay.Monitor = monitor;

            config.Overlay.MarginX = Number(_marginX, config.Overlay.MarginX);
            config.Overlay.MarginY = Number(_marginY, config.Overlay.MarginY);
            config.Overlay.Animations = _animations.State == NSCellStateValue.On;
            config.Overlay.AnimationMilliseconds = (int)Number(_animationMs, config.Overlay.AnimationMilliseconds);
            config.Overlay.BackgroundOpacity = Number(_backgroundOpacity, config.Overlay.BackgroundOpacity);
            config.Overlay.HideOnFocusLoss = _hideOnFocusLoss.State == NSCellStateValue.On;
            config.Overlay.AutoHide = _autoHide.State == NSCellStateValue.On;
            config.Overlay.AutoHideSeconds = Number(_autoHideSeconds, config.Overlay.AutoHideSeconds);

            config.Playback.VolumeStepPercent = Number(_volumeStep, config.Playback.VolumeStepPercent);

            config.SleepTimer.Enabled = _sleepEnabled.State == NSCellStateValue.On;
            config.SleepTimer.Steps = ParseSteps(_sleepSteps.StringValue, config.SleepTimer.Steps);

            config.Hotkeys.HoldDelaySeconds = Number(_holdDelay, config.Hotkeys.HoldDelaySeconds);

            foreach (var (action, keys, hold) in _hotkeyRows)
            {
                var entry = config.Hotkeys.For(action);
                entry.Keys = keys.StringValue.Trim();
                entry.Hold = hold.State == NSCellStateValue.On;
            }

            // Moves a binding still sitting on an abandoned default onto its replacement, so
            // an old config cannot collide with whatever took the key over — a collision
            // disables both shortcuts, not one.
            config.Hotkeys.MigrateSupersededDefaults();

            _config.Save();

            _saveStatus.StringValue = "Saved. Some changes need a restart.";
            _startupStatus.StringValue = string.Empty;
        }

        /// <summary>
        /// Keeps the old value when the box holds something that is not a number, rather than
        /// silently writing zero — which for a margin or a fade would look like a bug.
        /// </summary>
        private static double Number(NSTextField field, double fallback) =>
            double.TryParse(field.StringValue.Trim(), out double value) ? value : fallback;

        private static List<int> ParseSteps(string text, List<int> fallback)
        {
            var parsed = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(p => int.TryParse(p, out int v) ? v : -1)
                             .Where(v => v > 0)
                             .ToList();

            // An empty result falls back rather than leaving a cycle with nothing in it but
            // "off" — the sanitising in SleepTimerConfig does the rest.
            return parsed.Count > 0 ? parsed : fallback;
        }

        private sealed class FolderSource : NSTableViewDataSource
        {
            public List<string> Folders { get; set; } = new();

            public override nint GetRowCount(NSTableView tableView) => Folders.Count;
        }

        private sealed class FolderDelegate : NSTableViewDelegate
        {
            private readonly FolderSource _source;

            public FolderDelegate(FolderSource source) => _source = source;

            public override NSView GetViewForItem(NSTableView tableView, NSTableColumn tableColumn, nint row)
            {
                var label = Controls.Label(_source.Folders[(int)row]);
                label.Frame = new CGRect(2, 0, tableColumn.Width - 4, 18);
                label.LineBreakMode = NSLineBreakMode.TruncatingHead;
                return label;
            }
        }
    }
}
