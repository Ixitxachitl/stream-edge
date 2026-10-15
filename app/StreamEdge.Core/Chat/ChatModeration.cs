// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Chat;

public enum ChatModerationKind
{
    /// <summary>CLEARMSG: one message removed; hide the line whose
    /// <see cref="ChatMessage.Id"/> is <see cref="ChatModeration.TargetMessageId"/>.</summary>
    DeleteMessage,
    /// <summary>CLEARCHAT naming a user: a timeout (<see cref="ChatModeration.Duration"/>
    /// set) or a ban (null duration). Hide that user's lines.</summary>
    ClearUser,
    /// <summary>CLEARCHAT with no user: a moderator ran /clear.</summary>
    ClearChat,
}

/// <summary>"Take something out of the chat log." Kept apart from
/// <see cref="ChatMessage"/> because nothing here is drawn as a line of its
/// own; the UI applies it to lines it already has.</summary>
public sealed record ChatModeration(ChatModerationKind Kind, string Channel, DateTimeOffset Timestamp)
{
    /// <summary>Tag <c>target-msg-id</c>, for <see cref="ChatModerationKind.DeleteMessage"/>.</summary>
    public string? TargetMessageId { get; init; }

    /// <summary>Tag <c>target-user-id</c>, for <see cref="ChatModerationKind.ClearUser"/>.</summary>
    public string? TargetUserId { get; init; }

    /// <summary>Login of the affected user: the trailing parameter for
    /// CLEARCHAT, the <c>login</c> tag for CLEARMSG.</summary>
    public string? TargetLogin { get; init; }

    /// <summary>Timeout length (<c>ban-duration</c>); null on a permanent ban.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Text of the deleted message (CLEARMSG trailing), if any.</summary>
    public string? DeletedText { get; init; }

    public static ChatModeration? FromIrc(IrcMessage irc) => FromIrc(irc, DateTimeOffset.Now);

    /// <summary>A moderation event for CLEARMSG/CLEARCHAT, null for any
    /// other command.</summary>
    public static ChatModeration? FromIrc(IrcMessage irc, DateTimeOffset now)
    {
        var channel = irc.Params.Count > 0 ? irc.Params[0].TrimStart('#') : "";
        var at = ChatMessage.SentAt(irc) ?? now;
        switch (irc.Command)
        {
            case "CLEARMSG":
                var target = irc.Tag("target-msg-id");
                if (target is null) return null;
                return new ChatModeration(ChatModerationKind.DeleteMessage, channel, at)
                {
                    TargetMessageId = target,
                    TargetLogin = irc.Tag("login"),
                    DeletedText = irc.Params.Count > 1 ? irc.Trailing : null,
                };
            case "CLEARCHAT":
                // "CLEARCHAT #chan" clears everything; "CLEARCHAT #chan :login" one user.
                var login = irc.Params.Count > 1 ? irc.Trailing : null;
                if (string.IsNullOrEmpty(login))
                    return new ChatModeration(ChatModerationKind.ClearChat, channel, at);
                return new ChatModeration(ChatModerationKind.ClearUser, channel, at)
                {
                    TargetLogin = login,
                    TargetUserId = irc.Tag("target-user-id"),
                    Duration = int.TryParse(irc.Tag("ban-duration"), out var s) && s > 0
                        ? TimeSpan.FromSeconds(s)
                        : null,
                };
            default:
                return null;
        }
    }
}
