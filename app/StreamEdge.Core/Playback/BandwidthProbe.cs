// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace StreamEdge.Playback;

/// <summary>A probe's answer: how long one segment of a variant took to
/// download, as a share of the time it plays for. 0.5 means it arrived in
/// half real time, alongside whatever else was downloading.</summary>
public sealed record ProbeResult(double LoadRatio, double SegmentSeconds, long Bytes);

/// <summary>Times one recent, finished segment of a variant to tell whether
/// the connection could carry it. The running stream can't say: a live edge
/// only ever arrives at real time, however fast the connection is, and its
/// low-latency segments trickle in as they're encoded.</summary>
public static class BandwidthProbe
{
    /// <summary>Null when it can't tell (no finished segment, an ad, an
    /// error); a timeout counts as far too slow.</summary>
    public static async Task<ProbeResult?> MeasureAsync(HttpClient http, Uri variantPlaylist, CancellationToken ct)
    {
        try
        {
            using var playlistResponse = await http.GetAsync(variantPlaylist, ct).ConfigureAwait(false);
            if (!playlistResponse.IsSuccessStatusCode) return null;
            var playlist = HlsPlaylist.ParseMedia(
                await playlistResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false), variantPlaylist);
            // A finished one: a prefetch segment arrives at encoding speed.
            var segment = playlist.Segments.LastOrDefault(s => !s.IsPrefetch && !s.IsAd && s.Duration > 0);
            if (segment is null) return null;

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Twice its length is far too slow already; no need to wait longer.
            limit.CancelAfter(TimeSpan.FromSeconds(segment.Duration * 2));
            var clock = Stopwatch.StartNew();
            long bytes = 0;
            try
            {
                using var response = await http.GetAsync(segment.Uri, HttpCompletionOption.ResponseHeadersRead, limit.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                await using var body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                int n;
                while ((n = await body.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0) bytes += n;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ProbeResult(2, segment.Duration, bytes);
            }
            return new ProbeResult(clock.Elapsed.TotalSeconds / segment.Duration, segment.Duration, bytes);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
