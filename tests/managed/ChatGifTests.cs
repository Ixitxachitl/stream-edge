// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using Xunit;

namespace StreamEdge.Tests;

public class ChatGifTests
{
    // From Twitch's IRC reference, with the query string the docs truncate.
    private const string Url =
        "https://media4.giphy.com/media/joSNxeswxuc74Juo8X/giphy.gif?cid=e1bb72ffabc&ep=v1_gifs_search&rid=giphy.gif&ct=g";

    private static ChatMessage Chat(string tags, string text) =>
        ChatMessage.FromIrc(IrcMessage.Parse($"@{tags} :alice!alice@alice.tmi.twitch.tv PRIVMSG #chan :{text}"),
            DateTimeOffset.UnixEpoch)!;

    [Fact]
    public void ReadsTheGifsTagWithItsQueryStringIntact()
    {
        var m = Chat($"display-name=Alice;gifs=0-33|joSNxeswxuc74Juo8X|{Url};id=1", "[Y A Y Yes GIF by Djemilah Birnie]");

        var gif = Assert.Single(m.Gifs);
        Assert.Equal("joSNxeswxuc74Juo8X", gif.Id);
        Assert.Equal(Url, gif.Url.OriginalString);
        Assert.Equal((0, 34), (gif.Start, gif.Length));
    }

    [Fact]
    public void SeveralGifsAndAnEmojiBeforeThemLineUp()
    {
        // "😀 " is 2 code points but 3 UTF-16 chars: positions shift by one.
        var text = "😀 [A GIF] and [B GIF]";
        var m = Chat($"gifs=2-8|a|{Url},14-20|b|{Url}", text);

        Assert.Equal(["[A GIF]", "[B GIF]"], m.Gifs.Select(g => text.Substring(g.Start, g.Length)));
        Assert.Equal(["a", "b"], m.Gifs.Select(g => g.Id));
    }

    [Theory]
    [InlineData("gifs=0-99|x|https://media.giphy.com/x.gif")] // past the end
    [InlineData("gifs=0-6|x|http://media.giphy.com/x.gif")]   // not https
    [InlineData("gifs=0-6|x")]                                // no URL
    [InlineData("gifs=nonsense")]
    public void BadEntriesAreSkipped(string tags) => Assert.Empty(Chat(tags, "[A GIF]").Gifs);

    [Fact]
    public void TokenizesAsAGifOrAsItsTitleWhenGifsAreOff()
    {
        var m = Chat($"gifs=4-10|a|{Url}", "lol [A GIF] wow");

        var on = ChatTokenizer.Tokenize(m, null, null);
        var gif = Assert.Single(on, t => t.Kind == ChatTokenKind.Gif);
        Assert.Equal("[A GIF]", gif.Text);
        Assert.Equal(Url, gif.Url!.OriginalString);
        Assert.Equal("lol [A GIF] wow", string.Concat(on.Select(t => t.Text)));

        var off = ChatTokenizer.Tokenize(m, null, null, gifs: false);
        Assert.DoesNotContain(off, t => t.Kind == ChatTokenKind.Gif);
        Assert.Equal("lol [A GIF] wow", string.Concat(off.Select(t => t.Text)));
    }
}
