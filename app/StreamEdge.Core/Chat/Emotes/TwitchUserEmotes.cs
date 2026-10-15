// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamEdge.Chat.Emotes;

/// <summary>A Twitch emote the signed-in user may use, with the channel it
/// belongs to (a sub or follower emote) or "0" for Twitch's own globals.</summary>
public sealed record UserEmote(Emote Emote, string OwnerId, string Type);

/// <summary>A channel that owns some of the user's emotes, for the emote
/// menu's section headers.</summary>
public sealed record EmoteOwner(string Id, string DisplayName, Uri? ProfileImage);

/// <summary>The signed-in user's Twitch emotes, from Helix "Get User Emotes"
/// (needs the <c>user:read:emotes</c> scope). Used for the emote menu, and to
/// draw emotes in the user's own messages, which Twitch never echoes back
/// with emote tags.</summary>
public static class TwitchUserEmotes
{
    public const string Url = "https://api.twitch.tv/helix/chat/emotes/user";

    /// <param name="broadcasterId">Adds that channel's follower emotes, which
    /// are only returned when asked for by channel.</param>
    public static async Task<IReadOnlyList<UserEmote>> FetchAsync(HttpClient http, string clientId, string token,
        string userId, string? broadcasterId, CancellationToken ct)
    {
        var all = new List<UserEmote>();
        string? cursor = null;
        do
        {
            var url = $"{Url}?user_id={Uri.EscapeDataString(userId)}"
                      + (broadcasterId is null ? "" : "&broadcaster_id=" + Uri.EscapeDataString(broadcasterId))
                      + (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor));
            using var request = Helix(url, clientId, token);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var (page, next) = Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            all.AddRange(page);
            cursor = next;
        } while (!string.IsNullOrEmpty(cursor));
        return all;
    }

    /// <summary>Names and avatars for owner ids, 100 per Helix request.</summary>
    public static async Task<IReadOnlyDictionary<string, EmoteOwner>> FetchOwnersAsync(HttpClient http, string clientId,
        string token, IEnumerable<string> ownerIds, CancellationToken ct)
    {
        var owners = new Dictionary<string, EmoteOwner>(StringComparer.Ordinal);
        foreach (var batch in ownerIds.Where(id => id.Length > 0 && id.All(char.IsAsciiDigit) && id != "0")
                     .Distinct().Chunk(100))
        {
            var url = "https://api.twitch.tv/helix/users?" + string.Join("&", batch.Select(id => "id=" + id));
            using var request = Helix(url, clientId, token);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;
            foreach (var owner in ParseOwners(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)))
                owners[owner.Id] = owner;
        }
        return owners;
    }

    private static HttpRequestMessage Helix(string url, string clientId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Client-Id", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>One page: emotes plus the pagination cursor.</summary>
    public static (IReadOnlyList<UserEmote> Emotes, string? Cursor) Parse(string json)
    {
        var emotes = new List<UserEmote>();
        try
        {
            var root = JsonNode.Parse(json);
            foreach (var node in root?["data"]?.AsArray() ?? [])
            {
                var id = node?["id"]?.GetValue<string>();
                var name = node?["name"]?.GetValue<string>();
                if (id is null || name is null) continue;
                var animated = node?["format"]?.AsArray().Any(f => f?.GetValue<string>() == "animated") == true;
                var url = new Uri($"https://static-cdn.jtvnw.net/emoticons/v2/{id}/{(animated ? "animated" : "static")}/dark/2.0");
                emotes.Add(new UserEmote(
                    new Emote(name, url, EmoteProvider.Twitch, ZeroWidth: false, Width: null, Height: null),
                    node?["owner_id"]?.GetValue<string>() ?? "0",
                    node?["emote_type"]?.GetValue<string>() ?? ""));
            }
            return (emotes, root?["pagination"]?["cursor"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return (emotes, null);
        }
    }

    public static IReadOnlyList<EmoteOwner> ParseOwners(string json)
    {
        var owners = new List<EmoteOwner>();
        try
        {
            foreach (var node in JsonNode.Parse(json)?["data"]?.AsArray() ?? [])
            {
                var id = node?["id"]?.GetValue<string>();
                if (id is null) continue;
                Uri.TryCreate(node?["profile_image_url"]?.GetValue<string>(), UriKind.Absolute, out var avatar);
                owners.Add(new EmoteOwner(id, node?["display_name"]?.GetValue<string>() ?? id, avatar));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }
        return owners;
    }
}
