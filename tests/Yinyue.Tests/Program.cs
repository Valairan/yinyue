using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Yinyue;
using Yinyue.Models;
using Yinyue.Services;

// UseWindowsForms adds implicit global usings that collide with the WPF types of the same
// name, and ImplicitUsings does not cover System.IO here.
using System.IO;
using Application = System.Windows.Application;
using TabControl = System.Windows.Controls.TabControl;
using ListBox = System.Windows.Controls.ListBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Yinyue.Tests;

/// <summary>
/// The Windows suite: what genuinely needs a desktop session. XAML that has to parse, panels
/// that have to land in the right place, hotkeys the OS has to actually accept, and the
/// registry seeds the installer leaves behind.
///
/// Queue bookkeeping, search prefixes, volume and mute, persistence and the hotkey
/// configuration moved to tests/Yinyue.Core.Tests when Yinyue.Core was extracted. They were
/// never about Windows, and running them on either machine is the point of the split — so
/// run both suites, not just this one.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main()
    {
        // The suite builds real windows, so it needs a WPF Application for StaticResource
        // lookups. InitializeComponent loads App.xaml without running OnStartup, which
        // would take the single-instance mutex and put an icon in the tray.
        // The suite registers the app's real global hotkeys. A running Yinyue already owns
        // them, so every registration would fail and the run would report a dozen unrelated
        // failures — window tests included, once the first exception shut the Application
        // down. Say the one true thing instead.
        if (System.Diagnostics.Process.GetProcessesByName("Yinyue").Length > 0)
        {
            Console.Error.WriteLine("Yinyue is running. Close it first: the suite registers the same global hotkeys.");
            return 2;
        }

        var app = new App();
        app.InitializeComponent();

        // Groups close their windows as they finish. Under the default OnLastWindowClose, a
        // group that throws part-way can leave the last window closed and take the whole
        // Application down with it, failing everything after with "being shut down".
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        HotkeyTests.Run();
        ScanTests.Run();
        WindowTests.Run();

        return Check.Report();
    }
}

public static class HotkeyTests
{
    public static void Run()
    {
        Check.Group("hotkeys — parsing", () =>
        {
            foreach (var text in new[]
                     {
                         "Ctrl+Alt+Space", "Ctrl+Alt+S", "Ctrl+Shift+F12",
                         "Ctrl+Alt+Plus", "Ctrl+Alt+Minus", "Ctrl+Alt+Pipe",
                         "Ctrl+Alt+Backspace", "Ctrl+Alt+NumPad5", "Alt+Win+M",
                     })
            {
                Check.That($"{text} round-trips",
                    HotkeyBinding.TryParse(text, out var b) && b.ToString() == text);
            }

            Check.That("a bare key is rejected", !HotkeyBinding.TryParse("Space", out _));
            Check.That("modifiers alone are rejected", !HotkeyBinding.TryParse("Ctrl+Alt", out _));
            Check.That("nonsense is rejected", !HotkeyBinding.TryParse("Ctrl+Alt+Bananas", out _));
            Check.That("null is rejected", !HotkeyBinding.TryParse(null, out _));

            Check.That("case is ignored",
                HotkeyBinding.TryParse("ctrl+alt+space", out var lower) && lower.ToString() == "Ctrl+Alt+Space");
            Check.That("non-canonical order normalises",
                HotkeyBinding.TryParse("Win+Alt+M", out var a) &&
                HotkeyBinding.TryParse("Alt+Win+M", out var b2) && a == b2);

            HotkeyBinding.TryParse("Ctrl+Alt+Plus", out var plus);
            Check.Equal("Plus maps to VK_OEM_PLUS", 0xBBu, plus.VirtualKey);
            HotkeyBinding.TryParse("Ctrl+Alt+Backspace", out var back);
            Check.Equal("Backspace maps to VK_BACK", 0x08u, back.VirtualKey);
            Check.That("NOREPEAT is always set",
                (plus.Win32Modifiers & HotkeyBinding.MOD_NOREPEAT) != 0);

            Check.That("a malformed stored value falls back",
                HotkeyBinding.ParseOrDefault("!!", HotkeyConfig.DefaultToggleOverlay).ToString()
                    == HotkeyConfig.DefaultToggleOverlay);
        });



        Check.Group("hotkeys — the OS accepts every default", () =>
        {
            var window = new Window { Width = 0, Height = 0, ShowInTaskbar = false };
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();

            var manager = new HotkeyManager();
            var cfg = new HotkeyConfig();
            cfg.For(HotkeyActions.ToggleShuffle).Hold = true;
            cfg.For(HotkeyActions.RemoveFromQueue).Hold = true;   // must be ignored

            var refused = manager.Apply(hwnd, cfg);

            foreach (var failure in refused) Console.WriteLine($"       refused: {failure}");
            Check.That("nothing refused", refused.Count == 0);
            Check.Equal("all registered", HotkeyActions.All.Length, manager.Active.Count);

            var byAction = manager.Active.Values.ToDictionary(r => r.Action);
            Check.That("an enabled hold is honoured", byAction[HotkeyActions.ToggleShuffle].RequiresHold);
            Check.That("a withheld hold stays off", !byAction[HotkeyActions.RemoveFromQueue].RequiresHold);

            Check.That("arrows remain bindable",
                HotkeyBinding.TryParse("Ctrl+Alt+Up", out _));

            manager.UnregisterAll();
            window.Close();
        });


        Check.Group("hotkeys — Ctrl+Alt+Delete cannot be taken", () =>
        {
            // Reserved by Windows for the Secure Attention Sequence. Recorded so nobody
            // proposes it again.
            Check.That("it parses fine", HotkeyBinding.TryParse("Ctrl+Alt+Delete", out var sas));
            Check.That("but is not among the defaults",
                !HotkeyActions.All.Select(a => new HotkeyConfig().For(a).Keys).Contains("Ctrl+Alt+Delete"));
        });
    }
}

