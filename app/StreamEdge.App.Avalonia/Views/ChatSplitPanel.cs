// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>Lays chat splits out as columns of equal width, each a stack of
/// equal-height chats: a split whose <see cref="ChatColumnViewModel.StacksBelow"/>
/// is set goes under the one before it instead of beside it. With nothing
/// stacked it's the one-row grid it replaces.</summary>
public sealed class ChatSplitPanel : Panel
{
    public static readonly StyledProperty<double> StackGapProperty =
        AvaloniaProperty.Register<ChatSplitPanel, double>(nameof(StackGap), 3);

    static ChatSplitPanel()
    {
        AffectsMeasure<ChatSplitPanel>(StackGapProperty);
    }

    /// <summary>Space between stacked chats, so their tab strips don't run together.</summary>
    public double StackGap
    {
        get => GetValue(StackGapProperty);
        set => SetValue(StackGapProperty, value);
    }

    /// <summary>Children grouped into columns, left to right, each top to bottom.</summary>
    private List<List<Control>> Columns()
    {
        var columns = new List<List<Control>>();
        foreach (var child in Children)
        {
            var below = child.DataContext is ChatColumnViewModel { StacksBelow: true };
            if (columns.Count == 0 || !below) columns.Add([]);
            columns[^1].Add(child);
        }
        return columns;
    }

    /// <summary>Each child's box, in order. Whole pixels, so the last column
    /// and the last chat in each end exactly at the edge.</summary>
    private Dictionary<Control, Rect> Boxes(Size size)
    {
        var boxes = new Dictionary<Control, Rect>();
        var columns = Columns();
        if (columns.Count == 0) return boxes;
        for (var c = 0; c < columns.Count; c++)
        {
            var x = Math.Floor(size.Width * c / columns.Count);
            var width = Math.Floor(size.Width * (c + 1) / columns.Count) - x;
            var stack = columns[c];
            var space = Math.Max(0, size.Height - StackGap * (stack.Count - 1));
            for (var r = 0; r < stack.Count; r++)
            {
                var top = Math.Floor(space * r / stack.Count);
                var height = Math.Floor(space * (r + 1) / stack.Count) - top;
                boxes[stack[r]] = new Rect(x, top + StackGap * r, width, height);
            }
        }
        return boxes;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
        foreach (var (child, box) in Boxes(size)) child.Measure(box.Size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (child, box) in Boxes(finalSize)) child.Arrange(box);
        return finalSize;
    }
}
