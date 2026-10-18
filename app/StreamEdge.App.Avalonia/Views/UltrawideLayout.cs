// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>The app's arrangement: the video in the middle, and chat, the
/// live list and the channel panel each docked to a side of it
/// (<see cref="ChatPlace"/>, <see cref="ListPlace"/>, <see cref="PanelPlace"/>).
///
/// The video is the one thing whose size is fixed by the content: 16:9 at
/// the height the rows above and below leave it, so it fills that height
/// with no bars. Panes left and right of it share the rest of the width;
/// rows above and below it are as wide as it and take
/// <see cref="TopRowShare"/> and <see cref="BottomRowShare"/> of the height.
/// Panes docked to one side stand in columns, and panes stacked in a column
/// share its height. With panes on both sides, a <see cref="PaneGrip"/>
/// either side of the video slides it sideways (its size never changes).
/// A grid can't express "width follows height", which is why this is a
/// panel rather than a Grid.
///
/// Everything but the video resizes by dragging the gaps between things:
/// between columns, between stacked panes, and between a row and the video.
/// Sizes are the places' weights and the row shares; a finished drag raises
/// <see cref="PlacesResized"/> so they can be saved.</summary>
/// <remarks>Children: chat, player, list (always in that order), then two
/// <see cref="PaneGrip"/>s, left then right, then the channel panel. With
/// nothing left or right of the video, its column is the whole width and
/// the video letterboxes in it. Only when the window is too narrow to give
/// the sides <see cref="MinSideWidth"/> a column does the video shrink.</remarks>
public sealed class UltrawideLayout : Panel
{
    public static readonly StyledProperty<double> PlayerAspectProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(PlayerAspect), 16.0 / 9.0);

    public static readonly StyledProperty<double> MinSideWidthProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(MinSideWidth), 180);

    /// <summary>With panes on both sides of the video: chat's share of the
    /// width beside it when chat is one of them, else the left side's share.
    /// 0..1. Two-way: dragging a grip writes it back.</summary>
    public static readonly StyledProperty<double> ChatShareProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(ChatShare), 0.5,
            defaultBindingMode: BindingMode.TwoWay, coerce: (_, v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0.5);

    /// <summary>Grips beside the video and the gaps between panes.</summary>
    public static readonly StyledProperty<double> GripWidthProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(GripWidth), 5);

    public static readonly StyledProperty<bool> ShowPlayerProperty =
        AvaloniaProperty.Register<UltrawideLayout, bool>(nameof(ShowPlayer), true);

    /// <summary>Just the player, filling the panel (video-only fullscreen).</summary>
    public static readonly StyledProperty<bool> VideoOnlyProperty =
        AvaloniaProperty.Register<UltrawideLayout, bool>(nameof(VideoOnly));

    public static readonly StyledProperty<bool> ShowListProperty =
        AvaloniaProperty.Register<UltrawideLayout, bool>(nameof(ShowList), true);

    public static readonly StyledProperty<bool> ShowPanelProperty =
        AvaloniaProperty.Register<UltrawideLayout, bool>(nameof(ShowPanel));

    /// <summary>Where each pane docks: a side of the video, its place along
    /// it, whether it stacks under the pane before it, and its size.</summary>
    public static readonly StyledProperty<PanePlace> ChatPlaceProperty =
        AvaloniaProperty.Register<UltrawideLayout, PanePlace>(nameof(ChatPlace), PaneArrangement.Default(PaneId.Chat, DockSide.Right));

    public static readonly StyledProperty<PanePlace> ListPlaceProperty =
        AvaloniaProperty.Register<UltrawideLayout, PanePlace>(nameof(ListPlace), PaneArrangement.Default(PaneId.List, DockSide.Left));

    public static readonly StyledProperty<PanePlace> PanelPlaceProperty =
        AvaloniaProperty.Register<UltrawideLayout, PanePlace>(nameof(PanelPlace), PaneArrangement.Default(PaneId.Panel, DockSide.Bottom));

    /// <summary>Share of the height the row above the video takes. Two-way:
    /// dragging the line under it writes it back.</summary>
    public static readonly StyledProperty<double> TopRowShareProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(TopRowShare), 0.35,
            defaultBindingMode: BindingMode.TwoWay, coerce: (_, v) => double.IsFinite(v) ? Math.Clamp(v, 0.1, 0.7) : 0.35);

    /// <summary>The same for the row below the video.</summary>
    public static readonly StyledProperty<double> BottomRowShareProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(BottomRowShare), 0.35,
            defaultBindingMode: BindingMode.TwoWay, coerce: (_, v) => double.IsFinite(v) ? Math.Clamp(v, 0.1, 0.7) : 0.35);

    static UltrawideLayout()
    {
        AffectsMeasure<UltrawideLayout>(PlayerAspectProperty, MinSideWidthProperty, ChatShareProperty, GripWidthProperty,
            ShowPlayerProperty, VideoOnlyProperty, ShowListProperty, ShowPanelProperty,
            ChatPlaceProperty, ListPlaceProperty, PanelPlaceProperty, TopRowShareProperty, BottomRowShareProperty);
    }

    public double PlayerAspect { get => GetValue(PlayerAspectProperty); set => SetValue(PlayerAspectProperty, value); }
    public double MinSideWidth { get => GetValue(MinSideWidthProperty); set => SetValue(MinSideWidthProperty, value); }
    public double ChatShare { get => GetValue(ChatShareProperty); set => SetValue(ChatShareProperty, value); }
    public double GripWidth { get => GetValue(GripWidthProperty); set => SetValue(GripWidthProperty, value); }
    public bool ShowPlayer { get => GetValue(ShowPlayerProperty); set => SetValue(ShowPlayerProperty, value); }
    public bool VideoOnly { get => GetValue(VideoOnlyProperty); set => SetValue(VideoOnlyProperty, value); }
    public bool ShowList { get => GetValue(ShowListProperty); set => SetValue(ShowListProperty, value); }
    public bool ShowPanel { get => GetValue(ShowPanelProperty); set => SetValue(ShowPanelProperty, value); }
    public PanePlace ChatPlace { get => GetValue(ChatPlaceProperty); set => SetValue(ChatPlaceProperty, value); }
    public PanePlace ListPlace { get => GetValue(ListPlaceProperty); set => SetValue(ListPlaceProperty, value); }
    public PanePlace PanelPlace { get => GetValue(PanelPlaceProperty); set => SetValue(PanelPlaceProperty, value); }
    public double TopRowShare { get => GetValue(TopRowShareProperty); set => SetValue(TopRowShareProperty, value); }
    public double BottomRowShare { get => GetValue(BottomRowShareProperty); set => SetValue(BottomRowShareProperty, value); }

    /// <summary>Every pane's place, as the layout has them now.</summary>
    public IReadOnlyDictionary<PaneId, PanePlace> Places => new Dictionary<PaneId, PanePlace>
    {
        [PaneId.Chat] = ChatPlace,
        [PaneId.List] = ListPlace,
        [PaneId.Panel] = PanelPlace,
    };

    /// <summary>A drag between columns or stacked panes finished: the places
    /// with their new sizes, to keep.</summary>
    public event Action<IReadOnlyDictionary<PaneId, PanePlace>>? PlacesResized;

    /// <summary>A row of panes above or below the video is never shorter
    /// than this (when shown)…</summary>
    public const double MinRowHeight = 90;

    /// <summary>…and the rows leave the video at least this.</summary>
    public const double MinVideoHeight = 120;

    /// <summary>Resizing stops before a column gets narrower than this…</summary>
    public const double MinColumnWidth = 120;

    /// <summary>…or a stacked pane shorter than this.</summary>
    public const double MinStackHeight = 60;

    public readonly record struct Input(double Aspect, double MinSide, double ChatShare, bool ShowPlayer, double Grip,
        bool ShowList = true, bool ShowPanel = false,
        DockSide ChatDock = DockSide.Right, DockSide ListDock = DockSide.Left, DockSide PanelDock = DockSide.Bottom,
        double TopRowShare = 0.35, double BottomRowShare = 0.35, IReadOnlyDictionary<PaneId, PanePlace>? Places = null)
    {
        /// <summary>Each pane's place: <see cref="Places"/>, or by side alone
        /// (each where <see cref="PaneArrangement.Default"/> puts it).</summary>
        public IReadOnlyDictionary<PaneId, PanePlace> AllPlaces => Places ?? new Dictionary<PaneId, PanePlace>
        {
            [PaneId.Chat] = PaneArrangement.Default(PaneId.Chat, ChatDock),
            [PaneId.List] = PaneArrangement.Default(PaneId.List, ListDock),
            [PaneId.Panel] = PaneArrangement.Default(PaneId.Panel, PanelDock),
        };

        public DockSide ChatSide => AllPlaces[PaneId.Chat].Side;
    }

    /// <summary>What dragging a gap resizes.</summary>
    public enum SplitKind
    {
        /// <summary>Between two columns on one side: <see cref="Splitter.Before"/>
        /// and <see cref="Splitter.After"/> are the columns' first panes.</summary>
        Columns,
        /// <summary>Between two panes stacked in a column.</summary>
        Rows,
        /// <summary>Between the row above the video and the video.</summary>
        TopRow,
        /// <summary>Between the video and the row below it.</summary>
        BottomRow,
    }

    /// <summary>A gap that resizes what's either side of it when dragged.</summary>
    public readonly record struct Splitter(Rect Rect, SplitKind Kind, DockSide Side, PaneId Before = default, PaneId After = default)
    {
        public bool Vertical => Kind == SplitKind.Columns;
    }

    /// <summary>Where everything goes. A default (empty) rect means not shown.</summary>
    public readonly record struct Plan(Rect Chat, Rect Player, Rect List, Rect Panel, Rect LeftGrip, Rect RightGrip,
        IReadOnlyList<Splitter> Splitters)
    {
        public Rect this[PaneId pane] => pane switch { PaneId.Chat => Chat, PaneId.List => List, _ => Panel };
    }

    /// <summary>The panes shown.</summary>
    private static List<PaneId> Shown(Input p)
    {
        var shown = new List<PaneId> { PaneId.Chat };
        if (p.ShowList) shown.Add(PaneId.List);
        if (p.ShowPanel) shown.Add(PaneId.Panel);
        return shown;
    }

    /// <summary>The left side's share of the width beside the video, with
    /// panes on both sides: <see cref="Input.ChatShare"/> means chat's share
    /// when chat is beside the video.</summary>
    private static double LeftShare(Input p) =>
        p.ChatSide == DockSide.Left ? p.ChatShare : p.ChatSide == DockSide.Right ? 1 - p.ChatShare : p.ChatShare;

    /// <summary>Lengths for <paramref name="weights"/> sharing
    /// <paramref name="total"/>, whole pixels, the last taking what's left.</summary>
    private static double[] Split(double total, IReadOnlyList<double> weights)
    {
        var sum = weights.Sum(w => Math.Max(w, 0.01));
        var lengths = new double[weights.Count];
        var used = 0.0;
        for (var i = 0; i < weights.Count; i++)
        {
            lengths[i] = i == weights.Count - 1 ? Math.Max(0, total - used) : Math.Floor(total * Math.Max(weights[i], 0.01) / sum);
            used += lengths[i];
        }
        return lengths;
    }

    /// <summary>The arrangement for a panel size. Public so tests can check
    /// the arithmetic without rendering.</summary>
    public static Plan Arrange(Size size, Input p)
    {
        var w = Math.Max(0, size.Width);
        var h = double.IsFinite(size.Height) ? Math.Max(0, size.Height) : 0;
        var g = p.Grip;
        var places = p.AllPlaces;
        var shown = Shown(p);
        var columns = Enum.GetValues<DockSide>().ToDictionary(s => s, s => PaneArrangement.Columns(s, places, shown));
        bool At(DockSide side) => columns[side].Count > 0;
        var splitters = new List<Splitter>();

        // ----- Heights: a row above, a gap, the video, a gap, a row below.
        var top = At(DockSide.Top);
        var bottom = At(DockSide.Bottom);
        var topH = top ? Math.Max(MinRowHeight, Math.Round(h * p.TopRowShare)) : 0;
        var bottomH = bottom ? Math.Max(MinRowHeight, Math.Round(h * p.BottomRowShare)) : 0;
        var gaps = (top ? g : 0) + (bottom ? g : 0);
        if (topH + bottomH > 0 && h - gaps - topH - bottomH < MinVideoHeight)
        {
            // Not enough height for the rows and the video: the rows give way.
            var scale = Math.Max(0, h - gaps - MinVideoHeight) / (topH + bottomH);
            topH = Math.Floor(topH * scale);
            bottomH = Math.Floor(bottomH * scale);
        }
        var videoH = Math.Max(0, h - gaps - topH - bottomH);
        var videoY = topH + (top ? g : 0);

        // ----- Widths: panes left, the video's column, panes right. Each
        // column beside the video needs its minimum (stacked panes share one).
        static double SideMin(int count, double min, double gap) => count == 0 ? 0 : count * min + (count - 1) * gap;
        var leftMin = SideMin(columns[DockSide.Left].Count, p.MinSide, g);
        var rightMin = SideMin(columns[DockSide.Right].Count, p.MinSide, g);
        var left = leftMin > 0;
        var right = rightMin > 0;
        var gripCount = (left ? 1 : 0) + (right ? 1 : 0);
        var avail = Math.Max(0, w - gripCount * g);
        var column = gripCount == 0 ? avail
            : p.ShowPlayer ? Math.Floor(Math.Max(0, Math.Min(videoH * p.Aspect, avail - leftMin - rightMin))) : 0;
        var sides = avail - column;
        double leftW = 0, rightW = 0;
        if (left && right)
        {
            leftW = Math.Round(sides * LeftShare(p));
            if (sides >= leftMin + rightMin) leftW = Math.Clamp(leftW, leftMin, sides - rightMin);
            rightW = sides - leftW;
        }
        else if (left) leftW = sides;
        else if (right) rightW = sides;
        var x = leftW + (left ? g : 0);

        // ----- Each pane's rect: a side's columns share its width by their
        // weights, with a gap between; panes stacked in a column share its
        // height the same way.
        var rects = new Dictionary<PaneId, Rect>();
        void Place(DockSide side, Rect area)
        {
            var cols = columns[side];
            if (cols.Count == 0) return;
            var widths = Split(area.Width - (cols.Count - 1) * g, [.. cols.Select(c => places[c[0]].ColumnWeight)]);
            var cx = area.X;
            for (var c = 0; c < cols.Count; c++)
            {
                var stack = cols[c];
                var heights = Split(area.Height - (stack.Count - 1) * g, [.. stack.Select(q => places[q].RowWeight)]);
                var cy = area.Y;
                for (var r = 0; r < stack.Count; r++)
                {
                    rects[stack[r]] = new Rect(cx, cy, widths[c], heights[r]);
                    cy += heights[r];
                    if (r < stack.Count - 1)
                    {
                        splitters.Add(new Splitter(new Rect(cx, cy, widths[c], g), SplitKind.Rows, side, stack[r], stack[r + 1]));
                        cy += g;
                    }
                }
                cx += widths[c];
                if (c < cols.Count - 1)
                {
                    splitters.Add(new Splitter(new Rect(cx, area.Y, g, area.Height), SplitKind.Columns, side, stack[0], cols[c + 1][0]));
                    cx += g;
                }
            }
        }
        Place(DockSide.Left, new Rect(0, 0, leftW, h));
        Place(DockSide.Right, new Rect(x + column + (right ? g : 0), 0, rightW, h));
        Place(DockSide.Top, new Rect(x, 0, column, topH));
        Place(DockSide.Bottom, new Rect(x, h - bottomH, column, bottomH));
        if (top) splitters.Add(new Splitter(new Rect(x, topH, column, g), SplitKind.TopRow, DockSide.Top));
        if (bottom) splitters.Add(new Splitter(new Rect(x, h - bottomH - g, column, g), SplitKind.BottomRow, DockSide.Bottom));

        return new Plan(
            rects.GetValueOrDefault(PaneId.Chat),
            p.ShowPlayer ? new Rect(x, videoY, column, videoH) : default,
            rects.GetValueOrDefault(PaneId.List),
            rects.GetValueOrDefault(PaneId.Panel),
            left ? new Rect(leftW, 0, g, h) : default,
            right ? new Rect(x + column, 0, g, h) : default,
            splitters);
    }

    private Input Current() => Inputs(Places);

    /// <summary>This layout's settings with these pane places (what a drop
    /// would make of it, for the preview).</summary>
    public Input Inputs(IReadOnlyDictionary<PaneId, PanePlace> places) =>
        new(PlayerAspect, MinSideWidth, ChatShare, ShowPlayer, GripWidth, ShowList, ShowPanel,
            TopRowShare: TopRowShare, BottomRowShare: BottomRowShare, Places: places);

    /// <summary>Where everything is now, in this panel's coordinates.</summary>
    public Plan CurrentPlan => Arrange(Bounds.Size, Current());

    private static Rect[] Rects(Plan plan) => [plan.Chat, plan.Player, plan.List, plan.LeftGrip, plan.RightGrip, plan.Panel];

    private Plan _plan;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (VideoOnly)
        {
            for (var i = 0; i < Children.Count; i++) Children[i].Measure(i == 1 ? availableSize : default);
            return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
                double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
        }
        // Infinite width (inside a ScrollViewer, say) has no sensible split;
        // fall back to the video at its natural width plus two minimum sides.
        var width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : 2 * MinSideWidth + 2 * GripWidth + (double.IsFinite(availableSize.Height) ? availableSize.Height * PlayerAspect : 0);
        var size = new Size(width, double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
        var rects = Rects(Arrange(size, Current()));
        for (var i = 0; i < Children.Count; i++) Children[i].Measure(i < rects.Length ? rects[i].Size : default);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (VideoOnly)
        {
            _plan = default;
            for (var i = 0; i < Children.Count; i++) Children[i].Arrange(i == 1 ? new Rect(finalSize) : default);
            return finalSize;
        }
        _plan = Arrange(finalSize, Current());
        var rects = Rects(_plan);
        for (var i = 0; i < Children.Count; i++) Children[i].Arrange(i < rects.Length ? rects[i] : default);

        return finalSize;
    }

    // ----- Dragging -----------------------------------------------------------

    private bool _dragging;
    private double _dragStartX;
    private Plan _dragStart;

    /// <summary>Grips slide the video only with panes on both sides of it.</summary>
    private bool CanSlide
    {
        get
        {
            var (left, right) = SideColumns();
            return left > 0 && right > 0;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AddHandler(PointerPressedEvent, OnGripPressed, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnGripMoved, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnGripReleased, handledEventsToo: true);
        AddHandler(PointerExitedEvent, OnPointerLeft, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        RemoveHandler(PointerPressedEvent, OnGripPressed);
        RemoveHandler(PointerMovedEvent, OnGripMoved);
        RemoveHandler(PointerReleasedEvent, OnGripReleased);
        RemoveHandler(PointerExitedEvent, OnPointerLeft);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The gap under <paramref name="at"/>, when the mouse is on
    /// the gap itself (not on a pane).</summary>
    private Splitter? SplitterAt(object? source, Point at) =>
        source == this && _plan.Splitters is { } splitters
            ? splitters.Cast<Splitter?>().FirstOrDefault(s => s!.Value.Rect.Inflate(2).Contains(at))
            : null;

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (SplitterAt(e.Source, e.GetPosition(this)) is { } splitter)
        {
            BeginSplit(splitter, e.GetPosition(this));
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (e.Source is not PaneGrip grip || !CanSlide) return;
        if (e.ClickCount == 2)
        {
            ResetSplit();
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStart = Arrange(Bounds.Size, Current());
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        var at = e.GetPosition(this);
        if (_splitting is not null)
        {
            Resize(at);
            e.Handled = true;
            return;
        }
        if (_dragging)
        {
            Drag(at.X - _dragStartX);
            e.Handled = true;
            return;
        }
        // Over a gap: say it can be dragged.
        var hovered = SplitterAt(e.Source, at);
        if (hovered != _hovered)
        {
            _hovered = hovered;
            Cursor = hovered is { } h ? new Cursor(h.Vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth) : null;
        }
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_splitting is not null)
        {
            e.Pointer.Capture(null);
            EndResize();
            e.Handled = true;
            return;
        }
        if (!_dragging) return;
        e.Pointer.Capture(null);
        _dragging = false;
        e.Handled = true;
    }

    private void OnPointerLeft(object? sender, PointerEventArgs e)
    {
        if (_hovered is null || _splitting is not null || e.Source != this) return;
        _hovered = null;
        Cursor = null;

    }

    /// <summary>Slides the video <paramref name="dx"/> pixels right (negative:
    /// left) from where the drag started, with panes on both sides of it.
    /// Either grip does the same thing: the video's size is fixed, so the left
    /// side grows by what the right one loses. Public for tests.</summary>
    public void Drag(double dx)
    {
        if (!CanSlide) return;
        var start = _dragging ? _dragStart : Arrange(Bounds.Size, Current());
        var leftW = start.LeftGrip.X;
        var sides = leftW + (Bounds.Width - start.RightGrip.Right);
        if (sides <= 0) return;
        var left = leftW + dx;
        var (leftColumns, rightColumns) = SideColumns();
        var leftMin = leftColumns == 0 ? 0 : leftColumns * MinSideWidth + (leftColumns - 1) * GripWidth;
        var rightMin = rightColumns == 0 ? 0 : rightColumns * MinSideWidth + (rightColumns - 1) * GripWidth;
        if (sides >= leftMin + rightMin) left = Math.Clamp(left, leftMin, sides - rightMin);
        var leftShare = left / sides;
        SetCurrentValue(ChatShareProperty, ChatPlace.Side == DockSide.Right ? 1 - leftShare : leftShare);
    }

    /// <summary>Columns of panes left and right of the video.</summary>
    private (int Left, int Right) SideColumns()
    {
        var input = Current();
        var shown = Shown(input);
        return (PaneArrangement.Columns(DockSide.Left, input.AllPlaces, shown).Count,
            PaneArrangement.Columns(DockSide.Right, input.AllPlaces, shown).Count);
    }

    /// <summary>Back to an even split.</summary>
    public void ResetSplit() => SetCurrentValue(ChatShareProperty, 0.5);

    // ----- Resizing columns and rows --------------------------------------------

    private Splitter? _hovered;
    private Splitter? _splitting;
    private Point _splitStart;
    private Plan _splitPlan;
    private IReadOnlyDictionary<PaneId, PanePlace> _splitPlaces = new Dictionary<PaneId, PanePlace>();

    /// <summary>Starts dragging a gap. Public, with <see cref="Resize"/> and
    /// <see cref="EndResize"/>, for tests.</summary>
    public void BeginSplit(Splitter splitter, Point at)
    {
        _splitting = splitter;
        _splitStart = at;
        _splitPlan = Arrange(Bounds.Size, Current());
        // From here, sizes are measured in pixels: the side's (or column's)
        // weights become what's on screen, so moving one gap moves only it.
        var places = new Dictionary<PaneId, PanePlace>(Places);
        foreach (var (pane, place) in Places)
        {
            var rect = _splitPlan[pane];
            if (rect.Width <= 0 || place.Side != splitter.Side) continue;
            places[pane] = place with { ColumnWeight = rect.Width, RowWeight = rect.Height };
        }
        _splitPlaces = places;

    }

    /// <summary>Moves the gap being dragged to <paramref name="at"/>: the
    /// things either side of it grow and shrink by the same amount.</summary>
    public void Resize(Point at)
    {
        if (_splitting is not { } s) return;
        var h = Bounds.Height;
        switch (s.Kind)
        {
            case SplitKind.TopRow:
            {
                var row = _splitPlan.Splitters.First(x => x.Kind == SplitKind.TopRow).Rect.Y + (at.Y - _splitStart.Y);
                SetCurrentValue(TopRowShareProperty, Math.Clamp(row, MinRowHeight, h - MinVideoHeight) / h);
                return;
            }
            case SplitKind.BottomRow:
            {
                var row = h - (_splitPlan.Splitters.First(x => x.Kind == SplitKind.BottomRow).Rect.Bottom + (at.Y - _splitStart.Y));
                SetCurrentValue(BottomRowShareProperty, Math.Clamp(row, MinRowHeight, h - MinVideoHeight) / h);
                return;
            }
        }
        var before = _splitPlan[s.Before];
        var after = _splitPlan[s.After];
        var columns = s.Kind == SplitKind.Columns;
        var a = columns ? before.Width : before.Height;
        var b = columns ? after.Width : after.Height;
        var min = columns ? MinColumnWidth : MinStackHeight;
        var delta = columns ? at.X - _splitStart.X : at.Y - _splitStart.Y;
        var newA = a + b >= 2 * min ? Math.Clamp(a + delta, min, a + b - min) : a;
        var places = columns
            ? PaneArrangement.Resized(PaneArrangement.Resized(_splitPlaces, s.Before, columnWeight: newA), s.After, columnWeight: a + b - newA)
            : PaneArrangement.Resized(PaneArrangement.Resized(_splitPlaces, s.Before, rowWeight: newA), s.After, rowWeight: a + b - newA);
        SetCurrentValue(ChatPlaceProperty, places[PaneId.Chat]);
        SetCurrentValue(ListPlaceProperty, places[PaneId.List]);
        SetCurrentValue(PanelPlaceProperty, places[PaneId.Panel]);
    }

    /// <summary>The gap was let go: new column and row sizes are passed on
    /// to be kept (row heights above and below are two-way already).</summary>
    public void EndResize()
    {
        if (_splitting is not { } split) return;
        _splitting = null;

        if (split.Kind is SplitKind.Columns or SplitKind.Rows) PlacesResized?.Invoke(Places);
    }
}

/// <summary>The thin strip between the video and the panes beside it. With
/// panes on both sides, dragging slides the video and double-click centres it.</summary>
public sealed class PaneGrip : Border
{
    public PaneGrip()
    {
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        ToolTip.SetTip(this, "Drag to move the video · double-click to center");
    }
}
