// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>Where a dragged tab lands: at <see cref="Index"/> in a column's
/// tab strip, as a new column at <see cref="Index"/> (a drop on a chat's
/// left or right edge), or, with <see cref="StackBelow"/> set, as a new chat
/// stacked under (true) or over (false) <see cref="Column"/> (its bottom or
/// top edge). <see cref="Indicator"/> is what to light up, in chat-pane
/// coordinates.</summary>
public sealed record TabDropTarget(ChatColumnViewModel? Column, int Index, bool NewColumn, Rect Indicator,
    bool? StackBelow = null);

/// <summary>Dragging tabs: reorder within a strip, move to another chat, or
/// drop on a chat's edge to split it: a new column beside it (left, right)
/// or a new chat stacked with it (top, bottom).</summary>
public partial class MainWindow
{
    /// <summary>Pointer travel before a press on a tab becomes a drag, so a
    /// slightly shaky click still just selects.</summary>
    private const double DragThreshold = 6;

    /// <summary>Share of a column's width, on each side, that splits.</summary>
    private const double EdgeShare = 0.25;

    /// <summary>Narrowest a column may get from a split.</summary>
    private const double MinColumnWidth = 200;

    /// <summary>Shortest a stacked chat may get: a tab strip, a header, a few
    /// lines and the chat box.</summary>
    private const double MinStackHeight = 140;

    private ChannelTabViewModel? _pressedTab;
    private Border? _pressedTabBorder;
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
        var border = (e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tab"));
        if (border?.DataContext is not ChannelTabViewModel tab) return;
        _pressedTab = tab;
        _pressedTabBorder = border;
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
            // The tab in hand is the lit one in its strip while it moves
            // (it isn't selected: that would put it on video).
            MarkDraggedTab(true);
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
        if (drop.StackBelow is { } below) _vm.MoveTabToStack(tab, column, below);
        else if (drop.NewColumn) _vm.MoveTabToNewColumn(tab, drop.Index);
        else _vm.MoveTab(tab, column, drop.Index);
    }

    private void EndTabDrag()
    {
        MarkDraggedTab(false);
        _pressedTab = null;
        _pressedTabBorder = null;
        _draggingTab = false;
        _drop = null;
        ChatPane.Cursor = null;
        ShowDropIndicator(null);
    }

    private void MarkDraggedTab(bool dragging)
    {
        if (_pressedTabBorder is not { } border) return;
        border.Classes.Set("dragging", dragging);
        // Its strip too, so the selected tab there steps back meanwhile.
        border.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tabStrip"))
            ?.Classes.Set("dragging", dragging);
    }

    private void ShowDropIndicator(TabDropTarget? drop)
    {
        DropIndicator.IsVisible = drop is not null;
        if (drop is null) return;
        Canvas.SetLeft(DropIndicator, drop.Indicator.X);
        Canvas.SetTop(DropIndicator, drop.Indicator.Y);
        DropIndicator.Width = drop.Indicator.Width;
        DropIndicator.Height = drop.Indicator.Height;
        var split = drop.NewColumn || drop.StackBelow is not null;
        DropIndicator.Classes.Set("split", split);
        DropIndicator.Classes.Set("into", !split && drop.Indicator.Width > 4);
    }

    /// <summary>What dropping <paramref name="tab"/> at <paramref name="at"/>
    /// (chat-pane coordinates) would do; null for nothing. Public for tests.</summary>
    public TabDropTarget? DropTargetAt(ChannelTabViewModel tab, Point at)
    {
        if (_vm is null) return null;
        var views = ChatColumns.GetVisualDescendants().OfType<ChatColumnView>()
            .Where(v => v.DataContext is ChatColumnViewModel)
            .Select(v => (View: v, Column: (ChatColumnViewModel)v.DataContext!, Box: BoxOf(v)))
            .ToList();
        if (views.Count == 0) return null;
        // The chat under the pointer; past an edge of the pane, the nearest.
        var hit = views.FirstOrDefault(v => v.Box.Contains(at));
        if (hit.View is null) hit = views.MinBy(v => Distance(v.Box, at));
        var (view, column, box) = hit;

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

        // Over the chat: an edge splits, the middle moves the tab in. The
        // nearest edge wins, measured against its zone's size so the top
        // and bottom zones count as much as the sides on a wide chat.
        var chat = new Rect(box.X, stripBox.Bottom, box.Width, Math.Max(0, box.Bottom - stripBox.Bottom));
        var (edgeX, edgeY) = (Math.Max(40, chat.Width * EdgeShare), Math.Max(30, chat.Height * EdgeShare));
        var edges = new (Edge Edge, double Score)[]
        {
            (Edge.Left, (at.X - chat.X) / edgeX), (Edge.Right, (chat.Right - at.X) / edgeX),
            (Edge.Top, (at.Y - chat.Y) / edgeY), (Edge.Bottom, (chat.Bottom - at.Y) / edgeY),
        };
        foreach (var (edge, _) in edges.Where(e => e.Score < 1).OrderBy(e => e.Score))
        {
            if (edge is Edge.Left or Edge.Right)
            {
                // A new column goes before or after this chat's whole column.
                var stack = _vm.StackOf(column);
                var index = _vm.Columns.IndexOf(edge == Edge.Left ? stack[0] : stack[^1]) + (edge == Edge.Left ? 0 : 1);
                if (!CanSplitInto(tab, index)) continue;
                var half = new Rect(edge == Edge.Left ? box.X : box.Center.X, box.Y, box.Width / 2, box.Height);
                return new TabDropTarget(column, index, true, half);
            }
            var below = edge == Edge.Bottom;
            if (!CanStackInto(tab, column, below)) continue;
            var halfHeight = new Rect(box.X, below ? box.Center.Y : box.Y, box.Width, box.Height / 2);
            return new TabDropTarget(column, _vm.Columns.IndexOf(column), false, halfHeight, below);
        }
        if (column.Tabs.Contains(tab)) return null;
        return new TabDropTarget(column, column.Tabs.Count, false, box.Deflate(2));
    }

    private enum Edge { Left, Right, Top, Bottom }

    private static double Distance(Rect box, Point at)
    {
        var dx = Math.Max(0, Math.Max(box.X - at.X, at.X - box.Right));
        var dy = Math.Max(0, Math.Max(box.Y - at.Y, at.Y - box.Bottom));
        return dx * dx + dy * dy;
    }

    private bool CanStackInto(ChannelTabViewModel tab, ChatColumnViewModel anchor, bool below)
    {
        if (_vm is null || !_vm.CanStack(tab, anchor, below)) return false;
        var stack = _vm.StackOf(anchor);
        var adds = _vm.ColumnOf(tab) is { } source && source.Tabs.Count == 1 && stack.Contains(source) ? 0 : 1;
        return ChatPane.Bounds.Height / (stack.Count + adds) >= MinStackHeight;
    }

    private bool CanSplitInto(ChannelTabViewModel tab, int at)
    {
        if (_vm is null || !_vm.CanSplit(tab, at)) return false;
        // A lone tab moving its column doesn't add one; anything else must
        // leave every column at least MinColumnWidth.
        var adds = _vm.ColumnOf(tab) is { } source && source.Tabs.Count == 1 && _vm.StackOf(source).Count == 1 ? 0 : 1;
        return ChatPane.Bounds.Width / (_vm.VisibleColumnCount + adds) >= MinColumnWidth;
    }

    private Rect BoxOf(Visual visual) =>
        visual.TranslatePoint(default, ChatPane) is { } origin ? new Rect(origin, visual.Bounds.Size) : default;
}
