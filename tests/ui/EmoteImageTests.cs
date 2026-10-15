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
}
