---
name: jellyfin-api
description: Work with the Jellyfin server API in Yinyue — authentication and token storage, music search, choosing direct play vs transcoding for stream URLs, album artwork, favorites, and playback reporting. Use when editing JellyfinApiClient, JellyfinModels, or anything that talks to a Jellyfin/Emby server.
---

# Jellyfin integration

`Services/JellyfinApiClient.cs` covers auth, search, stream URL, and image URL. This skill
records the decisions that are easy to get wrong and the endpoints worth adding.
`references/endpoints.md` has the endpoint catalog.

**Keep Jellyfin types out of the UI.** Requirement 5 is multiple backends. `JellyfinMediaItem`
should never reach `MainWindow` — map it to a source-neutral track model behind the
`IMusicSource` seam described in CLAUDE.md.

## Authentication

`POST /Users/AuthenticateByName` with `{"Username": "...", "Pw": "..."}`, plus a client
identification header. The current code sends `X-Emby-Authorization`; recent Jellyfin
versions prefer the same value in a standard `Authorization` header and treat the
`X-Emby-*` form as legacy. Send `Authorization` and keep `X-Emby-Authorization` as a
fallback for older servers:

```
Authorization: MediaBrowser Client="Yinyue", Device="<machine>", DeviceId="<stable-guid>", Version="1.0.0"
```

Details that matter:

- **`DeviceId` must be stable across restarts and unique per install.** `AppConfig.DeviceId`
  holds a GUID generated on first run and persisted; `App` assigns it to the client. Jellyfin
  keys sessions off `DeviceId` — a changing one litters the server with dead sessions, and a
  colliding one makes two clients fight over a single session. The machine-name fallback in
  `ConfigureAuthorizationHeader` exists only for the window before config loads.
- After auth, send the token as `Token="..."` appended to the same `Authorization` header
  (the current `ConfigureAuthorizationHeader(token)` approach is correct).
- **Persist only the token, never the password.** Protect it with DPAPI on Windows. Tokens
  survive until revoked server-side, so there is no refresh flow to build — but do handle
  `401` by clearing the stored token and prompting for re-login.
- `AuthenticateAsync` returns a `JellyfinAuthResult` carrying a `JellyfinAuthStatus`, so
  unreachable, rejected-credentials, and protocol errors stay distinguishable. Keep it that
  way: "login failed" when the server is simply down is a bad experience in an app that has
  an offline mode.
- It probes `GET /System/Info/Public` first, unauthenticated, so an unreachable server can
  never be misreported as a bad password.
- Consider **Quick Connect** (`/QuickConnect/Initiate` then poll `/QuickConnect/Connect`) as
  the primary login path. Typing a password into a 420px overlay is unpleasant; Quick
  Connect replaces it with a six-character code approved in the Jellyfin web UI.

## Streaming: prefer direct play

`GetAudioStreamUrl` uses `/Audio/{id}/universal`, advertising the containers WinRT
MediaPlayer decodes natively (`mp3,aac,m4a,flac,alac,wav`). The server then direct-plays
anything compatible and transcodes only what we genuinely cannot decode. Verified against a
live server: a FLAC returns `206 Partial Content, Content-Type: audio/flac`.

**Do not regress this to `stream.{container}`.** That form forces a server-side transcode of
every track including lossless ones — it burns server CPU, adds start latency, and throws
away quality for no gain.

The two valid forms:

1. **`/Audio/{id}/universal`** — the server decides direct play vs transcode from the
   container/codec list you advertise. Best default: correct on odd formats, direct-plays
   everything else.
2. **`/Audio/{id}/stream?static=true`** — forced direct play, byte-for-byte original. Ideal
   when you know WinRT `MediaPlayer` handles the container (MP3, AAC/M4A, FLAC, WAV all
   work; Ogg Vorbis and Opus are the usual gaps).

`MaxStreamingBitrate` is configurable via `AppConfig.Jellyfin` and omitted entirely when
zero, which is the right default on a LAN.

Read `MediaSources[0].Container` and `SupportsDirectPlay` (request them via
`Fields=MediaSources`) if you want to make the decision client-side and show the user which
mode is active.

### The api_key query parameter

Stream URLs carry `api_key=<token>` in the query string because WinRT `MediaSource.CreateFromUri`
gives no clean way to attach headers. That is the accepted Jellyfin pattern, but it means
the token can land in server access logs and anywhere the URL is logged. Never write a
constructed stream URL to a log or error message without redacting `api_key`.

## Search

`SearchMusicAsync` uses the modern `/Items?userId=` form and requests
`fields=MediaSources,UserData,Genres`. Worth knowing:

- `/Users/{userId}/Items` is the legacy path and still works, but do not go back to it.
- `SearchTerm` matches names; it does not search lyrics or file paths.
- `IncludeItemTypes=Audio` restricts to tracks. For a richer overlay, also query
  `MusicAlbum` and `MusicArtist` and group results by type.
- Add `SortBy=SortName&SortOrder=Ascending` for stable ordering, and `StartIndex` for
  paging — `TotalRecordCount` in the response tells you whether more exist.
- The overlay searches as you type. **Debounce ~250 ms and cancel the in-flight request**
  with a `CancellationToken`; without that, a fast typist fires a request per keystroke and
  out-of-order responses make results flicker.
- `JellyfinMusicSource` wraps failures in `SearchResult.Fail`, keeping "no matches" distinct
  from "server down". `MusicLibrary` relies on that distinction to decide whether the local
  index should stand in — do not collapse it back to an empty list.

## Artwork

`/Items/{id}/Images/Primary?fillWidth=&fillHeight=` — the server resizes, so request the
size you actually display (the overlay art area is 130px wide) rather than downloading
full-resolution covers.

`GetImageUrl` does not check whether the item *has* an image; missing art returns 404. Ask
for `ImageTags` in `Fields` and fall back to the local placeholder when the tag is absent.
Cache fetched art on disk under `%APPDATA%\Yinyue\art\{itemId}.jpg` — the SMTC thumbnail
needs a file or stream anyway, and it keeps the overlay instant on repeat plays.

## Playback reporting

Implemented in `JellyfinPlaybackReporter`, which subscribes to `PlaybackService` rather than
living inside it — playback is source-neutral and must not grow Jellyfin-shaped branches.

- `POST /Sessions/Playing` on track start
- `POST /Sessions/Playing/Progress` periodically (every ~10s, and on pause/seek — not on the
  250 ms UI tick)
- `POST /Sessions/Playing/Stopped` on stop, track change, and app exit

Send these fire-and-forget: never let a reporting failure interrupt playback.

## Favorites

Implemented via the optional `ISupportsFavorites` interface — optional because the local
file index has nowhere to store favourites and should not pretend otherwise. `POST
/Users/{userId}/FavoriteItems/{itemId}` sets, `DELETE` clears, and current state arrives as
`UserData.IsFavorite` (requested through `fields=UserData`).

The overlay only repaints the heart after a confirmed round trip. A heart that lights up on
a failed request is lying about what the server stored.
