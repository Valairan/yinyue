---
name: media-integration
description: Wire Yinyue into OS media keys and now-playing surfaces — Windows SMTC (SystemMediaTransportControls) and, for the macOS port, MPRemoteCommandCenter/MPNowPlayingInfoCenter. Use when working on SmtcService, media key handling, lock-screen/flyout metadata, album art thumbnails, or the playback timeline scrubber.
---

# OS media integration

Media keys must work **while the overlay is hidden** — that is the normal case, since the
window spends most of its life hidden. So media handling belongs to a long-lived playback
service, never to `MainWindow`. See the state-ownership note in CLAUDE.md.

## Windows: SystemMediaTransportControls

`Services/SmtcService.cs` already has the correct shape:
`SystemMediaTransportControlsInterop.GetForWindow(hwnd)` is the desktop-app entry point
(the UWP `SystemMediaTransportControls.GetForCurrentView()` does not apply here). It needs a
real HWND — see the `overlay-window` skill for creating one before the window is ever shown.

### Disable MediaPlayer CommandManager or you get double handling

This is the highest-value gotcha on Windows. WinRT `MediaPlayer` ships with
`CommandManager` enabled, which **automatically** responds to transport commands. Combined
with a manual `ButtonPressed` handler, a single media-key press is processed twice — the
classic symptom is play/pause that toggles and immediately toggles back, or next-track that
skips two.

Taking manual control means turning it off in `AudioPlayerService`:

```csharp
_mediaPlayer.CommandManager.IsEnabled = false;
```

Choose one owner of transport commands. Since Yinyue needs queue semantics (shuffle, loop
modes, cross-source next/previous) that `CommandManager` knows nothing about, manual SMTC
handling is the right choice.

### Required setup

The OS shows the media flyout only when `IsEnabled` is true, at least one button is
enabled, **and** `PlaybackStatus` has been set to something other than `Closed`. Leaving
`PlaybackStatus` unset is the usual reason "SMTC does nothing" despite clean init.

```csharp
_smtc.IsEnabled = true;
_smtc.IsPlayEnabled = _smtc.IsPauseEnabled = true;
_smtc.IsNextEnabled = _smtc.IsPreviousEnabled = true;
_smtc.PlaybackStatus = MediaPlaybackStatus.Closed;   // until a track loads
```

Map states honestly: `Playing`, `Paused`, `Stopped` when the queue ends, `Changing` while a
track loads, `Closed` when there is no session. `UpdatePlaybackStatus(bool)` currently
collapses everything into Playing/Paused; widen it to the real state enum when wiring the
queue.

### DisplayUpdater rules

- Set `Type = MediaPlaybackType.Music` **before** writing `MusicProperties`. Writing music
  properties while `Type` is `Unknown` silently drops them.
- `ClearAll()` also resets `Type`. If you call it between tracks, set `Type` again after.
- Nothing takes effect until `Update()`. One `Update()` after all fields are written.

### The thumbnail stream bug

`UpdateMetadataAsync` writes album art through a `DataWriter` in a `using` block. Disposing
a `DataWriter` closes the stream it wraps, so the `InMemoryRandomAccessStream` handed to
`RandomAccessStreamReference.CreateFromStream` can be dead by the time SMTC reads it —
which shows up as art that works intermittently or not at all. Detach before disposing:

```csharp
var stream = new InMemoryRandomAccessStream();
using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
{
    writer.WriteBytes(albumArtBytes);
    await writer.StoreAsync();
    await writer.FlushAsync();
    writer.DetachStream();          // <-- required
}
_updater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
```

Keep the stream referenced for as long as it is the current thumbnail.

For Jellyfin tracks the art is a URL, not bytes — fetch it once, cache it on disk under
`%APPDATA%\Yinyue\art\`, and pass the cached file to `CreateFromFile`. Do not re-download
per track change.

### Timeline (the scrubber in the Windows 11 flyout)

Not yet implemented and worth adding:

```csharp
var t = new SystemMediaTransportControlsTimelineProperties
{
    StartTime = TimeSpan.Zero,
    MinSeekTime = TimeSpan.Zero,
    Position = position,
    MaxSeekTime = duration,
    EndTime = duration
};
_smtc.UpdateTimelineProperties(t);
```

Push this **at most once per second**, not on the 250 ms progress tick — it crosses a
process boundary and is pure overhead against the idle-CPU budget. Enable
`IsPositionEnabled` / handle `PlaybackPositionChangeRequested` to accept scrubs back.

### Threading

`ButtonPressed` arrives on a WinRT threadpool thread. Route it to the playback service
directly (which should be thread-safe) and marshal to the dispatcher only for UI updates.

## macOS: MPRemoteCommandCenter + MPNowPlayingInfoCenter

For the `mac/` port. The two halves mirror SMTC: the command center receives key presses,
the now-playing info center publishes state.

```swift
let cc = MPRemoteCommandCenter.shared()
cc.playCommand.addTarget           { _ in player.play();  return .success }
cc.pauseCommand.addTarget          { _ in player.pause(); return .success }
cc.togglePlayPauseCommand.addTarget{ _ in player.toggle(); return .success }
cc.nextTrackCommand.addTarget      { _ in queue.next();   return .success }
cc.previousTrackCommand.addTarget  { _ in queue.previous(); return .success }
cc.changePlaybackPositionCommand.addTarget { event in
    guard let e = event as? MPChangePlaybackPositionCommandEvent else { return .commandFailed }
    player.seek(to: e.positionTime); return .success
}
```

Points that bite:

- Return `.success` / `.commandFailed` accurately. Returning failure makes macOS consider
  routing the key elsewhere.
- Disable commands you do not support (`cc.seekForwardCommand.isEnabled = false`) — an
  enabled-but-unhandled command leaves a dead button in Control Center.
- Populate `MPNowPlayingInfoCenter.default().nowPlayingInfo` with at least
  `MPMediaItemPropertyTitle`, `MPMediaItemPropertyArtist`,
  `MPMediaItemPropertyPlaybackDuration`, `MPNowPlayingInfoPropertyElapsedPlaybackTime`, and
  `MPNowPlayingInfoPropertyPlaybackRate`. macOS extrapolates position from rate + elapsed,
  so you only need to republish on state change or seek — not continuously.
- Set `MPNowPlayingInfoCenter.default().playbackState` explicitly; media keys route to the
  app holding the active now-playing session.
- Artwork is `MPMediaItemArtwork(boundsSize:requestHandler:)`; the handler is called with
  varying sizes, so serve from the cached image.
- A menu-bar-only app needs `LSUIElement = true` in `Info.plist` — the macOS equivalent of
  `ShowInTaskbar="False"` plus the tray icon.

## Verification

Neither platform can be verified headlessly: SMTC and Control Center both require a real
desktop session, and the OS surfaces only appear once audio is actually playing. When you
change this code, build it, then state plainly what needs manual checking — typically:
press the media keys with the overlay hidden, confirm the flyout shows correct title,
artist, and art, confirm play/pause acts exactly once per press, and confirm the state
survives track changes.
