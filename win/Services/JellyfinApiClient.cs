using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Yinyue.Models;

namespace Yinyue.Services
{
    public enum JellyfinAuthStatus
    {
        Success,
        InvalidCredentials,
        Unreachable,
        NotAJellyfinServer,
        Error
    }

    /// <summary>
    /// Why authentication failed, not just that it did. The overlay has an offline mode, so
    /// "server unreachable" and "wrong password" need to lead to different behaviour — and
    /// telling a user their credentials are wrong when the server is simply down is a
    /// genuinely bad experience.
    /// </summary>
    public record JellyfinAuthResult(JellyfinAuthStatus Status, string Message)
    {
        public bool Succeeded => Status == JellyfinAuthStatus.Success;
    }

    public class JellyfinApiClient
    {
        /// <summary>
        /// Containers WinRT MediaPlayer handles natively. Advertising these to the
        /// /universal endpoint lets the server direct-play them untouched and transcode
        /// only what we genuinely cannot decode (ogg, opus, wma).
        /// </summary>
        private const string SupportedContainers = "mp3,aac,m4a,flac,alac,wav";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _httpClient;
        private string? _baseUrl;
        private string? _accessToken;
        private string? _userId;

        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) && !string.IsNullOrEmpty(_baseUrl);
        public string? BaseUrl => _baseUrl;
        public string? UserId => _userId;
        public string? AccessToken => _accessToken;

        /// <summary>Stable per-install identity, supplied from AppConfig.DeviceId.</summary>
        public string? DeviceId { get; set; }

        /// <summary>Ceiling for transcoded streams in bits per second. 0 means unlimited.</summary>
        public int MaxStreamingBitrate { get; set; }

        /// <summary>Raised when the server rejects our token, so the UI can prompt a re-login.</summary>
        public event EventHandler? Unauthorized;

        public JellyfinApiClient(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        #region Authentication

        private void ConfigureAuthorizationHeader(string? token = null)
        {
            string deviceName = Environment.MachineName;

            // Jellyfin keys sessions off DeviceId, so it must be stable across restarts and
            // unique per install. ConfigService supplies a persisted GUID; the machine-name
            // fallback only applies before config has loaded.
            string deviceId = DeviceId ?? "Yinyue-Desktop-" + Environment.MachineName;

            string headerValue =
                $"MediaBrowser Client=\"Yinyue\", Device=\"{deviceName}\", DeviceId=\"{deviceId}\", Version=\"1.0.0\"";

            if (!string.IsNullOrEmpty(token))
                headerValue += $", Token=\"{token}\"";

            // Modern Jellyfin prefers the standard Authorization header; X-Emby-Authorization
            // is the legacy form, still accepted and kept here for older servers.
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.Remove("X-Emby-Authorization");
            _httpClient.DefaultRequestHeaders.Add("Authorization", headerValue);
            _httpClient.DefaultRequestHeaders.Add("X-Emby-Authorization", headerValue);
        }

        /// <summary>
        /// Confirms a URL is reachable and really is a Jellyfin server, without credentials.
        /// </summary>
        public async Task<JellyfinPublicSystemInfo?> GetPublicSystemInfoAsync(
            string serverUrl, CancellationToken ct = default)
        {
            try
            {
                string url = serverUrl.TrimEnd('/') + "/System/Info/Public";
                var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return JsonSerializer.Deserialize<JellyfinPublicSystemInfo>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Jellyfin] Public info failed: {ex.Message}");
                return null;
            }
        }

