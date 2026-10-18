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

/// <summary>Docking a pane by dragging its header onto a side of the video.</summary>
public class PaneDragTests(HeadlessAvalonia ui) : UiTest(ui)
{
    private static MainWindow Open(AppSettings settings, out MainViewModel vm)
    {
        vm = new MainViewModel(settings, new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        return window;
    }

    /// <summary>A spot on the list's header that isn't one of its buttons: its title.</summary>
    private static Point ListHeader(Window window)
    {
        var header = window.FindControl<StreamListView>("StreamsPane")!.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("paneHeader"));
        var title = header.GetVisualDescendants().OfType<TextBlock>().Last();
        return title.TranslatePoint(new Point(10, title.Bounds.Height / 2), window)!.Value;
    }

    /// <summary>Where <paramref name="pane"/> is in the overlay the preview is drawn on.</summary>
    private static Rect InOverlay(Window window, Control pane) =>
        new(pane.TranslatePoint(default, window.FindControl<Canvas>("DockOverlay")!)!.Value, pane.Bounds.Size);

    private static Point InPlayer(Window window, double fx, double fy)
    {
        var player = window.FindControl<Control>("PlayerPane")!;
        return player.TranslatePoint(new Point(player.Bounds.Width * fx, player.Bounds.Height * fy), window)!.Value;
    }

