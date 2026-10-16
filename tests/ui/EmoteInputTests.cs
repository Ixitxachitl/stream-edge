// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;
using StreamEdge.AvaloniaApp.Views.Emotes;
using StreamEdge.Chat.Emotes;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

public class EmoteInputTests(HeadlessAvalonia ui) : UiTest(ui)
{
    private static Emote E(string code) => new(code, new Uri($"https://img.example/{code}"), EmoteProvider.Twitch);

    /// <summary>The demo window with sending allowed and a few emotes of
    /// the user's own; the chat box focused.</summary>
    private static (MainWindow Window, MainViewModel Vm, EmoteTextBox Input) Open()
    {
        var vm = new MainViewModel(new AppSettings { ShowPronouns = false }, new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        vm.FrontTab!.Chat.RoomId = "100";
        vm.FrontTab.Chat.CanSend = true;
        vm.Services.SetMyEmotes(
        [
            new UserEmote(E("Kappa"), "0", "globals"),
            new UserEmote(E("KappaPride"), "0", "globals"),
            new UserEmote(E("Keepo"), "0", "globals"),
            new UserEmote(E("chanHype"), "100", "subscriptions"),
        ], null, replace: true);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var input = window.GetVisualDescendants().OfType<EmoteTextBox>().Single();
        input.Focus();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, input);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Type(Window window, string text)
    {
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void TabCompletesTheWordAndCyclesThroughTheMatches() => Ui(() =>
    {
        var (window, vm, input) = Open();
        Type(window, "hi K");

        Press(window, Key.Tab);
        Assert.Equal("hi Kappa", vm.Chat.Draft);
        Assert.True(input.IsSuggesting);
        Assert.True(input.IsFocused); // Tab didn't move focus away
        Press(window, Key.Tab);
        Assert.Equal("hi Keepo", vm.Chat.Draft);
        Press(window, Key.Tab);
        Assert.Equal("hi KappaPride", vm.Chat.Draft);
        Press(window, Key.Tab, RawInputModifiers.Shift);
        Assert.Equal("hi Keepo", vm.Chat.Draft);

        // Typing on ends the cycle.
        Type(window, " ok");
        Assert.Equal("hi Keepo ok", vm.Chat.Draft);
        Assert.False(input.IsSuggesting);
        window.Close();
    });

    [Fact]
    public void AColonWordListsMatchesAndEnterPicksWithoutSending() => Ui(() =>
    {
        var (window, vm, input) = Open();
        Type(window, ":h");
        Assert.False(input.IsSuggesting); // too short yet
        Type(window, "yp");
        Assert.True(input.IsSuggesting);
        Assert.Equal(["chanHype"], input.Suggestions.Select(e => e.Code));
        var popup = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single(p => p.Name == "SuggestionPopup");
        Assert.True(popup.IsOpen);

        Press(window, Key.Enter);
        Assert.Equal("chanHype ", vm.Chat.Draft);
        Assert.False(input.IsSuggesting);
        window.Close();
    });

    [Fact]
    public void EscapeClosesTheListUntilTheWordChanges() => Ui(() =>
    {
        var (window, _, input) = Open();
        Type(window, ":kap");
        Assert.True(input.IsSuggesting);
        Press(window, Key.Escape);
        Assert.False(input.IsSuggesting);
        Type(window, "p");
        Assert.True(input.IsSuggesting);
        window.Close();
    });

    [Fact]
    public void EmoteCodesAreLaidOutAsImagesInTheBox() => Ui(() =>
    {
        var (window, vm, input) = Open();
        Type(window, "hi Kappa there");
        var presenter = input.GetVisualDescendants().OfType<EmoteTextPresenter>().Single();
        var runs = presenter.TextLayout.TextLines.SelectMany(l => l.TextRuns).ToList();
        var emote = Assert.Single(runs.OfType<DrawableTextRun>(), r => r is not ShapedTextRun);
        Assert.Equal("Kappa", emote.Text.ToString());
        // The caret steps over the emote whole.
        input.CaretIndex = 3;
        Press(window, Key.Right);
        Assert.Equal(8, input.CaretIndex);

        // Not an emote: plain text.
        input.Text = "hi Kapp";
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(presenter.TextLayout.TextLines.SelectMany(l => l.TextRuns), r => r is DrawableTextRun and not ShapedTextRun);

        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT") is { Length: > 0 } path)
        {
            // The real Kappa, for eyeballing how an emote sits in the line.
            var kappa = new Uri("https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/1.0");
            vm.Services.SetMyEmotes([new UserEmote(new Emote("Kappa", kappa, EmoteProvider.Twitch), "0", "globals")], null, replace: true);
            EmoteImageCache.GetAsync(kappa).GetAwaiter().GetResult();
            input.Text = "hi Kappa there";
            Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618
            window.CaptureRenderedFrame()!.Save(path.Replace(".png", "-input.png"));
#pragma warning restore CS0618
        }
        window.Close();
    });

    [Fact]
    public void AGifInChatIsDrawnAsAPicture() => Ui(() =>
    {
        var (window, vm, _) = Open();
        const string url = "https://media4.giphy.com/media/joSNxeswxuc74Juo8X/giphy.gif";
        var line = StreamEdge.Chat.ChatMessage.FromIrc(StreamEdge.Chat.IrcMessage.Parse(
            $"@display-name=Alice;gifs=0-33|joSNxeswxuc74Juo8X|{url};id=g1 :alice!alice@alice.tmi.twitch.tv PRIVMSG #pixelperegrine :[Y A Y Yes GIF by Djemilah Birnie]"),
            DateTimeOffset.Now)!;
        vm.FrontTab!.Chat.Add(line);
        Dispatcher.UIThread.RunJobs();

        var entry = vm.FrontTab.Chat.Lines[^1];
        Assert.Contains(entry.Tokens, t => t.Kind == StreamEdge.Chat.ChatTokenKind.Gif);
        var view = window.GetVisualDescendants().OfType<EmoteView>().Last();
        Assert.Equal(100, view.EmoteHeight);

        // Off: just the title.
        vm.ShowGifs = false;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(vm.FrontTab.Chat.Lines[^1].Tokens, t => t.Kind == StreamEdge.Chat.ChatTokenKind.Gif);
        vm.ShowGifs = true;
        Dispatcher.UIThread.RunJobs();

        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT") is { Length: > 0 } path)
        {
            // The real GIF, for eyeballing.
            EmoteImageCache.GetAsync(new Uri(url)).GetAwaiter().GetResult();
            vm.FrontTab.Chat.RenderAll();
            Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618
            window.CaptureRenderedFrame()!.Save(path.Replace(".png", "-gif.png"));
#pragma warning restore CS0618
        }
        window.Close();
    });
}
