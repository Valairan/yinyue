import AppKit
import Carbon

// Spike 1 — tap/hold detection on macOS. ANSWERED: see FINDINGS.md.
//
// Carbon delivers both press and release with no Accessibility permission, so the hold is
// the interval between two events. CGEventSource.keyState — the direct GetAsyncKeyState
// analogue Windows relies on — never reads true for a non-modifier key unprivileged, and
// that turns out not to matter.
//
// This version measures press-to-release directly. The first version polled keyState in a
// blocking loop on the main thread, which is where the Carbon handler also runs, so every
// release queued behind the poll and every tap reported exactly the poll's 250 ms timeout.
// The numbers were still readable, but only after subtracting an artifact — so the poll is
// gone, and only kept as a side observation to show it still fails.
//
// Build and run (Command Line Tools are enough, no Xcode):
//   swiftc -O mac/spikes/taphold/main.swift -o /tmp/taphold && /tmp/taphold

let signature = OSType(0x59_49_4E_59)   // 'YINY'
let holdThreshold = 0.8                 // HotkeyConfig.HoldDelaySeconds default
let started = Date()

func stamp() -> String { String(format: "%7.3fs", Date().timeIntervalSince(started)) }

var pressedAt: Date?
var pressCount = 0

func onPressed() {
    pressCount += 1
    pressedAt = Date()

    // Sampled once, not polled: enough to show it is false even with the key physically
    // down, without blocking the run loop that carries the release we actually want.
    let polled = CGEventSource.keyState(.combinedSessionState, key: CGKeyCode(kVK_ANSI_P))
    print("\n[\(stamp())] press #\(pressCount)   keyState says down? \(polled)")
}

func onReleased() {
    guard let down = pressedAt else { return }
    let held = Date().timeIntervalSince(down)
    pressedAt = nil

    let verdict = held >= holdThreshold ? "HOLD" : "tap"
    print(String(format: "[\(stamp())] release      held %.3fs -> %@", held, verdict))
}

// ---------------------------------------------------------------------------

print("""
Yinyue — tap/hold spike
-----------------------
Accessibility trusted : \(AXIsProcessTrusted())

Hold  Ctrl+Alt+P  briefly, then for a full second. Expect 'tap' then 'HOLD',
and expect keyState to say false throughout — that is the point.

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

let status = RegisterEventHotKey(UInt32(kVK_ANSI_P), UInt32(controlKey | optionKey),
                                 hotKeyID, GetApplicationEventTarget(), 0, &hotKeyRef)
guard status == noErr else { print("RegisterEventHotKey failed: \(status)"); exit(1) }
print("[\(stamp())] Ctrl+Alt+P registered (status 0). Listening…")

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
app.run()
