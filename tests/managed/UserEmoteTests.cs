// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using StreamEdge.Chat.Emotes;
using Xunit;

namespace StreamEdge.Tests;

public class UserEmoteTests
{
    [Fact]
    public void ParsesUserEmotesWithOwnersAndAnimatedUrls()
    {
        var (emotes, cursor) = TwitchUserEmotes.Parse("""
            {"data":[
              {"id":"25","name":"Kappa","emote_type":"globals","emote_set_id":"0","owner_id":"0","format":["static"],"scale":["1.0","2.0","3.0"],"theme_mode":["light","dark"]},
              {"id":"emotesv2_abc","name":"xqcL","emote_type":"subscriptions","emote_set_id":"1","owner_id":"71092938","format":["static","animated"]}
            ],
            "template":"https://static-cdn.jtvnw.net/emoticons/v2/{{id}}/{{format}}/{{theme_mode}}/{{scale}}",
            "pagination":{"cursor":"next-page"}}
            """);

        Assert.Equal("next-page", cursor);
        Assert.Equal(["Kappa", "xqcL"], emotes.Select(e => e.Emote.Code));
        Assert.Equal("https://static-cdn.jtvnw.net/emoticons/v2/25/static/dark/2.0", emotes[0].Emote.ImageUrl.AbsoluteUri);
        Assert.Equal("https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_abc/animated/dark/2.0", emotes[1].Emote.ImageUrl.AbsoluteUri);
        Assert.Equal("71092938", emotes[1].OwnerId);
        Assert.Equal(EmoteProvider.Twitch, emotes[1].Emote.Provider);
    }

    [Fact]
    public void LastPageHasNoCursor() =>
        Assert.Null(TwitchUserEmotes.Parse("""{"data":[],"pagination":{}}""").Cursor);

    [Fact]
    public void ParsesOwners()
    {
        var owners = TwitchUserEmotes.ParseOwners("""
            {"data":[{"id":"71092938","login":"xqc","display_name":"xQc","profile_image_url":"https://static-cdn.jtvnw.net/x.png"}]}
            """);
        var owner = Assert.Single(owners);
        Assert.Equal("xQc", owner.DisplayName);
        Assert.NotNull(owner.ProfileImage);
    }

    [Fact]
    public void UserStateCarriesHowWeAppear()
    {
        var state = ChatUserState.FromIrc(IrcMessage.Parse(
            "@badges=moderator/1,subscriber/12;color=#1E90FF;display-name=Me;emote-sets=0,33 :tmi.twitch.tv USERSTATE #chan"));
        Assert.NotNull(state);
        Assert.Equal("chan", state.Channel);
        Assert.Equal("Me", state.DisplayName);
        Assert.Equal("#1E90FF", state.Color);
        Assert.Equal(["moderator/1", "subscriber/12"], state.Badges);
        Assert.Equal(["0", "33"], state.EmoteSets);
        Assert.True(state.IsSubscriber);

        var global = ChatUserState.FromIrc(IrcMessage.Parse("@user-id=123;display-name=Me :tmi.twitch.tv GLOBALUSERSTATE"));
        Assert.Null(global!.Channel);
        Assert.Equal("123", global.UserId);
    }
}
