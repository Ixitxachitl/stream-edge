// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace StreamEdge.AvaloniaApp.Views.Emotes;

/// <summary>Draws an emote or badge at a fixed height, animating it if it
/// has frames, with zero-width emotes (7TV/BTTV overlays) drawn on top,
/// centred. Sits inside chat lines as an InlineUIContainer, and in the emote
/// menu (via <see cref="Source"/>).
///
/// One shared timer drives every animated emote on screen, rather than a
/// timer each: a busy chat can show hundreds.</summary>
public sealed class EmoteView : Control
{
    public static readonly StyledProperty<double> EmoteHeightProperty =
        AvaloniaProperty.Register<EmoteView, double>(nameof(EmoteHeight), 28);

    /// <summary>The image, for XAML use; code passes it to the constructor.</summary>
    public static readonly StyledProperty<Uri?> SourceProperty =
        AvaloniaProperty.Register<EmoteView, Uri?>(nameof(Source));

    static EmoteView()
    {
        AffectsMeasure<EmoteView>(EmoteHeightProperty);
    }

    private IReadOnlyList<Uri> _overlays;
    private EmoteImage? _image;
    private EmoteImage?[] _overlayImages;

    public EmoteView() : this(null)
    {
    }

    public EmoteView(Uri? url, IReadOnlyList<Uri>? overlays = null, string? tooltip = null)
    {
        _overlays = overlays ?? [];
        _overlayImages = new EmoteImage?[_overlays.Count];
        if (tooltip is not null) ToolTip.SetTip(this, tooltip);
        if (url is not null) Source = url;
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

    public Uri? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SourceProperty) return;
        _image = null;
        if (change.NewValue is Uri url)
        {
            _image = EmoteImageCache.TryGet(url);
            if (_image is null) Load(url);
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Shown (dimmed) in place of an image that couldn't be loaded,
    /// usually the emote's code, so the message still reads.</summary>
    public string? FallbackText { get; set; }

    private bool _failed;

    private void Load(Uri url) => _ = LoadAsync(url, image =>
    {
        if (Source != url) return;
        _image = image;
        _failed = image is null;
        if (image is null)
            // Try again while still on screen; the cache lets a retry through
            // once its back-off has passed.
            DispatcherTimer.RunOnce(() =>
            {
                if (IsAttached && Source == url && _image is null) Load(url);
            }, EmoteImageCache.RetryAfter + TimeSpan.FromSeconds(1));
    });

    private FormattedText? Fallback() =>
        _failed && _image is null && FallbackText is { Length: > 0 } text
            ? new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 13, new SolidColorBrush(Color.Parse("#8E8E99")))
            : null;

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
        if (Fallback() is { } text) return new Size(text.Width + 2, Math.Max(text.Height, EmoteHeight * 0.7));
        var h = EmoteHeight;
        // Overlays are centred on the base emote; a wider overlay widens it.
        var w = Math.Max(WidthFor(_image, h), _overlayImages.Select(o => WidthFor(o, h)).DefaultIfEmpty(0).Max());
        // In a fixed-width cell (the emote menu), a wide emote scales down
        // to fit rather than overflow.
        if (double.IsFinite(availableSize.Width) && w > availableSize.Width && w > 0)
            return new Size(availableSize.Width, h * availableSize.Width / w);
        return new Size(w, h);
    }

    public override void Render(DrawingContext context)
    {
        if (Fallback() is { } text)
        {
            context.DrawText(text, new Point(1, (Bounds.Height - text.Height) / 2));
            return;
        }
        var now = Animator.ElapsedMs;
        Draw(context, _image, now);
        foreach (var overlay in _overlayImages) Draw(context, overlay, now);
    }

    private void Draw(DrawingContext context, EmoteImage? image, long now)
    {
        if (image is null) return;
        // Scaled to the space actually given (smaller in a narrow cell).
        var h = Math.Min(EmoteHeight, Bounds.Height);
        var w = WidthFor(image, h);
        if (w > Bounds.Width && w > 0)
        {
            h *= Bounds.Width / w;
            w = Bounds.Width;
        }
        var rect = new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
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
