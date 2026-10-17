// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;
using StreamEdge.Chat;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

/// <summary>Names, badges and text on a chat line share one baseline, as
/// on twitch.tv, even with a tall GIF on the line.</summary>
public class ChatLineAlignmentTests(HeadlessAvalonia ui) : UiTest(ui)
{
    private const string Gif = "https://media4.giphy.com/media/joSNxeswxuc74Juo8X/giphy.gif";

    private static ChatMessage Line(string text, string? gifs = null) => ChatMessage.FromIrc(IrcMessage.Parse(
        $"@badges=moderator/1,subscriber/0;color=#5DA9A9;display-name=Wax_leftpaw{(gifs is null ? "" : ";gifs=" + gifs)};id={Guid.NewGuid()} "
        + $":wax_leftpaw!wax_leftpaw@wax_leftpaw.tmi.twitch.tv PRIVMSG #pixelperegrine :{text}"), DateTimeOffset.Now)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NameBadgesAndTextShareTheBaseline(bool withGif) => Ui(() =>
    {
        var vm = new MainViewModel(new AppSettings(), new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        vm.FrontTab!.Chat.Add(withGif
            ? Line("[HUH GIF]", $"0-8|abc|{Gif}")
            : Line("but where"));
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();

        var line = window.GetVisualDescendants().OfType<ChatLine>().Last();
        var text = line.GetVisualDescendants().OfType<SelectableTextBlock>().Last();
        var baseline = text.Padding.Top + text.TextLayout.TextLines[0].Baseline;
        var em = text.GetValue(TextElement.FontSizeProperty);

        var name = line.GetVisualDescendants().OfType<InlineTextBlock>().First();
        Assert.Equal("Wax_leftpaw", name.Text);
        var nameBaseline = name.TranslatePoint(new Point(0, TextBlock.GetBaselineOffset(name)), text)!.Value.Y;
        Assert.Equal(baseline, nameBaseline, 1.0);

        var badges = line.GetVisualDescendants().OfType<InlineMiddle>().Take(2).ToList();
        Assert.Equal(2, badges.Count);
        foreach (var badge in badges)
        {
            var middle = badge.TranslatePoint(new Point(0, badge.Bounds.Height / 2), text)!.Value.Y;
            Assert.Equal(baseline - em * InlineMiddle.TextMiddle, middle, 1.0);
        }

        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT") is { Length: > 0 } path)
#pragma warning disable CS0618
            window.CaptureRenderedFrame()!.Save(path.Replace(".png", withGif ? "-align-gif.png" : "-align.png"));
#pragma warning restore CS0618
        window.Close();
    });

    [Fact]
    public void AGiganticEmoteStandsOnTheBaselineAtTwitchsLargestSize() => Ui(() =>
    {
        var vm = new MainViewModel(new AppSettings(), new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        vm.FrontTab!.Chat.Add(ChatMessage.FromIrc(IrcMessage.Parse(
            "@color=#5DA9A9;display-name=Wax_leftpaw;emotes=25:6-10/354:12-16;msg-id=gigantified-emote-message;id=" + Guid.NewGuid()
            + " :wax_leftpaw!wax_leftpaw@wax_leftpaw.tmi.twitch.tv PRIVMSG #pixelperegrine :look! Kappa 4Head"), DateTimeOffset.Now)!);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();

        var line = window.GetVisualDescendants().OfType<ChatLine>().Last();
        var text = line.GetVisualDescendants().OfType<SelectableTextBlock>().Last();
        var baseline = text.Padding.Top + text.TextLayout.TextLines[^1].Baseline;
        var emotes = line.GetVisualDescendants().OfType<AvaloniaApp.Views.Emotes.EmoteView>().ToList();
        Assert.Equal(2, emotes.Count);
        var (normal, gigantic) = (emotes[0], emotes[1]);

        // The last Twitch emote, four times the size, from the 3.0 image.
        Assert.Equal(4 * normal.Bounds.Height, gigantic.Bounds.Height, 1.0);
        Assert.EndsWith("/354/default/dark/3.0", gigantic.Source!.AbsoluteUri);
        Assert.EndsWith("/25/default/dark/2.0", normal.Source!.AbsoluteUri);

        // Its bottom on the text's baseline, not centred on the text.
        var bottom = gigantic.TranslatePoint(new Point(0, gigantic.Bounds.Height), text)!.Value.Y;
        Assert.Equal(baseline, bottom, 1.0);
        window.Close();
    });
}
