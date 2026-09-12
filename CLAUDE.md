# Yinyue

A lightweight, always-resident music player with a PowerToys-Run-style summon overlay.
Plays from a **Jellyfin** server or from **local files**, integrates with OS media keys, and
stays out of the way until a global hotkey brings it up.

## Product requirements

These are the defining constraints. Weigh every design decision against them.

1. **Summon overlay, not a window.** A global hotkey toggles a small frameless panel. No
   taskbar entry, no alt-tab entry. It appears, you act, it dismisses.
2. **Configurable position, default bottom-right.** Anchored to the work area of a chosen
   monitor, DPI-correct, never overlapping the taskbar. See the `overlay-window` skill.
3. **OS media key control.** Windows SMTC today; macOS `MPRemoteCommandCenter` later. The
   OS now-playing surface must always reflect real playback state. See `media-integration`.
4. **Lightweight and performant.** It runs all day. Idle CPU ~0%, small working set, fast
   cold start. Reject dependencies and patterns that fight this (see *Performance budget*).
5. **Pluggable sources.** Jellyfin is the first remote backend, local files the second.
   Other servers (Navidrome/Subsonic, Plex) come later — so source-specific code belongs
   behind an interface, never inline in the UI.

## Repository layout

```
core/Yinyue.Core/         net8.0, no OS suffix — the platform-neutral half, shared by both apps
  Models/                 Track, TrackCollection, SearchQuery, AppConfig, HotkeyConfig, Jellyfin DTOs
  Services/               Playback, MusicLibrary + sources, Jellyfin client, local index, config, queue
  Platform/               The seams: IAudioPlayer, ISecretStore, AppPaths
win/                      .NET 8 WPF app — the working implementation, and the shell for Windows
  App.xaml.cs             Composition root: tray icon, single-instance mutex, service graph
  App.xaml                Catppuccin Mocha palette and shared control styles
  MainWindow.xaml(.cs)    The summon overlay — a view over PlaybackService
  SettingsWindow.xaml(.cs) Jellyfin sign-in, library folders, overlay position
  Models/                 HotkeyBinding — parses onto WPF's Key enum, so it stays here
  Services/               Audio, Smtc, Hotkey, Overlay, WindowStyling, Startup, Setup, SleepTimer, Dpapi
mac/                      Empty. Menu-bar shell over Yinyue.Core, not yet started.
tests/Yinyue.Core.Tests/  net8.0 — runs on macOS or Windows. Logic, no desktop needed
tests/Yinyue.Tests/       net8.0-windows — XAML, panel placement, real hotkey registration
tests/Shared/             Check (the harness) and Fakes, compiled into both suites
.claude/skills/           Project skills — read these before touching their domain
```

## Current state — read this before planning work

**Milestones 1 and 2 are implemented: local and Jellyfin playback both work end to end.**
`App.OnStartup` is the composition root and wires the whole graph against one eagerly
created HWND.

| Piece | State |
|---|---|
| `ConfigService` | `%APPDATA%\Yinyue\config.json`, atomic writes, DPAPI token protection |
| `IMusicSource` / `MusicLibrary` | Source abstraction. Local + Jellyfin registered; merges and dedups |
| `PlaybackService` | Owns queue, play order, shuffle, loop, volume, current track. Outlives the overlay |
| `MainWindow` | Search, transport, queue view, keyboard/wheel control, anchored positioning |
| `SettingsWindow` | Tabbed: Remote (server, streaming, offline), Local, Overlay, Hotkeys |
| `HotkeyManager` | Registered on the overlay HWND; reports combinations it could not claim |
| `SmtcService` | Initialized on the same HWND, bridged to `PlaybackService` in `App` |
| `JellyfinMusicSource` | Search, direct-play streaming, artwork cache, favourites (get and set) |
| `JellyfinPlaybackReporter` | Start/progress/stopped reporting, throttled to 10s |
| `ArtworkCache` | Shared disk cache, LRU-pruned to 50 MB on startup |
| `QueueStore` | Persists the queue to `queue.json`; restored armed but silent on startup |

Verified headlessly: builds with zero warnings, starts, writes a default config, runs
without unhandled exceptions, exits without orphans. The Jellyfin source has been exercised
against a live server — search returns mapped tracks, the stream URL returns
`206 Partial Content, audio/flac` (direct play, no transcode), artwork downloads and caches,
and the favourites query pages back 119 tracks. All eight global hotkeys, including
`Ctrl+Alt+Plus`, were accepted by `RegisterHotKey` against a live HWND.

Queue bookkeeping, hotkey parsing, cross-source deduplication, and index pruning are all
covered by harness runs against the real classes: a duplicate shared by two sources collapses to the local copy, duplicates *within*
one source survive, pruning removes exactly the rows whose files or folders are gone,
removal keeps the current-position index pointing at the same track, toggling shuffle
never loses your place, and a saved queue round-trips through disk with its shuffled order,
position, and modes intact while stale, corrupt, and empty files are all rejected.

**Not verified interactively** — overlay position, focus on summon, global hotkeys, and
media keys all need a real desktop session. See the `run-yinyue` skill.

## Tests

**Two suites. Run both** — neither is a superset of the other.

```bash
dotnet run --project tests/Yinyue.Core.Tests   # anywhere, including macOS
dotnet run --project tests/Yinyue.Tests        # Windows only
```

Both exit 0 on pass. The split follows the code: `Yinyue.Core.Tests` targets plain `net8.0`
and covers what has no platform in it — queue bookkeeping, play order, shuffle, volume and
mute, search prefixes, collections, persistence, hotkey *configuration*. 198 checks, and they
run on a Mac. `Yinyue.Tests` targets `net8.0-windows` and keeps what genuinely needs a
desktop: XAML that must parse, panel placement, hotkey *parsing and registration*, and the
installer's registry seeds.

Both are plain console runners rather than a test framework. The Windows one needs an STA
thread, a live WPF `Application` for `StaticResource` lookups, and real Win32 hotkey
registration — getting a conventional runner to supply all three costs more than it saves at
this size, and an exe's exit code is all CI needs. The Core one is a plain runner simply to
match it.

The Windows suite constructs every window for real, which is the only way XAML errors
surface: the build only proves the C# compiles, and `SettingsWindow` is created lazily so a
normal launch never touches it.

`tests/Shared/` holds `Check` and the fakes, compiled into both by `<Compile Include>` rather
than shared through a project reference — the two runners are separate executables and should
not know about each other.

**Tests use `SilentAudioPlayer`, not the real engine.** Queue bookkeeping is decided above
the audio engine, so these tests never needed one; they used to construct a WinRT
`MediaPlayer` purely to satisfy a constructor, and that was the only thing tying them to
Windows.

## Build and run

Requires the .NET 8 desktop runtime. Building `win/` needs an SDK on Windows; `core/` needs
only a plain .NET 8 SDK and builds anywhere.

```bash
dotnet build win/Yinyue.csproj              # ~3s — pulls in Yinyue.Core
dotnet run  --project win/Yinyue.csproj

dotnet build core/Yinyue.Core               # macOS or Windows
```

