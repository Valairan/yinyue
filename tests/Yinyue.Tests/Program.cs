using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Yinyue;
using Yinyue.Models;
using Yinyue.Services;

// UseWindowsForms adds implicit global usings that collide with the WPF types of the same
// name, and ImplicitUsings does not cover System.IO here.
using System.IO;
using Application = System.Windows.Application;
using TabControl = System.Windows.Controls.TabControl;

namespace Yinyue.Tests;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        // The suite builds real windows, so it needs a WPF Application for StaticResource
        // lookups. InitializeComponent loads App.xaml without running OnStartup, which
        // would take the single-instance mutex and put an icon in the tray.
        var app = new App();
        app.InitializeComponent();

        HotkeyTests.Run();
        QueueTests.Run().GetAwaiter().GetResult();
        VolumeTests.Run();
        PersistenceTests.Run().GetAwaiter().GetResult();
        WindowTests.Run();

        return Check.Report();
    }
}

public static class HotkeyTests
{
    public static void Run()
    {
        Check.Group("hotkeys — parsing", () =>
        {
            foreach (var text in new[]
                     {
                         "Ctrl+Alt+Space", "Ctrl+Alt+S", "Ctrl+Shift+F12",
                         "Ctrl+Alt+Plus", "Ctrl+Alt+Minus", "Ctrl+Alt+Pipe",
                         "Ctrl+Alt+Backspace", "Ctrl+Alt+NumPad5", "Alt+Win+M",
                     })
            {
                Check.That($"{text} round-trips",
                    HotkeyBinding.TryParse(text, out var b) && b.ToString() == text);
            }

            Check.That("a bare key is rejected", !HotkeyBinding.TryParse("Space", out _));
            Check.That("modifiers alone are rejected", !HotkeyBinding.TryParse("Ctrl+Alt", out _));
            Check.That("nonsense is rejected", !HotkeyBinding.TryParse("Ctrl+Alt+Bananas", out _));
            Check.That("null is rejected", !HotkeyBinding.TryParse(null, out _));

            Check.That("case is ignored",
                HotkeyBinding.TryParse("ctrl+alt+space", out var lower) && lower.ToString() == "Ctrl+Alt+Space");
            Check.That("non-canonical order normalises",
                HotkeyBinding.TryParse("Win+Alt+M", out var a) &&
                HotkeyBinding.TryParse("Alt+Win+M", out var b2) && a == b2);

            HotkeyBinding.TryParse("Ctrl+Alt+Plus", out var plus);
            Check.Equal("Plus maps to VK_OEM_PLUS", 0xBBu, plus.VirtualKey);
            HotkeyBinding.TryParse("Ctrl+Alt+Backspace", out var back);
            Check.Equal("Backspace maps to VK_BACK", 0x08u, back.VirtualKey);
            Check.That("NOREPEAT is always set",
                (plus.Win32Modifiers & HotkeyBinding.MOD_NOREPEAT) != 0);

            Check.That("a malformed stored value falls back",
                HotkeyBinding.ParseOrDefault("!!", HotkeyConfig.DefaultToggleOverlay).ToString()
                    == HotkeyConfig.DefaultToggleOverlay);
        });

        Check.Group("hotkeys — configuration", () =>
        {
            var cfg = new HotkeyConfig();

            Check.That("every action has a default",
                HotkeyActions.All.All(a => !string.IsNullOrWhiteSpace(cfg.For(a).Keys)));

            var keys = HotkeyActions.All.Select(a => cfg.For(a).Keys).ToList();
            Check.Equal("no two actions share a shortcut", keys.Count, keys.Distinct().Count());

            Check.That("hold is off by default", HotkeyActions.All.All(a => !cfg.For(a).Hold));

            foreach (var action in new[]
                     {
                         HotkeyActions.AddToQueue, HotkeyActions.RemoveFromQueue, HotkeyActions.PlayPause
                     })
            {
                Check.That($"{action} withholds the generic hold toggle",
                    !HotkeyActions.SupportsHoldToggle(action));
                Check.That($"{action} explains its gesture",
                    !string.IsNullOrEmpty(HotkeyActions.HoldNote(action)));
            }

            Check.That("an absent entry falls back to the default",
                new HotkeyConfig { Bindings = new Dictionary<string, HotkeyBindingConfig>() }
                    .For(HotkeyActions.ToggleOverlay).Keys == HotkeyConfig.DefaultToggleOverlay);
        });

        Check.Group("hotkeys — hold delay", () =>
        {
            var cfg = new HotkeyConfig();
            Check.Equal("default", 0.8, cfg.HoldDelaySeconds);

            cfg.HoldDelaySeconds = 0.001;
            Check.Equal("clamped up to the floor", HotkeyConfig.MinHoldDelaySeconds, cfg.HoldDelaySeconds);

            cfg.HoldDelaySeconds = 99;
            Check.Equal("clamped down to the ceiling", HotkeyConfig.MaxHoldDelaySeconds, cfg.HoldDelaySeconds);

            cfg.HoldDelaySeconds = double.NaN;
            Check.Equal("NaN falls back", 0.8, cfg.HoldDelaySeconds);

            cfg.HoldDelaySeconds = 0.375;
            Check.Equal("millisecond precision survives", 0.375, cfg.HoldDelaySeconds);
        });

        Check.Group("hotkeys — the OS accepts every default", () =>
        {
            var window = new Window { Width = 0, Height = 0, ShowInTaskbar = false };
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();

            var manager = new HotkeyManager();
            var cfg = new HotkeyConfig();
            cfg.For(HotkeyActions.ShuffleFavorites).Hold = true;
            cfg.For(HotkeyActions.RemoveFromQueue).Hold = true;   // must be ignored

            var refused = manager.Apply(hwnd, cfg);

            foreach (var failure in refused) Console.WriteLine($"       refused: {failure}");
            Check.That("nothing refused", refused.Count == 0);
            Check.Equal("all registered", HotkeyActions.All.Length, manager.Active.Count);

            var byAction = manager.Active.Values.ToDictionary(r => r.Action);
            Check.That("an enabled hold is honoured", byAction[HotkeyActions.ShuffleFavorites].RequiresHold);
            Check.That("a withheld hold stays off", !byAction[HotkeyActions.RemoveFromQueue].RequiresHold);

            Check.That("arrows remain bindable",
                HotkeyBinding.TryParse("Ctrl+Alt+Up", out _));

            manager.UnregisterAll();
            window.Close();
        });

        Check.Group("restart / previous", () =>
        {
            var cfg = new HotkeyConfig();

            Check.That("the action exists", HotkeyActions.All.Contains(HotkeyActions.RestartOrPrevious));
            Check.Equal("bound to Ctrl+Alt+O", "Ctrl+Alt+O", cfg.For(HotkeyActions.RestartOrPrevious).Keys);
            Check.Equal("offline mode moved aside", "Ctrl+Alt+L", cfg.For(HotkeyActions.OfflineMode).Keys);

            Check.That("its hold is spoken for",
                !HotkeyActions.SupportsHoldToggle(HotkeyActions.RestartOrPrevious));
            Check.That("and it says so",
                HotkeyActions.HoldNote(HotkeyActions.RestartOrPrevious)?.Contains("steps back") == true);

            var keys = HotkeyActions.All.Select(a => cfg.For(a).Keys).ToList();
            Check.Equal("still no collisions", keys.Count, keys.Distinct().Count());

            // A config written before Ctrl+Alt+O changed hands still holds the old value,
            // which would collide with its new owner and disable both shortcuts.
            var stale = new HotkeyConfig();
            stale.For(HotkeyActions.OfflineMode).Keys = "Ctrl+Alt+O";
            Check.That("a superseded default is migrated", stale.MigrateSupersededDefaults());
            Check.Equal("onto its replacement", "Ctrl+Alt+L", stale.For(HotkeyActions.OfflineMode).Keys);
            Check.That("and migrating again is a no-op", !stale.MigrateSupersededDefaults());

            var chosen = new HotkeyConfig();
            chosen.For(HotkeyActions.OfflineMode).Keys = "Ctrl+Alt+J";
            Check.That("a deliberate choice is left alone", !chosen.MigrateSupersededDefaults());
            Check.Equal("untouched", "Ctrl+Alt+J", chosen.For(HotkeyActions.OfflineMode).Keys);
        });

        Check.Group("hotkeys — Ctrl+Alt+Delete cannot be taken", () =>
        {
            // Reserved by Windows for the Secure Attention Sequence. Recorded so nobody
            // proposes it again.
            Check.That("it parses fine", HotkeyBinding.TryParse("Ctrl+Alt+Delete", out var sas));
            Check.That("but is not among the defaults",
                !HotkeyActions.All.Select(a => new HotkeyConfig().For(a).Keys).Contains("Ctrl+Alt+Delete"));
        });
    }
}

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

        return (new PlaybackService(new AudioPlayerService(), library), config);
    }
}

