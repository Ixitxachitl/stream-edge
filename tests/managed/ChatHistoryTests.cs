// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using Xunit;

namespace StreamEdge.Tests;

public class ChatHistoryTests
{
    // Shaped like twitch.tv's recentChatMessages reply (fields as the schema
    // accepts them); not a captured response.
    private const string TwitchReply = """
        {"data":{"channel":{"id":"71092938","recentChatMessages":[
          {"id":"m2","sentAt":"2026-10-15T05:00:02Z","deletedAt":null,
           "sender":{"id":"2","login":"amy","displayName":"Amy","chatColor":"#FF0000",
                     "displayBadges":[{"setID":"subscriber","version":"12"}]},
           "content":{"text":"🎉 Kappa hi","fragments":[
             {"text":"🎉 ","content":null},
             {"text":"Kappa","content":{"__typename":"Emote","id":"25","token":"Kappa"}},
             {"text":" hi","content":null}]}},
          {"id":"m1","sentAt":"2026-10-15T05:00:01Z","deletedAt":null,
           "sender":{"id":"1","login":"bob","displayName":"bob","chatColor":"","displayBadges":[]},
           "content":{"text":"first","fragments":[{"text":"first","content":null}]}},
          {"id":"m3","sentAt":"2026-10-15T05:00:03Z","deletedAt":"2026-10-15T05:00:04Z",
           "sender":{"id":"3","login":"spam","displayName":"spam","chatColor":null,"displayBadges":[]},
           "content":{"text":"bad","fragments":[{"text":"bad","content":null}]}}
        ]}}}
        """;

    [Fact]
    public void TwitchHistoryIsOldestFirstWithDeletedLinesMarked()
    {
        var messages = ChatHistory.ParseTwitch(TwitchReply, "xqc")!;
        Assert.Equal(["m1", "m2", "m3"], messages.Select(m => m.Id));
        Assert.Equal([false, false, true], messages.Select(m => m.WasDeleted));
        Assert.Equal("xqc", messages[0].Channel);
        Assert.Null(messages[0].Color);
    }

    [Fact]
    public void EmotesArePlacedByFragmentEvenAfterEmoji()
    {
        var amy = ChatHistory.ParseTwitch(TwitchReply, "xqc")![1];
        Assert.Equal("🎉 Kappa hi", amy.Text);
        var kappa = Assert.Single(amy.TwitchEmotes);
        Assert.Equal("Kappa", amy.Text.Substring(kappa.Start, kappa.Length));
        Assert.Equal("25", kappa.Id);
        Assert.Equal(["subscriber/12"], amy.Badges);
        Assert.Equal("2", amy.UserId);
    }

    [Theory]
    [InlineData("""{"errors":[{"message":"unauthenticated"}],"data":{"channel":null}}""")]
    [InlineData("""{"data":{"channel":null}}""")]
    [InlineData("not json")]
    public void UnusableRepliesMeanFallBack(string json) => Assert.Null(ChatHistory.ParseTwitch(json, "xqc"));

    [Fact]
    public void RecentMessagesServiceLinesParseLikeLiveOnes()
    {
        var messages = ChatHistory.ParseRecentMessages("""
            {"messages":[
              "@historical=1;id=a;tmi-sent-ts=1760504400000;user-id=1;display-name=Bob :bob!bob@bob.tmi.twitch.tv PRIVMSG #xqc :hello",
              "@historical=1;rm-deleted=1;id=b :bad!bad@bad.tmi.twitch.tv PRIVMSG #xqc :removed",
              "@historical=1 :someone!someone@someone.tmi.twitch.tv JOIN #xqc",
              "@historical=1;id=c;emotes=25:0-4 :amy!amy@amy.tmi.twitch.tv PRIVMSG #xqc :Kappa"
            ],"error":null}
            """);
        Assert.Equal(["a", "b", "c"], messages.Select(m => m.Id));
        Assert.Equal([false, true, false], messages.Select(m => m.WasDeleted));
        Assert.Single(messages[2].TwitchEmotes);
    }
}
