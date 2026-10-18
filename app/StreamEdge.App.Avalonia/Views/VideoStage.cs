// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>Holds one <see cref="PlayerSurface"/> per open tab and decides
/// where each goes: the front tab's fills the stage at 16:9; with
/// <see cref="ShowPip"/> the others are small live thumbnails over it, each
/// in a cell of a 4×4 grid spread evenly from corner to corner; without it
/// they're hidden (their tabs pause).
///
/// Thumbnails can be dragged (Windows): one follows the mouse, then snaps to
/// the nearest cell, swapping with any thumbnail already there. A tab that
/// was never moved takes the first free cell along the bottom row (then the
/// rows above), so new tabs don't land over the middle of the picture.
///
/// Every surface stays in the tree for its tab's whole life, only moved or
/// hidden, so switching tabs never recreates a native window or restarts a
/// stream.</summary>
public sealed class VideoStage : Panel
{
    public static readonly StyledProperty<bool> ShowPipProperty =
        AvaloniaProperty.Register<VideoStage, bool>(nameof(ShowPip), true);

    public static readonly StyledProperty<double> AspectProperty =
        AvaloniaProperty.Register<VideoStage, double>(nameof(Aspect), 16.0 / 9.0);

    /// <summary>Every video hidden, still playing, so what's drawn under them
    /// shows: the dock targets and preview while a pane is dragged. Native
    /// windows can't be drawn over.</summary>
    public static readonly StyledProperty<bool> SuspendedProperty =
        AvaloniaProperty.Register<VideoStage, bool>(nameof(Suspended));

    public bool Suspended
    {
        get => GetValue(SuspendedProperty);
        set => SetValue(SuspendedProperty, value);
    }

    static VideoStage()
    {
        AffectsArrange<VideoStage>(ShowPipProperty, AspectProperty, SuspendedProperty);
    }

    public bool ShowPip
    {
        get => GetValue(ShowPipProperty);
        set => SetValue(ShowPipProperty, value);
    }

