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
}
