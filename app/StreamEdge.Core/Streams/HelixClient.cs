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
        [property: JsonPropertyName("user_login")] string UserLogin,
        [property: JsonPropertyName("user_name")] string UserName,
        [property: JsonPropertyName("game_name")] string GameName,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("viewer_count")] int ViewerCount,
        [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt)
    {
        public LiveStream ToLiveStream() =>
            new(UserLogin, UserName, Title, GameName, ViewerCount, StartedAt);
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
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Client-Id", clientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<StreamsPage>(ct).ConfigureAwait(false);
            if (page is null) break;
            streams.AddRange(page.Data.Select(s => s.ToLiveStream()));
            cursor = page.Pagination?.Cursor;
        } while (!string.IsNullOrEmpty(cursor));
        return streams;
    }
}