public static class WindowTests
{
    public static void Run()
    {
        Check.Group("windows parse and resources resolve", () =>
        {
            foreach (var key in new[]
                     {
                         "PanelWidth", "BaseBrush", "AccentBrush", "CardStyle",
                         "ToggleSwitchStyle", "ModernComboBoxStyle", "ModernTabControlStyle",
                         "ThinProgressStyle", "KeyCaptureStyle",
                     })
            {
                Check.That($"resource '{key}'", Application.Current!.Resources.Contains(key));
            }

            var config = new ConfigService();
            var indexer = new LibraryIndexerService(AudioPlayerService.NativeContainers);
            var artwork = new ArtworkCache();
            var jellyfin = new JellyfinApiClient(AudioPlayerService.NativeContainers) { DeviceId = config.Current.DeviceId };

            var library = new MusicLibrary(config);
            library.Register(new LocalMusicSource(indexer, artwork));

            var playback = new PlaybackService(new AudioPlayerService(), library);

            var settings = new SettingsWindow(config, jellyfin, indexer);
            Check.That("SettingsWindow parses", true);

            // The caption used to be a literal, and promised .ogg while the engine could not
            // play it. It must now say what the engine says, and only that.
            string caption = (settings.FindName("TxtLocalCaption") as TextBlock)!.Text;
            Check.That("the local caption names the engine's formats",
                AudioPlayerService.NativeContainers.All(caption.Contains));
            Check.That("and nothing the engine lacks", !caption.Contains("ogg"));

            var overlay = new MainWindow(config, library, playback,
                () => new SettingsWindow(config, jellyfin, indexer));
            Check.That("MainWindow parses", true);

            var toast = new ToastWindow(config);
            Check.That("ToastWindow parses", true);

            var tabs = Descendants(settings).OfType<TabControl>().FirstOrDefault();
            var headers = tabs?.Items.OfType<TabItem>().Select(t => t.Header?.ToString()).ToList()
                          ?? new List<string?>();

            foreach (var want in new[] { "Remote", "Local", "General", "Hotkeys" })
                Check.That($"'{want}' tab present", headers.Contains(want));

            foreach (var name in new[]
                     {
                         "TxtServerUrl", "TxtUsername", "TxtPassword", "CmbQuality", "ChkPrebuffer",
                         "ChkOfflineMode", "LstFolders", "ChkScanOnStartup", "ScanProgress",
                         "CmbAnchor", "CmbMonitor", "TxtMarginX", "TxtMarginY", "ChkHideOnFocusLoss",
                         "ChkRunAtLogin", "ChkAnimations", "TxtAnimationMs", "TxtBackgroundOpacity", "ChkAutoHide", "TxtAutoHideSeconds", "TxtHoldDelay", "LstHotkeys", "TxtHotkeyStatus",
                     })
            {
                Check.That($"settings binds {name}", settings.FindName(name) != null);
            }

            int rows = (settings.FindName("LstHotkeys") as ItemsControl)?.ItemsSource
                ?.Cast<object>().Count() ?? 0;
            Check.Equal("a row per action", HotkeyActions.All.Length, rows);

            var texts = Descendants(settings).OfType<TextBlock>().Select(t => t.Text).ToList();
            Check.That("the arrow-key advisory is shown",
                texts.Any(t => t.Contains("arrow keys", StringComparison.OrdinalIgnoreCase)));

            settings.Close();
            overlay.Close();
            toast.Close();
            playback.Dispose();
        });

        Check.Group("startup registration reports honestly", () =>
        {
            // Read-only: registering would actually add the app to this machine's startup.
            Check.That("an executable path is discoverable", StartupService.ExecutablePath != null);
            Check.That("IsEnabled answers without throwing",
                StartupService.IsEnabled || !StartupService.IsEnabled);
        });

        Check.Group("fading follows the animations setting", () =>
        {
            // WPF only layers a window when AllowsTransparency is on, and an unlayered
            // window ignores Opacity outright. Turning animations off therefore has to drop
            // transparency as well, which is where the performance actually goes.
            foreach (bool animate in new[] { true, false })
            {
                var config = new ConfigService();
                config.Current.Overlay.Animations = animate;

                var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
                var toast = new ToastWindow(config);

                string mode = animate ? "on" : "off";
                Check.Equal($"overlay transparency with animations {mode}", animate, overlay.AllowsTransparency);
                Check.Equal($"toast transparency with animations {mode}", animate, toast.AllowsTransparency);

                overlay.Show();
                IntPtr hwnd = new WindowInteropHelper(overlay).Handle;
                Check.Equal($"overlay layered with animations {mode}", animate, IsLayered(hwnd));

                if (animate)
                {
                    overlay.Opacity = 0.5;
                    Check.Equal("opacity applies when animating", 0.5, overlay.Opacity);
                }

                overlay.Close();
                toast.Close();
            }
        });

        Check.Group("hold dial", () =>
        {
            var config = new ConfigService();
            config.Current.Overlay.Animations = true;

            var toast = new ToastWindow(config, ToastWindow.ToastRole.Hold);
            toast.Show("Music", "seed");           // realise the template

            var arc = toast.FindName("HoldArc") as System.Windows.Shapes.Path;
            var track = toast.FindName("HoldTrack") as System.Windows.Shapes.Ellipse;
            var glyph = toast.FindName("IcoGlyph") as FrameworkElement;
            var message = toast.FindName("TxtMessage") as TextBlock;

            Check.That("the dial exists", arc != null && track != null);
            Check.Equal("and is hidden for an ordinary toast", Visibility.Collapsed, arc!.Visibility);

            toast.ShowHoldProgress("Keep holding to clear the queue", 0.0);
            Check.Equal("the dial appears for a hold", Visibility.Visible, arc.Visibility);
            Check.Equal("the glyph steps aside", Visibility.Collapsed, glyph!.Visibility);
            Check.Equal("the label goes in the message line",
                "Keep holding to clear the queue", message!.Text);
            Check.That("an empty sweep draws nothing", arc.Data == Geometry.Empty);

            toast.ShowHoldProgress("Keep holding", 0.5);
            Check.That("a partial sweep is an arc", arc.Data is PathGeometry);

            toast.ShowHoldProgress("Keep holding", 1.0);
            Check.That("a full sweep closes into a circle", arc.Data is EllipseGeometry);

            toast.EndHoldProgress();
            Check.Equal("the dial goes away afterwards", Visibility.Collapsed, arc.Visibility);
            Check.Equal("and the glyph returns", Visibility.Visible, glyph.Visibility);

            toast.Close();
        });

        Check.Group("every surface has its own reserved row", () =>
        {
            var config = new ConfigService();
            config.Current.Overlay.Anchor = OverlayAnchor.BottomRight;

            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
            var message = new ToastWindow(config, ToastWindow.ToastRole.Message);
            var hold = new ToastWindow(config, ToastWindow.ToastRole.Hold);

            overlay.Show();
            new WindowInteropHelper(message).EnsureHandle();
            new WindowInteropHelper(hold).EnsureHandle();

            OverlayPositioner.PositionApplet(overlay, config.Current.Overlay);
            overlay.UpdateLayout();
            Pump();

            message.Show("\u266A", "Something — Someone");
            hold.ShowHoldProgress("Keep holding", 0.5);
            Pump();

            static Rect Of(Window w) => new(w.Left, w.Top, w.ActualWidth, w.ActualHeight);

            var applet = Of(overlay);
            var msgRow = Of(message);
            var holdRow = Of(hold);

            // The order the user asked for, bottom upwards: hold, message, applet.
            Check.That("the message row is below the applet", msgRow.Top >= applet.Bottom - 1);
            Check.That("and the hold row below that", holdRow.Top >= msgRow.Bottom - 1);

            // The whole reason for the rework: a hold happens while the overlay is up, and
            // the dial used to land on top of it.
            Check.That("the hold row misses the applet", !holdRow.IntersectsWith(applet));
            Check.That("the message row misses the applet", !msgRow.IntersectsWith(applet));
            Check.That("and the two rows miss each other", !holdRow.IntersectsWith(msgRow));

            Check.That("every row is applet-width",
                Math.Abs(msgRow.Width - applet.Width) < 1 && Math.Abs(holdRow.Width - applet.Width) < 1);
            Check.That("and left-aligned with it",
                Math.Abs(msgRow.X - applet.X) < 1 && Math.Abs(holdRow.X - applet.X) < 1);

            // The rows are reserved permanently, so the bottom one ends where an unlifted
            // applet would have: nothing moves when a toast appears or goes away.
            Check.That("the stack reaches the bottom of the work area",
                holdRow.Bottom <= SystemParameters.WorkArea.Bottom + 1 &&
                holdRow.Bottom > SystemParameters.WorkArea.Bottom - 60);

            // Rows keep their slot whether or not their neighbour is showing.
            var holdAlone = holdRow;
            message.HideNow();
            Pump();
            hold.ShowHoldProgress("Keep holding", 0.9);
            Pump();
            Check.Equal("the hold row does not move when the message goes",
                holdAlone.Top, Of(hold).Top);

            hold.HideNow();
            message.HideNow();
            hold.Close();
            message.Close();
            overlay.Close();
        });

        Check.Group("shortcut hints name the real binding", () =>
        {
            var config = new ConfigService();
            config.Current.Hotkeys.For(HotkeyActions.OfflineMode).Keys = "Ctrl+Alt+J";

            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);

            string offline = (overlay.FindName("BtnOfflineToggle") as System.Windows.Controls.Button)!
                .ToolTip?.ToString() ?? "";

            // The default moved from Ctrl+Alt+O to Ctrl+Alt+L once O became restart/previous,
            // and a hint written into the markup went on naming the old key.
            Check.That("it quotes the configured key", offline.Contains("Ctrl+Alt+J"));
            Check.That("not a stale default", !offline.Contains("Ctrl+Alt+O"));

            string favourites = (overlay.FindName("BtnShuffleFavorites") as System.Windows.Controls.Button)!
                .ToolTip?.ToString() ?? "";
            Check.That("favourites quotes its own binding",
                favourites.Contains(config.Current.Hotkeys.For(HotkeyActions.ShuffleFavorites).Keys));

            // Clearing a binding is not "unbound": For() substitutes the default, so the
            // hint follows it there rather than going blank.
            config.Current.Hotkeys.For(HotkeyActions.OfflineMode).Keys = "";
            var reset = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
            string back = (reset.FindName("BtnOfflineToggle") as System.Windows.Controls.Button)!
                .ToolTip?.ToString() ?? "";
            Check.That("a cleared binding falls back to the default",
                back.Contains(HotkeyConfig.DefaultOfflineMode));

            reset.Close();
            overlay.Close();
        });

