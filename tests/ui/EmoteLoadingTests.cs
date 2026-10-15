// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using SkiaSharp;
using StreamEdge.AvaloniaApp.Views.Emotes;
using Xunit;

namespace StreamEdge.UiTests;

public class EmoteLoadingTests(HeadlessAvalonia ui) : UiTest(ui)
{
    [Theory]
    [InlineData("https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/2.0", "https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/1.0")]
    [InlineData("https://static-cdn.jtvnw.net/badges/v1/5527c58c-fb7d-422d-b71b-f309dcb85cc1/2", "https://static-cdn.jtvnw.net/badges/v1/5527c58c-fb7d-422d-b71b-f309dcb85cc1/1")]
    [InlineData("https://cdn.7tv.app/emote/60ae7316f7c927fad14e6ca2/2x.webp", "https://cdn.7tv.app/emote/60ae7316f7c927fad14e6ca2/1x.webp")]
    [InlineData("https://cdn.betterttv.net/emote/5f1b0186cf6d2144653d2970/2x", "https://cdn.betterttv.net/emote/5f1b0186cf6d2144653d2970/1x")]
    [InlineData("https://cdn.frankerfacez.com/emote/381875/2", "https://cdn.frankerfacez.com/emote/381875/1")]
    [InlineData("https://cdn.frankerfacez.com/emote/720507/animated/2.webp", "https://cdn.frankerfacez.com/emote/720507/animated/1.webp")]
    // Not an emote CDN pattern: untouched.
    [InlineData("https://static-cdn.jtvnw.net/jtv_user_pictures/x-profile_image-300x300.png", "https://static-cdn.jtvnw.net/jtv_user_pictures/x-profile_image-300x300.png")]
    [InlineData("https://example.com/emote/2x", "https://example.com/emote/2x")]
    public void AtNormalScalingTheSmallerImagesAreUsed(string from, string to) =>
        Assert.Equal(to, EmoteImageCache.ForScale(new Uri(from), 1.0).AbsoluteUri);

    [Fact]
    public void HighDpiKeepsTheSharperImages()
    {
        var url = new Uri("https://cdn.7tv.app/emote/x/2x.webp");
        Assert.Same(url, EmoteImageCache.ForScale(url, 2.0));
    }

    [Fact]
    public void AFailedImageIsRetriedNotLeftBlankForever() => Ui(() =>
    {
        // A port with nothing listening yet: the first load fails.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var url = new Uri($"http://127.0.0.1:{port}/emote-{Guid.NewGuid():N}.png");

        var saved = EmoteImageCache.RetryAfter;
        EmoteImageCache.RetryAfter = TimeSpan.Zero;
        try
        {
            Assert.Null(EmoteImageCache.GetAsync(url).GetAwaiter().GetResult());

            // The server comes up: the next request must try again and succeed.
            using var server = ServeOnePng(port);
            var image = EmoteImageCache.GetAsync(url).GetAwaiter().GetResult();
            Assert.NotNull(image);
        }
        finally
        {
            EmoteImageCache.RetryAfter = saved;
        }
    });

    /// <summary>Answers one HTTP request with a 2×2 PNG.</summary>
    private static IDisposable ServeOnePng(int port)
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Red);
        using var png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        var body = png.ToArray();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
        });
        return new Stopper(listener);
    }

    private sealed class Stopper(TcpListener listener) : IDisposable
    {
        public void Dispose() => listener.Stop();
    }
}
