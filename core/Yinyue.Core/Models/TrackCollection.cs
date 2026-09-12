using System;

namespace Yinyue.Models
{
    /// <summary>What kind of collection a row represents. Only the wording differs.</summary>
    public enum CollectionKind
    {
        Playlist,
        Album,
    }

    /// <summary>
    /// A named, ordered group of tracks held by a source — a Jellyfin playlist or album.
    ///
    /// One type for both because they are the same thing structurally: you find them by
    /// name, expand them into tracks, and play or queue the result. Giving albums their own
    /// class would duplicate the expansion path, the row template, and every queue action
    /// for a difference that is only a word on screen.
    ///
    /// Kept separate from <see cref="Track"/> for the opposite reason: a collection has no
    /// duration and cannot be handed to the audio engine, so anything treating it as a track
    /// would be one missing check away from trying to play it.
    ///
    /// Property names match <see cref="Track"/> where they mean the same thing, so a single
    /// row template renders either without a selector.
    /// </summary>
    public class TrackCollection
    {
        /// <summary>Identifier within the owning source.</summary>
        public string Id { get; set; } = string.Empty;

        public TrackSource Source { get; set; }

        public CollectionKind Kind { get; set; }

        public string Title { get; set; } = string.Empty;

        /// <summary>Album artist. Empty for playlists, which have no single artist.</summary>
        public string Artist { get; set; } = string.Empty;

        /// <summary>What the server says it holds. Zero when the server did not report it.</summary>
        public int TrackCount { get; set; }

        public string KindNoun => Kind == CollectionKind.Album ? "Album" : "Playlist";

        /// <summary>
        /// Occupies the artist line in the search list, which is where a row says what kind
        /// of thing it is — without it a collection is indistinguishable from a track.
        ///
        /// An album leads with its artist because that is what disambiguates two albums of
        /// the same name; a playlist has no artist, so its size is the useful fact.
        /// </summary>
        public string DisplayArtist
        {
            get
            {
                string count = TrackCount > 0
                    ? $"{TrackCount} track{(TrackCount == 1 ? "" : "s")}"
                    : string.Empty;

                string detail = Kind == CollectionKind.Album && !string.IsNullOrWhiteSpace(Artist)
                    ? Artist
                    : count;

                return string.IsNullOrEmpty(detail) ? KindNoun : $"{KindNoun} · {detail}";
            }
        }

        /// <summary>
        /// Mirrors <see cref="Track.SearchSubtitle"/> so one row template renders either.
        /// A collection has nothing further to add beyond what it already says.
        /// </summary>
        public string SearchSubtitle => DisplayArtist;

        /// <summary>Mirrors <see cref="Track.DurationText"/>. A collection has no running time.</summary>
        public string DurationText => string.Empty;

        public override bool Equals(object? obj) =>
            obj is TrackCollection other && other.Source == Source && other.Kind == Kind &&
            string.Equals(other.Id, Id, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(Source, Kind, Id.ToLowerInvariant());
    }
}
