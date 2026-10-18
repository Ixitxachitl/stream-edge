// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamEdge.Playback;

namespace StreamEdge.Streams;

/// <summary>A followed channel, live or not: Helix "Get Followed Channels".</summary>
public sealed record ChannelRef(string Id, string Login, string DisplayName);

/// <summary>When channels last streamed, for offline rows in the list.
/// Helix has no "last live" short of listing each channel's videos, so this
/// is Twitch's GQL API with the web client ID, like <see cref="ChannelInfo"/>:
/// read-only, public, no login, up to 100 channels a request.</summary>
public static class LastBroadcasts
{
    private const int PerRequest = 100;

    private const string Query =
        "query LastBroadcasts($logins: [String!]) { users(logins: $logins) { login lastBroadcast { startedAt title game { name } } } }";

    /// <summary>By login (lower case). Channels that never streamed, or
    /// don't exist, are left out; so is a batch that failed.</summary>
    public static async Task<IReadOnlyDictionary<string, LastBroadcast>> FetchAsync(HttpClient http,
        IReadOnlyCollection<string> logins, CancellationToken ct)
    {
        var result = new Dictionary<string, LastBroadcast>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in logins.Select(l => l.ToLowerInvariant()).Distinct().Chunk(PerRequest))
        {
            var body = new JsonObject
            {
                ["query"] = Query,
                ["variables"] = new JsonObject { ["logins"] = new JsonArray([.. batch.Select(l => (JsonNode)l)]) },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Client-ID", TwitchPlayback.WebClientId);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            try
            {
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                foreach (var (login, last) in Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)))
                    result[login] = last;
            }
            catch (HttpRequestException)
            {
                // The rest of the list still shows; these rows go without.
            }
        }
        return result;
    }

    public static IReadOnlyDictionary<string, LastBroadcast> Parse(string json)
    {
        var result = new Dictionary<string, LastBroadcast>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var user in JsonNode.Parse(json)?["data"]?["users"]?.AsArray() ?? [])
            {
                if (user?["login"]?.GetValue<string>() is not { } login || user["lastBroadcast"] is not JsonObject last) continue;
                if (!DateTimeOffset.TryParse(last["startedAt"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var started)) continue;
                result[login] = new LastBroadcast(started, last["title"]?.GetValue<string>() ?? "",
                    last["game"]?["name"]?.GetValue<string>());
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }
        return result;
    }
}
