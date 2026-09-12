using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Local file library, backed by the SQLite index in LibraryIndexerService.
    /// Always available: even with no folders configured it simply returns no matches,
    /// which is what makes it a valid offline fallback for remote sources.
    /// </summary>
    public class LocalMusicSource : IMusicSource
    {
        private readonly LibraryIndexerService _indexer;
        private readonly ArtworkCache _artwork;

        public string Name => "Local";
        public TrackSource SourceKind => TrackSource.Local;
        public bool IsAvailable => true;

        public LocalMusicSource(LibraryIndexerService indexer, ArtworkCache artwork)
        {
            _indexer = indexer;
            _artwork = artwork;
        }

        public async Task<SearchResult> SearchAsync(SearchQuery query, int limit, CancellationToken ct)
        {
            try
            {
                // The local index holds plain tracks and nothing else: no collections, and
                // no favourites concept. A search for either has nothing here to offer and
                // should not cost a database round trip.
                if (!query.Wants(SearchScope.Tracks) || query.FavoritesOnly)
                    return SearchResult.Ok(Array.Empty<Track>());

                var localTracks = await _indexer.SearchAsync(query.Term, limit).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                return SearchResult.Ok(localTracks.Select(ToTrack).ToList());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return SearchResult.Fail($"Local index unavailable: {ex.Message}");
            }
        }

        public Task<string?> ResolvePlaybackUriAsync(Track track, CancellationToken ct)
        {
            // Id is the absolute file path for local tracks.
            if (string.IsNullOrWhiteSpace(track.Id) || !File.Exists(track.Id))
                return Task.FromResult<string?>(null);

            return Task.FromResult<string?>(track.Id);
        }

        public Task<string?> ResolveArtworkPathAsync(Track track, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(track.Id) || !File.Exists(track.Id))
                return Task.FromResult<string?>(null);

            return Task.Run<string?>(() =>
            {
                try
                {
                    string cachePath = _artwork.PathFor("local", track.Id);
                    if (ArtworkCache.IsUsable(cachePath))
                        return cachePath;

                    using var tagFile = TagLib.File.Create(track.Id);
                    var picture = tagFile.Tag.Pictures.FirstOrDefault();
                    if (picture?.Data?.Data == null || picture.Data.Data.Length == 0)
                        return null;

                    File.WriteAllBytes(cachePath, picture.Data.Data);
                    return cachePath;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Local] Artwork extraction failed: {ex.Message}");
                    return null;
                }
            }, ct);
        }

        private static Track ToTrack(LocalTrack local) => new()
        {
            Id = local.FilePath,
            Source = TrackSource.Local,
            Title = local.Title,
            Artist = local.Artist,
            Album = local.Album,
            Duration = TimeSpan.FromSeconds(local.DurationSeconds)
        };

    }
}
