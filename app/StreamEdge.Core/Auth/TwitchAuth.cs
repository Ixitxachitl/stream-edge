// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace StreamEdge.Auth;

/// <summary>Sign-in with Twitch's OAuth Device Code Grant: the app shows a
/// short code, the user approves it at twitch.tv/activate on any device. No
/// embedded browser, no redirect URI, no client secret, which suits a public
/// desktop client on all three platforms.</summary>
/// <remarks>Needs a Twitch application registered as a <b>Public</b> client
/// (<see cref="TwitchApp"/>), so tokens refresh without a secret.</remarks>
public sealed partial class TwitchAuth(HttpClient http, string clientId)
{
    /// <summary>Followed live streams, reading and sending chat, the user's
    /// own emotes (emote menu, own messages), moderator actions and the full
    /// viewer list for the channels they moderate, and whispers, chat colour
    /// and blocking. Logins from before a scope was added lack it.</summary>
    public const string Scopes = "user:read:follows chat:read chat:edit user:read:emotes"
        + " moderator:manage:banned_users moderator:manage:chat_messages moderator:manage:warnings"
        + " moderator:manage:chat_settings moderator:manage:announcements moderator:manage:shield_mode"
        + " moderator:read:followers moderator:read:chatters moderator:manage:shoutouts"
        + " user:manage:whispers user:manage:chat_color user:manage:blocked_users";

    /// <summary>Stands in for whispers, /color and /block, asked for
    /// together; tokens from before them lack all three.</summary>
    public const string UserActionsScope = "user:manage:whispers";

    /// <summary>Stands in for the moderation set: a token that has it was
    /// issued with all of them.</summary>
    public const string ModerationScope = "moderator:manage:banned_users";

    /// <summary>The viewer list's Helix "Get Chatters"; added after the
    /// moderation set, so tokens can have that without this.</summary>
    public const string ChattersScope = "moderator:read:chatters";

    /// <summary>Shoutouts; added after the moderation set.</summary>
    public const string ShoutoutsScope = "moderator:manage:shoutouts";

    /// <summary>Added after the first release; tokens from before lack it.</summary>
    public const string EmotesScope = "user:read:emotes";

    public sealed record DeviceCode(
        [property: JsonPropertyName("device_code")] string Code,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("verification_uri")] string VerificationUri,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval);

    public sealed record Tokens(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    public sealed record Identity(
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scopes")] IReadOnlyList<string>? Scopes = null);

    private sealed record ErrorBody([property: JsonPropertyName("message")] string? Message);

    /// <summary>Twitch's replies, read by generated code rather than
    /// reflection (which trimming the published app would break).</summary>
    [JsonSerializable(typeof(DeviceCode))]
    [JsonSerializable(typeof(Tokens))]
    [JsonSerializable(typeof(Identity))]
    [JsonSerializable(typeof(ErrorBody))]
    private sealed partial class AuthJson : JsonSerializerContext;

    public async Task<DeviceCode> StartAsync(CancellationToken ct)
    {
        using var response = await http.PostAsync("https://id.twitch.tv/oauth2/device",
            Form(("client_id", clientId), ("scopes", Scopes)), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(AuthJson.Default.DeviceCode, ct).ConfigureAwait(false))!;
    }

    /// <summary>Polls until the user approves the code, it expires, or they
    /// deny it. Returns null on expiry/denial.</summary>
    public async Task<Tokens?> WaitForApprovalAsync(DeviceCode code, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(1, code.Interval));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            using var response = await http.PostAsync("https://id.twitch.tv/oauth2/token", Form(
                ("client_id", clientId),
                ("scopes", Scopes),
                ("device_code", code.Code),
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code")), ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync(AuthJson.Default.Tokens, ct).ConfigureAwait(false);

            var error = await response.Content.ReadFromJsonAsync(AuthJson.Default.ErrorBody, ct).ConfigureAwait(false);
            switch (error?.Message)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                default:
                    return null;
            }
        }
        return null;
    }

    public async Task<Tokens?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var response = await http.PostAsync("https://id.twitch.tv/oauth2/token", Form(
            ("client_id", clientId),
            ("grant_type", "refresh_token"),
            ("refresh_token", refreshToken)), ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync(AuthJson.Default.Tokens, ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>Who a token belongs to, or null when it is no longer valid.
    /// Twitch requires apps to validate tokens at start-up and hourly.</summary>
    public async Task<Identity?> ValidateAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync(AuthJson.Default.Identity, ct).ConfigureAwait(false)
            : null;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
}
