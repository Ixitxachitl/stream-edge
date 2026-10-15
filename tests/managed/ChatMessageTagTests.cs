// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using Xunit;

namespace StreamEdge.Tests;

public class ChatMessageTagTests
{
    private static ChatMessage Chat(string line) =>
        ChatMessage.FromIrc(IrcMessage.Parse(line), DateTimeOffset.UnixEpoch)!;

    [Fact]
    public void ReadsTwitchTags()
    {
        var m = Chat(
            "@badge-info=subscriber/14;badges=subscriber/12;bits=100;color=#1E90FF;display-name=Alice;" +
            "emotes=;first-msg=1;id=885196de-cb67-427a-baa8-82f9b0fcd05f;msg-id=highlighted-message;" +
            "animation-id=rainbow-eclipse;reply-parent-display-name=Bob;reply-parent-msg-body=hello\\sthere;" +
            "reply-parent-user-login=bob;room-id=1337;tmi-sent-ts=1700000000000;user-id=4242 " +
            ":alice!alice@alice.tmi.twitch.tv PRIVMSG #chan :@Bob cheer100 hi");

        Assert.Equal("885196de-cb67-427a-baa8-82f9b0fcd05f", m.Id);
        Assert.Equal("4242", m.UserId);
        Assert.Equal("highlighted-message", m.MsgId);
        Assert.Equal("rainbow-eclipse", m.AnimationId);
        Assert.True(m.IsFirstMessage);
        Assert.True(m.IsReply);
        Assert.Equal("Bob", m.ReplyParentName);
        Assert.Equal("hello there", m.ReplyParentBody);
        Assert.Equal(100, m.Bits);
        Assert.Empty(m.TwitchEmotes);
    }

    [Fact]
    public void AbsentTagsDefault()
    {
        var m = Chat(":a!a@a PRIVMSG #c :hi");
        Assert.Null(m.Id);
        Assert.False(m.IsFirstMessage);
        Assert.False(m.IsReply);
        Assert.Equal(0, m.Bits);
        Assert.Empty(m.TwitchEmotes);
    }

    [Fact]
    public void EmoteRangesAreSortedAndUtf16()
    {
        // "Kappa Keepo Kappa": two ranges for 25, one for 1902, listed out of order.
        var m = Chat("@emotes=25:0-4,12-16/1902:6-10 :a!a@a PRIVMSG #c :Kappa Keepo Kappa");
        Assert.Equal(
            [new TwitchEmoteRange("25", 0, 5), new TwitchEmoteRange("1902", 6, 5), new TwitchEmoteRange("25", 12, 5)],
            m.TwitchEmotes);
        Assert.Equal(new Uri("https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/2.0"), m.TwitchEmotes[0].ImageUrl);
    }

    [Fact]
    public void EmoteRangesCountCodePointsNotChars()
    {
        // "😀 Kappa": the emoji is one code point but two UTF-16 chars, so
        // Twitch says 2-6 while the string index is 3.
        var m = Chat("@emotes=25:2-6 :a!a@a PRIVMSG #c :\U0001F600 Kappa");
        var r = Assert.Single(m.TwitchEmotes);
        Assert.Equal("Kappa", m.Text.Substring(r.Start, r.Length));
    }

    [Fact]
    public void EmoteThatIsItselfAnEmojiSpansTheSurrogatePair()
    {
        var m = Chat("@emotes=emotesv2_abc:0-0 :a!a@a PRIVMSG #c :\U0001F600 x");
        var r = Assert.Single(m.TwitchEmotes);
        Assert.Equal((0, 2), (r.Start, r.Length));
    }

    [Theory]
    [InlineData("25:0-99")]   // past the end
    [InlineData("25:3-1")]    // backwards
    [InlineData("25:x-y")]    // junk
    [InlineData("garbage")]
    public void BadEmoteRangesAreDropped(string tag) =>
        Assert.Empty(Chat($"@emotes={tag} :a!a@a PRIVMSG #c :Kappa").TwitchEmotes);

