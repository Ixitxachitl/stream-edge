// SPDX-License-Identifier: GPL-3.0-or-later
using Xunit;

namespace StreamEdge.Tests;

public class PaneArrangementTests
{
    private static readonly PaneId[] All = [PaneId.Chat, PaneId.List, PaneId.Panel];

    /// <summary>The default: list left, chat right, the About panels below.</summary>
    private static Dictionary<PaneId, PanePlace> Default() => new()
    {
        [PaneId.Chat] = PaneArrangement.Default(PaneId.Chat, DockSide.Right),
        [PaneId.List] = PaneArrangement.Default(PaneId.List, DockSide.Left),
        [PaneId.Panel] = PaneArrangement.Default(PaneId.Panel, DockSide.Bottom),
    };

    private static List<List<PaneId>> Columns(IReadOnlyDictionary<PaneId, PanePlace> places, DockSide side, params PaneId[] shown) =>
        [.. PaneArrangement.Columns(side, places, shown.Length > 0 ? shown : All).Select(c => c.ToList())];

    [Fact]
    public void StackingUnderAPaneSharesItsColumn()
    {
        var places = PaneArrangement.MoveStacked(Default(), PaneId.List, PaneId.Chat, below: true);
        Assert.Equal([[PaneId.Chat, PaneId.List]], Columns(places, DockSide.Right));
        Assert.Empty(Columns(places, DockSide.Left));

        // And above it, the other way up.
        places = PaneArrangement.MoveStacked(places, PaneId.Panel, PaneId.Chat, below: false);
        Assert.Equal([[PaneId.Panel, PaneId.Chat, PaneId.List]], Columns(places, DockSide.Right));
    }

    [Fact]
    public void BesideAPaneIsAColumnOfItsOwnOnEitherSideOfIt()
    {
        var before = PaneArrangement.MoveBeside(Default(), PaneId.Panel, PaneId.Chat, after: false);
        Assert.Equal([[PaneId.Panel], [PaneId.Chat]], Columns(before, DockSide.Right));
        var after = PaneArrangement.MoveBeside(Default(), PaneId.Panel, PaneId.Chat, after: true);
        Assert.Equal([[PaneId.Chat], [PaneId.Panel]], Columns(after, DockSide.Right));

        // Beside a stacked pane: a new column next to its whole column.
        var stacked = PaneArrangement.MoveStacked(Default(), PaneId.List, PaneId.Chat, below: true);
        var beside = PaneArrangement.MoveBeside(stacked, PaneId.Panel, PaneId.List, after: true);
        Assert.Equal([[PaneId.Chat, PaneId.List], [PaneId.Panel]], Columns(beside, DockSide.Right));
    }

    [Fact]
    public void ToASideOfTheVideoIsNextToIt()
    {
        // Right of the video, next to it is first; left of it, last.
        var right = PaneArrangement.MoveToSide(Default(), PaneId.Panel, DockSide.Right);
        Assert.Equal([[PaneId.Panel], [PaneId.Chat]], Columns(right, DockSide.Right));
        var left = PaneArrangement.MoveToSide(Default(), PaneId.Chat, DockSide.Left);
        Assert.Equal([[PaneId.List], [PaneId.Chat]], Columns(left, DockSide.Left));
        Assert.Empty(Columns(left, DockSide.Right));
    }

    [Fact]
    public void AHiddenPaneKeepsItsPlaceAndTheFirstShownStartsAColumn()
    {
        var places = PaneArrangement.MoveStacked(Default(), PaneId.List, PaneId.Chat, below: true);
        // Chat hidden can't happen, but the list hidden leaves chat alone in its column.
        Assert.Equal([[PaneId.Chat]], Columns(places, DockSide.Right, PaneId.Chat, PaneId.Panel));
        // Moving another pane doesn't lose the hidden one.
        var moved = PaneArrangement.MoveToSide(places, PaneId.Panel, DockSide.Top);
        Assert.Equal(DockSide.Right, moved[PaneId.List].Side);
        Assert.True(moved[PaneId.List].Below);
    }

    [Fact]
    public void SizesSurviveMovesElsewhereButASideAPaneJoinsOrLeavesStartsEven()
    {
        // Chat and the list in two resized columns on the right.
        var places = PaneArrangement.MoveBeside(Default(), PaneId.List, PaneId.Chat, after: true);
        places = PaneArrangement.Resized(places, PaneId.Chat, columnWeight: 300);
        places = PaneArrangement.Resized(places, PaneId.List, columnWeight: 100);

        // The panel moving between the bottom and the top leaves them alone.
        var moved = PaneArrangement.MoveToSide(places, PaneId.Panel, DockSide.Top);
        Assert.Equal(300, moved[PaneId.Chat].ColumnWeight);
        Assert.Equal(100, moved[PaneId.List].ColumnWeight);

        // Joining the right side evens it out.
        var joined = PaneArrangement.MoveStacked(places, PaneId.Panel, PaneId.Chat, below: true);
        Assert.All(joined.Values, p => Assert.Equal(1, p.ColumnWeight));
        Assert.All(joined.Values, p => Assert.Equal(1, p.RowWeight));
    }

    [Fact]
    public void DroppingOnItselfChangesNothing()
    {
        var places = Default();
        Assert.Equal(places, PaneArrangement.MoveStacked(places, PaneId.Chat, PaneId.Chat, below: true));
        Assert.Equal(places, PaneArrangement.MoveBeside(places, PaneId.Chat, PaneId.Chat, after: true));
    }

    [Fact]
    public void OlderSettingsFillInThePlaces()
    {
        // Older files: sides only, or the chat-on-the-left switch.
        var old = new AppSettings { ChatOnLeft = true };
        Assert.Equal(DockSide.Left, old.PanePlaces[PaneId.Chat].Side);
        Assert.Equal(DockSide.Right, old.PanePlaces[PaneId.List].Side);
        var sides = new AppSettings { ChatDock = DockSide.Top, PanelDock = DockSide.Left };
        Assert.Equal(DockSide.Top, sides.PanePlaces[PaneId.Chat].Side);
        Assert.Equal(DockSide.Left, sides.PanePlaces[PaneId.Panel].Side);
    }
}
