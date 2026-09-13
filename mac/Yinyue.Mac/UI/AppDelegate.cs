using AppKit;
using Foundation;
using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue.UI
{
    /// <summary>
    /// The menu-bar presence and the app's lifetime — the counterpart to the tray icon and
    /// single-instance handling in App.xaml.cs.
    ///
    /// The status item is not decoration. With LSUIElement there is no Dock icon, no Dock
    /// menu and no Cmd-Q target, so this menu is the only route into the app that does not
    /// depend on a hotkey being registered successfully. The Windows tray menu carries its
    /// own Settings item for exactly this reason: the overlay can auto-hide at an
    /// inconvenient moment, and must never be the only door.
    /// </summary>
    public sealed class AppDelegate : NSApplicationDelegate
    {
        private readonly ConfigService _config;
        private readonly PlaybackService _playback;

        private NSStatusItem? _statusItem;
        private OverlayPanel? _overlay;

        public AppDelegate(ConfigService config, PlaybackService playback)
        {
            _config = config;
            _playback = playback;
        }

        public override void DidFinishLaunching(NSNotification notification)
        {
            // Accessory, matching LSUIElement. Set in code as well as in the plist because a
            // debug launch from a bare binary does not always read the bundle.
            NSApplication.SharedApplication.ActivationPolicy =
                NSApplicationActivationPolicy.Accessory;

            BuildStatusItem();

            // Height is provisional until the applet's contents exist; the stack and its
            // reserved toast rows are the next piece of work.
            const double height = 170;
            _overlay = new OverlayPanel(_config.Current.Overlay, height);

            var applet = new AppletView(new CoreGraphics.CGRect(0, 0, Theme.PanelWidth, height), _playback);
            applet.SettingsRequested += (_, _) => { /* settings window is not built yet */ };
            _overlay.SetContent(applet);

            // --show summons it straight away, so the panel can be looked at without a
            // hotkey manager existing yet.
            if (Environment.GetCommandLineArgs().Contains("--show")) _overlay.ShowOverlay();
        }

        private void BuildStatusItem()
        {
            _statusItem = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Square);

            if (_statusItem.Button is { } button)
            {
                // The same 音樂 mark the Windows tray shows, from the same master in Common/.
                //
                // ONE asset here, not the pair Windows needs. A template image is a mask:
                // AppKit reads only its alpha and draws it in whatever colour the menu bar
                // wants, inverting automatically between light and dark. So there is no
                // tray-light/tray-dark choice to make and nothing to watch for a theme
                // change -- which is App.ApplyTrayIcon's entire job on Windows.
                var mark = NSImage.ImageNamed("menubar");
                if (mark is not null)
                {
                    mark.Template = true;
                    button.Image = mark;
                }
                else
                {
                    button.Title = "Yinyue";
                }

                button.ToolTip = "Yinyue";
            }

            var menu = new NSMenu();
            menu.AddItem(Item("Show Yinyue", (_, _) => _overlay?.ToggleOverlay()));
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(Item("Settings…", (_, _) => { /* settings window is not built yet */ }));
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(Item("Quit Yinyue", (_, _) => NSApplication.SharedApplication.Terminate(this)));

            _statusItem.Menu = menu;
        }

        private static NSMenuItem Item(string title, EventHandler handler)
        {
            var item = new NSMenuItem(title);
            item.Activated += handler;
            return item;
        }

        public override void WillTerminate(NSNotification notification)
        {
            _playback.Dispose();
            _statusItem?.Dispose();
        }
    }
}