    public double Aspect
    {
        get => GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    /// <summary>A thumbnail was dropped: every thumbnail on show, with the
    /// cell it now has (the dragged one's new cell, a swapped one's, and the
    /// rest pinned where they are).</summary>
    public event Action<IReadOnlyList<(ChannelTabViewModel Tab, PipCell Cell)>>? PipsMoved;

    private const double PipMargin = 8;

    /// <summary>Thumbnail height for a stage: about a quarter of it, within
    /// limits that stay readable on a 440-pixel bar and small on a big one.</summary>
    public static double PipHeight(double stageHeight) => Math.Round(Math.Clamp(stageHeight * 0.24, 54, 160));

    /// <summary>Thumbnail cells across and up: a fixed 4×4, evenly spread
    /// from corner to corner of the stage.</summary>
    public const int GridColumns = 4, GridRows = 4;

    /// <summary>Thumbnail size: <see cref="PipHeight"/>, shrunk if need be
    /// so four fit across and four up without overlapping.</summary>
    public static Size PipSize(Size stage, double aspect)
    {
        var h = Math.Min(PipHeight(stage.Height), (stage.Height - (GridRows + 1) * PipMargin) / GridRows);
        var w = h * aspect;
        var maxW = (stage.Width - (GridColumns + 1) * PipMargin) / GridColumns;
        if (w > maxW)
        {
            w = maxW;
            h = w / aspect;
        }
        return new Size(Math.Max(0, Math.Round(w)), Math.Max(0, Math.Round(h)));
    }

    /// <summary>Distance between neighbouring cells, so the outer ones sit
    /// in the corners, a margin from the edges.</summary>
    private static Vector Step(Size stage, Size pip) => new(
        Math.Max(0, (stage.Width - 2 * PipMargin - pip.Width) / (GridColumns - 1)),
        Math.Max(0, (stage.Height - 2 * PipMargin - pip.Height) / (GridRows - 1)));

    /// <summary>Where a cell is; column 0 is the rightmost, row 0 the bottom.</summary>
    public static Rect CellRect(Size stage, double aspect, PipCell cell)
    {
        var pip = PipSize(stage, aspect);
        var step = Step(stage, pip);
        return new Rect(
            Math.Round(stage.Width - PipMargin - pip.Width - cell.Column * step.X),
            Math.Round(stage.Height - PipMargin - pip.Height - cell.Row * step.Y),
            pip.Width, pip.Height);
    }

    /// <summary>The cell nearest a thumbnail-sized rect, kept on the grid.</summary>
    public static PipCell NearestCell(Size stage, double aspect, Rect rect)
    {
        var pip = PipSize(stage, aspect);
        var step = Step(stage, pip);
        var column = step.X > 0 ? (int)Math.Round((stage.Width - PipMargin - pip.Width - rect.X) / step.X) : 0;
        var row = step.Y > 0 ? (int)Math.Round((stage.Height - PipMargin - pip.Height - rect.Y) / step.Y) : 0;
        return new PipCell(Math.Clamp(column, 0, GridColumns - 1), Math.Clamp(row, 0, GridRows - 1));
    }

    /// <summary>Where each tab's video goes, and which cell each thumbnail
    /// took. A default (empty) rect means hidden. Thumbnails with a
    /// <paramref name="wanted"/> cell that's on the grid and free get it;
    /// the rest fill the bottom row from the right, then the rows above.
    /// Public so tests can check placement without native windows.</summary>
    public static IReadOnlyList<(Rect Rect, PipCell? Cell)> Place(Size stage, double aspect, bool showPip,
        IReadOnlyList<bool> isFront, IReadOnlyList<PipCell?>? wanted = null)
    {
        var placed = new (Rect, PipCell?)[isFront.Count];
        var taken = new HashSet<PipCell>();
        var cells = new PipCell?[isFront.Count];
        for (var i = 0; i < isFront.Count; i++)
        {
            if (isFront[i] || !showPip || wanted?[i] is not { } cell) continue;
            if (cell.Column is >= 0 and < GridColumns && cell.Row is >= 0 and < GridRows && taken.Add(cell)) cells[i] = cell;
        }
        var next = 0;
        for (var i = 0; i < isFront.Count; i++)
        {
            if (isFront[i])
            {
                placed[i] = (AspectRatioBox.Fit(stage, aspect), null);
                continue;
            }
            if (!showPip) continue;
            if (cells[i] is null)
            {
                while (next < GridColumns * GridRows && taken.Contains(new PipCell(next % GridColumns, next / GridColumns))) next++;
                if (next >= GridColumns * GridRows) continue;
                cells[i] = new PipCell(next % GridColumns, next / GridColumns);
                taken.Add(cells[i]!.Value);
            }
            placed[i] = (CellRect(stage, aspect, cells[i]!.Value), cells[i]);
        }
        return placed;
    }

    // ----- Dragging -------------------------------------------------------

    private PlayerSurface? _dragging;
    private Vector _dragOffset;
    private readonly Dictionary<PlayerSurface, PipCell> _cells = [];

    /// <summary>The mouse moved a thumbnail by <paramref name="offset"/>
    /// (logical pixels) since the button went down.</summary>
    public void DragPip(PlayerSurface surface, Vector offset)
    {
        if (!_cells.ContainsKey(surface)) return;
        _dragging = surface;
        _dragOffset = offset;
        InvalidateArrange();
    }

    /// <summary>The button came up: snap to the nearest cell.</summary>
    public void DropPip(PlayerSurface surface)
    {
        if (_dragging != surface || !_cells.TryGetValue(surface, out var from)) return;
        var stage = Bounds.Size;
        var to = NearestCell(stage, Aspect, CellRect(stage, Aspect, from).Translate(_dragOffset));
        _dragging = null;
        _dragOffset = default;
        var moved = new List<(ChannelTabViewModel, PipCell)>();
        foreach (var (s, cell) in _cells)
        {
            if (s.DataContext is not ChannelTabViewModel tab) continue;
            moved.Add((tab, s == surface ? to : cell == to ? from : cell));
        }
        PipsMoved?.Invoke(moved);
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(availableSize);
        return new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Suspended)
        {
            foreach (var child in Children)
            {
                child.IsVisible = false;
                child.Arrange(default);
            }
            return finalSize;
        }
        var surfaces = Children.OfType<PlayerSurface>().ToList();
        var fronts = surfaces.Select(s => s.DataContext is ChannelTabViewModel { IsFront: true }).ToList();
        // Nothing to show (offline, failed, not started): the native window
        // stays hidden, or it would cover the offline banner in black. Only
        // the videos with something to show get places, so thumbnails don't
        // leave gaps.
        var showing = Enumerable.Range(0, surfaces.Count)
            .Where(i => surfaces[i].DataContext is ChannelTabViewModel { Player.IsVideoVisible: true })
            .ToList();
        var placed = Place(finalSize, Aspect, ShowPip, [.. showing.Select(i => fronts[i])],
            [.. showing.Select(i => (surfaces[i].DataContext as ChannelTabViewModel)?.PipCell)]);
        var rects = new Rect[surfaces.Count];
        _cells.Clear();
        for (var k = 0; k < showing.Count; k++)
        {
            var surface = surfaces[showing[k]];
            var (rect, cell) = placed[k];
            if (cell is { } c) _cells[surface] = c;
            // The one being dragged follows the mouse, kept on the stage.
            if (surface == _dragging && cell is not null)
                rect = rect.Translate(new Vector(
                    Math.Clamp(_dragOffset.X, -rect.X, finalSize.Width - rect.Right),
                    Math.Clamp(_dragOffset.Y, -rect.Y, finalSize.Height - rect.Bottom)));
            rects[showing[k]] = rect;
        }
        if (_dragging is { } dragged && !_cells.ContainsKey(dragged)) _dragging = null;
        for (var i = 0; i < surfaces.Count; i++)
        {
            // Hidden, not just given no room: Avalonia leaves a native window
            // that gets a zero size where it was, still drawing.
            surfaces[i].IsVisible = rects[i].Width > 0 && rects[i].Height > 0;
            surfaces[i].Arrange(rects[i]);
            surfaces[i].CanDrag = _cells.ContainsKey(surfaces[i]);
        }
        // Thumbnails overlap the main video; native windows don't follow
        // Avalonia's drawing order, so put them on top explicitly, the one
        // being dragged last so it passes over the others.
        for (var i = 0; i < surfaces.Count; i++)
            if (!fronts[i] && rects[i].Width > 0 && surfaces[i] != _dragging) surfaces[i].BringToFront();
        _dragging?.BringToFront();
        return finalSize;
    }
}
