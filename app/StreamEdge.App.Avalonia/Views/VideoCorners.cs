// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>Rounds the videos' corners smoothly, drawn over them from the
/// overlay window (videos are native windows: nothing in the main window can
/// cover them). A window's own clip (<see cref="PlayerSurface.CornerRadius"/>)
/// is all or nothing per pixel, so its curve steps; this blends it.
///
/// The main video's corners are filled with what lies behind them (the
/// window's ground where the video meets the pane's own rounded corner, the
/// pane's black elsewhere), anti-aliased along the curve; the main video
/// isn't clipped then, so the curve blends with the picture itself. A
/// thumbnail lies over the main video, so nothing solid can stand in for
/// what's behind its corners: it keeps its clip, with a thin smooth edge
/// drawn along the curve to hide the steps.</summary>
public sealed class VideoCorners : Control
{
    public const double FrontRadius = 8;
    public const double PipRadius = 6;

    /// <summary>One video: where it is (this control's coordinates), whether
    /// it's the main one, and what lies behind each of its corners (top left,
    /// top right, bottom right, bottom left).</summary>
    public readonly record struct Video(Rect Rect, bool IsFront, IBrush[] Behind);

    private IReadOnlyList<Video> _videos = [];

    private static readonly Pen PipEdge = new(new SolidColorBrush(Color.Parse("#0E0E10")), 1.5);

    public IReadOnlyList<Video> Videos
    {
        get => _videos;
        set
        {
            _videos = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        foreach (var video in _videos)
        {
            var r = video.Rect;
            if (!video.IsFront)
            {
                context.DrawRectangle(null, PipEdge, r, PipRadius, PipRadius);
                continue;
            }
            const double c = FrontRadius;
            var corners = new[]
            {
                (new Rect(r.X, r.Y, c, c), new Point(r.X + c, r.Y + c)),
                (new Rect(r.Right - c, r.Y, c, c), new Point(r.Right - c, r.Y + c)),
                (new Rect(r.Right - c, r.Bottom - c, c, c), new Point(r.Right - c, r.Bottom - c)),
                (new Rect(r.X, r.Bottom - c, c, c), new Point(r.X + c, r.Bottom - c)),
            };
            for (var i = 0; i < 4; i++)
            {
                // The corner's square, less the circle the curve belongs to.
                var (square, centre) = corners[i];
                context.DrawGeometry(video.Behind[i], null, new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(square), new EllipseGeometry(new Rect(centre.X - c, centre.Y - c, 2 * c, 2 * c))));
            }
        }
    }
}
