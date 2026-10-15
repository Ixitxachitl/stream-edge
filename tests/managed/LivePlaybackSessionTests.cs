// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using StreamEdge.Playback;
using Xunit;

namespace StreamEdge.Tests;

public class LivePlaybackSessionTests
{
    /// <summary>GQL token, usher master playlist, and per-variant media
    /// playlists whose segment bodies name the variant they came from.</summary>
    private sealed class FakeTwitch : HttpMessageHandler
    {
        private long _edge = 100;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            string body;
            if (uri.Host == "gql.twitch.tv")
                body = """{"data":{"streamPlaybackAccessToken":{"value":"{}","signature":"sig"}}}""";
            else if (uri.Host == "usher.ttvnw.net")
                body = """
                    #EXTM3U
                    #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="chunked",NAME="1080p60 (source)"
                    #EXT-X-STREAM-INF:BANDWIDTH=8000000,RESOLUTION=1920x1080,VIDEO="chunked",FRAME-RATE=60.000
                    https://video.example/src/index.m3u8
                    #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="720p60",NAME="720p60"
                    #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720,VIDEO="720p60",FRAME-RATE=60.000
                    https://video.example/720/index.m3u8
                    #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="audio_only",NAME="audio_only"
                    #EXT-X-STREAM-INF:BANDWIDTH=160000,VIDEO="audio_only"
                    https://video.example/audio/index.m3u8
                    """;
            else if (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal))
            {
                var edge = Interlocked.Increment(ref _edge);
                body = $"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:{edge}\n#EXTINF:2.000,live\nseg{edge}.ts\n";
            }
            else
            {
                // Pace segments a little, like a live edge does.
                await Task.Delay(20, ct);
                body = uri.AbsolutePath.Split('/')[1] + ";";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    /// <summary>Stands in for mpv: opens whatever URL it is given and
    /// collects the bytes; reports whatever stats the test sets.</summary>
    private sealed class FakePlayer : IVideoPlayer
    {
        private CancellationTokenSource? _reading;
        private readonly StringBuilder _received = new();
        public readonly List<Uri> Loads = [];
        public readonly List<double> Speeds = [];
        public VideoPlayerStats Stats = new(1, false, false);

        public string Received { get { lock (_received) return _received.ToString(); } }

        public void Load(Uri url)
        {
            Loads.Add(url);
            _reading?.Cancel();
            _reading = new CancellationTokenSource();
            var ct = _reading.Token;
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                await using var stream = await http.GetStreamAsync(url, ct);
                var buffer = new byte[256];
                int n;
                while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                    lock (_received) _received.Append(Encoding.UTF8.GetString(buffer, 0, n));
            }, ct);
        }

        public void Stop() => _reading?.Cancel();
        public void SetSpeed(double speed) => Speeds.Add(speed);
        public VideoPlayerStats GetStats() => Stats;
        public void ShowMessage(string text) { }
        public void SetVolume(int percent) { }
        public void SetMute(bool mute) { }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "timed out waiting");
    }

    [Fact]
    public async Task PlaysThroughTheLocalServerAndCatchesUp()
    {
        var player = new FakePlayer();
        await using var session = new LivePlaybackSession(new HttpClient(new FakeTwitch()), player);
        await session.StartAsync("someone", authToken: null);

        Assert.Equal(PlaybackState.Playing, session.Status.State);
        Assert.Equal("1080p60", session.Current?.Name);
        Assert.Equal(["1080p60", "720p60", "audio only"], session.Variants.Select(v => v.Name));
        Assert.Equal("127.0.0.1", player.Loads.Single().Host);

        // Bytes flow from the fake CDN, through the reader and local server, to the player.
        // Segment bodies name their variant folder: source is "src".
        await Until(() => player.Received.Contains("src;"));

        var now = DateTimeOffset.UtcNow;
        // A little behind: speeds up, no reload.
        player.Stats = new VideoPlayerStats(4, false, false);
        session.Tick(now);
        Assert.True(player.Speeds[^1] > 1.0);
        Assert.Single(player.Loads);

        // Far behind: reloads at the live edge.
        player.Stats = new VideoPlayerStats(9, false, false);
        session.Tick(now + TimeSpan.FromSeconds(30));
        Assert.Equal(2, player.Loads.Count);
    }

    [Fact]
    public async Task RepeatedStallsStepDownButNeverToAudioOnly()
    {
        var player = new FakePlayer();
        await using var session = new LivePlaybackSession(new HttpClient(new FakeTwitch()), player);
        await session.StartAsync("someone", authToken: null);
        var t = DateTimeOffset.UtcNow;

        void Stall(int at)
        {
            player.Stats = new VideoPlayerStats(0, true, false);
            session.Tick(t + TimeSpan.FromSeconds(at));
            player.Stats = new VideoPlayerStats(0.5, false, false);
            session.Tick(t + TimeSpan.FromSeconds(at + 1));
        }

        Stall(0);
        Stall(5);
        Assert.Equal("720p60", session.Current?.Name);

        Stall(10);
        Stall(15);
        Assert.Equal("720p60", session.Current?.Name);
    }

    [Fact]
    public async Task ManualQualityPickBecomesTheBaseline()
    {
        var player = new FakePlayer();
        await using var session = new LivePlaybackSession(new HttpClient(new FakeTwitch()), player);
        await session.StartAsync("someone", authToken: null);
        session.SelectQuality(1);
        Assert.Equal("720p60", session.Current?.Name);
        Assert.Equal(2, player.Loads.Count);
    }
}
