// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>The app's three-pane arrangement: stream list | player | chat
/// (or chat | player | list with <see cref="ChatOnLeft"/>).
///
/// On a short, wide display the player is the one thing whose size is fixed
/// by the content: it gets exactly the width its aspect ratio needs at the
/// full window height, so video fills the height with no letterbox bars. The
/// side panes share whatever width is left, split by <see cref="ChatShare"/>.
/// A grid with star columns can't express "width follows height", which is
/// why this is a panel rather than a Grid.</summary>
/// <remarks>Children are always chat, player, list, whichever side chat is
/// drawn on. When the window is too narrow to give both sides
/// <see cref="MinSideWidth"/>, the player shrinks (and letterboxes) rather
/// than the sides vanishing.</remarks>
public sealed class UltrawideLayout : Panel
{
    public static readonly StyledProperty<double> PlayerAspectProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(PlayerAspect), 16.0 / 9.0);

    public static readonly StyledProperty<double> MinSideWidthProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(MinSideWidth), 240);

    public static readonly StyledProperty<double> ChatShareProperty =
        AvaloniaProperty.Register<UltrawideLayout, double>(nameof(ChatShare), 0.5,
            coerce: (_, v) => Math.Clamp(v, 0, 1));

    public static readonly StyledProperty<bool> ShowPlayerProperty =
        AvaloniaProperty.Register<UltrawideLayout, bool>(nameof(ShowPlayer), true);

    public static readonly StyledProperty<bool> ChatOnLeftProperty =
        AvaloniaProperty.Register<UltrawideLayout, bool>(nameof(ChatOnLeft));

    static UltrawideLayout()
    {
        AffectsMeasure<UltrawideLayout>(PlayerAspectProperty, MinSideWidthProperty,
            ChatShareProperty, ShowPlayerProperty);
        AffectsArrange<UltrawideLayout>(ChatOnLeftProperty);
    }

    public double PlayerAspect
    {
        get => GetValue(PlayerAspectProperty);
        set => SetValue(PlayerAspectProperty, value);
    }

    public double MinSideWidth
    {
        get => GetValue(MinSideWidthProperty);
        set => SetValue(MinSideWidthProperty, value);
    }

    public double ChatShare
    {
        get => GetValue(ChatShareProperty);
        set => SetValue(ChatShareProperty, value);
    }

    public bool ShowPlayer
    {
        get => GetValue(ShowPlayerProperty);
        set => SetValue(ShowPlayerProperty, value);
    }

    public bool ChatOnLeft
    {
        get => GetValue(ChatOnLeftProperty);
        set => SetValue(ChatOnLeftProperty, value);
    }

    /// <summary>Widths of chat, player and list for a given panel size.
    /// Public so tests can check the arithmetic without rendering.</summary>
    public static (double Chat, double Player, double List) Split(
        Size size, double aspect, double minSide, double chatShare, bool showPlayer)
    {
        var player = 0.0;
        if (showPlayer && double.IsFinite(size.Height))
            // Whole pixels, so layout rounding can't round all three panes up
            // and overflow the window by one.
            player = Math.Floor(Math.Max(0, Math.Min(size.Height * aspect, size.Width - 2 * minSide)));
        var sides = Math.Max(0, size.Width - player);
        var chat = Math.Round(sides * chatShare);
        return (chat, player, sides - chat);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Infinite width (inside a ScrollViewer, say) has no sensible split;
        // fall back to the player at its natural width plus two minimum sides.
        var width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : 2 * MinSideWidth + (double.IsFinite(availableSize.Height) ? availableSize.Height * PlayerAspect : 0);
        var size = new Size(width, availableSize.Height);
        var (chat, player, list) = Split(size, PlayerAspect, MinSideWidth, ChatShare, ShowPlayer);
        var widths = new[] { chat, player, list };
        for (var i = 0; i < Children.Count; i++)
            Children[i].Measure(new Size(i < 3 ? widths[i] : 0, availableSize.Height));
        return size.WithHeight(double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (chat, player, list) = Split(finalSize, PlayerAspect, MinSideWidth, ChatShare, ShowPlayer);
        var widths = new[] { chat, player, list };
        // Left-to-right drawing order, as child indices.
        int[] order = ChatOnLeft ? [0, 1, 2] : [2, 1, 0];
        var x = 0.0;
        foreach (var i in order)
        {
            if (i >= Children.Count) continue;
            Children[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i];
        }
        for (var i = 3; i < Children.Count; i++) Children[i].Arrange(default);
        return finalSize;
    }
}
