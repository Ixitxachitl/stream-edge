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

    private EmoteImage? _image;

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
        InvalidateVisual();
        if (change.NewValue is not Uri url) return;
        _image = EmoteImageCache.TryGet(url);
        if (_image is not null) return;
        _ = Task.Run(async () =>
        {
            var image = await EmoteImageCache.GetAsync(url);
            Dispatcher.UIThread.Post(() =>
            {
                if (Source != url) return;
                _image = image;
                InvalidateVisual();
            });
        });
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
