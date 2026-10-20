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
    public void OnlyTheBroadcasterActsOnModerators()
    {
        Assert.NotNull(ChatRoles.WhyNot(ModPower.Remove, asBroadcaster: false, ChatterRole.Moderator));
        Assert.NotNull(ChatRoles.WhyNot(ModPower.Delete, asBroadcaster: false, ChatterRole.Moderator));
        Assert.Null(ChatRoles.WhyNot(ModPower.Remove, asBroadcaster: true, ChatterRole.Moderator));
        Assert.Null(ChatRoles.WhyNot(ModPower.Delete, asBroadcaster: true, ChatterRole.Moderator));
    }

    [Fact]
    public void NobodyActsOnTheBroadcasterAndStaffCantBeRemoved()
    {
        Assert.NotNull(ChatRoles.WhyNot(ModPower.Delete, asBroadcaster: false, ChatterRole.Broadcaster));
        Assert.NotNull(ChatRoles.WhyNot(ModPower.Remove, asBroadcaster: true, ChatterRole.Staff));
        Assert.Null(ChatRoles.WhyNot(ModPower.Delete, asBroadcaster: false, ChatterRole.Staff));
        Assert.Null(ChatRoles.WhyNot(ModPower.Remove, asBroadcaster: false, ChatterRole.Vip));
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
