// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace StreamEdge.Streams;

/// <summary>The slice of the Helix API the stream list needs.</summary>
public sealed class HelixClient(HttpClient http, string clientId, string accessToken)
{
    internal sealed record StreamsPage(
        [property: JsonPropertyName("data")] List<HelixStream> Data,
        [property: JsonPropertyName("pagination")] Pagination? Pagination);

    internal sealed record Pagination([property: JsonPropertyName("cursor")] string? Cursor);

    internal sealed record HelixStream(
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("user_login")] string UserLogin,
        [property: JsonPropertyName("user_name")] string UserName,
        [property: JsonPropertyName("game_name")] string GameName,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("viewer_count")] int ViewerCount,
        [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt)
    {
        public LiveStream ToLiveStream() =>
            new(UserLogin, UserName, Title, GameName, ViewerCount, StartedAt, UserId: UserId);
    }

    internal sealed record UsersPage([property: JsonPropertyName("data")] List<HelixUser> Data);

    internal sealed record HelixUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("profile_image_url")] string? ProfileImageUrl);

    /// <summary>Helix takes at most this many ids per "Get Users" request.</summary>
    private const int UsersPerRequest = 100;

    /// <summary>Avatars for these user ids, by id; users without one are left
    /// out. The list shows them at 30 pixels, so this asks for Twitch's 70×70
    /// size rather than the 300×300 the API returns.</summary>
    public async Task<IReadOnlyDictionary<string, Uri>> GetProfileImagesAsync(
        IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var images = new Dictionary<string, Uri>();
        foreach (var batch in userIds.Distinct().Chunk(UsersPerRequest))
        {
            var url = "https://api.twitch.tv/helix/users?" + string.Join("&", batch.Select(id => "id=" + Uri.EscapeDataString(id)));
            var page = await GetAsync<UsersPage>(url, ct).ConfigureAwait(false);
            foreach (var user in page?.Data ?? [])
                if (ParseProfileImage(user.ProfileImageUrl) is { } image) images[user.Id] = image;
        }
        return images;
    }

    internal static Uri? ParseProfileImage(string? url) =>
        Uri.TryCreate(url?.Replace("-300x300.", "-70x70."), UriKind.Absolute, out var uri) ? uri : null;

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Client-Id", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
    }

    /// <summary>Every live channel the user follows (all pages).</summary>
    public async Task<IReadOnlyList<LiveStream>> GetFollowedStreamsAsync(
        string userId, CancellationToken ct)
    {
        var streams = new List<LiveStream>();
        string? cursor = null;
        do
        {
            var url = $"https://api.twitch.tv/helix/streams/followed?user_id={Uri.EscapeDataString(userId)}&first=100"
                      + (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor));
            var page = await GetAsync<StreamsPage>(url, ct).ConfigureAwait(false);
            if (page is null) break;
            streams.AddRange(page.Data.Select(s => s.ToLiveStream()));
            cursor = page.Pagination?.Cursor;
        } while (!string.IsNullOrEmpty(cursor));
        return streams;
    }
}