Use the **`run-yinyue` skill** to launch it. It is a tray app with a single-instance mutex:
a stray process from a previous run makes the next launch pop a modal dialog and exit, and
naive launches leave orphans behind.

Release publish. **`--no-self-contained`, not `--self-contained false`** — the .NET 10 SDK
silently ignores the latter and produces a 172 MB self-contained build instead of a 26 MB
framework-dependent one. Measured, twice, before it was believed:

```bash
dotnet publish win/Yinyue.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true
```

## Installer

`installer/build.ps1` publishes and packages a **per-user MSI** (~6 MB). Per-user because
configuration lives in `%APPDATA%` per user: an elevated per-machine install would seed
settings for whoever ran setup and leave every other account on defaults. It also means no
administrator prompt.

**WiX v5, pinned.** v6 and later refuse to run until the Open Source Maintenance Fee
agreement is accepted. v5 is the last MS-RL release — **MS-RL is OSI-approved**, and the
package sets `requireLicenseAcceptance: false`, so the whole toolchain here is free and open
source with nothing to agree to.

The cost of the pin is maintenance, not licensing: fixes will increasingly land only in v6+.
The escape routes are accepting the OSMF terms, or moving to `wixl` from GNU msitools
(GPLv2+, and builds on Linux) — which would mean re-authoring the wizard pages, since it has
no `WixUI` dialog sets.

**The package is declarative.** It installs two files, a Start Menu shortcut, and three
values under `HKCU\Software\Yinyue\Setup`. It does not write `config.json`.
`SetupSeedService` reads those values on the next launch, applies them, and deletes the key.

Precisely: **no custom action is authored here, and none runs during installation.** The
compiled MSI does contain three, and it is worth knowing what they are —
`SetLIBRARYFOLDER` is type 51, which assigns a property and executes no code, while
`WixUIPrintEula` and `WixUIValidatePath` come from the standard `WixUI` dialog library and
run only in the interactive UI sequence. Check with
`SELECT \`Action\`, \`Type\` FROM \`CustomAction\`` before repeating the claim.

That split is the point:

- Custom actions are the fragile half of any MSI. None is authored here, and nothing runs
  during installation — so there is nothing of ours to go wrong.
- The logic lands somewhere testable — the suite writes seeds to the registry and checks what
  the app makes of them, including an unrecognised anchor and a folder that has since gone.
- **An installer cannot validate a hotkey.** Nothing but `RegisterHotKey` at runtime can say
  whether another app already owns `Ctrl+Alt+Space`, so the wizard does not ask for one. It
  asks only for things it can be right about: the overlay corner, start-at-sign-in, and a
  folder to scan.
- Startup is recorded as a *preference*, not written to the Run key by the installer.
  `StartupService` applies it against its own executable path, so the installer never has to
  guess where the app ended up and there is one owner of that key.
- The seed applies **once**. Left in place it would re-apply on every launch, silently undoing
  anything changed in settings since. Re-running the installer writes it again, which is what
  someone picking a different corner expects.

## Architecture and conventions

**Two projects, one rule: `Yinyue.Core` may not know what it is running on.** It targets
plain `net8.0` with no OS suffix, which is not a detail — it is the thing that enforces the
rule. Anything platform-specific fails to compile there rather than getting caught in review.
If a change to Core needs a `#if WINDOWS`, the change is in the wrong project.

What lives where is decided by that rule, not by taste. Core holds the queue, play order,
shuffle, loop, retry and prebuffer logic; the Jellyfin client; `MusicLibrary` and its sources;
the local SQLite index; `SearchQuery`; config and queue persistence. The shells hold the
window, the audio engine, the media-key bridge, the global hotkeys, the secret store and the
startup registration.

**The seams are two interfaces, and both exist because something in Core needs them** —
`IAudioPlayer` (what `PlaybackService` needs from an audio engine, and no more) and
`ISecretStore` (what `ConfigService` needs to protect a token). There are no speculative
interfaces for SMTC, hotkeys or startup registration: nothing in Core consumes those, so a
contract for them would be guesswork written before the second implementation exists. Add
them when the Mac shell is real and the shape is known, not before.

- `ISecretStore` is optional on `ConfigService` so the many call sites that never touch a
  token need not supply one. Omitting it does **not** mean the token is stored in the clear —
  `UnavailableSecretStore` declines to store it at all. A forgotten wiring costs a sign-in,
  which is visible; a plaintext fallback costs a readable token in `config.json`, which is
  not. Fail in the recoverable direction.
- `AppPaths.DataFolder` is the single resolver for the data folder, and it is explicit about
  macOS: .NET maps `SpecialFolder.ApplicationData` to `~/.config` there, following XDG rather
  than Apple, and a Mac user looks in `~/Library/Application Support`. `LibraryIndexerService`
  was the one place in the portable half that resolved this for itself, which on macOS would
  have put `tracks.db` somewhere nothing else looked.

**Namespaces did not change.** Core keeps `Yinyue.Models` and `Yinyue.Services`, which is why
the extraction touched no `using` directive in any WPF file. Do not "tidy" this into
`Yinyue.Core.*` — the churn would be large and buys nothing.

**Target framework.** Core is `net8.0`. The Windows app is
`net8.0-windows10.0.19041.0`; the Windows-version suffix is what makes the WinRT surface
(`Windows.Media.*`, `Windows.Storage.*`) callable directly with no CsWinRT package. Do not
lower it — `SmtcService` and `AudioPlayerService` both depend on it, and do not add it to
Core.

**Playback failures recover rather than stopping.** `AudioPlayerService.MediaFailed`
surfaces what the engine could not play; `PlaybackService` re-resolves and retries the track
once — a dropped connection or an expired transcode session usually recovers on a fresh
request — then skips on. Three consecutive failures stop the queue instead of racing through
it. Any successful start clears the streak.

**Resume points.** `Track.ResumePosition` carries Jellyfin's `UserData.PlaybackPositionTicks`,
and a track abandoned part-way resumes there. Ignored within ten seconds of either end: one
would "resume" something that had effectively finished, the other treats a brief skip past
the start as progress.

**Animations are switchable, and the switch is really about transparency.**
`OverlayConfig.Animations` is read once in each window's constructor, because
`AllowsTransparency` cannot be changed after a window has a source — so a change needs a
restart, and the settings window says so on save. With it off, `WindowStyling.MakeOpaque`
drops transparency, restores an opaque background, squares the root `Border`, and DWM
supplies the corners instead. The fade itself is nearly free; the software-composited path
is the part worth reclaiming on a weak machine.

**Background opacity is a panel tint, not window opacity.** `OverlayConfig.BackgroundOpacity`
colours the root `Border` through `WindowStyling.Translucent`, so text and artwork stay fully
legible while the panel behind them goes translucent. Using `Window.Opacity` for this would
fade the content too. It applies to the overlay and the toast only — the settings window
keeps its chrome and stays opaque — and it needs transparency, so it does nothing while
animations are off. `AnimationMilliseconds` is read at use rather than cached, so both it and
the opacity take effect without a restart, unlike the animations switch itself.

