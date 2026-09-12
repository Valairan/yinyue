using System;

namespace Yinyue.Models
{
    public enum TrackSource
    {
        Local,
        Jellyfin
    }

    /// <summary>
    /// Source-neutral track. This is the only track type the UI and PlaybackService see.
    /// Backend DTOs (JellyfinMediaItem, LocalTrack) map into this at the IMusicSource
    /// boundary and must not leak past it — that seam is what makes adding Navidrome,
    /// Subsonic, or Plex cheap later.
    /// </summary>
    public class Track
    {
        /// <summary>Identifier within the owning source: a file path locally, an item id remotely.</summary>
        public string Id { get; set; } = string.Empty;

        public TrackSource Source { get; set; }

        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }

        public bool IsFavorite { get; set; }

        /// <summary>
        /// Where the server says playback stopped last time. Zero when there is nothing to
        /// resume, which is the common case for short tracks.
        /// </summary>
        public TimeSpan ResumePosition { get; set; }

        /// <summary>
        /// Source-specific artwork locator, opaque to everything but the owning
        /// IMusicSource. A remote image URL for Jellyfin; unused locally, where art is
        /// extracted from file tags instead.
        /// </summary>
        public string? ArtworkLocator { get; set; }

        /// <summary>Display string for the overlay track line.</summary>
        public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown Artist" : Artist;

        /// <summary>
        /// Artist and album together, which is what a search row needs to be pickable.
        ///
        /// Measured on a real library: with the artist alone, roughly a third of results had
        /// an identical twin on screen — the studio cut, the live version and the compilation
        /// all read "Hells Bells — AC-DC". The album separates them where duration does not,
        /// since two pressings of the same recording share a running time.
        /// </summary>
        public string SearchSubtitle => string.IsNullOrWhiteSpace(Album)
            ? DisplayArtist
            : $"{DisplayArtist} · {Album}";

        /// <summary>
        /// Running time for a list row, blank when the source did not report one. Matches the
        /// overlay's own clock so the queue and the search list do not disagree by a zero.
        /// </summary>
        public string DurationText => Duration > TimeSpan.Zero
            ? (Duration.TotalHours >= 1
                ? Duration.ToString(@"h\:mm\:ss")
                : Duration.ToString(@"mm\:ss"))
            : string.Empty;

        public override bool Equals(object? obj) =>
            obj is Track other && other.Source == Source &&
            string.Equals(other.Id, Id, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(Source, Id.ToLowerInvariant());
    }
}
