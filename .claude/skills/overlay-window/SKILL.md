---
name: overlay-window
description: Build and fix the Yinyue summon overlay — global hotkey toggle, configurable screen anchoring (default bottom-right), DPI and multi-monitor correctness, keyboard focus on show, dismiss on focus loss, and hiding from taskbar/alt-tab. Use for any work on MainWindow placement, ShowOverlay/Hide, HotkeyManager, or window interop.
---

# The summon overlay

The overlay is the product. It must feel like PowerToys Run: press the hotkey, it is
*there* with the keyboard already in it; press Escape or click away, it is gone. Anything
that adds perceptible latency to that loop is a bug.

## The single most important invariant: one stable HWND

`HotkeyManager.RegisterAll(hwnd)` and `SmtcService.Initialize(hwnd)` both bind to a window
handle. If that handle changes, global hotkeys and media keys silently stop working.

Therefore:

- **Hide, never close.** `Window.Close()` destroys the HWND. `ShowOverlay`/dismiss must use
  `Show()`/`Hide()` only.
- **Create the handle eagerly at startup.** `new MainWindow()` does *not* allocate an HWND —
  WPF creates it on first `Show()`. Since the app starts hidden in the tray but needs
  hotkeys live immediately, force it:

  ```csharp
  var helper = new WindowInteropHelper(_overlayWindow);
  helper.EnsureHandle();               // HWND now exists, window still not shown
  _hotkeys.RegisterAll(helper.Handle);
  _smtc.Initialize(helper.Handle);
  ```

  `App.OnStartup` currently constructs `MainWindow` and stops there, which is why neither
  subsystem is live.
- `HwndSource.FromHwnd` returns null before the handle exists. Inside the window itself,
  do handle-dependent work in `OnSourceInitialized`, not the constructor.

## Positioning

### Work in physical pixels via Win32, not WPF units

`Window.Left`/`Top` are device-independent units, and under Per-Monitor-V2 DPI (which
`app.manifest` enables) the mapping between DIPs and a secondary monitor with a different
scale factor is a reliable source of off-by-a-scale-factor bugs. Position with
`SetWindowPos` in physical pixels instead — `Screen.WorkingArea` is already in that space,
so no conversion is needed and mixed-DPI setups work by construction.

```csharp
[DllImport("user32.dll")]
static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
```

Size in physical pixels = DIP size x the monitor scale, from
`VisualTreeHelper.GetDpi(this).DpiScaleX` (valid only after `OnSourceInitialized`).

### Use WorkingArea, never Bounds

`Screen.WorkingArea` excludes the taskbar and any docked appbars; `Screen.Bounds` does not.
Bottom-right anchored against `Bounds` puts the overlay underneath the taskbar. Always
`WorkingArea`.

### Anchor model

Requirement 2 is "configurable position, default bottom-right". Express it as an anchor
plus a margin rather than absolute coordinates, so it survives resolution changes and
monitor swaps:

```csharp
enum OverlayAnchor { TopLeft, TopCenter, TopRight, Center,
                     BottomLeft, BottomCenter, BottomRight }

// config.json
// { "anchor": "BottomRight", "marginX": 16, "marginY": 16, "monitor": "Primary" }
```

Resolve `monitor` to a `Screen` by device name, falling back to `Screen.PrimaryScreen` when
the configured monitor is absent — people unplug docks, and a missing monitor must not mean
an overlay drawn off-screen. Two other useful `monitor` values: `Active` (the screen with
the foreground window, `Screen.FromHandle(GetForegroundWindow())`) and `Cursor`
(`Screen.FromPoint(Cursor.Position)`) — a summon overlay following the user across monitors
usually feels better than one pinned to a fixed display.

Always clamp the final rectangle into the target `WorkingArea` after computing it.

### Recompute on every show

Do not cache coordinates. Resolution changes, DPI changes, docking, and taskbar moves all
invalidate them. Recomputing costs microseconds. Also override `OnDpiChanged` to reposition
while visible.

## Showing and focusing

A `Topmost` window that was hidden does not reliably receive keyboard focus on `Show()` —
Windows restricts foreground activation to the process that owns the current foreground
window. `Activate()` alone often loses the race and the user types into whatever was behind
the overlay.

```csharp
public void ShowOverlay()
{
    PositionOverlay();          // recompute before showing, so it never flashes at 0,0
    Show();
    Activate();
    SetForegroundWindow(new WindowInteropHelper(this).Handle);
    SearchTextBox.Focus();      // put the caret where the user expects it
    Keyboard.Focus(SearchTextBox);
}
```

If focus still fails, the standard workaround is `AttachThreadInput` against the current
foreground thread for the duration of the `SetForegroundWindow` call. Reach for it only if
the simple path proves flaky in practice.

## Dismissing

- `Deactivated` -> `Hide()`. This is the click-away behaviour.
- `Escape` at the window level -> `Hide()`. `SearchTextBox_PreviewKeyDown` currently handles
  Escape by only closing the search box; the window-level case still needs handling.
- The tray menu and the toggle hotkey both route through `App.ToggleOverlay`, which already
  branches on `IsVisible`. Keep that as the one entry point.

