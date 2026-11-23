// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.Playback;
using Xunit;

namespace StreamEdge.UiTests;

public class OfflineBannerTests(HeadlessAvalonia ui) : UiTest(ui)
{
    private static void Apply(PlayerViewModel player, PlaybackState state, string message = "") =>
        player.Apply(new PlaybackStatus(state, null, 0, null, 1, false, message), [], null);

    [Fact]
    public void TheBannerStaysUpWhileAnOfflineChannelIsCheckedAgain() => Ui(() =>
    {
        var player = new PlayerViewModel { OfflineImage = new Uri("https://example.com/banner.png") };
        Apply(player, PlaybackState.Resolving, "Resolving stream…");
        Assert.False(player.ShowsBanner);
        Assert.Equal("Resolving stream…", player.Status);
        Apply(player, PlaybackState.Offline, "Offline");
        Assert.True(player.ShowsBanner);

        var changed = new List<string?>();
        player.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Every half minute the session asks whether it's live yet.
        Apply(player, PlaybackState.Resolving, "Resolving stream…");
        Assert.True(player.ShowsBanner);
        Apply(player, PlaybackState.Offline, "Offline");
        // The tab sent behind with PIP off, then brought back.
        Apply(player, PlaybackState.Stopped);
        Apply(player, PlaybackState.Resolving, "Resolving stream…");
        Apply(player, PlaybackState.Offline, "Offline");
        // Nothing the banner or the text under it hangs on moved.
        Assert.Empty(changed);
        Assert.Equal("Offline.", player.Status);

        // A check that fails says so, under the banner still.
        Apply(player, PlaybackState.Resolving, "Resolving stream…");
        Apply(player, PlaybackState.Error, "Network error: no route");
        Assert.True(player.ShowsBanner);
        Assert.Equal("Network error: no route", player.Status);
        Apply(player, PlaybackState.Resolving, "Resolving stream…");
        Apply(player, PlaybackState.Offline, "Offline");
        Assert.True(player.ShowsBanner);
        Assert.Equal("Offline.", player.Status);
        Assert.DoesNotContain(nameof(PlayerViewModel.ShowsBanner), changed);

        // It went live: the video takes over.
        Apply(player, PlaybackState.Resolving, "Resolving stream…");
        Apply(player, PlaybackState.Playing);
        Assert.False(player.ShowsBanner);
        Assert.True(player.IsVideoVisible);
    });
}
