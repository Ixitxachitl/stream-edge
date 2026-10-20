// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
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

    private static ChatMessage Line(string login, string text, string id, params string[] badges) =>
        new(DateTimeOffset.Now, ChatMessageKind.Message, "pixelperegrine", login, login, null, badges, text)
        { Id = id, UserId = "u-" + login };

    private static SelectableTextBlock Text(ChatLine line)
    {
        var stack = line.Child as StackPanel ?? ((Panel)line.Child!).Children.OfType<StackPanel>().First();
        return (SelectableTextBlock)stack.Children[^1];
    }

    /// <summary>The mod icons in a line, by their tips.</summary>
    private static List<string> ModIcons(ChatLine line) =>
        [.. Text(line).Inlines!.OfType<InlineUIContainer>().Select(c => c.Child).OfType<InlineMiddle>()
            .Select(m => m.Child).OfType<Border>().Where(b => b.Child is Avalonia.Controls.Shapes.Path)
            .Select(b => ToolTip.GetTip(b) as string ?? "")];

    /// <summary>The glyphs of clickable bits (mod icons, names) in a line.</summary>
    private static List<string> Clickables(ChatLine line)
    {
        // Lines that can be replied to wrap the stack with their ↩.
        var stack = line.Child as StackPanel ?? ((Panel)line.Child!).Children.OfType<StackPanel>().First();
        var text = (SelectableTextBlock)stack.Children[^1];
        // Icons come wrapped to centre them on the text, and go by their
        // tips; names don't, and go by their text.
        return [.. text.Inlines!.OfType<InlineUIContainer>().Select(c => c.Child is InlineMiddle m ? m.Child : c.Child)
            .Select(c => c is TextBlock t ? t.Text ?? "" : ToolTip.GetTip(c!) as string ?? "")];
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
        Assert.DoesNotContain("Ban", Clickables(bob));

        chat.IsModerator = true;
        Assert.Equal(["Delete message", "Timeout 10 minutes", "Ban", "bob"], Clickables(bob));
        // Your own line: delete, but no timing yourself out.
        Assert.Equal(["Delete message", "me"], Clickables(mine));
    });

    [Fact]
    public void ModIconsShowOnlyWhereTwitchWouldAllowThem() => Ui(() =>
    {
        var vm = Demo();
        var chat = vm.FrontTab!.Chat;
        chat.IsModerator = true;
        chat.Add(Line("leadmod", "hi", "m1", "lead_moderator/1"));
        chat.Add(Line("pixelperegrine", "hey", "m2"));
        chat.Add(Line("bob", "yo", "m3", "vip/1"));
        chat.InsertHistory([Line("amy", "old", "h1") with { Timestamp = DateTimeOffset.Now.AddHours(-7) }], "test");
        var mod = new ChatLine { Entry = chat.Lines.Single(l => l.Message.Id == "m1") };
        var owner = new ChatLine { Entry = chat.Lines.Single(l => l.Message.Id == "m2") };
        var vip = new ChatLine { Entry = chat.Lines.Single(l => l.Message.Id == "m3") };
        var old = new ChatLine { Entry = chat.Lines.Single(l => l.Message.Id == "h1") };

        Assert.Empty(ModIcons(mod));
        Assert.Empty(ModIcons(owner));
        Assert.Equal(["Delete message", "Timeout 10 minutes", "Ban"], ModIcons(vip));
        // Twitch deletes only young messages; old history can still be acted on.
        Assert.Equal(["Timeout 10 minutes", "Ban"], ModIcons(old));

        // The broadcaster can time out and ban moderators, not delete their lines.
        chat.IsBroadcaster = true;
        Assert.Equal(["Timeout 10 minutes", "Ban"], ModIcons(mod));
        Assert.True(vm.OpenUserCard("leadmod", mod.Entry)!.CanRemove);
        chat.IsBroadcaster = false;
        var card = vm.OpenUserCard("leadmod", mod.Entry)!;
        Assert.False(card.CanRemove);
        Assert.False(card.CanDeleteClicked);
        Assert.False(card.ShowsModeration);
    });

    [Fact]
    public void OwnLinesGetTheirIdFromTwitchAndCanBeDeleted() => Ui(() =>
    {
        var vm = Demo();
        var chat = vm.FrontTab!.Chat;
        chat.IsModerator = true;
        chat.AddOwn(Line("me", "oops", null!));
        var line = new ChatLine { Entry = chat.Lines[^1] };
        Assert.DoesNotContain("Delete message", ModIcons(line));   // no id yet

        chat.ClaimOwnId("sent-1");
        Assert.Equal("sent-1", chat.Lines[^1].Message.Id);
        Assert.Equal(["Delete message"], ModIcons(line));

        // The id can arrive before the line is drawn.
        chat.ClaimOwnId("sent-2");
        chat.AddOwn(Line("me", "again", null!));
        Assert.Equal("sent-2", chat.Lines[^1].Message.Id);
    });

    [Fact]
    public void DeletedLinesCanStayCrossedOut() => Ui(() =>
    {
        var settings = new AppSettings { ShowPronouns = false };
        var vm = new MainViewModel(settings, new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var chat = vm.FrontTab!.Chat;
        chat.Add(Line("bob", "rude words", "m1"));
        var line = new ChatLine { Entry = chat.Lines[^1] };
        chat.Apply(new ChatModeration(ChatModerationKind.DeleteMessage, "pixelperegrine", DateTimeOffset.Now) { TargetMessageId = "m1" });
        Assert.Contains(Text(line).Inlines!.OfType<Run>(), r => r.Text == "<message deleted>");

        vm.ShowDeletedMessages = true;
        var words = Assert.Single(Text(line).Inlines!.OfType<Run>(), r => r.Text == "rude words");
        Assert.Equal(TextDecorations.Strikethrough, words.TextDecorations);

        // History's deleted lines: left out, or shown crossed out.
        chat.InsertHistory([Line("amy", "gone", "h1") with { WasDeleted = true }], "test");
        Assert.True(Assert.Single(chat.Lines, l => l.Message.Id == "h1").IsDeleted);
        vm.ShowDeletedMessages = false;
        var fresh = vm.FrontTab.Chat;
        fresh.InsertHistory([Line("amy", "gone again", "h2") with { WasDeleted = true }], "test");
        Assert.DoesNotContain(fresh.Lines, l => l.Message.Id == "h2");
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
