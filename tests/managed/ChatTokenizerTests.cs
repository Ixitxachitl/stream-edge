// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using StreamEdge.Chat.Emotes;
using Xunit;

namespace StreamEdge.Tests;

public class ChatTokenizerTests
{
    private static readonly Uri Img = new("https://example.invalid/e.png");

    private static readonly Dictionary<string, Emote> ThirdParty = new()
    {
        ["catJAM"] = new("catJAM", Img, EmoteProvider.SevenTv),
        ["PETPET"] = new("PETPET", Img, EmoteProvider.SevenTv, ZeroWidth: true),
        ["SoSnowy"] = new("SoSnowy", Img, EmoteProvider.Bttv, ZeroWidth: true),
        ["OMEGALUL"] = new("OMEGALUL", Img, EmoteProvider.Ffz),
    };

    private static Emote? Lookup(string code) => ThirdParty.GetValueOrDefault(code);

    private static IReadOnlyList<ChatToken> Tokens(string line, string? self = null) =>
        ChatTokenizer.Tokenize(ChatMessage.FromIrc(IrcMessage.Parse(line), DateTimeOffset.UnixEpoch)!, Lookup, self);

    private static string Shape(IReadOnlyList<ChatToken> tokens) =>
        string.Join("|", tokens.Select(t => t.Kind switch
        {
            ChatTokenKind.Emote => $"E:{t.Text}" + string.Concat(t.Overlays.Select(o => "+" + o.Code)),
            ChatTokenKind.Link => $"L:{t.Text}",
            ChatTokenKind.Mention => $"M:{t.Text}",
            _ => $"T:{t.Text}",
        }));

    [Fact]
    public void PlainTextIsOneToken() =>
        Assert.Equal("T:hello there", Shape(Tokens(":a!a@a PRIVMSG #c :hello there")));

    [Fact]
    public void TwitchEmoteAfterEmojiLandsOnTheRightWord()
    {
        var tokens = Tokens("@emotes=25:5-9 :a!a@a PRIVMSG #c :\U0001F600\U0001F600 x Kappa!");
        Assert.Equal("T:\U0001F600\U0001F600 x |E:Kappa|T:!", Shape(tokens));
        var emote = tokens[1].Emote!;
        Assert.Equal(EmoteProvider.Twitch, emote.Provider);
        Assert.Equal(new Uri("https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/2.0"), emote.ImageUrl);
    }

    [Fact]
    public void TwitchRangesWinOverThirdPartyCodes()
    {
        // A channel could add a 7TV "catJAM"; the native emote (by range) still wins.
        var tokens = Tokens("@emotes=999:0-5 :a!a@a PRIVMSG #c :catJAM catJAM");
        Assert.Equal("E:catJAM|T: |E:catJAM", Shape(tokens));
        Assert.Equal(EmoteProvider.Twitch, tokens[0].Emote!.Provider);
        Assert.Equal(EmoteProvider.SevenTv, tokens[2].Emote!.Provider);
    }

    [Fact]
    public void ThirdPartyMatchesWholeWordsOnly() =>
        Assert.Equal("T:catJAMs xcatJAM |E:catJAM",
            Shape(Tokens(":a!a@a PRIVMSG #c :catJAMs xcatJAM catJAM")));

    [Fact]
    public void ZeroWidthAttachesToPreviousEmote()
    {
        var tokens = Tokens(":a!a@a PRIVMSG #c :look catJAM PETPET SoSnowy ok");
        Assert.Equal("T:look |E:catJAM+PETPET+SoSnowy|T: ok", Shape(tokens));
    }

    [Fact]
    public void ZeroWidthOverTwitchEmote() =>
        Assert.Equal("E:Kappa+SoSnowy", Shape(Tokens("@emotes=25:0-4 :a!a@a PRIVMSG #c :Kappa SoSnowy")));

    [Fact]
    public void ZeroWidthWithoutBaseIsAnOrdinaryEmote()
    {
        Assert.Equal("E:PETPET|T: hi", Shape(Tokens(":a!a@a PRIVMSG #c :PETPET hi")));
        Assert.Equal("E:catJAM|T: hi |E:PETPET", Shape(Tokens(":a!a@a PRIVMSG #c :catJAM hi PETPET")));
    }

    [Fact]
    public void LinksAreDetectedAndTrailingPunctuationLeftOut()
    {
        var tokens = Tokens(":a!a@a PRIVMSG #c :see https://www.twitch.tv/videos/2900045691. and HTTP://x.y ok ftp://no");
        Assert.Equal("T:see |L:https://www.twitch.tv/videos/2900045691|T:. and |L:HTTP://x.y|T: ok ftp://no", Shape(tokens));
        Assert.Equal(new Uri("https://www.twitch.tv/videos/2900045691"), tokens[1].Url);
    }

    [Fact]
    public void MentionsAreDetectedAndSelfFlagged()
    {
        var tokens = Tokens(":a!a@a PRIVMSG #c :@Moss_1, hi @bob @ and a@b", self: "moss_1");
        Assert.Equal("M:@Moss_1|T:, hi |M:@bob|T: @ and a@b", Shape(tokens));
        Assert.Equal("Moss_1", tokens[0].MentionLogin);
        Assert.True(tokens[0].IsSelfMention);
        Assert.False(tokens[2].IsSelfMention);
    }

    [Fact]
    public void NoLookupStillTokenizes()
    {
        var m = ChatMessage.FromIrc(IrcMessage.Parse(":a!a@a PRIVMSG #c :catJAM"), DateTimeOffset.UnixEpoch)!;
        Assert.Equal("T:catJAM", Shape(ChatTokenizer.Tokenize(m, null, null)));
    }

    [Fact]
    public void TextRoundTrips()
    {
        const string line = "@emotes=25:2-6 :a!a@a PRIVMSG #c :\U0001F600 Kappa  catJAM   https://a.b/c @x end";
        var m = ChatMessage.FromIrc(IrcMessage.Parse(line), DateTimeOffset.UnixEpoch)!;
        Assert.Equal(m.Text, string.Concat(ChatTokenizer.Tokenize(m, Lookup, null).Select(t => t.Text)));
    }
}
