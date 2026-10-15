// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat.Emotes;
using Xunit;

namespace StreamEdge.Tests;

/// <summary>Samples are trimmed copies of real responses (Oct 2026).</summary>
public class EmoteProviderTests
{
    private const string BttvGlobal = """
        [{"id":"54fa8f1401e468494b85b537","code":":tf:","imageType":"png","animated":false,"userId":"5561169bd6b9d206222a8c19","modifier":false},
         {"id":"5e76d338d6581c3724c0f0b2","code":"cvHazmat","imageType":"png","animated":false,"userId":"5561169bd6b9d206222a8c19","modifier":false},
         {"id":"5b77ac3af7bddc567b1d5fb2","code":"SoSnowy","imageType":"gif","animated":true,"userId":"5561169bd6b9d206222a8c19","modifier":false},
         {"id":"662953eeea369c0ece39eb93","code":"s!","imageType":"png","animated":false,"userId":"5561169bd6b9d206222a8c19","modifier":true},
         {"id":"","code":"broken"}]
        """;

    private const string BttvChannel = """
        {"id":"5717c985b017b9d058fad265","bots":["xqcbot"],"avatar":"https://x/y.jpeg",
         "channelEmotes":[{"id":"5ada077451d4120ea3918426","code":"blobDance","imageType":"gif","animated":true,"userId":"5717c985b017b9d058fad265"}],
         "sharedEmotes":[{"id":"566ca04265dbbdab32ec054a","code":"monkaSHAKE","imageType":"gif","animated":true,"user":{"id":"1","name":"x","displayName":"X","providerId":"2"}}]}
        """;

    [Fact]
    public void BttvGlobalParses()
    {
        var emotes = BttvEmotes.ParseGlobal(BttvGlobal);
        Assert.Equal([":tf:", "cvHazmat", "SoSnowy"], emotes.Select(e => e.Code));
        Assert.Equal(new Uri("https://cdn.betterttv.net/emote/54fa8f1401e468494b85b537/2x"), emotes[0].ImageUrl);
        Assert.All(emotes, e => Assert.Equal(EmoteProvider.Bttv, e.Provider));
        Assert.False(emotes[0].ZeroWidth);
        Assert.True(emotes[1].ZeroWidth);
        Assert.True(emotes[2].ZeroWidth);
    }

    [Fact]
    public void BttvChannelMergesChannelAndShared()
    {
        var emotes = BttvEmotes.ParseChannel(BttvChannel);
        Assert.Equal(["blobDance", "monkaSHAKE"], emotes.Select(e => e.Code));
        Assert.Empty(BttvEmotes.ParseChannel("""{"channelEmotes":[],"sharedEmotes":[]}"""));
    }

    private const string FfzGlobal = """
        {"default_sets":[3,1539687],"sets":{
          "3":{"id":3,"_type":1,"title":"Global Emotes","emoticons":[
            {"id":4,"name":"ManChicken","height":29,"width":30,"public":false,"hidden":false,"modifier":false,"modifier_flags":0,
             "urls":{"1":"https://cdn.frankerfacez.com/emote/4/1","2":"https://cdn.frankerfacez.com/emote/4/2","4":"https://cdn.frankerfacez.com/emote/4/4"}},
            {"id":720507,"name":"ffzHyper","height":32,"width":32,"modifier":true,"modifier_flags":12289,
             "urls":{"1":"https://cdn.frankerfacez.com/emote/720507/1"}}]},
          "1532818":{"id":1532818,"title":"Not for everyone","emoticons":[
            {"id":5,"name":"Private","urls":{"1":"https://cdn.frankerfacez.com/emote/5/1"}}]},
          "1539687":{"id":1539687,"title":"Overlays","emoticons":[
            {"id":9001,"name":"ffzHat","height":32,"width":32,"modifier":true,"modifier_flags":0,
             "urls":{"1":"https://cdn.frankerfacez.com/emote/9001/1"}},
            {"id":9002,"name":"Wiggle","height":21,"width":28,"modifier":false,
             "urls":{"1":"https://cdn.frankerfacez.com/emote/9002/1","2":"https://cdn.frankerfacez.com/emote/9002/2"},
             "animated":{"1":"https://cdn.frankerfacez.com/emote/9002/animated/1","2":"https://cdn.frankerfacez.com/emote/9002/animated/2"}}]}},
         "users":{}}
        """;

    private const string FfzRoomJson = """
        {"room":{"_id":166895,"twitch_id":71092938,"id":"xqc","display_name":"xQc","set":166907,
                 "moderator_badge":"https://cdn.frankerfacez.com/room-badge/mod/id/71092938/v/98a226c4/1",
                 "vip_badge":{"1":"https://cdn.frankerfacez.com/room-badge/vip/id/71092938/1","2":"https://cdn.frankerfacez.com/room-badge/vip/id/71092938/2"},
                 "mod_urls":{"1":"https://cdn.frankerfacez.com/room-badge/mod/id/71092938/v/98a226c4/1"},"user_badges":{},"css":null},
         "sets":{"166907":{"id":166907,"_type":1,"title":"Channel: xQc","emoticons":[
           {"id":246878,"name":"WideHard","height":20,"width":50,"public":true,"hidden":false,"modifier":false,"modifier_flags":0,
            "urls":{"1":"https://cdn.frankerfacez.com/emote/246878/1","2":"https://cdn.frankerfacez.com/emote/246878/2","4":"https://cdn.frankerfacez.com/emote/246878/4"}}]}}}
        """;

