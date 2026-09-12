using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Jellyfin as an IMusicSource. Owns the Jellyfin DTOs and maps them to Track at this
    /// boundary — nothing above this class knows Jellyfin exists, which is what keeps
    /// adding Navidrome or Plex a registration change rather than a UI change.
    /// </summary>
    public class JellyfinMusicSource : IMusicSource, ISupportsFavorites, ISupportsCollections
    {
        private readonly JellyfinApiClient _api;
        private readonly HttpClient _http;
        private readonly ArtworkCache _artwork;

        public string Name => "Jellyfin";
        public TrackSource SourceKind => TrackSource.Jellyfin;

        /// <summary>Must not perform I/O — this is checked on every search.</summary>
        public bool IsAvailable => _api.IsAuthenticated;

        public JellyfinMusicSource(JellyfinApiClient api, ArtworkCache artwork, HttpClient? http = null)
        {
            _api = api;
            _artwork = artwork;
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        public async Task<SearchResult> SearchAsync(SearchQuery query, int limit, CancellationToken ct)
        {
            if (!IsAvailable)
                return SearchResult.Fail("Jellyfin is not signed in.");

            try
            {
                // Both in one pass when both are wanted: a playlist is just another kind of
                // match, and making the caller ask twice would show tracks first and
                // playlists a moment later. A scoped search skips the half it does not need.
                var tracksTask = query.Wants(SearchScope.Tracks)
                    ? _api.SearchMusicAsync(query.Term, limit, query.FavoritesOnly, ct)
                    : Task.FromResult(new List<JellyfinMediaItem>());

                // A collection-only search can afford a far larger limit: the cap exists to
                // stop collections crowding out tracks, and with no tracks to crowd there is
                // nothing to protect.
                int collectionLimit = query.Wants(SearchScope.Tracks)
                    ? CollectionSearchLimit
                    : CollectionOnlyLimit;

                var playlistsTask = query.Wants(SearchScope.Playlists)
                    ? _api.SearchPlaylistsAsync(query.Term, collectionLimit, query.FavoritesOnly, ct)
                    : Task.FromResult(new List<JellyfinMediaItem>());

                var albumsTask = query.Wants(SearchScope.Albums)
                    ? _api.SearchAlbumsAsync(query.Term, collectionLimit, query.FavoritesOnly, ct)
                    : Task.FromResult(new List<JellyfinMediaItem>());

                await Task.WhenAll(tracksTask, playlistsTask, albumsTask).ConfigureAwait(false);

                // Albums first: they are the tighter match. Every album on this server is
                // also imported as a playlist, so the album row is the one worth seeing.
                var collections = albumsTask.Result
                    .Select(i => ToCollection(i, CollectionKind.Album))
                    .Concat(playlistsTask.Result.Select(i => ToCollection(i, CollectionKind.Playlist)))
                    .ToList();

                return SearchResult.Ok(tracksTask.Result.Select(ToTrack).ToList(), collections);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return SearchResult.Fail($"Jellyfin search failed: {ex.Message}");
            }
        }

        public Task<string?> ResolvePlaybackUriAsync(Track track, CancellationToken ct)
        {
            if (!IsAvailable || string.IsNullOrEmpty(track.Id))
                return Task.FromResult<string?>(null);

            return Task.FromResult<string?>(_api.GetAudioStreamUrl(track.Id));
        }

        /// <summary>
        /// Downloads artwork once and serves it from disk thereafter. SMTC needs a file or
        /// stream rather than a URL anyway, and caching keeps the overlay instant on repeat
        /// plays instead of re-fetching on every track change.
        /// </summary>
        public async Task<string?> ResolveArtworkPathAsync(Track track, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(track.ArtworkLocator)) return null;

            string cachePath = _artwork.PathFor("jf", track.Id);

            try
            {
                if (ArtworkCache.IsUsable(cachePath))
                    return cachePath;

                byte[] bytes = await _http.GetByteArrayAsync(track.ArtworkLocator, ct).ConfigureAwait(false);
                if (bytes.Length == 0) return null;

                await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false);
                return cachePath;
            }
            catch (OperationCanceledException)
            {
                // Partial file from an abandoned download would poison the cache.
                ArtworkCache.Discard(cachePath);
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Jellyfin] Artwork fetch failed: {ex.Message}");
                ArtworkCache.Discard(cachePath);
                return null;
            }
        }

        public async Task<SearchResult> GetFavoritesAsync(int cap, CancellationToken ct)
        {
            if (!IsAvailable)
                return SearchResult.Fail("Jellyfin is not signed in.");

            try
            {
                var items = await _api.GetFavoriteTracksAsync(cap, ct).ConfigureAwait(false);
                return SearchResult.Ok(items.Select(ToTrack).ToList());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return SearchResult.Fail($"Could not load favourites: {ex.Message}");
            }
        }

        public async Task<bool> SetFavoriteAsync(Track track, bool isFavorite, CancellationToken ct)
        {
            if (!IsAvailable || string.IsNullOrEmpty(track.Id)) return false;

            bool ok = await _api.SetFavoriteAsync(track.Id, isFavorite, ct).ConfigureAwait(false);
            if (ok) track.IsFavorite = isFavorite;
            return ok;
        }

        /// <summary>
        /// How many playlists a search may return. Deliberately small: on a library that
        /// imports every album as a playlist there are hundreds, and they would otherwise
        /// bury the track matches.
        /// </summary>
        private const int CollectionSearchLimit = 5;

        /// <summary>
        /// The ceiling when playlists are all that was asked for. Still bounded — this
        /// library has 232 playlists and an unbounded list would be a wall of rows.
        /// </summary>
        private const int CollectionOnlyLimit = 50;

        public async Task<SearchResult> GetCollectionTracksAsync(
            TrackCollection collection, int cap, CancellationToken ct)
        {
            if (!IsAvailable)
                return SearchResult.Fail("Jellyfin is not signed in.");

            try
            {
                // Albums are ordinary containers addressed by parentId; playlists have their
                // own route. Same shape out, so only the call differs.
                var items = collection.Kind == CollectionKind.Album
                    ? await _api.GetAlbumItemsAsync(collection.Id, cap, ct).ConfigureAwait(false)
                    : await _api.GetPlaylistItemsAsync(collection.Id, cap, ct).ConfigureAwait(false);

                // A collection can hold things that are not audio; those cannot be queued.
                return SearchResult.Ok(items
                    .Where(i => string.Equals(i.Type, "Audio", StringComparison.OrdinalIgnoreCase))
                    .Select(ToTrack)
                    .ToList());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return SearchResult.Fail($"Could not open \"{collection.Title}\": {ex.Message}");
            }
        }

        private static TrackCollection ToCollection(JellyfinMediaItem item, CollectionKind kind) => new()
        {
            Id = item.Id,
            Source = TrackSource.Jellyfin,
            Kind = kind,
            Title = item.Name,
            Artist = item.AlbumArtist ?? string.Empty,
            TrackCount = item.ChildCount ?? 0,
        };

        private Track ToTrack(JellyfinMediaItem item) => new()
        {
            Id = item.Id,
            Source = TrackSource.Jellyfin,
            Title = item.Name,
            Artist = item.Artists.Count > 0 ? string.Join(", ", item.Artists) : string.Empty,
            Album = item.Album ?? string.Empty,
            Duration = item.Duration,
            IsFavorite = item.UserData?.IsFavorite ?? false,
            ResumePosition = item.UserData is { PlaybackPositionTicks: > 0 } data
                ? TimeSpan.FromTicks(data.PlaybackPositionTicks)
                : TimeSpan.Zero,
            ArtworkLocator = _api.GetImageUrl(item)
        };
    }
}
