// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;

namespace StreamEdge.Chat.Emotes;

/// <summary>BetterTTV emotes (api.betterttv.net v3, cached endpoints).</summary>
public static class BttvEmotes
{
    public const string GlobalUrl = "https://api.betterttv.net/3/cached/emotes/global";

    public static string ChannelUrl(string twitchUserId) =>
        $"https://api.betterttv.net/3/cached/users/twitch/{Uri.EscapeDataString(twitchUserId)}";

    /// <summary>BTTV predates 7TV's zero-width flag; these global emotes have
    /// always been drawn over the previous emote by BTTV itself.</summary>
    public static readonly IReadOnlySet<string> LegacyZeroWidth = new HashSet<string>(StringComparer.Ordinal)
    {
        "SoSnowy", "IceCold", "SantaHat", "TopHat", "ReinDeer", "CandyCane", "cvMask", "cvHazmat",
    };

    /// <summary>Parses the global list: a bare array of emotes.</summary>
    public static IReadOnlyList<Emote> ParseGlobal(string json) =>
        JsonRead.Parse(json) is JsonArray array ? Collect(JsonRead.Items(array)) : [];

    /// <summary>Parses a channel: <c>{channelEmotes:[…], sharedEmotes:[…]}</c>.</summary>
    public static IReadOnlyList<Emote> ParseChannel(string json)
    {
        var root = JsonRead.Parse(json);
        return Collect(root.Arr("channelEmotes").Concat(root.Arr("sharedEmotes")));
    }

    public static async Task<IReadOnlyList<Emote>> FetchGlobalAsync(HttpClient http, CancellationToken ct) =>
        await Http.GetOrNullAsync(http, GlobalUrl, ct).ConfigureAwait(false) is { } json ? ParseGlobal(json) : [];

    /// <summary>A channel's emotes; empty when BTTV has never seen the
    /// channel (it answers 404).</summary>
    public static async Task<IReadOnlyList<Emote>> FetchChannelAsync(HttpClient http, string twitchUserId, CancellationToken ct) =>
        await Http.GetOrNullAsync(http, ChannelUrl(twitchUserId), ct).ConfigureAwait(false) is { } json ? ParseChannel(json) : [];

    private static List<Emote> Collect(IEnumerable<JsonNode> items)
    {
        var emotes = new List<Emote>();
        foreach (var item in items)
        {
            // "modifier" emotes (h!, w!, c!…) are prefix commands BTTV's own
            // client interprets, not images anyone means to see.
            if (item.Bool("modifier")) continue;
            var id = item.Str("id");
            var code = item.Str("code");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(code)) continue;
            var url = JsonRead.Url($"https://cdn.betterttv.net/emote/{Uri.EscapeDataString(id)}/2x");
            if (url is null) continue;
            emotes.Add(new Emote(code, url, EmoteProvider.Bttv, LegacyZeroWidth.Contains(code),
                item.Int("width"), item.Int("height")));
        }
        return emotes;
    }
}
