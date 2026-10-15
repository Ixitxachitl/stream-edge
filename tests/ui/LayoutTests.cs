// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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

    private static UltrawideLayout.Input Layout(double share = 0.5, bool showPlayer = true) =>
        new(16.0 / 9.0, 180, share, showPlayer, Grip);

    [Theory]
    // Common bar displays: the player takes height × 16/9.
    [InlineData(1920, 480, 853)]
    [InlineData(1920, 440, 782)]
    [InlineData(2560, 720, 1280)]
    [InlineData(1280, 400, 711)]
    public void PlayerFillsHeightAt16By9(double w, double h, double player)
    {
        var s = UltrawideLayout.Split(new Size(w, h), Layout());
        Assert.Equal(player, s.Player);
        Assert.Equal(w, s.Chat + s.Player + s.List + 2 * Grip);
        Assert.True(s.Chat >= 180 && s.List >= 180);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    public void TheSplitNeverChangesThePlayerSize(double share)
    {
        var s = UltrawideLayout.Split(new Size(1920, 480), Layout(share));
        Assert.Equal(853, s.Player);
        Assert.Equal(1920 - 853 - 2 * Grip, s.Sides);
    }

    [Fact]
    public void SidesKeepTheirMinimum()
    {
        var s = UltrawideLayout.Split(new Size(1920, 480), Layout(share: 0.01));
        Assert.Equal(180, s.Chat);
        Assert.Equal(853, s.Player);
    }

    [Fact]
    public void TheSplitIsProportionalWhenTheWindowResizes()
    {
        var narrow = UltrawideLayout.Split(new Size(1920, 480), Layout(share: 0.3));
        var wide = UltrawideLayout.Split(new Size(2560, 480), Layout(share: 0.3));
        Assert.Equal(0.3, narrow.Chat / narrow.Sides, 2);
        Assert.Equal(0.3, wide.Chat / wide.Sides, 2);
    }

    [Fact]
    public void TooNarrowShrinksPlayerNotSides()
    {
        var s = UltrawideLayout.Split(new Size(1000, 720), Layout());
        Assert.Equal(180, s.Chat);
        Assert.Equal(180, s.List);
        Assert.Equal(1000 - 360 - 2 * Grip, s.Player);
    }

    [Fact]
    public void HiddenPlayerGivesSidesEverything()
    {
        var s = UltrawideLayout.Split(new Size(1920, 480), Layout(share: 0.25, showPlayer: false));
        Assert.Equal(0, s.Player);
        Assert.Equal(1910, s.Sides);
    }

    [Theory]
    [InlineData(853, 480, 0, 0, 853, 480)]      // exact fit
    [InlineData(600, 480, 0, 71, 600, 338)]     // narrower pane: letterbox
    [InlineData(1000, 480, 73, 0, 853, 480)]    // wider pane: pillarbox
    public void AspectBoxFitsVideoAt16By9(double w, double h, double x, double y, double fw, double fh) =>
        Assert.Equal(new Rect(x, y, fw, fh), AspectRatioBox.Fit(new Size(w, h), 16.0 / 9.0));

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

        Assert.Equal(853, player.Bounds.Width);
        Assert.Equal(xBefore + 150, player.Bounds.X);
        Assert.Equal(listBefore + 150, streams.Bounds.Width);
        Assert.Equal(chatBefore - 150, chat.Bounds.Width);
        Assert.Equal(chat.Bounds.Width / (chat.Bounds.Width + streams.Bounds.Width), settings.ChatShare, 3);

        // Dragging far past the end stops at the other side's minimum.
        layout.Drag(5000);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(180, chat.Bounds.Width);
        Assert.Equal(853, player.Bounds.Width);

        layout.ResetSplit();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.5, settings.ChatShare);
        Assert.Equal(xBefore, player.Bounds.X);
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
        Assert.Equal(853, player.Bounds.Width);
        // Plus the two 5 px grips.
        Assert.Equal(1920, chat.Bounds.Width + player.Bounds.Width + streams.Bounds.Width + 10, 0);
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

    [Fact]
    public void ListIsOnTheLeftByDefaultAndSwapsWithTheSetting() => Ui(() =>
    {
        var window = Open(new AppSettings(), out var vm);
        var chat = window.FindControl<Control>("ChatPane")!;
        var streams = window.FindControl<Control>("StreamsPane")!;
        Assert.Equal(0, streams.Bounds.X);
        Assert.True(chat.Bounds.X > 1000);

        vm.ChatOnLeft = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, chat.Bounds.X);
        Assert.True(streams.Bounds.X > 1000);
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
