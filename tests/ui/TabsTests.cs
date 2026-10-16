// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

public class TabsTests(HeadlessAvalonia ui) : UiTest(ui)
{
    // Demo mode opens one tab (the sample channel) and creates no players.
    private static MainViewModel Demo(out AppSettings settings)
    {
        settings = new AppSettings();
        return new MainViewModel(settings, new StartupOptions { Demo = true }, InlineDispatcher.Instance);
    }

    [Fact]
    public void DoubleClickOpensInTheFrontTabInPlace() => Ui(() =>
    {
        var vm = Demo(out _);
        var item = vm.Streams.Items.First(i => i.Stream.Login == "bigtimebramble");

        item.OpenCommand.Execute(null);

        Assert.Single(vm.Tabs);
        Assert.Equal("bigtimebramble", vm.FrontTab?.Login);
        Assert.Same(vm.FrontTab!.Chat, vm.Chat);
    });

    [Fact]
    public void BackgroundTabsStayBehindAndSelectingBringsThemForward() => Ui(() =>
    {
        var vm = Demo(out var settings);
        var front = vm.FrontTab!;
        vm.Streams.Items.First(i => i.Stream.Login == "velvetcartridge").OpenInBackgroundCommand.Execute(null);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(front, vm.FrontTab);
        var background = vm.Tabs.Single(t => t.Login == "velvetcartridge");
        Assert.False(background.IsFront);
        Assert.Equal(["pixelperegrine", "velvetcartridge"], settings.OpenTabs);

        background.SelectCommand.Execute(null);
        Assert.Same(background, vm.FrontTab);
        Assert.True(background.IsFront);
        Assert.False(front.IsFront);
        Assert.Same(background.Chat, vm.Chat);
        Assert.Equal("velvetcartridge", settings.Channel);
    });

    [Fact]
    public void OpeningAnOpenChannelReusesItsTab() => Ui(() =>
    {
        var vm = Demo(out _);
        vm.Streams.Items.First(i => i.Stream.Login == "velvetcartridge").OpenInBackgroundCommand.Execute(null);
        vm.Streams.Items.First(i => i.Stream.Login == "velvetcartridge").OpenInNewTabCommand.Execute(null);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Equal("velvetcartridge", vm.FrontTab?.Login);
    });

    [Fact]
    public void ClosingTheFrontTabPromotesItsNeighbour() => Ui(() =>
    {
        var vm = Demo(out var settings);
        vm.Streams.Items.First(i => i.Stream.Login == "velvetcartridge").OpenInBackgroundCommand.Execute(null);

        vm.LeaveChannelCommand.Execute(null);

        Assert.Equal("velvetcartridge", Assert.Single(vm.Tabs).Login);
        Assert.Equal("velvetcartridge", vm.FrontTab?.Login);
        vm.LeaveChannelCommand.Execute(null);
        Assert.Empty(vm.Tabs);
        Assert.Null(vm.FrontTab);
        Assert.False(vm.HasChannel);
        Assert.Empty(settings.OpenTabs);
    });

    [Fact]
    public void PipThumbnailsLineUpBottomRightOverTheMainVideo()
    {
        var stage = new Size(853, 480);
        var rects = VideoStage.Place(stage, 16.0 / 9.0, showPip: true, [false, true, false, false]);

        Assert.Equal(new Rect(0, 0, 853, 480), rects[1]);
        var h = VideoStage.PipHeight(480);
        Assert.All(new[] { rects[0], rects[2], rects[3] }, r =>
        {
            Assert.Equal(h, r.Height);
            Assert.Equal(16.0 / 9.0, r.Width / r.Height, 1);
            Assert.True(r.Bottom <= 480 && r.Right <= 853 && r.X >= 0);
        });
        // In tab order, right to left, not overlapping each other.
        Assert.True(rects[0].X > rects[2].X && rects[2].X > rects[3].X);
        Assert.True(rects[2].Right <= rects[0].X);
    }

    [Fact]
    public void ThumbnailsThatDontFitAndPipOffAreHidden()
    {
        var stage = new Size(853, 480);
        var many = VideoStage.Place(stage, 16.0 / 9.0, showPip: true, [true, false, false, false, false, false, false]);
        Assert.Contains(many, r => r.Width == 0);

        var off = VideoStage.Place(stage, 16.0 / 9.0, showPip: false, [true, false]);
        Assert.Equal(0, off[1].Width);
    }
}
