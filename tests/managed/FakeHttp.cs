// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;

namespace StreamEdge.Tests;

/// <summary>HttpMessageHandler that answers from a function: no network in tests.</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Optional gate so tests can hold requests in flight.</summary>
    public Task? Gate { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        if (Gate is not null) await Gate.WaitAsync(ct).ConfigureAwait(false);
        return respond(request);
    }

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode code) =>
        new(code) { Content = new StringContent("not_found") };
}
