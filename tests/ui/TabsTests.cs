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
    public void NoMoreThanSeventeenChannelsOpen() => Ui(() =>
    {
        var vm = Demo(out _);
        for (var i = 0; vm.Tabs.Count < MainViewModel.MaxTabs; i++)
            Assert.NotNull(vm.OpenChannel("extra" + i, OpenMode.NewTab));
        Assert.Equal(17, vm.Tabs.Count);

        Assert.Null(vm.OpenChannel("onetoomany", OpenMode.NewTab));
        Assert.Null(vm.OpenChannel("onetoomany", OpenMode.Background));
        Assert.Equal(17, vm.Tabs.Count);
        Assert.Contains(vm.ActiveTab!.Chat.Lines, l => l.Message.Text.Contains("close one to open onetoomany"));

        // Swapping the front tab's channel in place doesn't add one.
        Assert.NotNull(vm.OpenChannel("replacement", OpenMode.Front));
        Assert.Equal(17, vm.Tabs.Count);
    });

    [Fact]
    public void PipThumbnailsLineUpBottomRightOverTheMainVideo()
    {
        var stage = new Size(853, 480);
        var rects = VideoStage.Place(stage, 16.0 / 9.0, showPip: true, [false, true, false, false]).Select(p => p.Rect).ToList();

        Assert.Equal(new Rect(0, 0, 853, 480), rects[1]);
        var pip = VideoStage.PipSize(stage, 16.0 / 9.0);
        Assert.All(new[] { rects[0], rects[2], rects[3] }, r =>
        {
            Assert.Equal(pip, r.Size);
            Assert.Equal(16.0 / 9.0, r.Width / r.Height, 1);
            Assert.True(r.Bottom <= 480 && r.Right <= 853 && r.X >= 0);
        });
        // In tab order, right to left along the bottom, not overlapping.
        Assert.True(rects[0].X > rects[2].X && rects[2].X > rects[3].X);
        Assert.True(rects[2].Right <= rects[0].X);
        Assert.Equal(rects[0].Y, rects[3].Y);
    }

    [Theory]
    [InlineData(853, 480)]
    [InlineData(1920, 480)]
    [InlineData(640, 480)]
    public void TheFourByFourGridReachesEveryCornerWithoutOverlap(double width, double height)
    {
        var stage = new Size(width, height);
        const double aspect = 16.0 / 9.0;
        Rect At(int c, int r) => VideoStage.CellRect(stage, aspect, new PipCell(c, r));

        // The outer cells sit a margin in from each corner.
        Assert.Equal(width - 8, At(0, 0).Right, 1.0);
        Assert.Equal(height - 8, At(0, 0).Bottom, 1.0);
        Assert.Equal(8, At(3, 3).X, 1.0);
        Assert.Equal(8, At(3, 3).Y, 1.0);
        // Evenly spaced, and neighbours never overlap.
        var stepX = At(0, 0).X - At(1, 0).X;
        var stepY = At(0, 0).Y - At(0, 1).Y;
        for (var i = 1; i < 3; i++)
        {
            Assert.Equal(stepX, At(i, 0).X - At(i + 1, 0).X, 1.0);
            Assert.Equal(stepY, At(0, i).Y - At(0, i + 1).Y, 1.0);
        }
        Assert.True(stepX >= At(0, 0).Width && stepY >= At(0, 0).Height);
    }

    [Fact]
    public void SixteenThumbnailsFitAndPipOffHidesThem()
    {
        var stage = new Size(853, 480);
        var front = new[] { true }.Concat(Enumerable.Repeat(false, 17)).ToList();
        var many = VideoStage.Place(stage, 16.0 / 9.0, showPip: true, front).Select(p => p.Rect).ToList();
        Assert.Equal(16, many.Skip(1).Count(r => r.Width > 0));
        Assert.Equal(0, many[^1].Width);

        var off = VideoStage.Place(stage, 16.0 / 9.0, showPip: false, [true, false]).Select(p => p.Rect).ToList();
        Assert.Equal(0, off[1].Width);
    }

    [Fact]
    public void DraggedThumbnailsKeepTheirCellsAndTheRestFillTheBottomRow()
    {
        var stage = new Size(853, 480);
        const double aspect = 16.0 / 9.0;

        // One dragged to the top-left corner, one to the bottom-right cell;
        // the third, never moved, takes the next free cell along the bottom.
        var top = new PipCell(3, 3);
        var placed = VideoStage.Place(stage, aspect, showPip: true, [true, false, false, false],
            [null, top, new PipCell(0, 0), null]);
        Assert.Equal(top, placed[1].Cell);
        Assert.Equal(new PipCell(0, 0), placed[2].Cell);
        Assert.Equal(new PipCell(1, 0), placed[3].Cell);
        Assert.Equal(VideoStage.CellRect(stage, aspect, top), placed[1].Rect);

        // Two wanting one cell: the first keeps it. A cell off the grid (an
        // older, bigger grid) counts as never moved.
        var clash = VideoStage.Place(stage, aspect, showPip: true, [false, false, false],
            [new PipCell(0, 0), new PipCell(0, 0), new PipCell(7, 0)]);
        Assert.Equal(new PipCell(0, 0), clash[0].Cell);
        Assert.Equal(new PipCell(1, 0), clash[1].Cell);
        Assert.Equal(new PipCell(2, 0), clash[2].Cell);

        // A full bottom row: the next goes up a row.
        var five = VideoStage.Place(stage, aspect, showPip: true, [false, false, false, false, false]);
        Assert.Equal(new PipCell(0, 1), five[4].Cell);
    }

    [Fact]
    public void ADroppedThumbnailSnapsToTheNearestCellOnTheGrid()
    {
        var stage = new Size(853, 480);
        const double aspect = 16.0 / 9.0;
        var cell = VideoStage.CellRect(stage, aspect, new PipCell(1, 1));

        // Most of the way into a cell, it lands there.
        Assert.Equal(new PipCell(1, 1), VideoStage.NearestCell(stage, aspect, cell.Translate(new Vector(30, -20))));
        Assert.Equal(new PipCell(2, 1), VideoStage.NearestCell(stage, aspect, cell.Translate(new Vector(-cell.Width * 0.7, 0))));
        // Thrown off the stage, it stays on the grid.
        Assert.Equal(new PipCell(3, 3), VideoStage.NearestCell(stage, aspect, cell.Translate(new Vector(-5000, -5000))));
        Assert.Equal(new PipCell(0, 0), VideoStage.NearestCell(stage, aspect, cell.Translate(new Vector(5000, 5000))));
    }

    [Fact]
    public void ADraggedCellIsSavedPerChannel() => Ui(() =>
    {
        var settings = new AppSettings();
        var vm = new MainViewModel(settings, new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var tab = vm.FrontTab!;

        vm.MovePips([(tab, new PipCell(2, 1))]);

        Assert.Equal(new PipCell(2, 1), tab.PipCell);
        Assert.Equal(new PipCell(2, 1), settings.PipCells[tab.Login]);
        var reopened = new MainViewModel(settings, new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        Assert.Equal(new PipCell(2, 1), reopened.Tabs.Single(t => t.Login == tab.Login).PipCell);
    });
}
