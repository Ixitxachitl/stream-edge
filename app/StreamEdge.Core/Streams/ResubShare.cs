// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamEdge.Playback;

namespace StreamEdge.Streams;

/// <summary>A resub the user can announce in a channel's chat, as twitch.tv
/// offers it ("Share your resub"): Twitch holds one per renewal until it's
/// used or the next one replaces it.</summary>
/// <param name="Id">What <see cref="ResubShare.ShareAsync"/> hands back.</param>
public sealed record ResubToken(string Id, int CumulativeMonths, int StreakMonths, bool IsGift, string? Gifter);

/// <summary>Announcing a resub in chat, from Twitch's GQL API with the
/// user's twitch.tv auth-token (the app's own login can't). Not documented
/// by Twitch; this is what twitch.tv sends, as other third-party chat apps
/// (Chatterino forks, StreamNook) do.</summary>
public static class ResubShare
{
    private const string TokenQuery =
        "query ResubToken($login: String!) { user(login: $login) { self { resubNotification { " +
        "id cumulativeTenureMonths streakTenureMonths isGiftSubscription gifter { displayName } } } } }";

    private const string ShareMutation =
        "mutation ShareResub($input: UseChatNotificationTokenInput!) { useChatNotificationToken(input: $input) { isSuccess } }";

    /// <summary>The resub waiting to be shared in <paramref name="login"/>'s
    /// chat, or null (none, or the request failed).</summary>
    public static async Task<ResubToken?> FetchAsync(HttpClient http, string login, string authToken, CancellationToken ct)
    {
        var json = await PostAsync(http, TokenQuery, new JsonObject { ["login"] = login }, authToken, ct).ConfigureAwait(false);
        return json is null ? null : ParseToken(json);
    }

    /// <summary>Posts the announcement, with the user's message (may be
    /// empty). Null on success, else why not.</summary>
    public static async Task<string?> ShareAsync(HttpClient http, string login, ResubToken token, string message,
        bool includeStreak, string authToken, CancellationToken ct)
    {
        var input = new JsonObject
        {
            ["channelLogin"] = login,
            ["tokenID"] = token.Id,
            ["message"] = message,
            ["includeStreak"] = includeStreak,
        };
        var json = await PostAsync(http, ShareMutation, new JsonObject { ["input"] = input }, authToken, ct).ConfigureAwait(false);
        return json is null ? "Twitch didn't answer." : ParseShare(json);
    }

    /// <summary>Public for tests.</summary>
    public static ResubToken? ParseToken(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["data"]?["user"]?["self"]?["resubNotification"] is not JsonObject n) return null;
            if (n["id"]?.GetValue<string>() is not { Length: > 0 } id) return null;
            return new ResubToken(id,
                Int(n["cumulativeTenureMonths"]),
                Int(n["streakTenureMonths"]),
                n["isGiftSubscription"]?.GetValue<bool>() == true,
                n["gifter"]?["displayName"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Null when Twitch says it worked; otherwise its reason.
    /// Public for tests.</summary>
    public static string? ParseShare(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root?["errors"] is JsonArray { Count: > 0 } errors)
                return errors[0]?["message"]?.GetValue<string>() ?? "Twitch refused.";
            return root?["data"]?["useChatNotificationToken"]?["isSuccess"]?.GetValue<bool>() == true
                ? null
                : "Twitch refused.";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return "Twitch's reply didn't make sense.";
        }
    }

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static async Task<string?> PostAsync(HttpClient http, string query, JsonObject variables, string authToken, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = query, ["variables"] = variables };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Client-ID", TwitchPlayback.WebClientId);
        request.Headers.Add("Authorization", "OAuth " + authToken.Trim());
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
    }
}