public static class VolumeTests
{
    public static void Run()
    {
        Check.Group("volume and mute", () =>
        {
            var (library, _) = Make.Library(new FakeSource("Fake", TrackSource.Local, Make.Tracks("t0")));
            var playback = new PlaybackService(new AudioPlayerService(), library);

            playback.Volume = 2;
            Check.Equal("clamped to the ceiling", 1.0, playback.Volume);
            playback.Volume = -1;
            Check.Equal("clamped to the floor", 0.0, playback.Volume);

            playback.Volume = 0.65;
            Check.That("mute reports muted", playback.ToggleMute());
            Check.Equal("engine silenced", 0.0, playback.Volume);
            Check.Equal("but the level is remembered", 0.65, playback.EffectiveVolume);

            Check.That("unmute reports unmuted", !playback.ToggleMute());
            Check.Equal("restored", 0.65, playback.Volume);

            playback.Volume = 0.4;
            playback.ToggleMute();
            Check.Equal("volume up restores rather than creeping", 0.4, playback.AdjustVolume(0.05));
            Check.That("and clears mute", !playback.IsMuted);

            playback.Volume = 0.5;
            playback.ToggleMute();
            playback.Volume = 0.3;
            Check.That("any audible level clears mute", !playback.IsMuted);

            playback.Volume = 0.0;
            playback.ToggleMute();
            playback.ToggleMute();
            Check.That("unmuting from silence gives something audible", playback.Volume > 0);

            // Repeated 0.05 steps otherwise drift to values like 0.6499999999999997.
            playback.Volume = 0.5;
            for (int i = 0; i < 3; i++) playback.AdjustVolume(0.05);
            Check.Equal("steps stay on clean values", 0.65, playback.Volume);

            playback.Dispose();
        });
    }
}

public static class PersistenceTests
{
    public static async Task Run()
    {
        await Check.GroupAsync("queue persistence", async () =>
        {
            var (library, _) = Make.Library(
                new FakeSource("Fake", TrackSource.Local, Make.Tracks("t0", "t1", "t2")));

            var playback = new PlaybackService(new AudioPlayerService(), library);
            playback.ToggleShuffle();
            playback.CycleLoop();                       // Queue
            await playback.PlayQueueAsync(Make.Tracks("t0", "t1", "t2"), 1);

            var snapshot = playback.Snapshot();
            snapshot.TrackPositionSeconds = 42.5;

            Check.Equal("captures the whole order", 3, snapshot.Tracks.Count);
            Check.That("captures shuffle", snapshot.Shuffle);
            Check.Equal("captures loop", LoopMode.Queue, snapshot.Loop);

            var restored = new PlaybackService(new AudioPlayerService(), library);
            restored.RestoreQueue(snapshot.Tracks, snapshot.Position,
                TimeSpan.FromSeconds(snapshot.TrackPositionSeconds), snapshot.Shuffle, snapshot.Loop);

            Check.That("play order is adopted verbatim",
                restored.PlayOrder.Select(t => t.Id)
                    .SequenceEqual(snapshot.Tracks.Select(t => t.Id)));
            Check.Equal("position preserved", snapshot.Position, restored.CurrentOrderPosition);
            Check.That("shuffle preserved", restored.Shuffle);
            Check.Equal("loop preserved", LoopMode.Queue, restored.Loop);
            Check.That("restoring does not start playing", !restored.IsPlaying);

            playback.Dispose();
            restored.Dispose();
        });

        Check.Group("queue store rejects what it should", () =>
        {
            var store = new QueueStore();
            string path = Path.Combine(ConfigService.AppDataFolder, "queue.json");
            string? backup = File.Exists(path) ? File.ReadAllText(path) : null;

            try
            {
                File.WriteAllText(path, "{ not json");
                Check.That("corrupt file is dropped, not thrown", store.Load() == null);

                store.SaveAsync(new QueueState { Tracks = new List<Track>(), Position = -1 })
                    .GetAwaiter().GetResult();
                Check.That("an empty queue is dropped", store.Load() == null);

                store.Delete();
                Check.That("a missing file is dropped", store.Load() == null);
            }
            finally
            {
                if (backup != null) File.WriteAllText(path, backup);
                else if (File.Exists(path)) File.Delete(path);
            }
        });

        Check.Group("track resume points", () =>
        {
            // Only a genuine mid-track abandonment should resume; the boundaries keep a
            // near-finished or barely-started track from jumping.
            var mid = Make.Track("a");
            mid.Duration = TimeSpan.FromMinutes(5);
            mid.ResumePosition = TimeSpan.FromMinutes(2);
            Check.That("a mid-track position is kept", mid.ResumePosition > TimeSpan.Zero);

            var nearEnd = Make.Track("b");
            nearEnd.Duration = TimeSpan.FromSeconds(100);
            nearEnd.ResumePosition = TimeSpan.FromSeconds(95);
            Check.That("a near-finished track has a position to ignore",
                nearEnd.ResumePosition >= nearEnd.Duration - TimeSpan.FromSeconds(10));
        });
    }
}

