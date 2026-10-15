// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;

namespace StreamEdge.Playback;

/// <summary>The playlist URL stopped working (token expired, stream ended or
/// moved). The session re-resolves and starts again.</summary>
public sealed class PlaylistGoneException(HttpStatusCode status)
    : Exception($"Media playlist returned {(int)status}.")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Follows one Twitch media playlist from the live edge and writes
/// its MPEG-TS segments, in order, to an output stream.
///
/// Twitch's low-latency playlists list the next segments before they exist
/// (<c>#EXT-X-TWITCH-PREFETCH</c>); requesting one streams it while it is
/// still being encoded. Bytes are forwarded as they arrive rather than per
/// whole segment, which is what keeps this a couple of seconds behind the
/// broadcaster instead of three segments.</summary>
public sealed class LiveHlsReader(HttpClient http, Uri mediaPlaylist, bool skipAds = true)
{
    /// <summary>How long any one request may go without a response, or a
    /// segment without a byte. Prefetch segments arrive as they're encoded,
    /// so the body timeout is per read, not per segment.</summary>
    internal static TimeSpan StepTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Playlist fetches that fail in a row before giving up, so the
    /// session reconnects (with a fresh token) instead of retrying forever.</summary>
    private const int MaxPlaylistFailures = 4;

    private Uri _playlist = mediaPlaylist;
    private Uri? _switchTo;
    private DateTimeOffset? _feedPdt;
    private DateTimeOffset _feedBegan;
    private double _feedDuration;

    /// <summary>Complete segments on the playlist not yet fetched. Normally
    /// 0; growing means the download can't keep up with this quality.</summary>
    public int SegmentsBehind { get; private set; }

    /// <summary>Twitch is currently serving a stitched-in ad break.</summary>
    public bool InAdBreak { get; private set; }

    public long BytesWritten { get; private set; }

    /// <summary>Seconds of media fully delivered so far. Against wall-clock
    /// time this says whether the connection keeps up: about 1.0 at the live
    /// edge (Twitch can't send faster than it encodes), below that when the
    /// download is the bottleneck.</summary>
    public double MediaSecondsWritten { get; private set; }

    /// <summary>The stream is fragmented MP4 (it has an init segment) rather
    /// than MPEG-TS. Its qualities each have their own init segment, which
    /// can't be spliced into a running stream, so a quality change needs a
    /// reload instead of <see cref="SwitchTo"/>.</summary>
    public bool IsFragmentedMp4 { get; private set; }

    private Uri? _sentInit;

    /// <summary>The playlist being followed (changes after <see cref="SwitchTo"/>).</summary>
    public Uri Playlist => _playlist;

    public event Action<string>? Notice;

    /// <summary>Broadcast time of the media most recently written, estimated
    /// from <c>#EXT-X-PROGRAM-DATE-TIME</c>. Only as good as the two clocks.</summary>
    public DateTimeOffset? FeedWallClock(DateTimeOffset now) =>
        _feedPdt is { } pdt ? pdt + TimeSpan.FromSeconds(Math.Min((now - _feedBegan).TotalSeconds, _feedDuration)) : null;

    /// <summary>Changes quality without a reload: from the next segment on,
    /// the stream continues from another variant's playlist into the same
    /// output. Twitch's variants share segment numbers and timestamps and
    /// every segment starts on a keyframe, so the player just sees the
    /// resolution change, with no gap or rebuffer. Thread-safe.</summary>
    public void SwitchTo(Uri playlist) => Volatile.Write(ref _switchTo, playlist);

