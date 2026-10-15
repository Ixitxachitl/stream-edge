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
    [Theory]
    // Common bar displays: the player always takes height × 16/9.
    [InlineData(1920, 480, 853)]
    [InlineData(1920, 440, 782)]
    [InlineData(2560, 720, 1280)]
    [InlineData(1280, 400, 711)]
    public void PlayerFillsHeightAt16By9(double w, double h, double player)
    {
        var (chat, p, list) = UltrawideLayout.Split(new Size(w, h), 16.0 / 9.0, 240, 0.5, true);
        Assert.Equal(player, p, 0);
        Assert.Equal(w, chat + p + list, 3);
        Assert.True(chat >= 240 && list >= 240);
    }

    [Fact]
    public void TooNarrowShrinksPlayerNotSides()
    {
        var (chat, p, list) = UltrawideLayout.Split(new Size(1000, 720), 16.0 / 9.0, 240, 0.5, true);
        Assert.Equal(520, p, 3);
        Assert.Equal(240, chat, 3);
        Assert.Equal(240, list, 3);
    }

    [Fact]
    public void HiddenPlayerGivesSidesEverything()
    {
        var (chat, p, list) = UltrawideLayout.Split(new Size(1920, 480), 16.0 / 9.0, 240, 0.25, false);
        Assert.Equal(0, p);
        Assert.Equal(480, chat, 3);
        Assert.Equal(1440, list, 3);
    }

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
        Assert.Equal(1920, chat.Bounds.Width + player.Bounds.Width + streams.Bounds.Width, 0);
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
