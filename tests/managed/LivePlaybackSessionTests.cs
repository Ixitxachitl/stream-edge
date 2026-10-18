// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using StreamEdge.Playback;
using Xunit;

namespace StreamEdge.Tests;

public sealed class LivePlaybackSessionTests : IDisposable
{
    // These tests move the clock by hand, far faster than bytes arrive, which
    // the no-progress watchdog and the bandwidth rule would rightly call a
    // stuck or slow stream. Both off by default here; their own tests turn
    // them back on.
    public LivePlaybackSessionTests()
    {
        LivePlaybackSession.NoProgressTimeout = TimeSpan.FromHours(1);
        LivePlaybackSession.NoPlaybackTimeout = TimeSpan.FromHours(1);
        LivePlaybackSession.DeliveryWindow = TimeSpan.FromHours(1);
        // Probes would step quality straight back up on the instant fake CDN.
        LivePlaybackSession.ProbeInterval = TimeSpan.FromHours(1);
    }

    public void Dispose()
    {
        LivePlaybackSession.NoProgressTimeout = TimeSpan.FromSeconds(12);
        LivePlaybackSession.NoPlaybackTimeout = TimeSpan.FromSeconds(20);
        LivePlaybackSession.DeliveryWindow = TimeSpan.FromSeconds(8);
        LivePlaybackSession.ProbeInterval = TimeSpan.FromSeconds(30);
    }

    /// <summary>GQL token, usher master playlist, and per-variant media
    /// playlists whose segment bodies name the variant they came from.</summary>
    private sealed class FakeTwitch : HttpMessageHandler
    {
        private long _edge = 100;

        /// <summary>Segment requests never answer, like a dead CDN edge.</summary>
        public bool StallSegments;

        /// <summary>Milliseconds each (2 s) segment takes to arrive.</summary>
        public int SegmentDelayMs = 20;

        /// <summary>This many token requests time out first.</summary>
        public int TokenTimeouts;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            string body;
            if (uri.Host == "gql.twitch.tv" && Interlocked.Decrement(ref TokenTimeouts) >= 0)
                // What HttpClient throws when its Timeout passes.
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
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
                // A window of six segments, like Twitch's, so a reader a
                // segment or two behind the edge is still on it.
                var edge = Interlocked.Increment(ref _edge);
                var sb = new StringBuilder($"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:{edge - 5}\n");
                for (var s = edge - 5; s <= edge; s++) sb.Append($"#EXTINF:2.000,live\nseg{s}.ts\n");
                body = sb.ToString();
            }
            else
            {
                // Pace segments a little, like a live edge does.
                await Task.Delay(StallSegments ? Timeout.Infinite : SegmentDelayMs, ct);
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

        public bool Stopped;

        public void Stop()
        {
            Stopped = true;
            _reading?.Cancel();
        }
        public void SetSpeed(double speed) => Speeds.Add(speed);
        public VideoPlayerStats GetStats() => Stats;
        public double ResumeBuffer;
        public void SetResumeBuffer(double seconds) => ResumeBuffer = seconds;
        public void ShowMessage(string text) { }
        public void SetVolume(int percent) { }
        public void SetMute(bool mute) { }
        public void SetStatusText(string? text) { }
        public void SetInfoStrip(InfoStrip? strip) { }
        public bool CaptionsOn;
        public void SetCaptions(bool on) => CaptionsOn = on;
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
        // Clearly behind: speeds up, no reload.
        player.Stats = new VideoPlayerStats(6, false, false);
        session.Tick(now);
        Assert.True(player.Speeds[^1] > 1.0);
        Assert.Single(player.Loads);

        // Far behind: reloads at the live edge (16 s on: past the resync
        // cooldown).
        player.Stats = new VideoPlayerStats(12, false, false);
        session.Tick(now + TimeSpan.FromSeconds(16));
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

        // Three stalls in a minute, after the startup grace.
        Stall(11);
        Stall(16);
        Stall(21);
        Assert.Equal("720p60", session.Current?.Name);

        Stall(26);
        Stall(31);
        Stall(36);
        Assert.Equal("720p60", session.Current?.Name);
    }

