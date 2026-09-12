import AppKit
import Carbon

// Spike 1 — tap/hold detection on macOS.
//
// CLAUDE.md records this as the one unverified assumption in the macOS plan, and four
// shortcuts depend on it: Ctrl+Alt+P, Ctrl+Alt+O, Ctrl+Alt+Pipe and Ctrl+Alt+Backspace all
// tap for the small action and hold for the bigger one.
//
// Windows does this by registering with MOD_NOREPEAT — so exactly one WM_HOTKEY arrives on
// press and nothing on release — then polling GetAsyncKeyState to find out when the key came
// back up. Carbon's RegisterEventHotKey gives us the same shape: a kEventHotKeyPressed event
// and, if we ask for it, a kEventHotKeyReleased one.
//
// The question this answers is narrower than "does it work": it is whether polling
// CGEventSource.keyState for a NON-MODIFIER key reports true while the key is held, from a
// process that has NOT been granted Accessibility. If it needs Accessibility, the Mac app has
// to prompt for it during onboarding and degrade gracefully when refused — which changes the
// first-run flow, the settings UI, and possibly the defaults.
//
// It reports three independent signals per press so they can be compared:
//   · keyState       — CGEventSource.keyState, the direct GetAsyncKeyState analogue
//   · releaseEvent   — Carbon's own kEventHotKeyReleased, which needs no permission at all
//   · modifierFlags  — NSEvent.modifierFlags, which is known to work unprivileged
//
// Build and run (Command Line Tools are enough, no Xcode):
//   swiftc -O mac/spikes/taphold/main.swift -o /tmp/taphold && /tmp/taphold

let signature = OSType(0x59_49_4E_59)   // 'YINY'
let holdThreshold = 0.8                 // HotkeyConfig.HoldDelaySeconds default
let pollInterval  = 0.02

func stamp() -> String {
    String(format: "%7.3fs", Date().timeIntervalSince(started))
}
let started = Date()

/// Polls the three signals until the key is observed up, or we give up.
/// Returns how long the key appeared to be held, and which signals ever saw it down.
func observeHold(keyCode: CGKeyCode) -> (held: TimeSpan, sawKeyState: Bool) {
    let begin = Date()
    var sawKeyState = false
    var lastDown = begin

    while Date().timeIntervalSince(begin) < 5.0 {
        let down = CGEventSource.keyState(.combinedSessionState, key: keyCode)
        if down {
            sawKeyState = true
            lastDown = Date()
        } else if sawKeyState {
            // Observed down and then up — that is a complete hold measurement.
            break
        } else if Date().timeIntervalSince(begin) > 0.25 {
            // Never saw it down at all within a human keypress. That is the failure mode
            // this spike exists to detect.
            break
        }
        Thread.sleep(forTimeInterval: pollInterval)
    }

    return (lastDown.timeIntervalSince(begin), sawKeyState)
}

typealias TimeSpan = TimeInterval

var pressCount = 0

func onPressed() {
    pressCount += 1
    let n = pressCount
    print("\n[\(stamp())] press #\(n) — Ctrl+Alt+P")

    let mods = NSEvent.modifierFlags
    print("           modifierFlags: control=\(mods.contains(.control)) option=\(mods.contains(.option))")

    let (held, sawKeyState) = observeHold(keyCode: CGKeyCode(kVK_ANSI_P))

    if sawKeyState {
        let verdict = held >= holdThreshold ? "HOLD" : "tap"
        print(String(format: "           keyState: WORKS — key down for %.3fs -> %@", held, verdict))
    } else {
        print("           keyState: NEVER READ TRUE — polling cannot see this key.")
        print("           ^ this is the failure case. Accessibility is probably required.")
    }
}

func onReleased() {
    print("[\(stamp())] release event received from Carbon")
}

// ---------------------------------------------------------------------------

let trusted = AXIsProcessTrusted()
print("""
Yinyue — tap/hold spike
-----------------------
Accessibility trusted : \(trusted)   \(trusted ? "(NOT the interesting case — revoke it to test properly)" : "(good: this is the unprivileged case we care about)")

Hold  Ctrl+Alt+P  for a moment, then release. Do it a few times:
  · a quick tap      -> expect 'tap'
  · a deliberate 1s+ -> expect 'HOLD'

Ctrl+C to finish.
""")

var handler: EventHandlerRef?
var spec = [
    EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed)),
    EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyReleased)),
]

InstallEventHandler(GetApplicationEventTarget(), { _, event, _ -> OSStatus in
    guard let event else { return noErr }
    if GetEventKind(event) == UInt32(kEventHotKeyPressed) { onPressed() } else { onReleased() }
    return noErr
}, spec.count, &spec, nil, &handler)

var hotKeyID = EventHotKeyID(signature: signature, id: 1)
var hotKeyRef: EventHotKeyRef?

let status = RegisterEventHotKey(
    UInt32(kVK_ANSI_P),
    UInt32(controlKey | optionKey),
    hotKeyID,
    GetApplicationEventTarget(),
    0,
    &hotKeyRef)

guard status == noErr else {
    print("RegisterEventHotKey failed: \(status)")
    exit(1)
}
print("[\(stamp())] Ctrl+Alt+P registered (status 0). Listening…")

// An LSUIElement-style process: no Dock icon, no menu bar, but a real run loop.
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
app.run()
