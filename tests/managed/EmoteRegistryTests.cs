// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using StreamEdge.Chat.Emotes;
using Xunit;

namespace StreamEdge.Tests;

public class EmoteRegistryTests
{
    private static Emote E(string code, EmoteProvider p) => new(code, new Uri($"https://example.invalid/{p}/{code}"), p);

    private static EmoteRegistry Filled()
    {
        var r = new EmoteRegistry(new HttpClient(new FakeHttp(_ => throw new InvalidOperationException("no network"))));
        foreach (var p in new[] { EmoteProvider.SevenTv, EmoteProvider.Bttv, EmoteProvider.Ffz })
        {
            r.SetGlobal(p, [E("Shared", p), E($"Only{p}", p)]);
            r.SetChannel("1", p, [E("ChanShared", p), E("Shared", p), E($"Chan{p}", p)]);
        }
        return r;
    }

    [Fact]
    public void ChannelBeatsGlobalAndSevenTvBeatsBttvBeatsFfz()
    {
        var r = Filled();
        Assert.Equal(EmoteProvider.SevenTv, r.Lookup("1", "Shared")!.Provider);
        Assert.Contains("/SevenTv/", r.Lookup("1", "Shared")!.ImageUrl.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(EmoteProvider.SevenTv, r.Lookup("1", "ChanShared")!.Provider);
        // Another room only sees globals.
        Assert.Equal(EmoteProvider.SevenTv, r.Lookup("2", "Shared")!.Provider);
        Assert.Null(r.Lookup("2", "ChanShared"));
        Assert.Null(r.Lookup(null, "ChanShared"));
    }

    [Fact]
    public void ChannelFfzBeatsGlobalSevenTv()
    {
        var r = new EmoteRegistry(new HttpClient());
        r.SetGlobal(EmoteProvider.SevenTv, [E("X", EmoteProvider.SevenTv)]);
        r.SetChannel("1", EmoteProvider.Ffz, [E("X", EmoteProvider.Ffz)]);
        Assert.Equal(EmoteProvider.Ffz, r.Lookup("1", "X")!.Provider);
    }

    [Fact]
    public void LookupIsCaseSensitive() => Assert.Null(Filled().Lookup("1", "shared"));

    [Fact]
    public void DisabledProvidersAreNotMatched()
    {
        var r = Filled();
        r.UseSevenTv = false;
        Assert.Equal(EmoteProvider.Bttv, r.Lookup("1", "Shared")!.Provider);
        Assert.Null(r.Lookup("1", "OnlySevenTv"));
        r.UseBttv = false;
        Assert.Equal(EmoteProvider.Ffz, r.Lookup("1", "Shared")!.Provider);
        r.UseFfz = false;
        Assert.Null(r.Lookup("1", "Shared"));
        r.UseSevenTv = true;
        Assert.Equal(EmoteProvider.SevenTv, r.Lookup("1", "Shared")!.Provider);
    }

    [Fact]
    public void ApplyCopiesSettings()
    {
        var r = new EmoteRegistry(new HttpClient());
        r.Apply(new AppSettings { UseBttv = false, UseFfz = true, UseSevenTv = false });
        Assert.False(r.UseBttv);
        Assert.True(r.UseFfz);
        Assert.False(r.UseSevenTv);
    }

    [Fact]
    public async Task DisabledProvidersAreNotFetched()
    {
        var hosts = new List<string>();
        var fake = new FakeHttp(req =>
        {
            lock (hosts) hosts.Add(req.RequestUri!.Host);
            return FakeHttp.Json("{}");
        });
        var r = new EmoteRegistry(new HttpClient(fake)) { UseBttv = false, UseFfz = false };
        await r.LoadGlobalsAsync();
        await r.LoadChannelAsync("1");
        Assert.Equal(["7tv.io", "7tv.io"], hosts);
    }

    [Fact]
    public async Task OneProviderFailingDoesNotStopTheOthers()
    {
        var fake = new FakeHttp(req => req.RequestUri!.Host switch
        {
            "api.betterttv.net" => FakeHttp.Json("""[{"id":"abc","code":"FeelsBirthdayMan","imageType":"png","animated":false}]"""),
            "7tv.io" => FakeHttp.Status(HttpStatusCode.InternalServerError),
            _ => FakeHttp.Json("{ this is not json"),
        });
        var r = new EmoteRegistry(new HttpClient(fake));
        string? changed = "unset";
        r.Changed += room => changed = room;
        await r.LoadGlobalsAsync();
        Assert.Equal(EmoteProvider.Bttv, r.Lookup("1", "FeelsBirthdayMan")!.Provider);
        Assert.Null(changed);
    }

    [Fact]
    public async Task LoadChannelStoresSetsAndFfzRoomBadges()
    {
        var fake = new FakeHttp(req => req.RequestUri!.ToString() switch
        {
            "https://api.betterttv.net/3/cached/users/twitch/42" => FakeHttp.Status(HttpStatusCode.NotFound),
            "https://7tv.io/v3/users/twitch/42" => FakeHttp.Json("""
                {"emote_set":{"emotes":[{"id":"1","name":"Chan7","flags":0,"data":{"flags":0,
                  "host":{"url":"//cdn.7tv.app/emote/1","files":[{"name":"2x.webp","width":64,"height":64,"format":"WEBP"}]}}}]}}
                """),
            "https://api.frankerfacez.com/v1/room/id/42" => FakeHttp.Json("""
                {"room":{"set":5,"mod_urls":{"1":"https://cdn.frankerfacez.com/room-badge/mod/1","2":"https://cdn.frankerfacez.com/room-badge/mod/2"},"vip_badge":null},
                 "sets":{"5":{"emoticons":[{"id":7,"name":"ChanF","urls":{"1":"https://cdn.frankerfacez.com/emote/7/1"}}]}}}
                """),
            var other => throw new InvalidOperationException(other),
        });
        var r = new EmoteRegistry(new HttpClient(fake));
        await r.LoadChannelAsync("42");
        Assert.Equal(EmoteProvider.SevenTv, r.Lookup("42", "Chan7")!.Provider);
        Assert.Equal(EmoteProvider.Ffz, r.Lookup("42", "ChanF")!.Provider);
        Assert.Equal(new Uri("https://cdn.frankerfacez.com/room-badge/mod/2"), r.FfzModeratorBadge("42"));
        Assert.Null(r.FfzVipBadge("42"));
        r.UseFfz = false;
        Assert.Null(r.FfzModeratorBadge("42"));
        r.UnloadChannel("42");
        Assert.Null(r.Lookup("42", "Chan7"));
    }

    [Fact]
    public void FfzUserBadgesRespectToggle()
    {
        var r = new EmoteRegistry(new HttpClient());
        r.SetFfzBadges(FfzEmotes.ParseBadges("""
            {"badges":[{"id":1,"name":"developer","title":"FFZ Developer","color":"#FAAF19","urls":{"2":"https://cdn.frankerfacez.com/badge/1/2"}}],
             "users":{"1":[26964566]}}
            """));
        Assert.Equal("FFZ Developer", Assert.Single(r.FfzBadgesFor("26964566")).Title);
        r.UseFfz = false;
        Assert.Empty(r.FfzBadgesFor("26964566"));
    }
}
