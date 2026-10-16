// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using Xunit;

namespace StreamEdge.Tests;

public class IrcMessageTests
{
    [Fact]
    public void ParsesTaggedPrivmsg()
    {
        var m = IrcMessage.Parse(
            "@badges=broadcaster/1,subscriber/0;color=#B65CFF;display-name=PixelPeregrine;tmi-sent-ts=1700000000000 " +
            ":pixelperegrine!pixelperegrine@pixelperegrine.tmi.twitch.tv PRIVMSG #pixelperegrine :!pokecatch Ultraball\r\n");

        Assert.Equal("PRIVMSG", m.Command);
        Assert.Equal("pixelperegrine", m.Nick);
        Assert.Equal(["#pixelperegrine", "!pokecatch Ultraball"], m.Params);
        Assert.Equal("PixelPeregrine", m.Tag("display-name"));
        Assert.Equal("#B65CFF", m.Tag("color"));
    }

    [Fact]
    public void UnescapesTagValues()
    {
        var m = IrcMessage.Parse(@"@system-msg=5\sraiders\sfrom\:\sX\\Y;empty= :tmi.twitch.tv USERNOTICE #c");
        Assert.Equal(@"5 raiders from; X\Y", m.Tag("system-msg"));
        Assert.Null(m.Tag("empty"));
    }

    [Fact]
    public void ParsesUntaggedPing()
    {
        var m = IrcMessage.Parse("PING :tmi.twitch.tv");
        Assert.Equal("PING", m.Command);
        Assert.Equal("tmi.twitch.tv", m.Trailing);
        Assert.Null(m.Prefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("@only-tags")]
    [InlineData(":only-prefix")]
    public void MalformedLinesDoNotThrow(string line) =>
        Assert.Equal("", IrcMessage.Parse(line).Command);

    [Fact]
    public void ActionBecomesActionKind()
    {
        var irc = IrcMessage.Parse(":bob!bob@bob PRIVMSG #c :\u0001ACTION waves\u0001");
        var chat = ChatMessage.FromIrc(irc, DateTimeOffset.UnixEpoch)!;
        Assert.Equal(ChatMessageKind.Action, chat.Kind);
        Assert.Equal("waves", chat.Text);
        Assert.Equal("c", chat.Channel);
    }

    [Fact]
    public void SentTimestampWinsOverReceiveTime()
    {
        var irc = IrcMessage.Parse("@tmi-sent-ts=1700000000000 :a!a@a PRIVMSG #c :hi");
        var chat = ChatMessage.FromIrc(irc, DateTimeOffset.UnixEpoch)!;
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), chat.Timestamp);
    }

    [Fact]
    public void BadgesSplitIntoSetVersionPairs()
    {
        var irc = IrcMessage.Parse("@badges=moderator/1,subscriber/12 :a!a@a PRIVMSG #c :hi");
        Assert.Equal(["moderator/1", "subscriber/12"], ChatMessage.FromIrc(irc, DateTimeOffset.UnixEpoch)!.Badges);
    }

    [Fact]
    public void PlumbingIsDropped() =>
        Assert.Null(ChatMessage.FromIrc(IrcMessage.Parse(":tmi.twitch.tv 001 justinfan1 :Welcome"), DateTimeOffset.UnixEpoch));

    [Fact]
    public void DefaultColourIsStablePerLogin() =>
        Assert.Equal(UserColors.For("someone", null), UserColors.For("someone", null));
}
