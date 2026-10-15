// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamEdge.Playback;

namespace StreamEdge.Chat;

/// <summary>What the name-click popup shows about a user. The profile comes
/// from Twitch's GQL API, which answers signed out; the follow date only
/// from Helix, which gives it to the channel's moderators and broadcaster.</summary>
public sealed record UserCard(
    string Id,
    string Login,
    string DisplayName,
    string? Description,
    DateTimeOffset? CreatedAt,
    Uri? ProfileImage,
    bool IsPartner,
    bool IsAffiliate,
    int? Followers)
{
    private const string Query =
        "query UserCard($login: String!) { user(login: $login) { id login displayName description createdAt"
        + " profileImageURL(width: 150) roles { isPartner isAffiliate } followers { totalCount } } }";

    public static async Task<UserCard?> FetchAsync(HttpClient http, string login, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = Query, ["variables"] = new JsonObject { ["login"] = login } };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Client-ID", TwitchPlayback.WebClientId);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) : null;
    }

    public static UserCard? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["data"]?["user"] is not JsonObject u) return null;
            var id = u["id"]?.GetValue<string>();
            var login = u["login"]?.GetValue<string>();
            if (id is null || login is null) return null;
            DateTimeOffset? created = DateTimeOffset.TryParse(u["createdAt"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var c) ? c : null;
            Uri.TryCreate(u["profileImageURL"]?.GetValue<string>(), UriKind.Absolute, out var avatar);
            return new UserCard(id, login, u["displayName"]?.GetValue<string>() ?? login,
                u["description"]?.GetValue<string>() is { Length: > 0 } d ? d : null,
                created, avatar,
                u["roles"]?["isPartner"]?.GetValue<bool?>() == true,
                u["roles"]?["isAffiliate"]?.GetValue<bool?>() == true,
                u["followers"]?["totalCount"]?.GetValue<int?>());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>When <paramref name="userId"/> followed the channel, or null
    /// (not following, or the caller isn't a moderator there: Helix needs
    /// <c>moderator:read:followers</c> and moderator status).</summary>
    public static async Task<DateTimeOffset?> FetchFollowedAtAsync(HttpClient http, string clientId, string token,
        string broadcasterId, string userId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.twitch.tv/helix/channels/followers?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&user_id={Uri.EscapeDataString(userId)}");
        request.Headers.Add("Client-Id", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? ParseFollowedAt(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) : null;
    }

    public static DateTimeOffset? ParseFollowedAt(string json)
    {
        try
        {
            var at = JsonNode.Parse(json)?["data"]?[0]?["followed_at"]?.GetValue<string>();
            return DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>"2 years, 3 months" / "5 days": coarse, as Twitch's card shows.</summary>
    public static string Age(DateTimeOffset since, DateTimeOffset now)
    {
        var days = (now - since).TotalDays;
        if (days < 1) return "today";
        if (days < 31) return Plural((int)days, "day");
        var months = (now.Year - since.Year) * 12 + now.Month - since.Month - (now.Day < since.Day ? 1 : 0);
        if (months < 12) return Plural(Math.Max(1, months), "month");
        var years = months / 12;
        var rest = months % 12;
        return rest == 0 ? Plural(years, "year") : $"{Plural(years, "year")}, {Plural(rest, "month")}";

        static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
    }
}
