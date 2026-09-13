using AppKit;
using Foundation;
using Yinyue.Models;
using Yinyue.Services;
using Yinyue.UI;

namespace Yinyue
{
    /// <summary>
    /// Composition root for the macOS shell — the counterpart to App.BuildServices on
    /// Windows, and hand-wired for the same reason: the app is small enough that a DI
    /// container would cost startup time for no benefit.
    ///
    /// Today this is a smoke test rather than an app: it builds the whole Core graph against
    /// the two macOS implementations and reports what came up. The point is that everything
    /// it constructs below the window is the same code the Windows app runs, so the only
    /// things that can fail here are the seams. The NSPanel overlay comes next.
    ///
    /// Run headless with --check; without it, nothing is shown yet.
    /// </summary>
    public static class MainClass
    {
        public static int Main(string[] args)
        {
            bool check = args.Contains("--check");
            if (args.Contains("--selftest")) { NSApplication.Init(); return SelfTest.Run(args.Length > 1 ? args[1] : "all"); }

            NSApplication.Init();

            var secrets = new KeychainSecretStore();
            var config = new ConfigService(secrets);

            // The engine owns the container list, and both of these take it: the indexer
            // decides which local files to admit, the client which containers to ask the
            // server to direct-play. Constructed before them for exactly that reason.
            using var audio = new MacAudioPlayer();

            var indexer = new LibraryIndexerService(audio.SupportedContainers);
            var artwork = new ArtworkCache();

            var jellyfin = new JellyfinApiClient(audio.SupportedContainers)
            {
                DeviceId = config.Current.DeviceId,
                MaxStreamingBitrate = config.Current.Jellyfin.MaxStreamingBitrate,
            };

            var library = new MusicLibrary(config);

            // Registration order is result order, exactly as on Windows: local tracks play
            // instantly and cannot fail mid-song, so they lead.
            library.Register(new LocalMusicSource(indexer, artwork));
            library.Register(new JellyfinMusicSource(jellyfin, artwork));

            var playback = new PlaybackService(audio, library);

            if (!check) return RunApp(config, playback);

            Console.WriteLine("Yinyue — macOS seam check");
            Console.WriteLine(new string('-', 52));
            Console.WriteLine($"data folder      : {AppPaths.DataFolder}");
            Console.WriteLine($"config loaded    : {config.Current.DeviceId is { Length: > 0 }}");
            Console.WriteLine($"secret store     : {secrets.GetType().Name}");
            Console.WriteLine($"audio engine     : {audio.GetType().Name}");
            Console.WriteLine($"containers ({audio.SupportedContainers.Count,2})  : {string.Join(" ", audio.SupportedContainers)}");
            Console.WriteLine($"sources          : {library.Sources.Count}");
            var snapshot = playback.Snapshot();
            Console.WriteLine($"playback ready   : queue={snapshot.Tracks.Count} position={snapshot.Position} playing={playback.IsPlaying}");
            Console.WriteLine($"volume round-trip: {RoundTripVolume(audio)}");
            Console.WriteLine($"token read       : {Describe(config.GetAccessToken())}");

            Console.WriteLine();
            Console.WriteLine("Core is running on macOS against the two macOS seams.");
            return 0;
        }

        private static string RoundTripVolume(IAudioPlayer audio)
        {
            audio.Volume = 0.42;
            double got = audio.Volume;
            audio.Volume = 1.0;
            return Math.Abs(got - 0.42) < 0.001 ? "ok" : $"MISMATCH ({got})";
        }

        private static string Describe(string? token) =>
            token is null ? "none stored (expected before sign-in)" : $"{token.Length} chars";

        private static int RunApp(ConfigService config, PlaybackService playback)
        {
            var app = NSApplication.SharedApplication;
            app.Delegate = new AppDelegate(config, playback);
            app.Run();
            return 0;
        }
    }
}
