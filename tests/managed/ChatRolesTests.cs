// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using Xunit;

namespace StreamEdge.Tests;

public class ChatRolesTests
{
    [Theory]
    [InlineData("moderator/1", ChatterRole.Moderator)]
    [InlineData("lead_moderator/1", ChatterRole.Moderator)]
    [InlineData("vip/1", ChatterRole.Vip)]
    [InlineData("global_mod/1", ChatterRole.Staff)]
    [InlineData("subscriber/12", ChatterRole.User)]
    public void RolesComeFromBadges(string badge, ChatterRole role) =>
        Assert.Equal(role, ChatRoles.RoleOf("amy", "xqc", [badge]));

    [Fact]
    public void TheChannelsOwnerIsTheBroadcasterWithoutTheBadge() =>
        Assert.Equal(ChatterRole.Broadcaster, ChatRoles.RoleOf("XQC", "xqc", []));

    [Fact]
    public void OnlyTheBroadcasterRemovesModeratorsAndNobodyDeletesTheirMessages()
    {
        Assert.False(ChatRoles.Allows(ModPower.Remove, asBroadcaster: false, ChatterRole.Moderator));
        Assert.True(ChatRoles.Allows(ModPower.Remove, asBroadcaster: true, ChatterRole.Moderator));
        // Helix: "The message must not belong to another moderator."
        Assert.False(ChatRoles.Allows(ModPower.Delete, asBroadcaster: false, ChatterRole.Moderator));
        Assert.False(ChatRoles.Allows(ModPower.Delete, asBroadcaster: true, ChatterRole.Moderator));
    }

    [Fact]
    public void AModeratorDeletesTheirOwnMessagesButTheBroadcasterCant()
    {
        Assert.True(ChatRoles.Allows(ModPower.Delete, asBroadcaster: false, ChatterRole.Moderator, own: true));
        Assert.False(ChatRoles.Allows(ModPower.Remove, asBroadcaster: false, ChatterRole.Moderator, own: true));
        // Helix: "The message must not belong to the broadcaster."
        Assert.False(ChatRoles.Allows(ModPower.Delete, asBroadcaster: true, ChatterRole.Broadcaster, own: true));
    }

    [Fact]
    public void TheUserstateAfterSendingNamesTheMessage()
    {
        var sent = ChatUserState.FromIrc(IrcMessage.Parse("@badges=moderator/1;id=abc-123;mod=1 :tmi.twitch.tv USERSTATE #xqc"))!;
        Assert.Equal("abc-123", sent.MessageId);
        Assert.Null(ChatUserState.FromIrc(IrcMessage.Parse("@badges=;mod=0 :tmi.twitch.tv USERSTATE #xqc"))!.MessageId);
    }

    [Fact]
    public void NobodyActsOnTheBroadcasterAndStaffCantBeRemoved()
    {
        Assert.False(ChatRoles.Allows(ModPower.Delete, asBroadcaster: false, ChatterRole.Broadcaster));
        Assert.False(ChatRoles.Allows(ModPower.Remove, asBroadcaster: true, ChatterRole.Staff));
        Assert.True(ChatRoles.Allows(ModPower.Delete, asBroadcaster: false, ChatterRole.Staff));
        Assert.True(ChatRoles.Allows(ModPower.Remove, asBroadcaster: false, ChatterRole.Vip));
    }

    [Theory]
    [InlineData("@badges=lead_moderator/1;mod=1 :tmi.twitch.tv USERSTATE #xqc", true, false)]
    [InlineData("@badges=;mod=1 :tmi.twitch.tv USERSTATE #xqc", true, false)]
    [InlineData("@badges=broadcaster/1;mod=0 :tmi.twitch.tv USERSTATE #xqc", true, true)]
    [InlineData("@badges=vip/1;mod=0 :tmi.twitch.tv USERSTATE #xqc", false, false)]
    public void LeadModeratorsGetTheModTools(string raw, bool canModerate, bool broadcaster)
    {
        var state = ChatUserState.FromIrc(IrcMessage.Parse(raw))!;
        Assert.Equal(canModerate, state.CanModerate);
        Assert.Equal(broadcaster, state.IsBroadcaster);
    }
}
