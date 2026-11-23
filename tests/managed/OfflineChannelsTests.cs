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
    public void LookedUpChannelsAreLiveWithTheirStreamOrNot()
    {
        var rows = ChannelLookup.Parse("""
            {"data":{"users":[
              {"id":"1","login":"relaxbeats","displayName":"RelaxBeats","profileImageURL":"https://static-cdn.jtvnw.net/jtv_user_pictures/a-70x70.jpeg",
               "stream":{"title":"Chill","viewersCount":102,"createdAt":"2026-11-22T15:06:32Z","game":{"name":"Music"},"freeformTags":[{"name":"English"},{"name":"lofi"}]}},
              {"id":"2","login":"twitch","displayName":"Twitch","profileImageURL":"https://static-cdn.jtvnw.net/jtv_user_pictures/b-70x70.png","stream":null},
              null,
              {"id":"3","login":"nogame","displayName":"","profileImageURL":null,"stream":{"title":"hi","viewersCount":0,"createdAt":"2026-11-22T15:06:32Z","game":null,"freeformTags":[]}}]}}
            """)!;
        Assert.Equal(["relaxbeats", "twitch", "nogame"], rows.Select(r => r.Login));

        var live = rows[0];
        Assert.True(live.IsLive);
        Assert.Equal(("RelaxBeats", "Chill", "Music", 102, "1"), (live.DisplayName, live.Title, live.Game, live.Viewers, live.UserId));
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 15, 6, 32, TimeSpan.Zero), live.StartedAt);
        Assert.Equal(["English", "lofi"], live.Tags);
        Assert.EndsWith("a-70x70.jpeg", live.ProfileImage!.AbsoluteUri);

        var offline = rows[1];
        Assert.False(offline.IsLive);
        Assert.Equal(("Twitch", -1), (offline.DisplayName, offline.Viewers));

        Assert.Equal(("nogame", "", 0), (rows[2].DisplayName, rows[2].Game, rows[2].Viewers));
        Assert.Null(rows[2].Tags);
        Assert.Null(rows[2].ProfileImage);

        // Not an answer: said so, rather than "none of them exist".
        Assert.Null(ChannelLookup.Parse("""{"errors":[{"message":"nope"}]}"""));
        Assert.Null(ChannelLookup.Parse("<html>"));
    }

    [Fact]
    public async Task ALookupThatFailsThrowsSoRowsCanKeepWhatTheyHad()
    {
        var asked = new List<string>();
        var ok = true;
        var http = new HttpClient(new FakeHttp(request =>
        {
            asked.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return ok
                ? FakeHttp.Json("""{"data":{"users":[{"id":"2","login":"twitch","displayName":"Twitch","stream":null}]}}""")
                : new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        }));

        var row = Assert.Single(await ChannelLookup.FetchAsync(http, ["Twitch", "twitch"], default));
        Assert.Equal("twitch", row.Login);
        Assert.Contains("\"logins\":[\"twitch\"]", Assert.Single(asked));

        ok = false;
        await Assert.ThrowsAsync<HttpRequestException>(() => ChannelLookup.FetchAsync(http, ["twitch"], default));
        // Nothing starred that isn't followed: nothing asked.
        Assert.Empty(await ChannelLookup.FetchAsync(http, [], default));
        Assert.Equal(2, asked.Count);
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
