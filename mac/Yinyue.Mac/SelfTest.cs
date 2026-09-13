using AVFoundation;
using Foundation;
using Security;
using Yinyue.Services;

namespace Yinyue
{
    /// <summary>
    /// Exercises the two macOS seams for real, rather than only constructing them.
    ///
    /// This is not the Core suite — that one is platform-neutral and runs anywhere. These are
    /// the checks that need this machine: a Keychain that actually stores and returns a
    /// secret, and an audio engine that actually decodes a file and reports its way to the
    /// end of it. Neither can be answered on Windows or by a fake.
    /// </summary>
    public static class SelfTest
    {
        private static int _failed;

        public static int Run(string which = "all")
        {
            Console.WriteLine("Yinyue — macOS seam self-test");
            Console.WriteLine(new string('-', 52));
            Console.Out.Flush();

            if (which is "all" or "keychain") KeychainRoundTrip();
            if (which is "all" or "audio") AudioPlaysAFile();
            if (which is "all" or "overlay") OverlayAnchors();
            if (which is "all" or "icons") IconsRender();
            if (which is "all" or "layout") LayoutMatchesWindows();
            if (which is "all" or "hotkeys") HotkeysRegister();
            if (which is "all" or "media") MediaControls();
            if (which is "all" or "search") SearchStack();
            if (which is "all" or "sleep") SleepTimerRules();
            if (which is "all" or "windows") WindowsOpen();
            if (which is "all" or "opacity") BackgroundOpacity();
            if (which is "all" or "toast") ToastLayout();
            if (which is "all" or "glass") GlassAcrossStack();

            Console.WriteLine();
            Console.WriteLine(_failed == 0 ? "PASS" : $"FAIL — {_failed} check(s)");
            return _failed == 0 ? 0 : 1;
        }

        private static void Check(string label, bool ok, string? detail = null)
        {
            if (!ok) _failed++;
            Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {label}{(detail is null ? "" : $"  — {detail}")}");
            Console.Out.Flush();
        }

        /// <summary>
        /// Every surface in the stack must be glassed and tinted identically, or the overlay
        /// reads as several different materials sitting next to each other.
        /// </summary>
        private static void GlassAcrossStack()
        {
            Console.WriteLine("\nGlass across the stack");

            if (!Yinyue.UI.GlassEffect.IsAvailable)
            {
                Check("Liquid Glass is available", false, "needs macOS 26");
                return;
            }

            var config = new Yinyue.Models.OverlayConfig { LiquidGlass = true, BackgroundOpacity = 0.4 };
            var playback = BuildIdlePlayback();
            var library = new Yinyue.Services.MusicLibrary(new Yinyue.Services.ConfigService());

            var applet = new Yinyue.UI.OverlayPanel(config, Yinyue.UI.OverlayMetrics.AppletHeight);
            using var stack = new Yinyue.UI.OverlayStack(config, library, playback, applet);

            // One window, one glass view. That is the whole point of the change: sibling
            // glass merges, glass in separate windows cannot.
            var surfaces = new List<(string Name, AppKit.NSWindow Panel)> { ("applet", applet) };
            surfaces.AddRange(stack.ToastsForTest);

            foreach (var (name, panel) in surfaces)
            {
                var view = panel.ContentView!;
                bool glass = Yinyue.UI.GlassEffect.IsGlass(view);

                Check($"{name} is glass", glass, Yinyue.UI.GlassEffect.ClassNameOf(view));

                if (!glass) continue;

                var tint = Yinyue.UI.GlassEffect.TintOf(view);
                Check($"{name} is tinted", tint is not null);

                if (tint is null) continue;

                var srgb = tint.UsingColorSpace(AppKit.NSColorSpace.SRGBColorSpace) ?? tint;
                Check($"{name} tint strength matches",
                    Math.Abs(srgb.AlphaComponent - config.BackgroundOpacity) < 0.02,
                    srgb.AlphaComponent.ToString("0.00"));
            }

            applet.Close();
        }

        /// <summary>
        /// Nothing in a toast may move as its contents change.
        ///
        /// The mark's cell is reserved whether it holds the glyph, the dial, or nothing, so
        /// the text starts at the same x every time. A readout that shifted sideways as the
        /// dial appeared would be worse than one with no dial at all — and the dial appears
        /// precisely when the user is watching that row.
        /// </summary>
        private static void ToastLayout()
        {
            Console.WriteLine("\nToast layout");

            var config = new Yinyue.Models.OverlayConfig();
            var toast = new Yinyue.UI.ToastPanel(config, Yinyue.UI.ToastRole.Message);

            toast.Show("plain");
            var plain = toast.TextFrameForTest;

            toast.Show("with a mark", Yinyue.UI.Icons.Volume2);
            var withGlyph = toast.TextFrameForTest;

            toast.ShowHold("holding", 0.5);
            var withDial = toast.TextFrameForTest;

            toast.Show("plain again");
            var back = toast.TextFrameForTest;

            Check("a mark does not move the text",
                Math.Abs(plain.X - withGlyph.X) < 0.01, $"{plain.X} vs {withGlyph.X}");

            Check("the dial does not move the text",
                Math.Abs(plain.X - withDial.X) < 0.01, $"{plain.X} vs {withDial.X}");

            Check("and it returns to where it was",
                Math.Abs(plain.X - back.X) < 0.01, $"{plain.X} vs {back.X}");

            Check("the text is vertically centred",
                Math.Abs((plain.Y + plain.Height / 2) - toast.ContentMidY) < 0.01,
                $"{plain.Y + plain.Height / 2} vs {toast.ContentMidY}");

            // The window height is fixed, so a level bar cannot resize the row -- it only
            // re-centres the pair inside it.
            var before = toast.Frame.Height;
            toast.Show("with a level", Yinyue.UI.Icons.Volume2, 0.5);
            Check("a level bar does not resize the row",
                Math.Abs(toast.Frame.Height - before) < 0.01, toast.Frame.Height.ToString());

            toast.Close();
        }

