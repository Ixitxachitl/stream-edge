// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>Where a dragged tab lands: at <see cref="Index"/> in a column's
/// tab strip, or as a new column at <see cref="Index"/> (a drop on a
/// column's left or right edge). <see cref="Indicator"/> is what to light up,
/// in chat-pane coordinates.</summary>
public sealed record TabDropTarget(ChatColumnViewModel? Column, int Index, bool NewColumn, Rect Indicator);

/// <summary>Dragging tabs: reorder within a strip, move to another column,
/// or drop on a column's edge to split it into a new column beside it.</summary>
public partial class MainWindow
{
    /// <summary>Pointer travel before a press on a tab becomes a drag, so a
    /// slightly shaky click still just selects.</summary>
    private const double DragThreshold = 6;

    /// <summary>Share of a column's width, on each side, that splits.</summary>
    private const double EdgeShare = 0.25;

    /// <summary>Narrowest a column may get from a split.</summary>
    private const double MinColumnWidth = 200;

    private ChannelTabViewModel? _pressedTab;
    private Point _pressedAt;
    private bool _draggingTab;
    private TabDropTarget? _drop;

    private void WatchTabDrags()
    {
        // Tunnel, and handled too: the tab is a button, which handles its own
        // press. The press still selects it; only moving turns it into a drag.
        ChatPane.AddHandler(PointerPressedEvent, OnTabPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatPane.AddHandler(PointerMovedEvent, OnTabDragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatPane.AddHandler(PointerReleasedEvent, OnTabReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatPane.AddHandler(PointerCaptureLostEvent, (_, _) => EndTabDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedTab = null;
        if (!e.GetCurrentPoint(ChatPane).Properties.IsLeftButtonPressed) return;
        var tab = (e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tab"))?.DataContext as ChannelTabViewModel;
        if (tab is null) return;
        _pressedTab = tab;
        _pressedAt = e.GetPosition(ChatPane);
    }

    private void OnTabDragMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedTab is not { } tab) return;
        var at = e.GetPosition(ChatPane);
        if (!_draggingTab)
        {
            if (Math.Abs(at.X - _pressedAt.X) < DragThreshold && Math.Abs(at.Y - _pressedAt.Y) < DragThreshold) return;
            _draggingTab = true;
            // Taken from the button, so letting go doesn't also click it.
            e.Pointer.Capture(ChatPane);
            ChatPane.Cursor = new Cursor(StandardCursorType.DragMove);
        }
        _drop = DropTargetAt(tab, at);
        ShowDropIndicator(_drop);
        e.Handled = true;
    }

    private void OnTabReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_draggingTab)
        {
            _pressedTab = null;
            return;
        }
        var (tab, drop) = (_pressedTab, _drop);
        e.Pointer.Capture(null);
        EndTabDrag();
        e.Handled = true;
        if (_vm is null || tab is null || drop?.Column is not { } column) return;
        if (drop.NewColumn) _vm.MoveTabToNewColumn(tab, drop.Index);
        else _vm.MoveTab(tab, column, drop.Index);
    }

    private void EndTabDrag()
    {
        _pressedTab = null;
        _draggingTab = false;
        _drop = null;
        ChatPane.Cursor = null;
        ShowDropIndicator(null);
    }

    private void ShowDropIndicator(TabDropTarget? drop)
    {
        DropIndicator.IsVisible = drop is not null;
        if (drop is null) return;
        Canvas.SetLeft(DropIndicator, drop.Indicator.X);
        Canvas.SetTop(DropIndicator, drop.Indicator.Y);
        DropIndicator.Width = drop.Indicator.Width;
        DropIndicator.Height = drop.Indicator.Height;
        DropIndicator.Classes.Set("split", drop.NewColumn);
        DropIndicator.Classes.Set("into", !drop.NewColumn && drop.Indicator.Width > 4);
    }

    /// <summary>What dropping <paramref name="tab"/> at <paramref name="at"/>
    /// (chat-pane coordinates) would do; null for nothing. Public for tests.</summary>
    public TabDropTarget? DropTargetAt(ChannelTabViewModel tab, Point at)
    {
        if (_vm is null) return null;
        var views = ChatColumns.GetVisualDescendants().OfType<ChatColumnView>()
            .Where(v => v.DataContext is ChatColumnViewModel)
            .Select(v => (View: v, Column: (ChatColumnViewModel)v.DataContext!, Box: BoxOf(v)))
            .OrderBy(v => v.Box.X)
            .ToList();
        if (views.Count == 0) return null;
        // Past either end of the pane counts as the nearest column.
        var hit = views.FirstOrDefault(v => at.X < v.Box.Right);
        if (hit.View is null) hit = views[^1];
        var (view, column, box) = hit;
        var columnIndex = _vm.Columns.IndexOf(column);

        // Over the tab strip: between which two tabs.
        var strip = view.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tabStrip"));
        var stripBox = strip is { IsVisible: true } ? BoxOf(strip) : new Rect(box.X, box.Y, box.Width, 26);
        if (at.Y <= stripBox.Bottom + 4)
        {
            var tabs = view.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("tab") && b.DataContext is ChannelTabViewModel)
                .Select(BoxOf).OrderBy(b => b.X).ToList();
            var index = tabs.Count(b => b.Center.X < at.X);
            var x = tabs.Count == 0 ? box.X + 2
                : index < tabs.Count ? tabs[index].X - 1
                : tabs[^1].Right + 1;
            // Dropped where it already is: nothing to do.
            if (column.Tabs.IndexOf(tab) is var from && from >= 0 && (index == from || index == from + 1)) return null;
            return new TabDropTarget(column, index, false, new Rect(x - 1, stripBox.Y, 3, stripBox.Height));
        }

        // Over the chat: an edge splits, the middle moves the tab in.
        var edge = Math.Max(40, box.Width * EdgeShare);
        var side = at.X < box.X + edge ? 0 : at.X > box.Right - edge ? 1 : -1;
        if (side >= 0 && CanSplitInto(tab, columnIndex + side))
        {
            var half = new Rect(side == 0 ? box.X : box.Center.X, box.Y, box.Width / 2, box.Height);
            return new TabDropTarget(column, columnIndex + side, true, half);
        }
        if (column.Tabs.Contains(tab)) return null;
        return new TabDropTarget(column, column.Tabs.Count, false, box.Deflate(2));
    }

    private bool CanSplitInto(ChannelTabViewModel tab, int at)
    {
        if (_vm is null || !_vm.CanSplit(tab, at)) return false;
        // A lone tab moving its column doesn't add one; anything else must
        // leave every column at least MinColumnWidth.
        var adds = _vm.ColumnOf(tab) is { Tabs.Count: > 1 } ? 1 : 0;
        return ChatPane.Bounds.Width / (_vm.Columns.Count + adds) >= MinColumnWidth;
    }

    private Rect BoxOf(Visual visual) =>
        visual.TranslatePoint(default, ChatPane) is { } origin ? new Rect(origin, visual.Bounds.Size) : default;
}