**Window fades require `AllowsTransparency`.** WPF only applies `WS_EX_LAYERED` to a
transparent window, and an unlayered window ignores `Opacity` outright — `SetWindowLong`
silently drops the style and `SetLayeredWindowAttributes` fails with error 87. Both facts
are verified in the suite. So the overlay and the toast are transparent windows, and their
rounded corners come from the root `Border` rather than DWM. That reverses an earlier
decision to drop transparency for performance: it bought only corners then, and it buys the
fades now. Do not reach for `SetLayeredWindowAttributes` alongside it — that API is mutually
exclusive with the `UpdateLayeredWindow` path WPF uses once transparency is on.

The settings window keeps its chrome, so it cannot be transparent and therefore does not
fade.

**Auto-hide.** The overlay dismisses itself after `OverlayConfig.AutoHideSeconds` of no
interaction, on by default at 10 s and switchable off. Any key, click or pointer movement
restarts the countdown, and an open search or queue — or the settings window in front —
suspends it entirely, because reading is not idling.

**Tracks stream; they are not downloaded first.** Playback reads from the Jellyfin stream
URL as it plays. A download-then-play cache was built and measured, and then removed: on a
link that already runs ahead of playback it bought smoothness that was mostly already there,
in exchange for a wait before every track that had not been fetched yet. Streaming is the
decided behaviour — do not reintroduce a track cache without a new reason.

**WinRT reports no buffering state for an HTTP audio stream.** Measured directly against a
live server, not inferred: `MediaPlaybackSession.DownloadProgress` returns `-1` for the whole
session, and `BufferingProgress` throws `InvalidCastException` through the CsWinRT
projection. So there is no signal to drive a loading indicator, and nothing to gate playback
on — any design built on those two properties waits out its timeout and then plays anyway.
If buffering feedback is ever wanted, it has to come from owning the transfer, which is the
cost that made the cache not worth keeping.

For reference, measured on a live server at Original quality (direct-play FLAC, 20–90 MB per
track): a full download runs at 0.10–0.65 of playback time. Comfortably ahead of the
playhead, but not by so wide a margin that a slow moment is impossible — which is the
argument for the quality setting rather than for a cache.

**Pre-buffering.** `PlaybackService.StartPrebuffer` opens the *next* track's stream while
the current one plays, so switching does not stall on a fresh connection. Measured against a
live Jellyfin server: 276 ms cold versus 52 ms warm. Rules that keep it honest:

- Remote sources only. A local file opens instantly and gains nothing.
- Fire-and-forget. A prebuffer failure is an optimisation not taken, never a playback error.
- Cancelled and restarted whenever the queue moves, so it never holds a stale stream.
- Started only *after* the current track is settled, so it competes with nothing that matters.
- `PeekNext` honours queue looping and returns null at the end when looping is off.

**Audio.** `AudioPlayerService` wraps WinRT `MediaPlayer`, which handles both local files
and HTTP streams and is the cheapest option that also feeds SMTC naturally. Do not add a second
audio stack alongside it — `NAudio` was declared and unused, and has been removed; another
one costs startup time and memory for no gain.

**The sleep timer is not auto-hide.** They are the only two timers in the app and are easily
confused: the sleep timer **pauses playback** after a long, absolute interval and nothing
resets it; auto-hide **hides the overlay** after a few seconds of inactivity and any input
restarts it. The settings caption says so, because the difference is not guessable from the
names.

- **Steps are configurable**, because 60 minutes is a nap and a working day is not. Stored as
  a list of minutes and sanitised on the way in: out-of-range values dropped, deduplicated,
  sorted, capped at `MaxSteps`. An empty result falls back to the defaults rather than
  leaving a cycle with nothing in it but "off".
- **"Off" is never stored in the list.** `SleepTimerService` prepends it, so it is always the
  entry point and always reachable — otherwise the shortcut could arm the timer but never
  disarm it.
- The cap exists because the only way to reach a step is to press the shortcut until it comes
  round. A long list makes the far end unusable.
- **A separate on/off switch**, distinct from "currently set to off". The hotkey cycles, so a
  mistaken press silently arms something that stops the music an hour later; the switch makes
  that unreachable. Turning it off **cancels anything running** — a countdown alive under a
  disabled feature would stop playback with no visible cause.
- Changing the steps under a running timer stops it if its duration is no longer in the list,
  rather than stranding it outside the cycle.
- Pressing the shortcut while disabled says so. Silence would read as a broken shortcut.
- **It says how long is left.** A readout appears in the overlay header while the timer runs,
  and the tray tooltip carries it too. Before this its only surface was the toast raised when
  cycling, so checking it meant pressing the shortcut — which also changed the setting.
  `SleepTimerService.Describe` is coarse far out and precise near the end, and rounds up, so
  it never reads "0m" with music still to come. The tooltip is shared with volume and written
  from one place, since `NotifyIcon.Text` throws above 63 characters rather than truncating.

**Search prefixes.** A leading prefix narrows what a search returns. `SearchQuery.Parse`
turns the raw box text into a term plus three orthogonal narrowings, and `MusicLibrary`
parses **once** at its boundary so every source is handed the same interpretation rather
than each re-deriving it.

| Prefix | Effect |
|---|---|
| `track:` `song:` | Songs only |
| `playlist:` | Playlists only |
| `album:` | Albums only |
| `fav:` `favourite:` | Favourites only, of any kind |
| `queue:` `q:` | Search what is already queued |

Plurals are accepted throughout, and `favorite:` as well as `favourite:`.

The three dimensions — `Scope` (what kind), `FavoritesOnly` (a flag), `Target` (library or
queue) — are deliberately separate rather than one enum. A favourite may be a track *or* a
collection, so folding it into the kind enum would be a lie; and every future combination
would otherwise need a new member.

- **Only a prefix at the very start counts, and only one.** A colon later in the line is just
  a colon — titles contain them ("Alive: Remastered"), and a search that silently
  reinterpreted those would be worse than one that never tried. A prefix must also be a
  single word, so "my track:thing" is a search.
- An unrecognised prefix stays literal rather than erroring. `artist:bowie` searches for that
  text, which is the safe reading when the vocabulary may grow.
- Plurals and `song:` are accepted. Spelling should not be a trap.
- A scoped search skips the half it does not need — `playlist:` costs no track query.
- **The cap on playlists is lifted when playlists are all that was asked for** (5 → 50). The
  cap exists so playlists cannot crowd out tracks; with no tracks to crowd there is nothing
  to protect. Still bounded: this library has 232.
- The library enforces the scope above the sources, **per kind**, so a source answering an
  album-only search cannot slip playlists in beside them.
- `queue:` never consults a source — the tracks are already in hand, and asking a server
  about them would be slower and wrong. Enter on such a hit **jumps** to it rather than
  rebuilding the queue from the results, which would discard everything that did not match.
- The local index serves neither collections nor favourites, so it skips the database round
  trip entirely for those rather than returning a misleading empty success.
- A prefix with nothing after it is intent without a subject: the overlay says what the
  prefix does instead of searching for the empty string, which would return an arbitrary
  slice of the whole library.
- An empty scoped result says *which* kind found nothing, so the user need not retype the
  term to find out.

To add a prefix, extend `SearchQuery.Prefixes` and the `SearchScope` flags — the sources and
the overlay follow from there.