    [Fact]
    public void FfzGlobalUsesDefaultSetsOnly()
    {
        var emotes = FfzEmotes.ParseGlobal(FfzGlobal);
        Assert.Equal(["ManChicken", "ffzHat", "Wiggle"], emotes.Select(e => e.Code));
        var chicken = emotes[0];
        Assert.Equal(new Uri("https://cdn.frankerfacez.com/emote/4/2"), chicken.ImageUrl);
        Assert.Equal((30, 29), (chicken.Width, chicken.Height));
        Assert.False(chicken.ZeroWidth);
        Assert.True(emotes[1].ZeroWidth);
        Assert.Equal(new Uri("https://cdn.frankerfacez.com/emote/9001/1"), emotes[1].ImageUrl); // 1x fallback
        Assert.Equal(new Uri("https://cdn.frankerfacez.com/emote/9002/animated/2"), emotes[2].ImageUrl);
    }

    [Fact]
    public void FfzRoomParsesEmotesAndBadges()
    {
        var room = FfzEmotes.ParseChannel(FfzRoomJson);
        var e = Assert.Single(room.Emotes);
        Assert.Equal("WideHard", e.Code);
        Assert.Equal(EmoteProvider.Ffz, e.Provider);
        Assert.Equal(new Uri("https://cdn.frankerfacez.com/room-badge/mod/id/71092938/v/98a226c4/1"), room.ModeratorBadge);
        Assert.Equal(new Uri("https://cdn.frankerfacez.com/room-badge/vip/id/71092938/2"), room.VipBadge);
    }

    [Fact]
    public void FfzRoomWithoutBadges()
    {
        var room = FfzEmotes.ParseChannel("""{"room":{"set":1,"mod_urls":null,"vip_badge":null},"sets":{"1":{"emoticons":[]}}}""");
        Assert.Empty(room.Emotes);
        Assert.Null(room.ModeratorBadge);
        Assert.Null(room.VipBadge);
    }

    [Fact]
    public void FfzBadgesMapUsersToBadges()
    {
        var table = FfzEmotes.ParseBadges("""
            {"badges":[
              {"id":2,"name":"bot","title":"Bot","slot":1,"replaces":"moderator","color":"#595959","image":"https://cdn.frankerfacez.com/badge/2/1",
               "urls":{"1":"https://cdn.frankerfacez.com/badge/2/1","2":"https://cdn.frankerfacez.com/badge/2/2","4":"https://cdn.frankerfacez.com/badge/2/4"},"css":null},
              {"id":3,"name":"supporter","title":"FFZ Supporter","slot":5,"color":"#755000",
               "urls":{"1":"https://cdn.frankerfacez.com/badge/3/1","2":"https://cdn.frankerfacez.com/badge/3/2","4":"https://cdn.frankerfacez.com/badge/3/4"}}],
             "users":{"2":[73227142,82455692],"3":[73227142],"99":[1]}}
            """);
        Assert.Equal(2, table.UserCount);
        var both = table.For("73227142");
        Assert.Equal(["Bot", "FFZ Supporter"], both.Select(b => b.Title));
        Assert.Equal(new Uri("https://cdn.frankerfacez.com/badge/2/2"), both[0].ImageUrl);
        Assert.Equal("#595959", both[0].BackgroundColor);
        Assert.Empty(table.For("1"));
        Assert.Empty(table.For(null));
    }

    private const string SevenTvGlobal = """
        {"id":"01GD17VE2R000E0RPNH2V9Z16G","name":"Global Emotes","flags":0,"tags":[],"immutable":false,"privileged":false,"emotes":[
          {"id":"01FGH8NE3800064MEQW00DNBNG","name":"reckH","flags":0,"timestamp":1632667089000,"actor_id":"01FBJ5134G0002BEVDT2R3WG7S",
           "data":{"id":"01FGH8NE3800064MEQW00DNBNG","name":"reckH","flags":0,"tags":["halloween"],"lifecycle":3,"state":["LISTED"],"listed":true,"animated":false,
                   "host":{"url":"//cdn.7tv.app/emote/01FGH8NE3800064MEQW00DNBNG","files":[
                     {"name":"1x.webp","static_name":"1x.webp","width":32,"height":32,"frame_count":1,"size":1156,"format":"WEBP"},
                     {"name":"2x.avif","static_name":"2x.avif","width":64,"height":64,"frame_count":1,"size":2000,"format":"AVIF"},
                     {"name":"2x.webp","static_name":"2x.webp","width":64,"height":64,"frame_count":1,"size":2626,"format":"WEBP"}]}}},
          {"id":"01GB2T9D8G000BP8MZ5ZCVBC0Q","name":"PETPET","flags":1,
           "data":{"id":"01GB2T9D8G000BP8MZ5ZCVBC0Q","name":"PETPET","flags":256,"animated":true,
                   "host":{"url":"//cdn.7tv.app/emote/01GB2T9D8G000BP8MZ5ZCVBC0Q","files":[
                     {"name":"2x.webp","width":64,"height":64,"format":"WEBP"}]}}},
          {"id":"A","name":"aliasName","flags":1,
           "data":{"id":"A","name":"originalName","flags":0,
                   "host":{"url":"//cdn.7tv.app/emote/A","files":[{"name":"2x.webp","width":112,"height":64,"format":"WEBP"}]}}},
          {"id":"B","name":"authorZw","flags":0,
           "data":{"id":"B","name":"authorZw","flags":256,"host":{"url":"//cdn.7tv.app/emote/B","files":[]}}},
          {"id":"C","name":"noHost","flags":0,"data":{"id":"C","flags":0}}]}
        """;