public static class WindowTests
{
    public static void Run()
    {
        Check.Group("windows parse and resources resolve", () =>
        {
            foreach (var key in new[]
                     {
                         "PanelWidth", "BaseBrush", "AccentBrush", "CardStyle",
                         "ToggleSwitchStyle", "ModernComboBoxStyle", "ModernTabControlStyle",
                         "ThinProgressStyle", "KeyCaptureStyle",
                     })
            {
                Check.That($"resource '{key}'", Application.Current!.Resources.Contains(key));
            }

            var config = new ConfigService();
            var indexer = new LibraryIndexerService();
            var artwork = new ArtworkCache();
            var jellyfin = new JellyfinApiClient { DeviceId = config.Current.DeviceId };

            var library = new MusicLibrary(config);
            library.Register(new LocalMusicSource(indexer, artwork));

            var playback = new PlaybackService(new AudioPlayerService(), library);

            var settings = new SettingsWindow(config, jellyfin, indexer);
            Check.That("SettingsWindow parses", true);

            var overlay = new MainWindow(config, library, playback,
                () => new SettingsWindow(config, jellyfin, indexer));
            Check.That("MainWindow parses", true);

            var toast = new ToastWindow(config);
            Check.That("ToastWindow parses", true);

            var tabs = Descendants(settings).OfType<TabControl>().FirstOrDefault();
            var headers = tabs?.Items.OfType<TabItem>().Select(t => t.Header?.ToString()).ToList()
                          ?? new List<string?>();

            foreach (var want in new[] { "Remote", "Local", "General", "Hotkeys" })
                Check.That($"'{want}' tab present", headers.Contains(want));

            foreach (var name in new[]
                     {
                         "TxtServerUrl", "TxtUsername", "TxtPassword", "CmbQuality", "ChkPrebuffer",
                         "ChkOfflineMode", "LstFolders", "ChkScanOnStartup", "ScanProgress",
                         "CmbAnchor", "CmbMonitor", "TxtMarginX", "TxtMarginY", "ChkHideOnFocusLoss",
                         "ChkRunAtLogin", "ChkAnimations", "TxtAnimationMs", "TxtBackgroundOpacity", "ChkAutoHide", "TxtAutoHideSeconds", "TxtHoldDelay", "LstHotkeys", "TxtHotkeyStatus",
                     })
            {
                Check.That($"settings binds {name}", settings.FindName(name) != null);
            }

            int rows = (settings.FindName("LstHotkeys") as ItemsControl)?.ItemsSource
                ?.Cast<object>().Count() ?? 0;
            Check.Equal("a row per action", HotkeyActions.All.Length, rows);

            var texts = Descendants(settings).OfType<TextBlock>().Select(t => t.Text).ToList();
            Check.That("the arrow-key advisory is shown",
                texts.Any(t => t.Contains("arrow keys", StringComparison.OrdinalIgnoreCase)));

            settings.Close();
            overlay.Close();
            toast.Close();
            playback.Dispose();
        });

        Check.Group("startup registration reports honestly", () =>
        {
            // Read-only: registering would actually add the app to this machine's startup.
            Check.That("an executable path is discoverable", StartupService.ExecutablePath != null);
            Check.That("IsEnabled answers without throwing",
                StartupService.IsEnabled || !StartupService.IsEnabled);
        });

        Check.Group("fading follows the animations setting", () =>
        {
            // WPF only layers a window when AllowsTransparency is on, and an unlayered
            // window ignores Opacity outright. Turning animations off therefore has to drop
            // transparency as well, which is where the performance actually goes.
            foreach (bool animate in new[] { true, false })
            {
                var config = new ConfigService();
                config.Current.Overlay.Animations = animate;

                var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
                var toast = new ToastWindow(config);

                string mode = animate ? "on" : "off";
                Check.Equal($"overlay transparency with animations {mode}", animate, overlay.AllowsTransparency);
                Check.Equal($"toast transparency with animations {mode}", animate, toast.AllowsTransparency);

                overlay.Show();
                IntPtr hwnd = new WindowInteropHelper(overlay).Handle;
                Check.Equal($"overlay layered with animations {mode}", animate, IsLayered(hwnd));

                if (animate)
                {
                    overlay.Opacity = 0.5;
                    Check.Equal("opacity applies when animating", 0.5, overlay.Opacity);
                }

                overlay.Close();
                toast.Close();
            }
        });

        Check.Group("hold dial", () =>
        {
            var config = new ConfigService();
            config.Current.Overlay.Animations = true;

            var toast = new ToastWindow(config, ToastWindow.ToastRole.Hold);
            toast.Show("\u266A", "seed");          // realise the template

            var arc = toast.FindName("HoldArc") as System.Windows.Shapes.Path;
            var track = toast.FindName("HoldTrack") as System.Windows.Shapes.Ellipse;
            var glyph = toast.FindName("TxtGlyph") as TextBlock;
            var message = toast.FindName("TxtMessage") as TextBlock;

            Check.That("the dial exists", arc != null && track != null);
            Check.Equal("and is hidden for an ordinary toast", Visibility.Collapsed, arc!.Visibility);

            toast.ShowHoldProgress("Keep holding to clear the queue", 0.0);
            Check.Equal("the dial appears for a hold", Visibility.Visible, arc.Visibility);
            Check.Equal("the glyph steps aside", Visibility.Collapsed, glyph!.Visibility);
            Check.Equal("the label goes in the message line",
                "Keep holding to clear the queue", message!.Text);
            Check.That("an empty sweep draws nothing", arc.Data == Geometry.Empty);

            toast.ShowHoldProgress("Keep holding", 0.5);
            Check.That("a partial sweep is an arc", arc.Data is PathGeometry);

            toast.ShowHoldProgress("Keep holding", 1.0);
            Check.That("a full sweep closes into a circle", arc.Data is EllipseGeometry);

            toast.EndHoldProgress();
            Check.Equal("the dial goes away afterwards", Visibility.Collapsed, arc.Visibility);
            Check.Equal("and the glyph returns", Visibility.Visible, glyph.Visibility);

            toast.Close();
        });

        Check.Group("every surface has its own reserved row", () =>
        {
            var config = new ConfigService();
            config.Current.Overlay.Anchor = OverlayAnchor.BottomRight;

            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
            var message = new ToastWindow(config, ToastWindow.ToastRole.Message);
            var hold = new ToastWindow(config, ToastWindow.ToastRole.Hold);

            overlay.Show();
            new WindowInteropHelper(message).EnsureHandle();
            new WindowInteropHelper(hold).EnsureHandle();

            OverlayPositioner.PositionApplet(overlay, config.Current.Overlay);
            overlay.UpdateLayout();
            Pump();

            message.Show("\u266A", "Something — Someone");
            hold.ShowHoldProgress("Keep holding", 0.5);
            Pump();

            static Rect Of(Window w) => new(w.Left, w.Top, w.ActualWidth, w.ActualHeight);

            var applet = Of(overlay);
            var msgRow = Of(message);
            var holdRow = Of(hold);

            // The order the user asked for, bottom upwards: hold, message, applet.
            Check.That("the message row is below the applet", msgRow.Top >= applet.Bottom - 1);
            Check.That("and the hold row below that", holdRow.Top >= msgRow.Bottom - 1);

            // The whole reason for the rework: a hold happens while the overlay is up, and
            // the dial used to land on top of it.
            Check.That("the hold row misses the applet", !holdRow.IntersectsWith(applet));
            Check.That("the message row misses the applet", !msgRow.IntersectsWith(applet));
            Check.That("and the two rows miss each other", !holdRow.IntersectsWith(msgRow));

            Check.That("every row is applet-width",
                Math.Abs(msgRow.Width - applet.Width) < 1 && Math.Abs(holdRow.Width - applet.Width) < 1);
            Check.That("and left-aligned with it",
                Math.Abs(msgRow.X - applet.X) < 1 && Math.Abs(holdRow.X - applet.X) < 1);

            // The rows are reserved permanently, so the bottom one ends where an unlifted
            // applet would have: nothing moves when a toast appears or goes away.
            Check.That("the stack reaches the bottom of the work area",
                holdRow.Bottom <= SystemParameters.WorkArea.Bottom + 1 &&
                holdRow.Bottom > SystemParameters.WorkArea.Bottom - 60);

            // Rows keep their slot whether or not their neighbour is showing.
            var holdAlone = holdRow;
            message.HideNow();
            Pump();
            hold.ShowHoldProgress("Keep holding", 0.9);
            Pump();
            Check.Equal("the hold row does not move when the message goes",
                holdAlone.Top, Of(hold).Top);

            hold.HideNow();
            message.HideNow();
            hold.Close();
            message.Close();
            overlay.Close();
        });

        Check.Group("shortcut hints name the real binding", () =>
        {
            var config = new ConfigService();
            config.Current.Hotkeys.For(HotkeyActions.OfflineMode).Keys = "Ctrl+Alt+J";

            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);

            string offline = (overlay.FindName("BtnOfflineToggle") as System.Windows.Controls.Button)!
                .ToolTip?.ToString() ?? "";

            // The default moved from Ctrl+Alt+O to Ctrl+Alt+L once O became restart/previous,
            // and a hint written into the markup went on naming the old key.
            Check.That("it quotes the configured key", offline.Contains("Ctrl+Alt+J"));
            Check.That("not a stale default", !offline.Contains("Ctrl+Alt+O"));

            string favourites = (overlay.FindName("BtnShuffleFavorites") as System.Windows.Controls.Button)!
                .ToolTip?.ToString() ?? "";
            Check.That("favourites quotes its own binding",
                favourites.Contains(config.Current.Hotkeys.For(HotkeyActions.ShuffleFavorites).Keys));

            // Clearing a binding is not "unbound": For() substitutes the default, so the
            // hint follows it there rather than going blank.
            config.Current.Hotkeys.For(HotkeyActions.OfflineMode).Keys = "";
            var reset = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
            string back = (reset.FindName("BtnOfflineToggle") as System.Windows.Controls.Button)!
                .ToolTip?.ToString() ?? "";
            Check.That("a cleared binding falls back to the default",
                back.Contains(HotkeyConfig.DefaultOfflineMode));

            reset.Close();
            overlay.Close();
        });

