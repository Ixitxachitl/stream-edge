// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Playback;
using Xunit;

namespace StreamEdge.Tests;

public class PlaybackTests
{
    [Fact]
    public void ParsesAccessToken()
    {
        var token = TwitchPlayback.ParseAccessToken(
            """{"data":{"streamPlaybackAccessToken":{"value":"{\"channel\":\"x\"}","signature":"abc123","__typename":"PlaybackAccessToken"}}}""");
        Assert.NotNull(token);
        Assert.Equal("""{"channel":"x"}""", token.Value);
        Assert.Equal("abc123", token.Signature);
    }

    [Theory]
    [InlineData("""{"data":{"streamPlaybackAccessToken":null}}""")]
    [InlineData("""{"errors":[{"message":"bad"}]}""")]
    [InlineData("not json")]
    public void UnusableResponsesGiveNull(string json) =>
        Assert.Null(TwitchPlayback.ParseAccessToken(json));

    [Fact]
    public void UsherUrlCarriesEscapedTokenAndSignature()
    {
        var url = TwitchPlayback.UsherUrl("NintenDope503", new("{\"a\":1}", "sig"));
        Assert.Equal("usher.ttvnw.net", url.Host);
        Assert.Equal("/api/channel/hls/nintendope503.m3u8", url.AbsolutePath);
        Assert.Contains("sig=sig", url.Query);
        Assert.Contains("token=%7B%22a%22%3A1%7D", url.Query);
    }
}
