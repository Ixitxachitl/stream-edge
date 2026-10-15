// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
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
}
