# Tap/hold on macOS — answered

CLAUDE.md carried this as the one unverified assumption in the macOS plan, with four
shortcuts depending on it. Measured on macOS 26, from a process with
`AXIsProcessTrusted() == false`.

## Result

| Signal | Needs Accessibility | Works unprivileged |
|---|---|---|
| `RegisterEventHotKey` (press) | no | **yes** — status 0 |
| `kEventHotKeyReleased` (release) | no | **yes** — fired on all six presses |
| `CGEventSource.keyState` (polling) | **yes** | **no** — never read true |
| `NSEvent.modifierFlags` | no | yes (modifiers only, as expected) |

**Polling is dead and it does not matter.** Carbon delivers a real release event, so the
hold is the interval between two events rather than something to be discovered by sampling.

Six presses, press-to-release, with the spike's own 250 ms blocking poll subtracted:

```
#1  tap        #4  2700 ms  HOLD
#2  tap        #5  1976 ms  HOLD
#3  tap        #6  tap
```

## What this means for the design

macOS is **better placed than Windows here, not worse.** Windows registers with
`MOD_NOREPEAT`, which delivers exactly one `WM_HOTKEY` on press and nothing on release — so
`HotkeyManager` has no choice but to poll `GetAsyncKeyState` to find out when the key came
back up. macOS hands us the release directly.

So the Mac shell is **event-driven, not polled**:

- Start a timer on `kEventHotKeyPressed`, cancel it on `kEventHotKeyReleased`.
- Under `HoldDelaySeconds` is a tap, over it is a hold. Same thresholds, same semantics, same
  `HotkeyConfig` — only the detection differs.
- `repeats: true` (the PlayPause escalation) becomes a repeating timer cancelled on release,
  which is closer to what the behaviour actually means than re-polling is.

And the consequences that do *not* follow, which is the point of having asked:

- **No Accessibility prompt.** Onboarding does not need one, settings does not need to explain
  one, and there is no degraded mode to design for a refusal. That was the risk.
- No entitlement, no TCC entry, no first-run permission dialog.

## Reading the log from the first run

The original spike polled `keyState` in a blocking loop on the main thread, and the Carbon
handler runs on that same run loop — so release events queued behind the poll and every tap
reported exactly 251 ms, the poll's own timeout. Real holds still showed through because they
exceeded it. `main.swift` now measures press-to-release directly and does not poll, so a
re-run gives clean numbers; the table above is the corrected reading of the first run.

Same lesson as the `NSApplication.Init()` deadlock in the C# self-test: on macOS, blocking
the main thread hides the very events you are waiting for.
