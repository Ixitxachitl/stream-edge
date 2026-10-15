// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;

namespace StreamEdge.Chat.Emotes;

/// <summary>A channel as FFZ describes it: its emotes plus the custom
/// moderator / VIP badge images a broadcaster can upload.</summary>
public sealed record FfzRoom(IReadOnlyList<Emote> Emotes, Uri? ModeratorBadge, Uri? VipBadge)
{
    public static readonly FfzRoom Empty = new([], null, null);
}

/// <summary>FFZ's own badges (developer, supporter, bot…) and which Twitch
/// user ids wear each.</summary>
public sealed class FfzBadgeTable
{
    public static readonly FfzBadgeTable Empty = new(new Dictionary<string, IReadOnlyList<BadgeImage>>());

    private readonly IReadOnlyDictionary<string, IReadOnlyList<BadgeImage>> _byUser;

    internal FfzBadgeTable(IReadOnlyDictionary<string, IReadOnlyList<BadgeImage>> byUser) => _byUser = byUser;

    public int UserCount => _byUser.Count;

    /// <summary>Badges for a Twitch user id, empty for most users.</summary>
    public IReadOnlyList<BadgeImage> For(string? twitchUserId) =>
        twitchUserId is not null && _byUser.TryGetValue(twitchUserId, out var b) ? b : [];
}

/// <summary>FrankerFaceZ emotes and badges (api.frankerfacez.com v1).</summary>
public static class FfzEmotes
{
    public const string GlobalUrl = "https://api.frankerfacez.com/v1/set/global";
    public const string BadgesUrl = "https://api.frankerfacez.com/v1/badges/ids";

    public static string ChannelUrl(string twitchUserId) =>
        $"https://api.frankerfacez.com/v1/room/id/{Uri.EscapeDataString(twitchUserId)}";

    /// <summary>modifier_flags bit 0: "hidden" — effect modifiers such as
    /// ffzW or ffzHyper that transform the previous emote. We can't draw the
    /// effect, and their placeholder icon is not meant to be seen.</summary>
    private const int HiddenModifierFlag = 1;

    /// <summary>Parses /set/global. Only <c>default_sets</c> are global to
    /// everyone; the other sets in the payload belong to specific users.</summary>
    public static IReadOnlyList<Emote> ParseGlobal(string json)
    {
        var root = JsonRead.Parse(json);
        var sets = root.Obj("sets");
        var emotes = new List<Emote>();
        foreach (var setId in root.Arr("default_sets").Select(JsonRead.AsString))
            if (setId is not null && sets?[setId] is JsonObject set)
                emotes.AddRange(SetEmotes(set));
        return emotes;
    }

    /// <summary>Parses /room/id/{id}: every set it returns is the room's.</summary>
    public static FfzRoom ParseChannel(string json)
    {
        var root = JsonRead.Parse(json);
        var emotes = new List<Emote>();
        if (root.Obj("sets") is { } sets)
            foreach (var (_, set) in sets)
                emotes.AddRange(SetEmotes(set));
        var room = root.Obj("room");
        return new FfzRoom(emotes, BestUrl(room?["mod_urls"]), BestUrl(room?["vip_badge"]));
    }

    /// <summary>Parses /badges/ids: badge definitions plus
    /// <c>users: {"badgeId": [twitchUserId, …]}</c>.</summary>
    public static FfzBadgeTable ParseBadges(string json)
    {
        var root = JsonRead.Parse(json);
        var defs = new Dictionary<string, BadgeImage>(StringComparer.Ordinal);
        foreach (var badge in root.Arr("badges"))
        {
            var id = JsonRead.AsString(badge.Get("id"));
            var url = BestUrl(badge.Get("urls")) ?? JsonRead.Url(badge.Str("image"));
            if (id is null || url is null) continue;
            defs[id] = new BadgeImage(url, badge.Str("title") ?? badge.Str("name") ?? "FFZ", badge.Str("color"));
        }

        var byUser = new Dictionary<string, List<BadgeImage>>(StringComparer.Ordinal);
        if (root.Obj("users") is { } users)
            foreach (var (badgeId, ids) in users)
            {
                if (!defs.TryGetValue(badgeId, out var badge) || ids is not JsonArray list) continue;
                foreach (var user in JsonRead.Items(list).Select(JsonRead.AsString))
                {
                    if (user is null) continue;
                    if (!byUser.TryGetValue(user, out var mine)) byUser[user] = mine = [];
                    mine.Add(badge);
                }
            }
        return new FfzBadgeTable(byUser.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<BadgeImage>)kv.Value, StringComparer.Ordinal));
    }

    public static async Task<IReadOnlyList<Emote>> FetchGlobalAsync(HttpClient http, CancellationToken ct) =>
        await Http.GetOrNullAsync(http, GlobalUrl, ct).ConfigureAwait(false) is { } json ? ParseGlobal(json) : [];

    /// <summary>A room's emotes and badges; <see cref="FfzRoom.Empty"/> on 404.</summary>
    public static async Task<FfzRoom> FetchChannelAsync(HttpClient http, string twitchUserId, CancellationToken ct) =>
        await Http.GetOrNullAsync(http, ChannelUrl(twitchUserId), ct).ConfigureAwait(false) is { } json ? ParseChannel(json) : FfzRoom.Empty;

    public static async Task<FfzBadgeTable> FetchBadgesAsync(HttpClient http, CancellationToken ct) =>
        await Http.GetOrNullAsync(http, BadgesUrl, ct).ConfigureAwait(false) is { } json ? ParseBadges(json) : FfzBadgeTable.Empty;

    private static IEnumerable<Emote> SetEmotes(JsonNode? set)
    {
        foreach (var e in set.Arr("emoticons"))
        {
            var name = e.Str("name");
            if (string.IsNullOrEmpty(name)) continue;
            var modifier = e.Bool("modifier");
            if (modifier && ((e.Int("modifier_flags") ?? 0) & HiddenModifierFlag) != 0) continue;
            // Animated emotes carry a second url map of animated WebPs; the
            // plain one is then only the first frame.
            var url = BestUrl(e.Get("animated")) ?? BestUrl(e.Get("urls"));
            if (url is null) continue;
            yield return new Emote(name, url, EmoteProvider.Ffz, ZeroWidth: modifier, e.Int("width"), e.Int("height"));
        }
    }

    /// <summary>The "2" entry of an FFZ <c>{"1":…, "2":…, "4":…}</c> map,
    /// falling back to the others; also accepts a bare URL string.</summary>
    private static Uri? BestUrl(JsonNode? urls) => urls switch
    {
        JsonObject o => JsonRead.Url(o.Str("2")) ?? JsonRead.Url(o.Str("1")) ?? JsonRead.Url(o.Str("4")),
        JsonValue v => JsonRead.Url(JsonRead.AsString(v)),
        _ => null,
    };
}
