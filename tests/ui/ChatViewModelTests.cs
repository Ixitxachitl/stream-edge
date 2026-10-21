// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.Chat;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

public class ChatViewModelTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);

    private static ChatViewModel Chat(AppSettings? settings = null)
    {
        settings ??= new AppSettings { ShowPronouns = false };
        // No network in these tests: nothing is loaded, so only Twitch's own
        // emotes (from the message tags) resolve.
        var services = new ChatServices(new HttpClient(), settings, InlineDispatcher.Instance, () => "me");
        return new ChatViewModel(settings, services) { Channel = "chan" };
    }

    private static ChatMessage Irc(string line) => ChatMessage.FromIrc(IrcMessage.Parse(line), T)!;

    [Fact]
    public void TheUsersOwnLinesFromHistoryAreKnownAsTheirs()
    {
        var chat = Chat();
        ChatMessage From(string login) => new(T, ChatMessageKind.Message, "chan", login, login, null, [], "hi") { Id = login };
        chat.InsertHistory([From("ME"), From("viewer")], "Twitch");

        // Sent from twitch.tv or another app, so not drawn here: still theirs,
        // so no ↩ or mod tools on it.
        Assert.True(chat.Lines.Single(l => l.Message.Login == "ME").IsFromSelf);
        Assert.False(chat.Lines.Single(l => l.Message.Login == "viewer").IsFromSelf);
    }

    [Fact]
    public void PlaceholderFollowsWhetherSendingIsPossible()
    {
        var chat = new ChatViewModel(new AppSettings());
        var raised = new List<string?>();
        chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal("Log in to chat", chat.Placeholder);
        chat.CanSend = true;
        Assert.Equal("Send a message", chat.Placeholder);
        Assert.Contains(nameof(ChatViewModel.Placeholder), raised);
    }

    [Fact]
    public void JoinAndPartAreHiddenUnlessTurnedOn()
    {
        var hidden = Chat();
        hidden.Add(Irc(":bob!bob@bob JOIN #chan"));
        Assert.Empty(hidden.Lines);

        var shown = Chat(new AppSettings { ShowJoinPart = true, ShowPronouns = false });
        shown.Add(Irc(":bob!bob@bob JOIN #chan"));
        Assert.Single(shown.Lines);
    }

    [Fact]
    public void TwitchEmotesAndMentionsAreTokenized()
    {
        var chat = Chat();
        chat.Add(Irc("@emotes=25:6-10;id=m1 :bob!bob@bob PRIVMSG #chan :hello Kappa @me"));

        var entry = Assert.Single(chat.Lines);
        Assert.Contains(entry.Tokens, t => t.Kind == ChatTokenKind.Emote && t.Emote!.Code == "Kappa");
        Assert.True(entry.IsSelfMention);
    }

    [Fact]
    public void DeletedMessagesAndTimeoutsAreHidden()
    {
        var chat = Chat();
        chat.Add(Irc("@id=a;user-id=1 :bob!bob@bob PRIVMSG #chan :first"));
        chat.Add(Irc("@id=b;user-id=1 :bob!bob@bob PRIVMSG #chan :second"));
        chat.Add(Irc("@id=c;user-id=2 :amy!amy@amy PRIVMSG #chan :third"));

        chat.Apply(ChatModeration.FromIrc(IrcMessage.Parse("@target-msg-id=c :tmi.twitch.tv CLEARMSG #chan :third"))!);
        Assert.True(chat.Lines[2].IsDeleted);
        Assert.False(chat.Lines[0].IsDeleted);

        chat.Apply(ChatModeration.FromIrc(IrcMessage.Parse("@ban-duration=600;target-user-id=1 :tmi.twitch.tv CLEARCHAT #chan :bob"))!);
        Assert.True(chat.Lines[0].IsDeleted);
        Assert.True(chat.Lines[1].IsDeleted);
        Assert.Contains("timed out for 10 minutes", chat.Lines[^1].Message.Text);
    }

    [Fact]
    public void HistoryGoesAboveLiveLinesWithoutRepeatsAndEndsWithADivider()
    {
        var chat = Chat();
        chat.Add(ChatMessage.System("chan", "### Joining #chan ###", T));
        chat.Add(Irc("@id=live1 :bob!bob@bob PRIVMSG #chan :live"));

        chat.InsertHistory(
        [
            Irc("@id=old1 :amy!amy@amy PRIVMSG #chan :older"),
            Irc("@id=live1 :bob!bob@bob PRIVMSG #chan :live"),
        ], "Twitch");

        Assert.Equal(["### Joining #chan ###", "older", "### Finished with history logs (Twitch). ###", "live"],
            chat.Lines.Select(l => l.Message.Text));
    }

    [Fact]
    public void NoHistoryMeansNoDivider()
    {
        var chat = Chat();
        chat.InsertHistory([], "Twitch");
        Assert.Empty(chat.Lines);
    }

    [Fact]
    public void BadgesWithoutArtFallBackToTheirSetName()
    {
        var chat = Chat();
        chat.Add(Irc("@badges=moderator/1,subscriber/12 :bob!bob@bob PRIVMSG #chan :hi"));

        Assert.Equal(["moderator", "subscriber"], chat.Lines[0].Badges.Select(b => b.Set));
        Assert.All(chat.Lines[0].Badges, b => Assert.Null(b.Image));
    }

    [Fact]
    public void RedemptionsShowTheirRewardOnceTheChannelsRewardsAreKnown()
    {
        var chat = Chat();
        chat.Add(Irc("@custom-reward-id=vote;display-name=Alice;id=1;user-id=2 :alice!alice@alice.tmi.twitch.tv PRIVMSG #chan :Elden Ring"));
        Assert.Null(chat.Lines[0].Reward);
        Assert.False(chat.HasPoints);

        var reward = new StreamEdge.Streams.ChannelReward("vote", "Sub Vote", 5000, "#392E5C", null, "!vote Game", true, true);
        chat.Points = new StreamEdge.Streams.ChannelPoints("PotatoCoin", null, [reward], 4000);
        Assert.Same(reward, chat.Lines[0].Reward);
        Assert.True(chat.HasPoints);
        Assert.Equal("4K", chat.BalanceText);

        // Lines after the rewards loaded get theirs straight away.
        chat.Add(Irc("@custom-reward-id=vote;display-name=Bob;id=3;user-id=4 :bob!bob@bob.tmi.twitch.tv PRIVMSG #chan :Hades"));
        Assert.Same(reward, chat.Lines[1].Reward);

        var tile = Assert.Single(chat.Rewards);
        Assert.Equal(0.45, tile.Opacity); // 1,000 short
        Assert.Contains("1,000 more needed", tile.Tip.Replace('.', ','));
    }

    [Theory]
    [InlineData("#FFFFFF", "#0E0E10")]   // white tile: dark text, not white on white
    [InlineData("#C2FF5C", "#0E0E10")]   // light green
    [InlineData("#9147FF", "#FFFFFF")]   // Twitch purple
    [InlineData("#1F69FF", "#FFFFFF")]
    [InlineData(null, "#FFFFFF")]        // no colour: purple
    public void RewardTitlesStandOutOnTheirTile(string? color, string text) =>
        Assert.Equal(text, new RewardTile(new Streams.ChannelReward("r", "Hydrate", 100, color, null, null, false, true), null).Foreground);

    [Fact]
    public void AResubToShareShowsUntilDismissedAndANewOneShowsAgain()
    {
        var chat = new ChatViewModel(new AppSettings());
        Assert.False(chat.ShowsResub);

        chat.Resub = new Streams.ResubToken("a", 14, 6, false, null);
        Assert.True(chat.ShowsResub);
        Assert.Equal("Share your 14-month resub", chat.ResubTitle);
        Assert.True(chat.HasStreak);
        chat.ExpandResubCommand.Execute(null);
        chat.ResubMessage = "hi";
        Assert.True(chat.ResubExpanded);

        // The same one again (the next refresh): still where it was.
        chat.Resub = new Streams.ResubToken("a", 14, 6, false, null);
        Assert.Equal("hi", chat.ResubMessage);

        chat.DismissResubCommand.Execute(null);
        Assert.False(chat.ShowsResub);
        chat.Resub = new Streams.ResubToken("a", 14, 6, false, null);
        Assert.False(chat.ShowsResub);

        // Next month's: shown, from the start.
        chat.Resub = new Streams.ResubToken("b", 15, 1, true, "Amy");
        Assert.True(chat.ShowsResub);
        Assert.Equal("Share your 15-month sub (gifted by Amy)", chat.ResubTitle);
        Assert.False(chat.HasStreak);
        Assert.False(chat.ResubExpanded);
        Assert.Equal("", chat.ResubMessage);
    }
}
