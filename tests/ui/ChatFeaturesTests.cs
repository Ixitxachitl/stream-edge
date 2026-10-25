// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;
using StreamEdge.Chat;
using StreamEdge.Chat.Emotes;
using StreamEdge.Streams;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

/// <summary>Slash commands, whispers, replies and go-live alerts, as far as
/// they go without a Twitch account (demo mode, signed out).</summary>
public class ChatFeaturesTests(HeadlessAvalonia ui) : UiTest(ui)
{
    private static MainViewModel Demo(AppSettings? settings = null) =>
        new(settings ?? new AppSettings(), new StartupOptions { Demo = true }, InlineDispatcher.Instance);

    /// <summary>An inbox on its own, its channel search knowing only
    /// <paramref name="channels"/> and never asking Twitch.</summary>
    private static WhisperInboxViewModel Inbox(AppSettings settings, Func<string, string?, string, Task<string?>> send,
        params ChannelSuggestion[] channels) =>
        new(settings, null, send, q => channels.Where(c => ChannelSearch.Matches(c.Login, c.DisplayName, q)),
            (_, _) => Task.FromResult<IReadOnlyList<ChannelSuggestion>>([]), InlineDispatcher.Instance);

    private static string LastLine(ChannelTabViewModel tab) => tab.Chat.Lines[^1].Message.Text;

    [Fact]
    public void CommandsAnswerInTheChatInsteadOfVanishing() => Ui(() =>
    {
        var vm = Demo();
        var tab = vm.FrontTab!;

        vm.RunCommandAsync(tab, ChatCommand.Parse("/help")!).GetAwaiter().GetResult();
        Assert.Contains("/timeout <user>", LastLine(tab));

        vm.RunCommandAsync(tab, ChatCommand.Parse("/frobnicate x")!).GetAwaiter().GetResult();
        Assert.Equal("Unknown command /frobnicate. Type /help for the ones StreamEdge knows.", LastLine(tab));

        // Signed out, each says what it needs rather than failing silently.
        vm.RunCommandAsync(tab, ChatCommand.Parse("/ban someone")!).GetAwaiter().GetResult();
        Assert.Equal("Log in to moderate.", LastLine(tab));
        vm.RunCommandAsync(tab, ChatCommand.Parse("/w someone hi")!).GetAwaiter().GetResult();
        Assert.Equal("Log in to do that.", LastLine(tab));
        vm.RunCommandAsync(tab, ChatCommand.Parse("/color")!).GetAwaiter().GetResult();
        Assert.StartsWith("Usage: /color", LastLine(tab));
    });

    [Fact]
    public void AWhisperGoesToTheInboxNotTheChat() => Ui(() =>
    {
        var vm = Demo();
        var alerts = new List<AppNotification>();
        vm.Notify += alerts.Add;
        var before = vm.ActiveTab!.Chat.Lines.Count;
        var opened = 0;
        vm.Whispers.OpenRequested += () => opened++;

        vm.OnWhisper(new WhisperEvent("w1", "42", "glowingsun", "GlowingSun", "psst"));

        Assert.Equal(before, vm.ActiveTab.Chat.Lines.Count);
        var thread = Assert.Single(vm.Whispers.Threads);
        Assert.Equal("GlowingSun", thread.Name);
        Assert.Equal("psst", thread.Preview);
        Assert.Equal(1, vm.Whispers.Unread);

        // The alert opens the inbox at that conversation, which reads it.
        var alert = Assert.Single(alerts);
        Assert.Equal("Whisper from GlowingSun", alert.Title);
        alert.Open!();
        Assert.Equal(1, opened);
        Assert.Same(thread, vm.Whispers.Selected);
        vm.Whispers.IsOpen = true;
        Assert.Equal(0, vm.Whispers.Unread);

        // While it's open on that conversation, no alert and nothing unread.
        vm.OnWhisper(new WhisperEvent("w2", "42", "glowingsun", "GlowingSun", "still there?"));
        Assert.Single(alerts);
        Assert.False(vm.Whispers.HasUnread);
        Assert.Equal(2, thread.Lines.Count);

        // Someone else's goes on top, unread, and the open one stays chosen.
        vm.OnWhisper(new WhisperEvent("w3", "7", "other", "Other", "hi"));
        Assert.Equal(["Other", "GlowingSun"], vm.Whispers.Threads.Select(t => t.Name));
        Assert.Same(thread, vm.Whispers.Selected);
        Assert.Equal(1, vm.Whispers.Unread);
        Assert.Equal(2, alerts.Count);
    });

