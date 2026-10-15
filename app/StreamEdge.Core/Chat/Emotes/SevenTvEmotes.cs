// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;

namespace StreamEdge.Chat.Emotes;

/// <summary>7TV emotes (7tv.io REST v3).</summary>
public static class SevenTvEmotes
{
    public const string GlobalUrl = "https://7tv.io/v3/emote-sets/global";

    public static string ChannelUrl(string twitchUserId) =>
        $"https://7tv.io/v3/users/twitch/{Uri.EscapeDataString(twitchUserId)}";

    /// <summary>Flag on the emote's entry in a set: the set owner made it
    /// zero-width.</summary>
    private const int SetZeroWidthFlag = 1;

    /// <summary>Flag on the emote itself: its author made it zero-width.</summary>
    private const int DataZeroWidthFlag = 256;

    /// <summary>Parses an emote set (the global endpoint returns one directly).</summary>
    public static IReadOnlyList<Emote> ParseGlobal(string json) => ParseSet(JsonRead.Parse(json));

    /// <summary>Parses a Twitch-linked user: <c>{emote_set:{emotes:[…]}}</c>;
    /// <c>emote_set</c> is null when the user has none active.</summary>
    public static IReadOnlyList<Emote> ParseChannel(string json) => ParseSet(JsonRead.Parse(json).Obj("emote_set"));

    public static async Task<IReadOnlyList<Emote>> FetchGlobalAsync(HttpClient http, CancellationToken ct) =>
        await Http.GetOrNullAsync(http, GlobalUrl, ct).ConfigureAwait(false) is { } json ? ParseGlobal(json) : [];

    /// <summary>A channel's active set; empty when the channel has no 7TV account (404).</summary>
    public static async Task<IReadOnlyList<Emote>> FetchChannelAsync(HttpClient http, string twitchUserId, CancellationToken ct) =>
        await Http.GetOrNullAsync(http, ChannelUrl(twitchUserId), ct).ConfigureAwait(false) is { } json ? ParseChannel(json) : [];

    private static List<Emote> ParseSet(JsonNode? set)
    {
        var emotes = new List<Emote>();
        foreach (var e in set.Arr("emotes"))
        {
            // The set-level name is what chatters type; it can be an alias
            // that differs from data.name.
            var name = e.Str("name");
            var data = e.Obj("data");
            var host = data.Obj("host");
            if (string.IsNullOrEmpty(name) || host is null) continue;
            var baseUrl = host.Str("url");
            if (string.IsNullOrEmpty(baseUrl)) continue;

            var files = host.Arr("files").ToList();
            var file = files.FirstOrDefault(f => f.Str("name") == "2x.webp")
                       ?? files.FirstOrDefault(f => string.Equals(f.Str("format"), "WEBP", StringComparison.OrdinalIgnoreCase))
                       ?? files.FirstOrDefault();
            var fileName = file.Str("name") ?? "2x.webp";
            var url = JsonRead.Url($"{baseUrl.TrimEnd('/')}/{fileName}");
            if (url is null) continue;

            // Size hint at 1x: from the 1x file, else half the 2x one.
            var one = files.FirstOrDefault(f => f.Str("name") == "1x.webp");
            int? width = one.Int("width") ?? file.Int("width") / 2;
            int? height = one.Int("height") ?? file.Int("height") / 2;

            var zeroWidth = ((e.Int("flags") ?? 0) & SetZeroWidthFlag) != 0
                            || ((data.Int("flags") ?? 0) & DataZeroWidthFlag) != 0;
            emotes.Add(new Emote(name, url, EmoteProvider.SevenTv, zeroWidth, width, height));
        }
        return emotes;
    }
}
