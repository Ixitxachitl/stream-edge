// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace StreamEdge.Streams;

/// <summary>The slice of the Helix API the stream list needs.</summary>
public sealed partial class HelixClient(HttpClient http, string clientId, string accessToken)
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
        [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
        [property: JsonPropertyName("tags")] List<string>? Tags = null)
    {
        public LiveStream ToLiveStream() =>
            new(UserLogin, UserName, Title, GameName, ViewerCount, StartedAt, UserId: UserId,
                Tags: Tags is { Count: > 0 } ? Tags : null);
    }

    internal sealed record UsersPage([property: JsonPropertyName("data")] List<HelixUser> Data);

    internal sealed record HelixUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("profile_image_url")] string? ProfileImageUrl,
        [property: JsonPropertyName("login")] string? Login = null,
        [property: JsonPropertyName("display_name")] string? DisplayName = null);

    /// <summary>Names and small avatars for these user ids, by id.</summary>
    public async Task<IReadOnlyDictionary<string, ChannelInfo>> GetUsersAsync(
        IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var users = new Dictionary<string, ChannelInfo>();
        foreach (var batch in userIds.Distinct().Chunk(UsersPerRequest))
        {
            var url = "https://api.twitch.tv/helix/users?" + string.Join("&", batch.Select(id => "id=" + Uri.EscapeDataString(id)));
            var page = await GetAsync(HelixJson.Default.UsersPage, url, ct).ConfigureAwait(false);
            foreach (var u in page?.Data ?? [])
                if (u.Login is { Length: > 0 } login)
                    users[u.Id] = new ChannelInfo(login, string.IsNullOrEmpty(u.DisplayName) ? login : u.DisplayName,
                        null, ParseProfileImage(u.ProfileImageUrl));
        }
        return users;
    }

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
            var page = await GetAsync(HelixJson.Default.UsersPage, url, ct).ConfigureAwait(false);
            foreach (var user in page?.Data ?? [])
                if (ParseProfileImage(user.ProfileImageUrl) is { } image) images[user.Id] = image;
        }
        return images;
    }

    internal static Uri? ParseProfileImage(string? url) =>
        Uri.TryCreate(url?.Replace("-300x300.", "-70x70."), UriKind.Absolute, out var uri) ? uri : null;

    private async Task<T?> GetAsync<T>(JsonTypeInfo<T> type, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Client-Id", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false);
    }

    /// <summary>Helix's pages, read by generated code rather than reflection
    /// (which trimming the published app would break).</summary>
    [JsonSerializable(typeof(ChannelInformationPage))]
    [JsonSerializable(typeof(ChannelsPage))]
    [JsonSerializable(typeof(FollowedChannelsPage))]
    [JsonSerializable(typeof(FollowedPage))]
    [JsonSerializable(typeof(SharedChatPage))]
    [JsonSerializable(typeof(StreamsPage))]
    [JsonSerializable(typeof(UsersPage))]
    private sealed partial class HelixJson : JsonSerializerContext;

    internal sealed record ChannelsPage([property: JsonPropertyName("data")] List<HelixChannel> Data);

    internal sealed record HelixChannel(
        [property: JsonPropertyName("broadcaster_login")] string Login,
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("is_live")] bool IsLive,
        [property: JsonPropertyName("game_name")] string? GameName,
        [property: JsonPropertyName("thumbnail_url")] string? ThumbnailUrl);

    /// <summary>Helix "Search Channels": channels whose name matches, live
    /// or not, in Twitch's order.</summary>
    public async Task<IReadOnlyList<ChannelSuggestion>> SearchChannelsAsync(string query, int first, CancellationToken ct)
    {
        var url = $"https://api.twitch.tv/helix/search/channels?query={Uri.EscapeDataString(query)}&first={first}";
        var page = await GetAsync(HelixJson.Default.ChannelsPage, url, ct).ConfigureAwait(false);
        return [.. (page?.Data ?? []).Select(c => new ChannelSuggestion(c.Login,
            string.IsNullOrEmpty(c.DisplayName) ? c.Login : c.DisplayName, c.IsLive, c.GameName ?? "",
            ParseProfileImage(c.ThumbnailUrl)))];
    }

    /// <summary>Which of these channels are live now (offline ones are left
    /// out), whether followed or not.</summary>
    public async Task<IReadOnlyList<LiveStream>> GetStreamsAsync(IReadOnlyCollection<string> logins, CancellationToken ct)
    {
        var streams = new List<LiveStream>();
        foreach (var batch in logins.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(UsersPerRequest))
        {
            var url = "https://api.twitch.tv/helix/streams?first=100&"
                      + string.Join("&", batch.Select(l => "user_login=" + Uri.EscapeDataString(l)));
            var page = await GetAsync(HelixJson.Default.StreamsPage, url, ct).ConfigureAwait(false);
            streams.AddRange((page?.Data ?? []).Select(s => s.ToLiveStream()));
        }
        return streams;
    }

    /// <summary>Whether the user follows the broadcaster (live or not).</summary>
    public async Task<bool> IsFollowingAsync(string userId, string broadcasterId, CancellationToken ct)
    {
        var url = $"https://api.twitch.tv/helix/channels/followed?user_id={Uri.EscapeDataString(userId)}"
                  + $"&broadcaster_id={Uri.EscapeDataString(broadcasterId)}";
        var page = await GetAsync(HelixJson.Default.FollowedPage, url, ct).ConfigureAwait(false);
        return page?.Data.Count > 0;
    }

    internal sealed record SharedChatPage([property: JsonPropertyName("data")] List<SharedChatSession> Data);

    internal sealed record SharedChatSession(
        [property: JsonPropertyName("host_broadcaster_id")] string? HostId,
        [property: JsonPropertyName("participants")] List<SharedChatParticipant>? Participants);

    internal sealed record SharedChatParticipant([property: JsonPropertyName("broadcaster_id")] string BroadcasterId);

    /// <summary>Helix "Get Shared Chat Session": the channels sharing chat
    /// with this one, host first and this one included; empty when it isn't
    /// in a session.</summary>
    public async Task<IReadOnlyList<string>> GetSharedChatAsync(string broadcasterId, CancellationToken ct)
    {
        var page = await GetAsync(HelixJson.Default.SharedChatPage, 
            "https://api.twitch.tv/helix/shared_chat/session?broadcaster_id=" + Uri.EscapeDataString(broadcasterId), ct).ConfigureAwait(false);
        if (page?.Data.FirstOrDefault() is not { Participants: { Count: > 0 } participants } session) return [];
        var ids = participants.Select(p => p.BroadcasterId).ToList();
        // Host first, as twitch.tv lists them.
        if (session.HostId is { } host && ids.Remove(host)) ids.Insert(0, host);
        return ids;
    }

    internal sealed record FollowedPage([property: JsonPropertyName("data")] List<System.Text.Json.JsonElement> Data);

    internal sealed record FollowedChannelsPage(
        [property: JsonPropertyName("data")] List<FollowedChannel> Data,
        [property: JsonPropertyName("pagination")] Pagination? Pagination);

    internal sealed record FollowedChannel(
        [property: JsonPropertyName("broadcaster_id")] string Id,
        [property: JsonPropertyName("broadcaster_login")] string Login,
        [property: JsonPropertyName("broadcaster_name")] string? Name);

    /// <summary>Every channel the user follows, live or not (all pages):
    /// id, login and display name.</summary>
    public async Task<IReadOnlyList<ChannelRef>> GetFollowedChannelsAsync(string userId, CancellationToken ct)
    {
        var channels = new List<ChannelRef>();
        string? cursor = null;
        do
        {
            var url = $"https://api.twitch.tv/helix/channels/followed?user_id={Uri.EscapeDataString(userId)}&first=100"
                      + (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor));
            var page = await GetAsync(HelixJson.Default.FollowedChannelsPage, url, ct).ConfigureAwait(false);
            if (page is null) break;
            channels.AddRange(page.Data.Select(c => new ChannelRef(c.Id, c.Login,
                string.IsNullOrEmpty(c.Name) ? c.Login : c.Name)));
            cursor = page.Pagination?.Cursor;
        } while (!string.IsNullOrEmpty(cursor));
        return channels;
    }

    internal sealed record ChannelInformationPage([property: JsonPropertyName("data")] List<ChannelInformation> Data);

    internal sealed record ChannelInformation(
        [property: JsonPropertyName("broadcaster_id")] string Id,
        [property: JsonPropertyName("content_classification_labels")] List<string>? Labels,
        [property: JsonPropertyName("is_branded_content")] bool IsBranded);

    /// <summary>Helix "Get Channel Information": each channel's content
    /// classification labels (ids, see <see cref="ContentLabels"/>), with
    /// branded content as one more. Channels with none are left out.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetContentLabelsAsync(
        IReadOnlyCollection<string> broadcasterIds, CancellationToken ct)
    {
        var labels = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var batch in broadcasterIds.Distinct().Chunk(UsersPerRequest))
        {
            var url = "https://api.twitch.tv/helix/channels?" + string.Join("&", batch.Select(id => "broadcaster_id=" + Uri.EscapeDataString(id)));
            var page = await GetAsync(HelixJson.Default.ChannelInformationPage, url, ct).ConfigureAwait(false);
            foreach (var c in page?.Data ?? [])
            {
                var list = (c.Labels ?? []).ToList();
                if (c.IsBranded) list.Add(ContentLabels.BrandedContent);
                if (list.Count > 0) labels[c.Id] = list;
            }
        }
        return labels;
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
            var page = await GetAsync(HelixJson.Default.StreamsPage, url, ct).ConfigureAwait(false);
            if (page is null) break;
            streams.AddRange(page.Data.Select(s => s.ToLiveStream()));
            cursor = page.Pagination?.Cursor;
        } while (!string.IsNullOrEmpty(cursor));
        return streams;
    }
}
