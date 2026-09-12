using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Yinyue.Models;

namespace Yinyue.Services
{
    public class LibraryIndexerService
    {
        private readonly string _dbPath;
        private readonly HashSet<string> _extensions;

        public event Action<int, int>? OnScanProgress; // (scanned, total)

        /// <summary>
        /// The engine's list, kept as given so the settings caption can name it. The shell
        /// supplies it from <see cref="IAudioPlayer.SupportedContainers"/>; this class used to
        /// hard-code a list of its own, which admitted .ogg while the engine could not play it.
        /// </summary>
        public IReadOnlyCollection<string> SupportedContainers { get; }

        /// <summary>
        /// Recursive, and tolerant of a subfolder that cannot be opened. The SearchOption
        /// overload of GetFiles is not — it enumerates with IgnoreInaccessible = false, so
        /// one denied directory anywhere in the tree threw and took the whole folder's scan
        /// with it. Hidden and system entries are skipped, as the default options do:
        /// desktop.ini and thumbnail caches are not music.
        /// </summary>
        private static readonly EnumerationOptions ScanOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        /// <param name="supportedContainers">
        /// What the audio engine decodes, as container names doubling as extensions. A name
        /// that is not an extension ("alac" lives in .m4a) simply never matches, harmlessly.
        /// </param>
        /// <param name="dbPath">
        /// Override for the suite, which indexes a real temporary tree and must not touch
        /// the user's tracks.db. Null means the shared data folder.
        /// </param>
        public LibraryIndexerService(IReadOnlyCollection<string> supportedContainers, string? dbPath = null)
        {
            SupportedContainers = supportedContainers;
            _extensions = new HashSet<string>(
                supportedContainers.Select(c => "." + c.TrimStart('.')),
                StringComparer.OrdinalIgnoreCase);

            // Through AppPaths rather than resolving it again here: this was the one place
            // in the portable half that computed the data folder for itself, and on macOS
            // that would have put tracks.db somewhere the rest of the app never looks.
            _dbPath = dbPath ?? Path.Combine(AppPaths.DataFolder, "tracks.db");
            InitializeDatabase();
        }

        private bool IsSupported(string file) => _extensions.Contains(Path.GetExtension(file));

        private void InitializeDatabase()
        {
            using var connection = GetConnection();
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Tracks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FilePath TEXT UNIQUE NOT NULL,
                    Title TEXT,
                    Artist TEXT,
                    Album TEXT,
                    DurationSeconds REAL,
                    Year INTEGER,
                    Genre TEXT,
                    IndexedAt TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_tracks_title ON Tracks(Title);
                CREATE INDEX IF NOT EXISTS idx_tracks_artist ON Tracks(Artist);
            ";
            command.ExecuteNonQuery();
        }

        private SqliteConnection GetConnection() => new($"Data Source={_dbPath}");

        /// <summary>
        /// Indexes every supported audio file under <paramref name="directoryPath"/>.
        /// Returns the number of tracks successfully written, so callers can report a
        /// concrete result — a fast scan is otherwise indistinguishable from no scan.
        /// </summary>
        public async Task<int> IndexDirectoryAsync(string directoryPath)
        {
            if (!Directory.Exists(directoryPath)) return 0;

            List<string> validFiles;
            try
            {
                validFiles = Directory
                    .EnumerateFiles(directoryPath, "*", ScanOptions)
                    .Where(IsSupported)
                    .ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // The root itself could not be read. There is nothing to index in a folder
                // we cannot open, and the caller's other folders should not pay for it.
                System.Diagnostics.Debug.WriteLine($"[Indexer] Cannot read {directoryPath}: {ex.Message}");
                return 0;
            }

            int total = validFiles.Count;
            int scanned = 0;
            int indexed = 0;
            var lastProgress = DateTime.MinValue;

            await Task.Run(() =>
            {
                using var connection = GetConnection();
                connection.Open();
                using var transaction = connection.BeginTransaction();

                var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT INTO Tracks (FilePath, Title, Artist, Album, DurationSeconds, Year, Genre, IndexedAt)
                    VALUES ($filePath, $title, $artist, $album, $duration, $year, $genre, $indexedAt)
                    ON CONFLICT(FilePath) DO UPDATE SET
                        Title=excluded.Title,
                        Artist=excluded.Artist,
                        Album=excluded.Album,
                        DurationSeconds=excluded.DurationSeconds,
                        Year=excluded.Year,
                        Genre=excluded.Genre,
                        IndexedAt=excluded.IndexedAt;
                ";

                var pFilePath = insertCmd.Parameters.Add("$filePath", SqliteType.Text);
                var pTitle = insertCmd.Parameters.Add("$title", SqliteType.Text);
                var pArtist = insertCmd.Parameters.Add("$artist", SqliteType.Text);
                var pAlbum = insertCmd.Parameters.Add("$album", SqliteType.Text);
                var pDuration = insertCmd.Parameters.Add("$duration", SqliteType.Real);
                var pYear = insertCmd.Parameters.Add("$year", SqliteType.Integer);
                var pGenre = insertCmd.Parameters.Add("$genre", SqliteType.Text);
                var pIndexedAt = insertCmd.Parameters.Add("$indexedAt", SqliteType.Text);

                foreach (var file in validFiles)
                {
                    try
                    {
                        using var tagFile = TagLib.File.Create(file);
                        
                        pFilePath.Value = file;
                        pTitle.Value = string.IsNullOrWhiteSpace(tagFile.Tag.Title) 
                            ? Path.GetFileNameWithoutExtension(file) 
                            : tagFile.Tag.Title;
                        pArtist.Value = string.IsNullOrWhiteSpace(tagFile.Tag.FirstPerformer) 
                            ? "Unknown Artist" 
                            : tagFile.Tag.FirstPerformer;
                        pAlbum.Value = tagFile.Tag.Album ?? "Unknown Album";
                        pDuration.Value = tagFile.Properties.Duration.TotalSeconds;
                        pYear.Value = (int)tagFile.Tag.Year;
                        pGenre.Value = tagFile.Tag.FirstGenre ?? "Unknown";
                        pIndexedAt.Value = DateTime.UtcNow.ToString("o");

                        insertCmd.ExecuteNonQuery();
                        indexed++;
                    }
                    catch
                    {
                        // Skip corrupted/unreadable files
                    }

                    scanned++;

                    // Throttled at the source. Firing per file floods every subscriber,
                    // and at thousands of files the notifications cost more than the work.
                    // The final tick always fires so listeners see completion.
                    var now = DateTime.UtcNow;
                    if (scanned == total || (now - lastProgress).TotalMilliseconds >= 50)
                    {
                        lastProgress = now;
                        OnScanProgress?.Invoke(scanned, total);
                    }
                }

                transaction.Commit();
            });

            return indexed;
        }