**Everything lives in one vertical stack, and every surface has a reserved slot.** Bottom
upwards: the hold dial, the message toast, the applet, the search bar, the search results,
the queue. All are
`PanelWidth` wide and left-aligned, separated by `OverlayPositioner.SideGap`.

The two toast rows are **reserved permanently** — `PositionApplet` lifts a bottom-anchored
overlay by `ReservedForToasts` whether or not a toast is showing. Making room on demand
would be worse: toasts arrive unbidden, on a track change or a volume key pressed inside
another app, and a panel that jumped upwards mid-interaction would move the thing the user
was reading. A fixed slot for everything is worth the pixels.

- Rows are a **fixed `ToastRowHeight`**, so a toast gaining or losing its level bar cannot
  resize the window and shift the stack.
- Only bottom anchors need the lift. Anywhere else there is already room below, so the
  overlay stays where the anchor puts it and the rows follow beneath it.
- `ToastRowHeight` appears twice — the resource in `App.xaml` sizes the window, the constant
  in `OverlayPositioner` spaces the rows. They must match.
- This replaced an attempt to sit the dial *beside* the applet as a square. It worked and was
  verified, but two shapes for one surface meant switching `SizeToContent` at runtime, and a
  square is a poor container for a long message. One stack, fixed slots, is simpler to reason
  about and never moves.

**The search bar is its own row, open for as long as the overlay is.** It used to share the
status line inside the applet, appearing on `Ctrl+Alt+S` and taking the status away while it
was there. Now the box is simply always present directly above the applet, the status line
keeps its place, and `OpenSearch` only moves the caret — it reveals nothing.

- **The lens button is gone.** Its only job was revealing the box, and that job no longer
  exists — clicking the box focuses it, `Escape` clears it, and typing anywhere in the overlay
  drops into it. A lens icon hinting "you can search" is also weaker than a search box with
  placeholder text in it. The row keeps a 🔍 as decoration.
- **"Is the user typing" is a focus question now, not a visibility one.** `HandleTransportKey`
  and `PreviewTextInput` test `IsKeyboardFocusWithin`; testing visibility would be permanently
  true and Space would never reach play/pause again.
- `Escape` decides on the box's *contents*: a search in progress is cleared, an already-empty
  box dismisses the overlay.
- `CloseSearch` puts the results away and empties the box but leaves the bar — it is part of
  the overlay, not something that was opened.

**A Popup does not follow its window.** It is placed when it opens and never again, so moving
the overlay — a changed anchor, a different monitor — strands every panel where the overlay
used to be. `MainWindow.LocationChanged` calls `ReplacePanels`, which nudges each open popup's
offset and puts it back: changing a placement property is the only lever WPF offers to make a
popup reconsider. Found by measuring, not by reasoning — the panels looked right in every
test until the window was moved after they opened.

**The search and queue panels stack above the applet.** Both are the same width as the
overlay (`PanelWidth`), carry the same background, border and corner radius, and take the
same background tint — they are meant to read as extensions of the applet, not as separate
popups. Search occupies the slot immediately above it; the queue sits above search when both
are open, and drops against the applet when search closes.

- **`Placement="Relative"`, not `Top`.** `PlacementMode.Top` is the obvious choice and the
  wrong one: WPF **flips it below the target** when there is no room above. Measured with the
  overlay near the top of a screen, both panels landed *underneath* the applet, overlapping
  it. Relative honours the offset it is given. Running off the top of a small screen is the
  lesser fault, and needs a top anchor plus a long queue to reach.
- `UpdatePanelStack` accumulates outward from the applet over an ordered list of panels, so
  each one clears everything between it and the applet, and a closed panel leaves no hole.
  Offsets come from each panel's **measured** height, because they grow and shrink with their
  contents — a search panel showing one result and one showing ten
  push the queue up by different amounts. Each panel's `SizeChanged` re-runs it, as do the
  popups' `Opened`/`Closed`.
- Offsets are negative: `Relative` measures down from the target's top-left, and the whole
  stack goes above it.
- **A panel is measured before it is placed, not after.** A Popup measures its child only
  once it opens, so a first placement computed from a height of zero gets corrected a moment
  later — and repositioning an already-open popup can dismiss it. `PanelHeight` measures
  explicitly when `ActualHeight` is still zero.
- If there is not room above — a top-anchored overlay has none — the stack mirrors *below*
  the applet rather than running off the screen. The order is preserved either way: search
  nearest the applet, the queue beyond it. Verified across window positions from y=0 to
  y=1258; the panels stay on screen and open at every one.
- Verified on screen rather than by offset alone. The offset-only version of the test passed
  while the panels were rendering in the wrong place, because the flip happens below that
  layer.

**Testing these panels needs a shown, anchored and *activated* window.** They are
`StaysOpen="False"` popups, which Windows dismisses the moment the owning app stops being
active — and a test window among a dozen others is not active by default. A test that only
calls `Show()` sees the panel close immediately, which looks like a placement bug and is not
one. Leaving the window at WPF's cascade position is the other trap: it moves between runs
and decides whether the stack goes above or below. `ShowAndActivate` does both.

**The queue keeps your place and your focus across a refresh.** `RefreshQueue` rebuilds the
whole entry collection, which destroys the focused `ListBoxItem` — and WPF does not reinstate
focus when the focused element leaves the tree, so the popup stayed open while keystrokes
went nowhere. Two rules follow, and both are load-bearing:

- **Focus is captured before the rebuild and restored after**, deferred to `Input` priority
  because the containers are only realised during layout.
- **The user's selection wins over the playhead.** Snapping back to the playing track on
  every refresh was actively hostile after a removal: the highlight landed on the one entry
  `RemoveAt` refuses, so the next press did nothing. Only a selection that no longer exists
  falls back, and then to where the removed row was — so a run of removals keeps working.
  Selecting the playing track is an *open*-time concern, which is why `ShowQueue` clears the
  selection first to signal that case.

Tests around this must pump the dispatcher: `QueueChanged` refreshes through
`BeginInvoke`, so reading the list straight after a change sees the state from before it.

**Moving a queue entry.** `Ctrl+Alt+G` picks up the highlighted entry; the arrows then move
it, `Enter` confirms, `Escape` puts it back where it was picked up. The held row grows and
gains an accent rule, so the mode is visible without a caption.

- **A global hotkey, not a key the list handles.** The queue lives in a `Popup` with its own
  visual tree, and the arrows there are already navigation — the grab has to come from
  outside that conversation. Same reasoning as the other queue hotkeys.
- While an entry is held the list handler swallows every other key, so a stray `Delete`
  cannot act on a queue mid-rearrangement.
- `PlaybackService.MoveTo` edits `_order` only, for the reason `RemoveAt` gives: `_queue`
  holds the tracks and `_order` is the arrangement.
- **The pointer follows the track, not the index.** Moving the playing entry, or moving
  something past it, adjusts `_orderPosition` so playback is never silently reassigned to a
  different song. Unlike `RemoveAt`, moving the playing track *is* allowed — it changes what
  comes next, not the audio in flight.
- The prebuffer restarts only when `PeekNext` actually changes, so stepping an entry one row
  at a time does not reopen a connection per keypress.