        Check.Group("queue keeps your place while removing", () =>
        {
            var playback = FadePlayback();

            // RestoreQueue builds the queue without starting playback, which is all these
            // checks need and keeps them off the audio device.
            playback.RestoreQueue(Make.Tracks("a", "b", "c", "d", "e").ToList(), 0,
                TimeSpan.Zero, shuffle: false, LoopMode.Off);

            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), playback, () => null!);

            var list = overlay.FindName("LstQueue") as System.Windows.Controls.ListBox;
            var popup = overlay.FindName("QueuePopup") as System.Windows.Controls.Primitives.Popup;
            Check.That("the queue list exists", list != null && popup != null);

            // The popup needs a realised placement target, and the refresh-on-change handler
            // only runs while it is open — without this the checks below pass vacuously.
            // Anchored deliberately: left at WPF's default cascade position the window lands
            // somewhere different each run, which changes whether the panels stack above or
            // below and made this test depend on luck.
            ShowAndActivate(overlay);
            overlay.ShowQueue();
            Pump();

            Check.That("the queue opened", popup!.IsOpen);
            Check.Equal("opens on the playing track", 0, list!.SelectedIndex);

            // Move to an entry that is not playing and remove it.
            list.SelectedIndex = 3;
            Check.That("removing succeeds", playback.RemoveAt(3));
            Pump();

            // The bug: the highlight used to snap back to the playing track on every
            // refresh, landing on the one entry RemoveAt refuses — so pressing the hotkey
            // again appeared to do nothing at all.
            Check.Equal("the selection stays where it was", 3, list.SelectedIndex);
            Check.Equal("now holding what moved up", "e", playback.PlayOrder[3].Title);

            // And again, so a run of removals keeps working.
            Check.That("a second removal also succeeds", playback.RemoveAt(list.SelectedIndex));
            Pump();
            Check.Equal("clamped to the shortened list", 2, list.SelectedIndex);
            Check.That("still a valid selection",
                list.SelectedIndex >= 0 && list.SelectedIndex < list.Items.Count);

