// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using StreamEdge.Streams;
using Xunit;

namespace StreamEdge.Tests;

public class ChatCommandTests
{
    [Fact]
    public void ParsesNameAndArgumentsButLeavesMessagesAndMeAlone()
    {
        var c = ChatCommand.Parse("  /Timeout @SomeOne 10m  spam   links ");
        Assert.NotNull(c);
        Assert.Equal("timeout", c.Name);
        Assert.Equal("someone", c.User(0));
        Assert.Equal("10m", c.Arg(1));
        Assert.Equal("spam   links", c.After(2));
        Assert.Null(c.Arg(5));

        Assert.Equal("clear", ChatCommand.Parse("/clear")?.Name);
        Assert.Equal("", ChatCommand.Parse("/clear")?.Text);
        Assert.Null(ChatCommand.Parse("hello /ban x"));
        Assert.Null(ChatCommand.Parse("/me waves"));
        Assert.Null(ChatCommand.Parse("/ space"));
        Assert.Null(ChatCommand.Parse("/"));
    }

    [Fact]
    public void TypingASlashListsTheCommandsThatMatch()
    {
        var all = ChatCommand.Complete("", moderator: false);
        Assert.Contains(all, c => c.Name == "w");
        Assert.DoesNotContain(all, c => c.Moderator); // not where the user can't use them

        Assert.Equal(["shoutout", "slow", "slowoff", "subscribers", "subscribersoff", "shield", "shieldoff"],
            ChatCommand.Complete("s", moderator: true).Select(c => c.Name));
        Assert.Equal("shoutout", Assert.Single(ChatCommand.Complete("SO", moderator: true)).Name); // by alias
        Assert.Empty(ChatCommand.Complete("so", moderator: false));
        Assert.Empty(ChatCommand.Complete("nope", moderator: true));
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("30s", 30)]
    [InlineData("10m", 600)]
    [InlineData("2H", 7200)]
    [InlineData("1d", 86_400)]
    [InlineData("2w", 1_209_600)]
    [InlineData("ten", null)]
    [InlineData("5y", null)]
    [InlineData("-5", null)]
    [InlineData(null, null)]
    public void DurationsTakePlainSecondsOrAUnit(string? text, int? seconds) =>
        Assert.Equal(seconds, ChatCommand.ParseDuration(text));

    [Theory]
    [InlineData("blue", "blue")]
    [InlineData("HotPink", "hot_pink")]
    [InlineData("hot pink", "hot_pink")]
    [InlineData("dodger-blue", "dodger_blue")]
    [InlineData("#9147FF", "#9147FF")]
    [InlineData("9147ff", "#9147ff")]
    public void ColoursAreWrittenTheWayHelixWantsThem(string typed, string sent) =>
        Assert.Equal(sent, TwitchUserApi.NormalizeColor(typed));

    [Fact]
    public void AReplyCarriesTwitchsReplyTag()
    {
        Assert.Equal("PRIVMSG #chan :hi", TwitchChatClient.Privmsg("#chan", "hi"));
        Assert.Equal("@reply-parent-msg-id=abc-123 PRIVMSG #chan :hi", TwitchChatClient.Privmsg("chan", "hi", "abc-123"));
    }

    private const string CheermotesJson = """
        {"data":[{"prefix":"Cheer","tiers":[
          {"min_bits":1,"id":"1","color":"#979797","images":{"dark":{"animated":{"2":"https://cdn.example/cheer/1.gif"},"static":{"2":"https://cdn.example/cheer/1.png"}}}},
          {"min_bits":100,"id":"100","color":"#9c3ee8","images":{"dark":{"animated":{"2":"https://cdn.example/cheer/100.gif"}}}},
          {"min_bits":1000,"id":"1000","color":"#1db2a5","images":{"dark":{"animated":{"2":"https://cdn.example/cheer/1000.gif"}}}}]},
          {"prefix":"Broken","tiers":[]}]}
        """;

    [Fact]
    public void CheermotesPickTheTierTheAmountReaches()
    {
        var cheermotes = Cheermotes.Parse(CheermotesJson);
        Assert.Single(cheermotes);

        var cheer = Cheermotes.Match(cheermotes, "cheer250");
        Assert.NotNull(cheer);
        Assert.Equal(250, cheer.Bits);
        Assert.Equal("#9c3ee8", cheer.Color);
        Assert.Equal("https://cdn.example/cheer/100.gif", cheer.Image.AbsoluteUri);
        Assert.Equal("https://cdn.example/cheer/1000.gif", Cheermotes.Match(cheermotes, "Cheer5000")!.Image.AbsoluteUri);

        Assert.Null(Cheermotes.Match(cheermotes, "cheer"));
        Assert.Null(Cheermotes.Match(cheermotes, "100"));
        Assert.Null(Cheermotes.Match(cheermotes, "cheer0"));
        Assert.Null(Cheermotes.Match(cheermotes, "kappa100"));
    }

    [Fact]
    public void CheersAreOnlyMatchedInMessagesWithBits()
    {
        var cheermotes = Cheermotes.Parse(CheermotesJson);
        var paid = new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, "c", "u", "U", null, [], "Cheer100 nice") { Bits = 100 };
        var tokens = ChatTokenizer.Tokenize(paid, null, null, cheermotes: cheermotes);
        Assert.Equal(ChatTokenKind.Cheer, tokens[0].Kind);
        Assert.Equal(100, tokens[0].Cheer?.Bits);

        var free = paid with { Bits = 0 };
        Assert.DoesNotContain(ChatTokenizer.Tokenize(free, null, null, cheermotes: cheermotes), t => t.Kind == ChatTokenKind.Cheer);
    }

    [Fact]
    public void WhisperNotificationSaysWhoAndWhat()
    {
        var m = EventSubClient.Parse("""
            {"metadata":{"message_id":"w1","message_type":"notification","subscription_type":"user.whisper.message"},
             "payload":{"subscription":{"type":"user.whisper.message"},
               "event":{"from_user_id":"423374343","from_user_login":"glowingsun","from_user_name":"GlowingSun",
                        "to_user_id":"424596340","to_user_login":"me","to_user_name":"Me",
                        "whisper_id":"some-whisper-id","whisper":{"text":"a secret"}}}}
            """);
        Assert.Equal(new WhisperEvent("some-whisper-id", "423374343", "glowingsun", "GlowingSun", "a secret"), m?.Whisper);
        Assert.Null(m?.Raid);
    }
}