- Closing the queue commits rather than reverts: the moves are already applied, and undoing
  them behind a closed panel would be a surprise.

**Collections: playlists and albums.** Both appear in search alongside tracks. `Enter` plays
one, `Ctrl+Alt+Pipe` queues all of it, and holding that plays all of it next.

- **One type, `TrackCollection`, for both.** They are the same thing structurally: found by
  name, expanded into tracks, played or queued. A separate `Album` class would duplicate the
  expansion path, the row template, and every queue action for a difference that is only a
  word on screen. `CollectionKind` carries that word.
- `ISupportsCollections` is optional, like `ISupportsFavorites` — the local index has no such
  concept and should not have to pretend.
- A `TrackCollection` is **not** a `Track`. It has no duration and cannot be handed to the
  audio engine, so modelling it as one would be a null-check away from trying to play it. It
  does share property names (`Title`, `DisplayArtist`, `Source`) so one row template renders
  either without a selector.
- An album row leads with its artist, a playlist row with its size — an artist is what tells
  two same-named albums apart, and a playlist has no single one.
- Albums expand via `parentId`, playlists via `/Playlists/{id}/Items`. Only the call differs.
- Albums are listed **before** playlists: on this server every album is also imported as a
  playlist, so the album row is the one worth seeing.
- Collections ride along on `SearchResult` rather than needing a second round trip, and the
  Jellyfin source fetches all three kinds in parallel.
- **Capped at five per search.** A library that imports every album as a playlist has
  hundreds; unbounded, they bury the track matches. Verified: 232 on the live server.
- Expansion preserves playlist order — no `sortBy` on the request, since the order the
  playlist was built in is the whole point. Verified against the server.
- Non-audio items are filtered out; a playlist can hold things that cannot be queued.
- Playing a row only queues the *tracks* from the results. A playlist row cannot be played by
  Next, and leaving it in the queue would make a skip appear to do nothing.
- Inserting a playlist "next" walks it backwards, because each insert lands directly after
  the current track.

**Source abstraction.** `IMusicSource` covers search, playback-URI resolution, and artwork
resolution; `MusicLibrary` aggregates every registered source and is the only thing the UI
talks to. Backend DTOs stop at the source boundary — `JellyfinMediaItem` must never reach
`MainWindow`. Adding Navidrome or Plex should be a new class plus one `Register` call in
`App.BuildServices`, nothing more. Capabilities that not every backend has go in optional
interfaces (`ISupportsFavorites`) rather than widening `IMusicSource`.

**State ownership.** The overlay is a *view* of playback, not its owner. Playback state
(current track, queue, shuffle, loop, position) must live in a service that outlives the
window, because the window gets hidden constantly and media keys must work while it is
hidden. `PlaybackService` owns all of it; `MainWindow` subscribes to its events and holds
no playback state of its own. Keep it that way.

**Threading.** `AudioPlayerService.ProgressUpdated` fires on a `System.Timers.Timer`
threadpool thread, and WinRT/SMTC callbacks arrive off the UI thread too. Marshal with
`Dispatcher.Invoke`/`InvokeAsync` before touching WPF elements.

**A search row must be pickable.** Rows show title, then **artist and album**, then duration
and source. The album is not decoration: measured across six realistic searches on a real
library, 14 of 45 rows had an identical twin on screen — the studio cut, the live version and
the compilation all read "Hells Bells — AC-DC". Duration does not separate them either, since
two pressings of one recording share a running time. `Track.SearchSubtitle` and
`Track.DurationText` are what the row binds; `TrackCollection` mirrors both so one template
still renders either.

**The search box teaches its own prefixes.** WPF has no placeholder, so `TxtSearchPlaceholder`
sits behind the box and hides on the first keystroke. It names the prefixes because there is
nowhere else they would ever be found — the search box is the only place they work and the
only place anyone looks. The suite checks that every prefix it advertises actually parses.

**Shortcut clashes are flagged inline, as they are chosen.** A clash disables *both*
shortcuts, so learning about it from a tray balloon after the window closed made the cause
hard to connect to the change. `HotkeyRow.Conflict` names the other action, and
`SettingsWindow` recomputes the whole set from each row's own `PropertyChanged` — driving it
from the row rather than the capture handler means no future path that sets `Keys` can leave
a stale flag behind. Recomputed wholesale because clearing a clash has to update the row that
was previously flagged too.

**Hints name the live binding, never a literal.** Every shortcut in this app is
rebindable, so a tooltip or message with `Ctrl+Alt+O` written into it is wrong the moment
someone rebinds — and wrong for everyone when a default moves, which is exactly what
happened when offline mode went to `Ctrl+Alt+L` and five user-facing strings went on naming
the old key. `MainWindow.KeyHint` reads `HotkeyConfig.For(action)`, and `ApplyShortcutHints`
re-runs on `ConfigChanged` so a rebind updates the overlay without a restart. Do not put a
key combination in markup.

**A control must not resize when its glyph changes.** The transport buttons size to their
content, and the play triangle measures 20.9px against 28.6px for every other glyph in the
row — so swapping it for the pause bars moved the centred row by nearly 8px and every button
appeared to jump. `MediaBtnStyle` carries a `MinWidth` that covers the widest glyph; a
minimum rather than a fixed width, so a wider glyph still fits instead of being clipped. The
suite measures the pairs rather than trusting the eye.

**Theme.** Catppuccin Mocha, defined once in `App.xaml` as colours, brushes, and shared
control styles. WPF's stock `CheckBox`, `ComboBox` and `TabControl` all ignore `Background`
and draw light chrome, so each is fully retemplated there — `ToggleSwitchStyle`,
`ModernComboBoxStyle`, `ModernTabControlStyle`. Use those rather than the defaults, or
controls will look pasted in from another app.

**Settings layout.** Four tabs: **Remote** (Jellyfin, offline mode), **Local** (library
folders and scanning), **General** (anchor, monitor, margins, animations, auto-hide),
**Hotkeys**. Offline mode
sits under Remote because it governs whether remote sources are consulted at all. Each tab
scrolls independently so the long shortcut list does not push the other tabs around.

**Pack URIs are assembly-qualified** — `pack://application:,,,/Yinyue;component/...`. The
bare form resolves against whatever assembly happens to be the entry point, which breaks
the moment the windows are constructed from anywhere but the app itself. Views reference them by key (`TextBrush`, `AccentBrush`, `Surface0Brush`,
`SuccessBrush`, `DangerBrush`, `WarningBrush`). **Do not add hex literals to a view** — that
turns theming into a find-and-replace job.

