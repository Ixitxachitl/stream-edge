// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using StreamEdge.Chat;
using Xunit;

namespace StreamEdge.Tests;

public class TwitchBadgesTests
{
    private const string Global = """
        {"data":[
          {"set_id":"moderator","versions":[{"id":"1",
            "image_url_1x":"https://static-cdn.jtvnw.net/badges/v1/3267646d-33f0-4b17-b3df-f923a41db1d0/1",
            "image_url_2x":"https://static-cdn.jtvnw.net/badges/v1/3267646d-33f0-4b17-b3df-f923a41db1d0/2",
            "image_url_4x":"https://static-cdn.jtvnw.net/badges/v1/3267646d-33f0-4b17-b3df-f923a41db1d0/3",
            "title":"Moderator","description":"Moderator","click_action":null,"click_url":null}]},
          {"set_id":"subscriber","versions":[
            {"id":"0","image_url_1x":"https://static-cdn.jtvnw.net/badges/v1/5d9f2208/1","image_url_2x":"https://static-cdn.jtvnw.net/badges/v1/5d9f2208/2","image_url_4x":"https://static-cdn.jtvnw.net/badges/v1/5d9f2208/3","title":"Subscriber"},
            {"id":"12","image_url_1x":"https://static-cdn.jtvnw.net/badges/v1/global12/1","image_url_2x":"https://static-cdn.jtvnw.net/badges/v1/global12/2","image_url_4x":"https://static-cdn.jtvnw.net/badges/v1/global12/3","title":"Subscriber"}]}]}
        """;

    private const string Channel = """
        {"data":[{"set_id":"subscriber","versions":[
          {"id":"12","image_url_1x":"https://static-cdn.jtvnw.net/badges/v1/chan12/1","image_url_2x":"https://static-cdn.jtvnw.net/badges/v1/chan12/2","image_url_4x":"https://static-cdn.jtvnw.net/badges/v1/chan12/3","title":"1-Year Subscriber"}]}]}
        """;

    [Fact]
    public void ParsesSetVersionKeysWith2xImages()
    {
        var map = TwitchBadges.Parse(Global);
        Assert.Equal(3, map.Count);
        Assert.Equal(new Uri("https://static-cdn.jtvnw.net/badges/v1/3267646d-33f0-4b17-b3df-f923a41db1d0/2"), map["moderator/1"].ImageUrl);
        Assert.Equal("Moderator", map["moderator/1"].Title);
    }

    [Fact]
    public void ChannelBadgesOverrideGlobalOnesInThatChannelOnly()
    {
        var badges = new TwitchBadges();
        badges.SetGlobal(TwitchBadges.Parse(Global));
        badges.SetChannel("71092938", TwitchBadges.Parse(Channel));

        Assert.Equal("1-Year Subscriber", badges.Resolve("71092938", "subscriber/12")!.Title);
        Assert.Equal("Subscriber", badges.Resolve("999", "subscriber/12")!.Title);
        Assert.Equal("Subscriber", badges.Resolve("71092938", "subscriber/0")!.Title);
        Assert.Equal("Moderator", badges.Resolve(null, "moderator/1")!.Title);
        Assert.Null(badges.Resolve("71092938", "nonexistent/1"));
    }

    [Fact]
    public async Task NoTokenMeansNoRequestAndNoBadges()
    {
        var fake = new FakeHttp(_ => FakeHttp.Json(Global));
        var badges = new TwitchBadges();
        await badges.LoadGlobalAsync(new HttpClient(fake), "client", null);
        Assert.Equal(0, fake.Calls);
        Assert.Null(badges.Resolve(null, "moderator/1"));
    }

    [Fact]
    public async Task LoadSendsHelixHeaders()
    {
        HttpRequestMessage? seen = null;
        var fake = new FakeHttp(req =>
        {
            seen = req;
            return FakeHttp.Json(Channel);
        });
        var badges = new TwitchBadges();
        await badges.LoadChannelAsync(new HttpClient(fake), "cid", "tok", "71092938");
        Assert.Equal("https://api.twitch.tv/helix/chat/badges?broadcaster_id=71092938", seen!.RequestUri!.ToString());
        Assert.Equal("cid", seen.Headers.GetValues("Client-Id").Single());
        Assert.Equal("Bearer tok", seen.Headers.Authorization!.ToString());
        Assert.NotNull(badges.Resolve("71092938", "subscriber/12"));
    }

    [Fact]
    public async Task UnauthorizedIsSwallowed()
    {
        var badges = new TwitchBadges();
        await badges.LoadGlobalAsync(new HttpClient(new FakeHttp(_ => FakeHttp.Status(HttpStatusCode.Unauthorized))), "c", "expired");
        Assert.Null(badges.Resolve(null, "moderator/1"));
    }
}
