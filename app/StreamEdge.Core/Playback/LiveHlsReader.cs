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
    private DateTimeOffset? _feedPdt;
    private DateTimeOffset _feedBegan;
    private double _feedDuration;

    /// <summary>Complete segments on the playlist not yet fetched. Normally
    /// 0; growing means the download can't keep up with this quality.</summary>
    public int SegmentsBehind { get; private set; }

    /// <summary>Twitch is currently serving a stitched-in ad break.</summary>
    public bool InAdBreak { get; private set; }

    public long BytesWritten { get; private set; }

    public event Action<string>? Notice;

    /// <summary>Broadcast time of the media most recently written, estimated
    /// from <c>#EXT-X-PROGRAM-DATE-TIME</c>. Only as good as the two clocks.</summary>
    public DateTimeOffset? FeedWallClock(DateTimeOffset now) =>
        _feedPdt is { } pdt ? pdt + TimeSpan.FromSeconds(Math.Min((now - _feedBegan).TotalSeconds, _feedDuration)) : null;

    public async Task RunAsync(Stream output, CancellationToken ct)
    {
        long next = -1;
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var playlist = await FetchPlaylistAsync(ct).ConfigureAwait(false);
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
            else if (next < segments[0].Sequence)
            {
                // Fell off the back of the playlist window: what we wanted is
                // gone, so rejoin at the edge rather than at the oldest.
                next = LiveEdge(segments);
                Notice?.Invoke("Skipped ahead: fell behind the playlist");
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

            await CopySegmentAsync(segment, output, buffer, ct).ConfigureAwait(false);
            next = segment.Sequence + 1;
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
        using var response = await http.GetAsync(mediaPlaylist, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
            throw new PlaylistGoneException(response.StatusCode);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return HlsPlaylist.ParseMedia(text, mediaPlaylist);
    }

    private async Task CopySegmentAsync(HlsSegment segment, Stream output, byte[] buffer, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(segment.Uri, HttpCompletionOption.ResponseHeadersRead, ct)
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
                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                int n;
                while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                    BytesWritten += n;
                }
                return;
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                // One retry for a dropped connection.
            }
            catch (HttpRequestException ex)
            {
                Notice?.Invoke("Skipped a segment: " + ex.Message);
            }
        }
    }
}
