// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http.Headers;
using StreamEdge.Chat.Emotes;

namespace StreamEdge.Chat;

/// <summary>Twitch's own chat badge images, from Helix. Resolves the
/// <c>"set/version"</c> strings in <see cref="ChatMessage.Badges"/>.</summary>
/// <remarks>Helix needs a user or app token; signed out, nothing is loaded,
/// <see cref="Resolve"/> returns null and the view draws its placeholder
/// chips. Thread-safe.</remarks>
public sealed class TwitchBadges
{
    public const string GlobalUrl = "https://api.twitch.tv/helix/chat/badges/global";

    public static string ChannelUrl(string broadcasterId) =>
        $"https://api.twitch.tv/helix/chat/badges?broadcaster_id={Uri.EscapeDataString(broadcasterId)}";

    private readonly object _gate = new();
    private IReadOnlyDictionary<string, BadgeImage> _global = new Dictionary<string, BadgeImage>();
    private readonly Dictionary<string, IReadOnlyDictionary<string, BadgeImage>> _channels = new(StringComparer.Ordinal);

    /// <summary>Raised after a load; argument is the room id, null for global.</summary>
    public event Action<string?>? Changed;

    /// <summary>The image and title for a badge such as "subscriber/12" in
    /// the room with id <paramref name="roomId"/>. Channel badges win: that
    /// is how a channel's custom sub and bits badges replace the defaults.</summary>
    public BadgeImage? Resolve(string? roomId, string badge)
    {
        lock (_gate)
        {
            if (roomId is not null && _channels.TryGetValue(roomId, out var channel)
                && channel.TryGetValue(badge, out var mine))
                return mine;
            return _global.TryGetValue(badge, out var global) ? global : null;
        }
    }

    /// <summary>Loads global badges. No-op without a token; failures are
    /// traced and leave the previous data in place.</summary>
    public async Task LoadGlobalAsync(HttpClient http, string clientId, string? accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(accessToken)) return;
        if (await FetchAsync(http, GlobalUrl, clientId, accessToken, ct).ConfigureAwait(false) is not { } map) return;
        lock (_gate) _global = map;
        Changed?.Invoke(null);
    }

    /// <summary>Loads a channel's badges (<paramref name="broadcasterId"/> =
    /// ROOMSTATE <c>room-id</c>). No-op without a token.</summary>
    public async Task LoadChannelAsync(HttpClient http, string clientId, string? accessToken, string broadcasterId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(accessToken)) return;
        if (await FetchAsync(http, ChannelUrl(broadcasterId), clientId, accessToken, ct).ConfigureAwait(false) is not { } map) return;
        lock (_gate) _channels[broadcasterId] = map;
        Changed?.Invoke(broadcasterId);
    }

    public void UnloadChannel(string roomId)
    {
        lock (_gate) _channels.Remove(roomId);
    }

    /// <summary>Parses a Helix badges response:
    /// <c>{data:[{set_id, versions:[{id, image_url_1x/2x/4x, title}]}]}</c>
    /// into <c>"set/version" → image</c>.</summary>
    public static IReadOnlyDictionary<string, BadgeImage> Parse(string json)
    {
        var map = new Dictionary<string, BadgeImage>(StringComparer.Ordinal);
        foreach (var set in JsonRead.Parse(json).Arr("data"))
        {
            var setId = set.Str("set_id");
            if (string.IsNullOrEmpty(setId)) continue;
            foreach (var version in set.Arr("versions"))
            {
                var id = version.Str("id");
                var url = JsonRead.Url(version.Str("image_url_2x")) ?? JsonRead.Url(version.Str("image_url_1x"));
                if (string.IsNullOrEmpty(id) || url is null) continue;
                map[$"{setId}/{id}"] = new BadgeImage(url, version.Str("title") ?? setId);
            }
        }
        return map;
    }

    internal void SetGlobal(IReadOnlyDictionary<string, BadgeImage> map)
    {
        lock (_gate) _global = map;
    }

    internal void SetChannel(string roomId, IReadOnlyDictionary<string, BadgeImage> map)
    {
        lock (_gate) _channels[roomId] = map;
    }

    private static async Task<IReadOnlyDictionary<string, BadgeImage>?> FetchAsync(
        HttpClient http, string url, string clientId, string accessToken, CancellationToken ct)
    {
        try
        {
            var json = await Http.GetOrNullAsync(http, url, ct, request =>
            {
                request.Headers.Add("Client-Id", clientId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }).ConfigureAwait(false);
            return json is null ? null : Parse(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            // An expired token (401) or Helix hiccup: placeholders are fine.
            Trace.TraceWarning($"Badges: {url} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
