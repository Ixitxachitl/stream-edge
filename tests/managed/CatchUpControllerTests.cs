// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Playback;
using Xunit;

namespace StreamEdge.Tests;

public class CatchUpControllerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 20, 0, 0, TimeSpan.Zero);

    private static CatchUpController Controller(int maxDowngrade = 3)
    {
        var c = new CatchUpController(new CatchUpOptions()) { MaxDowngrade = maxDowngrade };
        c.Reset(T0 - TimeSpan.FromMinutes(1));
        return c;
    }

    private static PlaybackSample At(double seconds, double buffer, bool stalled = false, int behind = 0, bool ad = false) =>
        new(T0 + TimeSpan.FromSeconds(seconds), buffer, stalled, behind, ad);

    [Fact]
    public void OnTargetPlaysAtNormalSpeed()
    {
        var d = Controller().Update(At(0, 1.0));
        Assert.Equal(1.0, d.Speed);
        Assert.Equal(CatchUpAction.None, d.Action);
    }

    [Fact]
    public void ModeratelyBehindSpeedsUpProportionally()
    {
        var c = Controller();
        var a = c.Update(At(0, 2.5)).Speed;
        var b = c.Update(At(1, 5.0)).Speed;
        Assert.InRange(a, 1.05, 1.1);
        Assert.InRange(b, 1.15, 1.25);
    }

    [Fact]
    public void KeepsSpeedingUntilBackOnTargetThenStops()
    {
        var c = Controller();
        Assert.True(c.Update(At(0, 2.5)).Speed > 1);
        // Below the start threshold but above target: hysteresis keeps going.
        Assert.True(c.Update(At(1, 1.5)).Speed > 1);
        Assert.Equal(1.0, c.Update(At(2, 0.9)).Speed);
        // And doesn't restart until past the start threshold again.
        Assert.Equal(1.0, c.Update(At(3, 1.8)).Speed);
    }

    [Fact]
    public void FarBehindJumpsToLiveOncePerCooldown()
    {
        var c = Controller();
        Assert.Equal(CatchUpAction.Resync, c.Update(At(0, 7)).Action);
        Assert.NotEqual(CatchUpAction.Resync, c.Update(At(2, 7)).Action);
        Assert.Equal(CatchUpAction.Resync, c.Update(At(10, 7)).Action);
    }

    [Fact]
    public void DownloaderFallingBehindAlsoResyncs() =>
        Assert.Equal(CatchUpAction.Resync, Controller().Update(At(0, 1, behind: 3)).Action);

    [Fact]
    public void RepeatedStallsDropQualityInsteadOfBuffering()
    {
        var c = Controller();
        Assert.Equal(CatchUpAction.None, c.Update(At(0, 0, stalled: true)).Action);
        c.Update(At(1, 0.5));
        var d = c.Update(At(10, 0, stalled: true));
        Assert.Equal(CatchUpAction.QualityDown, d.Action);
        Assert.Equal(1, c.QualityOffset);
    }

    [Fact]
    public void ASingleStallHoldsQuality()
    {
        var c = Controller();
        c.Update(At(0, 0, stalled: true));
        c.Update(At(1, 0.5));
        // Second stall falls outside the 45 s window.
        Assert.Equal(CatchUpAction.None, c.Update(At(60, 0, stalled: true)).Action);
        Assert.Equal(0, c.QualityOffset);
    }

    [Fact]
    public void StallsDuringAdsDontCount()
    {
        var c = Controller();
        c.Update(At(0, 0, stalled: true, ad: true));
        c.Update(At(1, 0.5, ad: true));
        Assert.Equal(CatchUpAction.None, c.Update(At(2, 0, stalled: true, ad: true)).Action);
    }

    [Fact]
    public void NeverDowngradesPastTheLowestVideoVariant()
    {
        var c = Controller(maxDowngrade: 0);
        c.Update(At(0, 0, stalled: true));
        c.Update(At(1, 0.5));
        Assert.Equal(CatchUpAction.None, c.Update(At(2, 0, stalled: true)).Action);
    }

    [Fact]
    public void StableAfterDowngradeStepsBackUp()
    {
        var c = Controller();
        c.Update(At(0, 0, stalled: true));
        c.Update(At(1, 0.5));
        c.Update(At(2, 0, stalled: true));
        Assert.Equal(1, c.QualityOffset);
        Assert.Equal(CatchUpAction.None, c.Update(At(60, 1)).Action);
        Assert.Equal(CatchUpAction.QualityUp, c.Update(At(2 + 181, 1)).Action);
        Assert.Equal(0, c.QualityOffset);
    }
}
