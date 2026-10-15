// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace StreamEdge.Playback;

/// <summary>One rendition in a Twitch master playlist.</summary>
public sealed record HlsVariant(string Name, Uri Uri, long Bandwidth, int Height, double FrameRate, bool IsSource, bool IsAudioOnly)
{
    public override string ToString() => Name;
}

/// <summary>One media segment. Prefetch segments are the ones Twitch's
/// low-latency playlists announce before they're finished
/// (<c>#EXT-X-TWITCH-PREFETCH</c>); fetching them streams the segment while
/// it is still being encoded, which is where most of the latency win is.</summary>
public sealed record HlsSegment(long Sequence, Uri Uri, double Duration, DateTimeOffset? ProgramDateTime, bool IsPrefetch, bool IsAd);

public sealed record HlsMediaPlaylist(long MediaSequence, double TargetDuration, IReadOnlyList<HlsSegment> Segments, bool Ended);

public static class HlsPlaylist
{
    /// <summary>Variants, best first, audio-only last.</summary>
    public static IReadOnlyList<HlsVariant> ParseMaster(string text, Uri baseUri)
    {
        // #EXT-X-MEDIA carries the display name ("720p60", "1080p60 (source)")
        // keyed by GROUP-ID; #EXT-X-STREAM-INF points at it with VIDEO=.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var variants = new List<HlsVariant>();
        Dictionary<string, string>? pending = null;

        foreach (var raw in Lines(text))
        {
            if (raw.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var a = Attributes(raw["#EXT-X-MEDIA:".Length..]);
                if (a.TryGetValue("GROUP-ID", out var group) && a.TryGetValue("NAME", out var name))
                    names[group] = name;
            }
            else if (raw.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                pending = Attributes(raw["#EXT-X-STREAM-INF:".Length..]);
            }
            else if (!raw.StartsWith('#') && pending is not null)
            {
                var group = pending.GetValueOrDefault("VIDEO") ?? "";
                var name = names.GetValueOrDefault(group) ?? group;
                var height = 0;
                if (pending.TryGetValue("RESOLUTION", out var res) && res.Split('x') is [_, var h])
                    int.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
                double.TryParse(pending.GetValueOrDefault("FRAME-RATE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps);
                long.TryParse(pending.GetValueOrDefault("BANDWIDTH"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bw);
                var audioOnly = group == "audio_only" || name == "audio_only";
                variants.Add(new HlsVariant(
                    audioOnly ? "audio only" : name.Replace(" (source)", "", StringComparison.Ordinal),
                    new Uri(baseUri, raw), bw, height, fps,
                    IsSource: group == "chunked" || name.Contains("(source)", StringComparison.Ordinal),
                    IsAudioOnly: audioOnly));
                pending = null;
            }
        }

        return [.. variants
            .OrderBy(v => v.IsAudioOnly)
            .ThenByDescending(v => v.IsSource)
            .ThenByDescending(v => v.Height)
            .ThenByDescending(v => v.FrameRate)
            .ThenByDescending(v => v.Bandwidth)];
    }

    /// <summary>Index of the variant to start on: the named one if present
    /// (matched loosely: "720p" finds "720p60"), else the best.</summary>
    public static int Pick(IReadOnlyList<HlsVariant> variants, string? preferred)
    {
        if (variants.Count == 0) return -1;
        if (string.IsNullOrWhiteSpace(preferred) || preferred.Equals("source", StringComparison.OrdinalIgnoreCase))
            return 0;
        var exact = variants.ToList().FindIndex(v => v.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return exact;
        var prefix = variants.ToList().FindIndex(v => v.Name.StartsWith(preferred, StringComparison.OrdinalIgnoreCase));
        return prefix >= 0 ? prefix : 0;
    }

    public static HlsMediaPlaylist ParseMedia(string text, Uri baseUri)
    {
        long mediaSequence = 0;
        double target = 2;
        var ended = false;
        var segments = new List<HlsSegment>();
        double? duration = null;
        string? title = null;
        DateTimeOffset? pdt = null;
        DateTimeOffset? nextPdt = null;
        var adRangeOpen = false;

        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out mediaSequence);
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
                double.TryParse(line["#EXT-X-TARGETDURATION:".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out target);
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
                ended = true;
            else if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.Ordinal)
                     && DateTimeOffset.TryParse(line["#EXT-X-PROGRAM-DATE-TIME:".Length..], CultureInfo.InvariantCulture,
                         DateTimeStyles.AssumeUniversal, out var parsed))
                pdt = parsed;
            else if (line.StartsWith("#EXT-X-DATERANGE:", StringComparison.Ordinal))
            {
                // Twitch marks server-side ad insertion with this class.
                if (line.Contains("twitch-stitched-ad", StringComparison.Ordinal)) adRangeOpen = true;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var body = line["#EXTINF:".Length..];
                var comma = body.IndexOf(',');
                double.TryParse(comma < 0 ? body : body[..comma], NumberStyles.Float, CultureInfo.InvariantCulture, out var d);
                duration = d;
                title = comma < 0 ? null : body[(comma + 1)..].Trim();
            }
            else if (line.StartsWith("#EXT-X-TWITCH-PREFETCH:", StringComparison.Ordinal))
            {
                var last = segments.Count > 0 ? segments[^1] : null;
                var d = last?.Duration ?? target;
                var start = nextPdt;
                segments.Add(new HlsSegment(mediaSequence + segments.Count,
                    new Uri(baseUri, line["#EXT-X-TWITCH-PREFETCH:".Length..]), d, start,
                    IsPrefetch: true, IsAd: last?.IsAd ?? false));
                nextPdt = start + TimeSpan.FromSeconds(d);
            }
            else if (!line.StartsWith('#'))
            {
                var d = duration ?? target;
                var start = pdt ?? nextPdt;
                // Live segments are titled "live"; ads carry other titles
                // ("Amazon|…"). Untitled segments count as live.
                var isAd = adRangeOpen || (!string.IsNullOrEmpty(title) && title != "live");
                segments.Add(new HlsSegment(mediaSequence + segments.Count, new Uri(baseUri, line), d, start,
                    IsPrefetch: false, IsAd: isAd));
                nextPdt = start + TimeSpan.FromSeconds(d);
                duration = null;
                title = null;
                pdt = null;
                adRangeOpen = false;
            }
        }
        return new HlsMediaPlaylist(mediaSequence, target, segments, ended);
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    /// <summary>HLS attribute list: KEY=VALUE,KEY="quoted, with commas".</summary>
    internal static Dictionary<string, string> Attributes(string list)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < list.Length)
        {
            var eq = list.IndexOf('=', i);
            if (eq < 0) break;
            var key = list[i..eq].Trim();
            i = eq + 1;
            string value;
            if (i < list.Length && list[i] == '"')
            {
                var close = list.IndexOf('"', i + 1);
                if (close < 0) close = list.Length;
                value = list[(i + 1)..close];
                i = close + 1;
            }
            else
            {
                var comma = list.IndexOf(',', i);
                if (comma < 0) comma = list.Length;
                value = list[i..comma];
                i = comma;
            }
            result[key] = value;
            if (i < list.Length && list[i] == ',') i++;
        }
        return result;
    }
}
