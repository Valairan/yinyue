using AppKit;
using CoreGraphics;
using Foundation;
using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue.UI
{
    /// <summary>
    /// Settings: the Jellyfin server, the local library, and where the overlay appears.
    ///
    /// A real titled window rather than a panel, deliberately. It keeps its chrome — it is
    /// not a summon surface, it is a thing you open, work in, and close — and unlike the
    /// overlay it should appear in the window list while it is open.
    ///
    /// <b>It must always be reachable.</b> The menu-bar item carries its own entry precisely
    /// because the overlay can auto-hide at an inconvenient moment, and a fresh install has
    /// no library, so the overlay opens into an empty search with nothing to find. If this
    /// window cannot be opened, nothing can ever play.
    /// </summary>
    public sealed class SettingsWindow : NSWindow
    {
        private readonly ConfigService _config;
        private readonly JellyfinApiClient _jellyfin;
        private readonly LibraryIndexerService _indexer;

        private readonly NSTextField _server;
        private readonly NSTextField _username;
        private readonly NSSecureTextField _password;
        private readonly NSTextField _signInStatus;

        private readonly NSTableView _folders;
        private readonly FolderSource _folderSource = new();

        private readonly NSPopUpButton _anchor;
        private readonly NSButton _startAtLogin;
        private readonly NSTextField _startupStatus;
        private readonly MacStartupService _startup = new();
        private readonly NSTextField _libraryStatus;

        public SettingsWindow(ConfigService config, JellyfinApiClient jellyfin,
                              LibraryIndexerService indexer)
            : base(new CGRect(0, 0, 520, 460),
                   NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable,
                   NSBackingStore.Buffered, false)
        {
            _config = config;
            _jellyfin = jellyfin;
            _indexer = indexer;

            Title = "Yinyue Settings";

            // The settings window keeps its chrome, so it cannot be transparent and therefore
            // does not take the overlay's background tint. Same rule as on Windows.
            BackgroundColor = Theme.Base;
            // Kept alive across a close, so reopening is instant and typing survives.
            ReleaseWhenClosed(false);

            var content = new NSView(new CGRect(0, 0, 520, 460));
            ContentView = content;

            double y = 400;

            content.AddSubview(Heading("Remote", 20, y));
            y -= 28;

            content.AddSubview(Caption("Jellyfin server", 20, y));
            _server = Field(160, y - 2, 340, _config.Current.Jellyfin.ServerUrl ?? string.Empty);
            content.AddSubview(_server);
            y -= 30;

            content.AddSubview(Caption("Username", 20, y));
            _username = Field(160, y - 2, 340, _config.Current.Jellyfin.Username ?? string.Empty);
            content.AddSubview(_username);
            y -= 30;

            content.AddSubview(Caption("Password", 20, y));
            _password = new NSSecureTextField(new CGRect(160, y - 2, 340, 22));
            content.AddSubview(_password);
            y -= 32;

            var signIn = new NSButton(new CGRect(160, y, 100, 26)) { Title = "Sign in", BezelStyle = NSBezelStyle.Rounded };
            signIn.Activated += (_, _) => _ = SignInAsync();
            content.AddSubview(signIn);

            _signInStatus = Caption(TokenStatus(), 270, y + 4);
            content.AddSubview(_signInStatus);
            y -= 40;

            content.AddSubview(Heading("Local", 20, y));
            y -= 24;

            _folders = new NSTableView(new CGRect(0, 0, 340, 90)) { HeaderView = null };
            _folders.AddColumn(new NSTableColumn("path") { Width = 330 });
            _folderSource.Folders = _config.Current.Library.Folders.ToList();
            _folders.DataSource = _folderSource;
            _folders.Delegate = new FolderDelegate(_folderSource);

            var scroll = new NSScrollView(new CGRect(160, y - 90, 340, 90))
            {
                DocumentView = _folders,
                HasVerticalScroller = true,
                BorderType = NSBorderType.BezelBorder,
            };
            content.AddSubview(scroll);

            var add = new NSButton(new CGRect(20, y - 26, 120, 26)) { Title = "Add folder…", BezelStyle = NSBezelStyle.Rounded };
            add.Activated += (_, _) => AddFolder();
            content.AddSubview(add);

            var scan = new NSButton(new CGRect(20, y - 56, 120, 26)) { Title = "Scan now", BezelStyle = NSBezelStyle.Rounded };
            scan.Activated += (_, _) => _ = ScanAsync();
            content.AddSubview(scan);

            _libraryStatus = Caption("…", 160, y - 112);
            _ = RefreshCountAsync();
            content.AddSubview(_libraryStatus);
            y -= 140;

            content.AddSubview(Heading("Overlay", 20, y));
            y -= 28;

            content.AddSubview(Caption("Position", 20, y));
            _anchor = new NSPopUpButton(new CGRect(160, y - 4, 200, 26), false);
            foreach (var value in Enum.GetNames<OverlayAnchor>()) _anchor.AddItem(value);
            _anchor.SelectItem(_config.Current.Overlay.Anchor.ToString());
            content.AddSubview(_anchor);

            y -= 32;

            _startAtLogin = new NSButton(new CGRect(160, y, 200, 22)) { Title = "Start at sign-in" };
            _startAtLogin.SetButtonType(NSButtonType.Switch);
            _startAtLogin.State = _startup.IsEnabled ? NSCellStateValue.On : NSCellStateValue.Off;
            _startAtLogin.Activated += (_, _) => ApplyStartAtLogin();
            content.AddSubview(_startAtLogin);

            _startupStatus = Caption(string.Empty, 160, y - 18);
            _startupStatus.TextColor = Theme.Warning;
            content.AddSubview(_startupStatus);

            var save = new NSButton(new CGRect(400, 20, 100, 30)) { Title = "Save", BezelStyle = NSBezelStyle.Rounded };
            save.Activated += (_, _) => Save();
            content.AddSubview(save);

            Center();
        }

        /// <summary>
        /// macOS can refuse this — the app may not be running from a bundle, or the user may
        /// have switched it off in System Settings, which an app cannot override. Report what
        /// happened and put the switch back rather than leaving it showing a lie.
        /// </summary>
        private void ApplyStartAtLogin()
        {
            bool wanted = _startAtLogin.State == NSCellStateValue.On;
            string? problem = _startup.SetEnabled(wanted);

            _startupStatus.StringValue = problem ?? string.Empty;

            if (problem is not null)
                _startAtLogin.State = _startup.IsEnabled ? NSCellStateValue.On : NSCellStateValue.Off;
        }

        private string TokenStatus() =>
            _config.GetAccessToken() is null ? "Not signed in" : "Signed in";

        private async Task SignInAsync()
        {
            _signInStatus.StringValue = "Signing in…";

            try
            {
                var result = await _jellyfin.AuthenticateAsync(
                    _server.StringValue.Trim(), _username.StringValue.Trim(),
                    _password.StringValue, CancellationToken.None).ConfigureAwait(false);

                NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                {
                    if (!result.Succeeded)
                    {
                        // The message distinguishes "unreachable" from "wrong password",
                        // which matters because one of those is worth retrying.
                        _signInStatus.StringValue = result.Message;
                        return;
                    }

                    // Never the password — only the token the server issued in exchange, and
                    // it goes to the Keychain rather than into config.json.
                    _config.Current.Jellyfin.ServerUrl = _server.StringValue.Trim();
                    _config.Current.Jellyfin.Username = _username.StringValue.Trim();
                    _config.SetAccessToken(_jellyfin.AccessToken);
                    _config.Current.Jellyfin.UserId = _jellyfin.UserId;
                    _config.Save();

                    _password.StringValue = string.Empty;
                    _signInStatus.StringValue = "Signed in";
                });
            }
            catch (Exception ex)
            {
                NSApplication.SharedApplication.BeginInvokeOnMainThread(
                    () => _signInStatus.StringValue = ex.Message);
            }
        }

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

        private async Task ScanAsync()
        {
            _libraryStatus.StringValue = "Scanning…";
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
                    () => _libraryStatus.StringValue = ex.Message);
            }
        }

        private async Task RefreshCountAsync()
        {
            int count = await _indexer.GetTrackCountAsync().ConfigureAwait(false);

            NSApplication.SharedApplication.BeginInvokeOnMainThread(
                () => _libraryStatus.StringValue = $"{count} tracks indexed");
        }

        private void Save()
        {
            _config.Current.Jellyfin.ServerUrl = _server.StringValue.Trim();
            _config.Current.Jellyfin.Username = _username.StringValue.Trim();
            _config.Current.Library.Folders = _folderSource.Folders.ToList();

            if (Enum.TryParse<OverlayAnchor>(_anchor.TitleOfSelectedItem, out var anchor))
                _config.Current.Overlay.Anchor = anchor;

            _config.Save();
        }

        // ---------------------------------------------------------------- chrome

        private static NSTextField Heading(string text, double x, double y) =>
            MakeLabel(text, x, y, 200, NSFont.BoldSystemFontOfSize(13));

        private static NSTextField Caption(string text, double x, double y) =>
            MakeLabel(text, x, y, 240, NSFont.SystemFontOfSize(11));

        private static NSTextField MakeLabel(string text, double x, double y, double width, NSFont? font) => new()
        {
            Frame = new CGRect(x, y, width, 18),
            StringValue = text,
            Editable = false,
            Selectable = false,
            Bezeled = false,
            DrawsBackground = false,
            TextColor = Theme.Text,
            Font = font ?? NSFont.SystemFontOfSize(NSFont.SystemFontSize)!,
        };

        private static NSTextField Field(double x, double y, double width, string value) => new()
        {
            Frame = new CGRect(x, y, width, 22),
            StringValue = value,
        };

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
                var label = MakeLabel(_source.Folders[(int)row], 2, 0, tableColumn.Width - 4,
                    NSFont.SystemFontOfSize(11));

                label.LineBreakMode = NSLineBreakMode.TruncatingHead;
                return label;
            }
        }
    }
}
