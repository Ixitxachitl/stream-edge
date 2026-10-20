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

public class LayoutTests(HeadlessAvalonia ui) : UiTest(ui)
{
    private const double Grip = 5;

    private static UltrawideLayout.Plan Plan(double w, double h, double share = 0.5, bool showPlayer = true,
        bool showList = true, bool showPanel = false, DockSide chat = DockSide.Right, DockSide list = DockSide.Left,
        DockSide panel = DockSide.Bottom) =>
        UltrawideLayout.Arrange(new Size(w, h),
            new UltrawideLayout.Input(16.0 / 9.0, 180, share, showPlayer, Grip, showList, showPanel, chat, list, panel, 0.35));

    [Theory]
    // Common bar displays: the player takes height × 16/9.
    [InlineData(1920, 480, 853)]
    [InlineData(1920, 440, 782)]
    [InlineData(2560, 720, 1280)]
    [InlineData(1280, 400, 711)]
    public void PlayerFillsHeightAt16By9(double w, double h, double player)
    {
        var p = Plan(w, h);
        Assert.Equal(player, p.Player.Width);
        Assert.Equal(h, p.Player.Height);
        Assert.Equal(w, p.Chat.Width + p.Player.Width + p.List.Width + 2 * Grip);
        Assert.True(p.Chat.Width >= 180 && p.List.Width >= 180);
        // List | player | chat.
        Assert.True(p.List.X < p.Player.X && p.Player.X < p.Chat.X);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    public void TheSplitNeverChangesThePlayerSize(double share)
    {
        var p = Plan(1920, 480, share);
        Assert.Equal(853, p.Player.Width);
        Assert.Equal(1920 - 853 - 2 * Grip, p.Chat.Width + p.List.Width);
    }

    [Fact]
    public void SidesKeepTheirMinimum()
    {
        var p = Plan(1920, 480, share: 0.01);
        Assert.Equal(180, p.Chat.Width);
        Assert.Equal(853, p.Player.Width);
    }

    [Fact]
    public void TheSplitIsProportionalWhenTheWindowResizes()
    {
        var narrow = Plan(1920, 480, share: 0.3);
        var wide = Plan(2560, 480, share: 0.3);
        Assert.Equal(0.3, narrow.Chat.Width / (narrow.Chat.Width + narrow.List.Width), 2);
        Assert.Equal(0.3, wide.Chat.Width / (wide.Chat.Width + wide.List.Width), 2);
    }

    [Fact]
    public void TooNarrowShrinksPlayerNotSides()
    {
        var p = Plan(1000, 720);
        Assert.Equal(180, p.Chat.Width);
        Assert.Equal(180, p.List.Width);
        Assert.Equal(1000 - 360 - 2 * Grip, p.Player.Width);
    }

    [Fact]
    public void HiddenPlayerGivesSidesEverything()
    {
        var p = Plan(1920, 480, share: 0.25, showPlayer: false);
        Assert.Equal(0, p.Player.Width);
        Assert.Equal(1910, p.Chat.Width + p.List.Width);
    }

    [Fact]
    public void APaneBelowShortensTheVideoWhichNarrowsToStay16By9()
    {
        var p = Plan(1920, 480, showPanel: true);
        // A row is 35% of the height: 168, then a 5 px gap; the video gets 307.
        Assert.Equal(307, p.Player.Height);
        Assert.Equal(545, p.Player.Width);
        Assert.Equal(new Rect(p.Player.X, 312, 545, 168), p.Panel);
        // The sides get what the video gave up.
        Assert.Equal(1920 - 545 - 2 * Grip, p.Chat.Width + p.List.Width);
    }

    [Fact]
    public void WithNothingBesideTheVideoItsColumnIsTheWholeWidth()
    {
        // Chat above, list below: the rows span the window, a gap either side
        // of the video, which letterboxes between them.
        var p = Plan(1920, 480, chat: DockSide.Top, list: DockSide.Bottom);
        Assert.Equal(new Rect(0, 0, 1920, 168), p.Chat);
        Assert.Equal(new Rect(0, 173, 1920, 134), p.Player);
        Assert.Equal(new Rect(0, 312, 1920, 168), p.List);
        Assert.Equal(default, p.LeftGrip);
        Assert.Equal(default, p.RightGrip);
    }

    [Fact]
    public void PanesOnOneSideShareItChatNearestTheVideo()
    {
        var right = Plan(1920, 480, list: DockSide.Right);
        Assert.Equal(0, right.Player.X);
        Assert.Equal(853, right.Player.Width);
        Assert.True(right.Chat.X < right.List.X);
        Assert.Equal(right.Chat.Width, right.List.Width, 1.0);
        Assert.Equal(1920, right.List.Right);

        // On the left, nearest the video is the rightmost.
        var left = Plan(1920, 480, chat: DockSide.Left, list: DockSide.Left, showPanel: true, panel: DockSide.Left);
        Assert.Equal(0, left.List.X);
        Assert.True(left.List.X < left.Panel.X && left.Panel.X < left.Chat.X);
        Assert.Equal(1920 - 853, left.Player.X);
    }


    [Fact]
    public void TwoPanesOnASideEachGetTheirMinimum()
    {
        // A tall window: the video would take nearly all the width, but chat
        // and the About panel sharing the right still get 180 px each.
        var p = Plan(1920, 1080, showPanel: true, panel: DockSide.Right);
        Assert.True(p.Chat.Width >= 180);
        Assert.True(p.Panel.Width >= 180);
        Assert.True(p.List.Width >= 180);
        // Two grips beside the video and a gap between the two columns.
        Assert.Equal(1920, p.List.Width + p.Player.Width + p.Chat.Width + p.Panel.Width + 3 * Grip);
    }

    [Fact]
    public void ColumnAndStackedPaneSizesFollowTheirWeights()
    {
        var places = new Dictionary<PaneId, PanePlace>
        {
            [PaneId.Chat] = new(DockSide.Right, 0, ColumnWeight: 3),
            [PaneId.List] = new(DockSide.Right, 1, ColumnWeight: 1, RowWeight: 1),
            [PaneId.Panel] = new(DockSide.Right, 2, Below: true, RowWeight: 3),
        };
        var p = UltrawideLayout.Arrange(new Size(1920, 480),
            new UltrawideLayout.Input(16.0 / 9.0, 180, 0.5, true, Grip, ShowPanel: true, Places: places));
        // Chat's column is three times the list's (a gap between them).
        Assert.Equal(3, p.Chat.Width / p.List.Width, 1);
        Assert.Equal(p.Chat.Right + Grip, p.List.X);
        // The panel, stacked under the list, three times its height.
        Assert.Equal(p.List.X, p.Panel.X);
        Assert.Equal(3, p.Panel.Height / p.List.Height, 1);
        Assert.Equal(480, p.List.Height + Grip + p.Panel.Height);
        // A gap to drag between each.
        Assert.Contains(p.Splitters, s => s.Kind == UltrawideLayout.SplitKind.Columns && s.Before == PaneId.Chat && s.After == PaneId.List);
        Assert.Contains(p.Splitters, s => s.Kind == UltrawideLayout.SplitKind.Rows && s.Before == PaneId.List && s.After == PaneId.Panel);
    }
    [Fact]
    public void WithoutTheListChatTakesTheRoomBesideTheVideo()
    {
        var p = Plan(1920, 480, showList: false);
        Assert.Equal(new Rect(0, 0, 853, 480), p.Player);
        Assert.Equal(1920 - 853 - Grip, p.Chat.Width);
        Assert.Equal(default, p.List);
    }

    [Fact]
    public void RowsGiveWayToKeepTheVideoVisible()
    {
        var p = Plan(1280, 240, chat: DockSide.Top, list: DockSide.Bottom, showPanel: true, panel: DockSide.Bottom);
        Assert.True(p.Player.Height >= UltrawideLayout.MinVideoHeight);
        // Rows, video and the two gaps fill the height.
        Assert.Equal(240, p.Chat.Height + p.Player.Height + p.List.Height + 2 * Grip, 1.0);
        Assert.Equal(p.List.Y, p.Panel.Y);
    }

    [Theory]
    [InlineData(853, 480, 0, 0, 853, 480)]      // exact fit
    [InlineData(600, 480, 0, 71, 600, 338)]     // narrower pane: letterbox
    [InlineData(1000, 480, 73, 0, 853, 480)]    // wider pane: pillarbox
    public void AspectBoxFitsVideoAt16By9(double w, double h, double x, double y, double fw, double fh) =>
        Assert.Equal(new Rect(x, y, fw, fh), AspectRatioBox.Fit(new Size(w, h), 16.0 / 9.0));

    [Fact]
    public void PanelsFillTheShortestColumnLikeTwitch()
    {
        // 700 px: two 320 px columns (and a 16 px gap); each panel goes
        // under whichever column is shorter so far.
        var rects = PanelColumns.Place([100, 300, 50, 80], 700, 320, 16);
        Assert.Equal(new Rect(0, 0, 320, 100), rects[0]);
        Assert.Equal(new Rect(336, 0, 320, 300), rects[1]);
        Assert.Equal(new Rect(0, 116, 320, 50), rects[2]);
        Assert.Equal(new Rect(0, 182, 320, 80), rects[3]);

        // Docked beside the video, narrower than a column: one column, its width.
        var narrow = PanelColumns.Place([100, 50], 250, 320, 16);
        Assert.Equal(new Rect(0, 0, 250, 100), narrow[0]);
        Assert.Equal(new Rect(0, 116, 250, 50), narrow[1]);
    }

    [Fact]
    public void DraggingSlidesThePlayerAndTheOtherSideGivesWay() => Ui(() =>
    {
        var settings = new AppSettings();
        var vm = new MainViewModel(settings, new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var layout = window.FindControl<UltrawideLayout>("Layout")!;
        var player = window.FindControl<Control>("PlayerPane")!;
        var streams = window.FindControl<Control>("StreamsPane")!;
        var chat = window.FindControl<Control>("ChatPane")!;
        var (listBefore, chatBefore, xBefore) = (streams.Bounds.Width, chat.Bounds.Width, player.Bounds.X);

        // Default layout: list | player | chat. Slide the player 150 px right.
        layout.Drag(150);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(835, player.Bounds.Width);
        Assert.Equal(xBefore + 150, player.Bounds.X);
        Assert.Equal(listBefore + 150, streams.Bounds.Width);
        Assert.Equal(chatBefore - 150, chat.Bounds.Width);
        Assert.Equal(chat.Bounds.Width / (chat.Bounds.Width + streams.Bounds.Width), settings.ChatShare, 3);

        // Dragging far past the end stops at the other side's minimum.
        layout.Drag(5000);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(180, chat.Bounds.Width);
        Assert.Equal(835, player.Bounds.Width);

        layout.ResetSplit();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.5, settings.ChatShare);
        Assert.Equal(xBefore, player.Bounds.X);
        window.Close();
    });

    [Fact]
    public void VideoOnlyGivesThePlayerTheWholeWindowAndEscapeBringsThePanesBack() => Ui(() =>
    {
        var settings = new AppSettings();
        var vm = new MainViewModel(settings, new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var player = window.FindControl<Control>("PlayerPane")!;
        var chat = window.FindControl<Control>("ChatPane")!;
        var playerBefore = player.Bounds;

        vm.VideoOnly = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(WindowState.FullScreen, window.WindowState);
        Assert.Equal(0, player.Bounds.X);
        Assert.Equal(window.ClientSize.Width, player.Bounds.Width);
        Assert.Equal(0, chat.Bounds.Width);

        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.VideoOnly);
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(playerBefore, player.Bounds);
        window.Close();
    });

    [Fact]
    public void PointsMenuShowsTheChannelsRewards() => Ui(() =>
    {
        var vm = new MainViewModel(new AppSettings(), new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PointsButton");
        Assert.True(button.IsVisible);

        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT") is { Length: > 0 } path)
#pragma warning disable CS0618
            frame!.Save(path.Replace(".png", "-points.png"));
#pragma warning restore CS0618
        Assert.Equal(3, vm.Chat.Rewards.Count);
        button.Flyout.Hide();
        window.Close();
    });

    [Fact]
    public void DemoWindowRendersAllThreePanes() => Ui(() =>
    {
        var vm = new MainViewModel(new AppSettings(), new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var chat = window.FindControl<Control>("ChatPane")!;
        var player = window.FindControl<Control>("PlayerPane")!;
        var streams = window.FindControl<Control>("StreamsPane")!;
        Assert.Equal(835, player.Bounds.Width);
        // Plus the two 5 px grips and the 5 px gap all round.
        Assert.Equal(1920, chat.Bounds.Width + player.Bounds.Width + streams.Bounds.Width + 10 + 10, 0);
        Assert.Equal(12, vm.Streams.Items.Count);
        Assert.True(vm.Chat.Lines.Count > 5);

        // Set STREAMEDGE_SNAPSHOT=<path.png> to save the render for eyeballing.
        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT") is { Length: > 0 } path)
#pragma warning disable CS0618 // PNG by extension is all this needs.
            frame!.Save(path);
#pragma warning restore CS0618
        window.Close();
    });

    [Fact]
    public void SignedOutListShowsLogInButton() => Ui(() =>
    {
        var vm = new MainViewModel(new AppSettings(), new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        vm.Streams.Set([], DateTimeOffset.Now);
        vm.Streams.EmptyText = "Log in to see which channels you follow are live.";
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();

        var streams = window.FindControl<Control>("StreamsPane")!;
        var login = streams.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Content as string == "Log in with Twitch");
        Assert.True(login.IsEffectivelyVisible);
        Assert.True(login.Bounds.Width > 0);

        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT_LOGIN") is { Length: > 0 } path)
        {
#pragma warning disable CS0618 // PNG by extension is all this needs.
            frame!.Save(path);
#pragma warning restore CS0618
        }
        window.Close();
    });
}

public class SidesAndFavoritesTests(HeadlessAvalonia ui) : UiTest(ui)
{
    private static MainWindow Open(AppSettings settings, out MainViewModel vm)
    {
        vm = new MainViewModel(settings, new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>Whether a click at the middle of <paramref name="target"/> reaches it.</summary>
    private static bool TakesClicks(Window window, Control target)
    {
        // Hit testing works on the rendered scene, so render one first.
        window.CaptureRenderedFrame();
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        return window.GetInputElementsAt(center).FirstOrDefault() is Visual hit
            && hit.GetSelfAndVisualAncestors().Contains(target);
    }

    [Fact]
    public void SignedOutTheLoginButtonsInTheListAndChatTakeClicks() => Ui(() =>
    {
        var window = Open(new AppSettings(), out var vm);
        vm.Streams.Set([], DateTimeOffset.Now);
        Dispatcher.UIThread.RunJobs();

        // The empty list used to sit over the login box and take its clicks.
        var streams = window.FindControl<Control>("StreamsPane")!;
        var listLogin = streams.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Log in with Twitch");
        Assert.True(TakesClicks(window, listLogin));

        var chatLogin = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ChatLogin");
        Assert.True(chatLogin.IsEffectivelyVisible);
        Assert.True(TakesClicks(window, chatLogin));
        window.Close();
    });

    [Fact]
    public void SettingsLoginButtonIsFullSizeNotAHeaderButton() => Ui(() =>
    {
        var window = Open(new AppSettings(), out _);
        var gear = window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Settings");
        gear.Flyout!.ShowAt(gear);
        Dispatcher.UIThread.RunJobs();

        var content = (Control)((Flyout)gear.Flyout).Content!;
        var login = content.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Log in with Twitch");
        // The header's compact-button style (20 px, transparent) once leaked into its flyouts.
        Assert.True(login.Bounds.Height > 20);
        Assert.NotEqual(Avalonia.Media.Colors.Transparent, (login.Background as Avalonia.Media.ISolidColorBrush)?.Color);
        // And ⚙ sits in the tab strip, the size of PIP beside it.
        var pip = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => b.Content as string == "⧉");
        Assert.Equal(pip.Bounds.Height, gear.Bounds.Height);
        gear.Flyout.Hide();
        window.Close();
    });

    [Fact]
    public void UsersInChatOpensOverTheChatFiltersAndCloses() => Ui(() =>
    {
        var window = Open(new AppSettings(), out var vm);
        var column = vm.Columns[0];
        vm.ShowChatters(column);
        Dispatcher.UIThread.RunJobs();

        var list = column.Chatters!;
        var view = window.GetVisualDescendants().OfType<ChatterListView>().Single();
        Assert.True(view.IsEffectivelyVisible);
        // Demo chat: the broadcaster and moderators first, lurkers among the users.
        Assert.Equal("BROADCASTER", ((ChatterSection)list.Items[0]).Title);
        Assert.Contains(list.Items, i => i is StreamEdge.Chat.Chatter { Login: "quietkoala" });
        var frame = window.CaptureRenderedFrame();
        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT") is { Length: > 0 } path)
#pragma warning disable CS0618
            frame!.Save(path.Replace(".png", "-chatters.png"));
#pragma warning restore CS0618

        list.Search = "koala";
        Assert.Equal(["USERS", "quietkoala"],
            list.Items.Select(i => i is ChatterSection s ? s.Title : ((StreamEdge.Chat.Chatter)i).Login));

        list.CloseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(column.Chatters);
        Assert.False(view.IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public void StreamRowsShowNameGameAndCompactViewers() => Ui(() =>
    {
        var window = Open(new AppSettings(), out _);
        var list = window.FindControl<StreamListView>("StreamsPane")!.FindControl<ListBox>("StreamList")!;
        var bramble = list.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(i => i.DataContext is StreamItemViewModel { DisplayName: "BigTimeBramble" });
        var texts = bramble.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("BigTimeBramble", texts);
        Assert.Contains("Grand Theft Auto V", texts);
        Assert.Contains("11.4K", texts);
        Assert.Contains("B", texts); // avatar placeholder: demo data has no avatars
        window.Close();
    });

    [Fact]
    public void ListIsOnTheLeftByDefaultAndChatOnTheRight() => Ui(() =>
    {
        var window = Open(new AppSettings(), out _);
        var chat = window.FindControl<Control>("ChatPane")!;
        var streams = window.FindControl<Control>("StreamsPane")!;
        Assert.Equal(0, streams.Bounds.X);
        Assert.True(chat.Bounds.X > 1000);
        window.Close();
    });

    [Fact]
    public void OlderSettingsWithChatOnTheLeftKeepThatLayout() => Ui(() =>
    {
        var window = Open(new AppSettings { ChatOnLeft = true }, out var vm);
        Assert.Equal(DockSide.Left, vm.ChatDock);
        Assert.Equal(DockSide.Right, vm.ListDock);
        Assert.Equal(0, window.FindControl<Control>("ChatPane")!.Bounds.X);
        window.Close();
    });

    [Fact]
    public void PanesDockWhereTheyAreMovedAndStayThere() => Ui(() =>
    {
        var settings = new AppSettings();
        var window = Open(settings, out var vm);
        var player = window.FindControl<Control>("PlayerPane")!;
        var chat = window.FindControl<Control>("ChatPane")!;
        var streams = window.FindControl<Control>("StreamsPane")!;

        // Chat under the video: as wide as it, and the video shorter.
        vm.DockChatCommand.Execute(DockSide.Bottom);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(player.Bounds.X, chat.Bounds.X);
        Assert.Equal(player.Bounds.Width, chat.Bounds.Width);
        Assert.Equal(player.Bounds.Bottom + 5, chat.Bounds.Y);   // a 5 px gap to drag it by
        Assert.True(player.Bounds.Height < 480);
        Assert.Equal(DockSide.Bottom, settings.ChatDock);
        Assert.Equal(640, window.MinWidth);

        // The list to the right: the video moves left to make room.
        vm.DockListCommand.Execute(DockSide.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, player.Bounds.X);
        Assert.True(streams.Bounds.X > player.Bounds.Right);

        // Remembered: a new start puts them back there.
        var reopened = new MainViewModel(settings, new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        Assert.Equal(DockSide.Bottom, reopened.ChatDock);
        Assert.Equal(DockSide.Right, reopened.ListDock);
        window.Close();
    });

    [Fact]
    public void TheListAndTheChannelPanelToggleAndTheVideoMakesRoom() => Ui(() =>
    {
        var window = Open(new AppSettings(), out var vm);
        var player = window.FindControl<Control>("PlayerPane")!;
        var chat = window.FindControl<Control>("ChatPane")!;
        var streams = window.FindControl<Control>("StreamsPane")!;
        var panel = window.FindControl<Control>("ChannelPanelPane")!;
        Assert.False(panel.IsVisible);

        vm.ShowStreamList = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(streams.IsVisible);
        Assert.Equal(0, player.Bounds.X);
        Assert.Equal(1920 - 10 - 835 - 5, chat.Bounds.Width);   // the gap all round, the video, the grip

        vm.ShowChannelPanel = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.IsVisible);
        Assert.Equal(player.Bounds.Bottom + 5, panel.Bounds.Y);
        Assert.Equal(player.Bounds.Width, panel.Bounds.Width);
        Assert.Equal(480 - 10, player.Bounds.Height + 5 + panel.Bounds.Height);

        // It moves like the others.
        vm.DockChannelPanelCommand.Execute(DockSide.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, panel.Bounds.X);
        Assert.Equal(480 - 10, player.Bounds.Height);
        window.Close();
    });


    [Fact]
    public void ToolbarsAreOneHeightAndChatsSitsAboveItsTabs() => Ui(() =>
    {
        var window = Open(new AppSettings { ShowChannelPanel = true }, out _);
        var headers = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("paneHeader")).ToList();
        Assert.Equal(3, headers.Count);   // chat, the live list, the About panels
        Assert.All(headers, h => Assert.Equal(30, h.Bounds.Height));

        var column = window.GetVisualDescendants().OfType<ChatColumnView>().Single();
        var toolbar = window.GetVisualDescendants().OfType<ChatHeaderView>().Single()
            .GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("paneHeader"));
        var tabs = column.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("tabStrip"));
        Assert.True(toolbar.TranslatePoint(new Point(0, toolbar.Bounds.Height), window)!.Value.Y
            <= tabs.TranslatePoint(default, window)!.Value.Y);
        // The app's controls are on the toolbar now, not among the tabs.
        Assert.Contains(toolbar.GetVisualDescendants().OfType<Button>(), b => ToolTip.GetTip(b) as string == "Settings");
        Assert.DoesNotContain(tabs.GetVisualDescendants().OfType<Button>(), b => ToolTip.GetTip(b) as string == "Settings");
        window.Close();
    });
    [Fact]
    public void SplitChatsShareOneToolbar() => Ui(() =>
    {
        var window = Open(new AppSettings(), out var vm);
        vm.OpenChannel("velvetcartridge", OpenMode.Background);
        vm.MoveTabToNewColumn(vm.Tabs.Single(t => t.Login == "velvetcartridge"), 1);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, window.GetVisualDescendants().OfType<ChatColumnView>().Count());
        var chat = window.FindControl<Control>("ChatPane")!;
        Assert.Single(chat.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("paneHeader"));
        window.Close();
    });

    [Fact]
    public void StarringMovesAChannelToTheTopAndIsRemembered() => Ui(() =>
    {
        var settings = new AppSettings();
        var window = Open(settings, out var vm);
        var last = vm.Streams.Items[^1];
        Assert.False(last.IsFavorite);

        last.ToggleFavoriteCommand.Execute(null);

        Assert.True(settings.IsFavorite(last.Stream.Login));
        Assert.Contains(vm.Streams.Items.TakeWhile(i => i.IsFavorite), i => i.Stream.Login == last.Stream.Login);

        // And un-starring drops it back out of the starred block.
        vm.Streams.Items.First(i => i.Stream.Login == last.Stream.Login).ToggleFavoriteCommand.Execute(null);
        Assert.False(settings.IsFavorite(last.Stream.Login));
        Assert.DoesNotContain(vm.Streams.Items.TakeWhile(i => i.IsFavorite), i => i.Stream.Login == last.Stream.Login);
        window.Close();
    });

    [Fact]
    public void DemoNeverWritesSettings() => Ui(() =>
    {
        var settings = new AppSettings();
        var window = Open(settings, out _);
        Assert.True(settings.ReadOnly);
        settings.Save();
        Assert.False(File.Exists(AppData.PathFor(AppSettings.FileName)));
        window.Close();
    });
}
