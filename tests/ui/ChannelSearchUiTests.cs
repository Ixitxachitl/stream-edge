// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;
using StreamEdge.Streams;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

public class ChannelSearchUiTests(HeadlessAvalonia ui) : UiTest(ui)
{
    /// <summary>The live list's search box.</summary>
    private static ChannelPicker Picker(Window window) =>
        window.FindControl<StreamListView>("StreamsPane")!.FindControl<ChannelPicker>("ChannelSearch")!;

    private static MainViewModel Demo(AppSettings? settings = null)
    {
        var vm = new MainViewModel(settings ?? new AppSettings(), new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        // Only what's known locally: no waiting on Twitch in tests.
        vm.Search.Debounce = TimeSpan.FromHours(1);
        return vm;
    }

    [Fact]
    public void TypingSuggestsKnownChannelsAndTheNameAsTyped() => Ui(() =>
    {
        var vm = Demo();
        vm.Search.Query = "velvet";

        Assert.True(vm.Search.IsOpen);
        Assert.Equal("velvetcartridge", vm.Search.Suggestions[0].Login);
        Assert.True(vm.Search.Suggestions[0].IsLive);
        Assert.Equal(0, vm.Search.SelectedIndex);
        // Not a known channel, but a valid name: offered last, to join as typed.
        var typed = vm.Search.Suggestions[^1];
        Assert.True(typed.IsTyped);
        Assert.Equal("velvet", typed.Login);

        vm.Search.Query = "";
        Assert.False(vm.Search.IsOpen);
        Assert.Empty(vm.Search.Suggestions);
    });

    [Fact]
    public void AcceptingJoinsInANewTabAndClearsTheBox() => Ui(() =>
    {
        var vm = Demo();
        vm.Search.Query = "#Comet";
        vm.Search.Accept(vm.Search.SelectedIndex);

        Assert.Equal(["pixelperegrine", "cometcrumb"], vm.Tabs.Select(t => t.Login));
        Assert.Equal("cometcrumb", vm.FrontTab?.Login);
        Assert.Equal("", vm.Search.Query);
        Assert.False(vm.Search.IsOpen);
    });

    [Fact]
    public void EnterWithNothingPickedJoinsTheTypedName() => Ui(() =>
    {
        var vm = Demo();
        vm.Search.Query = "twitch.tv/somebody_new";
        vm.Search.Accept(-1, OpenMode.Background);

        Assert.Contains(vm.Tabs, t => t.Login == "somebody_new");
        Assert.Equal("pixelperegrine", vm.FrontTab?.Login);

        // Not a name at all: nothing to join.
        vm.Search.Query = "two words";
        vm.Search.Accept(-1);
        Assert.Equal(2, vm.Tabs.Count);
    });

    [Fact]
    public void ArrowKeysWrapThroughSuggestions() => Ui(() =>
    {
        var vm = Demo();
        vm.Search.Query = "e";
        var count = vm.Search.Suggestions.Count;
        Assert.True(count > 2);
        vm.Search.Move(-1);
        Assert.Equal(count - 1, vm.Search.SelectedIndex);
        vm.Search.Move(1);
        Assert.Equal(0, vm.Search.SelectedIndex);
    });

    [Fact]
    public void UnfollowedOpenChannelsHeadTheListAboveASeparator() => Ui(() =>
    {
        var vm = Demo();
        vm.OpenChannel("somebody_new", OpenMode.Background);
        vm.OpenChannel("another_one", OpenMode.Background);

        var watched = vm.Streams.Items.TakeWhile(i => i.IsWatched).ToList();
        Assert.Equal(["somebody_new", "another_one"], watched.Select(i => i.Stream.Login));
        Assert.Equal([false, true], watched.Select(i => i.SeparatorBelow));
        // Demo plays nothing, so they're offline.
        Assert.All(watched, i => Assert.Equal("Offline", i.Game));
        Assert.All(watched, i => Assert.Equal("", i.ViewersText));
        // The header still counts the followed live channels.
        Assert.StartsWith("Live Streams (12)", vm.Streams.Header);
        Assert.Equal(14, vm.Streams.Items.Count);

        // Closing a tab takes its row away.
        vm.Tabs.Single(t => t.Login == "another_one").CloseCommand.Execute(null);
        var left = Assert.Single(vm.Streams.Items, i => i.IsWatched);
        Assert.True(left.SeparatorBelow);

        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT") is { Length: > 0 } path)
#pragma warning disable CS0618
            frame!.Save(path.Replace(".png", "-watched.png"));
#pragma warning restore CS0618
        window.Close();
    });

    [Fact]
    public void SearchBoxShowsSuggestionsUnderIt() => Ui(() =>
    {
        var vm = Demo();
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        var box = Picker(window).FindControl<TextBox>("ChannelSearchBox")!;
        box.Focus();
        vm.Search.Query = "Velvet";
        Dispatcher.UIThread.RunJobs();

        var popup = Picker(window).FindControl<Avalonia.Controls.Primitives.Popup>("ChannelSuggestionPopup")!;
        Assert.True(popup.IsOpen);
        var frame = window.CaptureRenderedFrame();
        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT") is { Length: > 0 } path)
#pragma warning disable CS0618
            frame!.Save(path.Replace(".png", "-search.png"));
#pragma warning restore CS0618
        window.Close();
    });

    [Fact]
    public void RaidMovesTheTabToTheRaidedChannelInPlace() => Ui(() =>
    {
        var vm = Demo();
        vm.OpenChannel("other", OpenMode.Background);
        vm.OnRaid(new RaidEvent("", "pixelperegrine", "222", "cometcrumb", "CometCrumb", 42));

        // Same place, now the raided channel, still on video.
        Assert.Equal(["cometcrumb", "other"], vm.Columns[0].Tabs.Select(t => t.Login));
        Assert.Equal("cometcrumb", vm.FrontTab?.Login);
        Assert.Contains(vm.FrontTab!.Chat.Lines, l => l.Message.Text.Contains("Followed the raid from"));
    });

    [Fact]
    public void WithFollowingOffTheRaiderStaysAndSaysSo() => Ui(() =>
    {
        var vm = Demo(new AppSettings { FollowRaids = false });
        vm.OnRaid(new RaidEvent("", "pixelperegrine", "222", "cometcrumb", "CometCrumb", 42));

        var tab = Assert.Single(vm.Tabs);
        Assert.Equal("pixelperegrine", tab.Login);
        Assert.Contains(tab.Chat.Lines, l => l.Message.Text == "Raiding CometCrumb with 42 viewers.");
    });

    [Fact]
    public void RaidIntoAnOpenChannelBringsItForward() => Ui(() =>
    {
        var vm = Demo();
        vm.OpenChannel("cometcrumb", OpenMode.Background);
        vm.OnRaid(new RaidEvent("", "pixelperegrine", "222", "cometcrumb", "CometCrumb", 42));

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Equal("cometcrumb", vm.FrontTab?.Login);
    });
}
