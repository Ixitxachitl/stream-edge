// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamEdge.Playback;

/// <summary>Resolves a live channel to its HLS master playlist the way the
/// twitch.tv web player does: a GQL PlaybackAccessToken, then usher.
///
/// The token request is where ads are decided. Sent anonymously it gets the
/// ad-supported stream; sent with the user's own twitch.tv <c>auth-token</c>
/// cookie, a subscriber (or Turbo user) gets the same ad-free stream they get
/// in the browser. That cookie is optional, separate from the OAuth sign-in
/// (Helix tokens are not accepted by GQL), and only ever sent to gql.twitch.tv.</summary>
public sealed class TwitchPlayback(HttpClient http)
{
    /// <summary>The web player's public client ID; GQL rejects third-party IDs
    /// for this query.</summary>
    public const string WebClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    private const string Query =
        "query PlaybackAccessToken_Template($login: String!, $isLive: Boolean!, $vodID: ID!, $isVod: Boolean!, $playerType: String!) {"
        + " streamPlaybackAccessToken(channelName: $login, params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: $playerType}) @include(if: $isLive) {"
        + " value signature authorization { isForbidden forbiddenReasonCode } __typename }"
        + " videoPlaybackAccessToken(id: $vodID, params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: $playerType}) @include(if: $isVod) {"
        + " value signature __typename } }";

    public sealed record AccessToken(string Value, string Signature);

    public async Task<AccessToken?> GetAccessTokenAsync(string login, string? authToken, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["operationName"] = "PlaybackAccessToken_Template",
            ["query"] = Query,
            ["variables"] = new JsonObject
            {
                ["isLive"] = true,
                ["login"] = login,
                ["isVod"] = false,
                ["vodID"] = "",
                ["playerType"] = "site",
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Client-ID", WebClientId);
        if (!string.IsNullOrWhiteSpace(authToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", authToken.Trim());

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseAccessToken(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Pulls value/signature out of the GQL response; null when the
    /// channel is offline, forbidden (geo/sub-only), or the shape changed.</summary>
    public static AccessToken? ParseAccessToken(string json)
    {
        try
        {
            var token = JsonNode.Parse(json)?["data"]?["streamPlaybackAccessToken"];
            var value = token?["value"]?.GetValue<string>();
            var signature = token?["signature"]?.GetValue<string>();
            return value is null || signature is null ? null : new AccessToken(value, signature);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The usher master playlist for a token. Hand it to the player
    /// (LibVLC / mpv), which picks the variant.</summary>
    public static Uri UsherUrl(string login, AccessToken token)
    {
        var query = new Dictionary<string, string>
        {
            ["sig"] = token.Signature,
            ["token"] = token.Value,
            ["allow_source"] = "true",
            ["allow_audio_only"] = "true",
            ["fast_bread"] = "true",
            ["playlist_include_framerate"] = "true",
            ["player"] = "twitchweb",
            ["p"] = Random.Shared.Next(1_000_000, 9_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var qs = string.Join("&", query.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        return new Uri($"https://usher.ttvnw.net/api/channel/hls/{Uri.EscapeDataString(login.ToLowerInvariant())}.m3u8?{qs}");
    }
}
