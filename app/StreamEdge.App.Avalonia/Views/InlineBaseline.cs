// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>Text inside a text flow (a clickable name, mention or link) that
/// sits on the flow's baseline. An embedded control is otherwise placed by
/// its bottom edge, which lifts its text by the font's descent.</summary>
public sealed class InlineTextBlock : TextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        if (TextLayout.TextLines.Count > 0)
            SetBaselineOffset(this, Padding.Top + TextLayout.TextLines[0].Baseline);
        return size;
    }
}

/// <summary>A picture in a text flow (badge, emote, icon) centred on the
/// text rather than on the line, as twitch.tv's <c>vertical-align: middle</c>:
/// a tall GIF elsewhere on the line doesn't pull it away from the words.</summary>
public sealed class InlineMiddle : Decorator
{
    /// <summary>How far above the baseline the middle of lowercase text is,
    /// as a share of the font size (half the x-height, near enough).</summary>
    public const double TextMiddle = 0.3;

    public InlineMiddle(Control child) => Child = child;

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        var em = GetValue(TextElement.FontSizeProperty);
        TextBlock.SetBaselineOffset(this, size.Height / 2 + em * TextMiddle);
        return size;
    }
}
