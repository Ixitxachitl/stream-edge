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
    public void BadgesWithoutArtFallBackToTheirSetName()
    {
        var chat = Chat();
        chat.Add(Irc("@badges=moderator/1,subscriber/12 :bob!bob@bob PRIVMSG #chan :hi"));

        Assert.Equal(["moderator", "subscriber"], chat.Lines[0].Badges.Select(b => b.Set));
        Assert.All(chat.Lines[0].Badges, b => Assert.Null(b.Image));
    }
}
