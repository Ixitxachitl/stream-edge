// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using StreamEdge.Playback;
using Xunit;

namespace StreamEdge.Tests;

public class LiveHlsReaderTests
{
    /// <summary>A Twitch stand-in: a media playlist that advances one
    /// segment per request, and segments whose body is their own name.</summary>
    private sealed class FakeTwitch : HttpMessageHandler
    {
        public long Edge = 10;
        public bool Gone;
        public bool AdAt12;
        /// <summary>Fragmented MP4: an init segment named in #EXT-X-MAP.</summary>
        public bool Mp4;
        public readonly List<string> Requests = [];

        /// <summary>The playlist jumps this far ahead after its second fetch,
        /// as after an outage longer than the playlist window.</summary>
        public long JumpAfterSecond;
        private int _fetches;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests) Requests.Add(path);
            if (path.EndsWith(".m3u8", StringComparison.Ordinal))
            {
                if (Gone) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                if (Interlocked.Increment(ref _fetches) == 3) Interlocked.Add(ref Edge, JumpAfterSecond);
                var edge = Interlocked.Increment(ref Edge) - 1;
                var sb = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:2\n");
                if (Mp4) sb.Append("#EXT-X-MAP:URI=\"init.mp4\"\n");
                sb.Append($"#EXT-X-MEDIA-SEQUENCE:{edge - 3}\n");
                for (var s = edge - 3; s <= edge; s++)
                {
                    var title = AdAt12 && s == 12 ? "Amazon|1" : "live";
                    sb.Append($"#EXTINF:2.000,{title}\nseg{s}.ts\n");
                }
                sb.Append($"#EXT-X-TWITCH-PREFETCH:seg{edge + 1}.ts\n");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString()) });
            }
            var name = path.TrimStart('/');
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(name + ";") });
        }
    }

    private static async Task<string> Run(FakeTwitch twitch, int bytes)
    {
        var reader = new LiveHlsReader(new HttpClient(twitch), new Uri("https://video.example/720p60.m3u8"));
        // A synchronised stream: the reader writes from the pool while this
        // thread polls its length.
        using var output = Stream.Synchronized(new MemoryStream());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // On the pool: with this fake everything completes synchronously, so
        // calling RunAsync directly would never return to the polling loop.
        var run = Task.Run(() => reader.RunAsync(output, cts.Token));
        while (output.Length < bytes && !run.IsCompleted) await Task.Delay(5);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        output.Position = 0;
        return new StreamReader(output).ReadToEnd();
    }

    [Fact]
    public async Task StartsAtTheLiveEdgeAndFollowsInOrderIncludingPrefetch()
    {
        var text = await Run(new FakeTwitch(), bytes: 40);
        // First playlist's newest complete segment is 10; older ones are skipped.
        Assert.StartsWith("seg10.ts;seg11.ts;seg12.ts;", text);
        Assert.DoesNotContain("seg9.ts", text);
    }

    [Fact]
    public async Task FragmentedMp4SendsItsInitSegmentFirstAndOnce()
    {
        var text = await Run(new FakeTwitch { Mp4 = true }, bytes: 60);
        Assert.StartsWith("init.mp4;seg10.ts;seg11.ts;", text);
        Assert.Equal(1, text.Split("init.mp4").Length - 1);
    }

    [Fact]
    public async Task PlaysAdSegmentsInOrder()
    {
        var text = await Run(new FakeTwitch { AdAt12 = true }, bytes: 40);
        Assert.Contains("seg11.ts;seg12.ts;seg13.ts;", text);
    }

    [Fact]
    public async Task FallingOffThePlaylistStopsRatherThanSplicingAJump()
    {
        var reader = new LiveHlsReader(new HttpClient(new FakeTwitch { JumpAfterSecond = 100 }), new Uri("https://video.example/x.m3u8"));
        using var output = Stream.Synchronized(new MemoryStream());
        await Assert.ThrowsAsync<FellBehindException>(() => reader.RunAsync(output, CancellationToken.None));
        output.Position = 0;
        // What came before the jump was written; nothing from after it.
        var text = new StreamReader(output).ReadToEnd();
        Assert.Equal("seg10.ts;seg11.ts;", text);
    }

    [Fact]
    public async Task ExpiredPlaylistSurfacesAsPlaylistGone()
    {
        var reader = new LiveHlsReader(new HttpClient(new FakeTwitch { Gone = true }), new Uri("https://video.example/x.m3u8"));
        await Assert.ThrowsAsync<PlaylistGoneException>(() => reader.RunAsync(Stream.Null, CancellationToken.None));
    }
}
