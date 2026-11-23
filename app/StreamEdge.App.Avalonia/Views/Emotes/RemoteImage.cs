// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace StreamEdge.AvaloniaApp.Views.Emotes;

/// <summary>A picture from a URL (offline banners, avatars), loaded and
/// disk-cached through <see cref="EmoteImageCache"/>. Draws nothing until
/// it arrives, so whatever is behind it shows meanwhile.</summary>
public sealed class RemoteImage : Control
{
    public static readonly StyledProperty<Uri?> SourceProperty =
        AvaloniaProperty.Register<RemoteImage, Uri?>(nameof(Source));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<RemoteImage, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    static RemoteImage()
    {
        AffectsRender<RemoteImage>(StretchProperty);
        ClipToBoundsProperty.OverrideDefaultValue<RemoteImage>(true);
    }

    public RemoteImage()
    {
        // Pictures are kept at their own size and drawn smaller (a 1080p
        // banner in the pane, a 300-pixel avatar at 88): scaled smoothly.
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    private EmoteImage? _image;

    /// <summary>The picture being fetched, so it isn't asked for twice.</summary>
    private Uri? _loading;

    /// <summary>The longest wait between tries for a picture that won't load.</summary>
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    public Uri? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SourceProperty) return;
        _image = null;
        InvalidateMeasure();
        InvalidateVisual();
        _loading = null;
        if (change.NewValue is Uri url) Load(url);
    }

    /// <summary>Back on show with its picture still missing (the last try
    /// failed, or it was taken off show while waiting to try again).</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _onShow = true;
        if (_image is null && Source is { } url) Load(url);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _onShow = false;
    }

    private bool _onShow;

    private void Load(Uri url)
    {
        _image = EmoteImageCache.TryGet(url);
        if (_image is not null)
        {
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }
        if (_loading == url) return;
        _loading = url;
        _ = Task.Run(async () =>
        {
            // A download that fails (a slow or dropped connection) is tried
            // again, less and less often, for as long as this is on show
            // wanting that picture: an offline banner otherwise stayed blank
            // until its tab was left and come back to.
            for (var wait = EmoteImageCache.RetryAfter; ;
                 wait = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds * 2, 1, MaxRetry.TotalSeconds)))
            {
                var image = await EmoteImageCache.GetAsync(url);
                if (image is not null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (Source != url) return;
                        _loading = null;
                        _image = image;
                        // Without a set size (About panel images), it takes the
                        // picture's, which is only known now.
                        InvalidateMeasure();
                        InvalidateVisual();
                    });
                    return;
                }
                // The cache refuses to try sooner than RetryAfter.
                await Task.Delay(wait + TimeSpan.FromMilliseconds(50));
                var wanted = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Source == url && _onShow) return true;
                    if (_loading == url) _loading = null;
                    return false;
                });
                if (!wanted) return;
            }
        });
    }

    /// <summary>With no size set, the picture's own size, scaled down to fit
    /// the width it's given (keeping its shape); nothing until it has loaded.
    /// Avatars and banners set their size, or are stretched to fill.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_image?.Frames[0] is not { } frame || frame.Size.Width <= 0 || frame.Size.Height <= 0) return default;
        var size = frame.Size;
        if (double.IsFinite(availableSize.Width) && size.Width > availableSize.Width)
            size = new Size(availableSize.Width, size.Height * availableSize.Width / size.Width);
        if (double.IsFinite(availableSize.Height) && size.Height > availableSize.Height)
            size = new Size(size.Width * availableSize.Height / size.Height, availableSize.Height);
        return size;
    }

    public override void Render(DrawingContext context)
    {
        if (_image?.Frames[0] is not { } frame) return;
        var source = new Rect(frame.Size);
        var bounds = new Rect(Bounds.Size);
        var scale = Stretch == Stretch.UniformToFill
            ? Math.Max(bounds.Width / source.Width, bounds.Height / source.Height)
            : Math.Min(bounds.Width / source.Width, bounds.Height / source.Height);
        var size = source.Size * scale;
        var dest = new Rect((bounds.Width - size.Width) / 2, (bounds.Height - size.Height) / 2, size.Width, size.Height);
        context.DrawImage(frame, source, dest);
    }
}
