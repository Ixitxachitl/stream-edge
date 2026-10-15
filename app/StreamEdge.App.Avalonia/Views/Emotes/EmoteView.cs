// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace StreamEdge.AvaloniaApp.Views.Emotes;

/// <summary>Draws an emote or badge at a fixed height, animating it if it
/// has frames, with zero-width emotes (7TV/BTTV overlays) drawn on top,
/// centred. Sits inside chat lines as an InlineUIContainer.
///
/// One shared timer drives every animated emote on screen, rather than a
/// timer each: a busy chat can show hundreds.</summary>
public sealed class EmoteView : Control
{
    public static readonly StyledProperty<double> EmoteHeightProperty =
        AvaloniaProperty.Register<EmoteView, double>(nameof(EmoteHeight), 28);

    static EmoteView()
    {
        AffectsMeasure<EmoteView>(EmoteHeightProperty);
    }

    private readonly Uri _url;
    private readonly IReadOnlyList<Uri> _overlays;
    private EmoteImage? _image;
    private EmoteImage?[] _overlayImages;

    public EmoteView(Uri url, IReadOnlyList<Uri>? overlays = null, string? tooltip = null)
    {
        _url = url;
        _overlays = overlays ?? [];
        _overlayImages = new EmoteImage?[_overlays.Count];
        if (tooltip is not null) ToolTip.SetTip(this, tooltip);
        _image = EmoteImageCache.TryGet(url);
        if (_image is null) _ = LoadAsync(url, i => _image = i);
        for (var k = 0; k < _overlays.Count; k++)
        {
            var index = k;
            _overlayImages[k] = EmoteImageCache.TryGet(_overlays[k]);
            if (_overlayImages[k] is null) _ = LoadAsync(_overlays[k], i => _overlayImages[index] = i);
        }
    }

    public double EmoteHeight
    {
        get => GetValue(EmoteHeightProperty);
        set => SetValue(EmoteHeightProperty, value);
    }

    private async Task LoadAsync(Uri url, Action<EmoteImage?> done)
    {
        var image = await EmoteImageCache.GetAsync(url);
        Dispatcher.UIThread.Post(() =>
        {
            done(image);
            InvalidateMeasure();
            InvalidateVisual();
            if (IsAnimated && IsAttached) Animator.Register(this);
        });
    }

    private bool IsAttached { get; set; }
    private bool IsAnimated => _image?.IsAnimated == true || _overlayImages.Any(o => o?.IsAnimated == true);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        IsAttached = true;
        if (IsAnimated) Animator.Register(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        IsAttached = false;
        Animator.Unregister(this);
        base.OnDetachedFromVisualTree(e);
    }

    private static double WidthFor(EmoteImage? image, double height) =>
        image is null ? height : height * image.PixelSize.Width / Math.Max(1, image.PixelSize.Height);

    protected override Size MeasureOverride(Size availableSize)
    {
        var h = EmoteHeight;
        // Overlays are centred on the base emote; a wider overlay widens it.
        var w = Math.Max(WidthFor(_image, h), _overlayImages.Select(o => WidthFor(o, h)).DefaultIfEmpty(0).Max());
        return new Size(w, h);
    }

    public override void Render(DrawingContext context)
    {
        var now = Animator.ElapsedMs;
        Draw(context, _image, now);
        foreach (var overlay in _overlayImages) Draw(context, overlay, now);
    }

    private void Draw(DrawingContext context, EmoteImage? image, long now)
    {
        if (image is null) return;
        var w = WidthFor(image, EmoteHeight);
        var rect = new Rect((Bounds.Width - w) / 2, 0, w, EmoteHeight);
        var frame = image.FrameAt(now);
        context.DrawImage(frame, new Rect(frame.Size), rect);
    }

    /// <summary>The shared animation clock: ~30 fps while anything animated
    /// is on screen, stopped otherwise.</summary>
    private static class Animator
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly HashSet<EmoteView> Views = [];
        private static DispatcherTimer? _timer;

        public static long ElapsedMs => Clock.ElapsedMilliseconds;

        public static void Register(EmoteView view)
        {
            Views.Add(view);
            if (_timer is not null) return;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
            {
                foreach (var v in Views) v.InvalidateVisual();
            });
            _timer.Start();
        }

        public static void Unregister(EmoteView view)
        {
            Views.Remove(view);
            if (Views.Count > 0 || _timer is null) return;
            _timer.Stop();
            _timer = null;
        }
    }
}
