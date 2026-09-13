using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>One registrable action: its id, which action it is, and how it is bound.</summary>
    public record HotkeyRegistration(int Id, string Action, HotkeyBinding Binding, bool RequiresHold);

    /// <summary>
    /// Global hotkeys via RegisterHotKey and a WM_HOTKEY message hook. Deliberately not a
    /// low-level keyboard hook: this approach costs nothing while idle, which matters for
    /// an app that runs all day.
    ///
    /// Registration is per-HWND and per-thread — call from the UI thread with the overlay
    /// handle, which must outlive the process (see MainWindow, which hides but never closes).
    /// </summary>
    public class HotkeyManager
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>How often a pending hold rechecks whether the key is still down.</summary>
        private static readonly TimeSpan HoldPollInterval = TimeSpan.FromMilliseconds(40);

        private const int FirstHotkeyId = 9001;

        private IntPtr _hWnd;
        private HwndSource? _source;
        private readonly List<int> _registered = new();
        private readonly Dictionary<int, HotkeyRegistration> _byId = new();

        private DispatcherTimer? _holdTimer;
        private HotkeyRegistration? _holding;
        private DateTime _holdStart;
        private TimeSpan _holdDelay = TimeSpan.FromSeconds(HotkeyConfig.DefaultHoldDelaySeconds);

        /// <summary>Fires when an action is invoked, named by <see cref="HotkeyActions"/>.</summary>
        public event Action<string>? Triggered;

        /// <summary>Progress of a pending hold: action name and a 0-1 fraction elapsed.</summary>
        public event Action<string, double>? HoldProgress;

        /// <summary>
        /// A pending hold is over, whether it was abandoned early or ran its course. Raised
        /// on success as well as on cancellation: it used to fire only for a cancelled hold,
        /// so a hold that succeeded never told the toast to take its dial down, and the dial
        /// stayed on screen for good.
        /// </summary>
        public event Action<string>? HoldEnded;

        public IReadOnlyDictionary<int, HotkeyRegistration> Active { get; private set; } =
            new Dictionary<int, HotkeyRegistration>();

        /// <summary>The live binding for an action, or null if it failed to register.</summary>
        public HotkeyBinding? BindingFor(string action) =>
            Active.Values.FirstOrDefault(r => r.Action == action)?.Binding;

        /// <summary>
        /// Binds every hotkey described by <paramref name="config"/>, replacing any previous
        /// registration. Safe to call repeatedly on the same HWND, which is what makes
        /// runtime rebinding from the settings window possible.
        ///
        /// Returns descriptions of bindings the OS refused — almost always because another
        /// application already owns the combination.
        /// </summary>
        public IReadOnlyList<string> Apply(IntPtr windowHandle, HotkeyConfig config)
        {
            if (_hWnd != windowHandle)
            {
                UnregisterAll();
                _hWnd = windowHandle;
                _source = HwndSource.FromHwnd(_hWnd);
                _source?.AddHook(HwndHook);
            }
            else
            {
                UnregisterKeysOnly();
            }

            CancelHold();
            _holdDelay = TimeSpan.FromSeconds(config.HoldDelaySeconds);

            var wanted = BuildRegistrations(config);
            var failures = new List<string>();
            var active = new Dictionary<int, HotkeyRegistration>();

            _byId.Clear();

            // Two actions on one combination would make the second silently lose the race
            // inside Windows; catch it here, where both can be named.
            var duplicates = wanted
                .GroupBy(r => r.Binding)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in duplicates)
            {
                failures.Add($"{group.Key} is assigned to more than one action " +
                             $"({string.Join(", ", group.Select(r => HotkeyActions.Describe(r.Action)))})");
            }

            var duplicateBindings = duplicates.Select(g => g.Key).ToHashSet();

            foreach (var registration in wanted)
            {
                if (duplicateBindings.Contains(registration.Binding)) continue;

                if (RegisterHotKey(_hWnd, registration.Id,
                        registration.Binding.Win32Modifiers, registration.Binding.VirtualKey))
                {
                    _registered.Add(registration.Id);
                    _byId[registration.Id] = registration;
                    active[registration.Id] = registration;
                }
                else
                {
                    failures.Add($"{registration.Binding} ({HotkeyActions.Describe(registration.Action)})");
                }
            }

            Active = active;
            return failures;
        }

        private static List<HotkeyRegistration> BuildRegistrations(HotkeyConfig config)
        {
            var list = new List<HotkeyRegistration>();
            int id = FirstHotkeyId;

            foreach (string action in HotkeyActions.All)
            {
                var entry = config.For(action);
                var binding = HotkeyBinding.ParseOrDefault(
                    entry.Keys, HotkeyConfig.DefaultKeysFor(action));

                // Honour the toggle only where a hold gesture is not already spoken for.
                bool hold = entry.Hold && HotkeyActions.SupportsHoldToggle(action);

                list.Add(new HotkeyRegistration(id++, action, binding, hold));
            }

            return list;
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg != WM_HOTKEY) return IntPtr.Zero;

            if (!_byId.TryGetValue(wParam.ToInt32(), out var registration)) return IntPtr.Zero;

            handled = true;

            if (registration.RequiresHold) BeginHold(registration);
            else Triggered?.Invoke(registration.Action);

            return IntPtr.Zero;
        }

        #region Hold to activate

        /// <summary>
        /// Waits for the combination to be held before firing.
        ///
        /// Polling is the only option here: every binding registers with MOD_NOREPEAT, so
        /// Windows sends exactly one WM_HOTKEY on press and nothing at all on release. There
        /// is no second message to time against.
        /// </summary>
        private void BeginHold(HotkeyRegistration registration)
        {
            if (_holding != null) return;   // a hold is already pending

            _holding = registration;
            _holdStart = DateTime.UtcNow;

            _holdTimer = new DispatcherTimer { Interval = HoldPollInterval };
            _holdTimer.Tick += HoldTick;
            _holdTimer.Start();
        }

        private void HoldTick(object? sender, EventArgs e)
        {
            var pending = _holding;
            if (pending == null)
            {
                CancelHold();
                return;
            }

            var elapsed = DateTime.UtcNow - _holdStart;

            // Only the main key is checked. Releasing Ctrl or Alt while keeping the key down
            // still counts as holding, which is the forgiving reading of the gesture.
            if (!IsKeyDown((int)pending.Binding.VirtualKey))
            {
                CancelHold();
                HoldEnded?.Invoke(pending.Action);
                return;
            }

            if (elapsed >= _holdDelay)
            {
                CancelHold();
                HoldEnded?.Invoke(pending.Action);
                Triggered?.Invoke(pending.Action);
                return;
            }

            double fraction = _holdDelay > TimeSpan.Zero
                ? Math.Clamp(elapsed.TotalMilliseconds / _holdDelay.TotalMilliseconds, 0, 1)
                : 1;

            HoldProgress?.Invoke(pending.Action, fraction);
        }

        private void CancelHold()
        {
            _holding = null;

            if (_holdTimer == null) return;

            _holdTimer.Stop();
            _holdTimer.Tick -= HoldTick;
            _holdTimer = null;
        }

        public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        #endregion

        private void UnregisterKeysOnly()
        {
            foreach (int id in _registered)
                UnregisterHotKey(_hWnd, id);

            _registered.Clear();
        }

        public void UnregisterAll()
        {
            CancelHold();

            if (_hWnd == IntPtr.Zero) return;

            UnregisterKeysOnly();
            _source?.RemoveHook(HwndHook);
            _source = null;

            _byId.Clear();
            Active = new Dictionary<int, HotkeyRegistration>();
        }
    }
}