    [Fact]
    public void WhispersAreKeptForTheSameAccountOnly() => Ui(() =>
    {
        var inbox = Inbox(new AppSettings(), (_, _, _) => Task.FromResult<string?>(null));
        inbox.Load("me");
        inbox.Received("glowingsun", "GlowingSun", "42", "psst", DateTimeOffset.Now);
        inbox.Sent("glowingsun", "", "42", "hey", DateTimeOffset.Now);

        inbox.Load("me");
        var thread = Assert.Single(inbox.Threads);
        Assert.Equal("GlowingSun", thread.Name);
        Assert.Equal(["GlowingSun: psst", "You: hey"], thread.Lines.Select(l => $"{l.Message.Sender}: {l.Message.Text}"));
        Assert.Equal(1, inbox.Unread);

        inbox.Load("someone else");
        Assert.False(inbox.HasThreads);
        inbox.Load(null);
        Assert.False(inbox.HasThreads);
    });

    [Fact]
    public void WhispersDrawEmotesOnceTheyAreKnown() => Ui(() =>
    {
        var vm = Demo();
        vm.OnWhisper(new WhisperEvent("w1", "42", "glowingsun", "GlowingSun", "hi Kappa"));
        var line = vm.Whispers.Threads[0].Lines[^1];
        Assert.DoesNotContain(line.Tokens, t => t.Kind == ChatTokenKind.Emote);

        // Twitch sends whispers without emote positions: matched by code,
        // and drawn again when the user's emotes arrive.
        var kappa = new Uri("https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/1.0");
        vm.Services.SetMyEmotes([new UserEmote(new Emote("Kappa", kappa, EmoteProvider.Twitch), "0", "globals")], null, replace: true);
        Assert.Contains(line.Tokens, t => t.Emote?.Code == "Kappa");
    });

    [Fact]
    public void ANewWhisperStartsAConversation() => Ui(() =>
    {
        WhisperInboxViewModel? inbox = null;
        var sent = new List<string>();
        inbox = Inbox(new AppSettings { ReadOnly = true }, (login, userId, text) =>
        {
            sent.Add($"{login}/{userId}: {text}");
            if (login == "nobody") return Task.FromResult<string?>("No Twitch user called nobody.");
            inbox!.Sent(login, "GlowingSun", "42", text, DateTimeOffset.Now);
            return Task.FromResult<string?>(null);
        });
        inbox.Load(null);

        // Empty, it opens ready to write.
        inbox.IsOpen = true;
        Assert.True(inbox.Composing);
        Assert.False(inbox.SendCommand.CanExecute(null));

        inbox.Recipients.Query = "nobody";
        inbox.Draft = "hello";
        inbox.SendCommand.Execute(null);
        Assert.Equal("No Twitch user called nobody.", inbox.Status);
        Assert.Equal("hello", inbox.Draft);             // kept to try again
        Assert.True(inbox.Composing);

        inbox.Recipients.Query = "@GlowingSun";
        inbox.SendCommand.Execute(null);
        Assert.Equal(["nobody/: hello", "GlowingSun/: hello"], sent);
        Assert.False(inbox.Composing);
        Assert.Equal("GlowingSun", inbox.Selected?.Name);
        Assert.Equal("", inbox.Draft);
        Assert.Equal("", inbox.Recipients.Query);

        // Answering a conversation sends to it, by its id.
        inbox.Draft = "again";
        inbox.SendCommand.Execute(null);
        Assert.Equal("glowingsun/42: again", sent[^1]);

        // The New button clears the choice and writes to someone else.
        inbox.ComposeCommand.Execute(null);
        Assert.True(inbox.Composing);
        Assert.Null(inbox.Selected);
    });

