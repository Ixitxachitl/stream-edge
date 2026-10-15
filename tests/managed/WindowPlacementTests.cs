// SPDX-License-Identifier: GPL-3.0-or-later
using Xunit;

namespace StreamEdge.Tests;

public class WindowPlacementTests
{
    // A 1920×1080 main monitor and a 1920×480 bar to its right.
    private static readonly (int, int, int, int)[] Screens = [(0, 0, 1920, 1040), (1920, 0, 1920, 480)];

    [Fact]
    public void OnTheBarDisplayIsVisible() =>
        Assert.True(new WindowPlacement(1920, 0, 1920, 480).IsVisibleOn(Screens, 1));

    [Fact]
    public void AfterTheBarIsUnpluggedItIsNot() =>
        Assert.False(new WindowPlacement(1920, 0, 1920, 480).IsVisibleOn([(0, 0, 1920, 1040)], 1));

    [Fact]
    public void ASliverOnScreenIsNotEnoughToGrab() =>
        Assert.False(new WindowPlacement(1900, 1020, 800, 400).IsVisibleOn([(0, 0, 1920, 1040)], 1));

    [Fact]
    public void ScalingAppliesToTheSize() =>
        // 60×40 DIPs is 120×80 px at 200%: enough once scaled, not before.
        Assert.True(new WindowPlacement(0, 0, 60, 40).IsVisibleOn([(0, 0, 1920, 1040)], 2));
}
