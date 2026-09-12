using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue.Tests;

/// <summary>
/// A source that resolves nothing, so queue bookkeeping can be exercised without touching
/// an audio device or the network.
/// </summary>
public sealed class FakeSource : IMusicSource
{
    private readonly List<Track> _tracks;

    public FakeSource(string name, TrackSource kind, IEnumerable<Track> tracks)
    {
        Name = name;
        SourceKind = kind;
        _tracks = tracks.ToList();
    }

    public string Name { get; }
    public TrackSource SourceKind { get; }
    public bool IsAvailable { get; set; } = true;

    public Task<SearchResult> SearchAsync(SearchQuery query, int limit, CancellationToken ct) =>
        Task.FromResult(SearchResult.Ok(_tracks));

    public Task<string?> ResolvePlaybackUriAsync(Track track, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task<string?> ResolveArtworkPathAsync(Track track, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// A source that also carries playlists, so the routing and expansion paths can be
/// exercised without a server.
/// </summary>
public sealed class FakeCollectionSource : IMusicSource, ISupportsCollections
{
    private readonly Dictionary<string, List<Track>> _playlists = new(StringComparer.OrdinalIgnoreCase);

    public FakeCollectionSource(string name, TrackSource kind) { Name = name; SourceKind = kind; }

    public string Name { get; }
    public TrackSource SourceKind { get; }
    public bool IsAvailable { get; set; } = true;

    /// <summary>Set to make expansion report failure rather than tracks.</summary>
    public string? FailWith { get; set; }

    public void AddCollection(string id, string title, CollectionKind kind, params string[] trackIds)
    {
        _playlists[id] = trackIds.Select(t => Make.Track(t, source: SourceKind)).ToList();
        Catalogue.Add(new TrackCollection
        {
            Id = id,
            Source = SourceKind,
            Kind = kind,
            Title = title,
            TrackCount = trackIds.Length,
        });
    }

    public List<TrackCollection> Catalogue { get; } = new();

    public Task<SearchResult> SearchAsync(SearchQuery query, int limit, CancellationToken ct) =>
        Task.FromResult(SearchResult.Ok(Array.Empty<Track>(), Catalogue
            .Where(c => query.Wants(c.Kind == CollectionKind.Album
                ? SearchScope.Albums
                : SearchScope.Playlists))
            .ToList()));

    public Task<SearchResult> GetCollectionTracksAsync(TrackCollection playlist, int cap, CancellationToken ct)
    {
        if (FailWith != null) return Task.FromResult(SearchResult.Fail(FailWith));

        return Task.FromResult(_playlists.TryGetValue(playlist.Id, out var tracks)
            ? SearchResult.Ok(tracks.Take(cap).ToList())
            : SearchResult.Fail("No such playlist."));
    }

    public Task<string?> ResolvePlaybackUriAsync(Track track, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task<string?> ResolveArtworkPathAsync(Track track, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}

public static class Make
{
    public static Track Track(string id, string? title = null, string artist = "A",
        TrackSource source = TrackSource.Local) =>
        new()
        {
            Id = id,
            Source = source,
            Title = title ?? id,
            Artist = artist,
        };

    public static IEnumerable<Track> Tracks(params string[] ids) => ids.Select(id => Track(id));

    /// <summary>
    /// A library over fake sources. Offline mode is forced off so tests do not depend on
    /// whatever the developer's own config happens to say.
    /// </summary>
    public static (MusicLibrary Library, ConfigService Config) Library(params IMusicSource[] sources)
    {
        var config = new ConfigService();
        config.Current.OfflineMode = false;

        var library = new MusicLibrary(config);
        foreach (var source in sources) library.Register(source);

        return (library, config);
    }
}
