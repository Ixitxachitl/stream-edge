// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace StreamEdge.Chat;

/// <summary>A Twitch user, as Helix "Get Users" names them.</summary>
public sealed record TwitchUser(string Id, string Login, string DisplayName);

/// <summary>Things the signed-in user (<paramref name="userId"/>) does for
/// themselves through Helix: whispers, their chat colour, blocking. Each
/// needs its own <c>user:manage:*</c> scope; see <see cref="Auth.TwitchAuth.Scopes"/>.</summary>
public sealed class TwitchUserApi(HttpClient http, string clientId, string token, string userId)
{
    private const string Helix = "https://api.twitch.tv/helix/";

    /// <summary>Whisper length to someone never whispered before; Twitch
    /// allows more in an existing conversation but truncates at this anyway.</summary>
    public const int WhisperLength = 500;

    /// <summary>Looks a user up by login; null if there's no such user (or
    /// Twitch couldn't be reached).</summary>
    public async Task<TwitchUser?> GetUserAsync(string login, CancellationToken ct)
    {
        try
        {
            using var request = Request(HttpMethod.Get, "users?login=" + Esc(login.TrimStart('@').ToLowerInvariant()), null);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var user = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["data"]?[0];
            return user?["id"]?.GetValue<string>() is { } id && user["login"]?.GetValue<string>() is { } l
                ? new TwitchUser(id, l, user["display_name"]?.GetValue<string>() is { Length: > 0 } d ? d : l)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Sends a whisper. Twitch needs a verified phone number on the
    /// sender's account and allows 40 new recipients a day; it says so in
    /// the error when either stops it.</summary>
    public Task<ModResult> WhisperAsync(string toUserId, string message, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"whispers?from_user_id={Esc(userId)}&to_user_id={Esc(toUserId)}",
            new JsonObject { ["message"] = message.Length > WhisperLength ? message[..WhisperLength] : message }, ct);

    /// <summary>Named colours ("blue", "hot_pink"…) work for everyone; a hex
    /// colour ("#9147FF") needs Turbo or Prime.</summary>
    public Task<ModResult> SetChatColorAsync(string color, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"chat/color?user_id={Esc(userId)}&color={Esc(NormalizeColor(color))}", null, ct);

    public Task<ModResult> BlockAsync(string targetUserId, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"users/blocks?target_user_id={Esc(targetUserId)}", null, ct);

    public Task<ModResult> UnblockAsync(string targetUserId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"users/blocks?target_user_id={Esc(targetUserId)}", null, ct);

    /// <summary>Helix wants named colours in snake case ("hot_pink") and hex
    /// with its #; people type "HotPink" or "9147FF".</summary>
    public static string NormalizeColor(string color)
    {
        color = color.Trim();
        if (color.Length == 6 && color.All(Uri.IsHexDigit)) return "#" + color;
        if (color.StartsWith('#')) return color;
        var named = new StringBuilder();
        foreach (var c in color)
        {
            if (char.IsUpper(c) && named.Length > 0 && named[^1] != '_') named.Append('_');
            named.Append(c is '-' or ' ' ? '_' : char.ToLowerInvariant(c));
        }
        return named.ToString();
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);

    private HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body)
    {
        var request = new HttpRequestMessage(method, Helix + path);
        request.Headers.Add("Client-Id", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<ModResult> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        try
        {
            using var request = Request(method, path, body);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return ModResult.Success;
            return new ModResult(false, TwitchModeration.ErrorMessage(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                                        ?? $"Twitch said {(int)response.StatusCode}.");
        }
        catch (HttpRequestException ex)
        {
            return new ModResult(false, ex.Message);
        }
    }
}
