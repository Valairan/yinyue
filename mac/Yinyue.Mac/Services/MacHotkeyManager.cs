using System.Runtime.InteropServices;
using AppKit;
using Foundation;
using Yinyue.Models;

namespace Yinyue.Services
{
    public sealed class HotkeyTriggeredEventArgs : EventArgs
    {
        /// <summary>One of the names in <see cref="HotkeyActions"/>.</summary>
        public required string Action { get; init; }

        /// <summary>True when the key was held past the hold delay rather than tapped.</summary>
        public required bool Held { get; init; }

        /// <summary>True for each repeat of a hold that repeats, rather than the first.</summary>
        public bool Repeat { get; init; }
    }

    /// <summary>
    /// Global hotkeys on macOS, via Carbon's <c>RegisterEventHotKey</c>.
    ///
    /// <b>Event-driven, not polled — and that is not a detail.</b> Windows registers with
    /// MOD_NOREPEAT, which delivers one WM_HOTKEY on press and nothing on release, so its
    /// HotkeyManager has no choice but to poll GetAsyncKeyState to learn when the key came
    /// back up. Carbon delivers a real <c>kEventHotKeyReleased</c>, so a hold is simply the
    /// interval between two events.
    ///
    /// The obvious port — <c>CGEventSource.keyState</c>, the direct GetAsyncKeyState analogue —
    /// <b>does not work</b>: it never reads true for a non-modifier key from a process without
    /// Accessibility. Measured across six presses; see mac/spikes/taphold/FINDINGS.md. The
    /// consequence that matters is the one avoided: no Accessibility prompt, nothing for
    /// onboarding to explain, and no degraded mode to design for a refusal.
    ///
    /// Carbon is deprecated and this specific API is not. There is no modern replacement that
    /// works without Accessibility, which is why every menu-bar app still uses it.
    /// </summary>
    public sealed class MacHotkeyManager : IDisposable
    {
        private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

        private const uint EventClassKeyboard = 0x6B657962;   // 'keyb'
        private const uint EventHotKeyPressed = 5;
        private const uint EventHotKeyReleased = 6;
        private const uint EventParamDirectObject = 0x2D2D2D2D;   // '----'
        private const uint TypeEventHotKeyID = 0x686B6964;        // 'hkid'

        [StructLayout(LayoutKind.Sequential)]
        private struct EventTypeSpec
        {
            public uint EventClass;
            public uint EventKind;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventHotKeyID
        {
            public uint Signature;
            public uint Id;
        }

        private delegate int EventHandlerProc(IntPtr callRef, IntPtr theEvent, IntPtr userData);

        [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();
        [DllImport(Carbon)] private static extern uint GetEventKind(IntPtr theEvent);

        [DllImport(Carbon)]
        private static extern int InstallEventHandler(
            IntPtr target, EventHandlerProc handler, int numTypes,
            EventTypeSpec[] typeList, IntPtr userData, out IntPtr handlerRef);

        [DllImport(Carbon)] private static extern int RemoveEventHandler(IntPtr handlerRef);

        [DllImport(Carbon)]
        private static extern int GetEventParameter(
            IntPtr theEvent, uint name, uint type, IntPtr outActualType,
            int bufferSize, IntPtr outActualSize, out EventHotKeyID outData);

        [DllImport(Carbon)]
        private static extern int RegisterEventHotKey(
            uint keyCode, uint modifiers, EventHotKeyID id,
            IntPtr target, uint options, out IntPtr hotKeyRef);

        [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

        private const uint Signature = 0x59494E59;   // 'YINY'

        private sealed class Registration
        {
            public required string Action { get; init; }
            public required IntPtr Reference { get; init; }
            public required MacHotkeyBinding Binding { get; init; }

            /// <summary>Set in settings: this shortcut fires only after being held.</summary>
            public required bool RequiresHold { get; init; }

            /// <summary>
            /// True when continuing to hold will actually do something — either the shortcut
            /// carries a built-in escalation, or the user asked it to require a hold.
            /// </summary>
            public bool ShowsDial => RequiresHold || !HotkeyActions.SupportsHoldToggle(Action);

            /// <summary>
            /// True when the action has a tap and a hold that mean different things, so the
            /// tap must wait for release to know which was meant.
            /// </summary>
            public bool HasEscalation => !HotkeyActions.SupportsHoldToggle(Action);

            /// <summary>
            /// Volume steps on press and keeps stepping while the key is down, like a
            /// keyboard's own repeat.
            /// </summary>
            public bool RepeatsWhileHeld =>
                Action is HotkeyActions.VolumeUp or HotkeyActions.VolumeDown;

            public DateTime PressedAt { get; set; }
            public NSTimer? HoldTimer { get; set; }
            public bool HoldFired { get; set; }
        }

        private readonly Dictionary<uint, Registration> _byId = new();
        private readonly EventHandlerProc _handler;   // held so the GC cannot collect it
        private IntPtr _handlerRef;
        private uint _nextId = 1;

        private double _holdDelaySeconds = 0.8;

        public event EventHandler<HotkeyTriggeredEventArgs>? Triggered;

        /// <summary>
        /// Reports how far through a hold the key is, so the dial can fill. Raised only for
        /// shortcuts whose hold means something — there is nothing to promise otherwise.
        /// </summary>
        public event EventHandler<(string Action, double Fraction)>? HoldProgress;

        /// <summary>Raised when a hold ends, whether it completed or was let go early.</summary>
        public event EventHandler<string>? HoldEnded;

        public IReadOnlyCollection<string> Active =>
            _byId.Values.Select(r => r.Action).ToList();

        public MacHotkeyManager()
        {
            _handler = OnCarbonEvent;

            var spec = new[]
            {
                new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed },
                new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyReleased },
            };

            InstallEventHandler(GetApplicationEventTarget(), _handler, spec.Length, spec,
                IntPtr.Zero, out _handlerRef);
        }

