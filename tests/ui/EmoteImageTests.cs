// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.AvaloniaApp.Views.Emotes;
using Xunit;

namespace StreamEdge.UiTests;

public class EmoteImageTests(HeadlessAvalonia ui) : UiTest(ui)
{
    /// <summary>A 1×1 animated GIF: black for 100 ms, then white for 200 ms.
    /// Hand-assembled so the test needs no network or fixture files.</summary>
    private static readonly byte[] TwoFrameGif =
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, // GIF89a, 1×1, 2-colour table
        0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,                                           // black, white
        0x21, 0xFF, 0x0B, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E',
        (byte)'2', (byte)'.', (byte)'0', 0x03, 0x01, 0x00, 0x00, 0x00,                // loop forever
        0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00,                               // 100 ms
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, // pixel 0
        0x21, 0xF9, 0x04, 0x00, 0x14, 0x00, 0x00, 0x00,                               // 200 ms
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x4C, 0x01, 0x00, // pixel 1
        0x3B,
    ];

    [Fact]
    public void DecodesEveryFrameOfAnAnimatedGifWithItsTiming() => Ui(() =>
    {
        var image = EmoteImageCache.Decode(TwoFrameGif);

        Assert.NotNull(image);
        Assert.True(image.IsAnimated);
        Assert.Equal([100, 200], image.DurationsMs);
        Assert.Equal(300, image.TotalMs);
        Assert.Same(image.Frames[0], image.FrameAt(50));
        Assert.Same(image.Frames[1], image.FrameAt(150));
        Assert.Same(image.Frames[0], image.FrameAt(310));
    });

    [Fact]
    public void GarbageIsNotAnImage() => Ui(() =>
        Assert.Null(EmoteImageCache.Decode([1, 2, 3, 4])));

    [Fact]
    public void TallAnimatedImagesAreShrunkWhenDecoded() => Ui(() =>
    {
        // The two-frame GIF on a 512×512 canvas, like a GIF from GIPHY:
        // every frame is kept, so they're kept small.
        var tall = TwoFrameGif.ToArray();
        (tall[6], tall[7], tall[8], tall[9]) = (0x00, 0x02, 0x00, 0x02);
        var image = EmoteImageCache.Decode(tall)!;
        Assert.True(image.IsAnimated);
        Assert.Equal(new Avalonia.PixelSize(256, 256), image.PixelSize);

        // Emote-sized images are left alone.
        using var small = new SkiaSharp.SKBitmap(112, 112);
        using var smallPng = small.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        Assert.Equal(new Avalonia.PixelSize(112, 112), EmoteImageCache.Decode(smallPng.ToArray())!.PixelSize);
    });

    [Fact]
    public void StillPicturesKeepTheirOwnSize() => Ui(() =>
    {
        static Avalonia.PixelSize Decoded(int width, int height)
        {
            using var bitmap = new SkiaSharp.SKBitmap(width, height);
            bitmap.Erase(SkiaSharp.SKColors.Purple);
            using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return EmoteImageCache.Decode(png.ToArray())!.PixelSize;
        }

        // An offline banner as Twitch asks for it: it fills the player
        // pane, where a 256-pixel copy stretched back up was soft.
        Assert.Equal(new Avalonia.PixelSize(1920, 1080), Decoded(1920, 1080));
        // A tall About panel image, shown at its own size.
        Assert.Equal(new Avalonia.PixelSize(320, 600), Decoded(320, 600));
        // Far past any screen: held to 4K's height, in proportion.
        Assert.Equal(new Avalonia.PixelSize(2880, 2160), Decoded(4000, 3000));
    });
}