    [Theory]
    [InlineData(20, "1080p60")]    // the source segment arrives in 1% of real time: step up
    [InlineData(3000, "720p60")]   // in 150%: it wouldn't fit, stay down
    public async Task AProbeStepsBackUpOnlyWhenTheHigherQualityFits(int segmentDelayMs, string expected)
    {
        LivePlaybackSession.ProbeInterval = TimeSpan.Zero;
        var twitch = new FakeTwitch();
        var player = new FakePlayer();
        await using var session = new LivePlaybackSession(new HttpClient(twitch), player);
        await session.StartAsync("someone", authToken: null);
        var t = DateTimeOffset.UtcNow;
        void Stall(int at)
        {
            player.Stats = new VideoPlayerStats(0, true, false);
            session.Tick(t + TimeSpan.FromSeconds(at));
            player.Stats = new VideoPlayerStats(2, false, false);
            session.Tick(t + TimeSpan.FromSeconds(at + 1));
        }
        Stall(11);
        Stall(16);
        Stall(21);
        Assert.Equal("720p60", session.Current?.Name);
        await Until(() => player.Received.Contains("720;"));

        // Calm for the probe's 20 s; it times a source segment.
        twitch.SegmentDelayMs = segmentDelayMs;
        session.Tick(t + TimeSpan.FromSeconds(45));
        for (var i = 0; i < 40 && session.Current?.Name == "720p60"; i++)
        {
            await Task.Delay(100);
            session.Tick(t + TimeSpan.FromSeconds(46 + i));
        }
        Assert.Equal(expected, session.Current?.Name);
    }

    private static async Task<(FakePlayer, LivePlaybackSession)> Playing(FakeTwitch? twitch = null)
    {
        var player = new FakePlayer();
        var session = new LivePlaybackSession(new HttpClient(twitch ?? new FakeTwitch()), player);
        await session.StartAsync("someone", authToken: null);
        // Wait for the reader to be running, as it is in normal playback.
        await Until(() => player.Received.Length > 0);
        return (player, session);
    }

    [Fact]
    public async Task VideoQualityChangesDontReload()
    {
        var (player, session) = await Playing();
        await using var _ = session;

        session.SelectQuality(1);

        Assert.Equal("720p60", session.Current?.Name);
        Assert.Single(player.Loads);
        // The same connection carries on from the 720p playlist.
        await Until(() => player.Received.Contains("720;"));
    }

    [Fact]
    public async Task VideoTiersSwitchInPlace()
    {
        var (player, session) = await Playing();
        await using var _ = session;

        session.Tier = PlaybackTier.Thumbnail;
        Assert.Equal("720p60", session.Current?.Name);
        await Until(() => player.Received.Contains("720;"));

        session.Tier = PlaybackTier.Full;
        Assert.Equal("1080p60", session.Current?.Name);
        Assert.Single(player.Loads);
    }

    [Fact]
    public async Task OffStopsEverythingAndComingBackReconnects()
    {
        var (player, session) = await Playing();
        await using var _ = session;

        session.Tier = PlaybackTier.Off;
        Assert.Equal(PlaybackState.Stopped, session.Status.State);
        Assert.Null(session.Current);
        // Still counts as live for the stream list, as it was a moment ago.
        Assert.True(session.LastKnownLive);
        Assert.True(player.Stopped);
        // Nothing restarts it while it's off, not even the retry timer.
        session.Tick(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5));
        Assert.Single(player.Loads);

