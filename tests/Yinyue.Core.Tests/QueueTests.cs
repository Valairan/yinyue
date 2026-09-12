using Yinyue.Models;
using Yinyue.Services;

namespace Yinyue.Tests;

public static class QueueTests
{
    public static async Task Run()
    {
        await Check.GroupAsync("queue — order and position", async () =>
        {
            var (playback, _) = NewPlayback("t0", "t1", "t2", "t3");

            await playback.PlayQueueAsync(Make.Tracks("t0", "t1", "t2", "t3"), 0);
            Check.Equal("starts at 0", 0, playback.CurrentOrderPosition);
            Check.Equal("four entries", 4, playback.PlayOrder.Count);

            await playback.JumpToAsync(3);
            Check.Equal("jumped", 3, playback.CurrentOrderPosition);

            await playback.JumpToAsync(99);
            Check.Equal("out-of-range jump ignored", 3, playback.CurrentOrderPosition);

            playback.Dispose();
        });

        await Check.GroupAsync("queue — removal keeps the pointer on the same track", async () =>
        {
            var (playback, _) = NewPlayback("t0", "t1", "t2", "t3");
            await playback.PlayQueueAsync(Make.Tracks("t0", "t1", "t2", "t3"), 3);

            Check.That("removing the playing entry is refused", !playback.RemoveAt(3));

            Check.That("removing an earlier entry succeeds", playback.RemoveAt(1));
            Check.Equal("position shifted down", 2, playback.CurrentOrderPosition);
            Check.Equal("still the same track", "t3", playback.PlayOrder[playback.CurrentOrderPosition].Id);

            Check.That("removing a later entry succeeds", playback.RemoveAt(0));
            Check.Equal("position shifted again", 1, playback.CurrentOrderPosition);
            Check.Equal("still the same track", "t3", playback.PlayOrder[playback.CurrentOrderPosition].Id);

            playback.Dispose();
        });

        await Check.GroupAsync("queue — enqueue versus play next", async () =>
        {
            var (playback, _) = NewPlayback("t0", "t1", "t2");
            await playback.PlayQueueAsync(Make.Tracks("t0", "t1", "t2"), 1);

            Check.Equal("enqueue lands at the end", 3, playback.Enqueue(Make.Track("LAST")));
            Check.Equal("order", "t0,t1,t2,LAST", Ids(playback));

            Check.Equal("insert-next lands after the current", 2, playback.InsertNext(Make.Track("NEXT")));
            Check.Equal("order", "t0,t1,NEXT,t2,LAST", Ids(playback));
            Check.Equal("current position untouched", 1, playback.CurrentOrderPosition);

            playback.Dispose();
        });

        await Check.GroupAsync("queue — an empty queue adopts whatever arrives", async () =>
        {
            var (playback, _) = NewPlayback();
            Check.Equal("enqueue lands at 0", 0, playback.Enqueue(Make.Track("only")));
            Check.Equal("becomes current", "only", playback.CurrentTrack?.Id);
            Check.That("but does not start playing", !playback.IsPlaying);
            playback.Dispose();

            var (other, _) = NewPlayback();
            Check.Equal("insert-next also lands at 0", 0, other.InsertNext(Make.Track("only")));
            Check.Equal("becomes current", "only", other.CurrentTrack?.Id);
            other.Dispose();

            await Task.CompletedTask;
        });

        await Check.GroupAsync("queue — shuffle", async () =>
        {
            var (playback, _) = NewPlayback("t0", "t1", "t2", "t3");
            playback.ToggleShuffle();
            await playback.PlayQueueAsync(Make.Tracks("t0", "t1", "t2", "t3"), 2);

            var order = playback.PlayOrder.Select(t => t.Id).ToList();
            Check.Equal("still four entries", 4, order.Count);
            Check.That("a permutation of the queue",
                order.OrderBy(x => x).SequenceEqual(new[] { "t0", "t1", "t2", "t3" }));
            Check.Equal("the chosen track plays first", "t2", order[0]);

            string before = playback.PlayOrder[playback.CurrentOrderPosition].Id;
            playback.ToggleShuffle();
            Check.Equal("un-shuffling keeps your place", before,
                playback.PlayOrder[playback.CurrentOrderPosition].Id);

            playback.Dispose();
        });

        await Check.GroupAsync("queue — loop modes", async () =>
        {
            var (playback, _) = NewPlayback("t0", "t1", "t2");

            Check.Equal("starts off", LoopMode.Off, playback.Loop);
            Check.Equal("then queue", LoopMode.Queue, playback.CycleLoop());
            Check.Equal("then track", LoopMode.Track, playback.CycleLoop());
            Check.Equal("then off again", LoopMode.Off, playback.CycleLoop());

            playback.CycleLoop();   // Queue
            await playback.PlayQueueAsync(Make.Tracks("t0", "t1", "t2"), 2);
            await playback.NextAsync();
            Check.Equal("queue looping wraps forward", 0, playback.CurrentOrderPosition);
            await playback.PreviousAsync();
            Check.Equal("and backward", 2, playback.CurrentOrderPosition);

            playback.CycleLoop();   // Track
            playback.CycleLoop();   // Off
            await playback.PlayQueueAsync(Make.Tracks("t0", "t1", "t2"), 2);
            await playback.NextAsync();
            Check.Equal("without looping it stops at the end", 2, playback.CurrentOrderPosition);

            int modeEvents = 0;
            playback.ModesChanged += (_, _) => modeEvents++;
            playback.CycleLoop();
            playback.ToggleShuffle();
            Check.Equal("mode changes are announced", 2, modeEvents);

            playback.Dispose();
        });

        await Check.GroupAsync("queue — peeking ahead", async () =>
        {
            var (playback, _) = NewPlayback("t0", "t1", "t2");
            await playback.PlayQueueAsync(Make.Tracks("t0", "t1", "t2"), 0);

            Check.Equal("peek sees the next entry", "t1", playback.PeekNext()?.Id);
            Check.Equal("peeking does not move", 0, playback.CurrentOrderPosition);

            await playback.JumpToAsync(2);
            Check.That("nothing to peek at the end without looping", playback.PeekNext() == null);

            playback.CycleLoop();   // Queue
            Check.Equal("queue looping wraps the peek", "t0", playback.PeekNext()?.Id);

            playback.Dispose();
        });

        await Check.GroupAsync("previous: restart versus step back", async () =>
        {
            var (playback, _) = NewPlayback("t0", "t1", "t2");
            await playback.PlayQueueAsync(Make.Tracks("t0", "t1", "t2"), 2);

            // Nothing has played, so the position is zero and the ordinary rule steps back.
            await playback.PreviousAsync();
            Check.Equal("an ordinary press steps back", 1, playback.CurrentOrderPosition);

            await playback.PreviousAsync(alwaysChangeTrack: true);
            Check.Equal("forcing a change also steps back", 0, playback.CurrentOrderPosition);

            // Without queue looping there is nowhere further back to go.
            await playback.PreviousAsync(alwaysChangeTrack: true);
            Check.Equal("and stops at the start", 0, playback.CurrentOrderPosition);

            playback.CycleLoop();   // Queue
            await playback.PreviousAsync(alwaysChangeTrack: true);
            Check.Equal("queue looping wraps to the end", 2, playback.CurrentOrderPosition);

            playback.Dispose();
        });

        Check.Group("search prefixes — parsing", () =>
        {
            var plain = SearchQuery.Parse("hells bells");
            Check.Equal("an unprefixed term is itself", "hells bells", plain.Term);
            Check.Equal("and searches everything", SearchScope.All, plain.Scope);
            Check.That("with no prefix", !plain.IsScoped);

            var songs = SearchQuery.Parse("track:hells");
            Check.Equal("track: strips the prefix", "hells", songs.Term);
            Check.Equal("and narrows to songs", SearchScope.Tracks, songs.Scope);
            Check.That("it wants tracks", songs.Wants(SearchScope.Tracks));
            Check.That("and not playlists", !songs.Wants(SearchScope.Playlists));

            var lists = SearchQuery.Parse("playlist:black");
            Check.Equal("playlist: strips too", "black", lists.Term);
            Check.Equal("and narrows to playlists", SearchScope.Playlists, lists.Scope);

            // Spelling should not be a trap.
            Check.Equal("plural works", SearchScope.Tracks, SearchQuery.Parse("tracks:x").Scope);
            Check.Equal("song works", SearchScope.Tracks, SearchQuery.Parse("song:x").Scope);
            Check.Equal("playlists works", SearchScope.Playlists, SearchQuery.Parse("playlists:x").Scope);
            Check.Equal("case is ignored", SearchScope.Tracks, SearchQuery.Parse("TRACK:x").Scope);

            // Spacing around the term is forgiven.
            Check.Equal("space after the colon", "hells", SearchQuery.Parse("track: hells").Term);
            Check.Equal("leading space", "hells", SearchQuery.Parse("   track:hells").Term);
        });

        Check.Group("search prefixes — what must NOT be a prefix", () =>
        {
            // Titles contain colons, and reinterpreting those would be worse than not trying.
            var colon = SearchQuery.Parse("Alive: Remastered");
            Check.Equal("an unknown word before a colon is just text",
                "Alive: Remastered", colon.Term);
            Check.That("and does not scope", !colon.IsScoped);

            var midway = SearchQuery.Parse("my track:thing");
            Check.Equal("a prefix must start the line", "my track:thing", midway.Term);
            Check.That("so this is unscoped", !midway.IsScoped);

            var unknown = SearchQuery.Parse("artist:bowie");
            Check.Equal("an unrecognised prefix stays literal", "artist:bowie", unknown.Term);
            Check.That("and searches everything", !unknown.IsScoped);

            var leading = SearchQuery.Parse(":thing");
            Check.Equal("a bare colon is text", ":thing", leading.Term);

            // A prefix alone is intent without a subject.
            var bare = SearchQuery.Parse("playlist:");
            Check.That("a prefix with no term is empty", bare.IsEmpty);
            Check.That("but still scoped", bare.IsScoped);
            Check.Equal("and remembers which", "playlist", bare.Prefix);

            Check.That("null is empty", SearchQuery.Parse(null).IsEmpty);
            Check.That("blank is empty", SearchQuery.Parse("   ").IsEmpty);
        });

        await Check.GroupAsync("search prefixes — narrowing the results", async () =>
        {
            var playlists = new FakeCollectionSource("Remote", TrackSource.Jellyfin);
            playlists.AddCollection("p1", "Back In Black", CollectionKind.Playlist, "hells", "shoot");

            var tracks = new FakeSource("Local", TrackSource.Local, Make.Tracks("a", "b"));
            var (library, _) = Make.Library(tracks, playlists);

            var all = await library.SearchAsync("black", 10, CancellationToken.None);
            Check.Equal("unscoped returns tracks", 2, all.Tracks.Count);
            Check.Equal("and playlists", 1, all.Collections.Count);

            var onlySongs = await library.SearchAsync("track:black", 10, CancellationToken.None);
            Check.Equal("track: keeps the tracks", 2, onlySongs.Tracks.Count);
            Check.Equal("and drops the playlists", 0, onlySongs.Collections.Count);

            var onlyLists = await library.SearchAsync("playlist:black", 10, CancellationToken.None);
            Check.Equal("playlist: keeps the playlists", 1, onlyLists.Collections.Count);
            Check.Equal("and drops the tracks", 0, onlyLists.Tracks.Count);

            // A source that ignores the scope must not be able to widen the result: the
            // library is the last word on what was asked for.
            Check.That("the narrowing is enforced above the source", onlyLists.Succeeded);
        });

        Check.Group("search prefixes — albums, favourites and the queue", () =>
        {
            var album = SearchQuery.Parse("album:black");
            Check.Equal("album: narrows to albums", SearchScope.Albums, album.Scope);
            Check.That("and excludes playlists", !album.Wants(SearchScope.Playlists));
            Check.That("and excludes tracks", !album.Wants(SearchScope.Tracks));
            Check.Equal("named for the message", "albums", album.Noun);

            // Favourites narrow by flag, not by kind — a favourite may be either.
            var fav = SearchQuery.Parse("fav:love");
            Check.That("fav: sets the flag", fav.FavoritesOnly);
            Check.Equal("but keeps every kind", SearchScope.All, fav.Scope);
            Check.Equal("term survives", "love", fav.Term);
            Check.That("both spellings work", SearchQuery.Parse("favorite:x").FavoritesOnly);
            Check.That("and the long one", SearchQuery.Parse("favourites:x").FavoritesOnly);
            Check.That("an unprefixed search is not favourites-only",
                !SearchQuery.Parse("love").FavoritesOnly);

            var queue = SearchQuery.Parse("queue:bells");
            Check.Equal("queue: targets the queue", SearchTarget.Queue, queue.Target);
            Check.Equal("and looks for tracks", SearchScope.Tracks, queue.Scope);
            Check.Equal("short form too", SearchTarget.Queue, SearchQuery.Parse("q:x").Target);
            Check.Equal("everything else targets the library",
                SearchTarget.Library, SearchQuery.Parse("album:x").Target);
        });

        Check.Group("collections — albums and playlists read differently", () =>
        {
            var album = new TrackCollection
            {
                Kind = CollectionKind.Album,
                Title = "Back In Black",
                Artist = "AC-DC",
                TrackCount = 10,
            };
            Check.Equal("an album leads with its artist", "Album · AC-DC", album.DisplayArtist);
            Check.Equal("and says what it is", "Album", album.KindNoun);

            var list = new TrackCollection
            {
                Kind = CollectionKind.Playlist,
                Title = "Road trip",
                TrackCount = 10,
            };
            Check.Equal("a playlist leads with its size", "Playlist · 10 tracks", list.DisplayArtist);

            // An album with no credited artist still says something useful.
            var anon = new TrackCollection { Kind = CollectionKind.Album, TrackCount = 3 };
            Check.Equal("falling back to the count", "Album · 3 tracks", anon.DisplayArtist);

            // Kind is part of identity: an album and a playlist can share an id.
            var a = new TrackCollection { Id = "x", Kind = CollectionKind.Album };
            var p = new TrackCollection { Id = "x", Kind = CollectionKind.Playlist };
            Check.That("kind distinguishes them", !a.Equals(p));
        });

        await Check.GroupAsync("search prefixes — album scoping end to end", async () =>
        {
            var remote = new FakeCollectionSource("Remote", TrackSource.Jellyfin);
            remote.AddCollection("al1", "Black Ice", CollectionKind.Album, "x", "y");
            remote.AddCollection("pl1", "AC-DC - Black Ice", CollectionKind.Playlist, "x", "y");

            var (library, _) = Make.Library(remote);

            var all = await library.SearchAsync("black", 10, CancellationToken.None);
            Check.Equal("unscoped sees both", 2, all.Collections.Count);

            var albums = await library.SearchAsync("album:black", 10, CancellationToken.None);
            Check.Equal("album: returns one", 1, albums.Collections.Count);
            Check.Equal("and it is the album", CollectionKind.Album, albums.Collections[0].Kind);

            var lists = await library.SearchAsync("playlist:black", 10, CancellationToken.None);
            Check.Equal("playlist: returns one", 1, lists.Collections.Count);
            Check.Equal("and it is the playlist", CollectionKind.Playlist, lists.Collections[0].Kind);

            // Expansion routes on kind, so an album must come back through the same call.
            var expanded = await library.GetCollectionTracksAsync(
                albums.Collections[0], 100, CancellationToken.None);
            Check.That("an album expands", expanded.Succeeded);
            Check.Equal("to its tracks", 2, expanded.Tracks.Count);
        });

        Check.Group("playlists — how a row describes itself", () =>
        {
            var many = new TrackCollection { Id = "p1", Title = "Back In Black", TrackCount = 10 };
            Check.Equal("plural for several", "Playlist · 10 tracks", many.DisplayArtist);

            var one = new TrackCollection { Id = "p2", Title = "Single", TrackCount = 1 };
            Check.Equal("singular for one", "Playlist · 1 track", one.DisplayArtist);

            var unknown = new TrackCollection { Id = "p3", Title = "Mystery" };
            Check.Equal("no count, no claim", "Playlist", unknown.DisplayArtist);

            // Identity is source plus id, the same rule Track uses.
            var a = new TrackCollection { Id = "x", Source = TrackSource.Jellyfin };
            var b = new TrackCollection { Id = "X", Source = TrackSource.Jellyfin };
            var c = new TrackCollection { Id = "x", Source = TrackSource.Local };
            Check.That("same id and source are equal", a.Equals(b));
            Check.That("a different source is not", !a.Equals(c));
        });

        await Check.GroupAsync("playlists — searching and expanding", async () =>
        {
            var playlists = new FakeCollectionSource("Remote", TrackSource.Jellyfin);
            playlists.AddCollection("p1", "Back In Black", CollectionKind.Playlist, "hells", "shoot", "what");

            var tracks = new FakeSource("Local", TrackSource.Local, Make.Tracks("a", "b"));
            var (library, _) = Make.Library(tracks, playlists);

            var result = await library.SearchAsync("black", 10, CancellationToken.None);
            Check.That("the search succeeded", result.Succeeded);
            Check.Equal("tracks come through", 2, result.Tracks.Count);
            Check.Equal("and so do playlists", 1, result.Collections.Count);
            Check.Equal("named", "Back In Black", result.Collections[0].Title);

            var expanded = await library.GetCollectionTracksAsync(
                result.Collections[0], 1000, CancellationToken.None);

            Check.That("expansion succeeded", expanded.Succeeded);
            Check.Equal("with every track", 3, expanded.Tracks.Count);
            Check.Equal("in playlist order, not sorted", "hells", expanded.Tracks[0].Title);
            Check.Equal("through to the end", "what", expanded.Tracks[2].Title);

            // The cap is a ceiling on what lands in the queue.
            var capped = await library.GetCollectionTracksAsync(
                result.Collections[0], 2, CancellationToken.None);
            Check.Equal("the cap is honoured", 2, capped.Tracks.Count);
        });

        await Check.GroupAsync("playlists — failures stay quiet", async () =>
        {
            var playlists = new FakeCollectionSource("Remote", TrackSource.Jellyfin);
            playlists.AddCollection("p1", "Something", CollectionKind.Playlist, "x");

            var (library, _) = Make.Library(playlists);

            // A playlist belonging to a source that cannot open one must report, not throw.
            var orphan = new TrackCollection { Id = "p1", Source = TrackSource.Local, Title = "Orphan" };
            var refused = await library.GetCollectionTracksAsync(orphan, 10, CancellationToken.None);
            Check.That("an unroutable playlist fails", !refused.Succeeded);
            Check.That("and says why", refused.Error?.Contains("cannot open playlists") == true);

            // A source that errors is reported rather than surfacing as an empty playlist,
            // which would look like success.
            playlists.FailWith = "Server said no.";
            var broken = await library.GetCollectionTracksAsync(
                playlists.Catalogue[0], 10, CancellationToken.None);
            Check.That("a failing source fails", !broken.Succeeded);
            Check.Equal("with its own message", "Server said no.", broken.Error);

            // A source with no playlist support contributes none rather than erroring.
            var plain = new FakeSource("Local", TrackSource.Local, Make.Tracks("a"));
            var (plainLibrary, _) = Make.Library(plain);
            var plainResult = await plainLibrary.SearchAsync("a", 10, CancellationToken.None);
            Check.That("a source without playlists still searches", plainResult.Succeeded);
            Check.Equal("and offers none", 0, plainResult.Collections.Count);
        });

        await Check.GroupAsync("queue — reordering", async () =>
        {
            var (playback, _) = NewPlayback("a", "b", "c", "d");
            await playback.PlayQueueAsync(Make.Tracks("a", "b", "c", "d"), 0);

            static string Titles(PlaybackService p) =>
                string.Join(",", p.PlayOrder.Select(t => t.Title));

            Check.Equal("starts in order", "a,b,c,d", Titles(playback));
            Check.Equal("playing the first", 0, playback.CurrentOrderPosition);

            // Moving the playing track is allowed: it changes what follows, not the audio.
            Check.That("the playing track can move", playback.MoveTo(0, 2));
            Check.Equal("and lands where asked", "b,c,a,d", Titles(playback));
            Check.Equal("the pointer follows it", 2, playback.CurrentOrderPosition);
            Check.Equal("still the same track", "a", playback.PlayOrder[playback.CurrentOrderPosition].Title);

            // Something behind the playhead moving ahead of it shifts the pointer back.
            Check.That("move from behind to ahead", playback.MoveTo(0, 3));
            Check.Equal("order updated", "c,a,d,b", Titles(playback));
            Check.Equal("pointer steps back", 1, playback.CurrentOrderPosition);
            Check.Equal("and still the same track", "a", playback.PlayOrder[playback.CurrentOrderPosition].Title);

            // And the reverse: something ahead moving behind shifts the pointer forward.
            Check.That("move from ahead to behind", playback.MoveTo(3, 0));
            Check.Equal("order updated again", "b,c,a,d", Titles(playback));
            Check.Equal("pointer steps forward", 2, playback.CurrentOrderPosition);
            Check.Equal("same track throughout", "a", playback.PlayOrder[playback.CurrentOrderPosition].Title);

            // Out-of-range and no-op moves report that nothing happened.
            Check.That("a move to itself changes nothing", !playback.MoveTo(1, 1));
            Check.That("a negative source is refused", !playback.MoveTo(-1, 0));
            Check.That("a source past the end is refused", !playback.MoveTo(9, 0));

            // A target past the end clamps rather than throwing.
            Check.That("a target past the end clamps", playback.MoveTo(0, 99));
            Check.Equal("to the last position", "c,a,d,b", Titles(playback));

            playback.Dispose();
        });

        await Check.GroupAsync("queue — reordering changes what plays next", async () =>
        {
            var (playback, _) = NewPlayback("a", "b", "c");
            await playback.PlayQueueAsync(Make.Tracks("a", "b", "c"), 0);

            Check.Equal("next is the second track", "b", playback.PeekNext()!.Title);

            // Pulling the third entry in front of the second changes the answer.
            playback.MoveTo(2, 1);
            Check.Equal("next follows the reorder", "c", playback.PeekNext()!.Title);

            // Moving entries behind the playhead leaves it alone.
            await playback.NextAsync();
            Check.Equal("now playing the third", "c", playback.PlayOrder[playback.CurrentOrderPosition].Title);
            Check.Equal("next is the second", "b", playback.PeekNext()!.Title);

            playback.Dispose();
        });

        await Check.GroupAsync("queue — clearing", async () =>
        {
            var (playback, _) = NewPlayback("t0", "t1");
            await playback.PlayQueueAsync(Make.Tracks("t0", "t1"), 0);

            await playback.ClearQueueAsync();
            Check.Equal("emptied", 0, playback.PlayOrder.Count);
            Check.Equal("position reset", -1, playback.CurrentOrderPosition);
            Check.That("nothing current", playback.CurrentTrack == null);

            playback.Dispose();
        });
    }

    private static string Ids(PlaybackService playback) =>
        string.Join(",", playback.PlayOrder.Select(t => t.Id));

    private static (PlaybackService Playback, ConfigService Config) NewPlayback(params string[] ids)
    {
        var (library, config) = Make.Library(
            new FakeSource("Fake", TrackSource.Local, Make.Tracks(ids)));

        return (new PlaybackService(new SilentAudioPlayer(), library), config);
    }
}

