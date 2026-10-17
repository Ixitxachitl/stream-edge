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
        Assert.Equal(1.0, Hold(c, 1, 20, 6.0).Speed);
        // Half a minute on, catching up is fine again (above the threshold
        // the stall's cushion raised by a second).
        Assert.Equal(1.08, Hold(c, 21, 15, 6.0).Speed);
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

    /// <summary>Three stalls from <paramref name="at"/>: one step down.</summary>
    private static void DropOnce(CatchUpController c, double at)
    {
        Stall(c, at);
        Stall(c, at + 5);
        c.Update(At(at + 10, 0, stalled: true));
        c.Update(At(at + 11, 2));
    }

    [Fact]
    public void AStepUpThatDropsBackDoublesTheNextWait()
    {
        var c = Controller();
        DropOnce(c, 0);
        Assert.Equal(1, c.QualityOffset);
        Assert.Equal(TimeSpan.FromMinutes(3), c.UpgradeWait);

        // Up after 3 stall-free minutes, down again within 2: that try failed.
        Assert.Equal(CatchUpAction.QualityUp, c.Update(At(10 + 181, 2)).Action);
        DropOnce(c, 200);
        Assert.Equal(1, c.QualityOffset);
        Assert.Equal(1, c.FailedUpgrades);
        Assert.Equal(TimeSpan.FromMinutes(6), c.UpgradeWait);

        // So 3 minutes isn't enough any more; 6 is.
        Assert.Equal(CatchUpAction.None, c.Update(At(210 + 181, 2)).Action);
        Assert.Equal(CatchUpAction.QualityUp, c.Update(At(210 + 361, 2)).Action);
    }

    [Fact]
    public void BackoffStopsAtTwentyFourMinutes()
    {
        var c = Controller();
        var now = T0;
        c.SetQualityOffset(1, now);
        for (var i = 0; i < 6; i++)
        {
            now += TimeSpan.FromMinutes(30);
            c.SetQualityOffset(0, now);
            c.SetQualityOffset(1, now + TimeSpan.FromSeconds(30));
        }
        Assert.Equal(6, c.FailedUpgrades);
        Assert.Equal(TimeSpan.FromMinutes(24), c.UpgradeWait);
    }

    [Fact]
    public void AStepUpThatHoldsForgivesEarlierFailures()
    {
        var c = Controller();
        c.SetQualityOffset(1, T0);
        c.SetQualityOffset(0, T0 + TimeSpan.FromMinutes(5));
        c.SetQualityOffset(1, T0 + TimeSpan.FromMinutes(5.5));
        Assert.Equal(1, c.FailedUpgrades);

        c.SetQualityOffset(0, T0 + TimeSpan.FromMinutes(20));
        // Steady well past the failure window.
        Hold(c, 20 * 60 + 1, 150, 2);
        Assert.Equal(0, c.FailedUpgrades);
    }

    [Fact]
    public void ProbingWaitsForCalmAndBacksOffToo()
    {
        var c = Controller();
        Assert.False(c.MayProbe(T0));               // at the preferred quality: nothing to probe
        c.SetQualityOffset(1, T0);
        Assert.False(c.MayProbe(T0 + TimeSpan.FromSeconds(10)));
        Assert.True(c.MayProbe(T0 + TimeSpan.FromSeconds(21)));

        // A failed step up doubles the calm needed before the next probe.
        c.SetQualityOffset(0, T0 + TimeSpan.FromSeconds(30));
        c.SetQualityOffset(1, T0 + TimeSpan.FromSeconds(60));
        Assert.False(c.MayProbe(T0 + TimeSpan.FromSeconds(60 + 21)));
        Assert.True(c.MayProbe(T0 + TimeSpan.FromSeconds(60 + 41)));
    }

    [Fact]
    public void ANewChannelForgetsTheBackoff()
    {
        var c = Controller();
        c.SetQualityOffset(1, T0);
        c.SetQualityOffset(0, T0 + TimeSpan.FromMinutes(1));
        c.SetQualityOffset(1, T0 + TimeSpan.FromMinutes(1.5));
        Assert.Equal(1, c.FailedUpgrades);
        c.Reset(T0 + TimeSpan.FromMinutes(2), keepQuality: false);
        Assert.Equal(0, c.FailedUpgrades);
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

    [Fact]
    public void StallsBuildASmallCushionThatCatchUpLeavesAlone()
    {
        var c = Controller();
        Assert.Equal(1.0, c.ResumeBuffer);
        c.Update(At(0, 0, stalled: true));
        c.Update(At(1, 2));
        Assert.Equal(1.0, c.Cushion);
        c.Update(At(2, 0, stalled: true));
        c.Update(At(3, 2));
        c.Update(At(4, 0, stalled: true));
        Assert.Equal(2.0, c.Cushion);
        Assert.Equal(3.0, c.ResumeBuffer);
        // 5 s buffered would normally be played off faster; with the cushion
        // it's where the buffer is meant to sit.
        Assert.Equal(1.0, Hold(c, 40, 20, 5.0).Speed);
    }

    [Fact]
    public void TheCushionWearsOffAfterQuietMinutes()
    {
        var c = Controller();
        c.Update(At(0, 0, stalled: true));
        Hold(c, 1, 119, 2);
        Assert.Equal(1.0, c.Cushion);
        Hold(c, 120, 2, 2);
        Assert.Equal(0, c.Cushion);
    }

    [Fact]
    public void ABandwidthDropAddsCushionAndANewChannelClearsIt()
    {
        var c = Controller();
        Assert.True(c.SetQualityOffset(2, T0));
        Assert.Equal(1.0, c.Cushion);
        c.Reset(T0, keepQuality: true);
        Assert.Equal(1.0, c.Cushion);
        c.Reset(T0, keepQuality: false);
        Assert.Equal(0, c.Cushion);
    }
}
