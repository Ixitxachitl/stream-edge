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
        vm.OpenChannel("vinesauce", OpenMode.Background);
        vm.OpenChannel("jerma985", OpenMode.Background);
        return vm;
    }

    private static List<string> Logins(ChatColumnViewModel column) => [.. column.Tabs.Select(t => t.Login)];

    private static ChannelTabViewModel Tab(MainViewModel vm, string login) => vm.Tabs.Single(t => t.Login == login);

    [Fact]
    public void TabsReorderWithinAStrip() => Ui(() =>
    {
        var vm = Demo(out var settings);
        var column = Assert.Single(vm.Columns);

        vm.MoveTab(Tab(vm, "jerma985"), column, 0);
        Assert.Equal(["jerma985", "nintendope503", "vinesauce"], Logins(column));
        // Indexes count the strip before the move: "after vinesauce" is 3.
        vm.MoveTab(Tab(vm, "jerma985"), column, 3);
        Assert.Equal(["nintendope503", "vinesauce", "jerma985"], Logins(column));
        Assert.Equal(["nintendope503", "vinesauce", "jerma985"], settings.OpenTabs);
    });

    [Fact]
    public void SplittingMakesANewColumnWithoutChangingTheVideo() => Ui(() =>
    {
        var vm = Demo(out var settings);
        var front = vm.FrontTab!;
        var vine = Tab(vm, "vinesauce");

        vm.MoveTabToNewColumn(vine, 1);

        Assert.Equal(2, vm.Columns.Count);
        Assert.Equal(["nintendope503", "jerma985"], Logins(vm.Columns[0]));
        Assert.Equal(["vinesauce"], Logins(vm.Columns[1]));
        Assert.Same(vine, vm.Columns[1].Selected);
        Assert.Same(front, vm.FrontTab); // still on video
        Assert.True(vm.Columns[0].HasFront);
        Assert.False(vm.Columns[1].HasFront);
        // The PIP switch stays in the rightmost column, wherever the video is.
        Assert.False(vm.Columns[0].IsLast);
        Assert.True(vm.Columns[1].IsLast);
        // The new column is the active one: the chat box acts on it.
        Assert.Same(vine.Chat, vm.Chat);
        Assert.Equal(2, settings.ChatColumns.Count);
        Assert.Equal("vinesauce", settings.ChatColumns[1].Selected);
    });

    [Fact]
    public void TypingInAColumnDoesNotMoveTheVideoButClickingATabDoes() => Ui(() =>
    {
        var vm = Demo(out _);
        var front = vm.FrontTab!;
        var vine = Tab(vm, "vinesauce");
        vm.MoveTabToNewColumn(vine, 1);

        vm.Activate(vm.Columns[0]);
        Assert.Same(front.Chat, vm.Chat);
        vm.Activate(vm.Columns[1]);
        Assert.Same(vine.Chat, vm.Chat);
        Assert.Same(front, vm.FrontTab);

        vine.SelectCommand.Execute(null);
        Assert.Same(vine, vm.FrontTab);
        Assert.True(vm.Columns[1].HasFront);
        Assert.False(vm.Columns[0].HasFront);
    });

    [Fact]
    public void AColumnEmptiedByDraggingOrClosingGoesAway() => Ui(() =>
    {
        var vm = Demo(out _);
        var vine = Tab(vm, "vinesauce");
        vm.MoveTabToNewColumn(vine, 0);
        Assert.Equal(["vinesauce"], Logins(vm.Columns[0]));

        // Dragged back into the other column.
        vm.MoveTab(vine, vm.Columns[1], 1);
        var column = Assert.Single(vm.Columns);
        Assert.Equal(["nintendope503", "vinesauce", "jerma985"], Logins(column));

        // Closed: the column goes, and the active one moves to its neighbour.
        vm.MoveTabToNewColumn(vine, 1);
        vine.CloseCommand.Execute(null);
        Assert.Single(vm.Columns);
        Assert.Same(vm.Columns[0], vm.ActiveColumn);
    });

    [Fact]
    public void ALoneTabDraggedToAnEdgeMovesItsColumn() => Ui(() =>
    {
        var vm = Demo(out _);
        vm.MoveTabToNewColumn(Tab(vm, "vinesauce"), 1);
        vm.MoveTabToNewColumn(Tab(vm, "jerma985"), 2);
        Assert.Equal(["vinesauce"], Logins(vm.Columns[1]));

        Assert.False(vm.CanSplit(Tab(vm, "jerma985"), 2)); // beside itself
        vm.MoveTabToNewColumn(Tab(vm, "jerma985"), 0);
        Assert.Equal(3, vm.Columns.Count);
        Assert.Equal(["jerma985"], Logins(vm.Columns[0]));
        Assert.Equal(["nintendope503"], Logins(vm.Columns[1]));
    });

    [Fact]
    public void ClosingTheTabOnVideoHandsVideoToWhatItsColumnNowShows() => Ui(() =>
    {
        var vm = Demo(out _);
        var vine = Tab(vm, "vinesauce");
        vm.MoveTabToNewColumn(vine, 1);
        vm.MoveTabToNewColumn(Tab(vm, "jerma985"), 2);
        vine.SelectCommand.Execute(null);

        vine.CloseCommand.Execute(null);

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
        var vine = Tab(vm, "vinesauce");

        // Right edge, over the chat: a new column to the right.
        var split = window.DropTargetAt(vine, new Point(w - 10, h / 2))!;
        Assert.True(split.NewColumn);
        Assert.Equal(1, split.Index);
        // Left edge: a new column on the left.
        Assert.Equal(0, window.DropTargetAt(vine, new Point(10, h / 2))!.Index);
        // The middle of its own column: nothing to do.
        Assert.Null(window.DropTargetAt(vine, new Point(w / 2, h / 2)));
        // Left end of the tab strip: first place.
        var first = window.DropTargetAt(vine, new Point(3, 12))!;
        Assert.False(first.NewColumn);
        Assert.Equal(0, first.Index);

        vm.MoveTabToNewColumn(vine, split.Index);
        Dispatcher.UIThread.RunJobs();
        var views = window.GetVisualDescendants().OfType<ChatColumnView>().ToList();
        Assert.Equal(2, views.Count);
        Assert.Equal(w / 2, views[0].Bounds.Width, 0);

        // Into the other column's middle: appended there.
        var into = window.DropTargetAt(Tab(vm, "jerma985"), new Point(w * 0.75, h / 2))!;
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
        var jerma = TabCenter("jerma985");
        window.MouseDown(jerma, MouseButton.Left);
        window.MouseUp(jerma, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("jerma985", vm.FrontTab?.Login);
        Assert.Single(vm.Columns);

        // Press on vinesauce, drag to the chat's right edge, let go.
        var vine = TabCenter("vinesauce");
        window.MouseDown(vine, MouseButton.Left);
        window.MouseMove(vine + new Point(20, 0));
        window.MouseMove(paneRight);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.FindControl<Border>("DropIndicator")!.IsVisible);
        window.MouseUp(paneRight, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.Columns.Count);
        Assert.Equal("vinesauce", Assert.Single(vm.Columns[1].Tabs).Login);
        Assert.Equal("jerma985", vm.FrontTab?.Login); // dragging doesn't change the video
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
        var vine = Tab(vm, "vinesauce");
        vine.Chat.InsertHistory([.. Enumerable.Range(0, 120).Select(i => Line("vinesauce", i))], "test");
        vine.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height * 2);
        Assert.True(AtBottom());

        // History arriving for the tab on screen keeps the end in view.
        var jerma = Tab(vm, "jerma985");
        jerma.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        jerma.Chat.InsertHistory([.. Enumerable.Range(0, 120).Select(i => Line("jerma985", i))], "test");
        Dispatcher.UIThread.RunJobs();
        Assert.True(AtBottom());

        // Scrolled up to read: new lines don't pull the view down.
        scroll.Offset = scroll.Offset.WithY(0);
        Dispatcher.UIThread.RunJobs();
        jerma.Chat.Add(Line("jerma985", 999));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, scroll.Offset.Y);
        window.Close();
    });
}
