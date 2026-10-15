// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Playback;
using Xunit;

namespace StreamEdge.Tests;

public class CatchUpControllerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    /// <summary>A controller whose load was a minute ago, so the startup
    /// grace has passed.</summary>
    private static CatchUpController Controller(int maxDowngrade = 3)
    {
        var c = new CatchUpController(new CatchUpOptions()) { MaxDowngrade = maxDowngrade };
        c.Reset(T0 - TimeSpan.FromMinutes(1));
        return c;
    }

    private static PlaybackSample At(double seconds, double buffer, bool stalled = false, int behind = 0, bool ad = false) =>
        new(T0 + TimeSpan.FromSeconds(seconds), buffer, stalled, behind, ad);

    /// <summary>Feeds the same reading at 4 Hz for a while; returns the last decision.</summary>
    private static CatchUpDecision Hold(CatchUpController c, double from, double seconds, double buffer)
    {
        CatchUpDecision last = default;
        for (var t = from; t < from + seconds; t += 0.25) last = c.Update(At(t, buffer));
        return last;
    }

    [Fact]
    public void OnTargetPlaysAtNormalSpeed() =>
        Assert.Equal(1.0, Controller().Update(At(0, 2.0)).Speed);

    [Fact]
    public void TheNormalSegmentSwingNeverChangesSpeed()
    {
        // Segments arrive in bursts: the buffer saws between ~0.5 and ~3.5 s.
        // That's the cause of the old stutter; it must not move the speed.
        var c = Controller();
        for (var t = 0.0; t < 60; t += 0.25)
        {
            var phase = t % 2;
            var d = c.Update(At(t, phase < 1 ? 3.5 - phase * 3 : 0.5 + (phase - 1) * 3));
            Assert.Equal(1.0, d.Speed);
            Assert.Equal(CatchUpAction.None, d.Action);
        }
    }

    [Fact]
    public void ClearlyBehindSpeedsUpGentlyAndMoreWhenWellBehind()
    {
        Assert.Equal(1.08, Hold(Controller(), 0, 10, 5.0).Speed);
        Assert.Equal(1.15, Hold(Controller(), 0, 10, 8.5).Speed);
    }

    [Fact]
    public void KeepsCatchingUpUntilNearTheTargetThenStops()
    {
        var c = Controller();
        Assert.Equal(1.08, Hold(c, 0, 10, 5.0).Speed);
        // Below the start threshold but still above target + 0.5: keep going.
        Assert.Equal(1.08, Hold(c, 10, 10, 3.0).Speed);
        Assert.Equal(1.0, Hold(c, 20, 10, 2.0).Speed);
        // And it doesn't restart until clearly behind again.
        Assert.Equal(1.0, Hold(c, 30, 10, 3.5).Speed);
    }

    [Fact]
    public void NoCatchingUpRightAfterAStall()
    {
        var c = Controller();
        c.Update(At(0, 0, stalled: true));
        // A cushion builds after the stall: keep it rather than spend it.
        Assert.Equal(1.0, Hold(c, 1, 20, 5.0).Speed);
        // Half a minute on, catching up is fine again.
        Assert.Equal(1.08, Hold(c, 21, 15, 5.0).Speed);
    }

    [Fact]
    public void FarBehindJumpsToLiveOncePerCooldown()
    {
        var c = Controller();
        Assert.Equal(CatchUpAction.Resync, c.Update(At(0, 12)).Action);
        Assert.NotEqual(CatchUpAction.Resync, c.Update(At(5, 12)).Action);
        Assert.Equal(CatchUpAction.Resync, c.Update(At(16, 12)).Action);
    }

    [Fact]
    public void DownloaderFallingBehindAlsoResyncs() =>
        Assert.Equal(CatchUpAction.Resync, Controller().Update(At(0, 2, behind: 4)).Action);

    private static void Stall(CatchUpController c, double at)
    {
        c.Update(At(at, 0, stalled: true));
        c.Update(At(at + 1, 1.5));
    }

    [Fact]
    public void ThreeStallsInAMinuteDropQuality()
    {
        var c = Controller();
        Stall(c, 0);
        Stall(c, 10);
        Assert.Equal(0, c.QualityOffset);
        c.Update(At(20, 0, stalled: true));
        Assert.Equal(1, c.QualityOffset);
    }

    [Fact]
    public void ScatteredStallsHoldQuality()
    {
        var c = Controller();
        Stall(c, 0);
        Stall(c, 40);
        Stall(c, 80);
        Assert.Equal(0, c.QualityOffset);
    }

    [Fact]
    public void StallsRightAfterALoadDontCount()
    {
        var c = new CatchUpController(new CatchUpOptions()) { MaxDowngrade = 3 };
        c.Reset(T0);
        Stall(c, 1);
        Stall(c, 4);
        Stall(c, 7);
        Assert.Equal(0, c.QualityOffset);
    }

    [Fact]
    public void StallsDuringAdsDontCount()
    {
        var c = Controller();
        for (var t = 0; t < 30; t += 5)
        {
            c.Update(At(t, 0, stalled: true, ad: true));
            c.Update(At(t + 1, 1, ad: true));
        }
        Assert.Equal(0, c.QualityOffset);
    }

    [Fact]
    public void NeverDowngradesPastTheLowestVideoVariant()
    {
        var c = Controller(maxDowngrade: 0);
        Stall(c, 0);
        Stall(c, 5);
        Stall(c, 10);
        Assert.Equal(0, c.QualityOffset);
    }

    [Fact]
    public void StableAfterDowngradeStepsBackUp()
    {
        var c = Controller();
        Stall(c, 0);
        Stall(c, 5);
        c.Update(At(10, 0, stalled: true));
        Assert.Equal(1, c.QualityOffset);
        Assert.Equal(CatchUpAction.None, c.Update(At(60, 2)).Action);
        Assert.Equal(CatchUpAction.QualityUp, c.Update(At(10 + 181, 2)).Action);
        Assert.Equal(0, c.QualityOffset);
    }
}