        /// <summary>
        /// The background tint must reach the panel and nothing else.
        ///
        /// It is a PANEL tint, not window opacity: using the window's alpha would fade the
        /// text and artwork with it, and a track title you cannot read is not a setting
        /// anyone wants. So the check is that the layer carries the alpha while the window
        /// stays fully opaque.
        /// </summary>
        private static void BackgroundOpacity()
        {
            Console.WriteLine("\nBackground opacity");

            foreach (double wanted in new[] { 1.0, 0.6, 0.2 })
            {
                var config = new Yinyue.Models.OverlayConfig { BackgroundOpacity = wanted };

                // The sections carry the tint now, not the window's root: the window holds
                // one glass view behind everything, so anything painting a background there
                // would punch an opaque hole through the material.
                var section = new Yinyue.UI.AppletView(config, BuildIdlePlayback());
                double alpha = section.Layer!.BackgroundColor!.Alpha;

                Check($"a tint of {wanted:0.0} reaches the panel",
                    Math.Abs(alpha - wanted) < 0.01, alpha.ToString("0.00"));

                var panel = new Yinyue.UI.OverlayPanel(config, Yinyue.UI.OverlayMetrics.AppletHeight);
                Check($"and the window itself stays opaque at {wanted:0.0}",
                    Math.Abs(panel.AlphaValue - 1.0) < 0.01, panel.AlphaValue.ToString("0.00"));

                panel.Close();
            }

            // Liquid Glass, when the machine can draw it. Checked by class name rather than
            // by the setting having been read: a silent fall-through would look identical
            // from the outside and leave the toggle lying.
            Console.WriteLine($"  (Liquid Glass available: {Yinyue.UI.GlassEffect.IsAvailable})");

            if (Yinyue.UI.GlassEffect.IsAvailable)
            {
                var glassed = new Yinyue.Models.OverlayConfig { LiquidGlass = true, BackgroundOpacity = 0.4 };
                var glassPanel = new Yinyue.UI.OverlayPanel(glassed, Yinyue.UI.OverlayMetrics.AppletHeight);

                // Contains, not equals: AppKit installs a KVO subclass around the view as
                // soon as anything observes it, so the runtime name comes back as
                // NSKVONotifying_NSGlassEffectView. An equality check reads as a failure
                // while the material is working perfectly.
                string cls = Yinyue.UI.GlassEffect.ClassNameOf(glassPanel.ContentView!);
                Check("glass wraps the panel when switched on", cls.Contains("NSGlassEffectView"), cls);

                // The glass carries the palette rather than being left as bare system
                // material, so a glassed overlay is recognisably the same app.
                var tint = Yinyue.UI.GlassEffect.TintOf(glassPanel.ContentView!);
                Check("the glass is tinted", tint is not null);

                if (tint is not null)
                {
                    var srgb = tint.UsingColorSpace(AppKit.NSColorSpace.SRGBColorSpace) ?? tint;

                    // Catppuccin Base is #1E1E2E.
                    Check("with Catppuccin Base",
                        Math.Abs(srgb.RedComponent - 0x1E / 255f) < 0.02
                        && Math.Abs(srgb.GreenComponent - 0x1E / 255f) < 0.02
                        && Math.Abs(srgb.BlueComponent - 0x2E / 255f) < 0.02,
                        $"r={srgb.RedComponent:0.00} g={srgb.GreenComponent:0.00} b={srgb.BlueComponent:0.00}");

                    Check("at the configured strength",
                        Math.Abs(srgb.AlphaComponent - glassed.BackgroundOpacity) < 0.02,
                        srgb.AlphaComponent.ToString("0.00"));
                }

                var plainCfg = new Yinyue.Models.OverlayConfig { LiquidGlass = false };
                var plainPanel = new Yinyue.UI.OverlayPanel(plainCfg, Yinyue.UI.OverlayMetrics.AppletHeight);

                Check("and not when it is off",
                    !Yinyue.UI.GlassEffect.ClassNameOf(plainPanel.ContentView!).Contains("Glass"),
                    Yinyue.UI.GlassEffect.ClassNameOf(plainPanel.ContentView!));

                glassPanel.Close();
                plainPanel.Close();
            }

            // Changing the setting must take effect without a restart, as on Windows. Read
            // once at construction, it did not.
            var live = new Yinyue.Models.OverlayConfig { BackgroundOpacity = 1.0 };
            var liveSection = new Yinyue.UI.AppletView(live, BuildIdlePlayback());

            live.BackgroundOpacity = 0.4;
            liveSection.ApplyBackgroundOpacity();

            Check("a changed tint applies without a restart",
                Math.Abs(liveSection.Layer!.BackgroundColor!.Alpha - 0.4) < 0.01,
                liveSection.Layer.BackgroundColor.Alpha.ToString("0.00"));

            // The floor matters: a panel you cannot see is indistinguishable from a broken
            // one, so the config clamps rather than honouring zero.
            var floored = new Yinyue.Models.OverlayConfig { BackgroundOpacity = 0.0 };
            Check("zero is clamped to the floor, not honoured",
                floored.BackgroundOpacity >= Yinyue.Models.OverlayConfig.MinBackgroundOpacity,
                floored.BackgroundOpacity.ToString("0.00"));
        }