    [Fact]
    public void WhoToSuggestsAsTheLiveListSearchDoes() => Ui(() =>
    {
        var inbox = Inbox(new AppSettings { ReadOnly = true }, (_, _, _) => Task.FromResult<string?>(null),
            new ChannelSuggestion("glowfish", "GlowFish", IsLive: true));
        inbox.Recipients.Debounce = TimeSpan.Zero;
        inbox.Load(null);
        inbox.Received("glowingsun", "GlowingSun", "42", "psst", DateTimeOffset.Now);
        var picked = 0;
        inbox.RecipientPicked += () => picked++;
        inbox.ComposeCommand.Execute(null);

        // People already whispered, channels known, and the name as typed.
        inbox.Recipients.Query = "glow";
        Assert.Equal(["glowfish", "glowingsun", "glow"], inbox.Recipients.Suggestions.Select(s => s.Login));
        Assert.Equal("Whisper this name", inbox.Recipients.Suggestions[^1].Detail);

        // Picking one keeps it in the box, closes the list, and moves on.
        inbox.Recipients.Accept(1);
        Assert.Equal("glowingsun", inbox.Recipients.Query);
        Assert.False(inbox.Recipients.IsOpen);
        Assert.Equal(1, picked);
        inbox.Draft = "hi";
        Assert.True(inbox.SendCommand.CanExecute(null));
    });

