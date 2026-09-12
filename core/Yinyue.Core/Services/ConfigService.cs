using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Loads and saves config.json, and hands the Jellyfin access token to the platform's
    /// secret store on the way past. Kept synchronous and tiny deliberately: it runs during
    /// startup, and startup has a budget.
    /// </summary>
    public class ConfigService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly string _configPath;
        private readonly ISecretStore _secrets;

        public AppConfig Current { get; private set; } = new();

        /// <summary>Raised after a successful Save so live components can re-read settings.</summary>
        public event EventHandler<AppConfig>? ConfigChanged;

        /// <summary>
        /// Kept as the name every caller already uses; the per-OS resolution lives in
        /// <see cref="AppPaths"/> so the indexer and the artwork cache cannot disagree.
        /// </summary>
        public static string AppDataFolder => AppPaths.DataFolder;

        /// <summary>
        /// <paramref name="secrets"/> is optional so that the many call sites which never
        /// touch a token — the whole test suite among them — need not supply one. Omitting
        /// it does not mean "store the token unprotected": the fallback declines to store it
        /// at all. See <see cref="UnavailableSecretStore"/>.
        /// </summary>
        public ConfigService(ISecretStore? secrets = null)
        {
            _secrets = secrets ?? UnavailableSecretStore.Instance;
            _configPath = Path.Combine(AppDataFolder, "config.json");
            Load();
        }

        public void Load()
        {
            if (!File.Exists(_configPath))
            {
                // First run: persist immediately so DeviceId is stable from here on.
                Current = new AppConfig();
                Save();
                return;
            }

            try
            {
                string json = File.ReadAllText(_configPath);
                Current = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions) ?? new AppConfig();

                bool dirty = false;

                if (string.IsNullOrWhiteSpace(Current.DeviceId))
                {
                    Current.DeviceId = Guid.NewGuid().ToString("N");
                    dirty = true;
                }

                // A shortcut whose default moved would otherwise sit on the old key and
                // collide with whatever now owns it, disabling both.
                if (Current.Hotkeys.MigrateSupersededDefaults()) dirty = true;

                if (dirty) Save();
            }
            catch (Exception ex)
            {
                // A corrupt config must not prevent startup. Keep the bad file for
                // diagnosis rather than silently overwriting the user's settings.
                System.Diagnostics.Debug.WriteLine($"[Config] Load failed, using defaults: {ex.Message}");
                TryBackupCorruptConfig();
                Current = new AppConfig();
            }
        }

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Current, SerializerOptions);

                // Write via temp file so an interrupted write cannot truncate the config.
                string tempPath = _configPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _configPath, overwrite: true);

                ConfigChanged?.Invoke(this, Current);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Config] Save failed: {ex.Message}");
            }
        }

        private void TryBackupCorruptConfig()
        {
            try
            {
                string backup = _configPath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Copy(_configPath, backup, overwrite: true);
            }
            catch
            {
                // Best effort only.
            }
        }

        #region Token protection

        /// <summary>
        /// Protects a Jellyfin access token for the current user and stores it. The password
        /// is never persisted — only the token the server issued in exchange.
        /// </summary>
        public void SetAccessToken(string? token)
        {
            if (string.IsNullOrEmpty(token))
            {
                Current.Jellyfin.ProtectedAccessToken = null;
                return;
            }

            // A store that cannot protect returns null, and null is written through as
            // "nothing stored". Never fall back to writing the token as it stands.
            Current.Jellyfin.ProtectedAccessToken = _secrets.Protect(token);
        }

        /// <summary>
        /// Returns the token, or null if absent or unreadable. Unreadable means the config
        /// was copied from another machine or user account; treat that as "not signed in"
        /// rather than as an error.
        /// </summary>
        public string? GetAccessToken()
        {
            string? stored = Current.Jellyfin.ProtectedAccessToken;
            if (string.IsNullOrEmpty(stored)) return null;

            return _secrets.Unprotect(stored);
        }

        public void ClearCredentials()
        {
            Current.Jellyfin.ProtectedAccessToken = null;
            Current.Jellyfin.UserId = null;
            Save();
        }

        #endregion
    }
}
