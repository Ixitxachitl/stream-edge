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
        var url = TwitchPlayback.UsherUrl("PixelPeregrine", new("{\"a\":1}", "sig"));
        Assert.Equal("usher.ttvnw.net", url.Host);
        Assert.Equal("/api/channel/hls/pixelperegrine.m3u8", url.AbsolutePath);
        Assert.Contains("sig=sig", url.Query);
        Assert.Contains("token=%7B%22a%22%3A1%7D", url.Query);
    }
}

public class ChannelInfoTests
{
    [Fact]
    public void ParsesOfflineBannerAndAvatar()
    {
        var info = StreamEdge.Streams.ChannelInfo.Parse("""
            {"data":{"user":{"login":"lofigirl","displayName":"LofiGirl",
              "offlineImageURL":"https://static-cdn.jtvnw.net/jtv_user_pictures/x-channel_offline_image-1920x1080.png",
              "profileImageURL":"https://static-cdn.jtvnw.net/jtv_user_pictures/x-profile_image-300x300.png"}}}
            """);
        Assert.NotNull(info);
        Assert.Equal("LofiGirl", info.DisplayName);
        Assert.EndsWith("1920x1080.png", info.OfflineImage!.AbsoluteUri);
        Assert.EndsWith("300x300.png", info.ProfileImage!.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{"data":{"user":null}}""")]
    [InlineData("""{"data":{"user":{"login":"x","displayName":"X","offlineImageURL":"","profileImageURL":null}}}""")]
    public void MissingPiecesAreNull(string json)
    {
        var info = StreamEdge.Streams.ChannelInfo.Parse(json);
        Assert.Null(info?.OfflineImage);
        Assert.Null(info?.ProfileImage);
    }
}
