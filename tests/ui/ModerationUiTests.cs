// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;
using StreamEdge.Chat;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

public class ModerationUiTests(HeadlessAvalonia ui) : UiTest(ui)
{
    private static MainViewModel Demo() =>
        new(new AppSettings { ShowPronouns = false }, new StartupOptions { Demo = true }, InlineDispatcher.Instance);

    private static ChatMessage Line(string login, string text, string id) =>
        new(DateTimeOffset.Now, ChatMessageKind.Message, "nintendope503", login, login, null, [], text)
        { Id = id, UserId = "u-" + login };

    /// <summary>The glyphs of clickable bits (mod icons, names) in a line.</summary>
    private static List<string> Clickables(ChatLine line)
    {
        var text = (SelectableTextBlock)((StackPanel)line.Child!).Children[^1];
        return [.. text.Inlines!.OfType<InlineUIContainer>().Select(c => c.Child).OfType<TextBlock>().Select(t => t.Text ?? "")];
    }

    [Fact]
    public void ModIconsShowOnlyForModsAndNotOnOwnLines() => Ui(() =>
    {
        var vm = Demo();
        var chat = vm.FrontTab!.Chat;
        chat.Add(Line("bob", "hello", "m1"));
        chat.AddOwn(Line("me", "mine", "m2"));
        var bob = new ChatLine { Entry = chat.Lines[^2] };
        var mine = new ChatLine { Entry = chat.Lines[^1] };
        Assert.DoesNotContain("⛔", Clickables(bob));

        chat.IsModerator = true;
        Assert.Equal(["🗑", "⏱", "⛔", "bob"], Clickables(bob));
        Assert.DoesNotContain("⛔", Clickables(mine));
        Assert.Contains("me", Clickables(mine));
    });

    [Fact]
    public void TheCardGathersWhatTheUserSaid() => Ui(() =>
    {
        var vm = Demo();
        var chat = vm.FrontTab!.Chat;
        chat.Add(Line("bob", "one", "a"));
        chat.Add(Line("amy", "other", "b"));
        chat.Add(Line("bob", "two", "c"));

        var card = vm.OpenUserCard("bob", chat.Lines[^1])!;

        Assert.Equal(["one", "two"], card.RecentMessages.Select(e => e.Message.Text));
        Assert.False(card.CanModerate);
        Assert.False(card.CanDeleteClicked);

        chat.IsModerator = true;
        Assert.True(vm.OpenUserCard("bob", chat.Lines[^1])!.CanDeleteClicked);
    });

    [Fact]
    public void ModeratingSignedOutSaysWhyInChat() => Ui(() =>
    {
        var vm = Demo();
        var tab = vm.FrontTab!;
        tab.Chat.RoomId = "1";
        tab.Chat.Add(Line("bob", "spam", "m1"));

        vm.ModerateAsync(tab, tab.Chat.Lines[^1], ModAction.Ban).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Log in to moderate.", tab.Chat.Lines[^1].Message.Text);
    });

    [Fact]
    public void MentionPutsTheNameInTheChatBox() => Ui(() =>
    {
        var vm = Demo();
        vm.FrontTab!.Chat.Add(Line("bob", "hi", "a"));
        vm.OpenUserCard("bob", null)!.MentionCommand.Execute(null);
        Assert.Equal("@bob ", vm.Chat.Draft);
    });
}
