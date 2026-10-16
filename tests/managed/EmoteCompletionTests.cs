// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat.Emotes;
using Xunit;

namespace StreamEdge.Tests;

public class EmoteCompletionTests
{
    private static Emote E(string code, bool zeroWidth = false) =>
        new(code, new Uri($"https://img.example/{code}"), EmoteProvider.SevenTv, zeroWidth);

    [Theory]
    [InlineData("hi Kap", 6, 3, 3)]
    [InlineData("hi Kap", 4, 3, 1)]
    [InlineData("hi ", 3, 3, 0)]
    [InlineData("", 0, 0, 0)]
    [InlineData(":hyp", 4, 0, 4)]
    public void WordAtIsFromThePreviousSpaceToTheCaret(string text, int caret, int start, int length) =>
        Assert.Equal((start, length), EmoteCompletion.WordAt(text, caret));

    [Fact]
    public void PrefixMatchesRankRecentThenExactCaseThenShortest()
    {
        var all = new[] { E("KappaPride"), E("Keepo"), E("kappaHD"), E("Kappa"), E("OMEGALUL") };

        Assert.Equal(["Kappa", "Keepo", "KappaPride", "kappaHD"],
            EmoteCompletion.Rank(all, "K", [], contains: false, max: 10).Select(e => e.Code));
        // Recently used goes first.
        Assert.Equal("KappaPride", EmoteCompletion.Rank(all, "K", ["KappaPride"], false, 10)[0].Code);
        Assert.Equal(2, EmoteCompletion.Rank(all, "K", [], false, 2).Count);
        Assert.Empty(EmoteCompletion.Rank(all, "", [], false, 10));
    }

    [Fact]
    public void AfterAColonAnyPartMatchesButStartsComeFirst()
    {
        var all = new[] { E("OMEGALUL"), E("LULW"), E("Kappa") };
        Assert.Equal(["LULW", "OMEGALUL"], EmoteCompletion.Rank(all, "lul", [], contains: true, max: 10).Select(e => e.Code));
        Assert.DoesNotContain(EmoteCompletion.Rank(all, "lul", [], contains: false, max: 10), e => e.Code == "OMEGALUL");
    }

    [Fact]
    public void ChannelEmotesMatchWithoutTheirPrefix()
    {
        var all = new[] { E("pokiHype"), E("HypeLUL"), E("hyperClap"), E("xqcL"), E("catjam"), E("cvHazmat", zeroWidth: true) };
        // Whole-code matches first.
        Assert.Equal(["HypeLUL", "hyperClap", "pokiHype"],
            EmoteCompletion.Rank(all, "Hype", [], contains: false, max: 10).Select(e => e.Code));
        Assert.Equal(["hyperClap"], EmoteCompletion.Rank(all, "clap", [], contains: false, max: 10).Select(e => e.Code));
        Assert.Equal(["xqcL"], EmoteCompletion.Rank(all, "l", [], contains: false, max: 10).Select(e => e.Code));
        // Lowercase all the way through has no prefix to leave out.
        Assert.Empty(EmoteCompletion.Rank(all, "jam", [], contains: false, max: 10));
        Assert.Empty(EmoteCompletion.Rank(all, "Haz", [], contains: false, max: 10));
    }

    [Fact]
    public void ZeroWidthEmotesOnlyMatchFromTheirStart()
    {
        var all = new[] { E("cvHazmat", zeroWidth: true), E("Hazmat") };
        Assert.Equal(["Hazmat"], EmoteCompletion.Rank(all, "haz", [], contains: true, max: 10).Select(e => e.Code));
    }
}
