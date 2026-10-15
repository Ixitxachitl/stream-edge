// SPDX-License-Identifier: GPL-3.0-or-later
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

/// <summary>A chat line ready to draw: no IRC left in it.</summary>
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

    public static ChatMessage System(string channel, string text, DateTimeOffset at) =>
        new(at, ChatMessageKind.System, channel, null, null, null, [], text);

    /// <summary>Turns a Twitch IRC line into a chat line, or null for lines
    /// that are protocol plumbing (PING, CAP, numerics, ROOMSTATE…).</summary>
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
                return new ChatMessage(SentAt(irc) ?? now, kind, channel, irc.Nick,
                    irc.Tag("display-name"), irc.Tag("color"), BadgesOf(irc), text);
            }
            case "USERNOTICE":
            {
                // Subs, raids, gifts: Twitch writes the human text for us.
                var system = irc.Tag("system-msg") ?? "";
                var text = irc.Params.Count > 1 ? $"{system} {irc.Trailing}".Trim() : system;
                return new ChatMessage(SentAt(irc) ?? now, ChatMessageKind.System, channel,
                    irc.Tag("login"), irc.Tag("display-name"), irc.Tag("color"), BadgesOf(irc), text);
            }
            case "NOTICE":
                return System(channel, irc.Trailing ?? "", now);
            case "JOIN" or "PART":
                return new ChatMessage(now, ChatMessageKind.Membership, channel, irc.Nick,
                    null, null, [], $"{irc.Command}: {irc.Nick}");
            default:
                return null;
        }
    }

    private static DateTimeOffset? SentAt(IrcMessage irc) =>
        long.TryParse(irc.Tag("tmi-sent-ts"), out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime()
            : null;

    /// <summary>"broadcaster/1,subscriber/12" → ["broadcaster/1", "subscriber/12"].
    /// Kept as set/version pairs so a badge image cache can key on them.</summary>
    private static IReadOnlyList<string> BadgesOf(IrcMessage irc) =>
        irc.Tag("badges")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
}