        Check.Group("queue keeps your place while removing", () =>
        {
            var playback = FadePlayback();

            // RestoreQueue builds the queue without starting playback, which is all these
            // checks need and keeps them off the audio device.
            playback.RestoreQueue(Make.Tracks("a", "b", "c", "d", "e").ToList(), 0,
                TimeSpan.Zero, shuffle: false, LoopMode.Off);

            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), playback, () => null!);

            var list = overlay.FindName("LstQueue") as System.Windows.Controls.ListBox;
            var popup = overlay.FindName("QueuePopup") as System.Windows.Controls.Primitives.Popup;
            Check.That("the queue list exists", list != null && popup != null);

            // The popup needs a realised placement target, and the refresh-on-change handler
            // only runs while it is open — without this the checks below pass vacuously.
            // Anchored deliberately: left at WPF's default cascade position the window lands
            // somewhere different each run, which changes whether the panels stack above or
            // below and made this test depend on luck.
            ShowAndActivate(overlay);
            overlay.ShowQueue();
            Pump();

            Check.That("the queue opened", popup!.IsOpen);
            Check.Equal("opens on the playing track", 0, list!.SelectedIndex);

            // Move to an entry that is not playing and remove it.
            list.SelectedIndex = 3;
            Check.That("removing succeeds", playback.RemoveAt(3));
            Pump();

