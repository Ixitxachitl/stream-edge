// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamEdge.Playback;

namespace StreamEdge.Streams;

/// <summary>A channel's look when it isn't live: name, offline banner,
/// avatar. From Twitch's GQL API with the web client ID, like the stream
/// itself, so it works signed out.</summary>
public sealed record ChannelInfo(string Login, string DisplayName, Uri? OfflineImage, Uri? ProfileImage)
{
    private const string Query =
        "query ChannelLook($login: String!) { user(login: $login) { login displayName offlineImageURL profileImageURL(width: 300) } }";

    public static async Task<ChannelInfo?> FetchAsync(HttpClient http, string login, CancellationToken ct)
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
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Null when the user doesn't exist or the shape changed.</summary>
    public static ChannelInfo? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["data"]?["user"] is not JsonObject user) return null;
            var login = user["login"]?.GetValue<string>();
            if (login is null) return null;
            return new ChannelInfo(login,
                user["displayName"]?.GetValue<string>() ?? login,
                ToUri(user["offlineImageURL"]?.GetValue<string>()),
                ToUri(user["profileImageURL"]?.GetValue<string>()));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Uri? ToUri(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri : null;
}
