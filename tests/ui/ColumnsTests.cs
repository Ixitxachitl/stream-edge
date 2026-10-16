// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

public class ColumnsTests(HeadlessAvalonia ui) : UiTest(ui)
{
    // Demo: one column with the sample channel in front; two more opened behind it.
    private static MainViewModel Demo(out AppSettings settings)
    {
        settings = new AppSettings();
        var vm = new MainViewModel(settings, new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        vm.OpenChannel("velvetcartridge", OpenMode.Background);
        vm.OpenChannel("bigtimebramble", OpenMode.Background);
        return vm;
    }

    private static List<string> Logins(ChatColumnViewModel column) => [.. column.Tabs.Select(t => t.Login)];

    private static ChannelTabViewModel Tab(MainViewModel vm, string login) => vm.Tabs.Single(t => t.Login == login);

    [Fact]
    public void TabsReorderWithinAStrip() => Ui(() =>
    {
        var vm = Demo(out var settings);
        var column = Assert.Single(vm.Columns);

        vm.MoveTab(Tab(vm, "bigtimebramble"), column, 0);
        Assert.Equal(["bigtimebramble", "pixelperegrine", "velvetcartridge"], Logins(column));
        // Indexes count the strip before the move: "after velvetcartridge" is 3.
        vm.MoveTab(Tab(vm, "bigtimebramble"), column, 3);
        Assert.Equal(["pixelperegrine", "velvetcartridge", "bigtimebramble"], Logins(column));
        Assert.Equal(["pixelperegrine", "velvetcartridge", "bigtimebramble"], settings.OpenTabs);
    });

    [Fact]
    public void SplittingMakesANewColumnWithoutChangingTheVideo() => Ui(() =>
    {
        var vm = Demo(out var settings);
        var front = vm.FrontTab!;
        var velvet = Tab(vm, "velvetcartridge");

        vm.MoveTabToNewColumn(velvet, 1);

        Assert.Equal(2, vm.Columns.Count);
        Assert.Equal(["pixelperegrine", "bigtimebramble"], Logins(vm.Columns[0]));
        Assert.Equal(["velvetcartridge"], Logins(vm.Columns[1]));
        Assert.Same(velvet, vm.Columns[1].Selected);
        Assert.Same(front, vm.FrontTab); // still on video
        Assert.True(vm.Columns[0].HasFront);
        Assert.False(vm.Columns[1].HasFront);
        // The PIP switch stays in the rightmost column, wherever the video is.
        Assert.False(vm.Columns[0].IsLast);
        Assert.True(vm.Columns[1].IsLast);
        // The new column is the active one: the chat box acts on it.
        Assert.Same(velvet.Chat, vm.Chat);
        Assert.Equal(2, settings.ChatColumns.Count);
        Assert.Equal("velvetcartridge", settings.ChatColumns[1].Selected);
    });

    [Fact]
    public void TypingInAColumnDoesNotMoveTheVideoButClickingATabDoes() => Ui(() =>
    {
        var vm = Demo(out _);
        var front = vm.FrontTab!;
        var velvet = Tab(vm, "velvetcartridge");
        vm.MoveTabToNewColumn(velvet, 1);

        vm.Activate(vm.Columns[0]);
        Assert.Same(front.Chat, vm.Chat);
        vm.Activate(vm.Columns[1]);
        Assert.Same(velvet.Chat, vm.Chat);
        Assert.Same(front, vm.FrontTab);

        velvet.SelectCommand.Execute(null);
        Assert.Same(velvet, vm.FrontTab);
        Assert.True(vm.Columns[1].HasFront);
        Assert.False(vm.Columns[0].HasFront);
    });

    [Fact]
    public void AColumnEmptiedByDraggingOrClosingGoesAway() => Ui(() =>
    {
        var vm = Demo(out _);
        var velvet = Tab(vm, "velvetcartridge");
        vm.MoveTabToNewColumn(velvet, 0);
        Assert.Equal(["velvetcartridge"], Logins(vm.Columns[0]));

        // Dragged back into the other column.
        vm.MoveTab(velvet, vm.Columns[1], 1);
        var column = Assert.Single(vm.Columns);
        Assert.Equal(["pixelperegrine", "velvetcartridge", "bigtimebramble"], Logins(column));

        // Closed: the column goes, and the active one moves to its neighbour.
        vm.MoveTabToNewColumn(velvet, 1);
        velvet.CloseCommand.Execute(null);
        Assert.Single(vm.Columns);
        Assert.Same(vm.Columns[0], vm.ActiveColumn);
    });

    [Fact]
    public void ALoneTabDraggedToAnEdgeMovesItsColumn() => Ui(() =>
    {
        var vm = Demo(out _);
        vm.MoveTabToNewColumn(Tab(vm, "velvetcartridge"), 1);
        vm.MoveTabToNewColumn(Tab(vm, "bigtimebramble"), 2);
        Assert.Equal(["velvetcartridge"], Logins(vm.Columns[1]));

        Assert.False(vm.CanSplit(Tab(vm, "bigtimebramble"), 2)); // beside itself
        vm.MoveTabToNewColumn(Tab(vm, "bigtimebramble"), 0);
        Assert.Equal(3, vm.Columns.Count);
        Assert.Equal(["bigtimebramble"], Logins(vm.Columns[0]));
        Assert.Equal(["pixelperegrine"], Logins(vm.Columns[1]));
    });

    [Fact]
    public void ClosingTheTabOnVideoHandsVideoToWhatItsColumnNowShows() => Ui(() =>
    {
        var vm = Demo(out _);
        var velvet = Tab(vm, "velvetcartridge");
        vm.MoveTabToNewColumn(velvet, 1);
        vm.MoveTabToNewColumn(Tab(vm, "bigtimebramble"), 2);
        velvet.SelectCommand.Execute(null);

        velvet.CloseCommand.Execute(null);

        Assert.Equal(2, vm.Columns.Count);
        Assert.NotNull(vm.FrontTab);
        Assert.Contains(vm.FrontTab, vm.Columns.Select(c => c.Selected));
    });

    [Fact]
    public void DropTargetsFollowThePointer() => Ui(() =>
    {
        var vm = Demo(out _);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var pane = window.FindControl<Control>("ChatPane")!;
        var (w, h) = (pane.Bounds.Width, pane.Bounds.Height);
        var velvet = Tab(vm, "velvetcartridge");

        // Right edge, over the chat: a new column to the right.
        var split = window.DropTargetAt(velvet, new Point(w - 10, h / 2))!;
        Assert.True(split.NewColumn);
        Assert.Equal(1, split.Index);
        // Left edge: a new column on the left.
        Assert.Equal(0, window.DropTargetAt(velvet, new Point(10, h / 2))!.Index);
        // The middle of its own column: nothing to do.
        Assert.Null(window.DropTargetAt(velvet, new Point(w / 2, h / 2)));
        // Left end of the tab strip: first place.
        var first = window.DropTargetAt(velvet, new Point(3, 12))!;
        Assert.False(first.NewColumn);
        Assert.Equal(0, first.Index);

        vm.MoveTabToNewColumn(velvet, split.Index);
        Dispatcher.UIThread.RunJobs();
        var views = window.GetVisualDescendants().OfType<ChatColumnView>().ToList();
        Assert.Equal(2, views.Count);
        Assert.Equal(w / 2, views[0].Bounds.Width, 0);

        // Into the other column's middle: appended there.
        var into = window.DropTargetAt(Tab(vm, "bigtimebramble"), new Point(w * 0.75, h / 2))!;
        Assert.False(into.NewColumn);
        Assert.Same(vm.Columns[1], into.Column);
        Assert.Equal(1, into.Index);

        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT") is { Length: > 0 } path)
#pragma warning disable CS0618
            window.CaptureRenderedFrame()!.Save(path.Replace(".png", "-columns.png"));
#pragma warning restore CS0618
        window.Close();
    });

    [Fact]
    public void StackingPutsAChatAboveOrBelowAnotherAndIsRemembered() => Ui(() =>
    {
        var vm = Demo(out var settings);
        var (pixel, velvet, bramble) = (vm.FrontTab!, Tab(vm, "velvetcartridge"), Tab(vm, "bigtimebramble"));

        vm.MoveTabToStack(velvet, vm.Columns[0], below: true);
        Assert.Equal([["pixelperegrine", "bigtimebramble"], ["velvetcartridge"]], vm.Columns.Select(Logins));
        Assert.Equal([false, true], vm.Columns.Select(c => c.StacksBelow));
        Assert.Equal(1, vm.VisibleColumnCount);
        Assert.Same(pixel, vm.FrontTab); // stacking doesn't touch the video

        vm.MoveTabToStack(bramble, vm.Columns[0], below: false);
        Assert.Equal([["bigtimebramble"], ["pixelperegrine"], ["velvetcartridge"]], vm.Columns.Select(Logins));
        Assert.Equal([false, true, true], vm.Columns.Select(c => c.StacksBelow));
        Assert.Equal([false, true, true], settings.ChatColumns.Select(c => c.Below));
        // The PIP switch stays in the top chat.
        Assert.True(vm.Columns[0].IsLast);

        // The top chat leaving its column: the one under it heads it now.
        vm.MoveTabToNewColumn(bramble, 3);
        Assert.Equal([["pixelperegrine"], ["velvetcartridge"], ["bigtimebramble"]], vm.Columns.Select(Logins));
        Assert.Equal([false, true, false], vm.Columns.Select(c => c.StacksBelow));
        Assert.Equal(2, vm.VisibleColumnCount);
        Assert.True(vm.Columns[2].IsLast);

        // A chat alone in its spot can't stack onto itself or where it already is.
        Assert.False(vm.CanStack(velvet, vm.Columns[1], below: true));
        Assert.False(vm.CanStack(velvet, vm.Columns[0], below: true));
        Assert.True(vm.CanStack(velvet, vm.Columns[0], below: false));
    });

    [Fact]
    public void TopAndBottomEdgesStackAndTheChatsShareTheHeight() => Ui(() =>
    {
        // A 4:3 screen, video above the list: a tall chat pane, where
        // stacking pays off. (Three across at 1080p leaves chat 180 px wide.)
        var vm = Demo(out var settings);
        settings.VideoAboveList = true;
        var window = new MainWindow { DataContext = vm, Width = 1024, Height = 768 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var pane = window.FindControl<Control>("ChatPane")!;
        var (w, h) = (pane.Bounds.Width, pane.Bounds.Height);
        var velvet = Tab(vm, "velvetcartridge");

        var bottom = window.DropTargetAt(velvet, new Point(w / 2, h - 20))!;
        Assert.True(bottom.StackBelow);
        Assert.False(bottom.NewColumn);
        Assert.False(window.DropTargetAt(velvet, new Point(w / 2, 80))!.StackBelow);
        // The sides still make columns.
        Assert.True(window.DropTargetAt(velvet, new Point(w - 10, h / 2))!.NewColumn);

        vm.MoveTabToStack(velvet, bottom.Column!, below: true);
        Dispatcher.UIThread.RunJobs();
        var views = window.GetVisualDescendants().OfType<ChatColumnView>().ToList();
        Assert.Equal(2, views.Count);
        Rect InPane(Control c) => new(c.TranslatePoint(default, pane)!.Value, c.Bounds.Size);
        var (top, under) = (InPane(views[0]), InPane(views[1]));
        Assert.Equal(top.X, under.X);
        Assert.Equal(w, top.Width);
        Assert.InRange(Math.Abs(top.Height - under.Height), 0, 1); // whole pixels: an odd one goes below
        Assert.True(under.Y > top.Bottom); // a gap between them
        Assert.Equal(h, under.Bottom, 0);

        // Over the lower chat's left edge: a new column left of the whole stack.
        var left = window.DropTargetAt(Tab(vm, "bigtimebramble"), new Point(10, h * 0.75))!;
        Assert.True(left.NewColumn);
        Assert.Equal(0, left.Index);

        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT") is { Length: > 0 } path)
#pragma warning disable CS0618
            window.CaptureRenderedFrame()!.Save(path.Replace(".png", "-stacked.png"));
#pragma warning restore CS0618
        window.Close();
    });

    [Fact]
    public void DraggingATabWithTheMouseSplitsItOffAndAClickStillSelects() => Ui(() =>
    {
        var vm = Demo(out _);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var pane = window.FindControl<Control>("ChatPane")!;
        Point TabCenter(string login)
        {
            var border = window.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("tab") && b.DataContext is ChannelTabViewModel t && t.Login == login);
            return border.TranslatePoint(new Point(border.Bounds.Width / 2, border.Bounds.Height / 2), window)!.Value;
        }
        var paneRight = pane.TranslatePoint(new Point(pane.Bounds.Width - 10, pane.Bounds.Height / 2), window)!.Value;

        // A plain click selects (and puts on video), no drag.
        var bramble = TabCenter("bigtimebramble");
        window.MouseDown(bramble, MouseButton.Left);
        window.MouseUp(bramble, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("bigtimebramble", vm.FrontTab?.Login);
        Assert.Single(vm.Columns);

        // Press on velvetcartridge, drag to the chat's right edge, let go.
        var velvet = TabCenter("velvetcartridge");
        window.MouseDown(velvet, MouseButton.Left);
        window.MouseMove(velvet + new Point(20, 0));
        window.MouseMove(paneRight);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.FindControl<Border>("DropIndicator")!.IsVisible);
        window.MouseUp(paneRight, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.Columns.Count);
        Assert.Equal("velvetcartridge", Assert.Single(vm.Columns[1].Tabs).Login);
        Assert.Equal("bigtimebramble", vm.FrontTab?.Login); // dragging doesn't change the video
        Assert.False(window.FindControl<Border>("DropIndicator")!.IsVisible);
        window.Close();
    });

    [Fact]
    public void ChatSticksToTheBottomThroughHistoryUntilScrolledUp() => Ui(() =>
    {
        var vm = Demo(out _);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "ChatScroll");
        bool AtBottom() => scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1;
        static StreamEdge.Chat.ChatMessage Line(string login, int i) =>
            new(DateTimeOffset.Now, StreamEdge.Chat.ChatMessageKind.Message, login, "someone", "Someone", null, [], $"history line {i}");

        // History arrives for a tab in the background, then it's opened.
        var velvet = Tab(vm, "velvetcartridge");
        velvet.Chat.InsertHistory([.. Enumerable.Range(0, 120).Select(i => Line("velvetcartridge", i))], "test");
        velvet.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height * 2);
        Assert.True(AtBottom());

        // History arriving for the tab on screen keeps the end in view.
        var bramble = Tab(vm, "bigtimebramble");
        bramble.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        bramble.Chat.InsertHistory([.. Enumerable.Range(0, 120).Select(i => Line("bigtimebramble", i))], "test");
        Dispatcher.UIThread.RunJobs();
        Assert.True(AtBottom());

        // Scrolled up to read: new lines don't pull the view down.
        scroll.Offset = scroll.Offset.WithY(0);
        Dispatcher.UIThread.RunJobs();
        bramble.Chat.Add(Line("bigtimebramble", 999));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, scroll.Offset.Y);
        window.Close();
    });
}
