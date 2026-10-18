// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge;

/// <summary>The panes that dock around the video.</summary>
public enum PaneId
{
    Chat,
    List,
    Panel,
}

/// <summary>Where a pane docks: a side of the video, its place along that
/// side (left to right, also for the rows above and below), and whether it
/// stacks under the pane before it rather than standing beside it.</summary>
/// <param name="ColumnWeight">Its column's width against the side's other
/// columns (read from a column's first pane).</param>
/// <param name="RowWeight">Its height against the panes stacked with it.</param>
public sealed record PanePlace(DockSide Side, int Order = 0, bool Below = false,
    double ColumnWeight = 1, double RowWeight = 1);

/// <summary>How the panes around the video are arranged, and how dragging
/// one somewhere rearranges them. Pure: a placement in, a placement out.</summary>
public static class PaneArrangement
{
    private static readonly PaneId[] All = [PaneId.Chat, PaneId.List, PaneId.Panel];

    /// <summary>Where panes docked by side alone (older settings) go: nearest
    /// the video chat, then the About panels, then the list.</summary>
    public static PanePlace Default(PaneId pane, DockSide side)
    {
        var fromVideo = pane switch { PaneId.Chat => 0, PaneId.Panel => 1, _ => 2 };
        // Left of the video, nearest it is rightmost.
        return new PanePlace(side, side == DockSide.Left ? 2 - fromVideo : fromVideo);
    }

    /// <summary>A side's panes as columns, left to right, each a stack top to
    /// bottom. Only <paramref name="shown"/> panes count; the first in a side
    /// always starts a column.</summary>
    public static IReadOnlyList<IReadOnlyList<PaneId>> Columns(DockSide side,
        IReadOnlyDictionary<PaneId, PanePlace> places, ICollection<PaneId> shown)
    {
        var columns = new List<List<PaneId>>();
        foreach (var pane in Ordered(side, places).Where(shown.Contains))
        {
            if (places[pane].Below && columns.Count > 0) columns[^1].Add(pane);
            else columns.Add([pane]);
        }
        return columns;
    }

    private static IEnumerable<PaneId> Ordered(DockSide side, IReadOnlyDictionary<PaneId, PanePlace> places) =>
        All.Where(p => places.TryGetValue(p, out var place) && place.Side == side)
            .OrderBy(p => places[p].Order).ThenBy(p => (int)p);

    /// <summary><paramref name="pane"/> as a column of its own on
    /// <paramref name="side"/>, at the edge next to the video.</summary>
    public static Dictionary<PaneId, PanePlace> MoveToSide(IReadOnlyDictionary<PaneId, PanePlace> places,
        PaneId pane, DockSide side)
    {
        var layout = Without(places, pane);
        var columns = layout[side];
        var touched = new[] { places[pane].Side, side };
        // Next to the video: last on the left, first on the right; rows above
        // and below just grow to the right.
        if (side == DockSide.Right) columns.Insert(0, [pane]);
        else columns.Add([pane]);
        return Renumber(layout, places, touched);
    }

    /// <summary><paramref name="pane"/> as a column of its own beside
    /// <paramref name="target"/>'s column: before it (to its left) or after.</summary>
    public static Dictionary<PaneId, PanePlace> MoveBeside(IReadOnlyDictionary<PaneId, PanePlace> places,
        PaneId pane, PaneId target, bool after)
    {
        if (pane == target) return new(places);
        var layout = Without(places, pane);
        var (side, column, _) = Find(layout, target);
        layout[side].Insert(after ? column + 1 : column, [pane]);
        return Renumber(layout, places, [places[pane].Side, side]);
    }

    /// <summary><paramref name="pane"/> stacked in <paramref name="target"/>'s
    /// column, just above or just below it.</summary>
    public static Dictionary<PaneId, PanePlace> MoveStacked(IReadOnlyDictionary<PaneId, PanePlace> places,
        PaneId pane, PaneId target, bool below)
    {
        if (pane == target) return new(places);
        var layout = Without(places, pane);
        var (side, column, row) = Find(layout, target);
        layout[side][column].Insert(below ? row + 1 : row, pane);
        return Renumber(layout, places, [places[pane].Side, side]);
    }

    /// <summary>Every side's columns (hidden panes included, so they keep
    /// their place), with <paramref name="pane"/> taken out.</summary>
    private static Dictionary<DockSide, List<List<PaneId>>> Without(IReadOnlyDictionary<PaneId, PanePlace> places, PaneId pane)
    {
        var layout = new Dictionary<DockSide, List<List<PaneId>>>();
        foreach (var side in Enum.GetValues<DockSide>())
        {
            var columns = Columns(side, places, All).Select(c => c.Where(p => p != pane).ToList()).Where(c => c.Count > 0).ToList();
            layout[side] = columns;
        }
        return layout;
    }

    private static (DockSide Side, int Column, int Row) Find(Dictionary<DockSide, List<List<PaneId>>> layout, PaneId pane)
    {
        foreach (var (side, columns) in layout)
            for (var c = 0; c < columns.Count; c++)
                if (columns[c].IndexOf(pane) is >= 0 and var r) return (side, c, r);
        throw new ArgumentException($"{pane} isn't placed.", nameof(pane));
    }

    /// <summary>Places from columns. The sides a pane left or joined
    /// (<paramref name="touched"/>) start evenly sized again; the others
    /// keep the sizes they were given.</summary>
    private static Dictionary<PaneId, PanePlace> Renumber(Dictionary<DockSide, List<List<PaneId>>> layout,
        IReadOnlyDictionary<PaneId, PanePlace> before, ICollection<DockSide> touched)
    {
        var places = new Dictionary<PaneId, PanePlace>();
        foreach (var (side, columns) in layout)
        {
            var keep = !touched.Contains(side);
            var order = 0;
            foreach (var column in columns)
                for (var r = 0; r < column.Count; r++)
                {
                    var old = before[column[r]];
                    places[column[r]] = new PanePlace(side, order++, Below: r > 0,
                        ColumnWeight: keep ? old.ColumnWeight : 1, RowWeight: keep ? old.RowWeight : 1);
                }
        }
        return places;
    }

    /// <summary>The places with <paramref name="pane"/>'s sizes changed.</summary>
    public static Dictionary<PaneId, PanePlace> Resized(IReadOnlyDictionary<PaneId, PanePlace> places, PaneId pane,
        double? columnWeight = null, double? rowWeight = null)
    {
        var result = new Dictionary<PaneId, PanePlace>(places);
        var place = result[pane];
        result[pane] = place with
        {
            ColumnWeight = columnWeight ?? place.ColumnWeight,
            RowWeight = rowWeight ?? place.RowWeight,
        };
        return result;
    }
}
