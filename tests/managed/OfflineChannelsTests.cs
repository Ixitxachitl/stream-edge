// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Streams;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.Tests;

public class OfflineChannelsTests
{
    [Fact]
    public void LastBroadcastsComeBackByLoginAndSkipUnknownUsers()
    {
        var found = LastBroadcasts.Parse("""
            {"data":{"users":[
              {"login":"shroud","lastBroadcast":{"startedAt":"2026-10-17T01:53:26.477148Z","title":"second season","game":{"name":"WARDOGS"}}},
              {"login":"newbie","lastBroadcast":null},
              null]}}
            """);
        var shroud = Assert.Single(found).Value;
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 1, 53, 26, 477, TimeSpan.Zero), shroud.StartedAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal("second season", shroud.Title);
        Assert.Equal("WARDOGS", shroud.Game);
        Assert.True(found.ContainsKey("SHROUD"));
        Assert.Empty(LastBroadcasts.Parse("""{"errors":[{"message":"nope"}]}"""));
    }

    [Fact]
    public async Task FollowedChannelsLabelsAndTagsComeFromHelix()
    {
        var http = new HttpClient(new FakeHttp(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/channels/followed", StringComparison.Ordinal))
                return url.Contains("after=", StringComparison.Ordinal)
                    ? FakeHttp.Json("""{"data":[{"broadcaster_id":"3","broadcaster_login":"c","broadcaster_name":""}],"pagination":{}}""")
                    : FakeHttp.Json("""{"data":[{"broadcaster_id":"1","broadcaster_login":"a","broadcaster_name":"A"},{"broadcaster_id":"2","broadcaster_login":"b","broadcaster_name":"B"}],"pagination":{"cursor":"next"}}""");
            if (url.Contains("/helix/channels?", StringComparison.Ordinal))
                return FakeHttp.Json("""{"data":[{"broadcaster_id":"1","content_classification_labels":["MatureGame","Gambling"],"is_branded_content":true},{"broadcaster_id":"2","content_classification_labels":[],"is_branded_content":false}]}""");
            return FakeHttp.Json("""{"data":[{"user_id":"1","user_login":"a","user_name":"A","game_name":"G","title":"T","viewer_count":5,"started_at":"2026-10-18T00:00:00Z","tags":["English","Speedrun"]}],"pagination":{}}""");
        }));
        var helix = new HelixClient(http, "client", "token");

        var followed = await helix.GetFollowedChannelsAsync("me", default);
        Assert.Equal(["a", "b", "c"], followed.Select(c => c.Login));
        Assert.Equal("c", followed[2].DisplayName);

        var labels = await helix.GetContentLabelsAsync(["1", "2"], default);
        Assert.Equal(["MatureGame", "Gambling", ContentLabels.BrandedContent], labels["1"]);
        Assert.False(labels.ContainsKey("2"));
        Assert.Equal("Mature-rated game", ContentLabels.Describe("MatureGame"));

        var live = Assert.Single(await helix.GetFollowedStreamsAsync("me", default));
        Assert.Equal(["English", "Speedrun"], live.Tags);
    }

    [Theory]
    [InlineData(45, "45m")]
    [InlineData(5 * 60, "5h")]
    [InlineData(47 * 60, "47h")]
    [InlineData(3 * 24 * 60, "3d")]
    [InlineData(90 * 24 * 60, "3mo")]
    public void AgoIsCoarse(int minutes, string text) => Assert.Equal(text, UiFormats.Ago(TimeSpan.FromMinutes(minutes)));
}
