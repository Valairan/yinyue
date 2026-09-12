using System;
using System.Collections.Generic;
using System.Linq;
using Yinyue.Models;
using System.Windows.Threading;

namespace Yinyue.Services
{
    /// <summary>
    /// Stops playback after a set time. Cycled rather than typed in, so it can be driven
    /// entirely from a hotkey: off, then each configured step, then back to off.
    /// </summary>
    public class SleepTimerService : IDisposable
    {
        private readonly DispatcherTimer _ticker;
        private DateTime _endsAtUtc;

        private int[] _steps = BuildCycle(SleepTimerConfig.DefaultSteps);
        private bool _enabled = true;

        /// <summary>
        /// Whether the timer can be armed at all. Turning it off cancels anything running —
        /// leaving a countdown alive under a disabled feature would stop the music with no
        /// visible cause.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (!_enabled) Set(0);
            }
        }

        /// <summary>
        /// The durations the cycle offers, in minutes, without "off". Zero is prepended
        /// internally: it is the entry point and must always be reachable, or the shortcut
        /// could arm the timer but never disarm it.
        /// </summary>
        public void SetSteps(IEnumerable<int> steps)
        {
            _steps = BuildCycle(SleepTimerConfig.Sanitise(steps));

            // A running timer set to a duration that no longer exists has nothing to cycle
            // from, so it is stopped rather than left stranded outside the list.
            if (IsRunning && !_steps.Contains(Minutes)) Set(0);
        }

        /// <summary>The steps on offer, "off" first.</summary>
        public IReadOnlyList<int> Steps => _steps;

        private static int[] BuildCycle(IEnumerable<int> steps) =>
            new[] { 0 }.Concat(steps).ToArray();

        /// <summary>Raised when the timer runs out. The caller decides what stopping means.</summary>
        public event Action? Elapsed;

        /// <summary>Raised whenever the setting or the remaining time changes.</summary>
        public event Action<TimeSpan>? Changed;

        public int Minutes { get; private set; }

        public bool IsRunning => Minutes > 0;

        public TimeSpan Remaining =>
            IsRunning ? Max(_endsAtUtc - DateTime.UtcNow, TimeSpan.Zero) : TimeSpan.Zero;

        public SleepTimerService()
        {
            // One tick a second is plenty for a countdown measured in minutes.
            _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _ticker.Tick += OnTick;
        }

        /// <summary>Advances to the next step and returns the minutes now set.</summary>
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

            if (Minutes == 0)
            {
                _ticker.Stop();
            }
            else
            {
                _endsAtUtc = DateTime.UtcNow.AddMinutes(Minutes);
                _ticker.Start();
            }

            Changed?.Invoke(Remaining);
        }

        public void Cancel() => Set(0);

        /// <summary>
        /// Coarse near the start, precise near the end: the exact second matters when the
        /// music is about to stop and not at all when it is hours away. Rounded up, so it
        /// never reads "0m" while still playing.
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

        private void OnTick(object? sender, EventArgs e)
        {
            if (!IsRunning) return;

            if (Remaining > TimeSpan.Zero)
            {
                Changed?.Invoke(Remaining);
                return;
            }

            // Clear first: whatever handles Elapsed may look at our state.
            Set(0);
            Elapsed?.Invoke();
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        public void Dispose()
        {
            _ticker.Stop();
            _ticker.Tick -= OnTick;
        }
    }
}
