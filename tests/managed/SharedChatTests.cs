// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using StreamEdge.Streams;
using Xunit;

namespace StreamEdge.Tests;

public class SharedChatTests
{
    private static ChatMessage Parse(string line) => ChatMessage.FromIrc(IrcMessage.Parse(line), DateTimeOffset.Now)!;

    [Fact]
    public void ALineFromAnotherChannelKeepsItsChannelIdAndBadgesThere()
    {
        var m = Parse("@badges=;source-badges=subscriber/12,vip/1;source-room-id=222;source-id=src-1;room-id=111;id=here-1;"
                      + "display-name=OrbitWren :orbitwren!orbitwren@orbitwren.tmi.twitch.tv PRIVMSG #pixelperegrine :hi from there");
        Assert.Equal("222", m.SourceRoomId);
        Assert.Equal("src-1", m.SourceId);
        Assert.Equal("here-1", m.Id);
        Assert.Equal(["subscriber/12", "vip/1"], m.ShownBadges);
        Assert.True(m.IsFromOtherRoom("111"));
        Assert.False(m.IsFromOtherRoom("222"));
    }

    [Fact]
    public void OutsideASessionThereIsNoSourceAndTheBadgesAreTheUsual()
    {
        var m = Parse("@badges=moderator/1;room-id=111;id=x :bob!bob@bob.tmi.twitch.tv PRIVMSG #pixelperegrine :hello");
        Assert.Null(m.SourceRoomId);
        Assert.Null(m.SourceBadges);
        Assert.Equal(["moderator/1"], m.ShownBadges);
        Assert.False(m.IsFromOtherRoom("111"));
    }

    [Fact]
    public void ASharedChatNoticeIsTheEventItWrapped()
    {
        var m = Parse("@msg-id=sharedchatnotice;source-msg-id=resub;source-room-id=222;login=orbitwren;display-name=OrbitWren;"
                      + @"system-msg=OrbitWren\ssubscribed\sfor\s12\smonths! :tmi.twitch.tv USERNOTICE #pixelperegrine");
        Assert.Equal("resub", m.MsgId);
        Assert.Equal(ChatMessageKind.System, m.Kind);
        Assert.Equal("222", m.SourceRoomId);
    }

    [Fact]
    public void ModerationNamesTheChannelItHappenedIn()
    {
        var ban = ChatModeration.FromIrc(IrcMessage.Parse(
            "@room-id=111;source-room-id=222;target-user-id=9 :tmi.twitch.tv CLEARCHAT #pixelperegrine :orbitwren"))!;
        Assert.Equal("222", ban.SourceRoomId);
        var plain = ChatModeration.FromIrc(IrcMessage.Parse("@room-id=111 :tmi.twitch.tv CLEARCHAT #pixelperegrine"))!;
        Assert.Null(plain.SourceRoomId);
    }

    [Fact]
    public async Task HelixSessionListsTheHostFirst()
    {
        using var http = new HttpClient(new FakeHttp(_ => FakeHttp.Json("""
            {"data":[{"session_id":"s","host_broadcaster_id":"333",
              "participants":[{"broadcaster_id":"111"},{"broadcaster_id":"222"},{"broadcaster_id":"333"}]}]}
            """)));
        var helix = new HelixClient(http, "cid", "tok");
        Assert.Equal(["333", "111", "222"], await helix.GetSharedChatAsync("111", CancellationToken.None));

        using var none = new HttpClient(new FakeHttp(_ => FakeHttp.Json("""{"data":[]}""")));
        Assert.Empty(await new HelixClient(none, "cid", "tok").GetSharedChatAsync("111", CancellationToken.None));
    }

    [Fact]
    public void ChannelLookupByIdReadsNameAndAvatar()
    {
        var info = ChannelInfo.Parse("""
            {"data":{"user":{"login":"cometcrumb","displayName":"CometCrumb","offlineImageURL":null,
              "profileImageURL":"https://static-cdn.jtvnw.net/u/c-profile_image-70x70.png"}}}
            """);
        Assert.Equal("CometCrumb", info?.DisplayName);
        Assert.Equal("https://static-cdn.jtvnw.net/u/c-profile_image-70x70.png", info?.ProfileImage?.ToString());
    }
}
