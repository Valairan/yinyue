using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Aggregates every configured IMusicSource behind one surface. The overlay talks to
    /// this and never to an individual source, so adding a backend is a registration
    /// change rather than a UI change.
    /// </summary>
    public class MusicLibrary
    {
        private readonly List<IMusicSource> _sources = new();
        private readonly ConfigService _config;

        public MusicLibrary(ConfigService config)
        {
            _config = config;
        }

        public IReadOnlyList<IMusicSource> Sources => _sources;

        public void Register(IMusicSource source) => _sources.Add(source);

        /// <summary>
        /// Sources usable right now. Offline mode drops everything but the local index,
        /// which is the whole point of the feature.
        /// </summary>
        private IEnumerable<IMusicSource> ActiveSources() =>
            _sources.Where(s => s.IsAvailable &&
                                (!_config.Current.OfflineMode || s.SourceKind == TrackSource.Local));

        /// <summary>
        /// Searches all active sources concurrently and merges the results.
        /// Local results come first: they play instantly and never fail mid-track.
        /// </summary>
        public Task<SearchResult> SearchAsync(string query, int limit, CancellationToken ct) =>
            SearchAsync(SearchQuery.Parse(query), limit, ct);

        /// <summary>
        /// Searches every active source and merges the results.
        ///
        /// The query is parsed once here rather than by each source, so a prefix means the
        /// same thing everywhere and a source cannot quietly disagree about what was asked.
        /// </summary>
        public async Task<SearchResult> SearchAsync(SearchQuery query, int limit, CancellationToken ct)
        {
            var active = ActiveSources().ToList();
            if (active.Count == 0)
                return SearchResult.Fail("No music sources are configured. Open Settings to add a library folder or a Jellyfin server.");

            var tasks = active.Select(s => SafeSearchAsync(s, query, limit, ct)).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            var merged = new List<Track>();
            var playlists = new List<TrackCollection>();
            var errors = new List<string>();
            var seenInEarlierSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Preserve source registration order rather than completion order, so results
            // do not reshuffle between identical searches.
            foreach (var result in results)
            {
                if (!result.Succeeded)
                {
                    if (result.Error != null) errors.Add(result.Error);
                    continue;
                }

                var keysFromThisSource = new List<string>();

                foreach (var track in result.Tracks)
                {
                    // A source that ignores the scope must not widen the result behind the
                    // user's back: the prefix is the whole point of having typed it.
                    if (!query.Wants(SearchScope.Tracks)) break;

                    string key = DedupKey(track);

                    // Only suppress duplicates across sources — the same file indexed
                    // locally and held on Jellyfin. Repeats within one source are real
                    // distinct items (the same song on a single and on an album), and
                    // hiding them would misrepresent that source's catalogue.
                    if (seenInEarlierSources.Contains(key)) continue;

                    merged.Add(track);
                    keysFromThisSource.Add(key);
                }

                foreach (string key in keysFromThisSource) seenInEarlierSources.Add(key);

                // Not deduplicated: only one source has playlists today, and two sources
                // sharing a playlist name would not make them the same playlist.
                // Filtered per kind, not per source: a source that returns albums for an
                // album-only search must not also slip playlists in beside them.
                playlists.AddRange(result.Collections.Where(c => query.Wants(ScopeOf(c.Kind))));
            }

            if (merged.Count == 0 && errors.Count > 0)
                return SearchResult.Fail(string.Join("; ", errors));

            return SearchResult.Ok(merged.Take(limit).ToList(), playlists);
        }

        private static SearchScope ScopeOf(CollectionKind kind) =>
            kind == CollectionKind.Album ? SearchScope.Albums : SearchScope.Playlists;

        /// <summary>
        /// The tracks in a playlist or album, asked of the source that owns it.
        ///
        /// Routed by <see cref="TrackCollection.Source"/> rather than tried against every source,
        /// because a playlist id means nothing outside the server that issued it.
        /// </summary>
        public async Task<SearchResult> GetCollectionTracksAsync(
            TrackCollection playlist, int cap, CancellationToken ct)
        {
            var owner = ActiveSources()
                .FirstOrDefault(s => s.SourceKind == playlist.Source && s is ISupportsCollections);

            if (owner is not ISupportsCollections source)
                return SearchResult.Fail($"{playlist.Source} cannot open playlists right now.");

            try
            {
                return await source.GetCollectionTracksAsync(playlist, cap, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return SearchResult.Fail($"Could not open \"{playlist.Title}\": {ex.Message}");
            }
        }

        /// <summary>
        /// Identity for cross-source duplicate detection: title and artist with formatting
        /// stripped. Duration is deliberately excluded — the same recording routinely
        /// differs by a second or two between a local file and a server's metadata, so
        /// including it would fail to match exactly the cases this exists to catch.
        /// </summary>
        private static string DedupKey(Track track) =>
            $"{NormalizeForMatching(track.Title)}|{NormalizeForMatching(track.Artist)}";

        private static string NormalizeForMatching(string value) =>
            string.IsNullOrEmpty(value)
                ? string.Empty
                : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        private static async Task<SearchResult> SafeSearchAsync(
            IMusicSource source, SearchQuery query, int limit, CancellationToken ct)
        {
            try
            {
                return await source.SearchAsync(query, limit, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return SearchResult.Ok(Array.Empty<Track>());
            }
            catch (Exception ex)
            {
                // One broken source must not take down the whole search.
                return SearchResult.Fail($"{source.Name}: {ex.Message}");
            }
        }

        public Task<string?> ResolvePlaybackUriAsync(Track track, CancellationToken ct)
        {
            var source = SourceFor(track);
            return source == null
                ? Task.FromResult<string?>(null)
                : source.ResolvePlaybackUriAsync(track, ct);
        }

        public Task<string?> ResolveArtworkPathAsync(Track track, CancellationToken ct)
        {
            var source = SourceFor(track);
            return source == null
                ? Task.FromResult<string?>(null)
                : source.ResolveArtworkPathAsync(track, ct);
        }

        /// <summary>True when the owning source can persist favourites for this track.</summary>
        public bool SupportsFavorites(Track track) => SourceFor(track) is ISupportsFavorites;

        /// <summary>
        /// Persists a favourite where the source supports it. Returns false when the source
        /// cannot store favourites or the call failed — the caller must not update the UI
        /// on a false, or the heart will lie about server state.
        /// </summary>
        public Task<bool> TrySetFavoriteAsync(Track track, bool isFavorite, CancellationToken ct) =>
            SourceFor(track) is ISupportsFavorites favorites
                ? favorites.SetFavoriteAsync(track, isFavorite, ct)
                : Task.FromResult(false);

        /// <summary>
        /// Favourites from every source that keeps them. Respects offline mode, so this
        /// returns nothing useful while offline — which is correct, and the caller should
        /// say so rather than showing an empty list.
        /// </summary>
        public async Task<SearchResult> GetFavoritesAsync(int cap, CancellationToken ct)
        {
            var capable = ActiveSources().OfType<ISupportsFavorites>().ToList();

            if (capable.Count == 0)
            {
                return SearchResult.Fail(_config.Current.OfflineMode
                    ? "Favourites live on the server, and offline mode is on."
                    : "No source with favourites is available. Sign in to Jellyfin first.");
            }

            var merged = new List<Track>();
            var errors = new List<string>();

            foreach (var source in capable)
            {
                try
                {
                    var result = await source.GetFavoritesAsync(cap, ct).ConfigureAwait(false);
                    if (result.Succeeded) merged.AddRange(result.Tracks);
                    else if (result.Error != null) errors.Add(result.Error);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                }
            }

            if (merged.Count == 0 && errors.Count > 0)
                return SearchResult.Fail(string.Join("; ", errors));

            return SearchResult.Ok(merged);
        }

        private IMusicSource? SourceFor(Track track) =>
            _sources.FirstOrDefault(s => s.SourceKind == track.Source);
    }
}
