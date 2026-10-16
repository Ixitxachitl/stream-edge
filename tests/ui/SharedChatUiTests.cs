// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;
using StreamEdge.Chat;
using StreamEdge.Demo;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

/// <summary>The demo channel shares its chat with CometCrumb and MossyLedge.</summary>
public class SharedChatUiTests(HeadlessAvalonia ui) : UiTest(ui)
{
    private static MainViewModel Demo() =>
        new(new AppSettings(), new StartupOptions { Demo = true }, InlineDispatcher.Instance);

    [Fact]
    public void TheStripNamesTheOtherChannels() => Ui(() =>
    {
        var vm = Demo();
        var chat = vm.FrontTab!.Chat;
        Assert.True(chat.IsShared);
        Assert.Equal(["PixelPeregrine", "CometCrumb", "MossyLedge"], chat.SharedChannels.Select(c => c.Name));
        Assert.Equal("Shared chat with CometCrumb, MossyLedge", chat.SharedSummary);
    });

    [Fact]
    public void LinesCarryTheirChannelAndOnlyLocalOnesCanBeDeleted() => Ui(() =>
    {
        var vm = Demo();
        var chat = vm.FrontTab!.Chat;
        var fromComet = chat.Lines.Single(l => l.Message.Login == "orbitwren");
        Assert.Equal("CometCrumb", fromComet.Source?.DisplayName);
        Assert.True(fromComet.IsFromOtherRoom);
        // The badges it has in CometCrumb's chat.
        Assert.Equal(["subscriber"], fromComet.Badges.Select(b => b.Set));

        var local = chat.Lines.First(l => l.Message.Login == "chattynarwhal");
        Assert.Equal("PixelPeregrine", local.Source?.DisplayName);
        Assert.False(local.IsFromOtherRoom);

        chat.IsModerator = true;
        Assert.DoesNotContain("🗑", Glyphs(new ChatLine { Entry = fromComet }));
        Assert.Contains("⛔", Glyphs(new ChatLine { Entry = fromComet }));
    });

    private static List<string> Glyphs(ChatLine line)
    {
        var text = (SelectableTextBlock)((StackPanel)line.Child!).Children[^1];
        return [.. text.Inlines!.OfType<Avalonia.Controls.Documents.InlineUIContainer>()
            .Select(c => c.Child is InlineMiddle m ? m.Child : c.Child).OfType<TextBlock>().Select(t => t.Text ?? "")];
    }

    [Fact]
    public void ABanInAnotherChannelOnlyTakesWhatWasSaidThere() => Ui(() =>
    {
        var vm = Demo();
        var chat = vm.FrontTab!.Chat;
        var comet = SampleData.RoomId("cometcrumb");
        chat.Add(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, "pixelperegrine", "orbitwren", "OrbitWren", null, [], "said here")
            { SourceRoomId = chat.RoomId, UserId = "9" });
        chat.Apply(new ChatModeration(ChatModerationKind.ClearUser, "pixelperegrine", DateTimeOffset.Now)
            { TargetLogin = "orbitwren", SourceRoomId = comet });

        Assert.True(chat.Lines.Single(l => l.Message.Text == "hi from Comet's chat!").IsDeleted);
        Assert.False(chat.Lines.Single(l => l.Message.Text == "said here").IsDeleted);
        Assert.Equal("orbitwren has been banned in CometCrumb.", chat.Lines[^1].Message.Text);
    });

    [Fact]
    public void AnUntaggedLineMeansTheSessionEnded() => Ui(() =>
    {
        var vm = Demo();
        var chat = vm.FrontTab!.Chat;
        chat.Add(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, "pixelperegrine", "bob", "bob", null, [], "back to normal"));
        Assert.False(chat.IsShared);
        Assert.Empty(chat.SharedChannels);

        // And a tagged one from elsewhere starts one again.
        chat.Add(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, "pixelperegrine", "x", "x", null, [], "hi")
            { SourceRoomId = SampleData.RoomId("mossyledge") });
        Assert.True(chat.IsShared);
        Assert.Equal(["PixelPeregrine", "MossyLedge"], chat.SharedChannels.Select(c => c.Name));
    });

    [Fact]
    public void OwnLinesInASessionCountAsSentHere() => Ui(() =>
    {
        var vm = Demo();
        var chat = vm.FrontTab!.Chat;
        chat.AddOwn(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, "pixelperegrine", "me", "Me", null, [], "mine"));
        Assert.Equal(chat.RoomId, chat.Lines[^1].Message.SourceRoomId);
        Assert.True(chat.IsShared);
    });

    [Fact]
    public void TheStreamListShowsWhoSharesChat() => Ui(() =>
    {
        var vm = Demo();
        var comet = vm.Streams.Items.Single(i => i.Stream.Login == "cometcrumb");
        Assert.True(comet.IsSharedChat);
        Assert.Equal("Shared chat with PixelPeregrine, MossyLedge", comet.SharedText);
        Assert.Equal(["PixelPeregrine", "MossyLedge"], comet.SharedAvatars.Select(a => a.Name));
        Assert.False(vm.Streams.Items.Single(i => i.Stream.Login == "bigtimebramble").IsSharedChat);

        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var strip = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("sharedStrip"));
        Assert.True(strip.IsEffectivelyVisible);
        var frame = window.CaptureRenderedFrame();
        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT") is { Length: > 0 } path)
#pragma warning disable CS0618
            frame!.Save(path.Replace(".png", "-shared.png"));
#pragma warning restore CS0618
        window.Close();
    });
}