    [Fact]
    public void DraggingTheListsHeaderOntoTheRightDocksItThere() => Ui(() =>
    {
        var settings = new AppSettings();
        var window = Open(settings, out var vm);
        var targets = window.FindControl<Control>("DockTargets")!;
        var videos = window.FindControl<VideoStage>("Videos")!;
        Assert.Equal(DockSide.Left, vm.ListDock);

        var from = ListHeader(window);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(20, 0));
        Dispatcher.UIThread.RunJobs();
        // Dragging: the targets show, the videos step aside.
        Assert.True(targets.IsVisible);
        Assert.True(videos.Suspended);

        var right = InPlayer(window, 0.9, 0.5);
        window.MouseMove(right);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("active", window.FindControl<Border>("DockRight")!.Classes);
        window.MouseUp(right, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DockSide.Right, vm.ListDock);
        Assert.Equal(DockSide.Right, settings.ListDock);
        Assert.False(targets.IsVisible);
        Assert.False(videos.Suspended);
        var streams = window.FindControl<Control>("StreamsPane")!;
        Assert.True(streams.Bounds.X > window.FindControl<Control>("PlayerPane")!.Bounds.X);
        window.Close();
    });

    [Fact]
    public void AShortPressIsAClickAndEscapeCancelsADrag() => Ui(() =>
    {
        var window = Open(new AppSettings(), out var vm);
        var from = ListHeader(window);

        // Barely moving: no drag.
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(2, 1));
        window.MouseUp(from + new Point(2, 1), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.FindControl<Control>("DockTargets")!.IsVisible);
        Assert.Equal(DockSide.Left, vm.ListDock);

        // A real drag, then Esc: nothing moves.
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(InPlayer(window, 0.5, 0.1));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.FindControl<Control>("DockTargets")!.IsVisible);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(InPlayer(window, 0.5, 0.1), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.FindControl<Control>("DockTargets")!.IsVisible);
        Assert.Equal(DockSide.Left, vm.ListDock);
        Assert.False(window.FindControl<VideoStage>("Videos")!.Suspended);
        window.Close();
    });


    [Fact]
    public void DroppingOnTheLowerHalfOfAPaneStacksUnderItWithAPreviewFirst() => Ui(() =>
    {
        var settings = new AppSettings();
        var window = Open(settings, out var vm);
        var chat = window.FindControl<Control>("ChatPane")!;
        var preview = window.FindControl<Border>("DockPreview")!;

        var from = ListHeader(window);
        var target = chat.TranslatePoint(new Point(chat.Bounds.Width / 2, chat.Bounds.Height * 0.75), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(20, 0));
        window.MouseMove(target);
        Dispatcher.UIThread.RunJobs();

        // The preview shows where it would go, on the layout as it is now:
        // chat's bottom half.
        Assert.True(preview.IsVisible);
        // The preview is drawn over the whole window; the panes sit inside
        // the layout, a gap in from its edge.
        var before = InOverlay(window, chat);
        var previewRect = new Rect(Canvas.GetLeft(preview), Canvas.GetTop(preview), preview.Width, preview.Height);
        Assert.Equal(new Rect(before.X, before.Y + before.Height / 2, before.Width, before.Height / 2), previewRect);

        window.MouseUp(target, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.False(preview.IsVisible);
        Assert.Equal(new PanePlace(DockSide.Right, 1, Below: true), vm.ListPlace);
        Assert.Equal(new PanePlace(DockSide.Right, 1, Below: true), settings.ListPlace);

        // Stacked: same column, chat on top, the list under it.
        var streams = window.FindControl<Control>("StreamsPane")!;
        Assert.Equal(chat.Bounds.X, streams.Bounds.X);
        Assert.Equal(chat.Bounds.Width, streams.Bounds.Width);
        Assert.Equal(chat.Bounds.Bottom + 5, streams.Bounds.Y);   // the gap between them resizes them
        window.Close();
    });

    [Fact]
    public void DroppingOnAPanesEdgePutsItBeside() => Ui(() =>
    {
        var window = Open(new AppSettings(), out var vm);
        var chat = window.FindControl<Control>("ChatPane")!;
        var from = ListHeader(window);
        var edge = chat.TranslatePoint(new Point(chat.Bounds.Width * 0.9, chat.Bounds.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(20, 0));
        window.MouseMove(edge);
        window.MouseUp(edge, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Right of chat: chat's column, then the list's.
        Assert.Equal(DockSide.Right, vm.ListDock);
        Assert.False(vm.ListPlace.Below);
        var streams = window.FindControl<Control>("StreamsPane")!;
        Assert.Equal(chat.Bounds.Right + 5, streams.Bounds.X);
        Assert.Equal(480 - 10, streams.Bounds.Height);   // the full height, inside the gap all round
        window.Close();
    });

    [Fact]
    public void OverTheVideoThePreviewIsThatSideOfTheVideoAsItIsNow() => Ui(() =>
    {
        var window = Open(new AppSettings(), out _);
        var player = window.FindControl<Control>("PlayerPane")!;
        var preview = window.FindControl<Border>("DockPreview")!;
        var video = InOverlay(window, player);
        var from = ListHeader(window);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(20, 0));
        Rect PreviewAt(double fx, double fy)
        {
            window.MouseMove(InPlayer(window, fx, fy));
            Dispatcher.UIThread.RunJobs();
            Assert.True(preview.IsVisible);
            return new Rect(Canvas.GetLeft(preview), Canvas.GetTop(preview), preview.Width, preview.Height);
        }

        // The bottom of the video: a strip along the bottom of the video.
        var below = PreviewAt(0.5, 0.85);
        Assert.Equal(video.X, below.X, 1.0);
        Assert.Equal(video.Width, below.Width, 1.0);
        Assert.Equal(video.Bottom, below.Bottom, 1.0);
        Assert.True(below.Height < video.Height / 2);

        var right = PreviewAt(0.9, 0.5);
        Assert.Equal(video.Right, right.Right, 1.0);
        Assert.Equal(video.Height, right.Height, 1.0);
        Assert.True(right.X > video.X);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(from, MouseButton.Left);
        window.Close();
    });

    [Fact]
    public void UnplacedVideosAreHiddenNotJustShrunkAndADragHidesThemAll() => Ui(() =>
    {
        // Avalonia leaves a native window that's given no size where it was,
        // still drawing; only hiding it takes it off the screen.
        var vm = new MainViewModel(new AppSettings(), new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        vm.OpenChannel("velvetcartridge", OpenMode.Background);
        var front = vm.FrontTab!;
        var back = vm.Tabs.Single(t => t != front);
        front.Player.IsVideoVisible = true;
        back.Player.IsVideoVisible = true;
        var stage = new VideoStage { ShowPip = false };
        var (frontSurface, backSurface) = (new PlayerSurface { DataContext = front }, new PlayerSurface { DataContext = back });
        stage.Children.Add(frontSurface);
        stage.Children.Add(backSurface);
        var window = new Window { Content = stage, Width = 853, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // PIP off: the background video has no place, so it's hidden.
        Assert.True(frontSurface.IsVisible);
        Assert.False(backSurface.IsVisible);

        stage.ShowPip = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(backSurface.IsVisible);

        // Dragging a pane: every video out of sight, back afterwards.
        stage.Suspended = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(frontSurface.IsVisible);
        Assert.False(backSurface.IsVisible);
        stage.Suspended = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(frontSurface.IsVisible);
        Assert.True(backSurface.IsVisible);
        window.Close();
    });

    /// <summary>Drags the gap of <paramref name="kind"/> by (dx, dy) with the mouse.</summary>
    private static void DragGap(Window window, UltrawideLayout.SplitKind kind, double dx, double dy)
    {
        var layout = window.FindControl<UltrawideLayout>("Layout")!;
        var gap = layout.CurrentPlan.Splitters.First(s => s.Kind == kind).Rect;
        var at = layout.TranslatePoint(gap.Center, window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at + new Point(dx, dy));
        window.MouseUp(at + new Point(dx, dy), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void DraggingTheGapsResizesColumnsStacksAndRowsAndRemembersThem() => Ui(() =>
    {
        var settings = new AppSettings();
        var window = Open(settings, out var vm);
        var chat = window.FindControl<Control>("ChatPane")!;
        var streams = window.FindControl<Control>("StreamsPane")!;

        // Two columns on the right: chat, then the list. Widen chat by 100.
        vm.SetPanePlaces(PaneArrangement.MoveBeside(vm.PanePlaces, PaneId.List, PaneId.Chat, after: true));
        Dispatcher.UIThread.RunJobs();
        var (chatW, listW) = (chat.Bounds.Width, streams.Bounds.Width);
        DragGap(window, UltrawideLayout.SplitKind.Columns, 100, 0);
        Assert.Equal(chatW + 100, chat.Bounds.Width, 1.0);
        Assert.Equal(listW - 100, streams.Bounds.Width, 1.0);
        // Kept: a new start lays them out the same.
        Assert.Equal(chat.Bounds.Width / streams.Bounds.Width,
            settings.ChatPlace!.ColumnWeight / settings.ListPlace!.ColumnWeight, 2);

        // Stacked instead: give the list, underneath, 60 px more.
        vm.SetPanePlaces(PaneArrangement.MoveStacked(vm.PanePlaces, PaneId.List, PaneId.Chat, below: true));
        Dispatcher.UIThread.RunJobs();
        var listH = streams.Bounds.Height;
        DragGap(window, UltrawideLayout.SplitKind.Rows, 0, -60);
        Assert.Equal(listH + 60, streams.Bounds.Height, 1.0);
        Assert.True(settings.ListPlace!.RowWeight > settings.ChatPlace!.RowWeight);

        // A row below the video: drag the line above it up 40 px.
        vm.DockChannelPanelCommand.Execute(DockSide.Bottom);
        Dispatcher.UIThread.RunJobs();
        var panel = window.FindControl<Control>("ChannelPanelPane")!;
        var panelH = panel.Bounds.Height;
        DragGap(window, UltrawideLayout.SplitKind.BottomRow, 0, -40);
        Assert.Equal(panelH + 40, panel.Bounds.Height, 1.0);
        Assert.Equal(panel.Bounds.Height / window.FindControl<UltrawideLayout>("Layout")!.Bounds.Height, settings.BottomRowShare, 2);
        window.Close();
    });
    [Theory]
    [InlineData(0.1, 0.5, DockSide.Left)]
    [InlineData(0.95, 0.9, DockSide.Right)]
    [InlineData(0.5, 0.2, DockSide.Top)]
    [InlineData(0.5, 0.8, DockSide.Bottom)]
    [InlineData(-3, 0.5, DockSide.Left)]       // past the player, over a side pane
    [InlineData(0.5, 2, DockSide.Bottom)]
    public void TheTargetIsTheSideOfTheVideoUnderTheMouse(double fx, double fy, DockSide side) =>
        Assert.Equal(side, MainWindow.DockSideAt(new Point(800 * fx, 450 * fy), new Size(800, 450)));
}
