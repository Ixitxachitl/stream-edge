// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamEdge.Playback;

namespace StreamEdge.Streams;

/// <summary>Channels by name, followed or not: who each is and, if it's
/// live, its stream. For starred channels the user doesn't follow, which
/// Helix's followed lists leave out. Twitch's GQL API with the web client
/// ID, like <see cref="LastBroadcasts"/>: public, so it works signed out,
/// up to 100 channels a request.</summary>
public static class ChannelLookup
{
    private const int PerRequest = 100;

    private const string Query =
        "query ChannelLookup($logins: [String!]) { users(logins: $logins) { id login displayName profileImageURL(width: 70) "
        + "stream { title viewersCount createdAt game { name } freeformTags { name } } } }";

    /// <summary>A row for each channel that exists: live with its stream, or
    /// not (<see cref="LiveStream.IsLive"/>). Throws if Twitch can't be
    /// reached or answers with something else, so what was known can stay.</summary>
    public static async Task<IReadOnlyList<LiveStream>> FetchAsync(HttpClient http,
        IReadOnlyCollection<string> logins, CancellationToken ct)
    {
        var rows = new List<LiveStream>();
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
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            rows.AddRange(Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                          ?? throw new HttpRequestException("Unexpected answer from Twitch."));
        }
        return rows;
    }

    /// <summary>Null when the answer isn't a list of users (an error, or the
    /// shape changed); names that aren't channels are left out.</summary>
    public static IReadOnlyList<LiveStream>? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["data"]?["users"] is not JsonArray users) return null;
            var rows = new List<LiveStream>();
            foreach (var user in users)
            {
                if (user?["login"]?.GetValue<string>() is not { Length: > 0 } login) continue;
                var name = user["displayName"]?.GetValue<string>() is { Length: > 0 } display ? display : login;
                var id = user["id"]?.GetValue<string>();
                var avatar = Uri.TryCreate(user["profileImageURL"]?.GetValue<string>(), UriKind.Absolute, out var uri)
                             && uri.Scheme == "https" ? uri : null;
                if (user["stream"] is not JsonObject stream)
                {
                    rows.Add(new LiveStream(login, name, "", "", -1, default, UserId: id, ProfileImage: avatar, IsLive: false));
                    continue;
                }
                DateTimeOffset.TryParse(stream["createdAt"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var started);
                var tags = (stream["freeformTags"] as JsonArray)?
                    .Select(t => t?["name"]?.GetValue<string>()).OfType<string>().ToList();
                rows.Add(new LiveStream(login, name, stream["title"]?.GetValue<string>() ?? "",
                    stream["game"]?["name"]?.GetValue<string>() ?? "",
                    // Unknown rather than zero, if either is ever missing.
                    started == default ? -1 : stream["viewersCount"]?.GetValue<int>() ?? -1, started,
                    UserId: id, ProfileImage: avatar, Tags: tags is { Count: > 0 } ? tags : null));
            }
            return rows;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