    [Fact]
    public void OverlappingEmoteRangesKeepTheFirst()
    {
        var m = Chat("@emotes=1:0-4/2:2-6 :a!a@a PRIVMSG #c :Kappa123");
        Assert.Equal("1", Assert.Single(m.TwitchEmotes).Id);
    }

    [Fact]
    public void ActionEmoteOffsetsAreRelativeToTheStrippedText()
    {
        var m = Chat("@emotes=25:6-10 :a!a@a PRIVMSG #c :\u0001ACTION waves Kappa\u0001");
        var r = Assert.Single(m.TwitchEmotes);
        Assert.Equal("Kappa", m.Text.Substring(r.Start, r.Length));
    }

    [Fact]
    public void UserNoticeEmotesShiftPastSystemText()
    {
        var m = Chat(@"@emotes=25:0-4;login=ann;msg-id=resub;system-msg=ann\ssubscribed\sfor\s3\smonths. " +
                     ":tmi.twitch.tv USERNOTICE #c :Kappa thanks");
        Assert.Equal("ann subscribed for 3 months. Kappa thanks", m.Text);
        Assert.Equal("resub", m.MsgId);
        var r = Assert.Single(m.TwitchEmotes);
        Assert.Equal("Kappa", m.Text.Substring(r.Start, r.Length));
    }

    [Fact]
    public void ClearMsgDeletesOneMessage()
    {
        var mod = ChatModeration.FromIrc(IrcMessage.Parse(
            "@login=ronni;room-id=;target-msg-id=abc-123-def;tmi-sent-ts=1642720582342 :tmi.twitch.tv CLEARMSG #dallas :HeyGuys"))!;
        Assert.Equal(ChatModerationKind.DeleteMessage, mod.Kind);
        Assert.Equal("dallas", mod.Channel);
        Assert.Equal("abc-123-def", mod.TargetMessageId);
        Assert.Equal("ronni", mod.TargetLogin);
        Assert.Equal("HeyGuys", mod.DeletedText);
    }

    [Fact]
    public void ClearChatWithUserIsATimeout()
    {
        var mod = ChatModeration.FromIrc(IrcMessage.Parse(
            "@ban-duration=350;room-id=12345678;target-user-id=87654321;tmi-sent-ts=1642719320727 :tmi.twitch.tv CLEARCHAT #dallas :ronni"))!;
        Assert.Equal(ChatModerationKind.ClearUser, mod.Kind);
        Assert.Equal("ronni", mod.TargetLogin);
        Assert.Equal("87654321", mod.TargetUserId);
        Assert.Equal(TimeSpan.FromSeconds(350), mod.Duration);
    }

    [Fact]
    public void ClearChatWithUserAndNoDurationIsABan()
    {
        var mod = ChatModeration.FromIrc(IrcMessage.Parse(
            "@room-id=12345678;target-user-id=87654321 :tmi.twitch.tv CLEARCHAT #dallas :ronni"))!;
        Assert.Equal(ChatModerationKind.ClearUser, mod.Kind);
        Assert.Null(mod.Duration);
    }

    [Fact]
    public void ClearChatAloneClearsEverything()
    {
        var mod = ChatModeration.FromIrc(IrcMessage.Parse("@room-id=12345678 :tmi.twitch.tv CLEARCHAT #dallas"))!;
        Assert.Equal(ChatModerationKind.ClearChat, mod.Kind);
        Assert.Null(mod.TargetLogin);
    }

    [Fact]
    public void ModerationIsNotAChatLineAndChatIsNotModeration()
    {
        Assert.Null(ChatMessage.FromIrc(IrcMessage.Parse(":tmi.twitch.tv CLEARCHAT #dallas"), DateTimeOffset.UnixEpoch));
        Assert.Null(ChatModeration.FromIrc(IrcMessage.Parse(":a!a@a PRIVMSG #c :hi")));
    }
}