        public async Task<JellyfinAuthResult> AuthenticateAsync(
            string serverUrl, string username, string password, CancellationToken ct = default)
        {
            _baseUrl = serverUrl.TrimEnd('/');
            ConfigureAuthorizationHeader();

            // Probe first so an unreachable server cannot be mistaken for bad credentials.
            var info = await GetPublicSystemInfoAsync(_baseUrl, ct).ConfigureAwait(false);
            if (info == null)
            {
                return new JellyfinAuthResult(JellyfinAuthStatus.Unreachable,
                    "Could not reach that address, or it is not a Jellyfin server. " +
                    "Check the URL, the port, and that the server is running.");
            }

            var requestBody = new JellyfinAuthRequest { Username = username, Password = password };
            using var content = new StringContent(
                JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient
                    .PostAsync($"{_baseUrl}/Users/AuthenticateByName", content, ct)
                    .ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return new JellyfinAuthResult(JellyfinAuthStatus.InvalidCredentials,
                        $"{info.ServerName} rejected that username or password.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new JellyfinAuthResult(JellyfinAuthStatus.Error,
                        $"{info.ServerName} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var authResult = JsonSerializer.Deserialize<JellyfinAuthResponse>(json, JsonOptions);

                if (authResult == null || string.IsNullOrEmpty(authResult.AccessToken))
                {
                    return new JellyfinAuthResult(JellyfinAuthStatus.Error,
                        "The server accepted the sign-in but returned no access token.");
                }

                _accessToken = authResult.AccessToken;
                _userId = authResult.User?.Id;
                ConfigureAuthorizationHeader(_accessToken);

                return new JellyfinAuthResult(JellyfinAuthStatus.Success,
                    $"Connected to {info.ServerName} {info.Version} as {authResult.User?.Name ?? username}.");
            }
            catch (TaskCanceledException)
            {
                return new JellyfinAuthResult(JellyfinAuthStatus.Unreachable,
                    "The server did not respond in time.");
            }
            catch (Exception ex)
            {
                return new JellyfinAuthResult(JellyfinAuthStatus.Error, ex.Message);
            }
        }

        public void RestoreSession(string serverUrl, string accessToken, string? userId)
        {
            _baseUrl = serverUrl.TrimEnd('/');
            _accessToken = accessToken;
            _userId = userId;
            ConfigureAuthorizationHeader(_accessToken);
        }

        public void SignOut()
        {
            _accessToken = null;
            _userId = null;
            ConfigureAuthorizationHeader();
        }

        #endregion

        #region Search

        public async Task<List<JellyfinMediaItem>> SearchMusicAsync(
            string query, int limit = 20, bool favoritesOnly = false, CancellationToken ct = default)
        {
            if (!IsAuthenticated)
                throw new InvalidOperationException("Client is not authenticated with a Jellyfin server.");

            string requestUrl = $"{_baseUrl}/Items" +
                                $"?userId={Uri.EscapeDataString(_userId ?? string.Empty)}" +
                                $"&searchTerm={Uri.EscapeDataString(query)}" +
                                "&includeItemTypes=Audio" +
                                "&recursive=true" +
                                $"&limit={limit}" +
                                "&sortBy=SortName&sortOrder=Ascending" +
                                (favoritesOnly ? "&filters=IsFavorite" : string.Empty) +
                                "&fields=MediaSources,UserData,Genres";

            var response = await SendAsync(requestUrl, ct).ConfigureAwait(false);
            if (response == null) return new List<JellyfinMediaItem>();

            var searchResult = JsonSerializer.Deserialize<JellyfinSearchResponse>(response, JsonOptions);
            return searchResult?.Items ?? new List<JellyfinMediaItem>();
        }

        /// <summary>
        /// Playlists matching a term.
        ///
        /// Narrower than the track search on purpose: a library where every album is also a
        /// playlist returns hundreds of them, and letting those crowd out the track matches
        /// would make search worse, not better.
        /// </summary>
        public Task<List<JellyfinMediaItem>> SearchPlaylistsAsync(
            string query, int limit = 5, bool favoritesOnly = false, CancellationToken ct = default) =>
            SearchContainersAsync("Playlist", query, limit, favoritesOnly, ct);

        /// <summary>
        /// Albums matching a term. Same shape as the playlist search — on this server the
        /// two differ only in <c>includeItemTypes</c>.
        /// </summary>
        public Task<List<JellyfinMediaItem>> SearchAlbumsAsync(
            string query, int limit = 5, bool favoritesOnly = false, CancellationToken ct = default) =>
            SearchContainersAsync("MusicAlbum", query, limit, favoritesOnly, ct);

        private async Task<List<JellyfinMediaItem>> SearchContainersAsync(
            string itemType, string query, int limit, bool favoritesOnly, CancellationToken ct)
        {
            if (!IsAuthenticated)
                throw new InvalidOperationException("Client is not authenticated with a Jellyfin server.");

            string requestUrl = $"{_baseUrl}/Items" +
                                $"?userId={Uri.EscapeDataString(_userId ?? string.Empty)}" +
                                $"&searchTerm={Uri.EscapeDataString(query)}" +
                                $"&includeItemTypes={itemType}" +
                                "&recursive=true" +
                                $"&limit={limit}" +
                                "&sortBy=SortName&sortOrder=Ascending" +
                                (favoritesOnly ? "&filters=IsFavorite" : string.Empty) +
                                "&fields=ChildCount,AlbumArtist";

            var response = await SendAsync(requestUrl, ct).ConfigureAwait(false);
            if (response == null) return new List<JellyfinMediaItem>();

            var searchResult = JsonSerializer.Deserialize<JellyfinSearchResponse>(response, JsonOptions);
            return searchResult?.Items ?? new List<JellyfinMediaItem>();
        }

        /// <summary>
        /// The contents of a playlist, in playlist order, paged up to a cap.
        ///
        /// No sortBy: the order the playlist was built in is the whole point, and asking the
        /// server to sort it would throw that away.
        /// </summary>
        /// <summary>
        /// The tracks of an album, in track order.
        ///
        /// A separate route from playlists because albums are not under <c>/Playlists</c>;
        /// they are ordinary containers addressed by <c>parentId</c>.
        /// </summary>
        public async Task<List<JellyfinMediaItem>> GetAlbumItemsAsync(
            string albumId, int cap = 1000, CancellationToken ct = default)
        {
            if (!IsAuthenticated)
                throw new InvalidOperationException("Client is not authenticated with a Jellyfin server.");

            string requestUrl = $"{_baseUrl}/Items" +
                                $"?userId={Uri.EscapeDataString(_userId ?? string.Empty)}" +
                                $"&parentId={Uri.EscapeDataString(albumId)}" +
                                $"&limit={cap}" +
                                "&sortBy=ParentIndexNumber,IndexNumber,SortName" +
                                "&fields=MediaSources,UserData";

            string? json = await SendAsync(requestUrl, ct).ConfigureAwait(false);
            if (json == null) return new List<JellyfinMediaItem>();

            var page = JsonSerializer.Deserialize<JellyfinSearchResponse>(json, JsonOptions);
            return page?.Items ?? new List<JellyfinMediaItem>();
        }

        public async Task<List<JellyfinMediaItem>> GetPlaylistItemsAsync(
            string playlistId, int cap = 1000, CancellationToken ct = default)
        {
            if (!IsAuthenticated)
                throw new InvalidOperationException("Client is not authenticated with a Jellyfin server.");

            const int pageSize = 200;
            var all = new List<JellyfinMediaItem>();

            while (all.Count < cap)
            {
                string requestUrl = $"{_baseUrl}/Playlists/{Uri.EscapeDataString(playlistId)}/Items" +
                                    $"?userId={Uri.EscapeDataString(_userId ?? string.Empty)}" +
                                    $"&startIndex={all.Count}" +
                                    $"&limit={Math.Min(pageSize, cap - all.Count)}" +
                                    "&fields=MediaSources,UserData";

                string? json = await SendAsync(requestUrl, ct).ConfigureAwait(false);
                if (json == null) break;

                var page = JsonSerializer.Deserialize<JellyfinSearchResponse>(json, JsonOptions);
                if (page == null || page.Items.Count == 0) break;

                all.AddRange(page.Items);

                if (all.Count >= page.TotalRecordCount) break;
            }

            return all;
        }

        /// <summary>
        /// Every favourited track, paged until the server runs out or the cap is reached.
        /// Favourites are a whole-library query rather than a search, so there is no term
        /// to narrow it — paging is the only thing keeping a large collection sane.
        /// </summary>
        public async Task<List<JellyfinMediaItem>> GetFavoriteTracksAsync(
            int cap = 1000, CancellationToken ct = default)
        {
            if (!IsAuthenticated)
                throw new InvalidOperationException("Client is not authenticated with a Jellyfin server.");

            const int pageSize = 200;
            var all = new List<JellyfinMediaItem>();

            while (all.Count < cap)
            {
                string requestUrl = $"{_baseUrl}/Items" +
                                    $"?userId={Uri.EscapeDataString(_userId ?? string.Empty)}" +
                                    "&includeItemTypes=Audio" +
                                    "&recursive=true" +
                                    "&filters=IsFavorite" +
                                    "&sortBy=SortName&sortOrder=Ascending" +
                                    $"&startIndex={all.Count}" +
                                    $"&limit={Math.Min(pageSize, cap - all.Count)}" +
                                    "&fields=MediaSources,UserData";

                string? json = await SendAsync(requestUrl, ct).ConfigureAwait(false);
                if (json == null) break;

                var page = JsonSerializer.Deserialize<JellyfinSearchResponse>(json, JsonOptions);
                if (page == null || page.Items.Count == 0) break;

                all.AddRange(page.Items);

                if (all.Count >= page.TotalRecordCount) break;
            }

            return all;
        }

        #endregion

        #region Streaming and images

        /// <summary>
        /// Builds a /universal stream URL. The server decides direct play versus transcode
        /// from the containers advertised, so already-compatible files stream untouched
        /// instead of being re-encoded — which is what the old stream.mp3 URL forced for
        /// every single track, lossless included.
        /// </summary>
        public string GetAudioStreamUrl(string itemId)
        {
            if (!IsAuthenticated)
                throw new InvalidOperationException("Client is not authenticated with a Jellyfin server.");

            var url = new StringBuilder($"{_baseUrl}/Audio/{itemId}/universal")
                .Append($"?userId={Uri.EscapeDataString(_userId ?? string.Empty)}")
                .Append($"&deviceId={Uri.EscapeDataString(DeviceId ?? "Yinyue")}")
                .Append($"&api_key={Uri.EscapeDataString(_accessToken!)}")
                .Append($"&container={SupportedContainers}")
                .Append("&transcodingContainer=mp3")
                .Append("&audioCodec=mp3")
                .Append("&enableRemoteMedia=true");

            if (MaxStreamingBitrate > 0)
                url.Append($"&maxStreamingBitrate={MaxStreamingBitrate}");

            return url.ToString();
        }

        /// <summary>
        /// Artwork URL for a track, or null when neither the track nor its album has one.
        /// Most audio items carry no image of their own — the album holds it.
        /// </summary>
        public string? GetImageUrl(JellyfinMediaItem item, int size = 300)
        {
            if (string.IsNullOrEmpty(_baseUrl)) return null;

            string? itemId = null;
            string? tag = null;

            if (item.ImageTags != null && item.ImageTags.TryGetValue("Primary", out string? primaryTag))
            {
                itemId = item.Id;
                tag = primaryTag;
            }
            else if (!string.IsNullOrEmpty(item.AlbumId) && !string.IsNullOrEmpty(item.AlbumPrimaryImageTag))
            {
                itemId = item.AlbumId;
                tag = item.AlbumPrimaryImageTag;
            }

            if (itemId == null) return null;

            return $"{_baseUrl}/Items/{itemId}/Images/Primary" +
                   $"?fillWidth={size}&fillHeight={size}&quality=90&tag={tag}";
        }

        /// <summary>
        /// Strips the token from a URL before it goes anywhere a human or a log can see it.
        /// Stream URLs must carry api_key in the query because WinRT MediaSource offers no
        /// clean way to attach headers.
        /// </summary>
        public static string Redact(string url)
        {
            int index = url.IndexOf("api_key=", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return url;

            int end = url.IndexOf('&', index);
            return end < 0
                ? url[..index] + "api_key=***"
                : url[..index] + "api_key=***" + url[end..];
        }

        #endregion

        #region Playback reporting

        public Task ReportPlaybackStartAsync(string itemId, CancellationToken ct = default) =>
            PostJsonAsync("/Sessions/Playing", new
            {
                ItemId = itemId,
                CanSeek = true,
                IsPaused = false,
                PlayMethod = "DirectStream"
            }, ct);

        public Task ReportPlaybackProgressAsync(
            string itemId, TimeSpan position, bool isPaused, CancellationToken ct = default) =>
            PostJsonAsync("/Sessions/Playing/Progress", new
            {
                ItemId = itemId,
                PositionTicks = position.Ticks,
                IsPaused = isPaused,
                CanSeek = true,
                PlayMethod = "DirectStream"
            }, ct);

        public Task ReportPlaybackStoppedAsync(
            string itemId, TimeSpan position, CancellationToken ct = default) =>
            PostJsonAsync("/Sessions/Playing/Stopped", new
            {
                ItemId = itemId,
                PositionTicks = position.Ticks
            }, ct);

        #endregion

        #region User data

        public async Task<bool> SetFavoriteAsync(string itemId, bool isFavorite, CancellationToken ct = default)
        {
            if (!IsAuthenticated) return false;

            string url = $"{_baseUrl}/Users/{_userId}/FavoriteItems/{itemId}";

            try
            {
                using var request = new HttpRequestMessage(
                    isFavorite ? HttpMethod.Post : HttpMethod.Delete, url);

                var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                NoteIfUnauthorized(response);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Jellyfin] Favourite update failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Transport helpers

        private async Task<string?> SendAsync(string url, CancellationToken ct)
        {
            try
            {
                var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
                NoteIfUnauthorized(response);

                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Jellyfin] {(int)response.StatusCode} for {Redact(url)}");
                    return null;
                }

                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Jellyfin] Request failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reporting calls are fire-and-forget by design: a failed progress report must
        /// never interrupt playback.
        /// </summary>
        private async Task PostJsonAsync(string path, object body, CancellationToken ct)
        {
            if (!IsAuthenticated) return;

            try
            {
                using var content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                var response = await _httpClient
                    .PostAsync($"{_baseUrl}{path}", content, ct)
                    .ConfigureAwait(false);

                NoteIfUnauthorized(response);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Jellyfin] Report to {path} failed: {ex.Message}");
            }
        }

        private void NoteIfUnauthorized(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                Unauthorized?.Invoke(this, EventArgs.Empty);
        }

        #endregion
    }
}
