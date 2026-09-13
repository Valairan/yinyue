using AppKit;
using Foundation;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Pauses playback after a long, absolute interval.
    ///
    /// <b>This is not auto-hide.</b> They are the only two timers in the app and are easily
    /// confused: the sleep timer stops the music and nothing resets it; auto-hide hides the
    /// overlay after a few seconds of inactivity and any input restarts it.
    ///
    /// Mirrors the rules of the Windows `SleepTimerService` exactly. It is a separate class
    /// rather than the shared one because that service ticks on a `DispatcherTimer`, which is
    /// WPF — the arithmetic that matters (<see cref="Describe"/>, the cycle, the
    /// sanitisation) is the part worth eventually sharing, and CLAUDE.md records that.
    /// </summary>
    public sealed class MacSleepTimer : IDisposable
    {
        private NSTimer? _ticker;
        private DateTime _endsAtUtc;
        private bool _enabled = true;
        private int[] _steps = BuildCycle(SleepTimerConfig.DefaultSteps);

        /// <summary>Raised when the timer runs out. The caller decides what stopping means.</summary>
        public event Action? Elapsed;

        public event Action<TimeSpan>? Changed;

        public int Minutes { get; private set; }

        public bool IsRunning => Minutes > 0;

        public TimeSpan Remaining =>
            IsRunning ? Max(_endsAtUtc - DateTime.UtcNow, TimeSpan.Zero) : TimeSpan.Zero;

        /// <summary>
        /// A separate switch from "currently set to off". The shortcut cycles, so a mistaken
        /// press would otherwise arm something that stops the music an hour later. Turning it
        /// off cancels anything running — a countdown alive under a disabled feature would
        /// stop playback with no visible cause.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (!value) Set(0);
            }
        }

        public IReadOnlyList<int> Steps => _steps;

        /// <summary>
        /// "Off" is never stored in the list — it is prepended here, so it is always the entry
        /// point and always reachable. Otherwise the shortcut could arm the timer but never
        /// disarm it.
        /// </summary>
        private static int[] BuildCycle(IEnumerable<int> steps) => new[] { 0 }.Concat(steps).ToArray();

        public void SetSteps(IEnumerable<int> steps)
        {
            _steps = BuildCycle(steps);

            // A running timer whose duration is no longer in the list is stopped rather than
            // stranded outside the cycle.
            if (IsRunning && !_steps.Contains(Minutes)) Set(0);
        }

        public int Cycle()
        {
            if (!_enabled) return 0;

            int index = Array.IndexOf(_steps, Minutes);
            int next = _steps[(index < 0 ? 0 : index + 1) % _steps.Length];

            Set(next);
            return Minutes;
        }

        public void Set(int minutes)
        {
            Minutes = _enabled && _steps.Contains(minutes) ? minutes : 0;

            _ticker?.Invalidate();
            _ticker = null;

            if (Minutes > 0)
            {
                _endsAtUtc = DateTime.UtcNow.AddMinutes(Minutes);
                _ticker = NSTimer.CreateRepeatingScheduledTimer(1.0, _ => OnTick());
            }

            Changed?.Invoke(Remaining);
        }

        public void Cancel() => Set(0);

        /// <summary>
        /// Coarse near the start, precise near the end: the exact second matters when the
        /// music is about to stop and not at all when it is hours away. Rounded up, so it
        /// never reads "0m" with music still to come.
        /// </summary>
        public static string Describe(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero) return string.Empty;

            if (remaining < TimeSpan.FromMinutes(1))
                return $"{Math.Ceiling(remaining.TotalSeconds):F0}s";

            int minutes = (int)Math.Ceiling(remaining.TotalMinutes);

            if (minutes < 60) return $"{minutes}m";

            return minutes % 60 == 0
                ? $"{minutes / 60}h"
                : $"{minutes / 60}h {minutes % 60:00}m";
        }

        private void OnTick()
        {
            if (!IsRunning) return;

            if (Remaining > TimeSpan.Zero)
            {
                Changed?.Invoke(Remaining);
                return;
            }

            // Cleared first: whatever handles Elapsed may look at our state.
            Set(0);
            Elapsed?.Invoke();
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        public void Dispose()
        {
            _ticker?.Invalidate();
            _ticker = null;
        }
    }
}