            overlay.Close();
            playback.Dispose();
        });

        Check.Group("queue reopens on the playing track", () =>
        {
            var playback = FadePlayback();
            playback.RestoreQueue(Make.Tracks("a", "b", "c").ToList(), 1,
                TimeSpan.Zero, shuffle: false, LoopMode.Off);

            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), playback, () => null!);
            var list = overlay.FindName("LstQueue") as System.Windows.Controls.ListBox;
            var popup = overlay.FindName("QueuePopup") as System.Windows.Controls.Primitives.Popup;

            ShowAndActivate(overlay);
            overlay.ShowQueue();
            Pump();
            Check.Equal("opens on what is playing", 1, list!.SelectedIndex);

            // Wander off, close, reopen: an open is not an update, so it starts fresh.
            list.SelectedIndex = 2;
            popup!.IsOpen = false;
            overlay.ShowQueue();
            Pump();
            Check.Equal("reopening returns to the playing track", 1, list.SelectedIndex);

            overlay.Close();
            playback.Dispose();
        });

        Check.Group("a search row can be told apart", () =>
        {
            // The real case this fixes: three pressings of one recording, identical until
            // the album is shown. Duration does not separate the first two.
            var studio = new Track { Title = "Hells Bells", Artist = "AC-DC", Album = "Back In Black" };
            var comp = new Track { Title = "Hells Bells", Artist = "AC-DC", Album = "Who Made Who" };

            Check.That("the artist alone does not distinguish them",
                studio.DisplayArtist == comp.DisplayArtist);
            Check.That("the subtitle does", studio.SearchSubtitle != comp.SearchSubtitle);
            Check.Equal("and reads naturally", "AC-DC · Back In Black", studio.SearchSubtitle);

            // A track with no album must not show a dangling separator.
            var bare = new Track { Title = "Demo", Artist = "Someone" };
            Check.Equal("no album, no separator", "Someone", bare.SearchSubtitle);

            var unknown = new Track { Title = "Demo" };
            Check.Equal("no artist either", "Unknown Artist", unknown.SearchSubtitle);

            // Duration formatting matches the overlay clock, and is blank when unknown.
            var timed = new Track { Duration = TimeSpan.FromSeconds(255) };
            Check.Equal("minutes and seconds", "04:15", timed.DurationText);

            var longer = new Track { Duration = TimeSpan.FromMinutes(75) };
            Check.Equal("hours when it runs long", "1:15:00", longer.DurationText);

            Check.Equal("blank when unreported", string.Empty, new Track().DurationText);

            // A collection renders through the same template, so it needs the same members.
            var album = new TrackCollection { Kind = CollectionKind.Album, Artist = "AC-DC" };
            Check.Equal("a collection subtitle says what it is", "Album · AC-DC", album.SearchSubtitle);
            Check.Equal("and has no running time", string.Empty, album.DurationText);
        });

        Check.Group("search placeholder teaches the prefixes", () =>
        {
            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);

            var box = overlay.FindName("SearchTextBox") as System.Windows.Controls.TextBox;
            var hint = overlay.FindName("TxtSearchPlaceholder") as TextBlock;
            Check.That("both exist", box != null && hint != null);

            // The box is permanent now, so the placeholder is governed purely by whether
            // anything has been typed — not by whether search was "opened".
            overlay.OpenSearch();
            Check.Equal("shown on an empty box", Visibility.Visible, hint!.Visibility);

            // It names prefixes that actually parse, or it is teaching a lie.
            foreach (string prefix in new[] { "album", "fav", "queue" })
            {
                Check.That($"{prefix}: is mentioned", hint.Text.Contains(prefix + ":"));
                Check.That($"{prefix}: really parses", SearchQuery.Parse(prefix + ":x").IsScoped);
            }

            box!.Text = "a";
            Check.Equal("hidden once typing starts", Visibility.Collapsed, hint.Visibility);

            box.Text = string.Empty;
            Check.Equal("back when cleared", Visibility.Visible, hint.Visibility);

            overlay.Close();
        });

        Check.Group("the queue hint names the move shortcut", () =>
        {
            var config = new ConfigService();
            config.Current.Hotkeys.For(HotkeyActions.GrabQueueEntry).Keys = "Ctrl+Alt+J";

            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
            var hint = overlay.FindName("TxtQueueHint") as TextBlock;

            ShowAndActivate(overlay);
            overlay.ShowQueue();
            Pump();

            Check.That("it quotes the configured key", hint!.Text.Contains("Ctrl+Alt+J"));
            Check.That("and says what it does", hint.Text.Contains("moves"));
            Check.That("the ordinary actions survive", hint.Text.Contains("Enter plays"));

            overlay.Close();
        });

        Check.Group("a clashing shortcut is flagged inline", () =>
        {
            var config = new ConfigService();
            var indexer = new LibraryIndexerService(AudioPlayerService.NativeContainers);
            var jellyfin = new JellyfinApiClient(AudioPlayerService.NativeContainers) { DeviceId = config.Current.DeviceId };

            var settings = new SettingsWindow(config, jellyfin, indexer);
            var rows = (settings.FindName("LstHotkeys") as ItemsControl)?.ItemsSource
                as System.Collections.Generic.IEnumerable<HotkeyRow>;

            Check.That("the rows were generated", rows != null);
            var list = rows!.ToList();

            Check.That("the defaults are clash-free", list.All(r => r.Conflict == null));

            // Point one action at another's combination: both ends must light up, because a
            // clash disables both shortcuts rather than just the newer one.
            var queue = list.First(r => r.Action == HotkeyActions.OpenQueue);
            var settingsRow = list.First(r => r.Action == HotkeyActions.OpenSettings);

            string taken = settingsRow.Keys;
            queue.Keys = taken;

            Check.That("the row just changed is flagged", queue.Conflict != null);
            Check.That("and so is the one it collided with", settingsRow.Conflict != null);
            Check.That("naming the other action",
                queue.Conflict!.Contains(HotkeyActions.Describe(HotkeyActions.OpenSettings)));
            Check.Equal("so it is visible", Visibility.Visible, queue.ConflictVisibility);

            // Undoing it must clear both, not just the row that moved.
            queue.Keys = "Ctrl+Alt+J";

            Check.That("the flag clears", queue.Conflict == null);
            Check.That("on both rows", settingsRow.Conflict == null);
            Check.Equal("and hides", Visibility.Collapsed, queue.ConflictVisibility);

            settings.Close();
        });

        Check.Group("the search bar is part of the overlay", () =>
        {
            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);

            var bar = overlay.FindName("SearchBarPopup") as System.Windows.Controls.Primitives.Popup;
            var box = overlay.FindName("SearchTextBox") as System.Windows.Controls.TextBox;
            var results = overlay.FindName("SearchPopup") as System.Windows.Controls.Primitives.Popup;
            var status = overlay.FindName("TxtStatus") as TextBlock;

            Check.That("the bar exists", bar != null && box != null);
            Check.That("closed while the overlay is down", !bar!.IsOpen);

            ShowAndActivate(overlay);
            overlay.ShowOverlay();
            Pump();

            Check.That("open as soon as the overlay is", bar.IsOpen);
            Check.Equal("and the box is not hidden", Visibility.Visible, box!.Visibility);

            // The status line no longer gives up its place to the search box.
            Check.Equal("the status line stays", Visibility.Visible, status!.Visibility);

            box.Text = "hells";
            overlay.OpenSearch();
            Pump();

            // Clearing a search puts the results away but must leave the bar alone.
            typeof(MainWindow)
                .GetMethod("CloseSearch", System.Reflection.BindingFlags.NonPublic |
                                          System.Reflection.BindingFlags.Instance)!
                .Invoke(overlay, null);
            Pump();

            Check.Equal("the search is cleared", string.Empty, box.Text);
            Check.That("the results are put away", !results!.IsOpen);
            Check.That("but the bar stays", bar.IsOpen);

            overlay.HideOverlay();
            Pump();
            Check.That("and goes with the overlay", !bar.IsOpen);

            overlay.Close();
        });

        Check.Group("arrowing into the results lands on a row, and keeps moving", () =>
        {
            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);

            var box = (System.Windows.Controls.TextBox)overlay.FindName("SearchTextBox");
            var list = (ListBox)overlay.FindName("LstSearchResults");
            var results = (System.Windows.Controls.Primitives.Popup)overlay.FindName("SearchPopup");

            ShowAndActivate(overlay);
            overlay.ShowOverlay();
            Pump();

            // Results are filled directly rather than through the debounced search, so the
            // test is about the keys and not about timing.
            var items = (System.Collections.ObjectModel.ObservableCollection<object>)typeof(MainWindow)
                .GetField("_searchResults", System.Reflection.BindingFlags.NonPublic |
                                            System.Reflection.BindingFlags.Instance)!
                .GetValue(overlay)!;
            foreach (var track in Make.Tracks("r0", "r1", "r2")) items.Add(track);
            results.IsOpen = true;
            Pump();

            box.Focus();
            Keyboard.Focus(box);
            Pump();
            Check.That("typing starts in the box", Keyboard.FocusedElement == box);

            int FocusedRow() => Keyboard.FocusedElement is ListBoxItem row
                ? list.ItemContainerGenerator.IndexFromContainer(row)
                : -1;

            Press(box, Key.Down);
            Pump();
            Check.Equal("Down from the box focuses the first row itself, not the list", 0, FocusedRow());
            Check.Equal("and highlights it", 0, list.SelectedIndex);

            Press((UIElement)Keyboard.FocusedElement, Key.Down);
            Pump();
            Check.Equal("the next Down moves on", 1, FocusedRow());

            Press((UIElement)Keyboard.FocusedElement, Key.Up);
            Pump();
            Check.Equal("Up comes back", 0, FocusedRow());

            Press((UIElement)Keyboard.FocusedElement, Key.Up);
            Pump();
            Check.That("Up off the top returns to typing", Keyboard.FocusedElement == box);

            // The reported bug: the row was still selected from the first visit, so the
            // second entry changed nothing visible and the Down after it appeared stuck.
            Press(box, Key.Down);
            Pump();
            Check.Equal("re-entering lands on the first row", 0, FocusedRow());

            Press((UIElement)Keyboard.FocusedElement, Key.Down);
            Pump();
            Check.Equal("and the very next Down moves to the second", 1, FocusedRow());

            overlay.Close();
        });

        Check.Group("panels stack above the applet", () =>
        {
            var config = new ConfigService();
            config.Current.Overlay.BackgroundOpacity = 0.8;

            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);

            var root = overlay.FindName("RootBorder") as Border;
            var searchPopup = overlay.FindName("SearchPopup") as System.Windows.Controls.Primitives.Popup;
            var queuePopup = overlay.FindName("QueuePopup") as System.Windows.Controls.Primitives.Popup;
            var searchPanel = overlay.FindName("SearchPanel") as Border;
            var queuePanel = overlay.FindName("QueuePanel") as Border;
            var barPanel = overlay.FindName("SearchBarPanel") as Border;

            Check.That("every piece exists",
                root != null && searchPopup != null && queuePopup != null &&
                searchPanel != null && queuePanel != null);

            double panelWidth = (double)Application.Current.FindResource("PanelWidth");
            Check.Equal("search matches the applet width", panelWidth, searchPopup!.Width);
            Check.Equal("queue matches the applet width", panelWidth, queuePopup!.Width);

            // Relative, not Top: PlacementMode.Top flips the panel *below* the target when
            // there is no room above, which put both panels over the applet.
            Check.Equal("search placement cannot flip",
                System.Windows.Controls.Primitives.PlacementMode.Relative, searchPopup.Placement);
            Check.Equal("queue placement cannot flip",
                System.Windows.Controls.Primitives.PlacementMode.Relative, queuePopup.Placement);
            Check.That("search targets the applet", ReferenceEquals(searchPopup.PlacementTarget, root));
            Check.That("queue targets the applet", ReferenceEquals(queuePopup.PlacementTarget, root));

            // Bottom-right of the work area: the default anchor, and the case this is for.
            ShowAndActivate(overlay);

            var rootFill = root!.Background as SolidColorBrush;
            Check.That("the search panel is tinted like the applet",
                (searchPanel!.Background as SolidColorBrush)?.Color == rootFill!.Color);
            Check.That("and so is the queue",
                (queuePanel!.Background as SolidColorBrush)?.Color == rootFill.Color);
            Check.Equal("corners match the applet", root.CornerRadius, searchPanel.CornerRadius);

            static Rect OnScreen(FrameworkElement e)
            {
                var origin = e.PointToScreen(new System.Windows.Point(0, 0));
                return new Rect(origin.X, origin.Y, e.ActualWidth, e.ActualHeight);
            }

            var applet = OnScreen(root);

            // Queue alone sits directly above the applet.
            overlay.ShowQueue();
            Pump();
            Pump();

            var queueAlone = OnScreen(queuePanel);
            Check.That("the queue is above the applet", queueAlone.Bottom <= applet.Top + 1);
            Check.That("left edges line up", Math.Abs(queueAlone.X - applet.X) < 1);
            Check.That("widths line up", Math.Abs(queueAlone.Width - applet.Width) < 1);

            // Open search: it takes the slot against the applet and the queue moves above it.
            overlay.OpenSearch();
            searchPopup.IsOpen = true;
            Pump();
            Pump();

            var search = OnScreen(searchPanel);
            var queue = OnScreen(queuePanel);

            var bar = OnScreen(barPanel!);

            // Outward from the applet: the search bar, its results, then the queue.
            Check.That("the bar is immediately above the applet", bar.Bottom <= applet.Top + 1);
            Check.That("and close to it", applet.Top - bar.Bottom < 20);
            Check.That("results sit above the bar", search.Bottom <= bar.Top + 1);
            Check.That("the queue moved above results", queue.Bottom <= search.Top + 1);
            Check.That("nothing overlaps",
                !queue.IntersectsWith(search) && !search.IntersectsWith(bar) &&
                !bar.IntersectsWith(applet));
            Check.That("all four stay left-aligned",
                Math.Abs(search.X - applet.X) < 1 && Math.Abs(queue.X - applet.X) < 1 &&
                Math.Abs(bar.X - applet.X) < 1);

            overlay.Close();
        });

        Check.Group("transport buttons hold their size", () =>
        {
            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);

            var play = overlay.FindName("BtnPlayPause") as System.Windows.Controls.Button;
            var loop = overlay.FindName("BtnLoop") as System.Windows.Controls.Button;
            Check.That("the transport buttons exist", play != null && loop != null);

            static double WidthWith(System.Windows.Controls.Button button, string kind)
            {
                ((Yinyue.Controls.Icon)button.Content).Kind = kind;
                button.InvalidateMeasure();
                button.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                return button.DesiredSize.Width;
            }

            // With text glyphs the play triangle was narrower than everything else in the
            // row, and swapping it for the pause bars reflowed the centred row. Icons are a
            // fixed box, so this now guards the property rather than fixing a fault.
            double playing = WidthWith(play!, "Pause");
            double paused = WidthWith(play!, "Play");
            Check.Equal("play and pause measure the same", paused, playing);

            double loopAll = WidthWith(loop!, "Repeat");
            double loopOne = WidthWith(loop!, "Repeat1");
            Check.Equal("both loop icons measure the same", loopOne, loopAll);

            // Every button in the row shares one width, so nothing shifts as states change.
            var previous = overlay.FindName("BtnPrevious") as System.Windows.Controls.Button;
            previous!.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Check.Equal("and match the rest of the row", previous.DesiredSize.Width, playing);

            // The header icons share the transport row's size, so the two rows read as one
            // set of controls rather than a large one and a small one. DesiredSize includes
            // the margin, and only the transport row spaces its buttons out, so compare the
            // button box itself.
            static System.Windows.Size BoxOf(System.Windows.Controls.Button button)
            {
                button.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                return new System.Windows.Size(
                    button.DesiredSize.Width - button.Margin.Left - button.Margin.Right,
                    button.DesiredSize.Height - button.Margin.Top - button.Margin.Bottom);
            }

            var transport = BoxOf(previous);
            foreach (var name in new[] { "BtnBurgerMenu", "BtnOfflineToggle", "BtnShuffleFavorites", "BtnSettingsGear" })
            {
                var icon = (System.Windows.Controls.Button)overlay.FindName(name);
                Check.Equal($"{name} is the size of a transport button", transport, BoxOf(icon));
                Check.Equal($"{name} draws its icon at the transport size",
                    ((Yinyue.Controls.Icon)previous.Content).Size, ((Yinyue.Controls.Icon)icon.Content).Size);
            }

            // Two hearts, two jobs. The toggle offers heart-plus until the track is a
            // favourite, then a filled heart; the header's shuffle-favourites carries the
            // heart-shuffle mark, so the two buttons are told apart at a glance.
            var favourite = (Yinyue.Controls.Icon)overlay.FindName("IcoFavorite");
            var shuffleFavourites = (Yinyue.Controls.Icon)overlay.FindName("IcoShuffleFavorites");
            Check.Equal("the favourite toggle starts as heart-plus", "HeartPlus", favourite.Kind);
            Check.That("and is not filled until it is a favourite", !favourite.Filled);
            Check.Equal("the header carries the heart-shuffle mark", "HeartShuffle", shuffleFavourites.Kind);

            overlay.Close();
        });

        Check.Group("every icon the app names has a geometry", () =>
        {
            // Icons.xaml is generated from win/Assets/Icons; the Kind strings in markup and
            // code are plain text. This is what ties the two together.
            foreach (var kind in new[]
                     {
                         "SkipBack", "SkipForward", "Play", "Pause", "Heart", "HeartPlus", "HeartShuffle",
                         "Search", "List", "Repeat", "Repeat1", "RepeatOff", "VolumeX", "Volume2",
                         "Cloud", "CloudOff", "Shuffle", "Moon", "Cog", "Info", "Music", "MoveVertical",
                     })
            {
                Check.That($"Icon.{kind} is a geometry with something in it",
                    Application.Current!.Resources["Icon." + kind] is Geometry g && !g.IsEmpty());

                var icon = new Yinyue.Controls.Icon { Kind = kind };
                Check.That($"an Icon resolves '{kind}' on its own", icon.Data != null);
            }

            Check.That("an unknown kind draws nothing rather than throwing",
                new Yinyue.Controls.Icon { Kind = "NoSuchIcon" }.Data == null);
        });

        Check.Group("the seek bar and the level bar are one white line", () =>
        {
            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
            ShowAndActivate(overlay);
            overlay.ShowOverlay();
            Pump();

            var slider = (System.Windows.Controls.Slider)overlay.FindName("SliderProgress");
            var seekLine = slider.Template.FindName("SeekLine", slider) as Border;
            Check.That("the seek bar is retemplated", seekLine != null);

            var toast = new ToastWindow(config);
            toast.Show("Volume2", "Volume 60%", 0.6);
            Pump();
            var bar = (System.Windows.Controls.ProgressBar)toast.FindName("LevelBar");
            var indicator = bar.Template.FindName("PART_Indicator", bar) as System.Windows.Shapes.Rectangle;

            Check.Equal("same thickness", bar.ActualHeight, seekLine!.ActualHeight);
            Check.That("the level bar is the icon white",
                indicator != null && indicator.Fill == Application.Current!.Resources["IconBrush"]);

            toast.Close();
            overlay.Close();
        });

        Check.Group("volume steps by the configured amount", () =>
        {
            var config = new ConfigService();
            config.Current.Playback.VolumeStepPercent = 10;

            var playback = FadePlayback();
            playback.Volume = 0.5;
            var overlay = new MainWindow(config, FadeLibrary(), playback, () => null!);

            // No binding means no key to watch, so each call is exactly one step.
            overlay.BeginVolumeRepeat(default, +1);
            Check.Equal("up by the configured step", 0.6, playback.Volume);
            overlay.BeginVolumeRepeat(default, -1);
            Check.Equal("and down again", 0.5, playback.Volume);

            overlay.Close();
            playback.Dispose();
        });

        Check.Group("album art is a centred square", () =>
        {
            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
            ShowAndActivate(overlay);
            overlay.ShowOverlay();
            Pump();

            var frame = (Border)overlay.FindName("ArtFrame");
            var art = (System.Windows.Controls.Image)overlay.FindName("ImgAlbumArt");
            var column = (FrameworkElement)VisualTreeHelper.GetParent(frame);

            Check.Equal("square", frame.ActualWidth, frame.ActualHeight);

            // UniformToFill used to overflow the frame and clip on one side only, which is
            // what put the mark off centre. Now the image fits, and sits centred.
            Check.That("the image does not overflow its frame",
                art.ActualWidth <= frame.ActualWidth + 0.5 && art.ActualHeight <= frame.ActualHeight + 0.5);

            double top = frame.TranslatePoint(new System.Windows.Point(0, 0), column).Y;
            double bottom = column.ActualHeight - (top + frame.ActualHeight);
            Check.That("centred vertically in the column", Math.Abs(top - bottom) < 0.5,
                $"{top:F1} above, {bottom:F1} below");

            overlay.Close();
        });

        Check.Group("animation and opacity settings", () =>
        {
            var overlay = new OverlayConfig();
            Check.That("animations on by default", overlay.Animations);
            overlay.Animations = false;
            Check.That("animations can be turned off", !overlay.Animations);

            Check.Equal("default duration",
                OverlayConfig.DefaultAnimationMilliseconds, overlay.AnimationMilliseconds);

            overlay.AnimationMilliseconds = -50;
            Check.Equal("duration clamped up",
                OverlayConfig.MinAnimationMilliseconds, overlay.AnimationMilliseconds);

            overlay.AnimationMilliseconds = 99999;
            Check.Equal("duration clamped down",
                OverlayConfig.MaxAnimationMilliseconds, overlay.AnimationMilliseconds);

            overlay.AnimationMilliseconds = 0;
            Check.Equal("zero is allowed, meaning instant", 0, overlay.AnimationMilliseconds);

            overlay.AnimationMilliseconds = 250;
            Check.Equal("a sane duration is kept", 250, overlay.AnimationMilliseconds);

            Check.Equal("default opacity",
                OverlayConfig.DefaultBackgroundOpacity, overlay.BackgroundOpacity);

            overlay.BackgroundOpacity = 0.0;
            Check.Equal("opacity floored well above invisible",
                OverlayConfig.MinBackgroundOpacity, overlay.BackgroundOpacity);

            overlay.BackgroundOpacity = 5;
            Check.Equal("opacity capped", OverlayConfig.MaxBackgroundOpacity, overlay.BackgroundOpacity);

            overlay.BackgroundOpacity = double.NaN;
            Check.Equal("NaN falls back",
                OverlayConfig.DefaultBackgroundOpacity, overlay.BackgroundOpacity);

            overlay.BackgroundOpacity = 0.75;
            Check.Equal("a sane opacity is kept", 0.75, overlay.BackgroundOpacity);
        });

        Check.Group("background opacity reaches the panel, not the content", () =>
        {
            var opaque = (System.Windows.Media.SolidColorBrush)
                WindowStyling.Translucent(System.Windows.Media.Colors.CornflowerBlue, 1.0);
            Check.Equal("full opacity is fully opaque", (byte)255, opaque.Color.A);

            var half = (System.Windows.Media.SolidColorBrush)
                WindowStyling.Translucent(System.Windows.Media.Colors.CornflowerBlue, 0.5);
            Check.Equal("half opacity halves the alpha", (byte)128, half.Color.A);
            Check.Equal("the colour itself is untouched",
                System.Windows.Media.Colors.CornflowerBlue.R, half.Color.R);
            Check.That("and the brush is frozen for reuse", half.IsFrozen);

            // The overlay tints its panel; the settings window has chrome and must not.
            var config = new ConfigService();
            config.Current.Overlay.Animations = true;
            config.Current.Overlay.BackgroundOpacity = 0.6;

            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
            var root = overlay.FindName("RootBorder") as System.Windows.Controls.Border;
            var tint = root?.Background as System.Windows.Media.SolidColorBrush;

            Check.That("the overlay panel is tinted", tint != null && tint.Color.A < 255);
            Check.Equal("to the configured amount", (byte)153, tint!.Color.A);

            var indexer = new LibraryIndexerService(AudioPlayerService.NativeContainers);
            var jellyfin = new JellyfinApiClient(AudioPlayerService.NativeContainers) { DeviceId = config.Current.DeviceId };
            var settings = new SettingsWindow(config, jellyfin, indexer);
            Check.That("the settings window stays opaque", !settings.AllowsTransparency);

            settings.Close();
            overlay.Close();
        });

        Check.Group("auto-hide settings", () =>
        {
            var overlay = new OverlayConfig();
            Check.That("on by default", overlay.AutoHide);
            Check.Equal("default delay", OverlayConfig.DefaultAutoHideSeconds, overlay.AutoHideSeconds);

            overlay.AutoHideSeconds = 0.1;
            Check.Equal("clamped up", OverlayConfig.MinAutoHideSeconds, overlay.AutoHideSeconds);

            overlay.AutoHideSeconds = 9999;
            Check.Equal("clamped down", OverlayConfig.MaxAutoHideSeconds, overlay.AutoHideSeconds);

            overlay.AutoHideSeconds = double.NaN;
            Check.Equal("NaN falls back", OverlayConfig.DefaultAutoHideSeconds, overlay.AutoHideSeconds);

            overlay.AutoHideSeconds = 12.5;
            Check.Equal("a sane value is kept", 12.5, overlay.AutoHideSeconds);
        });

        Check.Group("installer choices reach the app", () =>
        {
            const string KeyPath = @"Software\Yinyue\Setup";

            static void WriteSeed(string? anchor, int? startup, string? folder)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
                if (anchor != null) key!.SetValue("OverlayAnchor", anchor);
                if (startup.HasValue) key!.SetValue("StartWithWindows", startup.Value);
                if (folder != null) key!.SetValue("LibraryFolder", folder);
            }

            static bool SeedExists() =>
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath) != null;

            // Nothing left behind on an ordinary launch, which is every launch but the first
            // after an install.
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
            Check.That("no seed, nothing to do", SetupSeedService.Take() == null);

            string folder = Path.Combine(Path.GetTempPath(), "yinyue-seed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            WriteSeed("TopLeft", 0, folder);
            var seed = SetupSeedService.Take();

            Check.That("the seed is read", seed != null);
            Check.Equal("the chosen corner", OverlayAnchor.TopLeft, seed!.Anchor);
            Check.Equal("the startup choice", false, seed.StartWithWindows);
            Check.Equal("the chosen folder", folder, seed.LibraryFolder);

            // Once only: a value that stayed would be re-applied on every launch, silently
            // undoing anything the user changed in settings afterwards.
            Check.That("and consumed", !SeedExists());
            Check.That("so a second launch finds nothing", SetupSeedService.Take() == null);

            var config = new AppConfig();
            config.Overlay.Anchor = OverlayAnchor.BottomRight;

            Check.That("applying it changes the config", SetupSeedService.Apply(seed, config));
            Check.Equal("the anchor moved", OverlayAnchor.TopLeft, config.Overlay.Anchor);
            Check.Equal("the folder was added", 1, config.Library.Folders.Count);

            // Re-running the installer must not throw away folders added since.
            config.Library.Folders.Add(@"C:\Elsewhere");
            SetupSeedService.Apply(seed, config);
            Check.Equal("existing folders survive", 2, config.Library.Folders.Count);
            Check.That("and the seeded one is not duplicated",
                config.Library.Folders.Count(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) == 1);

            // Applying the same values twice is not a change the config needs saving for.
            Check.That("an unchanged apply reports nothing to save",
                !SetupSeedService.Apply(new SetupSeedService.Seed { Anchor = OverlayAnchor.TopLeft }, config));

            try { Directory.Delete(folder, true); } catch { }
        });

        Check.Group("the installer can ask for settings on first launch", () =>
        {
            // Path.Combine builds the registry path, which keeps this file free of escapes.
            string keyPath = Path.Combine("Software", "Yinyue", "Setup");
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);

            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath))
                key!.SetValue("OpenSettings", 1);

            var seed = SetupSeedService.Take();
            Check.That("a seed carrying only the request still counts", seed != null);
            Check.That("and asks for settings", seed!.OpenSettings);

            // Opening a window is the app's job, not a configuration change.
            Check.That("applying it leaves the config alone", !SetupSeedService.Apply(seed, new AppConfig()));

            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath))
                key!.SetValue("OverlayAnchor", "Center");

            var plain = SetupSeedService.Take();
            Check.That("absent means no request", plain != null && !plain.OpenSettings);

            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
        });

        Check.Group("a bad seed is discarded, not obeyed", () =>
        {
            const string KeyPath = @"Software\Yinyue\Setup";

            static void Write(string name, object value)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
                key!.SetValue(name, value);
            }

            // An anchor the app does not recognise, from a hand-edited registry or a newer
            // installer, must not stop the app or land as a default.
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
            Write("OverlayAnchor", "SomewhereElse");
            Check.That("an unknown anchor yields no seed", SetupSeedService.Take() == null);
            Check.That("and is cleared anyway, not re-read forever",
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath) == null);

            // The installer cannot know the folder still exists by the time the app runs.
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
            Write("LibraryFolder", @"Z:\gone\missing");
            Check.That("a vanished folder is not offered", SetupSeedService.Take() == null);

            // An empty folder value is how "I chose nothing" arrives, since MSI properties
            // cannot hold an empty string until one is set.
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
            Write("LibraryFolder", "");
            Write("OverlayAnchor", "Center");
            var seed = SetupSeedService.Take();
            Check.That("the rest of the seed still applies", seed != null);
            Check.Equal("with the anchor", OverlayAnchor.Center, seed!.Anchor);
            Check.That("and no folder", seed.LibraryFolder == null);

            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
        });

        Check.Group("sleep timer cycles the configured steps", () =>
        {
            using var timer = new SleepTimerService();

            Check.Equal("starts off", 0, timer.Minutes);
            Check.Equal("then the first default", 15, timer.Cycle());
            Check.Equal("then 30", 30, timer.Cycle());
            Check.Equal("then 45", 45, timer.Cycle());
            Check.Equal("then 60", 60, timer.Cycle());
            Check.Equal("then back to off", 0, timer.Cycle());

            // A working day needs longer steps than a nap does.
            timer.SetSteps(new[] { 60, 240, 480 });
            Check.Equal("an hour first", 60, timer.Cycle());
            Check.Equal("then four", 240, timer.Cycle());
            Check.Equal("then eight", 480, timer.Cycle());
            Check.Equal("off is always reachable", 0, timer.Cycle());

            timer.Set(240);
            Check.That("running once set", timer.IsRunning);
            Check.That("counts down from roughly the full period",
                timer.Remaining > TimeSpan.FromMinutes(239));

            // Changing the steps out from under a running timer must not strand it on a
            // duration the cycle can no longer reach.
            timer.SetSteps(new[] { 15, 30 });
            Check.That("a stranded timer is stopped", !timer.IsRunning);
            Check.Equal("and reports nothing remaining", TimeSpan.Zero, timer.Remaining);

            timer.Set(15);
            timer.Cancel();
            Check.That("cancelled", !timer.IsRunning);
        });

        Check.Group("sleep timer says how long is left", () =>
        {
            // Coarse far out, precise near the end.
            Check.Equal("hours and minutes", "2h 05m",
                SleepTimerService.Describe(TimeSpan.FromMinutes(125)));
            Check.Equal("whole hours drop the minutes", "8h",
                SleepTimerService.Describe(TimeSpan.FromHours(8)));
            Check.Equal("under an hour is minutes", "42m",
                SleepTimerService.Describe(TimeSpan.FromMinutes(42)));
            Check.Equal("the last minute counts seconds", "45s",
                SleepTimerService.Describe(TimeSpan.FromSeconds(45)));

            // Rounded up, or it would read "0m" with 30 seconds of music still to come.
            Check.Equal("part-minutes round up", "3m",
                SleepTimerService.Describe(TimeSpan.FromSeconds(121)));
            Check.Equal("nothing left, nothing said", string.Empty,
                SleepTimerService.Describe(TimeSpan.Zero));
            Check.Equal("and the same past zero", string.Empty,
                SleepTimerService.Describe(TimeSpan.FromSeconds(-5)));

            // The overlay shows it only while the timer runs.
            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
            var readout = overlay.FindName("TxtSleepRemaining") as TextBlock;

            Check.That("the readout exists", readout != null);
            Check.Equal("hidden by default", Visibility.Collapsed, readout!.Visibility);

            overlay.ShowSleepRemaining(TimeSpan.FromMinutes(90));
            Check.Equal("shown while running", Visibility.Visible, readout.Visibility);
            Check.That("and says how long", readout.Text.Contains("1h 30m"));

            overlay.ShowSleepRemaining(TimeSpan.Zero);
            Check.Equal("hidden again once it stops", Visibility.Collapsed, readout.Visibility);

            overlay.Close();
        });

        Check.Group("sleep timer can be turned off entirely", () =>
        {
            using var timer = new SleepTimerService();

            timer.Set(30);
            Check.That("armed", timer.IsRunning);

            // Disabling must cancel what is running: a countdown alive under a disabled
            // feature would stop the music with nothing on screen explaining why.
            timer.Enabled = false;
            Check.That("turning it off cancels the countdown", !timer.IsRunning);
            Check.Equal("and clears the setting", 0, timer.Minutes);

            Check.Equal("the shortcut does nothing while off", 0, timer.Cycle());
            Check.That("still not running", !timer.IsRunning);

            timer.Set(30);
            Check.That("and cannot be armed directly either", !timer.IsRunning);

            timer.Enabled = true;
            Check.Equal("it works again once allowed", 15, timer.Cycle());
        });

        Check.Group("sleep timer steps are sanitised", () =>
        {
            var cfg = new SleepTimerConfig();
            Check.That("allowed by default", cfg.Enabled);
            Check.Equal("with the usual steps", "15,30,45,60", string.Join(",", cfg.Steps));

            cfg.Steps = new List<int> { 480, 60, 60, 0, -5, 240 };
            Check.Equal("junk dropped, deduped and sorted",
                "60,240,480", string.Join(",", cfg.Steps));

            cfg.Steps = new List<int> { 99999 };
            Check.Equal("beyond a day is not a sleep timer",
                string.Join(",", SleepTimerConfig.DefaultSteps), string.Join(",", cfg.Steps));

            cfg.Steps = new List<int>();
            Check.Equal("an empty list falls back rather than emptying the cycle",
                string.Join(",", SleepTimerConfig.DefaultSteps), string.Join(",", cfg.Steps));

            cfg.Steps = Enumerable.Range(1, 30).ToList();
            Check.Equal("capped so the far end stays reachable",
                SleepTimerConfig.MaxSteps, cfg.Steps.Count);

            Check.Equal("a day is the ceiling", 1440, SleepTimerConfig.MaxStepMinutes);
        });
    }

    private static ConfigService FadeConfig() => new();

    private static MusicLibrary FadeLibrary()
    {
        var (library, _) = Make.Library(
            new FakeSource("Fake", Yinyue.Models.TrackSource.Local, Make.Tracks("t0")));
        return library;
    }

    /// <summary>
    /// Puts a window where the overlay actually lives. Without this a test window lands at
    /// WPF's cascade position, which moves between runs and decides whether the panels stack
    /// above the applet or fall back to below it.
    /// </summary>
    private static void AnchorBottomRight(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.Left = area.Right - window.Width - 12;
        window.Top = area.Bottom - window.Height - 12;
    }

    /// <summary>
    /// Shows a window the way the overlay is actually shown.
    ///
    /// Activation matters: the panels are StaysOpen="False" popups, which Windows dismisses
    /// as soon as the owning app stops being active — and a test window shown among a dozen
    /// others is not active by default. Without this the panels close the moment they open,
    /// which looks like a placement bug and is not one.
    /// </summary>
    /// <summary>
    /// Presses a key on the focused element the way the OS would, through the input manager.
    ///
    /// Raising the routed events by hand is not enough: WPF's arrow navigation between list
    /// rows happens in <c>KeyboardNavigation</c>'s post-processing of the input, after the
    /// KeyDown event has finished unhandled — a raised event ends before that stage, so the
    /// list highlights nothing and a test would fail against a control that works fine.
    /// <c>ProcessInput</c> runs the whole staging area: Preview, promotion to KeyDown, and
    /// the navigation afterwards. The routed event has no source, so the input manager
    /// routes it to the keyboard's focused element — which is why the caller focuses first.
    ///
    /// One more thing real input does that a synthetic event does not: it marks the keyboard
    /// as the most recent input device. <c>ListBox.OnGotKeyboardFocus</c> makes selection
    /// follow focus only under that condition (see the WPF source), so without it the
    /// highlight would stay behind while focus moved — a failure the app never shows.
    /// </summary>
    private static void Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)!;

        typeof(InputManager).GetProperty(nameof(InputManager.MostRecentInputDevice))!
            .SetValue(InputManager.Current, Keyboard.PrimaryDevice);

        InputManager.Current.ProcessInput(
            new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
    }

    private static void ShowAndActivate(Window window)
    {
        AnchorBottomRight(window);
        window.Show();
        window.Activate();
        Pump();
    }

    private static PlaybackService FadePlayback() =>
        new(new AudioPlayerService(), FadeLibrary());

    /// <summary>
    /// Runs everything already queued on the dispatcher.
    ///
    /// The overlay refreshes the queue through <c>Dispatcher.BeginInvoke</c>, and restores
    /// focus at Input priority after that, so a test reading the list straight after a
    /// change would see the state from before it. Draining at Background priority — lower
    /// than both — guarantees they have run.
    /// </summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();

        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));

        Dispatcher.PushFrame(frame);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private static bool IsLayered(IntPtr hwnd) => (GetWindowLong(hwnd, -20) & 0x00080000) != 0;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
