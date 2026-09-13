# Yinyue 音樂

A lightweight, always-resident music player for Windows with a PowerToys-Run-style summon
overlay. It plays from a **Jellyfin** server or from **local files**, lives in the tray, and
stays out of the way until a global hotkey brings it up.

It is built to be driven entirely from the keyboard. Every action has a shortcut; nothing is
reachable only by pointing.

```
┌──────────────────────────────────┐
│  queue                           │   ← Ctrl+Alt+Q
├──────────────────────────────────┤
│  search results                  │
├──────────────────────────────────┤
│  🔍  Search…  try album: fav:    │   ← always there
├──────────────────────────────────┤
│  ♪  Hells Bells — AC-DC          │
│     ▸ ──────●────────  02:41     │   ← the overlay, Ctrl+Alt+Space
│     ⏮  ▶  ⏭   🔀 🔁 ❤           │
├──────────────────────────────────┤
│  ♪  Now playing / volume / loop  │   ← toast
├──────────────────────────────────┤
│  ◔  Keep holding to clear queue  │   ← hold dial
└──────────────────────────────────┘
```

Every surface has a reserved slot in one vertical stack, so nothing overlaps and nothing
moves when a toast arrives.

## Requirements

- Windows 10 version 1904 or later
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- Optionally a [Jellyfin](https://jellyfin.org) server; local files alone work fine

## Running it

```bash
dotnet build win/Yinyue.csproj
dotnet run  --project win/Yinyue.csproj
```

Yinyue is a **tray application with no main window**. A successful launch looks like nothing
happening: no console output, no window, an icon in the tray. Press `Ctrl+Alt+Space` to
summon the overlay, or use the tray menu.

On first run, open settings with `Ctrl+Alt+I` to add a library folder or sign in to Jellyfin.

The Windows app and its Windows test suite target `net8.0-windows` and build only on Windows.
Everything that has no platform in it — playback, the Jellyfin client, the library and search,
persistence — lives in `core/Yinyue.Core`, targets plain `net8.0`, and builds anywhere. The
macOS app will live in `mac/` and will be a second shell over that same core.

For a standalone build:

```bash
dotnet publish win/Yinyue.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true
```

## Building the installer

```powershell
dotnet tool install --global wix --version "5.*"
wix extension add -g WixToolset.UI.wixext/5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2

.\installeruild.ps1 -Version 0.1.0
```

This produces a per-user MSI of about 6 MB. It installs without an administrator prompt,
into `%LOCALAPPDATA%\Programs\Yinyue`, and its wizard asks where the overlay should appear,
whether to start Yinyue at sign-in, and optionally a music folder to scan. All three can be changed later in the app.

When setup finishes it offers to start Yinyue, and that first launch opens the settings window
— a fresh install has a server to sign in to and shortcuts worth checking. Upgrades and silent
installs skip both.

WiX **v5** specifically: v6 and later require accepting the Open Source Maintenance Fee
agreement before the tool will run. v5 is MS-RL licensed, which is OSI-approved, so building
Yinyue needs no proprietary or fee-bearing tooling.

The installer writes no configuration itself. It records the wizard's answers under
`HKCU\Software\Yinyue\Setup`, and the app applies them on its next launch and clears them.
Nothing of ours runs during installation, and the logic sits somewhere it can be tested.

## Keyboard

### Global shortcuts

These work anywhere, whether or not the overlay is visible. All are configurable under
**Settings → Hotkeys**.

| Shortcut | Action |
|---|---|
| `Ctrl+Alt+Space` | Show / hide the overlay |
| `Ctrl+Alt+S` | Summon and put the caret in the search box |
| `Ctrl+Alt+P` | Tap: play / pause. Hold: skip forward, repeatedly |
| `Ctrl+Alt+O` | Tap: restart the track. Hold: step back, repeatedly |
| `Ctrl+Alt+Plus` / `Ctrl+Alt+Minus` | Volume, one step per press (5% by default, set in Settings). Hold to keep stepping |
| `Ctrl+Alt+M` | Mute / unmute |
| `Ctrl+Alt+R` | Cycle loop: off → queue → track |
| `Ctrl+Alt+X` | Toggle shuffle |
| `Ctrl+Alt+F` | Shuffle all favourites |
| `Ctrl+Alt+Q` | Open the queue |
| `Ctrl+Alt+G` | Pick up the highlighted queue entry to move it |
| <code>Ctrl+Alt+&#124;</code> | Tap: queue the highlighted result. Hold: play it next |
| `Ctrl+Alt+Backspace` | Tap: remove a queue entry. Hold: clear the queue |
| `Ctrl+Alt+T` | Sleep timer |
| `Ctrl+Alt+L` | Toggle offline mode |
| `Ctrl+Alt+I` | Open settings |

Four of these carry a **tap/hold escalation**: a tap does the small thing, a hold does the
bigger version and shows a filling dial while you hold it. Any other shortcut can be set to
require a hold before it fires.

### In the overlay

Just start typing — anything printable lands in the search box.

| Key | Action |
|---|---|
| `Enter` | Play the highlighted result |
| Arrow keys | Navigate results and the queue — navigation only, never volume or seeking |
| `Space` | Play / pause, when not typing |
| `Escape` | Clear the search, or hide the overlay |
| `Enter` / `Delete` in the queue | Jump to / remove an entry |
| Arrows / `Enter` / `Esc` while holding an entry | Move it · confirm · put it back |

## Search prefixes

A prefix at the start of a search narrows what comes back:

| Prefix | Finds |
|---|---|
| `track:` `song:` | Songs only |
| `album:` | Albums only |
| `playlist:` | Playlists only |
| `fav:` | Favourites only |
| `queue:` `q:` | Searches what is already queued |

Plurals work, and so does `favorite:`. A colon anywhere but the start is just a colon, so
searching for `Alive: Remastered` does what you would expect.

Albums and playlists appear alongside tracks. `Enter` plays one,
<code>Ctrl+Alt+&#124;</code> queues the whole thing, and holding that plays it next.

## Settings

Four tabs, reachable with `Ctrl+Alt+I`:

- **Remote** — Jellyfin server and sign-in, streaming quality, offline mode
- **Local** — library folders and scanning
- **General** — where the overlay appears, which monitor, animations, auto-hide, sleep timer
- **Hotkeys** — every shortcut, with clashes flagged as you choose them

Your password is never written to disk. Only the access token Jellyfin issues in exchange is
stored, encrypted with DPAPI for your Windows account.

Configuration lives in `%APPDATA%\Yinyue\`.

## Status

The Windows app is complete and has been exercised against a live Jellyfin server.

**macOS is not started**, but the groundwork is: a third of the app — playback, the Jellyfin
client, the library, search and persistence — now lives in `core/Yinyue.Core`, targets plain
`net8.0`, and builds and tests on a Mac today. The plan is an AppKit menu-bar app as a second
shell over that core, not a cross-platform toolkit: both defining features (a borderless
always-on-top overlay, and OS media-key integration) are platform-specific interop whichever
toolkit you pick, so the UI is written twice on purpose.

Avalonia was measured as the alternative and rejected. It matched WPF on startup and memory,
but kept about 1% of a core busy even with the overlay hidden, where WPF idles at zero; for an
app that is resident all day, that was decisive. The numbers are in [CLAUDE.md](CLAUDE.md).

Known limitations are listed at the end of [CLAUDE.md](CLAUDE.md).

## Contributing

[CLAUDE.md](CLAUDE.md) is the guide to the codebase: the architecture, the conventions, and —
more usefully — the reasoning behind the decisions that are easy to get subtly wrong. Several
of them were arrived at by measuring rather than reasoning, and the notes say which.

There are two test suites, and neither is a superset of the other:

```bash
dotnet run --project tests/Yinyue.Core.Tests   # 214 checks, runs anywhere
dotnet run --project tests/Yinyue.Tests        # Windows only
```

Both exit 0 on pass. The Core suite covers what has no platform in it — the queue, play order,
shuffle, volume and mute, search prefixes, collections, persistence — and runs on macOS as
readily as on Windows. The Windows suite keeps what needs a real desktop: XAML that must parse,
panel placement, and hotkeys the OS has to actually accept.

Both are plain console runners rather than a test framework, because the Windows one needs an
STA thread, a live WPF `Application`, and real Win32 hotkey registration. It builds every
window for real, which is the only way XAML errors surface.

## Credits

Icons are [Lucide](https://lucide.dev), used under the ISC licence; the SVGs and the licence
are in `win/Assets/Icons/`. The one exception is `heart-shuffle.svg`, Yinyue's own mark for
"shuffle all favourites", made from Lucide's heart-x. The colour scheme is
[Catppuccin](https://catppuccin.com) Mocha.
