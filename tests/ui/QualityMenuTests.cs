// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;
using StreamEdge.Playback;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

public class QualityMenuTests(HeadlessAvalonia ui) : UiTest(ui)
{
    private static readonly Uri Base = new("https://video.example/");

    private static readonly IReadOnlyList<HlsVariant> Variants =
    [
        new("1080p60", new Uri(Base, "a.m3u8"), 8_000_000, 1080, 60, true, false),
        new("720p60", new Uri(Base, "b.m3u8"), 3_400_000, 720, 60, false, false),
        new("480p", new Uri(Base, "c.m3u8"), 1_400_000, 480, 30, false, false),
    ];

    /// <summary>What a session reports once it's playing.</summary>
    private static void Playing(PlayerViewModel player) =>
        player.Apply(new PlaybackStatus(PlaybackState.Playing, "1080p60", 1, null, 1, false, ""), Variants, Variants[0]);

    /// <summary>Opens the quality menu and counts the entries it draws.</summary>
    private static int Shown(Window window)
    {
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Flyout is MenuFlyout { ItemsSource: not null });
        var flyout = (MenuFlyout)button.Flyout!;
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        var count = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single()
            .GetVisualDescendants().OfType<MenuItem>().Count(m => m.IsVisible);
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();
        return count;
    }

    [Fact]
    public void TheMenuShowsTheNewStreamsQualitiesAfterSwitching() => Ui(() =>
    {
        var vm = new MainViewModel(new AppSettings(), new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Playing(vm.Player);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, Shown(window));

        // Double-click another stream: it replaces the front tab in place,
        // and the old tab's list empties as its session stops.
        var old = vm.Player;
        vm.Streams.Items.First(i => i.Stream.Login == "bigtimebramble").OpenCommand.Execute(null);
        old.Qualities.Clear();
        Playing(vm.Player);
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(old, vm.Player);
        // Picking a quality rebuilds the list while the menu is open.
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Flyout is MenuFlyout { ItemsSource: not null });
        var flyout = (MenuFlyout)button.Flyout!;
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        vm.Player.Apply(new PlaybackStatus(PlaybackState.Playing, "720p60", 1, null, 1, false, ""), Variants, Variants[1]);
        Dispatcher.UIThread.RunJobs();
        var names = string.Join(",", window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single().GetVisualDescendants().OfType<MenuItem>().Select(m => m.Header));
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("1080p60 (source),720p60,480p", names);
        Assert.Equal(3, Shown(window));
        window.Close();
    });
}