            // The bug: the highlight used to snap back to the playing track on every
            // refresh, landing on the one entry RemoveAt refuses — so pressing the hotkey
            // again appeared to do nothing at all.
            Check.Equal("the selection stays where it was", 3, list.SelectedIndex);
            Check.Equal("now holding what moved up", "e", playback.PlayOrder[3].Title);

            // And again, so a run of removals keeps working.
            Check.That("a second removal also succeeds", playback.RemoveAt(list.SelectedIndex));
            Pump();
            Check.Equal("clamped to the shortened list", 2, list.SelectedIndex);
            Check.That("still a valid selection",
                list.SelectedIndex >= 0 && list.SelectedIndex < list.Items.Count);

            overlay.Close();
            playback.Dispose();
        });

        Check.Group("queue reopens on the playing track", () =>
        {
            var playback = FadePlayback();
            playback.RestoreQueue(Make.Tracks("a", "b", "c").ToList(), 1,
                TimeSpan.Zero, shuffle: false, LoopMode.Off);

            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), playback, () => null!);
            var list = overlay.FindName("LstQueue") as System.Windows.Controls.ListBox;
            var popup = overlay.FindName("QueuePopup") as System.Windows.Controls.Primitives.Popup;

            ShowAndActivate(overlay);
            overlay.ShowQueue();
            Pump();
            Check.Equal("opens on what is playing", 1, list!.SelectedIndex);

            // Wander off, close, reopen: an open is not an update, so it starts fresh.
            list.SelectedIndex = 2;
            popup!.IsOpen = false;
            overlay.ShowQueue();
            Pump();
            Check.Equal("reopening returns to the playing track", 1, list.SelectedIndex);

            overlay.Close();
            playback.Dispose();
        });

        Check.Group("a search row can be told apart", () =>
        {
            // The real case this fixes: three pressings of one recording, identical until
            // the album is shown. Duration does not separate the first two.
            var studio = new Track { Title = "Hells Bells", Artist = "AC-DC", Album = "Back In Black" };
            var comp = new Track { Title = "Hells Bells", Artist = "AC-DC", Album = "Who Made Who" };

            Check.That("the artist alone does not distinguish them",
                studio.DisplayArtist == comp.DisplayArtist);
            Check.That("the subtitle does", studio.SearchSubtitle != comp.SearchSubtitle);
            Check.Equal("and reads naturally", "AC-DC · Back In Black", studio.SearchSubtitle);

            // A track with no album must not show a dangling separator.
            var bare = new Track { Title = "Demo", Artist = "Someone" };
            Check.Equal("no album, no separator", "Someone", bare.SearchSubtitle);

            var unknown = new Track { Title = "Demo" };
            Check.Equal("no artist either", "Unknown Artist", unknown.SearchSubtitle);

            // Duration formatting matches the overlay clock, and is blank when unknown.
            var timed = new Track { Duration = TimeSpan.FromSeconds(255) };
            Check.Equal("minutes and seconds", "04:15", timed.DurationText);

            var longer = new Track { Duration = TimeSpan.FromMinutes(75) };
            Check.Equal("hours when it runs long", "1:15:00", longer.DurationText);

            Check.Equal("blank when unreported", string.Empty, new Track().DurationText);

            // A collection renders through the same template, so it needs the same members.
            var album = new TrackCollection { Kind = CollectionKind.Album, Artist = "AC-DC" };
            Check.Equal("a collection subtitle says what it is", "Album · AC-DC", album.SearchSubtitle);
            Check.Equal("and has no running time", string.Empty, album.DurationText);
        });

        Check.Group("search placeholder teaches the prefixes", () =>
        {
            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);

            var box = overlay.FindName("SearchTextBox") as System.Windows.Controls.TextBox;
            var hint = overlay.FindName("TxtSearchPlaceholder") as TextBlock;
            Check.That("both exist", box != null && hint != null);

            // The box is permanent now, so the placeholder is governed purely by whether
            // anything has been typed — not by whether search was "opened".
            overlay.OpenSearch();
            Check.Equal("shown on an empty box", Visibility.Visible, hint!.Visibility);

            // It names prefixes that actually parse, or it is teaching a lie.
            foreach (string prefix in new[] { "album", "fav", "queue" })
            {
                Check.That($"{prefix}: is mentioned", hint.Text.Contains(prefix + ":"));
                Check.That($"{prefix}: really parses", SearchQuery.Parse(prefix + ":x").IsScoped);
            }

            box!.Text = "a";
            Check.Equal("hidden once typing starts", Visibility.Collapsed, hint.Visibility);

            box.Text = string.Empty;
            Check.Equal("back when cleared", Visibility.Visible, hint.Visibility);

            overlay.Close();
        });

        Check.Group("the queue hint names the move shortcut", () =>
        {
            var config = new ConfigService();
            config.Current.Hotkeys.For(HotkeyActions.GrabQueueEntry).Keys = "Ctrl+Alt+J";

            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
            var hint = overlay.FindName("TxtQueueHint") as TextBlock;

            ShowAndActivate(overlay);
            overlay.ShowQueue();
            Pump();

            Check.That("it quotes the configured key", hint!.Text.Contains("Ctrl+Alt+J"));
            Check.That("and says what it does", hint.Text.Contains("moves"));
            Check.That("the ordinary actions survive", hint.Text.Contains("Enter plays"));

            overlay.Close();
        });

        Check.Group("a clashing shortcut is flagged inline", () =>
        {
            var config = new ConfigService();
            var indexer = new LibraryIndexerService();
            var jellyfin = new JellyfinApiClient { DeviceId = config.Current.DeviceId };

            var settings = new SettingsWindow(config, jellyfin, indexer);
            var rows = (settings.FindName("LstHotkeys") as ItemsControl)?.ItemsSource
                as System.Collections.Generic.IEnumerable<HotkeyRow>;

            Check.That("the rows were generated", rows != null);
            var list = rows!.ToList();

            Check.That("the defaults are clash-free", list.All(r => r.Conflict == null));

            // Point one action at another's combination: both ends must light up, because a
            // clash disables both shortcuts rather than just the newer one.
            var queue = list.First(r => r.Action == HotkeyActions.OpenQueue);
            var settingsRow = list.First(r => r.Action == HotkeyActions.OpenSettings);

            string taken = settingsRow.Keys;
            queue.Keys = taken;

            Check.That("the row just changed is flagged", queue.Conflict != null);
            Check.That("and so is the one it collided with", settingsRow.Conflict != null);
            Check.That("naming the other action",
                queue.Conflict!.Contains(HotkeyActions.Describe(HotkeyActions.OpenSettings)));
            Check.Equal("so it is visible", Visibility.Visible, queue.ConflictVisibility);

            // Undoing it must clear both, not just the row that moved.
            queue.Keys = "Ctrl+Alt+J";

            Check.That("the flag clears", queue.Conflict == null);
            Check.That("on both rows", settingsRow.Conflict == null);
            Check.Equal("and hides", Visibility.Collapsed, queue.ConflictVisibility);

            settings.Close();
        });

        Check.Group("the search bar is part of the overlay", () =>
        {
            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);

            var bar = overlay.FindName("SearchBarPopup") as System.Windows.Controls.Primitives.Popup;
            var box = overlay.FindName("SearchTextBox") as System.Windows.Controls.TextBox;
            var results = overlay.FindName("SearchPopup") as System.Windows.Controls.Primitives.Popup;
            var status = overlay.FindName("TxtStatus") as TextBlock;

            Check.That("the bar exists", bar != null && box != null);
            Check.That("closed while the overlay is down", !bar!.IsOpen);

            ShowAndActivate(overlay);
            overlay.ShowOverlay();
            Pump();

            Check.That("open as soon as the overlay is", bar.IsOpen);
            Check.Equal("and the box is not hidden", Visibility.Visible, box!.Visibility);

            // The status line no longer gives up its place to the search box.
            Check.Equal("the status line stays", Visibility.Visible, status!.Visibility);

            box.Text = "hells";
            overlay.OpenSearch();
            Pump();

            // Clearing a search puts the results away but must leave the bar alone.
            typeof(MainWindow)
                .GetMethod("CloseSearch", System.Reflection.BindingFlags.NonPublic |
                                          System.Reflection.BindingFlags.Instance)!
                .Invoke(overlay, null);
            Pump();

            Check.Equal("the search is cleared", string.Empty, box.Text);
            Check.That("the results are put away", !results!.IsOpen);
            Check.That("but the bar stays", bar.IsOpen);

            overlay.HideOverlay();
            Pump();
            Check.That("and goes with the overlay", !bar.IsOpen);

            overlay.Close();
        });

        Check.Group("panels stack above the applet", () =>
        {
            var config = new ConfigService();
            config.Current.Overlay.BackgroundOpacity = 0.8;

            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);

            var root = overlay.FindName("RootBorder") as Border;
            var searchPopup = overlay.FindName("SearchPopup") as System.Windows.Controls.Primitives.Popup;
            var queuePopup = overlay.FindName("QueuePopup") as System.Windows.Controls.Primitives.Popup;
            var searchPanel = overlay.FindName("SearchPanel") as Border;
            var queuePanel = overlay.FindName("QueuePanel") as Border;
            var barPanel = overlay.FindName("SearchBarPanel") as Border;

            Check.That("every piece exists",
                root != null && searchPopup != null && queuePopup != null &&
                searchPanel != null && queuePanel != null);

            double panelWidth = (double)Application.Current.FindResource("PanelWidth");
            Check.Equal("search matches the applet width", panelWidth, searchPopup!.Width);
            Check.Equal("queue matches the applet width", panelWidth, queuePopup!.Width);

            // Relative, not Top: PlacementMode.Top flips the panel *below* the target when
            // there is no room above, which put both panels over the applet.
            Check.Equal("search placement cannot flip",
                System.Windows.Controls.Primitives.PlacementMode.Relative, searchPopup.Placement);
            Check.Equal("queue placement cannot flip",
                System.Windows.Controls.Primitives.PlacementMode.Relative, queuePopup.Placement);
            Check.That("search targets the applet", ReferenceEquals(searchPopup.PlacementTarget, root));
            Check.That("queue targets the applet", ReferenceEquals(queuePopup.PlacementTarget, root));

            // Bottom-right of the work area: the default anchor, and the case this is for.
            ShowAndActivate(overlay);

            var rootFill = root!.Background as SolidColorBrush;
            Check.That("the search panel is tinted like the applet",
                (searchPanel!.Background as SolidColorBrush)?.Color == rootFill!.Color);
            Check.That("and so is the queue",
                (queuePanel!.Background as SolidColorBrush)?.Color == rootFill.Color);
            Check.Equal("corners match the applet", root.CornerRadius, searchPanel.CornerRadius);

            static Rect OnScreen(FrameworkElement e)
            {
                var origin = e.PointToScreen(new System.Windows.Point(0, 0));
                return new Rect(origin.X, origin.Y, e.ActualWidth, e.ActualHeight);
            }

            var applet = OnScreen(root);

            // Queue alone sits directly above the applet.
            overlay.ShowQueue();
            Pump();
            Pump();

            var queueAlone = OnScreen(queuePanel);
            Check.That("the queue is above the applet", queueAlone.Bottom <= applet.Top + 1);
            Check.That("left edges line up", Math.Abs(queueAlone.X - applet.X) < 1);
            Check.That("widths line up", Math.Abs(queueAlone.Width - applet.Width) < 1);

            // Open search: it takes the slot against the applet and the queue moves above it.
            overlay.OpenSearch();
            searchPopup.IsOpen = true;
            Pump();
            Pump();

            var search = OnScreen(searchPanel);
            var queue = OnScreen(queuePanel);

            var bar = OnScreen(barPanel!);

            // Outward from the applet: the search bar, its results, then the queue.
            Check.That("the bar is immediately above the applet", bar.Bottom <= applet.Top + 1);
            Check.That("and close to it", applet.Top - bar.Bottom < 20);
            Check.That("results sit above the bar", search.Bottom <= bar.Top + 1);
            Check.That("the queue moved above results", queue.Bottom <= search.Top + 1);
            Check.That("nothing overlaps",
                !queue.IntersectsWith(search) && !search.IntersectsWith(bar) &&
                !bar.IntersectsWith(applet));
            Check.That("all four stay left-aligned",
                Math.Abs(search.X - applet.X) < 1 && Math.Abs(queue.X - applet.X) < 1 &&
                Math.Abs(bar.X - applet.X) < 1);

            overlay.Close();
        });

        Check.Group("transport buttons hold their size", () =>
        {
            var config = new ConfigService();
            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);

            var play = overlay.FindName("BtnPlayPause") as System.Windows.Controls.Button;
            var loop = overlay.FindName("BtnLoop") as System.Windows.Controls.Button;
            Check.That("the transport buttons exist", play != null && loop != null);

            static double WidthWith(System.Windows.Controls.Button button, string glyph)
            {
                button.Content = glyph;
                button.InvalidateMeasure();
                button.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                return button.DesiredSize.Width;
            }

            // The play triangle is narrower than every other glyph in this row. Left to size
            // itself the button would shrink, and the centred row would reflow around it.
            double playing = WidthWith(play!, "\u23F8");
            double paused = WidthWith(play!, "\u25B6");
            Check.Equal("play and pause measure the same", paused, playing);

            double loopAll = WidthWith(loop!, "\uD83D\uDD01");
            double loopOne = WidthWith(loop!, "\uD83D\uDD02");
            Check.Equal("both loop glyphs measure the same", loopOne, loopAll);

            // Every button in the row shares one width, so nothing shifts as states change.
            var previous = overlay.FindName("BtnPrevious") as System.Windows.Controls.Button;
            previous!.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Check.Equal("and match the rest of the row", previous.DesiredSize.Width, playing);

            overlay.Close();
        });

        Check.Group("animation and opacity settings", () =>
        {
            var overlay = new OverlayConfig();
            Check.That("animations on by default", overlay.Animations);
            overlay.Animations = false;
            Check.That("animations can be turned off", !overlay.Animations);

            Check.Equal("default duration",
                OverlayConfig.DefaultAnimationMilliseconds, overlay.AnimationMilliseconds);

            overlay.AnimationMilliseconds = -50;
            Check.Equal("duration clamped up",
                OverlayConfig.MinAnimationMilliseconds, overlay.AnimationMilliseconds);

            overlay.AnimationMilliseconds = 99999;
            Check.Equal("duration clamped down",
                OverlayConfig.MaxAnimationMilliseconds, overlay.AnimationMilliseconds);

            overlay.AnimationMilliseconds = 0;
            Check.Equal("zero is allowed, meaning instant", 0, overlay.AnimationMilliseconds);

            overlay.AnimationMilliseconds = 250;
            Check.Equal("a sane duration is kept", 250, overlay.AnimationMilliseconds);

            Check.Equal("default opacity",
                OverlayConfig.DefaultBackgroundOpacity, overlay.BackgroundOpacity);

            overlay.BackgroundOpacity = 0.0;
            Check.Equal("opacity floored well above invisible",
                OverlayConfig.MinBackgroundOpacity, overlay.BackgroundOpacity);

            overlay.BackgroundOpacity = 5;
            Check.Equal("opacity capped", OverlayConfig.MaxBackgroundOpacity, overlay.BackgroundOpacity);

            overlay.BackgroundOpacity = double.NaN;
            Check.Equal("NaN falls back",
                OverlayConfig.DefaultBackgroundOpacity, overlay.BackgroundOpacity);

            overlay.BackgroundOpacity = 0.75;
            Check.Equal("a sane opacity is kept", 0.75, overlay.BackgroundOpacity);
        });

        Check.Group("background opacity reaches the panel, not the content", () =>
        {
            var opaque = (System.Windows.Media.SolidColorBrush)
                WindowStyling.Translucent(System.Windows.Media.Colors.CornflowerBlue, 1.0);
            Check.Equal("full opacity is fully opaque", (byte)255, opaque.Color.A);

            var half = (System.Windows.Media.SolidColorBrush)
                WindowStyling.Translucent(System.Windows.Media.Colors.CornflowerBlue, 0.5);
            Check.Equal("half opacity halves the alpha", (byte)128, half.Color.A);
            Check.Equal("the colour itself is untouched",
                System.Windows.Media.Colors.CornflowerBlue.R, half.Color.R);
            Check.That("and the brush is frozen for reuse", half.IsFrozen);

            // The overlay tints its panel; the settings window has chrome and must not.
            var config = new ConfigService();
            config.Current.Overlay.Animations = true;
            config.Current.Overlay.BackgroundOpacity = 0.6;

            var overlay = new MainWindow(config, FadeLibrary(), FadePlayback(), () => null!);
            var root = overlay.FindName("RootBorder") as System.Windows.Controls.Border;
            var tint = root?.Background as System.Windows.Media.SolidColorBrush;

            Check.That("the overlay panel is tinted", tint != null && tint.Color.A < 255);
            Check.Equal("to the configured amount", (byte)153, tint!.Color.A);

            var indexer = new LibraryIndexerService();
            var jellyfin = new JellyfinApiClient { DeviceId = config.Current.DeviceId };
            var settings = new SettingsWindow(config, jellyfin, indexer);
            Check.That("the settings window stays opaque", !settings.AllowsTransparency);

            settings.Close();
            overlay.Close();
        });

        Check.Group("auto-hide settings", () =>
        {
            var overlay = new OverlayConfig();
            Check.That("on by default", overlay.AutoHide);
            Check.Equal("default delay", OverlayConfig.DefaultAutoHideSeconds, overlay.AutoHideSeconds);

            overlay.AutoHideSeconds = 0.1;
            Check.Equal("clamped up", OverlayConfig.MinAutoHideSeconds, overlay.AutoHideSeconds);

            overlay.AutoHideSeconds = 9999;
            Check.Equal("clamped down", OverlayConfig.MaxAutoHideSeconds, overlay.AutoHideSeconds);

            overlay.AutoHideSeconds = double.NaN;
            Check.Equal("NaN falls back", OverlayConfig.DefaultAutoHideSeconds, overlay.AutoHideSeconds);

            overlay.AutoHideSeconds = 12.5;
            Check.Equal("a sane value is kept", 12.5, overlay.AutoHideSeconds);
        });

        Check.Group("sleep timer cycles the configured steps", () =>
        {
            using var timer = new SleepTimerService();

            Check.Equal("starts off", 0, timer.Minutes);
            Check.Equal("then the first default", 15, timer.Cycle());
            Check.Equal("then 30", 30, timer.Cycle());
            Check.Equal("then 45", 45, timer.Cycle());
            Check.Equal("then 60", 60, timer.Cycle());
            Check.Equal("then back to off", 0, timer.Cycle());

            // A working day needs longer steps than a nap does.
            timer.SetSteps(new[] { 60, 240, 480 });
            Check.Equal("an hour first", 60, timer.Cycle());
            Check.Equal("then four", 240, timer.Cycle());
            Check.Equal("then eight", 480, timer.Cycle());
            Check.Equal("off is always reachable", 0, timer.Cycle());

            timer.Set(240);
            Check.That("running once set", timer.IsRunning);
            Check.That("counts down from roughly the full period",
                timer.Remaining > TimeSpan.FromMinutes(239));

            // Changing the steps out from under a running timer must not strand it on a
            // duration the cycle can no longer reach.
            timer.SetSteps(new[] { 15, 30 });
            Check.That("a stranded timer is stopped", !timer.IsRunning);
            Check.Equal("and reports nothing remaining", TimeSpan.Zero, timer.Remaining);

            timer.Set(15);
            timer.Cancel();
            Check.That("cancelled", !timer.IsRunning);
        });

        Check.Group("sleep timer can be turned off entirely", () =>
        {
            using var timer = new SleepTimerService();

            timer.Set(30);
            Check.That("armed", timer.IsRunning);

            // Disabling must cancel what is running: a countdown alive under a disabled
            // feature would stop the music with nothing on screen explaining why.
            timer.Enabled = false;
            Check.That("turning it off cancels the countdown", !timer.IsRunning);
            Check.Equal("and clears the setting", 0, timer.Minutes);

            Check.Equal("the shortcut does nothing while off", 0, timer.Cycle());
            Check.That("still not running", !timer.IsRunning);

            timer.Set(30);
            Check.That("and cannot be armed directly either", !timer.IsRunning);

            timer.Enabled = true;
            Check.Equal("it works again once allowed", 15, timer.Cycle());
        });

        Check.Group("sleep timer steps are sanitised", () =>
        {
            var cfg = new SleepTimerConfig();
            Check.That("allowed by default", cfg.Enabled);
            Check.Equal("with the usual steps", "15,30,45,60", string.Join(",", cfg.Steps));

            cfg.Steps = new List<int> { 480, 60, 60, 0, -5, 240 };
            Check.Equal("junk dropped, deduped and sorted",
                "60,240,480", string.Join(",", cfg.Steps));

            cfg.Steps = new List<int> { 99999 };
            Check.Equal("beyond a day is not a sleep timer",
                string.Join(",", SleepTimerConfig.DefaultSteps), string.Join(",", cfg.Steps));

            cfg.Steps = new List<int>();
            Check.Equal("an empty list falls back rather than emptying the cycle",
                string.Join(",", SleepTimerConfig.DefaultSteps), string.Join(",", cfg.Steps));

            cfg.Steps = Enumerable.Range(1, 30).ToList();
            Check.Equal("capped so the far end stays reachable",
                SleepTimerConfig.MaxSteps, cfg.Steps.Count);

            Check.Equal("a day is the ceiling", 1440, SleepTimerConfig.MaxStepMinutes);
        });
    }

    private static ConfigService FadeConfig() => new();

    private static MusicLibrary FadeLibrary()
    {
        var (library, _) = Make.Library(
            new FakeSource("Fake", Yinyue.Models.TrackSource.Local, Make.Tracks("t0")));
        return library;
    }

    /// <summary>
    /// Puts a window where the overlay actually lives. Without this a test window lands at
    /// WPF's cascade position, which moves between runs and decides whether the panels stack
    /// above the applet or fall back to below it.
    /// </summary>
    private static void AnchorBottomRight(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.Left = area.Right - window.Width - 12;
        window.Top = area.Bottom - window.Height - 12;
    }

    /// <summary>
    /// Shows a window the way the overlay is actually shown.
    ///
    /// Activation matters: the panels are StaysOpen="False" popups, which Windows dismisses
    /// as soon as the owning app stops being active — and a test window shown among a dozen
    /// others is not active by default. Without this the panels close the moment they open,
    /// which looks like a placement bug and is not one.
    /// </summary>
    private static void ShowAndActivate(Window window)
    {
        AnchorBottomRight(window);
        window.Show();
        window.Activate();
        Pump();
    }

    private static PlaybackService FadePlayback() =>
        new(new AudioPlayerService(), FadeLibrary());

    /// <summary>
    /// Runs everything already queued on the dispatcher.
    ///
    /// The overlay refreshes the queue through <c>Dispatcher.BeginInvoke</c>, and restores
    /// focus at Input priority after that, so a test reading the list straight after a
    /// change would see the state from before it. Draining at Background priority — lower
    /// than both — guarantees they have run.
    /// </summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();

        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));

        Dispatcher.PushFrame(frame);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private static bool IsLayered(IntPtr hwnd) => (GetWindowLong(hwnd, -20) & 0x00080000) != 0;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