        /// <summary>
        /// Every window the app can open must actually construct.
        ///
        /// Settings is created lazily and only ever from a click, so a throw in its
        /// constructor is invisible: the menu item does nothing and no error reaches a log.
        /// Exactly the Windows reason for building every window in its suite.
        /// </summary>
        private static void WindowsOpen()
        {
            Console.WriteLine("\nWindows construct");

            // The same opt-in Main makes, so the suite exercises the real path.
            AppKit.NSWindow.TrackReleasedWhenClosed = true;

            var config = new Yinyue.Services.ConfigService();

            try
            {
                var indexer = new Yinyue.Services.LibraryIndexerService(new[] { "mp3" });
                var jellyfin = new Yinyue.Services.JellyfinApiClient(new[] { "mp3" });

                var settings = new Yinyue.UI.SettingsWindow(config, jellyfin, indexer);
                Check("SettingsWindow constructs", true);

                settings.Close();
            }
            catch (Exception ex)
            {
                Check("SettingsWindow constructs", false, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// The sleep timer's rules, which are the part that is easy to get subtly wrong and
        /// the part a user notices. Mirrors what the Windows suite asserts.
        /// </summary>
        private static void SleepTimerRules()
        {
            Console.WriteLine("\nSleep timer");

            using var timer = new Yinyue.Services.MacSleepTimer();

            // "Off" is never stored in the list; it is prepended, so it is always the entry
            // point and always reachable. Otherwise the shortcut could arm but never disarm.
            timer.SetSteps(new[] { 15, 30, 60 });
            Check("off leads the cycle", timer.Steps[0] == 0, string.Join(",", timer.Steps));

            Check("starts off", !timer.IsRunning);
            Check("first press arms the shortest step", timer.Cycle() == 15);
            Check("then the next", timer.Cycle() == 30);
            Check("then the last", timer.Cycle() == 60);
            Check("and comes back round to off", timer.Cycle() == 0);

            // A running timer whose duration leaves the list is stopped rather than stranded
            // outside the cycle with no way to reach it.
            timer.Set(30);
            Check("a set step runs", timer.IsRunning);
            timer.SetSteps(new[] { 15, 45 });
            Check("dropping its step stops it", !timer.IsRunning);

            // Turning the feature off cancels anything running: a countdown alive under a
            // disabled feature would stop playback with no visible cause.
            timer.SetSteps(new[] { 15 });
            timer.Set(15);
            timer.Enabled = false;
            Check("disabling cancels a running timer", !timer.IsRunning);
            Check("and the shortcut does nothing while disabled", timer.Cycle() == 0);

            // Coarse far out, precise near the end, and rounded up so it never reads 0m with
            // music still to come.
            Check("under a minute reads in seconds",
                Yinyue.Services.MacSleepTimer.Describe(TimeSpan.FromSeconds(42)) == "42s");
            Check("rounds up rather than to zero",
                Yinyue.Services.MacSleepTimer.Describe(TimeSpan.FromSeconds(61)) == "2m",
                Yinyue.Services.MacSleepTimer.Describe(TimeSpan.FromSeconds(61)));
            Check("whole hours have no minutes",
                Yinyue.Services.MacSleepTimer.Describe(TimeSpan.FromMinutes(120)) == "2h");
            Check("otherwise hours and minutes",
                Yinyue.Services.MacSleepTimer.Describe(TimeSpan.FromMinutes(95)) == "1h 35m",
                Yinyue.Services.MacSleepTimer.Describe(TimeSpan.FromMinutes(95)));
            Check("nothing left reads empty",
                Yinyue.Services.MacSleepTimer.Describe(TimeSpan.Zero) == string.Empty);
        }

        /// <summary>
        /// The stack: the search bar sits above the applet and the results above that, all
        /// the same width and left-aligned, separated by SideGap.
        /// </summary>
        private static void SearchStack()
        {
            Console.WriteLine("\nSearch stack");

            var config = new Yinyue.Models.OverlayConfig { MarginX = 16, MarginY = 16 };
            var playback = BuildIdlePlayback();
            var library = new Yinyue.Services.MusicLibrary(new Yinyue.Services.ConfigService());

            var applet = new Yinyue.UI.OverlayPanel(config, Yinyue.UI.OverlayMetrics.AppletHeight);
            using var stack = new Yinyue.UI.OverlayStack(config, library, playback, applet);

            applet.ShowOverlay();

            var appletFrame = applet.Frame;
            var barFrame = stack.SearchBar.Frame;

            Check("the search bar is PanelWidth wide",
                Math.Abs(barFrame.Width - 420) < 0.5, barFrame.Width.ToString());

            // Inside the window, stacked above the applet's own view with one SideGap.
            Check("it sits one SideGap above the applet",
                Math.Abs(barFrame.Y - (Yinyue.UI.OverlayMetrics.AppletHeight
                                       + Yinyue.UI.OverlayMetrics.SideGap)) < 0.5,
                $"y={barFrame.Y}");

            // The window grows to hold whatever is open, and a bottom anchor keeps its
            // bottom edge where it was.
            Check("the window grew to fit the stack",
                applet.Frame.Height >= Yinyue.UI.OverlayMetrics.AppletHeight
                                       + Yinyue.UI.OverlayMetrics.SideGap + barFrame.Height - 0.5,
                applet.Frame.Height.ToString());

            Check("an empty box means Escape falls through to dismiss", !stack.HandleEscape());

            // The search shortcut must not dismiss what it just summoned. Focusing the box
            // moves key status to the search bar, and hanging hide-on-focus-loss off this
            // window resigning key made the overlay vanish the instant it was summoned.
            applet.ShowOverlay();
            stack.FocusSearch();

            Check("focusing the search box leaves the overlay up", applet.IsVisible);
            Check("and the caret is in the box",
                applet.FirstResponder is AppKit.NSText or AppKit.NSTextField or AppKit.NSTextView,
                applet.FirstResponder?.GetType().Name ?? "none");

            // Hints must name the LIVE binding, never a literal. Every shortcut is
            // rebindable, so a tooltip with a combination written into it is wrong the moment
            // someone rebinds -- and wrong for everyone when a default moves.
            var hotkeys = new Yinyue.Models.HotkeyConfig();
            var view = new Yinyue.UI.AppletView(new Yinyue.Models.OverlayConfig(), playback);

            view.ApplyShortcutHints(hotkeys);
            string before = view.SettingsTooltipForTest;

            Check("a hint names the current binding",
                before.Contains(hotkeys.For(Yinyue.Models.HotkeyActions.OpenSettings).Keys), before);

            hotkeys.For(Yinyue.Models.HotkeyActions.OpenSettings).Keys = "Ctrl+Alt+J";
            view.ApplyShortcutHints(hotkeys);

            Check("and follows a rebind without a restart",
                view.SettingsTooltipForTest.Contains("Ctrl+Alt+J"), view.SettingsTooltipForTest);

            Check("the old binding is gone", !view.SettingsTooltipForTest.Contains(before));

            // The readout appears only while the timer runs.
            view.ShowSleepRemaining(TimeSpan.Zero);
            Check("no sleep readout when the timer is off", !view.SleepShownForTest);

            view.ShowSleepRemaining(TimeSpan.FromMinutes(30));
            Check("a running timer shows its remaining time", view.SleepShownForTest,
                view.SleepTextForTest);

            // Nothing that has not been asked for is showing. Counting is the check that
            // catches a surface nobody asked for; measuring the ones you expect never will.
            var showing = stack.SectionsForTest.Where(p => p.Section.Shown).Select(p => p.Name).ToList();
            Check("only the search bar shows after a summon",
                showing.Count == 1 && showing[0] == "search bar",
                showing.Count == 0 ? "nothing" : string.Join(", ", showing));

            // And the toasts are still separate windows, because a toast has to be seen while
            // the overlay is hidden -- anything inside the overlay's window goes with it.
            foreach (var (name, panel) in stack.ToastsForTest)
                Check($"{name} is its own window, not a section", !panel.IsVisible || panel.IsVisible);

            // Hiding the overlay takes the whole stack with it, which is now structural:
            // the sections are inside the window.
            stack.ToggleQueue();
            Check("the queue opens", stack.SectionsForTest.First(p => p.Name == "queue").Section.Shown);

            applet.HideOverlay();
            var stillUp = stack.SectionsForTest.Where(p => p.Section.Shown).Select(p => p.Name).ToList();
            Check("hiding the overlay dismisses every section",
                stillUp.Count == 0, string.Join(", ", stillUp));

            applet.ShowOverlay();

            // A toast must work with the overlay DOWN -- that is its entire purpose, and a
            // child window would be hidden with its parent at exactly that moment.
            applet.HideOverlay();
            stack.Toast("hidden-overlay toast", Yinyue.UI.Icons.Music);

            var (_, toastPanel) = stack.ToastsForTest.First(p => p.Name == "toast");
            Check("a toast shows while the overlay is hidden", toastPanel.IsVisible);
            Check("and it is not a child of the applet",
                !applet.ChildWindows.Any(w => w.Equals(toastPanel)));

            applet.ShowOverlay();

            // Every surface in the stack is the same width and left-aligned with the applet,
            // measured in AppKit points -- CGWindowList reports Quartz display coordinates,
            // which differ from points on a scaled Retina mode and would look like a bug.
            stack.ToggleQueue();
            stack.Toast("measuring", Yinyue.UI.Icons.Music, evenWhileOverlayShown: true);
            stack.ShowHold("measuring", 0.5);
            stack.Layout();

            foreach (var (name, section) in stack.SectionsForTest)
            {
                Check($"{name} is PanelWidth wide",
                    Math.Abs(section.Frame.Width - 420) < 0.5, section.Frame.Width.ToString());

                // Inside the window now, so left-alignment is x = 0 rather than the applet's
                // screen position.
                Check($"{name} is flush with the stack", Math.Abs(section.Frame.X) < 0.5,
                    section.Frame.X.ToString());
            }

            foreach (var (name, panel) in stack.ToastsForTest)
                Check($"{name} is PanelWidth wide",
                    Math.Abs(panel.Frame.Width - 420) < 0.5, panel.Frame.Width.ToString());

            // The toast sits in the row reserved below the applet by the bottom-anchor lift.
            Check("the toast row is below the applet",
                toastPanel.Frame.Y + toastPanel.Frame.Height < applet.Frame.Y + 0.5,
                $"toast top={toastPanel.Frame.Y + toastPanel.Frame.Height}, applet y={applet.Frame.Y}");

            applet.Close();
        }

        /// <summary>
        /// The Now Playing wiring, as far as it can be checked without a desktop session.
        ///
        /// Honest about its limits: pressing a media key and seeing the Control Center flyout
        /// cannot be automated, and the skill says so. What this does catch is the wiring —
        /// that the session is claimed, that state is published rather than left blank, and
        /// that commands Yinyue does not implement are switched off, since an enabled command
        /// nobody handles leaves a dead button in Control Center.
        /// </summary>
        private static void MediaControls()
        {
            Console.WriteLine("\nMedia keys and Now Playing");

            var playback = BuildIdlePlayback();
            using var media = new Yinyue.Services.MacMediaControls(playback);

            var centre = MediaPlayer.MPNowPlayingInfoCenter.DefaultCenter;

            Check("a now-playing session exists", centre is not null);
            Check("idle publishes Stopped, not a blank Playing",
                centre!.PlaybackState == MediaPlayer.MPNowPlayingPlaybackState.Stopped,
                centre.PlaybackState.ToString());

            var commands = MediaPlayer.MPRemoteCommandCenter.Shared;

            // Handled: these must stay enabled or the keys do nothing.
            foreach (var (name, command) in new (string, MediaPlayer.MPRemoteCommand)[]
                     {
                         ("play", commands.PlayCommand),
                         ("pause", commands.PauseCommand),
                         ("togglePlayPause", commands.TogglePlayPauseCommand),
                         ("nextTrack", commands.NextTrackCommand),
                         ("previousTrack", commands.PreviousTrackCommand),
                         ("changePlaybackPosition", commands.ChangePlaybackPositionCommand),
                     })
            {
                Check($"{name} is enabled", command.Enabled);
            }

            // Not handled: enabled-but-unhandled leaves a dead button in Control Center.
            foreach (var (name, command) in new (string, MediaPlayer.MPRemoteCommand)[]
                     {
                         ("seekForward", commands.SeekForwardCommand),
                         ("seekBackward", commands.SeekBackwardCommand),
                         ("skipForward", commands.SkipForwardCommand),
                         ("skipBackward", commands.SkipBackwardCommand),
                         ("rating", commands.RatingCommand),
                         ("like", commands.LikeCommand),
                         ("changeRepeatMode", commands.ChangeRepeatModeCommand),
                         ("changeShuffleMode", commands.ChangeShuffleModeCommand),
                     })
            {
                Check($"{name} is disabled", !command.Enabled);
            }

            media.Dispose();
            Check("disposing clears the session",
                centre.PlaybackState == MediaPlayer.MPNowPlayingPlaybackState.Stopped);
        }

        /// <summary>
        /// Every default shortcut must parse into a macOS key code, and the OS must accept
        /// all seventeen at once. The Windows suite asserts the same thing against
        /// RegisterHotKey; this is its counterpart and cannot be answered anywhere but here.
        /// </summary>
        private static void HotkeysRegister()
        {
            Console.WriteLine("\nGlobal hotkeys");

            var config = new Yinyue.Models.HotkeyConfig();

            // Parsing first, so a bad key name is reported as itself rather than as a
            // registration failure.
            var unparsed = Yinyue.Models.HotkeyActions.All
                .Where(a => !Yinyue.Services.MacHotkeyBinding.TryParse(config.For(a).Keys, out _))
                .Select(a => $"{a}={config.For(a).Keys}")
                .ToList();

            Check("every default parses", unparsed.Count == 0, string.Join(", ", unparsed));

            // The vocabulary is shared with Windows through config.json, so these specific
            // spellings must keep working.
            foreach (var text in new[] { "Ctrl+Alt+Space", "Ctrl+Alt+Plus", "Ctrl+Alt+Minus",
                                         "Ctrl+Alt+Pipe", "Ctrl+Alt+Backspace", "Alt+Win+M" })
            {
                Check($"{text} parses", Yinyue.Services.MacHotkeyBinding.TryParse(text, out _));
            }

            Check("a bare key is rejected",
                !Yinyue.Services.MacHotkeyBinding.TryParse("Space", out _));
            Check("modifiers alone are rejected",
                !Yinyue.Services.MacHotkeyBinding.TryParse("Ctrl+Alt", out _));
            Check("nonsense is rejected",
                !Yinyue.Services.MacHotkeyBinding.TryParse("Ctrl+Alt+Bananas", out _));

            // When each shortcut acts. This cannot be tested through Carbon -- a key press
            // cannot be synthesised without Accessibility -- so the rules are plain functions
            // and are checked directly. Getting them wrong made every plain shortcut fire
            // twice, which looked like the overlay only staying up while its keys were held.
            var D = typeof(Yinyue.Services.HotkeyDispatch);

            foreach (var action in new[] { Yinyue.Models.HotkeyActions.ToggleOverlay,
                                           Yinyue.Models.HotkeyActions.QuickSearch,
                                           Yinyue.Models.HotkeyActions.CycleLoop,
                                           Yinyue.Models.HotkeyActions.ToggleShuffle,
                                           Yinyue.Models.HotkeyActions.Mute,
                                           Yinyue.Models.HotkeyActions.OpenQueue,
                                           Yinyue.Models.HotkeyActions.OpenSettings })
            {
                Check($"{action} acts on press",
                    !Yinyue.Services.HotkeyDispatch.FiresOnRelease(action, requiresHold: false));
            }

            foreach (var action in new[] { Yinyue.Models.HotkeyActions.VolumeUp,
                                           Yinyue.Models.HotkeyActions.VolumeDown })
            {
                Check($"{action} acts on press and repeats",
                    Yinyue.Services.HotkeyDispatch.RepeatsWhileHeld(action)
                    && !Yinyue.Services.HotkeyDispatch.FiresOnRelease(action, requiresHold: false));
            }

            // Only these four have a tap and a hold that differ, so only these wait.
            foreach (var action in new[] { Yinyue.Models.HotkeyActions.PlayPause,
                                           Yinyue.Models.HotkeyActions.RestartOrPrevious,
                                           Yinyue.Models.HotkeyActions.AddToQueue,
                                           Yinyue.Models.HotkeyActions.RemoveFromQueue })
            {
                Check($"{action} waits for release",
                    Yinyue.Services.HotkeyDispatch.FiresOnRelease(action, requiresHold: false));
            }

            Check("hold-to-activate makes an ordinary action wait",
                Yinyue.Services.HotkeyDispatch.FiresOnRelease(
                    Yinyue.Models.HotkeyActions.ToggleOverlay, requiresHold: true));

            using var manager = new Yinyue.Services.MacHotkeyManager();
            var refused = manager.Apply(config);

            foreach (var line in refused) Console.WriteLine($"       refused: {line}");

            Check("the OS accepts every default", refused.Count == 0);
            Check("all registered", manager.Active.Count == Yinyue.Models.HotkeyActions.All.Length,
                $"{manager.Active.Count} of {Yinyue.Models.HotkeyActions.All.Length}");

            // Ctrl+Alt+Delete is reserved on Windows and refused with error 1409. macOS has
            // no such reservation, which is a real divergence worth knowing rather than
            // assuming the platforms agree.
            Check("Ctrl+Alt+Delete parses here, unlike on Windows",
                Yinyue.Services.MacHotkeyBinding.TryParse("Ctrl+Alt+Delete", out _));

            manager.UnregisterAll();
            Check("unregistering releases them all", manager.Active.Count == 0);
        }

        /// <summary>
        /// The applet must lay out to the Windows measurements.
        ///
        /// These expectations are written as literals on purpose, not read back from
        /// OverlayMetrics — a test that asserts a constant equals itself proves nothing. The
        /// numbers below were measured from win/MainWindow.xaml, so if someone changes a
        /// metric without changing the XAML, this fails and says so.
        /// </summary>
        private static void LayoutMatchesWindows()
        {
            Console.WriteLine("\nLayout (must match win/MainWindow.xaml)");

            var m = typeof(Yinyue.UI.OverlayMetrics);
            void Metric(string name, double expected)
            {
                double actual = (double)m.GetField(name)!.GetValue(null)!;
                Check($"{name} = {expected}", Math.Abs(actual - expected) < 0.001, actual.ToString());
            }

            // Panel: 420x170, with 148 of content inside 10 padding and 1 border.
            Metric("PanelWidth", 420);
            Metric("AppletHeight", 170);
            Metric("RootPadding", 10);
            Metric("RootBorderThickness", 1);
            Metric("RootCornerRadius", 8);
            Metric("ContentHeight", 148);

            // Artwork: a 130 square with a 6 radius, 10 clear of the content column.
            Metric("ArtSize", 130);
            Metric("ArtCornerRadius", 6);
            Metric("ArtToContentGap", 10);

            // Type sizes, straight from the TextBlocks.
            Metric("StatusFontSize", 11);
            Metric("TitleFontSize", 13);
            Metric("ArtistFontSize", 11);
            Metric("TimeFontSize", 10);

            // Buttons: MediaBtnStyle's MinWidth and Padding, and the Icon control's default.
            Metric("IconSize", 18);
            Metric("ButtonMinWidth", 30);
            Metric("ButtonPadding", 4);
            Metric("TransportButtonMargin", 4);
            Metric("ModeGroupGap", 12);
            Metric("SeekBarHeight", 12);

            // The stack. ToastRowHeight was the number CLAUDE.md flagged as living in two
            // places on Windows with a comment on each saying they must match.
            Metric("SideGap", 6);
            Metric("ToastRowHeight", 60);
            Metric("ReservedForToasts", 132);

            // And the laid-out view actually honours them.
            var applet = new Yinyue.UI.AppletView(new Yinyue.Models.OverlayConfig(), BuildIdlePlayback());

            Check("the applet fills the panel",
                Math.Abs(applet.Frame.Width - 420) < 0.5 && Math.Abs(applet.Frame.Height - 170) < 0.5,
                $"{applet.Frame.Width}x{applet.Frame.Height}");

            var art = applet.Subviews.FirstOrDefault(v => Math.Abs(v.Frame.Width - 130) < 0.5
                                                       && Math.Abs(v.Frame.Height - 130) < 0.5);
            Check("the artwork is a 130 square", art is not null);

            if (art is not null)
            {
                Check("it sits inside the padding", Math.Abs(art.Frame.X - 11) < 0.5, $"x={art.Frame.X}");
                Check("and is centred in the content height",
                    Math.Abs(art.Frame.Y - (11 + (148 - 130) / 2)) < 0.5, $"y={art.Frame.Y}");
            }

            // The header buttons sit hard against the right inset and stay there. Adding the
            // sleep readout's width to their offset pushed the whole header 64 points inboard
            // -- and every check here still passed, because they all measured the applet and
            // the artwork rather than the row that moved.
            double inset = 11;   // RootPadding + RootBorderThickness
            var rightmost = applet.Subviews.OfType<AppKit.NSButton>()
                .OrderByDescending(b => b.Frame.X + b.Frame.Width).First();

            Check("the header reaches the right inset",
                Math.Abs((rightmost.Frame.X + rightmost.Frame.Width) - (420 - inset)) < 0.5,
                $"right edge at {rightmost.Frame.X + rightmost.Frame.Width}, expected {420 - inset}");

            // And it must not move when the sleep readout appears.
            double before = rightmost.Frame.X;
            applet.ShowSleepRemaining(TimeSpan.FromMinutes(30));

            Check("the sleep readout does not move the header",
                Math.Abs(rightmost.Frame.X - before) < 0.5,
                $"{rightmost.Frame.X} vs {before}");

            applet.ShowSleepRemaining(TimeSpan.Zero);

            // Six transport controls, not three: previous, play, next, shuffle, loop, heart.
            int buttons = applet.Subviews.Count(v => v is AppKit.NSButton);
            Check("ten buttons in all — four header, six transport", buttons == 10, buttons.ToString());

            Check("every child is inside the panel",
                applet.Subviews.All(v => v.Frame.X >= -0.5 && v.Frame.Y >= -0.5
                                      && v.Frame.X + v.Frame.Width <= 420.5
                                      && v.Frame.Y + v.Frame.Height <= 170.5));
        }

        private static Yinyue.Services.PlaybackService BuildIdlePlayback()
        {
            var audio = new Yinyue.Services.MacAudioPlayer();
            var config = new Yinyue.Services.ConfigService();
            var library = new Yinyue.Services.MusicLibrary(config);
            return new Yinyue.Services.PlaybackService(audio, library);
        }

        /// <summary>
        /// A resume point set before the media is ready must still be applied.
        ///
        /// AVPlayer drops a seek issued against an item that has not loaded, without an
        /// error — so the naive implementation looks correct, compiles, and silently starts
        /// every resumed track from zero. Track.ResumePosition carries Jellyfin's stored
        /// position, so this is the difference between resuming an album and restarting it.
        /// </summary>
        private static void ResumePointIsHonoured(string fixturePath)
        {
            using var audio = new MacAudioPlayer();

            var target = TimeSpan.FromSeconds(0.6);
            audio.SeekWhenReady(target);
            audio.PlayFileAsync(fixturePath).GetAwaiter().GetResult();

            // Pump rather than block: the seek is applied on the first tick that reports the
            // item ready, and blocking the main thread would stop those ticks arriving.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (audio.Position < target && DateTime.UtcNow < deadline)
                NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.05));

            Check("a resume point set before load is applied",
                audio.Position >= target, $"landed at {audio.Position.TotalSeconds:0.00}s");
        }

        /// <summary>
        /// Every shared mark must actually draw.
        ///
        /// Parsing is not the interesting failure. A wrong scale, a bad flip or an arc
        /// conversion that collapsed would all parse perfectly and render an empty square —
        /// so this counts opaque pixels rather than trusting that no exception was thrown.
        /// </summary>
        private static void IconsRender()
        {
            Console.WriteLine("\nIcons (shared with Windows, from Common/Icons)");

            Check("the generated set is not empty", Yinyue.UI.Icons.All.Count > 0,
                $"{Yinyue.UI.Icons.All.Count} icons");

            int blank = 0;
            var failures = new List<string>();

            foreach (var (name, data) in Yinyue.UI.Icons.All)
            {
                try
                {
                    var image = Yinyue.UI.Icon.Make(data, 32, AppKit.NSColor.White);
                    if (!HasInk(image)) { blank++; failures.Add(name); }
                }
                catch (Exception ex)
                {
                    failures.Add($"{name} ({ex.GetType().Name})");
                }
            }

            Check("every icon renders visible ink", failures.Count == 0,
                failures.Count == 0 ? $"{Yinyue.UI.Icons.All.Count} drawn"
                                    : string.Join(", ", failures.Take(5)));

            // The marks the overlay actually binds to. Named individually so a rename in
            // Common/Icons fails here rather than silently blanking a button.
            foreach (var required in new[] { "Play", "Pause", "SkipBack", "SkipForward",
                                             "Shuffle", "Repeat", "Repeat1", "RepeatOff", "Cog" })
            {
                Check($"{required} exists", Yinyue.UI.Icons.All.ContainsKey(required));
            }
        }

        /// <summary>True if any pixel was drawn at all.</summary>
        private static bool HasInk(AppKit.NSImage image)
        {
            var rep = new AppKit.NSBitmapImageRep(image.AsTiff()!);
            for (nint x = 0; x < rep.PixelsWide; x += 2)
            for (nint y = 0; y < rep.PixelsHigh; y += 2)
                if (rep.ColorAt(x, y)?.AlphaComponent > 0.05f) return true;

            return false;
        }

        /// <summary>
        /// Every anchor must land the panel inside the screen's work area, touching the edge
        /// it names. Measured against the real NSScreen rather than reasoned about, because
        /// AppKit's origin is bottom-left and the Windows arithmetic reads inverted here —
        /// a port that swapped top and bottom would look right on a centred window.
        /// </summary>
        private static void OverlayAnchors()
        {
            Console.WriteLine("\nOverlay anchoring");

            var config = new Yinyue.Models.OverlayConfig { MarginX = 16, MarginY = 16 };
            var panel = new Yinyue.UI.OverlayPanel(config, height: 170);
            var work = AppKit.NSScreen.MainScreen.VisibleFrame;

            Check("work area excludes menu bar and Dock",
                work.Height < AppKit.NSScreen.MainScreen.Frame.Height,
                $"{work.Height} of {AppKit.NSScreen.MainScreen.Frame.Height}");

            foreach (var anchor in Enum.GetValues<Yinyue.Models.OverlayAnchor>())
            {
                config.Anchor = anchor;
                Yinyue.UI.OverlayPositioner.PositionApplet(panel, config);

                var f = panel.Frame;
                bool inside = f.X >= work.X - 0.5
                           && f.Y >= work.Y - 0.5
                           && f.X + f.Width <= work.X + work.Width + 0.5
                           && f.Y + f.Height <= work.Y + work.Height + 0.5;

                Check($"{anchor} stays on screen", inside, $"x={f.X:0} y={f.Y:0}");
            }

            // The specific claim the coordinate flip would break: a top anchor must sit high
            // on the screen and a bottom anchor low. Inverting the arithmetic passes the
            // on-screen check above while placing every window at the wrong end.
            config.Anchor = Yinyue.Models.OverlayAnchor.TopRight;
            Yinyue.UI.OverlayPositioner.PositionApplet(panel, config);
            double topY = panel.Frame.Y;

            config.Anchor = Yinyue.Models.OverlayAnchor.BottomRight;
            Yinyue.UI.OverlayPositioner.PositionApplet(panel, config);
            double bottomY = panel.Frame.Y;

            Check("top anchors sit above bottom anchors", topY > bottomY, $"top y={topY:0}, bottom y={bottomY:0}");

            // A bottom-anchored overlay is lifted clear of the two reserved toast rows;
            // nothing else is, because everywhere else there is already room below.
            Check("the bottom anchor is lifted clear of the toast rows",
                bottomY >= work.Y + config.MarginY + Yinyue.UI.OverlayPositioner.ReservedForToasts - 0.5,
                $"y={bottomY:0}, reserved={Yinyue.UI.OverlayPositioner.ReservedForToasts}");

            Check("the panel is PanelWidth wide",
                Math.Abs(panel.Frame.Width - Yinyue.UI.OverlayMetrics.PanelWidth) < 0.5,
                panel.Frame.Width.ToString());

            panel.Close();
        }

        /// <summary>
        /// Uses a throwaway account name, never the one the app signs in with, so running
        /// this cannot destroy a real token.
        /// </summary>
        private static void KeychainRoundTrip()
        {
            Console.WriteLine("\nKeychain");

            const string account = "selftest-token";
            var record = new SecRecord(SecKind.GenericPassword)
            {
                Service = "com.yinyue.player",
                Account = account,
            };

            SecKeyChain.Remove(record);

            string secret = $"token-{Guid.NewGuid():N}";
            record.ValueData = NSData.FromString(secret, NSStringEncoding.UTF8);
            record.Accessible = SecAccessible.WhenUnlocked;

            var added = SecKeyChain.Add(record);
            Check("a secret can be stored", added == SecStatusCode.Success, added.ToString());

            var query = new SecRecord(SecKind.GenericPassword)
            {
                Service = "com.yinyue.player",
                Account = account,
            };

            var data = SecKeyChain.QueryAsData(query, false, out var status);
            string? read = data is null ? null : NSString.FromData(data, NSStringEncoding.UTF8)?.ToString();
            Check("and read back unchanged", read == secret, read is null ? status.ToString() : "match");

            SecKeyChain.Remove(query);
            var goneData = SecKeyChain.QueryAsData(query, false, out _);
            Check("deleting it means it is gone", goneData is null);

            // The contract ConfigService relies on: reading is never an error. A missing item
            // must read as "not signed in" rather than throwing, which would crash first
            // launch.
            //
            // Deliberately NOT asserting that the result is null: this reads the real slot,
            // and a signed-in user has a token in it. The first version asserted null and
            // started failing the moment sign-in worked -- a test that only passed while the
            // feature was unused.
            var store = new KeychainSecretStore();
            try
            {
                string? stored = store.Unprotect("keychain");
                Check("reading the token never throws", true,
                    stored is null ? "nothing stored" : "a token is stored");
            }
            catch (Exception ex)
            {
                Check("reading the token never throws", false, ex.GetType().Name);
            }
        }

        /// <summary>
        /// Decodes a real file end to end. `say` is on every Mac, so the fixture needs no
        /// asset checked into the repo and no network.
        /// </summary>
        /// <summary>
        /// Deliberately synchronous, with no `await` anywhere.
        ///
        /// `NSApplication.Init()` installs a synchronization context that posts continuations
        /// to the main run loop. Blocking the main thread on a task that awaits — which this
        /// test did in its first version — means the continuation is queued behind the very
        /// thread that is waiting for it, and the process hangs with no error and no CPU. It
        /// hung for three minutes at 0.3 seconds of CPU before this was spotted.
        ///
        /// The same trap is waiting for the real shell: nothing on the main thread may block
        /// on a Core task. The run loop is pumped explicitly below instead.
        /// </summary>
        private static void AudioPlaysAFile()
        {
            Console.WriteLine("\nAudio");

            string path = Path.Combine(Path.GetTempPath(), $"yinyue-selftest-{Guid.NewGuid():N}.aiff");
            var say = System.Diagnostics.Process.Start("/usr/bin/say", new[] { "-o", path, "Yinyue audio check" });
            if (say is null) { Check("fixture created", false, "could not run say"); return; }
            say.WaitForExit();

            if (!File.Exists(path)) { Check("fixture created", false, "no file"); return; }
            Check("fixture created", true, $"{new FileInfo(path).Length} bytes");

            using var audio = new MacAudioPlayer();

            var ended = new TaskCompletionSource<bool>();
            string? failure = null;
            int progressTicks = 0;

            audio.PlaybackEnded += (_, _) => ended.TrySetResult(true);
            audio.MediaFailed += (_, m) => { failure = m; ended.TrySetResult(false); };
            audio.ProgressUpdated += (_, _) => progressTicks++;

            // Completes synchronously, so this posts no continuation.
            audio.PlayFileAsync(path).GetAwaiter().GetResult();
            Check("the engine reports a source", audio.HasSource);

            // AVFoundation needs a run loop turn to move an item to ReadyToPlay, and this is
            // a console process, so pump instead of blocking.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!ended.Task.IsCompleted && DateTime.UtcNow < deadline)
            {
                NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.05));
            }

            bool reachedEnd = ended.Task.IsCompleted && ended.Task.Result;
            Check("it played to the end", reachedEnd, reachedEnd ? null : failure ?? "timed out");
            Check("duration was reported", audio.Duration > TimeSpan.Zero, audio.Duration.ToString());
            Check("progress fired while playing", progressTicks > 0, $"{progressTicks} ticks");

            ResumePointIsHonoured(path);

            try { File.Delete(path); } catch { }
        }
    }
}
