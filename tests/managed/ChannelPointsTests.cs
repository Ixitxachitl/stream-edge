// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using StreamEdge.Streams;
using Xunit;

namespace StreamEdge.Tests;

public class ChannelPointsTests
{
    // Trimmed from a real signed-out response (lirik), plus a balance.
    private const string Json = """
        {"data":{"channel":{"communityPointsSettings":{"name":"PotatoCoin","isEnabled":true,
          "image":{"url":"https://static-cdn.jtvnw.net/channel-points-icons/1/icon-1.png","url2x":"https://static-cdn.jtvnw.net/channel-points-icons/1/icon-2.png"},
          "customRewards":[
            {"id":"vote","title":"Sub Vote ","cost":5000,"backgroundColor":"#392E5C","isEnabled":true,"isPaused":false,"isInStock":true,
             "isUserInputRequired":true,"prompt":"!vote Game","image":null,
             "defaultImage":{"url":"https://static-cdn.jtvnw.net/custom-reward-images/default-1.png","url2x":"https://static-cdn.jtvnw.net/custom-reward-images/default-2.png"}},
            {"id":"off","title":"Hidden","cost":1,"backgroundColor":null,"isEnabled":false,"isPaused":false,"isInStock":true,
             "isUserInputRequired":false,"prompt":null,"image":null,"defaultImage":null},
            {"id":"hydrate","title":"Hydrate","cost":150,"backgroundColor":null,"isEnabled":true,"isPaused":true,"isInStock":true,
             "isUserInputRequired":false,"prompt":"","image":{"url":"https://static-cdn.jtvnw.net/custom-reward-images/1/h-1.png","url2x":null},"defaultImage":null}
          ]},
          "self":{"communityPoints":{"balance":12345}}}}}
        """;

    [Fact]
    public void ParsesRewardsCheapestFirstWithoutDisabledOnes()
    {
        var points = ChannelPoints.Parse(Json)!;
        Assert.Equal("PotatoCoin", points.Name);
        Assert.Equal(12345, points.Balance);
        Assert.EndsWith("icon-1.png", points.Icon!.AbsoluteUri);
        Assert.Equal(["hydrate", "vote"], points.Rewards.Select(r => r.Id));

        var hydrate = points.Rewards[0];
        Assert.False(hydrate.IsAvailable); // paused
        Assert.Null(hydrate.Prompt);
        Assert.EndsWith("h-1.png", hydrate.Image!.AbsoluteUri);

        var vote = points.Reward("vote")!;
        Assert.Equal("Sub Vote", vote.Title);
        Assert.True(vote.RequiresInput);
        Assert.Equal("!vote Game", vote.Prompt);
        Assert.EndsWith("default-1.png", vote.Image!.AbsoluteUri); // falls back to the default image
        Assert.Null(points.Reward("missing"));
    }

    [Fact]
    public void SignedOutHasNoBalanceAndUnnamedPointsGetTheDefaultName()
    {
        var points = ChannelPoints.Parse("""
            {"data":{"channel":{"communityPointsSettings":{"name":null,"isEnabled":true,"image":null,"customRewards":[]},"self":null}}}
            """)!;
        Assert.Null(points.Balance);
        Assert.Equal(ChannelPoints.DefaultName, points.Name);
        Assert.Null(points.Icon);
    }

    [Theory]
    [InlineData("""{"data":{"channel":null}}""")]
    [InlineData("""{"data":{"channel":{"communityPointsSettings":{"isEnabled":false}}}}""")]
    [InlineData("""{"errors":[{"message":"service timeout"}]}""")]
    [InlineData("not json")]
    public void MissingOrOffIsNull(string json) => Assert.Null(ChannelPoints.Parse(json));

    [Theory]
    [InlineData(950, "950")]
    [InlineData(12_345, "12.3K")]
    [InlineData(999_999, "999K")]
    [InlineData(1_250_000, "1.2M")]
    public void ShortensLikeTwitch(int points, string expected) =>
        Assert.Equal(expected, ChannelPoints.Short(points).Replace(',', '.'));

    [Fact]
    public void ReadsTheRewardTag()
    {
        var m = ChatMessage.FromIrc(IrcMessage.Parse(
            "@custom-reward-id=vote;display-name=Alice;id=1;user-id=2 :alice!alice@alice.tmi.twitch.tv PRIVMSG #chan :Elden Ring"),
            DateTimeOffset.UnixEpoch)!;
        Assert.Equal("vote", m.CustomRewardId);
    }
}