**Storage.** `%APPDATA%\Yinyue\` holds `config.json`, `queue.json`, `tracks.db` (SQLite
index), `art/` (shared artwork cache), and `yinyue.log`. Config and queue are separate
files on purpose: config is small and meant to be hand-editable, while the queue can run to
hundreds of entries and churns on every skip. **Never write
Jellyfin passwords to disk** — store only the returned access token, protected with DPAPI
(`ProtectedData`) on Windows / Keychain on macOS.

## Input map

**This app is keyboard-driven. Do not add features that require the mouse to use.** Mouse
support may exist as a convenience, but every action must have a keyboard route, and
anything reachable only by pointing is incomplete.

### Global hotkeys

Work anywhere, whether or not the overlay is visible. All configurable in settings and
stored in `config.json`; see `HotkeyConfig` for the defaults.

| Default | Action |
|---|---|
| `Ctrl+Alt+Space` | Show / hide the overlay |
| `Ctrl+Alt+S` | Search |
| `Ctrl+Alt+P` | Tap: play / pause. Hold: skip forward, repeatedly |
| `Ctrl+Alt+O` | Tap: restart the track. Hold: step back, repeatedly |
| `Ctrl+Alt+L` | Toggle offline mode |
| `Ctrl+Alt+R` | Cycle loop mode |
| `Ctrl+Alt+X` | Toggle shuffle |
| `Ctrl+Alt+Plus` / `Ctrl+Alt+Minus` | Volume, 5% a step |
| `Ctrl+Alt+M` | Mute / unmute |
| `Ctrl+Alt+T` | Sleep timer: cycles off and then each configured step |
| `Ctrl+Alt+F` | Shuffle all favourites |
| `Ctrl+Alt+Pipe` | Tap: queue the highlighted result at the end. Hold: play it next |
| `Ctrl+Alt+Backspace` | Tap: remove the selected queue entry. Hold 800 ms: clear the queue |
| `Ctrl+Alt+Q` | Open the queue |
| `Ctrl+Alt+G` | Pick up the highlighted queue entry to move it, or put it down |
| `Ctrl+Alt+I` | Open settings |

`Ctrl+Alt+O` and `Ctrl+Alt+P` are a deliberate pair: adjacent keys, tap for the small
action and hold to walk the queue backwards or forwards. Offline mode moved to `Ctrl+Alt+L`
to make room.

Four shortcuts carry a built-in tap/hold escalation — tap does the ordinary thing, hold
does the bigger version. `MainWindow.BeginTapOrHold` implements all of them; the specific
entry points supply the two actions and their preconditions.

`PlayPause` passes `repeats: true`, so holding it keeps firing once per hold delay instead
of once per press — hold it and the queue walks forward a track at a time. It is also the
one escalation that does **not** summon the overlay, since play/pause is used while working
in another window; the toast reports where a skip landed.

Any shortcut can be set to **hold to activate** — it then fires only after being held for
`HoldDelaySeconds` (0.2–5 s, three decimals, one setting shared by every hold gesture). Off
by default. The exception is `RemoveFromQueue`, whose hold already means "clear the queue",
so neither queue action has a toggle — their hold already means something.
`HotkeyActions.SupportsHoldToggle` is the single place that decides, and it is enforced
again at registration so a hand-edited config cannot get past it.

Bindings live in `config.json` under `Hotkeys.Bindings`, keyed by the action names in
`HotkeyActions`. That same vocabulary is the `Tag` on each settings row and the payload of
`HotkeyManager.Triggered`, so adding an action means touching one array rather than a
config class, an event, a switch, and a block of markup.

**`Ctrl+Alt+Delete` is not available to bind.** Windows reserves it for the Secure
Attention Sequence and `RegisterHotKey` refuses it with error 1409 — verified, not assumed.
That is why clearing the queue is a hold on the remove hotkey rather than its own binding.

**Changing a default needs a migration.** A config written before the change still holds
the old value, which then collides with whatever took the key over — and a collision
disables *both* shortcuts, not one. `HotkeyConfig.MigrateSupersededDefaults` moves a binding
still sitting on an abandoned default onto its replacement, and leaves anything the user
chose deliberately alone. `SupersededDefaults` is the table to extend when a default moves.

Detecting that hold means polling `GetAsyncKeyState`: every binding registers with
`MOD_NOREPEAT`, so Windows sends exactly one `WM_HOTKEY` on press and nothing on release.
The tap fires on *release* rather than on press — acting on press would mean waiting out the
hold threshold before every single removal. The hold also serves as the confirmation for an
action that cannot be undone.

The queue hotkeys are global on purpose. `WM_HOTKEY` is posted to the overlay's message
queue regardless of which window holds focus, so they work even while focus is inside a
`Popup` — where routed key events cannot reach (see the `overlay-window` skill). That makes
them the only reliable way to act on the search results list without the mouse.

Punctuation keys are written as words (`Plus`, `Minus`, `Comma`, `Pipe`) rather than symbols,
because `+` is the separator between parts — `Ctrl+Alt++` could not be parsed unambiguously.
`HotkeyBinding` maps these both ways and keeps the enum names working as input.

### Within the overlay

Applies only while the search box is **closed** — with it open, Space and the arrows belong
to the text box.

**The arrow keys are reserved for navigation** — moving through search results and the
queue, and nothing else. They previously seeked and changed volume here, which fought with
the lists and made the panel unpredictable to move around. Volume, seeking and skipping are
global shortcuts instead.

That is a rule about the *overlay*, not about bindings: a user may still assign
`Ctrl+Alt+Up` to any action, and the settings window says so rather than blocking it.

| Input | Action |
|---|---|
| Any printable character | Opens search and keeps the character |
| `track:` `playlist:` `album:` `fav:` `queue:` | Narrows the search — see *Search prefixes* |
| `Space` | Play / pause |
| Arrow keys | Navigation only |
| `Escape` | Close search, or hide the overlay |
| `Enter` | Play the highlighted (or top) search result |
| `Enter` / `Delete` in the queue | Jump to / remove an entry |
| Arrows / `Enter` / `Esc` while holding an entry | Move it · confirm · put it back |

Volume is the app's own level, independent of the Windows mixer. Saves are debounced 1 s,
since holding a hotkey walks the level in 5% steps, and the level is rounded to three
decimals so repeated steps do not drift into `config.json` as `0.6499999999999997`.

Mute is a remembered state, not just "volume 0":

- `EffectiveVolume` is what gets persisted. Saving the raw `Volume` while muted would write
  0, and the app would come back silent with nothing explaining why.
- Volume up while muted restores the previous level rather than creeping up from silence.
- Setting any audible level clears mute, however it was reached.
- Muting from an already-silent slider still unmutes to something audible.
- `ToggleMute` raises `VolumeChanged` even when the level does not move — muting at 0 is a
  no-op for the audio engine, but the state changed and the UI needs to know.

### Feedback while the overlay is hidden

`ToastWindow` is a small transient readout for changes made by global hotkeys — volume,
loop mode, shuffle. It also announces track changes as `Title — Artist`, so skipping by hotkey says what you
landed on. `App.ShowToast` is the single entry point and it shows the toast **only
while the overlay is hidden**; with the overlay open its status line already says the same
thing, and two readouts of one change is noise.

Requirements on it, all load-bearing:

- **Carries the hold dial**, in its own row. A filling arc rather than a countdown: it reads
  at a glance and does not resize the panel as digits change. The toast hosts it rather than
  the overlay because the overlay is often hidden — play/pause and restart deliberately do
  not summon it.
- **Two instances, not one.** `ToastRole` decides which row a toast occupies. A track change
  can land while a hold is in progress, and one window cannot be in two rows at once.
- **Fades rather than pops**, via `Window.Opacity`. See the note below on why that
  requires `AllowsTransparency`.
- **Never takes focus.** `WS_EX_NOACTIVATE` plus `ShowActivated="False"`. This is the whole
  reason it exists rather than briefly showing the overlay, which would steal focus from
  whatever the user is typing into.
- **Hide, never close.** Holding a volume key fires several times a second; recreating the
  window each time would churn HWNDs.
- **Repeat calls only swap the text.** They must not reset opacity, re-show, or reposition.
  Re-running the entrance on every call is what made rapid volume steps strobe. The window
  is repositioned only when the level bar appears or disappears, since that is the only
  thing that changes its height.
- **A fade-out in progress is reversible.** `_fadeGeneration` invalidates the pending
  completion handler, so a toast raised mid-fade is not hidden a moment later by the old
  animation finishing.
- **Follows the overlay's anchor**, and shares its width through the `PanelWidth` resource
  in `App.xaml`. The two sit at the same anchor, so any mismatch would be obvious.
- **Automatic advances do not toast.** `TrackChangedEventArgs.Automatic` marks a queue
  advancing on its own at the end of a track; announcing those would fire all day for
  something the user never asked for. Only deliberate changes are reported.
- **Suppressed until `_ready`.** Restoring a queue and applying the saved volume both raise
  change events during startup, and neither is something the user did.

`WindowStyling` holds the Win32 traits WPF does not expose (tool window, non-activating,
DWM rounded corners), shared by the overlay and the toast.

## Branding assets

`Common/` holds the shared logo masters (including `Logo.ai`); `win/Assets/` holds the
Windows-specific derivatives. **`Dark` and `Light` name the colour of the mark, not the
theme it belongs to** — `Dark.png` is the black 音樂 for light backgrounds, `Light.png` the
white one for dark backgrounds. Getting this backwards makes the mark invisible rather than
merely wrong, which is how it goes unnoticed.

Generated from those masters, all multi-frame (16/20/24/32/48/64/128/256):

| File | Use |
|---|---|
| `Assets/yinyue.ico` | `ApplicationIcon` — the mark on a rounded `#1E1E2E` plate, because a bare transparent glyph disappears against a matching Explorer background |
| `Assets/tray-light.ico` | Tray, dark taskbar |
| `Assets/tray-dark.ico` | Tray, light taskbar |
| `Resources/placeholder.png` | Album-art fallback |

