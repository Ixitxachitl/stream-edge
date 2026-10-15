// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamEdge.Playback;

namespace StreamEdge.Streams;

/// <summary>A channel-points reward as the channel offers it.</summary>
public sealed record ChannelReward(string Id, string Title, int Cost, string? Color, Uri? Image,
    string? Prompt, bool RequiresInput, bool IsAvailable);

/// <summary>A channel's points: what they're called, their icon, the rewards
/// on offer and, with the user's auth-token, the user's balance.
/// From Twitch's GQL API with the web client ID: Helix only shows rewards to
/// the broadcaster who made them, and has no viewer balance at all. Earning
/// and redeeming aren't here: Twitch gates both behind its own clients'
/// integrity checks.</summary>
public sealed record ChannelPoints(string Name, Uri? Icon, IReadOnlyList<ChannelReward> Rewards, int? Balance)
{
    public const string DefaultName = "Channel Points";

    private const string Query =
        "query ChannelPoints($login: String!) { channel(name: $login) { " +
        "communityPointsSettings { name isEnabled image { url url2x } " +
        "customRewards { id title cost backgroundColor isEnabled isPaused isInStock isUserInputRequired prompt " +
        "image { url url2x } defaultImage { url url2x } } } " +
        "self { communityPoints { balance } } } }";

    /// <param name="authToken">The twitch.tv auth-token cookie, for the
    /// balance; null fetches only the rewards.</param>
    public static async Task<ChannelPoints?> FetchAsync(HttpClient http, string login, string? authToken, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["query"] = Query,
            ["variables"] = new JsonObject { ["login"] = login },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Client-ID", TwitchPlayback.WebClientId);
        if (!string.IsNullOrWhiteSpace(authToken)) request.Headers.Add("Authorization", "OAuth " + authToken.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Null when the channel doesn't exist, has channel points
    /// off, or the shape changed.</summary>
    public static ChannelPoints? Parse(string json, int scale = 1)
    {
        try
        {
            if (JsonNode.Parse(json)?["data"]?["channel"] is not JsonObject channel) return null;
            if (channel["communityPointsSettings"] is not JsonObject settings) return null;
            if (settings["isEnabled"]?.GetValue<bool>() == false) return null;
            var rewards = new List<ChannelReward>();
            if (settings["customRewards"] is JsonArray list)
                foreach (var node in list)
                {
                    if (node is not JsonObject r || Str(r["id"]) is not { } id) continue;
                    // Disabled rewards are hidden on twitch.tv; paused or
                    // sold-out ones show greyed out.
                    if (r["isEnabled"]?.GetValue<bool>() == false) continue;
                    rewards.Add(new ChannelReward(id,
                        Str(r["title"])?.Trim() ?? "",
                        r["cost"]?.GetValue<int>() ?? 0,
                        Str(r["backgroundColor"]),
                        Image(r["image"], scale) ?? Image(r["defaultImage"], scale),
                        Str(r["prompt"]) is { Length: > 0 } prompt ? prompt : null,
                        r["isUserInputRequired"]?.GetValue<bool>() == true,
                        r["isPaused"]?.GetValue<bool>() != true && r["isInStock"]?.GetValue<bool>() != false));
                }
            rewards.Sort((a, b) => a.Cost.CompareTo(b.Cost));
            int? balance = channel["self"]?["communityPoints"]?["balance"] is JsonValue v && v.TryGetValue<int>(out var b) ? b : null;
            return new ChannelPoints(
                Str(settings["name"]) is { Length: > 0 } name ? name : DefaultName,
                Image(settings["image"], scale),
                rewards,
                balance);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public ChannelReward? Reward(string? id) => id is null ? null : Rewards.FirstOrDefault(r => r.Id == id);

    /// <summary>"950", "12.3K", "1.2M": how twitch.tv shortens a balance.</summary>
    public static string Short(int points) => points switch
    {
        < 1_000 => points.ToString(CultureInfo.CurrentCulture),
        < 1_000_000 => Trim(points / 1_000.0) + "K",
        _ => Trim(points / 1_000_000.0) + "M",
    };

    // Rounded down, as Twitch does: 999,999 is "999K", never "1000K".
    private static string Trim(double value) => value >= 100
        ? Math.Floor(value).ToString("0", CultureInfo.CurrentCulture)
        : (Math.Floor(value * 10) / 10).ToString("0.#", CultureInfo.CurrentCulture);

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static Uri? Image(JsonNode? node, int scale) =>
        node is JsonObject image && Str(scale > 1 ? image["url2x"] ?? image["url"] : image["url"]) is { } s
            && Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            ? uri
            : null;
}