        /// <summary>
        /// Registers every binding in <paramref name="config"/> and returns the ones the OS
        /// refused, described for the user. A refusal means another application already owns
        /// the combination — which, exactly as on Windows, cannot be known until registration
        /// is attempted.
        /// </summary>
        public IReadOnlyList<string> Apply(HotkeyConfig config)
        {
            UnregisterAll();
            _holdDelaySeconds = config.HoldDelaySeconds;

            var refused = new List<string>();

            foreach (var action in HotkeyActions.All)
            {
                var entry = config.For(action);
                if (!MacHotkeyBinding.TryParse(entry.Keys, out var binding))
                {
                    refused.Add($"{HotkeyActions.Describe(action)}: '{entry.Keys}' is not a shortcut");
                    continue;
                }

                uint id = _nextId++;
                var hotKeyId = new EventHotKeyID { Signature = Signature, Id = id };

                int status = RegisterEventHotKey(binding.KeyCode, binding.Modifiers, hotKeyId,
                    GetApplicationEventTarget(), 0, out IntPtr reference);

                if (status != 0 || reference == IntPtr.Zero)
                {
                    refused.Add($"{HotkeyActions.Describe(action)}: {binding} is taken (status {status})");
                    continue;
                }

                // Enforced again here so a hand-edited config cannot give a hold toggle to an
                // action whose hold already means something else.
                bool hold = entry.Hold && HotkeyActions.SupportsHoldToggle(action);

                _byId[id] = new Registration
                {
                    Action = action,
                    Reference = reference,
                    Binding = binding,
                    RequiresHold = hold,
                };
            }

            return refused;
        }

        public void UnregisterAll()
        {
            foreach (var registration in _byId.Values)
            {
                registration.HoldTimer?.Invalidate();
                UnregisterEventHotKey(registration.Reference);
            }

            _byId.Clear();
        }

        private int OnCarbonEvent(IntPtr callRef, IntPtr theEvent, IntPtr userData)
        {
            if (GetEventParameter(theEvent, EventParamDirectObject, TypeEventHotKeyID,
                    IntPtr.Zero, Marshal.SizeOf<EventHotKeyID>(), IntPtr.Zero,
                    out EventHotKeyID id) != 0)
            {
                return 0;
            }

            if (!_byId.TryGetValue(id.Id, out var registration)) return 0;

            if (GetEventKind(theEvent) == EventHotKeyPressed) OnPressed(registration);
            else OnReleased(registration);

            return 0;   // noErr — handled
        }

        /// <summary>
        /// Matches the keyboard's own repeat: one step immediately, a pause, then a fast
        /// stream for as long as the key is down.
        /// </summary>
        private static readonly TimeSpan RepeatInitialDelay = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(75);

