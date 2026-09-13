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
                Math.Abs(panel.Frame.Width - Yinyue.UI.Theme.PanelWidth) < 0.5,
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

            // The contract ConfigService relies on: a missing item reads as "not signed in",
            // not as an error. A throw here would surface as a crash on first launch.
            var store = new KeychainSecretStore();
            Check("a missing token reads as null, not a throw", store.Unprotect("keychain") is null);
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

            try { File.Delete(path); } catch { }
        }
    }
}