    public async Task RunAsync(Stream output, CancellationToken ct)
    {
        long next = -1;
        var failures = 0;
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref _switchTo, null) is { } target && target != _playlist)
                _playlist = target;

            HlsMediaPlaylist playlist;
            try
            {
                playlist = await FetchPlaylistAsync(ct).ConfigureAwait(false);
                failures = 0;
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // Brief network trouble: keep the connection to the player and
                // try again. Persistent trouble: give up so the session
                // reconnects from scratch.
                if (++failures >= MaxPlaylistFailures) throw;
                await Task.Delay(TimeSpan.FromSeconds(failures), ct).ConfigureAwait(false);
                continue;
            }

            if (playlist.Ended) return;
            var segments = playlist.Segments;
            if (segments.Count == 0)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            if (next < 0)
            {
                next = LiveEdge(segments);
            }
            else if (next < segments[0].Sequence || next > segments[^1].Sequence + 1)
            {
                // Fell off the back of the window (or a variant switch landed
                // on a playlist numbered differently): rejoin at the edge
                // rather than wait for, or replay, what isn't there.
                next = LiveEdge(segments);
                Notice?.Invoke("Skipped ahead to live");
            }

            var pending = segments.Where(s => s.Sequence >= next).ToList();
            SegmentsBehind = Math.Max(0, pending.Count(s => !s.IsPrefetch) - 1);
            if (pending.Count == 0)
            {
                // Caught up with everything announced; the playlist refreshes
                // about once per segment.
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(playlist.TargetDuration / 4, 0.25, 1)), ct)
                    .ConfigureAwait(false);
                continue;
            }

            var segment = pending[0];
            var wasAd = InAdBreak;
            InAdBreak = segment.IsAd;
            if (segment.IsAd && skipAds)
            {
                if (!wasAd) Notice?.Invoke("Ad break: waiting for the stream");
                next = segment.Sequence + 1;
                continue;
            }
            if (wasAd && !segment.IsAd) Notice?.Invoke("Back to the stream");

            // fMP4 needs its init segment ahead of the first media segment
            // (and again if Twitch changes it, as it can around ads).
            IsFragmentedMp4 = playlist.IsFragmentedMp4;
            if (playlist.InitSegment is { } init && init != _sentInit)
            {
                await CopySegmentAsync(new HlsSegment(-1, init, 0, null, false, false), output, buffer, ct).ConfigureAwait(false);
                _sentInit = init;
            }

            await CopySegmentAsync(segment, output, buffer, ct).ConfigureAwait(false);
            next = segment.Sequence + 1;
            MediaSecondsWritten += segment.Duration;
            // Re-read the playlist after every segment: by now it lists new
            // prefetch entries, and fetching the next one late costs latency.
        }
    }

    /// <summary>Where to join: the newest complete segment, so playback
    /// starts on a keyframe right away, followed by the prefetch ones.</summary>
    internal static long LiveEdge(IReadOnlyList<HlsSegment> segments)
    {
        for (var i = segments.Count - 1; i >= 0; i--)
            if (!segments[i].IsPrefetch && !segments[i].IsAd) return segments[i].Sequence;
        return segments[^1].Sequence;
    }

    private async Task<HlsMediaPlaylist> FetchPlaylistAsync(CancellationToken ct)
    {
        using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
        step.CancelAfter(StepTimeout);
        using var response = await http.GetAsync(_playlist, step.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
            throw new PlaylistGoneException(response.StatusCode);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(step.Token).ConfigureAwait(false);
        return HlsPlaylist.ParseMedia(text, _playlist);
    }

    private async Task CopySegmentAsync(HlsSegment segment, Stream output, byte[] buffer, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                step.CancelAfter(StepTimeout);
                using var response = await http.GetAsync(segment.Uri, HttpCompletionOption.ResponseHeadersRead, step.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // A missing segment isn't worth stalling for; the next one
                    // starts on a keyframe.
                    if (attempt == 1) Notice?.Invoke($"Skipped a segment ({(int)response.StatusCode})");
                    continue;
                }
                _feedPdt = segment.ProgramDateTime;
                _feedBegan = DateTimeOffset.UtcNow;
                _feedDuration = segment.Duration;
                await using var body = await response.Content.ReadAsStreamAsync(step.Token).ConfigureAwait(false);
                while (true)
                {
                    // HttpClient's own timeout stops at the headers; a body
                    // that stops arriving would otherwise hang here for good.
                    step.CancelAfter(StepTimeout);
                    var n = await body.ReadAsync(buffer, step.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                    BytesWritten += n;
                }
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
            {
                // One retry for a dropped or stalled connection, then move on:
                // the next segment starts on a keyframe.
                if (attempt == 1) Notice?.Invoke("Skipped a segment: " + (ex is OperationCanceledException ? "timed out" : ex.Message));
            }
        }
    }
}