    [Fact]
    public void SevenTvGlobalParses()
    {
        var emotes = SevenTvEmotes.ParseGlobal(SevenTvGlobal);
        Assert.Equal(["reckH", "PETPET", "aliasName", "authorZw"], emotes.Select(e => e.Code));
        var reck = emotes[0];
        Assert.Equal(new Uri("https://cdn.7tv.app/emote/01FGH8NE3800064MEQW00DNBNG/2x.webp"), reck.ImageUrl);
        Assert.Equal((32, 32), (reck.Width, reck.Height));
        Assert.False(reck.ZeroWidth);
        Assert.True(emotes[1].ZeroWidth);          // both flags
        Assert.True(emotes[2].ZeroWidth);          // set-level flag only
        Assert.Equal((56, 32), (emotes[2].Width, emotes[2].Height)); // half the 2x file
        Assert.True(emotes[3].ZeroWidth);          // emote-level flag only
        Assert.All(emotes, e => Assert.Equal(EmoteProvider.SevenTv, e.Provider));
    }

    [Fact]
    public void SevenTvChannelReadsEmoteSet()
    {
        var json = """
            {"id":"71092938","platform":"TWITCH","username":"xqc","emote_capacity":1000,"emote_set_id":"01FE",
             "emote_set":{"id":"01FE","name":"xQc's Emotes","flags":0,"emotes":[
               {"id":"60ae7316f7c927fad14e6ca2","name":"catJAM","flags":0,
                "data":{"name":"catJAM","flags":0,"animated":true,"host":{"url":"//cdn.7tv.app/emote/60ae7316f7c927fad14e6ca2",
                        "files":[{"name":"2x.webp","width":64,"height":64,"format":"WEBP"}]}}}],"emote_count":1,"capacity":1000},
             "user":{"id":"01"}}
            """;
        Assert.Equal("catJAM", Assert.Single(SevenTvEmotes.ParseChannel(json)).Code);
        Assert.Empty(SevenTvEmotes.ParseChannel("""{"id":"1","emote_set":null}"""));
        Assert.Empty(SevenTvEmotes.ParseChannel("""{"id":"1","emote_set":{"emotes":null}}"""));
    }

    [Fact]
    public async Task FetchTreats404AsNoEmotes()
    {
        using var http = new HttpClient(new FakeHttp(_ => FakeHttp.Status(System.Net.HttpStatusCode.NotFound)));
        Assert.Empty(await BttvEmotes.FetchChannelAsync(http, "1", default));
        Assert.Empty(await SevenTvEmotes.FetchChannelAsync(http, "1", default));
        Assert.Same(FfzRoom.Empty, await FfzEmotes.FetchChannelAsync(http, "1", default));
    }

    [Fact]
    public async Task FetchAsksForAndDecodesGzip()
    {
        string? acceptEncoding = null;
        using var http = new HttpClient(new FakeHttp(req =>
        {
            acceptEncoding = string.Join(",", req.Headers.AcceptEncoding);
            var buffer = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                gz.Write(System.Text.Encoding.UTF8.GetBytes(BttvGlobal));
            var content = new ByteArrayContent(buffer.ToArray());
            content.Headers.ContentEncoding.Add("gzip");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
        }));
        Assert.Equal(3, (await BttvEmotes.FetchGlobalAsync(http, default)).Count);
        Assert.Contains("gzip", acceptEncoding);
    }

    [Fact]
    public async Task FetchHitsDocumentedUrls()
    {
        var seen = new List<string>();
        using var http = new HttpClient(new FakeHttp(req =>
        {
            seen.Add(req.RequestUri!.ToString());
            return FakeHttp.Json("[]");
        }));
        await BttvEmotes.FetchGlobalAsync(http, default);
        await BttvEmotes.FetchChannelAsync(http, "71092938", default);
        Assert.Equal(["https://api.betterttv.net/3/cached/emotes/global",
                      "https://api.betterttv.net/3/cached/users/twitch/71092938"], seen);
    }
}