        /// <summary>
        /// Drops rows that no longer belong: files deleted from disk, anything outside the
        /// currently configured folders, and anything the engine no longer admits. Without
        /// this the index only ever grows — a removed folder would keep serving its tracks
        /// in search forever, and a format dropped from the engine's list would keep
        /// offering files that fail when played. Returns the number of rows removed.
        /// </summary>
        public async Task<int> PruneAsync(IEnumerable<string> configuredFolders)
        {
            var roots = configuredFolders
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar))
                .ToList();

            return await Task.Run(() =>
            {
                var doomed = new List<string>();

                using var connection = GetConnection();
                connection.Open();

                var select = connection.CreateCommand();
                select.CommandText = "SELECT FilePath FROM Tracks;";

                using (var reader = select.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string path = reader.GetString(0);

                        bool insideConfiguredRoot = roots.Any(root =>
                            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(path, root, StringComparison.OrdinalIgnoreCase));

                        if (!insideConfiguredRoot || !IsSupported(path) || !File.Exists(path))
                            doomed.Add(path);
                    }
                }

                if (doomed.Count == 0) return 0;

                using var transaction = connection.BeginTransaction();
                var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Tracks WHERE FilePath = $path;";
                var parameter = delete.Parameters.Add("$path", SqliteType.Text);

                foreach (string path in doomed)
                {
                    parameter.Value = path;
                    delete.ExecuteNonQuery();
                }

                transaction.Commit();
                return doomed.Count;
            });
        }

        /// <summary>Total tracks currently in the index, across all folders.</summary>
        public async Task<int> GetTrackCountAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var connection = GetConnection();
                    connection.Open();

                    var command = connection.CreateCommand();
                    command.CommandText = "SELECT COUNT(*) FROM Tracks;";
                    return Convert.ToInt32(command.ExecuteScalar());
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Indexer] Count failed: {ex.Message}");
                    return 0;
                }
            });
        }

        public async Task<List<LocalTrack>> SearchAsync(string query, int limit = 20)
        {
            var results = new List<LocalTrack>();

            await Task.Run(() =>
            {
                using var connection = GetConnection();
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT Id, FilePath, Title, Artist, Album, DurationSeconds, Year, Genre, IndexedAt
                    FROM Tracks
                    WHERE Title LIKE $query OR Artist LIKE $query OR Album LIKE $query
                    LIMIT $limit;
                ";
                command.Parameters.AddWithValue("$query", $"%{query}%");
                command.Parameters.AddWithValue("$limit", limit);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new LocalTrack
                    {
                        Id = reader.GetInt32(0),
                        FilePath = reader.GetString(1),
                        Title = reader.GetString(2),
                        Artist = reader.GetString(3),
                        Album = reader.GetString(4),
                        DurationSeconds = reader.GetDouble(5),
                        Year = reader.GetInt32(6),
                        Genre = reader.GetString(7),
                        IndexedAt = DateTime.Parse(reader.GetString(8))
                    });
                }
            });

            return results;
        }
    }
}