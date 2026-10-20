// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Chat;

/// <summary>What a moderator does to someone: take a message away, or the
/// person (a timeout, ban or warning).</summary>
public enum ModPower
{
    Delete,
    Remove,
}

/// <summary>Who someone is in a channel, from their badges, and what Twitch
/// lets a moderator do to them.
///
/// A lead moderator wears <c>lead_moderator/1</c> in place of
/// <c>moderator/1</c> (a user shows one of the two, never both); over chat
/// they have a moderator's powers, so they count as one here.</summary>
public static class ChatRoles
{
    /// <summary>Twitch deletes only messages younger than this.</summary>
    public static readonly TimeSpan DeleteWindow = TimeSpan.FromHours(6);

    public static bool Has(IReadOnlyList<string> badges, string set) =>
        badges.Any(b => b.StartsWith(set + "/", StringComparison.Ordinal));

    public static bool IsModerator(IReadOnlyList<string> badges) =>
        Has(badges, "moderator") || Has(badges, "lead_moderator");

    public static bool IsBroadcaster(string? login, string channel, IReadOnlyList<string> badges) =>
        Has(badges, "broadcaster") || string.Equals(login, channel, StringComparison.OrdinalIgnoreCase);

    public static ChatterRole RoleOf(string? login, string channel, IReadOnlyList<string> badges) =>
        IsBroadcaster(login, channel, badges) ? ChatterRole.Broadcaster
        : IsModerator(badges) ? ChatterRole.Moderator
        : Has(badges, "vip") ? ChatterRole.Vip
        : Has(badges, "staff") || Has(badges, "admin") || Has(badges, "global_mod") ? ChatterRole.Staff
        : ChatterRole.User;

    /// <summary>Whether Twitch allows <paramref name="power"/> over someone
    /// of <paramref name="target"/>'s role (<paramref name="own"/>: over the
    /// user's own message). As Helix has it, a message can't be deleted if
    /// it's the broadcaster's (even by the broadcaster) or another
    /// moderator's, but a moderator can delete their own. Only the
    /// broadcaster times out or bans moderators; staff can't be.</summary>
    public static bool Allows(ModPower power, bool asBroadcaster, ChatterRole target, bool own = false) => (power, target) switch
    {
        (_, ChatterRole.Broadcaster) => false,
        (ModPower.Delete, _) when own => true,
        (ModPower.Remove, _) when own => false,
        (ModPower.Delete, ChatterRole.Moderator) => false,
        (ModPower.Remove, ChatterRole.Moderator) => asBroadcaster,
        (ModPower.Remove, ChatterRole.Staff) => false,
        _ => true,
    };
}