    [Fact]
    public void TheInboxOpensUnderItsButton() => Ui(() =>
    {
        var vm = Demo();
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "InboxButton");
        Assert.False(button.IsVisible);                 // signed out, no whispers: nothing to show

        vm.OnWhisper(new WhisperEvent("w1", "42", "glowingsun", "GlowingSun", "psst"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.IsVisible);

        vm.Whispers.Open("glowingsun");
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.Flyout!.IsOpen);
        Assert.True(vm.Whispers.IsOpen);
        Assert.False(vm.Whispers.HasUnread);
        button.Flyout.Hide();
        Assert.False(vm.Whispers.IsOpen);
        window.Close();
    });

    [Fact]
    public void ReplyingShowsWhatsAnsweredUntilCancelled() => Ui(() =>
    {
        var vm = Demo();
        var tab = vm.FrontTab!;
        tab.Chat.Add(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, tab.Login, "viewer", "Viewer", null, [], "nice play")
        {
            Id = "msg-1",
        });
        var entry = tab.Chat.Lines[^1];

        vm.StartReply(entry);
        Assert.Same(entry, tab.Chat.ReplyTo);
        Assert.True(tab.Chat.IsReplying);
        Assert.Equal("Replying to @Viewer: nice play", tab.Chat.ReplyText);

        tab.Chat.CancelReplyCommand.Execute(null);
        Assert.False(tab.Chat.IsReplying);
    });

    [Fact]
    public void OnlyOthersMessagesWithAnIdGetAReplyArrow() => Ui(() =>
    {
        var vm = Demo();
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        var tab = vm.FrontTab!;
        tab.Chat.Add(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, tab.Login, "viewer", "Viewer", null, [], "has id") { Id = "a" });
        tab.Chat.Add(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, tab.Login, "viewer", "Viewer", null, [], "no id"));
        Dispatcher.UIThread.RunJobs();

        var lines = window.GetVisualDescendants().OfType<ChatLine>().ToList();
        static bool HasArrow(ChatLine l) =>
            l.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t => t.Text == "↩");
        Assert.True(HasArrow(lines.Single(l => l.Entry?.Message.Text == "has id")));
        Assert.False(HasArrow(lines.Single(l => l.Entry?.Message.Text == "no id")));
        window.Close();
    });

    [Fact]
    public void RightClickingALineOffersToReplyToIt() => Ui(() =>
    {
        var vm = Demo();
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        var tab = vm.FrontTab!;
        tab.Chat.Add(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, tab.Login, "viewer", "Viewer", null, [], "has id") { Id = "a" });
        tab.Chat.Add(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, tab.Login, "viewer", "Viewer", null, [], "no id"));
        Dispatcher.UIThread.RunJobs();

        var lines = window.GetVisualDescendants().OfType<ChatLine>().ToList();
        var column = lines[0].FindAncestorOfType<ChatColumnView>()!;
        var menu = column.FindControl<ScrollViewer>("ChatScroll")!.ContextMenu!;
        var reply = column.FindControl<MenuItem>("ReplyItem")!;
        // Right-clicked on the text with the mouse, as a user would: the
        // text's own Copy menu mustn't open instead.
        void RightClick(string text)
        {
            menu.Close();
            var block = lines.Single(l => l.Entry?.Message.Text == text).GetVisualDescendants().OfType<SelectableTextBlock>().First();
            var at = block.TranslatePoint(new Point(4, block.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Right);
            window.MouseUp(at, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.True(menu.IsOpen);
        }

        RightClick("no id");
        Assert.False(reply.IsVisible);

        RightClick("has id");
        Assert.True(reply.IsVisible);
        reply.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("has id", tab.Chat.ReplyTo?.Message.Text);
        menu.Close();
        window.Close();
    });

    [Fact]
    public void RightClickCopiesTheSelectedText() => Ui(() =>
    {
        var vm = Demo();
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        var tab = vm.FrontTab!;
        // A badge, a name and an emote: each an inline control in the text.
        tab.Chat.Add(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, tab.Login, "viewer", "Viewer", null,
            ["subscriber/12"], "copy Kappa words")
        {
            Id = "a",
            TwitchEmotes = [new TwitchEmoteRange("25", 5, 5)],
        });
        Dispatcher.UIThread.RunJobs();

        var line = window.GetVisualDescendants().OfType<ChatLine>().Single(l => l.Entry?.Message.Text == "copy Kappa words");
        var column = line.FindAncestorOfType<ChatColumnView>()!;
        var menu = column.FindControl<ScrollViewer>("ChatScroll")!.ContextMenu!;
        var copy = column.FindControl<MenuItem>("CopyItem")!;
        var block = line.GetVisualDescendants().OfType<SelectableTextBlock>().First();

        // Drag across the line with the mouse, then right-click on it.
        var from = block.TranslatePoint(new Point(1, block.Bounds.Height / 2), window)!.Value;
        var to = block.TranslatePoint(new Point(block.Bounds.Width - 1, block.Bounds.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.EndsWith("copy ￼ words", block.SelectedText);
        // As written: the name and the emote's code, no badge.
        var selected = $"{line.Entry!.Time} Viewer: copy Kappa words";

        // On the text, clear of the ↩ in the corner.
        var on = block.TranslatePoint(new Point(20, block.Bounds.Height / 2), window)!.Value;
        window.MouseDown(on, MouseButton.Right);
        window.MouseUp(on, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.True(menu.IsOpen);
        Assert.True(copy.IsVisible);

        copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
        Assert.Equal(selected, Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard).GetAwaiter().GetResult());
        menu.Close();
        window.Close();
    });

    [Fact]
    public void FavouritesGoingLiveAlertButNotAtStartUp() => Ui(() =>
    {
        var settings = new AppSettings { Favorites = ["fav", "later"] };
        var vm = Demo(settings);
        var alerts = new List<AppNotification>();
        vm.Notify += alerts.Add;
        LiveStream Live(string login) => new(login, login.ToUpperInvariant(), "Title", "Game", 10, DateTimeOffset.Now);

        vm.AlertGoLive([Live("fav"), Live("other")]);
        Assert.Empty(alerts);                           // already live when the app started

        vm.AlertGoLive([Live("fav"), Live("other"), Live("later"), Live("notfav")]);
        var alert = Assert.Single(alerts);              // only the favourite that just went live
        Assert.Equal("LATER is live", alert.Title);
        Assert.NotNull(alert.Open);

        settings.NotifyGoLive = false;
        vm.AlertGoLive([Live("fav")]);
        vm.AlertGoLive([Live("fav"), Live("later")]);
        Assert.Single(alerts);
    });

    [Fact]
    public void AlertsGoOnTheirOwnEvenWhenTheWindowIsMinimized() => Ui(() =>
    {
        var settings = new AppSettings { Favorites = ["a", "b", "c", "d"] };
        var vm = Demo(settings);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480, AlertTime = TimeSpan.FromMilliseconds(200) };
        window.Show();
        window.WindowState = WindowState.Minimized;
        var stack = window.FindControl<StackPanel>("Alerts")!;
        LiveStream Live(string login) => new(login, login.ToUpperInvariant(), "Title", "Game", 10, DateTimeOffset.Now);

        vm.AlertGoLive([]);
        vm.AlertGoLive([Live("a"), Live("b"), Live("c"), Live("d")]);
        Assert.Equal(3, stack.Children.Count);          // the oldest makes room

        // Gone on the timer alone: no closing animation to wait for, which
        // doesn't run while the window isn't drawn.
        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
            Dispatcher.UIThread.MainLoop(wait.Token);
        Assert.Empty(stack.Children);
        window.Close();
    });
}
