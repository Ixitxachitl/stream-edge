// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace StreamEdge.Chat;

public enum ChatMessageKind
{
    Message,
    /// <summary>/me: the whole line is drawn in the sender's colour.</summary>
    Action,
    /// <summary>JOIN/PART and similar low-key lines, drawn dimmed.</summary>
    Membership,
    /// <summary>Client and server notices, subs, raids.</summary>
    System,
}

/// <summary>A Twitch-native emote inside <see cref="ChatMessage.Text"/>.
/// <see cref="Start"/> and <see cref="Length"/> are UTF-16 offsets (what
/// <see cref="string.Substring(int, int)"/> wants), already converted from the
/// code-point offsets Twitch sends.</summary>
/// <summary>A GIF (from GIPHY) in a message, from the <c>gifs</c> tag. The
/// message text there is its title in brackets, "[Yes GIF by …]", shown when
/// the GIF isn't. <see cref="Url"/> must be used exactly as Twitch sends it.</summary>
public sealed record ChatGif(string Id, Uri Url, int Start, int Length);

public sealed record TwitchEmoteRange(string Id, int Start, int Length)
{
    /// <summary>Twitch's CDN picks the format for us with "default": an
    /// animated GIF for animated emotes, PNG otherwise. 2.0 is the size that
    /// stays sharp at 150–200% display scaling.</summary>
    public Uri ImageUrl => ImageUrlFor(Id);

    public static Uri ImageUrlFor(string id) =>
        new($"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(id)}/default/dark/2.0");
}

