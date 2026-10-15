// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Streams;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.Tests;

public class StreamListTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 19, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(22, "22m")]
    [InlineData(59, "59m")]
    [InlineData(60, "1.0h")]
    [InlineData(144, "2.4h")]
    [InlineData(612, "10.2h")]
    public void UptimeIsCompact(int minutes, string expected) =>
        Assert.Equal(expected, UiFormats.Uptime(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void HeaderMatchesChattyStyle()
    {
        var s = new LiveStream("jerma985", "Jerma985", "t", "g", 11455, Now - TimeSpan.FromHours(2.7));
        Assert.Equal("Jerma985 (11,455 | 2.7h)", s.Header(Now));
    }

    [Fact]
    public void FavouritesSortFirstThenByViewers()
    {
        LiveStream S(string n, int v, bool fav = false) => new(n, n, "", "", v, Now, fav);
        var sorted = StreamSort.Apply([S("big", 5000), S("fav", 3, fav: true), S("mid", 50)], StreamSortOrder.Viewers);
        Assert.Equal(["fav", "big", "mid"], sorted.Select(s => s.Login));
    }

    [Fact]
    public void UptimeSortIsLongestFirst()
    {
        LiveStream S(string n, double h) => new(n, n, "", "", 1, Now - TimeSpan.FromHours(h));
        var sorted = StreamSort.Apply([S("short", 1), S("long", 10)], StreamSortOrder.Uptime);
        Assert.Equal(["long", "short"], sorted.Select(s => s.Login));
    }

    [Fact]
    public void StartupOptionsParse()
    {
        var o = StartupOptions.Parse(["--screen", "1", "--fullscreen", "--channel", "#Foo", "--demo"]);
        Assert.Equal(1, o.ScreenIndex);
        Assert.True(o.Fullscreen);
        Assert.True(o.Demo);
        Assert.Equal("Foo", o.Channel);
    }
}