        private void OnPressed(Registration registration)
        {
            registration.PressedAt = DateTime.UtcNow;
            registration.HoldFired = false;

            // Volume acts on press and repeats. Waiting for release would make a single tap
            // feel late, and holding the key would do nothing at all.
            if (registration.RepeatsWhileHeld)
            {
                Raise(registration.Action, held: false);

                registration.HoldTimer?.Invalidate();
                registration.HoldTimer = NSTimer.CreateRepeatingScheduledTimer(
                    RepeatInterval.TotalSeconds, _ =>
                    {
                        if (DateTime.UtcNow - registration.PressedAt < RepeatInitialDelay) return;
                        Raise(registration.Action, held: false, repeat: true);
                    });

                return;
            }

            // Everything without a tap/hold escalation acts on press too. Only an action
            // whose tap and hold differ has to wait for release to know which was meant.
            if (!registration.HasEscalation && !registration.RequiresHold)
            {
                Raise(registration.Action, held: false);
                return;
            }

            bool repeats = registration.Action == HotkeyActions.PlayPause;

            // The hold is a timer armed on press and cancelled on release, which is what
            // having a release event buys us. PlayPause repeats while held, so the queue
            // walks forward a track per interval rather than once per press.
            //
            // It ticks far faster than the threshold so the dial can fill smoothly; the hold
            // fires on whichever tick crosses it. One timer rather than two, because two
            // would have to agree about when the hold began.
            registration.HoldTimer?.Invalidate();
            registration.HoldTimer = NSTimer.CreateRepeatingScheduledTimer(DialTick, _ =>
            {
                double held = (DateTime.UtcNow - registration.PressedAt).TotalSeconds;
                double fraction = Math.Clamp(held / _holdDelaySeconds, 0, 1);

                if (!registration.HoldFired)
                {
                    // Only where a hold means something: the four with a built-in escalation,
                    // and anything the user has set to hold-to-activate. Filling a dial for a
                    // shortcut whose hold does nothing would promise an action that is not
                    // coming.
                    if (registration.ShowsDial) RaiseProgress(registration.Action, fraction);

                    if (fraction < 1) return;

                    registration.HoldFired = true;
                    if (registration.ShowsDial) RaiseHoldEnded(registration.Action);
                    Raise(registration.Action, held: true);

                    if (!repeats) registration.HoldTimer?.Invalidate();
                    return;
                }

                // Already fired: only a repeating hold has anything left to do, once per
                // delay rather than once per dial tick.
                if (!repeats) return;

                if (held >= _holdDelaySeconds)
                {
                    registration.PressedAt = DateTime.UtcNow;
                    Raise(registration.Action, held: true, repeat: true);
                }
            });
        }

        /// <summary>
        /// How often the dial is refreshed while a key is held. Fast enough to read as
        /// filling rather than stepping, and cheap: it only runs while a key is down.
        /// </summary>
        private const double DialTick = 1.0 / 30;

        private void OnReleased(Registration registration)
        {
            registration.HoldTimer?.Invalidate();
            registration.HoldTimer = null;

            RaiseHoldEnded(registration.Action);

            // The hold already fired and did the bigger thing; a tap on top would do the
            // small thing as well, which is not what holding meant.
            if (registration.HoldFired) return;

            // A shortcut set to "hold to activate" does nothing on a tap. That is the whole
            // point of the setting.
            if (registration.RequiresHold) return;

            Raise(registration.Action, held: false);
        }

        /// <summary>
        /// Carbon calls us on the main thread already, but the timer callback does not
        /// guarantee it, and every subscriber touches AppKit. One hop covers both.
        /// </summary>
        private void Raise(string action, bool held, bool repeat = false) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                Triggered?.Invoke(this, new HotkeyTriggeredEventArgs
                {
                    Action = action,
                    Held = held,
                    Repeat = repeat,
                }));

        private void RaiseProgress(string action, double fraction) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(
                () => HoldProgress?.Invoke(this, (action, fraction)));

        private void RaiseHoldEnded(string action) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(
                () => HoldEnded?.Invoke(this, action));

        public void Dispose()
        {
            UnregisterAll();

            if (_handlerRef != IntPtr.Zero)
            {
                RemoveEventHandler(_handlerRef);
                _handlerRef = IntPtr.Zero;
            }
        }
    }
}
