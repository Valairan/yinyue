# What AVFoundation decodes

Run: `swiftc -O mac/spikes/avcaps/main.swift -o /tmp/avcaps && /tmp/avcaps`
(Command Line Tools are enough — this needs no Xcode and no workload.)

`IAudioPlayer.SupportedContainers` is supplied by the engine, so the Mac engine needs its
own list. Measured on macOS 26, not assumed:

| | Windows (WinRT) | macOS (AVFoundation) |
|---|---|---|
| Shared | `mp3` `aac` `m4a` `flac` `wav` | same |
| macOS also decodes | — | `opus` `ogg` `oga` `aiff` `aif` `caf` `ac3` `eac3` `mp2` `amr` `au` `m4b` |
| Neither decodes | `wma` `ape` `wv` `dsf` `mka` `mpc` | same |

## The trap: `alac` is not a file extension

`AVURLAsset.audiovisualTypes()` reports **no** `.alac` extension, because ALAC is a codec
that lives inside an `.m4a` container. The Windows list names it anyway, and that is not a
mistake — `SupportedContainers` does double duty:

- **Local files**, where the value is matched against a file extension.
- **Jellyfin direct play**, where it is matched against the server's *container* name — and
  Jellyfin does report `alac`.

So a Mac engine that derives its list purely from `audiovisualTypes()` would compile, pass
every test, and then quietly transcode every ALAC track on the server. Keep `alac` in the
Mac list even though no file on disk will ever carry that extension.

The same asymmetry is why the macOS additions are worth taking: `opus` and `ogg` direct-play
on a Mac and transcode on Windows.