        session.Tier = PlaybackTier.Thumbnail;
        await Until(() => player.Loads.Count == 2);
        Assert.Equal(PlaybackState.Playing, session.Status.State);
        Assert.Equal("720p60", session.Current?.Name);
    }

    [Fact]
    public async Task ATabOpenedOffWaitsUntilItsShown()
    {
        var player = new FakePlayer();
        await using var session = new LivePlaybackSession(new HttpClient(new FakeTwitch()), player) { Tier = PlaybackTier.Off };
        await session.StartAsync("someone", authToken: null);
        Assert.Empty(player.Loads);

        session.Tier = PlaybackTier.Full;
        await Until(() => player.Loads.Count == 1);
        Assert.Equal("1080p60", session.Current?.Name);
    }

    [Fact]
    public async Task AQualityPickedByHandSurvivesTimeOff()
    {
        var (player, session) = await Playing();
        await using var _ = session;
        session.SelectQuality(1);

        session.Tier = PlaybackTier.Off;
        session.Tier = PlaybackTier.Full;
        await Until(() => player.Loads.Count == 2);
        Assert.Equal("720p60", session.Current?.Name);
    }

    [Fact]
    public async Task ATokenTimeoutIsRetriedNotLeftStuck()
    {
        var player = new FakePlayer();
        await using var session = new LivePlaybackSession(new HttpClient(new FakeTwitch { TokenTimeouts = 1 }), player);
        await session.StartAsync("someone", authToken: null);
        Assert.Equal(PlaybackState.Error, session.Status.State);

        session.Tick(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5));
        await Until(() => session.Status.State == PlaybackState.Playing);
        Assert.Single(player.Loads);
    }

    [Fact]
    public async Task AStreamThatStopsDeliveringReconnects()
    {
        var saved = LivePlaybackSession.NoProgressTimeout;
        LivePlaybackSession.NoProgressTimeout = TimeSpan.FromSeconds(2);
        try
        {
            var player = new FakePlayer();
            await using var session = new LivePlaybackSession(new HttpClient(new FakeTwitch { StallSegments = true }), player);
            await session.StartAsync("someone", authToken: null);
            var t = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

            session.Tick(t);                              // past the load grace: watching
            session.Tick(t + TimeSpan.FromSeconds(3));    // no bytes for 3 s: reconnect
            Assert.Equal(PlaybackState.Error, session.Status.State);

            session.Tick(t + TimeSpan.FromSeconds(4));    // the retry: resolve and load again
            await Until(() => player.Loads.Count == 2);
        }
        finally
        {
            LivePlaybackSession.NoProgressTimeout = saved;
        }
    }

    [Fact]
    public async Task PlaybackThatFreezesWhileBytesStillArriveReconnects()
    {
        LivePlaybackSession.NoPlaybackTimeout = TimeSpan.FromSeconds(2);
        var (player, session) = await Playing();
        await using var _ = session;
        player.Stats = new VideoPlayerStats(3, Stalled: true, Idle: false, Position: 41.5);
        var t = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        session.Tick(t);                              // past the load grace: watching
        session.Tick(t + TimeSpan.FromSeconds(1));
        Assert.Equal(PlaybackState.Playing, session.Status.State);
        session.Tick(t + TimeSpan.FromSeconds(3));    // stuck at 41.5 s for 3 s: reconnect
        Assert.Equal(PlaybackState.Error, session.Status.State);

        session.Tick(t + TimeSpan.FromSeconds(4));    // the retry: resolve and load again
        await Until(() => player.Loads.Count == 2);
    }

    [Fact]
    public async Task AudioThatStaysOutOfSyncReloadsOnceAndBlipsAreIgnored()
    {
        var (player, session) = await Playing();
        await using var _ = session;
        var t = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        void At(int s, double avsync)
        {
            player.Stats = new VideoPlayerStats(2, Stalled: false, Idle: false, Position: 100 + s, AvSync: avsync);
            session.Tick(t + TimeSpan.FromSeconds(s));
        }

        // A blip: apart for 2 s, then back.
        At(0, 0.5); At(1, 0.5); At(2, 0.5); At(3, 0.01);
        Assert.Single(player.Loads);

        // Apart and staying apart (video behind: negative): reload after 4 s.
        for (var s = 4; s <= 8; s++) At(s, -0.6);
        Assert.Equal(2, player.Loads.Count);

        // Still apart after the reload: not again within the cooldown.
        for (var s = 9; s <= 30; s++) At(s, -0.6);
        Assert.Equal(2, player.Loads.Count);
        Assert.Equal(PlaybackState.Playing, session.Status.State);
    }

    [Fact]
    public async Task MovingPlaybackIsLeftAlone()
    {
        LivePlaybackSession.NoPlaybackTimeout = TimeSpan.FromSeconds(2);
        var (player, session) = await Playing();
        await using var _ = session;
        var t = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        for (var s = 0; s < 10; s++)
        {
            player.Stats = new VideoPlayerStats(2, Stalled: false, Idle: false, Position: 100 + s);
            session.Tick(t + TimeSpan.FromSeconds(s));
        }
        Assert.Equal(PlaybackState.Playing, session.Status.State);
        Assert.Single(player.Loads);
    }

    [Fact]
    public async Task ASlowConnectionGoesStraightToAQualityThatFits()
    {
        var saved = LivePlaybackSession.DeliveryWindow;
        LivePlaybackSession.DeliveryWindow = TimeSpan.FromSeconds(1);
        try
        {
            // 2 s segments arriving in 4 s: half real time.
            var player = new FakePlayer();
            await using var session = new LivePlaybackSession(new HttpClient(new FakeTwitch { SegmentDelayMs = 4000 }), player);
            await session.StartAsync("someone", authToken: null);
            for (var i = 0; i < 12 && session.Current?.Name == "1080p60"; i++)
            {
                await Task.Delay(250);
                session.Tick(DateTimeOffset.UtcNow);
            }
            Assert.Equal("720p60", session.Current?.Name);
            Assert.True(session.DeliveryRate is null or < 0.85);
        }
        finally
        {
            LivePlaybackSession.DeliveryWindow = saved;
        }
    }

    [Fact]
    public async Task CaptionsAreOnlyForTheFrontTier()
    {
        var (player, session) = await Playing();
        await using var _ = session;

        session.Captions = true;
        Assert.True(player.CaptionsOn);
        session.Tier = PlaybackTier.Thumbnail;
        Assert.False(player.CaptionsOn);
        session.Tier = PlaybackTier.Full;
        Assert.True(player.CaptionsOn);
    }
}