`App.ApplyTrayIcon` picks between the two tray marks from
`HKCU\...\Themes\Personalize\SystemUsesLightTheme` and re-applies on
`SystemEvents.UserPreferenceChanged`, so the icon survives a theme switch. It requests the
exact `SystemInformation.SmallIconSize` frame rather than letting Windows rescale a larger
one — the mark is dense and downscales badly.

Regenerate the icons from `Common/` rather than editing them directly.

## Performance budget

- Process start to a first-rendered window measures **~370 ms for a bare WPF window** on this
  machine. ReadyToRun changes nothing — WPF is already precompiled as part of Windows. The
  earlier "< 200 ms" target here was written down and never measured, and WPF cannot reach it.
  It also matters less than it reads: the app is resident, so this is paid once at login, and
  summoning is `Show()` on a live window. Keep `OnStartup` free of I/O regardless; defer
  library scans, config-heavy work, and network calls.
- Idle CPU ~0%, and for the framework itself that is a measured **0.00%** of a core with a
  window open and nothing happening — the property Avalonia could not match. The 250 ms
  progress timer in `AudioPlayerService` stops whenever playback stops. It keeps running while playing with the overlay hidden, and has to: `App` feeds
  `SmtcService.UpdateTimeline` from the same event, so the OS scrubber would freeze without
  it. Tying it to overlay visibility would be wrong, not an optimisation.
- Do not add MVVM frameworks, DI containers, or reactive libraries for their own sake.
  Hand-wired construction in `App.BuildServices` is sufficient and cheaper.
- Network and disk work belongs off the startup path. `ScanLibraryAsync` and
  `ArtworkCache.PruneAsync` are both deliberately fire-and-forget after the window exists.

## Cross-platform strategy

**Decided: two native shells over one shared core.** `win/` stays .NET 8 WPF. `mac/` becomes
an AppKit menu-bar app on `net8.0-macos`. Both are shells over `core/Yinyue.Core`, and they
share everything that has no platform in it — playback, the Jellyfin client, the library,
search, persistence — plus the config schema and the server contract.

This is a revision. This section previously said the two apps would share "a config schema and
the server API contract — not code", on the reasoning that a Swift Mac app could share nothing
else. Measuring the tree changed the premise rather than the conclusion: **a third of the app
had no platform in it and moved wholesale**, so the choice was never between sharing UI and
sharing nothing. The UI is still not shared, and never will be.

Do not propose a shared UI framework. Avalonia, Electron, Tauri, and MAUI are all off the
table. Both defining features (the borderless always-on-top summon overlay and OS media-key
integration) are platform-specific interop regardless of toolkit, so a shared framework
would still need two interop layers while adding runtime cost against requirement 4.

**Avalonia was measured, not merely ruled out.** The decision was reopened when the apps were
asked to look identical on both platforms, which removes the "feel native" argument. Two bare
420×170 transparent Catppuccin panels, WPF and Avalonia 11.3, both framework-dependent single
file, five launches each:

| | WPF | Avalonia + ReadyToRun |
|---|---|---|
| Warm startup | 366 ms | 371 ms |
| Resting memory | 101 MB | 108 MB (66 MB software-rendered) |
| Installer | ~6 MB | ~14 MB |
| **Idle CPU, window hidden** | **0.00%** of a core | **~0.9%** of a core |

On startup and memory it was a wash; ReadyToRun is mandatory for Avalonia (JIT cold launch is
1.3 s). The deciding row is idle CPU. WPF sat at a true zero in every sample. Avalonia's
compositor keeps a render loop alive on every open TopLevel, so it ran at 0.9–1.3% of a core
continuously **and did not stop when the window was hidden**. Opaque changed nothing, so it is
the compositor rather than transparency; software rendering was lower but too scattered
(0.0 / 0.3 / 2.5%) to trust. Yinyue is hidden almost all day and its invariant is hide, never
close, so that cost would run from login onward. Against requirement 4 — the requirement the
owner named as the main one — that settled it: lightness over identical pixels, chosen
knowingly. The likely mitigation (close the overlay on hide and bind hotkeys/SMTC to a
separate hidden window) was identified but not measured.

What this means in practice:

- **Share the logic, port the behaviour.** Anything in Core is shared outright. Anything above
  it — the window, the input handling, the chrome — is ported by behaviour, and the two shells
  will diverge in structure. That is fine: WPF conventions on Windows, AppKit on macOS.
- **A shared core does not mean a shared look by accident.** The overlay's reserved vertical
  stack is a design decision recorded in this file, not something Core enforces. The Mac shell
  has to build it deliberately.
- **Credential storage is per-platform by design:** `DpapiSecretStore` on Windows, a Keychain
  implementation on macOS. `ISecretStore` is the seam; there is no shared secret store behind
  it, and there should not be one.
