// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;
using StreamEdge.Chat;
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
    public void AWhisperShowsInTheOpenChatAndAlerts() => Ui(() =>
    {
        var vm = Demo();
        var alerts = new List<AppNotification>();
        vm.Notify += alerts.Add;

        vm.OnWhisper(new WhisperEvent("w1", "42", "glowingsun", "GlowingSun", "psst"));

        var line = vm.ActiveTab!.Chat.Lines[^1].Message;
        Assert.Equal(ChatMessageKind.Whisper, line.Kind);
        Assert.Equal("GlowingSun", line.Sender);
        Assert.Equal("psst", line.Text);
        Assert.Equal("Whisper from GlowingSun", Assert.Single(alerts).Title);

        // Its ↩ fills in a whisper back.
        vm.StartReply(vm.ActiveTab.Chat.Lines[^1]);
        Assert.Equal("/w glowingsun ", vm.ActiveTab.Chat.Draft);
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
}
