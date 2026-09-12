using System.IO;
using Yinyue.Services;

namespace Yinyue.Tests;

/// <summary>
/// The local index over a real temporary tree: real files, a real SQLite database, TagLib
/// reading real (if tiny) WAVs. Nothing here touches the user's tracks.db — the indexer takes
/// a database path for exactly this reason.
/// </summary>
public static class IndexerTests
{
    public static async Task Run()
    {
        await Check.GroupAsync("the indexer admits only what the engine plays", async () =>
        {
            using var tree = new TempTree();
            tree.Wav("song.wav");
            tree.Wav("deeper/another.wav");
            tree.Wav("vorbis.ogg");            // real audio bytes behind an extension the engine lacks
            tree.Text("notes.txt");

            var indexer = new LibraryIndexerService(new[] { "mp3", "wav" }, tree.Db);

            Check.Equal("two files indexed", 2, await indexer.IndexDirectoryAsync(tree.Root));
            Check.Equal("and counted", 2, await indexer.GetTrackCountAsync());
            Check.Equal("found by file name", 1, (await indexer.SearchAsync("another")).Count);
            Check.Equal("the unsupported extension is not there", 0, (await indexer.SearchAsync("vorbis")).Count);

            Check.Equal("a second pass updates rather than duplicates", 2, await indexer.IndexDirectoryAsync(tree.Root));
            Check.Equal("still two", 2, await indexer.GetTrackCountAsync());

            Check.Equal("a missing folder indexes nothing", 0,
                await indexer.IndexDirectoryAsync(Path.Combine(tree.Root, "nowhere")));
        });

        await Check.GroupAsync("pruning removes what no longer belongs", async () =>
        {
            using var tree = new TempTree();
            string a = Path.GetDirectoryName(tree.Wav("a/keep.wav"))!;
            string gone = tree.Wav("a/gone.wav");
            string b = Path.GetDirectoryName(tree.Wav("b/outside.wav"))!;

            var indexer = new LibraryIndexerService(new[] { "wav" }, tree.Db);
            await indexer.IndexDirectoryAsync(a);
            await indexer.IndexDirectoryAsync(b);
            Check.Equal("three to begin with", 3, await indexer.GetTrackCountAsync());

            File.Delete(gone);
            Check.Equal("a deleted file and an unconfigured folder go", 2, await indexer.PruneAsync(new[] { a }));
            Check.Equal("the survivor stays", 1, await indexer.GetTrackCountAsync());
            Check.Equal("nothing more to prune", 0, await indexer.PruneAsync(new[] { a }));

            // The engine changed its mind about a format. Rows for it must not linger,
            // offering files that will fail the moment they are played.
            var narrower = new LibraryIndexerService(new[] { "mp3" }, tree.Db);
            Check.Equal("a format the engine dropped is pruned", 1, await narrower.PruneAsync(new[] { a }));
            Check.Equal("index empty", 0, await narrower.GetTrackCountAsync());
        });

        Check.Group("the Jellyfin client advertises the engine's containers", () =>
        {
            var client = new JellyfinApiClient(new SilentAudioPlayer().SupportedContainers);
            client.RestoreSession("http://jellyfin.example", "not-a-real-token", "user");

            // The URL carries the api_key. Assert on it; never print it.
            string url = client.GetAudioStreamUrl("item");
            Check.That("direct-play list is the engine's", url.Contains("&container=mp3,wav&"));
            Check.That("everything else transcodes to mp3", url.Contains("&transcodingContainer=mp3"));

            var none = new JellyfinApiClient(Array.Empty<string>());
            none.RestoreSession("http://jellyfin.example", "not-a-real-token", "user");
            Check.That("an empty list still builds a URL, and the server transcodes everything",
                none.GetAudioStreamUrl("item").Contains("&container=&"));
        });
    }
}