Guard against the dismiss-then-resummon race: if the toggle hotkey fires while the window
is deactivating, `IsVisible` may still be true and the press is swallowed. Track your own
`_isShown` flag set in `ShowOverlay`/`Hide` if this shows up.

## Popups are separate windows, so key handling does not reach them

The search results and the queue both live in WPF `Popup`s. A `Popup` is hosted in **its own
top-level HWND with its own visual tree**, not as a descendant of the overlay. Routed events
raised inside a popup therefore never tunnel through `MainWindow`, and handlers on the
window or on sibling controls do not see them.

This bit once already: arrowing down into the search results moved focus into the popup,
and Enter then hit nothing — the list had no key handler of its own, and the search box's
handler could not see the event. Enter looked like it did nothing.

When adding keyboard behaviour to popup content:

- Put the handler **on the control inside the popup**. A window-level `PreviewKeyDown` is
  not a substitute; keep it only as a fallback for when focus is outside the popup.
- Give focus a way back out. `Up` from the first result returns to the search box, so the
  list is not a keyboard trap.
- Every commit gesture needs all three routes wired separately: Enter in the text box (list
  not focused), Enter in the list (focused), and double-click.

## Never let the overlay strand the user

The overlay is the only always-available UI, so anything that hides its escape hatches can
lock a user out of the app entirely. This has already happened once: `OpenSearch()`
collapsed `HeaderButtonsPanel` to make room for the search box, which hid the settings cog.
Because the overlay opens into search whenever nothing is playing, a fresh install had no
reachable way to add a library — so nothing could ever play, so it always opened into
search. A closed loop.

Rules that follow from it:

- **The settings cog is always visible.** `HeaderButtonsPanel` must never be collapsed.
  Status text and the search box swap within their own cell; the buttons sit in a separate
  grid column.
- **Keep a non-overlay entry point.** The tray menu carries its own Settings item precisely
  because the overlay can auto-hide at an inconvenient moment.
- **Empty states point somewhere.** When no source is configured, say so and name the
  control to click — do not present an empty search box, which just looks broken.
- Escape with an empty search box hides the whole overlay. That is correct, but it means
  Escape is not a way to *reveal* anything, so nothing may depend on it for discoverability.

## Hiding from taskbar and alt-tab

`ShowInTaskbar="False"` is already set in XAML, but that alone still leaves the window in
the alt-tab list in some configurations. The reliable fix is the tool-window extended
style, applied in `OnSourceInitialized`:

```csharp
const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x00000080;
var hwnd = new WindowInteropHelper(this).Handle;
SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
```

## Global hotkeys

`HotkeyManager` uses `RegisterHotKey` + a `WM_HOTKEY` (`0x0312`) `HwndSource` hook, which is
the right approach — it needs no low-level keyboard hook, so it costs nothing at idle.

Things to get right when extending it:

- **Always check what `RegisterHotKey` returns.** It fails when another app already owns
  the combination, and a silently dead hotkey is the worst outcome — the app looks broken
  with no explanation. `HotkeyManager.Apply` returns the display names of everything the OS
  refused, and `App` surfaces them as a tray balloon.
- Keep `MOD_NOREPEAT`; without it, holding the key repeats the action.
- Registration is per-HWND and per-thread. Register from the UI thread on the overlay HWND.
- **Bindings are configurable and live-rebindable.** `HotkeyBinding` parses the
  `"Ctrl+Alt+Space"` strings in `config.json`, and `Apply` unregisters then re-registers on
  the same HWND, so settings changes take effect without a restart. `App.ApplyHotkeys`
  compares a signature first so unrelated config saves do not churn registrations — that
  churn would briefly leave no hotkey live.
- **Punctuation keys are named, not symbolic.** `Plus`, `Minus`, `Comma` and friends, because
  `+` separates the parts of a binding — `Ctrl+Alt++` is unparseable. `HotkeyBinding`
  round-trips the names and still accepts the raw enum names as input.
- Guard rails that must stay: every binding needs at least one modifier (a bare global key
  would swallow that key system-wide), `ParseOrDefault` falls back when `config.json` holds
  something malformed, and duplicate bindings are rejected before registration rather than
  letting Windows silently drop the second one.

## Performance notes specific to this window

**Do not reintroduce `AllowsTransparency="True"`.** It makes the window a layered window,
pushing composition onto a software path and costing memory proportional to window area.
The overlay is opaque, so the only thing it ever bought was rounded corners — and DWM does
those for free, applied in `OnSourceInitialized`:

```csharp
int preference = 2; // DWMWCP_ROUND
DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int));
```

This is Windows 11 only. On Windows 10 the attribute is unknown, the call fails harmlessly,
and the overlay is square — an accepted cosmetic trade, not a bug to work around. Because
DWM clips the window itself, the root `Border` must keep `CornerRadius="0"`: a radius there
would curve the border stroke inward while DWM clips outward.

Keep the window instance alive for the whole app lifetime — hidden WPF windows cost almost
nothing, and recreating one per summon would both break the stable-HWND invariant and add
visible latency.
