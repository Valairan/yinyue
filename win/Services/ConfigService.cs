using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Loads and saves %APPDATA%\Yinyue\config.json, and handles DPAPI protection for the
    /// Jellyfin access token. Kept synchronous and tiny deliberately: it runs during
    /// startup, which has a sub-200ms budget.
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

        public AppConfig Current { get; private set; } = new();

        /// <summary>Raised after a successful Save so live components can re-read settings.</summary>
        public event EventHandler<AppConfig>? ConfigChanged;

        public static string AppDataFolder
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Yinyue");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        public ConfigService()
        {
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
        /// Encrypts a Jellyfin access token for the current Windows user and stores it.
        /// The password is never persisted — only the token the server issued in exchange.
        /// </summary>
        public void SetAccessToken(string? token)
        {
            if (string.IsNullOrEmpty(token))
            {
                Current.Jellyfin.ProtectedAccessToken = null;
                return;
            }

            try
            {
                byte[] protectedBytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(token),
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);

                Current.Jellyfin.ProtectedAccessToken = Convert.ToBase64String(protectedBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Config] Token protection failed: {ex.Message}");
                Current.Jellyfin.ProtectedAccessToken = null;
            }
        }

        /// <summary>
        /// Returns the decrypted access token, or null if absent or undecryptable.
        /// Undecryptable means the config was copied from another machine or user profile;
        /// treat that as "not logged in" rather than an error.
        /// </summary>
        public string? GetAccessToken()
        {
            string? stored = Current.Jellyfin.ProtectedAccessToken;
            if (string.IsNullOrEmpty(stored)) return null;

            try
            {
                byte[] unprotectedBytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(stored),
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);

                return Encoding.UTF8.GetString(unprotectedBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Config] Token unprotect failed: {ex.Message}");
                return null;
            }
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
