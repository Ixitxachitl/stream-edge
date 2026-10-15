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
public sealed class TwitchAuth(HttpClient http, string clientId)
{
    /// <summary>Followed live streams, reading and sending chat, and the
    /// user's own emotes (for the emote menu and their own messages).</summary>
    public const string Scopes = "user:read:follows chat:read chat:edit user:read:emotes";

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

    public async Task<DeviceCode> StartAsync(CancellationToken ct)
    {
        using var response = await http.PostAsync("https://id.twitch.tv/oauth2/device",
            Form(("client_id", clientId), ("scopes", Scopes)), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceCode>(ct).ConfigureAwait(false))!;
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
                return await response.Content.ReadFromJsonAsync<Tokens>(ct).ConfigureAwait(false);

            var error = await response.Content.ReadFromJsonAsync<ErrorBody>(ct).ConfigureAwait(false);
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
            ? await response.Content.ReadFromJsonAsync<Tokens>(ct).ConfigureAwait(false)
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
            ? await response.Content.ReadFromJsonAsync<Identity>(ct).ConfigureAwait(false)
            : null;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
}