- The macOS app needs `LSUIElement = true` in `Info.plist` (menu-bar-only, no Dock icon),
  the AppKit equivalent of `ShowInTaskbar="False"` plus the tray icon.

`win/` is the reference implementation for anything above Core. Get a feature working and
proven there before porting it; `mac/` is empty and not yet started.

### Starting the macOS app

Development moves to a Mac from here. Everything below except the Core extraction was worked
out on Windows and has not been tried on macOS.

**Decided: C# over AppKit via `Microsoft.macOS`** (`net8.0-macos`), sharing `Yinyue.Core` with
the Windows app. The UI is still `NSPanel` and friends, written against AppKit; only the
platform-neutral half is shared. Swift/AppKit was the alternative and would have meant
re-deriving every behaviour in `PlaybackService` and the search pipeline from the prose in
this file.

**The extraction is done, and it was measured rather than estimated.** The earlier note here
said ~3,700 lines looked portable and that the extraction was "best done on Windows, where it
can be tested". The second half was wrong in a way worth recording: Core targets *plain*
`net8.0`, so it builds and its tests run on a Mac. The split as built:

| | Lines | |
|---|---|---|
| `core/Yinyue.Core` | 3,916 | Moved unmodified but for the three seams below. Builds on macOS, 0 warnings |
| Windows-only services | ~1,400 | Audio, SMTC, hotkeys, overlay positioning, window styling, startup, setup seed |
| WPF UI | ~5,600 | Rewritten on either path — C# bought nothing here |

The whole seam was **three types**, found by compiling rather than by reading:
`PlaybackService` held the concrete `AudioPlayerService` (now `IAudioPlayer`),
`AudioProgressEventArgs` happened to live in the WinRT file (now beside the interface), and
`ConfigService` made two `ProtectedData` calls (now `ISecretStore`). Nothing else in 3,916
lines touched Windows, and exactly one line made a platform assumption —
`LibraryIndexerService` resolving its own data folder.

**Still open, and still the first thing to find out: tap/hold detection.** See below. Four
shortcuts depend on it and Core cannot answer it.

**What the Mac shell must provide:**

- Overlay: `NSPanel` with `.nonactivatingPanel`, floating level, no title bar, carrying the
  same reserved vertical stack as Windows — hold row, toast row, applet, search bar, results,
  queue — so the two look the same by design even though they share no UI code.
- Global hotkeys: Carbon `RegisterEventHotKey` still works and needs no Accessibility
  permission. **Unverified: tap/hold detection.** Windows polls `GetAsyncKeyState`; the macOS
  equivalent (`CGEventSource.keyState`) may need Accessibility for non-modifier keys. Test this
  first — four shortcuts depend on it.
- Media keys: `MPRemoteCommandCenter` and `MPNowPlayingInfoCenter`. See `media-integration`.
- Audio: an `IAudioPlayer` over `AVPlayer`, which handles both HTTP and local files, FLAC
  included since 10.13. The interface is the specification — implement it and
  `PlaybackService` works as it does on Windows.
- Secrets: an `ISecretStore` over Keychain, replacing `DpapiSecretStore`. Never the password;
  only the Jellyfin token.
- Startup: `SMAppService` (macOS 13+), replacing the Run key.
- Hotkeys: a platform key model. `HotkeyBinding` stayed in `win/` because it parses onto WPF's
  `Key` enum; `HotkeyConfig` — the vocabulary, the defaults and the migrations — is in Core
  and is already covered by the Core suite. The Mac shell writes its own parser against the
  same strings in `config.json`.
- Distribution: a `.app`, signed and notarised (Apple Developer Program) or right-click-to-open.

**Toolchain reality:** `win/` and `tests/Yinyue.Tests` target `net8.0-windows` and will not
build on a Mac. That is expected, not a defect — but `core/` and `tests/Yinyue.Core.Tests`
build and pass on either, which is what makes Mac-side work possible at all.

## Known issues

Verified against the current tree — these are real, not speculative.

- `LibraryIndexerService.SearchAsync` uses `LIKE '%q%'`, which cannot use the declared
  indexes. Fine at the current scale; move to FTS5 when a library gets large.
- Cross-source dedup matches on normalised title and artist only. Two genuinely different
  recordings sharing both (a studio and a live cut named identically) would collapse to one
  if they appeared in different sources. Duration was excluded deliberately — metadata
  drifts by a second or two between sources, which would defeat the match far more often.
- With animations switched off, `WindowStyling.MakeOpaque` drops transparency and the
  corners fall back to DWM (`DWMWA_WINDOW_CORNER_PREFERENCE`), which is Windows 11 only —
  so on Windows 10 turning animations off also squares the overlay. With animations on
  (the default) the corners come from the root `Border` and are correct everywhere.
- A shortcut already owned by **another application** cannot be detected until registration
  is attempted, so that kind of clash still surfaces as a tray balloon after Save. Clashes
  *within* Yinyue are flagged inline as you choose them.
- Clearing the queue cannot be undone. The hold is the only guard, and the cleared state is
  persisted to `queue.json` about two seconds later.
- Restructuring `Hotkeys` into `Bindings` orphaned the older flat format. A config written
  before that change silently reverts to default shortcuts rather than migrating.
- A playlist is expanded into the queue at the moment it is played or queued, so it is a
  snapshot: later edits on the server are not reflected in a queue already built from it.
- Playlist expansion is capped at 1000 tracks, the same ceiling shuffle-favourites uses.
- Artists and genres are not searchable as entities. Jellyfin's artist records on this
  server carry junk names ("AC-DC - Discography 1975-2020 (FLAC) 88") and it returns no
  genre entities at all, so neither could be offered as a prefix that works.
- Moving a queue entry cannot be undone once confirmed, beyond moving it back by hand.
- `StartupService` writes an absolute path to the Run key, so moving or republishing the app
  leaves a stale entry. Settings detects this and prompts, but does not repair it silently.
- Shuffle-favourites caps at 1000 tracks (`MainWindow.FavoritesCap`) and holds them in
  memory. Fine for a normal favourites list; a pathological one would be truncated silently.
- Only Jellyfin implements `ISupportsFavorites`. The local index has no favourites concept,
  so the action does nothing while offline — it says so rather than showing an empty queue.
- Volume has no on-screen control — only the global hotkeys and the toast they raise. The
  arrow keys are navigation now, so there is no in-overlay route at all.
- There is no loading or buffering indicator, and there cannot be one while tracks stream —
  see the note above on WinRT reporting no buffering state. A slow start looks like nothing
  happening.
- A restored queue holds whatever metadata was saved. If a local file moved or a Jellyfin
  item was deleted, that only surfaces as a playback error when the user presses play —
  entries are not validated on restore, because doing so would mean I/O on the startup path.

## Skills

Read the matching skill before working in its area — each one carries the interop details
and API shapes that are easy to get subtly wrong.

- **`run-yinyue`** — build, launch, inspect, and cleanly kill the tray app.
- **`overlay-window`** — frameless overlay: positioning, DPI, multi-monitor, focus, hotkeys.
- **`media-integration`** — SMTC on Windows, `MPRemoteCommandCenter` on macOS.
- **`jellyfin-api`** — auth, search, direct play vs transcode, artwork, playback reporting.
