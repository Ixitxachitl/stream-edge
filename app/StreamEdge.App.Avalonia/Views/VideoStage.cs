// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>Holds one <see cref="PlayerSurface"/> per open tab and decides
/// where each goes: the front tab's fills the stage at 16:9; with
/// <see cref="ShowPip"/> the others line up as small live thumbnails along
/// the bottom right, over the main video; without it they're hidden (their
/// tabs keep playing audio).
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

    static VideoStage()
    {
        AffectsArrange<VideoStage>(ShowPipProperty, AspectProperty);
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

    private const double PipMargin = 8;

    /// <summary>Thumbnail height for a stage: about a quarter of it, within
    /// limits that stay readable on a 440-pixel bar and small on a big one.</summary>
    public static double PipHeight(double stageHeight) => Math.Round(Math.Clamp(stageHeight * 0.24, 54, 160));

    /// <summary>Where each tab's video goes. Rect.Empty-sized (default) means
    /// hidden. Public so tests can check placement without native windows.</summary>
    public static IReadOnlyList<Rect> Place(Size stage, double aspect, bool showPip, IReadOnlyList<bool> isFront)
    {
        var rects = new Rect[isFront.Count];
        var h = PipHeight(stage.Height);
        var w = Math.Round(h * aspect);
        var x = stage.Width - PipMargin - w;
        var y = stage.Height - PipMargin - h;
        for (var i = 0; i < isFront.Count; i++)
        {
            if (isFront[i])
            {
                rects[i] = AspectRatioBox.Fit(stage, aspect);
            }
            else if (showPip && x >= PipMargin)
            {
                rects[i] = new Rect(x, y, w, h);
                x -= w + PipMargin;
            }
        }
        return rects;
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
        var surfaces = Children.OfType<PlayerSurface>().ToList();
        var fronts = surfaces.Select(s => s.DataContext is ChannelTabViewModel { IsFront: true }).ToList();
        // Nothing to show (offline, failed, not started): the native window
        // stays hidden, or it would cover the offline banner in black. Only
        // the videos with something to show get places, so thumbnails don't
        // leave gaps.
        var showing = Enumerable.Range(0, surfaces.Count)
            .Where(i => surfaces[i].DataContext is ChannelTabViewModel { Player.IsVideoVisible: true })
            .ToList();
        var placed = Place(finalSize, Aspect, ShowPip, [.. showing.Select(i => fronts[i])]);
        var rects = new Rect[surfaces.Count];
        for (var k = 0; k < showing.Count; k++) rects[showing[k]] = placed[k];
        for (var i = 0; i < surfaces.Count; i++) surfaces[i].Arrange(rects[i]);
        // Thumbnails overlap the main video; native windows don't follow
        // Avalonia's drawing order, so put them on top explicitly.
        for (var i = 0; i < surfaces.Count; i++)
            if (!fronts[i] && rects[i].Width > 0) surfaces[i].BringToFront();
        return finalSize;
    }
}
