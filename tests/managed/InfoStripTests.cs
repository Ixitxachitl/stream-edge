// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Playback;
using Xunit;

namespace StreamEdge.Tests;

public class InfoStripTests
{
    [Fact]
    public void TitlesCannotOpenOverrideBlocks()
    {
        Assert.Equal("a \\{\\⁠b1\\} b", InfoStripAss.Escape(@"a {\b1} b"));
        Assert.Equal("two lines", InfoStripAss.Escape("two\nlines"));
    }

    [Fact]
    public void BuildsABandAndTwoLines()
    {
        var ass = InfoStripAss.Build(new InfoStrip("CometCrumb", "Speedruns", "Super Mario World", "57 viewers · 2.4h"),
            InfoStripAss.TwoLineTop);
        var events = ass.Split('\n');
        Assert.Equal(2, events.Length);
        Assert.Contains(@"\p1", events[0]);
        Assert.Contains("CometCrumb", events[1]);
        Assert.Contains("Speedruns", events[1]);
        Assert.Contains(@"\N", events[1]);
        Assert.Contains("57 viewers", events[1]);
        Assert.Contains("Super Mario World", events[1]);
    }

    [Fact]
    public void LeavesOutWhatIsUnknown()
    {
        var ass = InfoStripAss.TextEvent(new InfoStrip("CometCrumb", "", "", null));
        Assert.DoesNotContain("●", ass);
        Assert.DoesNotContain("·", ass);
        Assert.EndsWith(@"\N{\fs24\b0}", ass);
    }

    [Fact]
    public void TheBandGrowsWithTheText()
    {
        var strip = new InfoStrip("CometCrumb", "Speedruns", "", null);
        Assert.Contains(@"\pos(0,600)", InfoStripAss.Build(strip, 612));
        Assert.Contains(@"\pos(0,540)", InfoStripAss.Build(strip, 552));
        Assert.Contains(@"\pos(0,0)", InfoStripAss.Build(strip, -50));
    }
}
