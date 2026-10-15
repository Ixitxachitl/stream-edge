// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>Gives its child the largest rectangle of <see cref="Aspect"/>
/// that fits, centred: the rest stays the box's background (letterbox or
/// pillarbox bars). Holds the video surface so the native window itself is
/// always 16:9, whatever the user does to the pane widths.</summary>
public sealed class AspectRatioBox : Decorator
{
    public static readonly StyledProperty<double> AspectProperty =
        AvaloniaProperty.Register<AspectRatioBox, double>(nameof(Aspect), 16.0 / 9.0);

    static AspectRatioBox()
    {
        AffectsMeasure<AspectRatioBox>(AspectProperty);
    }

    public double Aspect
    {
        get => GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    /// <summary>The fitted rectangle inside <paramref name="size"/>.</summary>
    public static Rect Fit(Size size, double aspect)
    {
        if (size.Width <= 0 || size.Height <= 0 || aspect <= 0) return default;
        // Nearest whole pixel, capped at the box: rounding down would leave a
        // 1 px bar under a pane that is already (to the pixel) 16:9.
        var w = Math.Min(size.Width, size.Height * aspect);
        var h = Math.Min(size.Height, Math.Round(w / aspect));
        w = Math.Min(size.Width, Math.Round(w));
        return new Rect(Math.Floor((size.Width - w) / 2), Math.Floor((size.Height - h) / 2), w, h);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var fit = Fit(availableSize, Aspect);
        Child?.Measure(fit.Size);
        return availableSize.Constrain(new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : fit.Width,
            double.IsFinite(availableSize.Height) ? availableSize.Height : fit.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(Fit(finalSize, Aspect));
        return finalSize;
    }
}
