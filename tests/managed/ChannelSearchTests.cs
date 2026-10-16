// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Streams;
using Xunit;

namespace StreamEdge.Tests;

public class ChannelSearchTests
{
    [Theory]
    [InlineData("  pixelperegrine ", "pixelperegrine")]
    [InlineData("#pixelperegrine", "pixelperegrine")]
    [InlineData("@PixelPeregrine", "PixelPeregrine")]
    [InlineData("https://www.twitch.tv/pixelperegrine", "pixelperegrine")]
    [InlineData("twitch.tv/pixelperegrine/videos?filter=all", "pixelperegrine")]
    public void NormalizeStripsWhatSurroundsAName(string typed, string expected) =>
        Assert.Equal(expected, ChannelSearch.Normalize(typed));

    [Theory]
    [InlineData("pixel_peregrine42", true)]
    [InlineData("pixel peregrine", false)]
    [InlineData("pixel-peregrine", false)]
    [InlineData("", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz", false)] // 26: one too many
    public void LoginsAreLettersDigitsAndUnderscores(string text, bool valid) =>
        Assert.Equal(valid, ChannelSearch.IsValidLogin(text));

    [Fact]
    public void ExactNameFirstThenPrefixesThenLiveBeforeOffline()
    {
        var found = ChannelSearch.Rank(
        [
            new("amberpixel", "AmberPixel", IsLive: true),
            new("pixelperegrineclips", "PixelPeregrineClips", IsLive: false),
            new("pixelpals", "PixelPals", IsLive: true),
            new("pixel", "Pixel", IsLive: false),
        ], "pixel", max: 10);
        Assert.Equal(["pixel", "pixelpals", "pixelperegrineclips", "amberpixel"], found.Select(s => s.Login));
    }

    [Fact]
    public void DuplicatesKeepTheFirstButBorrowWhatLaterOnesKnow()
    {
        var avatar = new Uri("https://img.example/a.png");
        var found = ChannelSearch.Rank(
        [
            new("pixelpals", "PixelPals"),
            new("pixelpals", "pixelpals", IsLive: true, Game: "Tetris", ProfileImage: avatar),
        ], "pix", max: 10);
        var only = Assert.Single(found);
        Assert.Equal("PixelPals", only.DisplayName);
        Assert.True(only.IsLive);
        Assert.Equal("Tetris", only.Detail);
        Assert.Equal(avatar, only.ProfileImage);
    }

    [Fact]
    public void DetailSaysLiveGameOfflineOrNothing()
    {
        Assert.Equal("Tetris", new ChannelSuggestion("a", "A", true, "Tetris").Detail);
        Assert.Equal("Live", new ChannelSuggestion("a", "A", true).Detail);
        Assert.Equal("Offline", new ChannelSuggestion("a", "A", false).Detail);
        Assert.Equal("", new ChannelSuggestion("a", "A").Detail);
        Assert.Equal("Join this channel", new ChannelSuggestion("a", "a") { IsTyped = true }.Detail);
    }

    [Fact]
    public async Task HelixSearchReadsChannelsWithSmallAvatars()
    {
        string? asked = null;
        using var http = new HttpClient(new FakeHttp(r =>
        {
            asked = r.RequestUri!.AbsoluteUri;
            return FakeHttp.Json("""
                {"data":[{"broadcaster_login":"pixelpals","display_name":"PixelPals","is_live":true,"game_name":"Tetris",
                  "thumbnail_url":"https://static-cdn.jtvnw.net/u/abc-profile_image-300x300.png","title":"hi","id":"1"},
                 {"broadcaster_login":"pixel","display_name":"","is_live":false,"game_name":"","thumbnail_url":""}]}
                """);
        }));
        var found = await new HelixClient(http, "cid", "tok").SearchChannelsAsync("pix el", 8, CancellationToken.None);

        Assert.Contains("query=pix%20el", asked);
        Assert.Equal(["pixelpals", "pixel"], found.Select(f => f.Login));
        Assert.Equal("https://static-cdn.jtvnw.net/u/abc-profile_image-70x70.png", found[0].ProfileImage?.ToString());
        Assert.True(found[0].IsLive);
        // No display name: the login stands in.
        Assert.Equal("pixel", found[1].DisplayName);
        Assert.Null(found[1].ProfileImage);
    }

    [Fact]
    public void OfflineOrUnknownStreamsHeaderIsJustTheName()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal("Pixel", new LiveStream("pixel", "Pixel", "", "", 0, now, IsLive: false).Header(now));
        Assert.Equal("Pixel", new LiveStream("pixel", "Pixel", "", "", -1, now).Header(now));
    }
}
