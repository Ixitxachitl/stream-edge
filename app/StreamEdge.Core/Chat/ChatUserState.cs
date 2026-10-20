// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Chat;

/// <summary>How the signed-in user appears in a channel: Twitch sends this
/// (USERSTATE) on joining and after each message we send, and never echoes
/// our own messages back, so it's what our own lines are drawn from.
/// GLOBALUSERSTATE carries the same plus the user's id, with no channel.</summary>
public sealed record ChatUserState(
    string? Channel,
    string? UserId,
    string? DisplayName,
    string? Color,
    IReadOnlyList<string> Badges)
{
    public static ChatUserState? FromIrc(IrcMessage irc)
    {
        if (irc.Command is not ("USERSTATE" or "GLOBALUSERSTATE")) return null;
        var channel = irc.Command == "USERSTATE" && irc.Params.Count > 0 ? irc.Params[0].TrimStart('#') : null;
        return new ChatUserState(channel, irc.Tag("user-id"), irc.Tag("display-name"), irc.Tag("color"),
            irc.Tag("badges")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            IsMod = irc.Tag("mod") == "1",
            MessageId = irc.Command == "USERSTATE" ? irc.Tag("id") : null,
        };
    }

    /// <summary>The <c>mod</c> tag: a moderator here, whichever badge they
    /// show (a lead moderator's is <c>lead_moderator</c>).</summary>
    public bool IsMod { get; init; }

    /// <summary>The USERSTATE that answers a message we sent carries that
    /// message's id: our own line, drawn locally, has none until then.</summary>
    public string? MessageId { get; init; }

    /// <summary>May use the mod tools here: the broadcaster or a moderator.</summary>
    public bool CanModerate => IsMod || ChatRoles.IsModerator(Badges) || ChatRoles.Has(Badges, "broadcaster");

    public bool IsBroadcaster => ChatRoles.Has(Badges, "broadcaster");
}
