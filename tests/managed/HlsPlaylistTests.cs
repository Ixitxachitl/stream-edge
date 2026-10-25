// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Playback;
using Xunit;

namespace StreamEdge.Tests;

public class HlsPlaylistTests
{
    private static readonly Uri Base = new("https://usher.example/api/channel/hls/x.m3u8");

    private const string Master = """
        #EXTM3U
        #EXT-X-TWITCH-INFO:NODE="video-edge"
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="chunked",NAME="1080p60 (source)",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=8000000,RESOLUTION=1920x1080,CODECS="avc1.64002A,mp4a.40.2",VIDEO="chunked",FRAME-RATE=60.000
        https://video.example/chunked.m3u8
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="480p30",NAME="480p",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=1400000,RESOLUTION=852x480,CODECS="avc1.4D401F,mp4a.40.2",VIDEO="480p30",FRAME-RATE=30.000
        https://video.example/480p30.m3u8
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="720p60",NAME="720p60",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=3400000,RESOLUTION=1280x720,CODECS="avc1.4D401F,mp4a.40.2",VIDEO="720p60",FRAME-RATE=60.000
        https://video.example/720p60.m3u8
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="audio_only",NAME="audio_only",AUTOSELECT=NO,DEFAULT=NO
        #EXT-X-STREAM-INF:BANDWIDTH=160000,CODECS="mp4a.40.2",VIDEO="audio_only"
        https://video.example/audio_only.m3u8
        """;

    [Fact]
    public void MasterVariantsAreBestFirstWithAudioOnlyLast()
    {
        var v = HlsPlaylist.ParseMaster(Master, Base);
        Assert.Equal(["1080p60", "720p60", "480p", "audio only"], v.Select(x => x.Name));
        Assert.True(v[0].IsSource);
        Assert.True(v[^1].IsAudioOnly);
        Assert.Equal(720, v[1].Height);
        Assert.Equal(60, v[1].FrameRate);
        Assert.Equal("https://video.example/720p60.m3u8", v[1].Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("source", 0)]
    [InlineData("720p60", 1)]
    [InlineData("720p", 1)]
    [InlineData("480p", 2)]
    [InlineData("4k", 0)]
    [InlineData("1440p60", 0)]
    [InlineData("900p60", 1)]
    [InlineData("720p30", 1)]
    [InlineData("360p", 2)]
    public void PickMatchesLoosely(string? preferred, int expected) =>
        Assert.Equal(expected, HlsPlaylist.Pick(HlsPlaylist.ParseMaster(Master, Base), preferred));

    private const string SourceAt720 = """
        #EXTM3U
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="chunked",NAME="720p60 (source)",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=5957031,RESOLUTION=1280x720,CODECS="avc1.640020,mp4a.40.2",VIDEO="chunked",FRAME-RATE=60.000
        https://video.example/chunked.m3u8
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="720p60",NAME="720p60",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=3422999,RESOLUTION=1280x720,CODECS="avc1.4D401F,mp4a.40.2",VIDEO="720p60",FRAME-RATE=60.000
        https://video.example/720p60.m3u8
        """;

    [Theory]
    [InlineData("source", 0)]
    [InlineData("720p60", 1)]
    public void ASourceAndATranscodeOfTheSameSizeStayApart(string preferred, int expected) =>
        Assert.Equal(expected, HlsPlaylist.Pick(HlsPlaylist.ParseMaster(SourceAt720, Base), preferred));

    private const string Media = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:6
        #EXT-X-MEDIA-SEQUENCE:100
        #EXT-X-PROGRAM-DATE-TIME:2026-10-14T20:00:00.000Z
        #EXTINF:2.000,live
        seg100.ts
        #EXT-X-PROGRAM-DATE-TIME:2026-10-14T20:00:02.000Z
        #EXTINF:2.000,live
        seg101.ts
        #EXT-X-TWITCH-PREFETCH:https://video.example/seg102.ts
        #EXT-X-TWITCH-PREFETCH:https://video.example/seg103.ts
        """;

    [Fact]
    public void MediaPlaylistNumbersSegmentsAndPrefetch()
    {
        var p = HlsPlaylist.ParseMedia(Media, new Uri("https://video.example/720p60.m3u8"));
        Assert.Equal(100, p.MediaSequence);
        Assert.Equal([100L, 101, 102, 103], p.Segments.Select(s => s.Sequence));
        Assert.Equal([false, false, true, true], p.Segments.Select(s => s.IsPrefetch));
        Assert.Equal("https://video.example/seg101.ts", p.Segments[1].Uri.AbsoluteUri);
        // Prefetch start times continue from the last dated segment.
        Assert.Equal(DateTimeOffset.Parse("2026-10-14T20:00:04Z"), p.Segments[2].ProgramDateTime);
        Assert.Equal(DateTimeOffset.Parse("2026-10-14T20:00:06Z"), p.Segments[3].ProgramDateTime);
        Assert.False(p.Ended);
    }

    [Fact]
    public void AdSegmentsAreMarked()
    {
        var p = HlsPlaylist.ParseMedia("""
            #EXTM3U
            #EXT-X-MEDIA-SEQUENCE:5
            #EXTINF:2.000,live
            a.ts
            #EXT-X-DATERANGE:ID="stitched-ad-1",CLASS="twitch-stitched-ad",START-DATE="2026-10-14T20:00:00Z"
            #EXTINF:2.000,Amazon|123
            b.ts
            #EXTINF:2.000,Amazon|123
            c.ts
            #EXTINF:2.000,live
            d.ts
            """, Base);
        Assert.Equal([false, true, true, false], p.Segments.Select(s => s.IsAd));
    }

    [Fact]
    public void LiveEdgeIsNewestCompleteSegment()
    {
        var p = HlsPlaylist.ParseMedia(Media, Base);
        Assert.Equal(101, LiveHlsReader.LiveEdge(p.Segments));
        // Further back for a buffer, but no further than the playlist goes.
        Assert.Equal(100, LiveHlsReader.LiveEdge(p.Segments, behind: 1));
        Assert.Equal(p.Segments[0].Sequence, LiveHlsReader.LiveEdge(p.Segments, behind: 50));
    }

    [Fact]
    public void QuotedAttributesKeepTheirCommas()
    {
        var a = HlsPlaylist.Attributes("""BANDWIDTH=1,CODECS="avc1,mp4a",VIDEO="x" """.Trim());
        Assert.Equal("avc1,mp4a", a["CODECS"]);
        Assert.Equal("x", a["VIDEO"]);
    }
}
