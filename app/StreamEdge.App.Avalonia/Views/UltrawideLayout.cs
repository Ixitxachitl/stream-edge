// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>The app's three-pane arrangement: stream list | player | chat
/// (or chat | player | list with <see cref="ChatOnLeft"/>), with a draggable
/// <see cref="PaneGrip"/> on each side of the player.
///
/// The player is the one thing whose size is fixed by the content: exactly
/// 16:9 at the full window height, so the video fills the height with no bars.
/// The side panes share whatever is left, split by <see cref="ChatShare"/>.
/// Dragging either grip slides the player sideways (its size never changes),
/// so one side grows by exactly what the other loses. The split is kept as a
/// share, so resizing the window keeps the sides in proportion.
/// A grid with star columns can't express "width follows height", which is
/// why this is a panel rather than a Grid.</summary>
/// <remarks>Children: chat, player, list (always in that order, whichever
/// side chat is drawn on), then two <see cref="PaneGrip"/>s, left then right.
/// Only when the window is too narrow to give both sides
/// <see cref="MinSideWidth"/> does the player shrink (and letterbox).</remarks>
public sealed class UltrawideLayout : Panel
{
    public static readonly StyledProperty<double> PlayerAspectProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(PlayerAspect), 16.0 / 9.0);

    public static readonly StyledProperty<double> MinSideWidthProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(MinSideWidth), 180);

    /// <summary>Chat's share of the width beside the player, 0..1. Two-way:
    /// dragging a grip writes it back.</summary>
    public static readonly StyledProperty<double> ChatShareProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(ChatShare), 0.5,
            defaultBindingMode: BindingMode.TwoWay, coerce: (_, v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0.5);

    public static readonly StyledProperty<double> GripWidthProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(GripWidth), 5);

    public static readonly StyledProperty<bool> ShowPlayerProperty =
        AvaloniaProperty.Register<UltrawideLayout, bool>(nameof(ShowPlayer), true);

    public static readonly StyledProperty<bool> ChatOnLeftProperty =
        AvaloniaProperty.Register<UltrawideLayout, bool>(nameof(ChatOnLeft));

    static UltrawideLayout()
    {
        AffectsMeasure<UltrawideLayout>(PlayerAspectProperty, MinSideWidthProperty,
            ChatShareProperty, GripWidthProperty, ShowPlayerProperty);
        AffectsArrange<UltrawideLayout>(ChatOnLeftProperty);
    }

    public double PlayerAspect { get => GetValue(PlayerAspectProperty); set => SetValue(PlayerAspectProperty, value); }
    public double MinSideWidth { get => GetValue(MinSideWidthProperty); set => SetValue(MinSideWidthProperty, value); }
    public double ChatShare { get => GetValue(ChatShareProperty); set => SetValue(ChatShareProperty, value); }
    public double GripWidth { get => GetValue(GripWidthProperty); set => SetValue(GripWidthProperty, value); }
    public bool ShowPlayer { get => GetValue(ShowPlayerProperty); set => SetValue(ShowPlayerProperty, value); }
    public bool ChatOnLeft { get => GetValue(ChatOnLeftProperty); set => SetValue(ChatOnLeftProperty, value); }

    public readonly record struct Input(double Aspect, double MinSide, double ChatShare, bool ShowPlayer, double Grip);

    public readonly record struct Widths(double Chat, double Player, double List)
    {
        public double Sides => Chat + List;
    }

    /// <summary>Pane widths for a panel size, grips excluded. Public so tests
    /// can check the arithmetic without rendering.</summary>
    public static Widths Split(Size size, Input p)
    {
        var avail = Math.Max(0, size.Width - 2 * p.Grip);
        // Whole pixels throughout, so layout rounding can't push the panes
        // past the window edge.
        var player = p.ShowPlayer && double.IsFinite(size.Height)
            ? Math.Floor(Math.Max(0, Math.Min(size.Height * p.Aspect, avail - 2 * p.MinSide)))
            : 0;
        var sides = avail - player;
        var chat = Math.Round(sides * p.ChatShare);
        // Neither side below its minimum, as far as there's room for both.
        if (sides >= 2 * p.MinSide) chat = Math.Clamp(chat, p.MinSide, sides - p.MinSide);
        return new Widths(chat, player, sides - chat);
    }

    private Input Current() => new(PlayerAspect, MinSideWidth, ChatShare, ShowPlayer, GripWidth);

    private IEnumerable<PaneGrip> Grips => Children.OfType<PaneGrip>();

    protected override Size MeasureOverride(Size availableSize)
    {
        // Infinite width (inside a ScrollViewer, say) has no sensible split;
        // fall back to the player at its natural width plus two minimum sides.
        var width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : 2 * MinSideWidth + 2 * GripWidth + (double.IsFinite(availableSize.Height) ? availableSize.Height * PlayerAspect : 0);
        var size = new Size(width, availableSize.Height);
        var w = Split(size, Current());
        var widths = new[] { w.Chat, w.Player, w.List };
        for (var i = 0; i < Children.Count; i++)
            Children[i].Measure(new Size(i < 3 ? widths[i] : GripWidth, availableSize.Height));
        return size.WithHeight(double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var w = Split(finalSize, Current());
        var widths = new[] { w.Chat, w.Player, w.List };
        var grips = Grips.ToList();
        // Left-to-right: side, grip, player, grip, side.
        int[] order = ChatOnLeft ? [0, 1, 2] : [2, 1, 0];
        var x = 0.0;
        for (var k = 0; k < 3; k++)
        {
            var i = order[k];
            if (i < Children.Count && Children[i] is not PaneGrip)
                Children[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i];
            if (k < 2)
            {
                if (k < grips.Count) grips[k].Arrange(new Rect(x, 0, GripWidth, finalSize.Height));
                x += GripWidth;
            }
        }
        return finalSize;
    }

    // ----- Dragging ---------------------------------------------------------

    private bool _dragging;
    private double _dragStartX;
    private Widths _dragStartWidths;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AddHandler(PointerPressedEvent, OnGripPressed, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnGripMoved, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnGripReleased, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        RemoveHandler(PointerPressedEvent, OnGripPressed);
        RemoveHandler(PointerMovedEvent, OnGripMoved);
        RemoveHandler(PointerReleasedEvent, OnGripReleased);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not PaneGrip grip || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            ResetSplit();
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartWidths = Split(Bounds.Size, Current());
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        Drag(e.GetPosition(this).X - _dragStartX);
        e.Handled = true;
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        e.Pointer.Capture(null);
        _dragging = false;
        e.Handled = true;
    }

    /// <summary>Slides the player <paramref name="dx"/> pixels right (negative:
    /// left) from where the drag started. Either grip does the same thing: the
    /// player's size is fixed, so the left side grows by what the right side
    /// loses. Public for tests.</summary>
    public void Drag(double dx)
    {
        var start = _dragging ? _dragStartWidths : Split(Bounds.Size, Current());
        if (start.Sides <= 0) return;
        var left = (ChatOnLeft ? start.Chat : start.List) + dx;
        if (start.Sides >= 2 * MinSideWidth) left = Math.Clamp(left, MinSideWidth, start.Sides - MinSideWidth);
        var chat = ChatOnLeft ? left : start.Sides - left;
        SetCurrentValue(ChatShareProperty, chat / start.Sides);
    }

    /// <summary>Back to an even split.</summary>
    public void ResetSplit() => SetCurrentValue(ChatShareProperty, 0.5);
}

/// <summary>The thin draggable strip between the player and a side pane.
/// Double-click resets to an even split.</summary>
public sealed class PaneGrip : Border
{
    public PaneGrip()
    {
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        ToolTip.SetTip(this, "Drag to move the video · double-click to center");
    }
}
