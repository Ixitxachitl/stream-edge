// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using StreamEdge.Chat;
using Xunit;

namespace StreamEdge.Tests;

public class PronounServiceTests
{
    private const string List = """
        {"any":{"name":"any","subject":"Any","object":"Any","singular":true},
         "hehim":{"name":"hehim","subject":"He","object":"Him","singular":false},
         "sheher":{"name":"sheher","subject":"She","object":"Her","singular":false},
         "theythem":{"name":"theythem","subject":"They","object":"Them","singular":false}}
        """;

    private static HttpResponseMessage Respond(HttpRequestMessage req) => req.RequestUri!.AbsolutePath switch
    {
        "/v1/pronouns" => FakeHttp.Json(List),
        "/v1/users/alejo_47" => FakeHttp.Json("""{"channel_id":"64697598","channel_login":"alejo_47","pronoun_id":"hehim","alt_pronoun_id":null}"""),
        "/v1/users/ann" => FakeHttp.Json("""{"channel_id":"1","channel_login":"ann","pronoun_id":"sheher","alt_pronoun_id":"theythem"}"""),
        "/v1/users/anyone" => FakeHttp.Json("""{"channel_id":"2","channel_login":"anyone","pronoun_id":"any","alt_pronoun_id":null}"""),
        "/v1/users/broken" => FakeHttp.Status(HttpStatusCode.BadGateway),
        _ => FakeHttp.Status(HttpStatusCode.NotFound),
    };

    [Fact]
    public void ParsesList()
    {
        var list = PronounService.ParseList(List);
        Assert.Equal(4, list.Count);
        Assert.Equal(new Pronoun("hehim", "He", "Him", false), list["hehim"]);
    }

    [Fact]
    public void Formats()
    {
        var list = PronounService.ParseList(List);
        Assert.Equal("He/Him", PronounService.Format(list["hehim"]));
        Assert.Equal("Any", PronounService.Format(list["any"]));
        Assert.Equal("She/They", PronounService.Format(list["sheher"], list["theythem"]));
    }

    [Theory]
    [InlineData("alejo_47", "He/Him")]
    [InlineData("Ann", "She/They")]
    [InlineData("anyone", "Any")]
    [InlineData("nobody", null)]
    [InlineData("broken", null)]
    public async Task GetsFormattedPronouns(string login, string? expected)
    {
        var service = new PronounService(new HttpClient(new FakeHttp(Respond)));
        Assert.Equal(expected, await service.GetAsync(login));
    }

    [Fact]
    public async Task CachesIncludingNegativeResults()
    {
        var fake = new FakeHttp(Respond);
        var service = new PronounService(new HttpClient(fake));
        Assert.Null(await service.GetAsync("nobody"));
        Assert.Equal("He/Him", await service.GetAsync("alejo_47"));
        var calls = fake.Calls; // list + two users
        Assert.Equal(3, calls);
        Assert.Null(await service.GetAsync("NOBODY"));
        Assert.Equal("He/Him", await service.GetAsync("alejo_47"));
        Assert.Equal(calls, fake.Calls);
        Assert.True(service.TryGetCached("nobody", out var none));
        Assert.Null(none);
        Assert.True(service.TryGetCached("alejo_47", out var he));
        Assert.Equal("He/Him", he);
        Assert.False(service.TryGetCached("stranger", out _));
    }

    [Fact]
    public async Task FailuresAreRetried()
    {
        var fake = new FakeHttp(Respond);
        var service = new PronounService(new HttpClient(fake));
        Assert.Null(await service.GetAsync("broken"));
        var calls = fake.Calls;
        Assert.Null(await service.GetAsync("broken"));
        Assert.Equal(calls + 1, fake.Calls);
        Assert.False(service.TryGetCached("broken", out _));
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneFetch()
    {
        var gate = new TaskCompletionSource();
        var fake = new FakeHttp(Respond) { Gate = gate.Task };
        var service = new PronounService(new HttpClient(fake));
        var all = Enumerable.Range(0, 10).Select(_ => service.GetAsync("ann")).ToList();
        gate.SetResult();
        Assert.All(await Task.WhenAll(all), p => Assert.Equal("She/They", p));
        Assert.Equal(2, fake.Calls); // the list once, the user once
    }

    [Fact]
    public async Task CancellingAWaitDoesNotPoisonTheCache()
    {
        var gate = new TaskCompletionSource();
        var service = new PronounService(new HttpClient(new FakeHttp(Respond) { Gate = gate.Task }));
        using var cts = new CancellationTokenSource();
        var first = service.GetAsync("ann", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = service.GetAsync("ann");
        gate.SetResult();
        Assert.Equal("She/They", await second);
    }
}
