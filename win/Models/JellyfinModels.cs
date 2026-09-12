using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Yinyue.Models
{
    public class JellyfinAuthRequest
    {
        [JsonPropertyName("Username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("Pw")]
        public string Password { get; set; } = string.Empty;
    }

    public class JellyfinAuthResponse
    {
        [JsonPropertyName("AccessToken")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("User")]
        public JellyfinUser? User { get; set; }

        [JsonPropertyName("ServerId")]
        public string ServerId { get; set; } = string.Empty;
    }

    public class JellyfinUser
    {
        [JsonPropertyName("Id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;
    }

    public class JellyfinSearchResponse
    {
        [JsonPropertyName("Items")]
        public List<JellyfinMediaItem> Items { get; set; } = new();

        [JsonPropertyName("TotalRecordCount")]
        public int TotalRecordCount { get; set; }
    }

    public class JellyfinMediaItem
    {
        [JsonPropertyName("Id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("Type")]
        public string Type { get; set; } = string.Empty; // "Audio", "MusicAlbum", "MusicArtist"

        /// <summary>How many items a container holds. Only sent for playlists and albums.</summary>
        public int? ChildCount { get; set; }

        /// <summary>Credited artist of an album. Absent on playlists, which have no single one.</summary>
        public string? AlbumArtist { get; set; }

        [JsonPropertyName("Artists")]
        public List<string> Artists { get; set; } = new();

        [JsonPropertyName("Album")]
        public string? Album { get; set; }

        [JsonPropertyName("AlbumId")]
        public string? AlbumId { get; set; }

        [JsonPropertyName("RunTimeTicks")]
        public long? RunTimeTicks { get; set; }

        /// <summary>
        /// Image tags keyed by type ("Primary", "Backdrop"). Absent means the item has no
        /// such image — requesting one anyway returns 404, so always check first.
        /// Including the tag in an image URL also makes that URL cache-stable.
        /// </summary>
        [JsonPropertyName("ImageTags")]
        public Dictionary<string, string>? ImageTags { get; set; }

        /// <summary>Most tracks carry no art of their own; the album holds it.</summary>
        [JsonPropertyName("AlbumPrimaryImageTag")]
        public string? AlbumPrimaryImageTag { get; set; }

        [JsonPropertyName("UserData")]
        public JellyfinUserData? UserData { get; set; }

        [JsonPropertyName("MediaSources")]
        public List<JellyfinMediaSource>? MediaSources { get; set; }

        public TimeSpan Duration => RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(RunTimeTicks.Value)
            : TimeSpan.Zero;
    }

    public class JellyfinUserData
    {
        [JsonPropertyName("IsFavorite")]
        public bool IsFavorite { get; set; }

        [JsonPropertyName("PlaybackPositionTicks")]
        public long PlaybackPositionTicks { get; set; }

        [JsonPropertyName("Played")]
        public bool Played { get; set; }
    }

    public class JellyfinMediaSource
    {
        [JsonPropertyName("Container")]
        public string? Container { get; set; }

        [JsonPropertyName("SupportsDirectPlay")]
        public bool SupportsDirectPlay { get; set; }

        [JsonPropertyName("SupportsDirectStream")]
        public bool SupportsDirectStream { get; set; }

        [JsonPropertyName("Bitrate")]
        public int? Bitrate { get; set; }
    }

    /// <summary>Unauthenticated server identity, from /System/Info/Public.</summary>
    public class JellyfinPublicSystemInfo
    {
        [JsonPropertyName("ServerName")]
        public string ServerName { get; set; } = string.Empty;

        [JsonPropertyName("Version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("Id")]
        public string Id { get; set; } = string.Empty;
    }
}