# Jellyfin endpoint catalog

Paths are relative to the server base URL (`http://host:8096`, possibly with a base path).
Always `TrimEnd('/')` the configured URL before concatenating.

Full spec: `https://api.jellyfin.org/` — or fetch the live server contract from
`{baseUrl}/api-docs/openapi.json`, which is authoritative for the version you are hitting.

## Discovery and auth

| Method | Path | Notes |
|---|---|---|
| GET | `/System/Info/Public` | No auth. Server name, version, ID. Use to validate a URL. |
| POST | `/Users/AuthenticateByName` | Body `{"Username","Pw"}`. Returns `AccessToken`, `User`, `SessionInfo`. |
| POST | `/QuickConnect/Initiate` | Returns `Code` + `Secret`. |
| GET | `/QuickConnect/Connect?secret=` | Poll until `Authenticated: true`, then exchange. |
| POST | `/QuickConnect/Authenticate` | Body `{"Secret"}`. Returns the same shape as AuthenticateByName. |
| POST | `/Sessions/Logout` | Revokes the current token. |

## Browsing and search

| Method | Path | Notes |
|---|---|---|
| GET | `/Items?userId={uid}` | Preferred modern form. |
| GET | `/Users/{uid}/Items` | Legacy form; what the code uses today. |
| GET | `/Items/{id}` | Single item detail. |
| GET | `/Artists`, `/Artists/AlbumArtists` | Artist listings. |
| GET | `/Items/{id}/InstantMix` | Server-generated similar-tracks mix. Good "radio" feature. |
| GET | `/Playlists/{id}/Items` | Playlist contents. |
| POST | `/Playlists` | Create. Body includes `Name`, `Ids`, `UserId`, `MediaType: "Audio"`. |

Useful query parameters on the item endpoints:

| Param | Purpose |
|---|---|
| `SearchTerm` | Name match. |
| `IncludeItemTypes` | `Audio`, `MusicAlbum`, `MusicArtist`, `Playlist`. |
| `Recursive=true` | Required to search across the whole library. |
| `ParentId` | Scope to one library or album. |
| `Limit`, `StartIndex` | Paging. Response carries `TotalRecordCount`. |
| `SortBy`, `SortOrder` | e.g. `SortName`/`Ascending`, `DatePlayed`/`Descending`. |
| `Filters=IsFavorite` | Favorites only. |
| `Fields` | Comma-separated extras — see below. |

`Fields` values worth requesting: `MediaSources` (container/codec, for direct-play
decisions), `PrimaryImageAspectRatio`, `Genres`, `Path`, `CanDownload`, `UserData`
(includes `IsFavorite`, `PlaybackPositionTicks`, `Played`).

## Streaming

| Method | Path | Notes |
|---|---|---|
| GET | `/Audio/{id}/universal` | Server picks direct play or transcode. **Preferred.** |
| GET | `/Audio/{id}/stream?static=true` | Forced direct play, original bytes. |
| GET | `/Audio/{id}/stream.{container}` | Forced transcode to `container`. What the code does now. |

`universal` parameters: `UserId`, `DeviceId`, `api_key`, `MaxStreamingBitrate`,
`Container` (comma-separated list the client supports), `TranscodingContainer`,
`AudioCodec`, `EnableRemoteMedia=true`.

Containers WinRT `MediaPlayer` plays natively: `mp3, aac, m4a, flac, wav, alac`.
Gaps to list a transcode fallback for: `ogg, opus, wma`.

## Images

| Method | Path | Notes |
|---|---|---|
| GET | `/Items/{id}/Images/Primary` | Album/track art. |
| GET | `/Items/{id}/Images/Backdrop` | Artist backdrops. |

Parameters: `fillWidth`, `fillHeight`, `maxWidth`, `maxHeight`, `quality` (1-100),
`tag` (the value from `ImageTags.Primary`; including it makes the URL cache-stable).

Returns 404 when the item has no image — check `ImageTags` first.

## Playback reporting

| Method | Path | Body |
|---|---|---|
| POST | `/Sessions/Playing` | `PlaybackStartInfo`: `ItemId`, `PlaySessionId`, `CanSeek`, `IsPaused`. |
| POST | `/Sessions/Playing/Progress` | `PlaybackProgressInfo`: adds `PositionTicks`. |
| POST | `/Sessions/Playing/Stopped` | `PlaybackStopInfo`: `ItemId`, `PositionTicks`. |

Ticks are 100-nanosecond units: `TimeSpan.Ticks`. `RunTimeTicks` on items uses the same
unit — `JellyfinMediaItem.Duration` already converts correctly.

## User data

| Method | Path | Notes |
|---|---|---|
| POST | `/Users/{uid}/FavoriteItems/{id}` | Mark favorite. |
| DELETE | `/Users/{uid}/FavoriteItems/{id}` | Unmark. |
| POST | `/Users/{uid}/PlayedItems/{id}` | Mark played. |
| POST | `/Users/{uid}/Items/{id}/Rating` | Thumbs up/down. |
