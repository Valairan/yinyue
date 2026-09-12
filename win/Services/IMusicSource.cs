using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Outcome of a search. Distinguishes "no matches" from "source unavailable" — the
    /// overlay needs to tell those apart to decide whether to fall back to the local index.
    /// </summary>
    public class SearchResult
    {
        public IReadOnlyList<Track> Tracks { get; init; } = System.Array.Empty<Track>();

        /// <summary>
        /// Playlists and albums matching the same query. Empty for sources that have no such
        /// concept, which is why it rides along here rather than forcing a second round trip.
        /// </summary>
        public IReadOnlyList<TrackCollection> Collections { get; init; } = System.Array.Empty<TrackCollection>();

        public bool Succeeded { get; init; }
        public string? Error { get; init; }

        public static SearchResult Ok(IReadOnlyList<Track> tracks) =>
            new() { Tracks = tracks, Succeeded = true };

        public static SearchResult Ok(
            IReadOnlyList<Track> tracks, IReadOnlyList<TrackCollection> collections) =>
            new() { Tracks = tracks, Collections = collections, Succeeded = true };

        public static SearchResult Fail(string error) =>
            new() { Succeeded = false, Error = error };
    }

    /// <summary>
    /// A place music comes from. Jellyfin and the local file index both implement this;
    /// Navidrome/Subsonic and Plex are the expected future implementations.
    ///
    /// Implementations own their backend DTOs and must return only <see cref="Track"/>.
    /// </summary>
    public interface IMusicSource
    {
        /// <summary>Short name for UI and diagnostics, e.g. "Local" or "Jellyfin".</summary>
        string Name { get; }

        TrackSource SourceKind { get; }

        /// <summary>
        /// False when the source cannot serve requests right now — not configured, not
        /// authenticated, or known to be offline. Must not perform I/O.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Searches within the query's scope. A source must not return kinds the scope
        /// excludes; it may return fewer kinds than asked for if it has no such concept.
        /// </summary>
        Task<SearchResult> SearchAsync(SearchQuery query, int limit, CancellationToken ct);

        /// <summary>
        /// Resolves what the audio engine should open: an absolute file path for local
        /// tracks, an HTTP stream URL for remote ones.
        /// </summary>
        Task<string?> ResolvePlaybackUriAsync(Track track, CancellationToken ct);

        /// <summary>
        /// Resolves album art to a local file path, downloading and caching if needed.
        /// Null when the track has no artwork. Used by the overlay and by SMTC.
        /// </summary>
        Task<string?> ResolveArtworkPathAsync(Track track, CancellationToken ct);
    }

    /// <summary>
    /// Implemented by sources that keep server-side playlists or albums. Optional for the
    /// same reason favourites are: the local file index has no such concept, and a source
    /// should not have to pretend to a capability it lacks.
    /// </summary>
    public interface ISupportsCollections
    {
        /// <summary>
        /// The tracks in a playlist or album, in the collection's own order. Capped because
        /// one can be arbitrarily long and the whole thing goes into the queue.
        /// </summary>
        Task<SearchResult> GetCollectionTracksAsync(
            TrackCollection collection, int cap, CancellationToken ct);
    }

    /// <summary>
    /// Implemented by sources that can persist favourites server-side. Optional on purpose:
    /// the local file index has nowhere to put them, and a source should not have to
    /// pretend to support a capability it lacks.
    /// </summary>
    public interface ISupportsFavorites
    {
        Task<bool> SetFavoriteAsync(Track track, bool isFavorite, CancellationToken ct);

        /// <summary>Every favourited track this source knows about.</summary>
        Task<SearchResult> GetFavoritesAsync(int cap, CancellationToken ct);
    }
}
