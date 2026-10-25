// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamEdge.Chat;

/// <summary>What a moderation call came back with: ok, or Twitch's reason
/// ("The user specified in the user_id field is already banned.").</summary>
public readonly record struct ModResult(bool Ok, string? Error)
{
    public static readonly ModResult Success = new(true, null);
}

/// <summary>A channel's chat modes, as Helix reports and changes them.
/// Durations are null when the mode is off.</summary>
public sealed record ChatSettings(
    int? SlowSeconds,
    int? FollowerMinutes,
    bool SubscriberOnly,
    bool EmoteOnly,
    bool UniqueChat);

/// <summary>Moderator actions through the official Helix API, acting as the
/// signed-in user (<paramref name="moderatorId"/>) in a channel they
/// moderate (<paramref name="broadcasterId"/>). Each needs its own
/// <c>moderator:manage:*</c> scope; see <see cref="Auth.TwitchAuth.Scopes"/>.</summary>
public sealed class TwitchModeration(HttpClient http, string clientId, string token, string moderatorId)
{
    private const string Helix = "https://api.twitch.tv/helix/";

    /// <summary>Ban, or with <paramref name="seconds"/> a timeout (1 s to 2 weeks).</summary>
    public Task<ModResult> BanAsync(string broadcasterId, string userId, int? seconds, string? reason, CancellationToken ct)
    {
        var data = new JsonObject { ["user_id"] = userId };
        if (seconds is { } s) data["duration"] = Math.Clamp(s, 1, 1_209_600);
        if (!string.IsNullOrWhiteSpace(reason)) data["reason"] = reason.Trim();
        return SendAsync(HttpMethod.Post, $"moderation/bans?{Ids(broadcasterId)}", new JsonObject { ["data"] = data }, ct);
    }

    public Task<ModResult> UnbanAsync(string broadcasterId, string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"moderation/bans?{Ids(broadcasterId)}&user_id={Esc(userId)}", null, ct);

    public Task<ModResult> DeleteMessageAsync(string broadcasterId, string messageId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"moderation/chat?{Ids(broadcasterId)}&message_id={Esc(messageId)}", null, ct);

    /// <summary>Clears the whole chat (a delete with no message named).</summary>
    public Task<ModResult> ClearChatAsync(string broadcasterId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"moderation/chat?{Ids(broadcasterId)}", null, ct);

    public Task<ModResult> WarnAsync(string broadcasterId, string userId, string reason, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"moderation/warnings?{Ids(broadcasterId)}",
            new JsonObject { ["data"] = new JsonObject { ["user_id"] = userId, ["reason"] = reason.Trim() } }, ct);

    /// <summary>Announcement colours: primary, blue, green, orange, purple.</summary>
    public Task<ModResult> AnnounceAsync(string broadcasterId, string message, string color, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"chat/announcements?{Ids(broadcasterId)}",
            new JsonObject { ["message"] = message, ["color"] = color }, ct);

    public Task<ModResult> SetShieldModeAsync(string broadcasterId, bool on, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"moderation/shield_mode?{Ids(broadcasterId)}", new JsonObject { ["is_active"] = on }, ct);

    /// <summary>Twitch's shoutout: a card in the channel's chat that points
    /// viewers at <paramref name="toBroadcasterId"/>. Needs the channel to be
    /// live, and Twitch limits how often.</summary>
    public Task<ModResult> ShoutoutAsync(string broadcasterId, string toBroadcasterId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"chat/shoutouts?from_broadcaster_id={Esc(broadcasterId)}"
            + $"&to_broadcaster_id={Esc(toBroadcasterId)}&moderator_id={Esc(moderatorId)}", null, ct);

    public async Task<ChatSettings?> GetChatSettingsAsync(string broadcasterId, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"chat/settings?{Ids(broadcasterId)}", null);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? ParseChatSettings(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
            : null;
    }

    public Task<ModResult> SetChatSettingsAsync(string broadcasterId, ChatSettings settings, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["slow_mode"] = settings.SlowSeconds is not null,
            ["follower_mode"] = settings.FollowerMinutes is not null,
            ["subscriber_mode"] = settings.SubscriberOnly,
            ["emote_mode"] = settings.EmoteOnly,
            ["unique_chat_mode"] = settings.UniqueChat,
        };
        if (settings.SlowSeconds is { } slow) body["slow_mode_wait_time"] = Math.Clamp(slow, 3, 120);
        if (settings.FollowerMinutes is { } follow) body["follower_mode_duration"] = Math.Clamp(follow, 0, 129_600);
        return SendAsync(HttpMethod.Patch, $"chat/settings?{Ids(broadcasterId)}", body, ct);
    }

    public static ChatSettings? ParseChatSettings(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["data"]?[0] is not JsonObject s) return null;
            return new ChatSettings(
                s["slow_mode"]?.GetValue<bool>() == true ? s["slow_mode_wait_time"]?.GetValue<int?>() ?? 30 : null,
                s["follower_mode"]?.GetValue<bool>() == true ? s["follower_mode_duration"]?.GetValue<int?>() ?? 0 : null,
                s["subscriber_mode"]?.GetValue<bool>() == true,
                s["emote_mode"]?.GetValue<bool>() == true,
                s["unique_chat_mode"]?.GetValue<bool>() == true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private string Ids(string broadcasterId) => $"broadcaster_id={Esc(broadcasterId)}&moderator_id={Esc(moderatorId)}";

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
            return new ModResult(false, ErrorMessage(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                                        ?? $"Twitch said {(int)response.StatusCode}.");
        }
        catch (HttpRequestException ex)
        {
            return new ModResult(false, ex.Message);
        }
    }

    /// <summary>Helix errors: {"error":"Bad Request","status":400,"message":"…"}.</summary>
    public static string? ErrorMessage(string json)
    {
        try
        {
            return JsonNode.Parse(json)?["message"]?.GetValue<string>() is { Length: > 0 } m ? m : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
