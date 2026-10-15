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

    [Theory]
    [InlineData(0, "0")]
    [InlineData(676, "676")]
    [InlineData(999, "999")]
    [InlineData(1000, "1K")]
    [InlineData(2447, "2.4K")]
    [InlineData(11455, "11.4K")]
    [InlineData(999_999, "999.9K")]
    [InlineData(1_250_000, "1.2M")]
    public void ViewersAreCompactLikeTwitchsSidebar(int viewers, string expected) =>
        Assert.Equal(expected, UiFormats.CompactViewers(viewers));

    [Fact]
    public async Task FollowedStreamsCarryTheUserIdAndAvatarsComeInBatches()
    {
        var userRequests = new List<Uri>();
        var http = new HttpClient(new FakeHttp(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/streams/followed"))
                return FakeHttp.Json("""
                    {"data":[{"user_id":"1","user_login":"a","user_name":"A","game_name":"g","title":"t","viewer_count":5,"started_at":"2026-10-14T18:00:00Z"}],
                     "pagination":{}}
                    """);
            userRequests.Add(request.RequestUri);
            var ids = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query).GetValues("id")!;
            var users = ids.Select(id =>
                $$"""{"id":"{{id}}","profile_image_url":"https://static-cdn.jtvnw.net/jtv_user_pictures/{{id}}-profile_image-300x300.png"}""");
            return FakeHttp.Json("""{"data":[""" + string.Join(",", users) + "]}");
        }));
        var helix = new HelixClient(http, "client", "token");

        var streams = await helix.GetFollowedStreamsAsync("me", CancellationToken.None);
        Assert.Equal("1", streams.Single().UserId);

        var ids = Enumerable.Range(1, 150).Select(i => i.ToString()).ToList();
        var images = await helix.GetProfileImagesAsync(ids, CancellationToken.None);
        Assert.Equal(2, userRequests.Count); // 100 + 50: Helix's per-request limit
        Assert.Equal(150, images.Count);
        Assert.Equal("https://static-cdn.jtvnw.net/jtv_user_pictures/7-profile_image-70x70.png", images["7"].AbsoluteUri);
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
