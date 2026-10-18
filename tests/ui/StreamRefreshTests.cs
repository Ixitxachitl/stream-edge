// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.Streams;
using Xunit;

namespace StreamEdge.UiTests;

public class StreamRefreshTests(HeadlessAvalonia ui) : UiTest(ui)
{
    [Fact]
    public void AFailedRefreshIsFlaggedOverTheListItKept() => Ui(() =>
    {
        var list = new StreamListViewModel(new AppSettings());
        var now = DateTimeOffset.Now;
        list.Set([new LiveStream("a", "A", "t", "g", 5, now)], now);
        Assert.Equal("Live channels you follow", list.HeaderTip);
        Assert.DoesNotContain("⚠", list.Header);

        var changed = new List<string?>();
        list.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        list.RefreshProblem = "Couldn't refresh: 401 (Unauthorized)";

        // Still listed, but the header says the list is stale and why.
        Assert.Single(list.Items);
        Assert.EndsWith("⚠", list.Header);
        Assert.Equal("Couldn't refresh: 401 (Unauthorized)", list.HeaderTip);
        Assert.Contains(nameof(StreamListViewModel.Header), changed);

        list.RefreshProblem = null;
        Assert.DoesNotContain("⚠", list.Header);
    });

    [Fact]
    public void OfflineChannelsListBelowLiveOnesMostRecentlyLiveFirst() => Ui(() =>
    {
        var settings = new AppSettings { Favorites = ["neverlive"] };
        var list = new StreamListViewModel(settings);
        var now = DateTimeOffset.Now;
        list.Set([new LiveStream("live", "Live", "t", "g", 5, now)], now);
        LiveStream Offline(string login, double hoursAgo) => new(login, login, "", "", -1, default, IsLive: false,
            LastLive: double.IsNaN(hoursAgo) ? null : new LastBroadcast(now.AddHours(-hoursAgo), "old title", "Chess"));
        list.SetOffline([Offline("weekago", 7 * 24), Offline("neverlive", double.NaN), Offline("hourago", 1), Offline("live", 2)], now);

        // Off by default: just the live one.
        Assert.Equal(["live"], list.Items.Select(i => i.Stream.Login));

        list.ShowOffline = true;
        Assert.True(settings.ShowOffline);
        // Live first; then offline by last live, newest first, whatever the
        // sort or the stars; a channel listed live isn't repeated.
        Assert.Equal(["live", "hourago", "weekago", "neverlive"], list.Items.Select(i => i.Stream.Login));
        Assert.True(list.Items[0].SeparatorBelow);
        Assert.Equal("Last live 1h ago · Chess", list.Items[1].Uptime);
        Assert.Equal("old title", list.Items[1].CardTitle);
        Assert.Equal("Offline · 1h ago", list.Items[1].Game);
        Assert.Equal("Offline", list.Items[3].Uptime);
        Assert.StartsWith("Live Streams (1)", list.Header);   // the count is still live channels
    });

    [Fact]
    public void TheHoverCardNamesContentLabelsAndTags() => Ui(() =>
    {
        var list = new StreamListViewModel(new AppSettings());
        var now = DateTimeOffset.Now;
        list.Set([new LiveStream("a", "A", "t", "g", 5, now, Tags: ["English", "Speedrun"],
            ContentLabels: ["MatureGame", "ProfanityVulgarity"])], now);
        var item = list.Items[0];
        Assert.Equal("⚠ Mature-rated game, Significant Profanity or Vulgarity", item.LabelsText);
        Assert.Equal("#English  #Speedrun", item.TagsText);

        list.Set([new LiveStream("b", "B", "t", "g", 5, now)], now);
        Assert.False(list.Items[0].HasLabels);
        Assert.False(list.Items[0].HasTags);
    });
}