/// <summary>A chat line ready to draw: no IRC left in it.</summary>
/// <remarks>The positional members are the original shape and stay so that
/// hand-built lines (local echo, demo data) keep compiling; everything Twitch
/// adds on top arrives as init-only properties that default to "absent".</remarks>
public sealed record ChatMessage(
    DateTimeOffset Timestamp,
    ChatMessageKind Kind,
    string Channel,
    string? Login,
    string? DisplayName,
    string? Color,
    IReadOnlyList<string> Badges,
    string Text)
{
    private const string ActionPrefix = "\u0001ACTION ";

    public string Sender => DisplayName ?? Login ?? "";

    /// <summary>Twitch's message id (tag <c>id</c>); what a later CLEARMSG
    /// names in <see cref="ChatModeration.TargetMessageId"/>.</summary>
    public string? Id { get; init; }

    /// <summary>History only: a moderator had already deleted it.</summary>
    public bool WasDeleted { get; init; }

    /// <summary>Sender's numeric user id (tag <c>user-id</c>). Third-party
    /// badges (FFZ) and CLEARCHAT are keyed on it, not on the login.</summary>
    public string? UserId { get; init; }

    /// <summary>Native emotes, sorted by <see cref="TwitchEmoteRange.Start"/>,
    /// non-overlapping and inside <see cref="Text"/>.</summary>
    public IReadOnlyList<TwitchEmoteRange> TwitchEmotes { get; init; } = [];

    /// <summary>Tag <c>msg-id</c>: on PRIVMSG "highlighted-message" or
    /// "gigantified-emote-message" (channel-point redemptions); on USERNOTICE
    /// "sub", "resub", "subgift", "raid", "announcement"…</summary>
    public string? MsgId { get; init; }

    /// <summary>Tag <c>animation-id</c>: the paid "message effects" power-up
    /// ("simmer", "rainbow-eclipse", "cosmic-abyss"…).</summary>
    public string? AnimationId { get; init; }

    /// <summary>The sender's first ever message in this channel; official
    /// chat flags it for moderators.</summary>
    public bool IsFirstMessage { get; init; }

    /// <summary>Set when this line is a reply: who and what it answers.</summary>
    public string? ReplyParentName { get; init; }
    public string? ReplyParentBody { get; init; }

    /// <summary>Bits cheered with this message, 0 for none.</summary>
    public int Bits { get; init; }

    /// <summary>GIFs in the message (tag <c>gifs</c>), by position in
    /// <see cref="Text"/>, sorted and non-overlapping.</summary>
    public IReadOnlyList<ChatGif> Gifs { get; init; } = [];

    /// <summary>Tag <c>custom-reward-id</c>: the message was sent by
    /// redeeming this channel-points reward (one that asks for text).</summary>
    public string? CustomRewardId { get; init; }

    /// <summary>Shared chat: tag <c>source-room-id</c>, the channel the line
    /// was sent in. Twitch sets it on every line while the channel is in a
    /// shared chat session (its own lines too), and never otherwise.</summary>
    public string? SourceRoomId { get; init; }

    /// <summary>Shared chat: tag <c>source-id</c>, the message's id in the
    /// channel it was sent in; a deletion there names this one.</summary>
    public string? SourceId { get; init; }

    /// <summary>Shared chat: tag <c>source-badges</c>, the sender's badges
    /// in the channel the line was sent in, which twitch.tv shows.</summary>
    public IReadOnlyList<string>? SourceBadges { get; init; }

    /// <summary>Months subscribed, from tag <c>badge-info</c> (or, in shared
    /// chat, <c>source-badge-info</c>): what twitch.tv shows over the
    /// subscriber or founder badge. Null when Twitch didn't say.</summary>
    public int? SubscribedMonths { get; init; }

    /// <summary>The badges twitch.tv draws: the sender's in the channel the
    /// line came from.</summary>
    public IReadOnlyList<string> ShownBadges => SourceBadges ?? Badges;

    /// <summary>Sent in another channel of a shared chat session than
    /// <paramref name="roomId"/> (this tab's).</summary>
    public bool IsFromOtherRoom(string? roomId) => SourceRoomId is { } source && roomId is not null && source != roomId;

    public bool IsReply => ReplyParentName is not null;

    public static ChatMessage System(string channel, string text, DateTimeOffset at) =>
        new(at, ChatMessageKind.System, channel, null, null, null, [], text);

    /// <summary>Turns a Twitch IRC line into a chat line, or null for lines
    /// that are protocol plumbing (PING, CAP, numerics, ROOMSTATE…) or
    /// moderation (see <see cref="ChatModeration.FromIrc(IrcMessage)"/>).</summary>
    public static ChatMessage? FromIrc(IrcMessage irc, DateTimeOffset now)
    {
        var channel = irc.Params.Count > 0 ? irc.Params[0].TrimStart('#') : "";
        switch (irc.Command)
        {
            case "PRIVMSG":
            {
                var text = irc.Trailing ?? "";
                var kind = ChatMessageKind.Message;
                if (text.StartsWith(ActionPrefix, StringComparison.Ordinal))
                {
                    kind = ChatMessageKind.Action;
                    text = text[ActionPrefix.Length..].TrimEnd('\u0001');
                }
                return WithTags(new ChatMessage(SentAt(irc) ?? now, kind, channel, irc.Nick,
                    irc.Tag("display-name"), irc.Tag("color"), BadgesOf(irc), text), irc, text, 0);
            }
            case "USERNOTICE":
            {
                // Subs, raids, gifts: Twitch writes the human text for us. The
                // emotes tag indexes the user's own message, which we place
                // after the system text, so its offsets shift by that much.
                var system = irc.Tag("system-msg") ?? "";
                var user = irc.Params.Count > 1 ? irc.Trailing ?? "" : "";
                var (text, offset) = (system.Length, user.Length) switch
                {
                    (0, _) => (user, 0),
                    (_, 0) => (system, 0),
                    _ => ($"{system} {user}", system.Length + 1),
                };
                return WithTags(new ChatMessage(SentAt(irc) ?? now, ChatMessageKind.System, channel,
                    irc.Tag("login"), irc.Tag("display-name"), irc.Tag("color"), BadgesOf(irc), text), irc, user, offset);
            }
            case "NOTICE":
                return System(channel, irc.Trailing ?? "", now) with { MsgId = irc.Tag("msg-id") };
            case "JOIN" or "PART":
                return new ChatMessage(now, ChatMessageKind.Membership, channel, irc.Nick,
                    null, null, [], $"{irc.Command}: {irc.Nick}");
            default:
                return null;
        }
    }

    private static ChatMessage WithTags(ChatMessage m, IrcMessage irc, string emoteText, int emoteOffset) => m with
    {
        Id = irc.Tag("id"),
        UserId = irc.Tag("user-id"),
        TwitchEmotes = ParseEmotes(irc.Tag("emotes"), emoteText, emoteOffset),
        // A sub, raid or announcement in another channel of a shared chat
        // comes as "sharedchatnotice"; what it was is in source-msg-id.
        MsgId = irc.Tag("msg-id") == "sharedchatnotice" ? irc.Tag("source-msg-id") ?? "sharedchatnotice" : irc.Tag("msg-id"),
        SourceRoomId = irc.Tag("source-room-id") is { Length: > 0 } source ? source : null,
        SourceId = irc.Tag("source-id"),
        SourceBadges = irc.Tag("source-room-id") is { Length: > 0 } && irc.Tag("source-badges") is { } sourceBadges
            ? sourceBadges.Split(',', StringSplitOptions.RemoveEmptyEntries)
            : null,
        SubscribedMonths = MonthsIn(irc.Tag("source-room-id") is { Length: > 0 }
            ? irc.Tag("source-badge-info")
            : irc.Tag("badge-info")),
        AnimationId = irc.Tag("animation-id"),
        IsFirstMessage = irc.Tag("first-msg") == "1",
        ReplyParentName = irc.Tag("reply-parent-display-name") ?? irc.Tag("reply-parent-user-login"),
        ReplyParentBody = irc.Tag("reply-parent-msg-body"),
        Bits = int.TryParse(irc.Tag("bits"), out var bits) && bits > 0 ? bits : 0,
        CustomRewardId = irc.Tag("custom-reward-id"),
        Gifs = ParseGifs(irc.Tag("gifs"), emoteText, emoteOffset),
    };

    internal static DateTimeOffset? SentAt(IrcMessage irc) =>
        long.TryParse(irc.Tag("tmi-sent-ts"), out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime()
            : null;

    /// <summary>"broadcaster/1,subscriber/12" → ["broadcaster/1", "subscriber/12"].
    /// Kept as set/version pairs so a badge image cache can key on them.</summary>
    /// <summary>The months in <c>subscriber/14</c> (or <c>founder/14</c>)
    /// among a badge-info tag's entries.</summary>
    internal static int? MonthsIn(string? badgeInfo)
    {
        foreach (var info in badgeInfo?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            var parts = info.Split('/', 2);
            if (parts.Length == 2 && parts[0] is "subscriber" or "founder"
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var months)
                && months > 0)
                return months;
        }
        return null;
    }

    private static IReadOnlyList<string> BadgesOf(IrcMessage irc) =>
        irc.Tag("badges")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];

    /// <summary>Parses <c>id:start-end,start-end/id2:start-end</c>.</summary>
    /// <remarks>Twitch counts Unicode code points, so an emoji (a surrogate
    /// pair in .NET strings) earlier in the line would push every emote one
    /// char to the left if the numbers were used as string indexes. We map
    /// code points to UTF-16 offsets once, then drop anything out of range or
    /// overlapping rather than trust the tag blindly.</remarks>
    internal static IReadOnlyList<TwitchEmoteRange> ParseEmotes(string? tag, string text, int offset = 0)
    {
        if (string.IsNullOrEmpty(tag) || text.Length == 0) return [];

        var cpStart = CodePointStarts(text);
        var codePoints = cpStart.Count - 1;

        var ranges = new List<TwitchEmoteRange>();
        foreach (var entry in tag.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = entry.IndexOf(':');
            if (colon <= 0) continue;
            var id = entry[..colon];
            foreach (var span in entry[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var dash = span.IndexOf('-');
                if (dash < 0
                    || !int.TryParse(span.AsSpan(0, dash), out var first)
                    || !int.TryParse(span.AsSpan(dash + 1), out var last)
                    || first < 0 || last < first || last >= codePoints) continue;
                var start = cpStart[first];
                ranges.Add(new TwitchEmoteRange(id, start + offset, cpStart[last + 1] - start));
            }
        }
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

        var clean = new List<TwitchEmoteRange>(ranges.Count);
        var end = 0;
        foreach (var r in ranges)
        {
            if (r.Start < end) continue;
            clean.Add(r);
            end = r.Start + r.Length;
        }
        return clean;
    }

    /// <summary>cpStart[k] = UTF-16 index of code point k, plus one extra
    /// slot for the end: Twitch's tag positions count code points.</summary>
    private static List<int> CodePointStarts(string text)
    {
        var cpStart = new List<int>(text.Length + 1);
        for (var i = 0; i < text.Length; i++)
        {
            cpStart.Add(i);
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
        }
        cpStart.Add(text.Length);
        return cpStart;
    }

    /// <summary>Parses <c>start-end|gifId|url,start-end|gifId|url</c>: positions
    /// are code points with the end included, as for emotes. URLs carry query
    /// strings, so entries are split only where a new "start-end|" begins.</summary>
    internal static IReadOnlyList<ChatGif> ParseGifs(string? tag, string text, int offset = 0)
    {
        if (string.IsNullOrEmpty(tag) || text.Length == 0) return [];
        var cpStart = CodePointStarts(text);
        var codePoints = cpStart.Count - 1;
        var gifs = new List<ChatGif>();
        foreach (var entry in GifEntry.Split(tag))
        {
            var parts = entry.Split('|', 3);
            if (parts.Length != 3) continue;
            var dash = parts[0].IndexOf('-');
            if (dash < 0
                || !int.TryParse(parts[0].AsSpan(0, dash), out var first)
                || !int.TryParse(parts[0].AsSpan(dash + 1), out var last)
                || first < 0 || last < first || last >= codePoints
                || !Uri.TryCreate(parts[2], UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) continue;
            var start = cpStart[first];
            gifs.Add(new ChatGif(parts[1], url, start + offset, cpStart[last + 1] - start));
        }
        gifs.Sort((a, b) => a.Start.CompareTo(b.Start));
        var clean = new List<ChatGif>(gifs.Count);
        var end = 0;
        foreach (var g in gifs)
        {
            if (g.Start < end) continue;
            clean.Add(g);
            end = g.Start + g.Length;
        }
        return clean;
    }

    private static readonly global::System.Text.RegularExpressions.Regex GifEntry =
        new(@",(?=\d+-\d+\|)", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
