// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>Docking by dragging: press on a pane's header (chat's, the live
/// list's, the About panels'), drag, and drop it:
/// <list type="bullet">
/// <item>on the video: docked to that side of it, next to it;</item>
/// <item>on another pane's left or right edge: beside it, a column of its own;</item>
/// <item>on another pane's top or bottom half: stacked above or below it.</item>
/// </list>
/// While dragging, a preview shows exactly where it would land (the layout
/// worked out as if dropped), and the videos step aside so it can be drawn
/// over them (native windows can't be). Esc cancels; under the drag distance
/// it's an ordinary click.</summary>
public partial class MainWindow
{
    /// <summary>Movement before a press on a header becomes a drag.</summary>
    private const double PaneDragThreshold = 6;

    /// <summary>The share of a pane's width, at each side, that drops beside
    /// it; the rest stacks above or below it.</summary>
    private const double BesideEdge = 0.25;

    private PaneId? _panePressed;
    private Point _panePressedAt;
    private bool _paneDragging;
    private IReadOnlyDictionary<PaneId, PanePlace>? _dropPlaces;

    private void WatchPaneDrags()
    {
        AddHandler(PointerPressedEvent, OnPanePressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPaneMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPaneReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) => EndPaneDrag(drop: false), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!_paneDragging || e.Key != Key.Escape) return;
            EndPaneDrag(drop: false);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>The pane whose header <paramref name="source"/> is in, unless
    /// it's in something that takes clicks itself (a button, a box).</summary>
    private static PaneId? HeaderPane(object? source)
    {
        var inHeader = false;
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            switch (v)
            {
                case Button or ToggleButton or TextBox or ScrollBar or MenuItem when !inHeader:
                    return null;
                case Border b when b.Classes.Contains("paneHeader"):
                    inHeader = true;
                    break;
                case StreamListView when inHeader:
                    return PaneId.List;
                case ChannelPanelView when inHeader:
                    return PaneId.Panel;
                case ChatHeaderView when inHeader:
                    return PaneId.Chat;
            }
        }
        return null;
    }

    private void OnPanePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || HeaderPane(e.Source) is not { } pane) return;
        _panePressed = pane;
        _panePressedAt = e.GetPosition(this);
    }

    private void OnPaneMoved(object? sender, PointerEventArgs e)
    {
        if (_panePressed is not { } pane) return;
        var at = e.GetPosition(this);
        if (!_paneDragging)
        {
            if (Math.Abs(at.X - _panePressedAt.X) < PaneDragThreshold && Math.Abs(at.Y - _panePressedAt.Y) < PaneDragThreshold) return;
            // The press already holds the mouse (Avalonia captures it for the
            // pressed control), so moves and the release keep arriving here.
            _paneDragging = true;
            Videos.Suspended = true;
            DockTargets.IsVisible = true;
            Cursor = new Cursor(StandardCursorType.DragMove);
        }
        ShowDrop(pane, this.TranslatePoint(at, Layout));
        e.Handled = true;
    }

    private void OnPaneReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_paneDragging)
        {
            e.Handled = true;
            EndPaneDrag(drop: true);
        }
        _panePressed = null;
    }

    /// <summary>The side of the video nearest <paramref name="at"/> (in the
    /// player's coordinates), the way the targets split it: left and right
    /// thirds, top and bottom halves of the middle.</summary>
    public static DockSide DockSideAt(Point at, Size player)
    {
        var x = player.Width > 0 ? at.X / player.Width : 0.5;
        var y = player.Height > 0 ? at.Y / player.Height : 0.5;
        if (x < 0.3) return DockSide.Left;
        if (x > 0.7) return DockSide.Right;
        return y < 0.5 ? DockSide.Top : DockSide.Bottom;
    }

    /// <summary>Where the panes would be if <paramref name="pane"/> were
    /// dropped at <paramref name="at"/> (layout coordinates), and the side
    /// of the video it's over, if it is; null over itself (no change).</summary>
    public static (IReadOnlyDictionary<PaneId, PanePlace>? Places, DockSide? VideoSide) DropAt(
        UltrawideLayout.Plan plan, IReadOnlyDictionary<PaneId, PanePlace> places, PaneId pane, Point at)
    {
        foreach (var (other, rect) in new[] { (PaneId.Chat, plan.Chat), (PaneId.List, plan.List), (PaneId.Panel, plan.Panel) })
        {
            if (rect.Width <= 0 || rect.Height <= 0 || !rect.Contains(at)) continue;
            if (other == pane) return (null, null);
            var fx = (at.X - rect.X) / rect.Width;
            var fy = (at.Y - rect.Y) / rect.Height;
            if (fx < BesideEdge) return (PaneArrangement.MoveBeside(places, pane, other, after: false), null);
            if (fx > 1 - BesideEdge) return (PaneArrangement.MoveBeside(places, pane, other, after: true), null);
            return (PaneArrangement.MoveStacked(places, pane, other, below: fy >= 0.5), null);
        }
        // Over the video (or a grip): that side of it.
        var side = DockSideAt(at - plan.Player.Position, plan.Player.Size);
        return (PaneArrangement.MoveToSide(places, pane, side), side);
    }

    /// <summary>Where the preview goes, in the layout as it is now
    /// (<paramref name="now"/>); <paramref name="after"/> is the layout once
    /// dropped, for the pane's size. Over the video: a strip on that side of
    /// it, the pane's width (or height), at most part of the video. Over a
    /// pane: the half of it the drop takes (beside it, or stacked above or
    /// below), as chat's tab drags show it.</summary>
    public static Rect PreviewRect(UltrawideLayout.Plan now, UltrawideLayout.Plan after, PaneId pane, Point at)
    {
        foreach (var (other, r) in new[] { (PaneId.Chat, now.Chat), (PaneId.List, now.List), (PaneId.Panel, now.Panel) })
        {
            if (other == pane || r.Width <= 0 || r.Height <= 0 || !r.Contains(at)) continue;
            var fx = (at.X - r.X) / r.Width;
            var fy = (at.Y - r.Y) / r.Height;
            if (fx < BesideEdge) return r.WithWidth(r.Width / 2);
            if (fx > 1 - BesideEdge) return new Rect(r.X + r.Width / 2, r.Y, r.Width / 2, r.Height);
            return fy < 0.5 ? r.WithHeight(r.Height / 2) : new Rect(r.X, r.Y + r.Height / 2, r.Width, r.Height / 2);
        }
        var video = now.Player;
        var landed = pane switch { PaneId.Chat => after.Chat, PaneId.List => after.List, _ => after.Panel };
        var w = Math.Min(landed.Width > 0 ? landed.Width : video.Width / 3, video.Width * 0.45);
        var h = Math.Min(landed.Height > 0 ? landed.Height : video.Height / 3, video.Height * 0.45);
        return DockSideAt(at - video.Position, video.Size) switch
        {
            DockSide.Left => new Rect(video.X, video.Y, w, video.Height),
            DockSide.Right => new Rect(video.Right - w, video.Y, w, video.Height),
            DockSide.Top => new Rect(video.X, video.Y, video.Width, h),
            _ => new Rect(video.X, video.Bottom - h, video.Width, h),
        };
    }

    private void ShowDrop(PaneId pane, Point? at)
    {
        _dropPlaces = null;
        DockSide? side = null;
        if (at is { } p) (_dropPlaces, side) = DropAt(Layout.CurrentPlan, Layout.Places, pane, p);
        DockLeft.Classes.Set("active", side == DockSide.Left);
        DockRight.Classes.Set("active", side == DockSide.Right);
        DockTop.Classes.Set("active", side == DockSide.Top);
        DockBottom.Classes.Set("active", side == DockSide.Bottom);

        // The preview, in the layout as it is now (everything shifts once
        // dropped, so where it finally lands would mislead).
        if (_dropPlaces is null || at is not { } point)
        {
            DockPreview.IsVisible = false;
            return;
        }
        var rect = PreviewRect(Layout.CurrentPlan,
            UltrawideLayout.Arrange(Layout.Bounds.Size, Layout.Inputs(_dropPlaces)), pane, point);
        var origin = Layout.TranslatePoint(default, DockOverlay) ?? default;
        Canvas.SetLeft(DockPreview, origin.X + rect.X);
        Canvas.SetTop(DockPreview, origin.Y + rect.Y);
        DockPreview.Width = rect.Width;
        DockPreview.Height = rect.Height;
        DockPreview.IsVisible = rect.Width > 0 && rect.Height > 0;
    }

    private void EndPaneDrag(bool drop)
    {
        if (!_paneDragging) return;
        _paneDragging = false;
        var pane = _panePressed;
        var places = _dropPlaces;
        _panePressed = null;
        _dropPlaces = null;
        Cursor = null;
        DockTargets.IsVisible = false;
        DockPreview.IsVisible = false;
        foreach (var target in new[] { DockLeft, DockRight, DockTop, DockBottom }) target.Classes.Set("active", false);
        Videos.Suspended = false;
        if (drop && pane is { } moved && places is not null) _vm?.SetPanePlaces(places, moved);
    }
}
