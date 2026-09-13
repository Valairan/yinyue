using AppKit;
using Foundation;

namespace Yinyue.Services
{
    /// <summary>
    /// One copy at a time, and a second launch summons the first — the counterpart to the
    /// named mutex and summon event in App.xaml.cs.
    ///
    /// This is not housekeeping. The app is a resident menu-bar process with a global hotkey
    /// and an anchored overlay, so a second copy means two menu-bar marks, two overlays
    /// fighting over the same anchor, and — worse — a second attempt to register the same
    /// seventeen shortcuts, every one of which fails because the first copy already owns
    /// them. The symptom is "my hotkeys stopped working", which points nowhere near the cause.
    ///
    /// Two halves, matching the Windows design:
    ///
    /// · An exclusive lock file decides who is first. A lock rather than a process scan
    ///   because the app runs both as a bundle and straight from the binary during
    ///   development, and a scan by bundle identifier misses the latter.
    /// · A distributed notification hands off to the running copy, standing in for the named
    ///   EventWaitHandle. Re-running the app should summon it, not scold the user.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        private const string SummonNotification = "com.yinyue.player.summon";

        private FileStream? _lock;
        private NSObject? _observer;

        /// <summary>
        /// True if this process took the lock. False means another copy is running and has
        /// been asked to show itself; the caller should exit quietly.
        /// </summary>
        public bool IsFirst { get; private init; }

        public static SingleInstance Acquire()
        {
            string path = Path.Combine(AppPaths.DataFolder, "instance.lock");

            try
            {
                // FileShare.None takes an exclusive advisory lock on macOS. Left open for the
                // life of the process; the OS releases it if we are killed, which a lock file
                // holding a PID would not survive.
                var handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None);

                return new SingleInstance { IsFirst = true, _lock = handle };
            }
            catch (IOException)
            {
                // Someone else holds it. Ask them to come to the front, then leave.
                NSDistributedNotificationCenter.DefaultCenter.PostNotificationName(
                    SummonNotification, null);

                return new SingleInstance { IsFirst = false };
            }
        }

        /// <summary>
        /// Listens for a later launch asking us to show ourselves. Distributed notifications
        /// arrive on the main thread, but the handler is hopped anyway because it touches a
        /// window and the guarantee is not worth relying on.
        /// </summary>
        public void ListenForSummon(Action onSummon)
        {
            if (!IsFirst) return;

            _observer = NSDistributedNotificationCenter.DefaultCenter.AddObserver(
                (NSString)SummonNotification,
                _ => NSApplication.SharedApplication.BeginInvokeOnMainThread(() => onSummon()));
        }

        public void Dispose()
        {
            if (_observer is not null)
            {
                NSDistributedNotificationCenter.DefaultCenter.RemoveObserver(_observer);
                _observer.Dispose();
                _observer = null;
            }

            _lock?.Dispose();
            _lock = null;
        }
    }
}
