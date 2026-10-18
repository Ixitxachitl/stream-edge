// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace StreamEdge.AvaloniaApp.Views.Emotes;

/// <summary>A decoded emote or badge: one frame for static images, all of
/// them (with their durations) for animated GIF/WebP.</summary>
public sealed class EmoteImage
{
    public EmoteImage(IReadOnlyList<Bitmap> frames, IReadOnlyList<int> durationsMs)
    {
        Frames = frames;
        DurationsMs = durationsMs;
        TotalMs = Math.Max(1, durationsMs.Sum());
    }

    public IReadOnlyList<Bitmap> Frames { get; }
    public IReadOnlyList<int> DurationsMs { get; }
    public int TotalMs { get; }
    public bool IsAnimated => Frames.Count > 1;
    public PixelSize PixelSize => Frames[0].PixelSize;

    /// <summary>The frame to show at <paramref name="ms"/> into the loop.</summary>
    public Bitmap FrameAt(long ms)
    {
        if (!IsAnimated) return Frames[0];
        var t = (int)(ms % TotalMs);
        for (var i = 0; i < Frames.Count; i++)
        {
            t -= DurationsMs[i];
            if (t < 0) return Frames[i];
        }
        return Frames[^1];
    }
}

/// <summary>Downloads and decodes emote and badge images once per URL, for
/// every chat line that shows them. Bytes are also kept on disk (under the
/// app's data folder), so a restart doesn't refetch every emote in the chat.</summary>
public static class EmoteImageCache
{
    // Large animated emotes can have hundreds of frames; past this they loop
    // early rather than hold that much memory per emote.
    private const int MaxFrames = 240;

    /// <summary>Tallest frame kept, in pixels. Emotes are far below it; GIFs
    /// from GIPHY come full-size, and every frame is kept decoded, so they
    /// are shrunk to this (still sharp at chat's 100-pixel GIF height on a
    /// 2x display).</summary>
    private const int MaxDecodeHeight = 256;

    // A slow connection (tens of KB/s) can still fetch a 50 KB emote; it
    // just can't fetch fifty at once inside 20 s, which is what used to happen.
    private static readonly HttpClient Http = CreateHttp();
    private static readonly ConcurrentDictionary<Uri, Lazy<Task<EmoteImage?>>> Loaded = new();
    private static readonly ConcurrentDictionary<Uri, DateTimeOffset> FailedAt = new();

    /// <summary>Downloads at once. A busy chat asks for dozens of images
    /// together; started all at once they share the connection, each crawls,
    /// and many time out. A few at a time, each one finishes.</summary>
    private static readonly SemaphoreSlim Downloads = new(6);

    /// <summary>A failed image is tried again after this long, rather than
    /// staying blank until the app restarts.</summary>
    public static TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The display's scaling, set by the window: at 1 (100%) or
    /// 1.25, the 1x images are pixel-exact and two to three times smaller
    /// than the 2x ones the emote APIs are asked for.</summary>
    public static double Scale { get; set; } = 1;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("StreamEdge/0.1");
        return http;
    }

    /// <summary>Already decoded, or null (and then <see cref="GetAsync"/>).</summary>
    public static EmoteImage? TryGet(Uri url) =>
        Loaded.TryGetValue(ForScale(url, Scale), out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully
            ? lazy.Value.Result
            : null;

    /// <summary>For tests: an image as if already downloaded.</summary>
    public static void Put(Uri url, EmoteImage image) =>
        Loaded[ForScale(url, Scale)] = new Lazy<Task<EmoteImage?>>(Task.FromResult<EmoteImage?>(image));

    /// <summary>The image, or null if it can't be had right now; a later
    /// call (after <see cref="RetryAfter"/>) tries again.</summary>
    public static async Task<EmoteImage?> GetAsync(Uri url)
    {
        url = ForScale(url, Scale);
        if (FailedAt.TryGetValue(url, out var failed))
        {
            if (DateTimeOffset.UtcNow - failed < RetryAfter) return null;
            FailedAt.TryRemove(url, out _);
            Loaded.TryRemove(url, out _);
        }
        // Not back on the caller's thread: a caller that blocks on this (the
        // UI thread in a test) would otherwise wait on itself forever.
        var image = await Loaded.GetOrAdd(url, u => new Lazy<Task<EmoteImage?>>(() => Task.Run(() => LoadAsync(u)))).Value
            .ConfigureAwait(false);
        if (image is null) FailedAt[url] = DateTimeOffset.UtcNow;
        return image;
    }

    /// <summary>The 1x version of a 2x emote or badge URL, for displays at up
    /// to 125% scaling. Only rewrites the CDNs whose URL patterns are known;
    /// anything else is left alone. Public for tests.</summary>
    public static Uri ForScale(Uri url, double scale)
    {
        if (scale > 1.25) return url;
        var s = url.AbsoluteUri;
        var rewritten = url.Host switch
        {
            // …/emoticons/v2/{id}/{format}/{theme}/2.0 ; badges …/badges/v1/{id}/2
            "static-cdn.jtvnw.net" => Swap(Swap(s, "/2.0", "/1.0"), "/2", "/1"),
            "cdn.7tv.app" => Swap(s, "/2x.webp", "/1x.webp"),
            "cdn.betterttv.net" => Swap(Swap(s, "/2x.webp", "/1x.webp"), "/2x", "/1x"),
            // …/emote/{id}/2, …/emote/{id}/animated/2.webp, …/badge/{id}/2
            "cdn.frankerfacez.com" => Swap(Swap(s, "/2.webp", "/1.webp"), "/2", "/1"),
            _ => s,
        };
        return rewritten == s ? url : new Uri(rewritten);

        static string Swap(string s, string end, string with) =>
            s.EndsWith(end, StringComparison.Ordinal) ? s[..^end.Length] + with : s;
    }

    private static async Task<EmoteImage?> LoadAsync(Uri url)
    {
        try
        {
            var bytes = await ReadAsync(url).ConfigureAwait(false);
            return bytes is null ? null : Decode(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Trace.WriteLine($"emote {url}: {ex.Message}");
            return null;
        }
    }

    private static async Task<byte[]?> ReadAsync(Uri url)
    {
        var dir = AppData.PathFor("cache/images");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri))));
        if (File.Exists(file)) return await File.ReadAllBytesAsync(file).ConfigureAwait(false);

        await Downloads.WaitAsync().ConfigureAwait(false);
        byte[] bytes;
        try
        {
            using var response = await Http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        finally
        {
            Downloads.Release();
        }
        var temp = file + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
        File.Move(temp, file, overwrite: true);
        return bytes;
    }

    /// <summary>PNG, GIF or WebP (static or animated) into Avalonia bitmaps.
    /// Public for tests.</summary>
    public static EmoteImage? Decode(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        // Frames are decoded at full size (later frames draw over earlier
        // ones) and kept at most MaxDecodeHeight tall.
        var shrink = info.Height > MaxDecodeHeight;
        var kept = shrink
            ? new SKImageInfo(Math.Max(1, info.Width * MaxDecodeHeight / info.Height), MaxDecodeHeight, SKColorType.Bgra8888, SKAlphaType.Premul)
            : info;
        var frameInfos = codec.FrameInfo;
        var count = Math.Clamp(frameInfos.Length, 1, MaxFrames);
        var decoded = new SKBitmap[count];
        var durations = new int[count];
        var frames = new Bitmap[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                var bitmap = new SKBitmap(info);
                var required = frameInfos.Length > 0 ? frameInfos[i].RequiredFrame : -1;
                // A frame that only draws a delta over an earlier one starts
                // from that frame's pixels.
                if (required >= 0 && required < i) decoded[required].CopyTo(bitmap);
                else bitmap.Erase(SKColors.Transparent);
                var result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(i, required < i ? required : -1));
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    bitmap.Dispose();
                    if (i == 0) return null;
                    count = i;
                    break;
                }
                decoded[i] = bitmap;
                // Browsers treat very short GIF delays as 100 ms; so do we.
                var duration = frameInfos.Length > 0 ? frameInfos[i].Duration : 0;
                durations[i] = duration <= 10 ? 100 : duration;
                if (shrink)
                {
                    using var scaled = bitmap.Resize(kept, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    frames[i] = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, scaled.GetPixels(),
                        new PixelSize(kept.Width, kept.Height), new Vector(96, 96), scaled.RowBytes);
                }
                else
                {
                    frames[i] = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, bitmap.GetPixels(),
                        new PixelSize(info.Width, info.Height), new Vector(96, 96), bitmap.RowBytes);
                }
            }
            return new EmoteImage(frames[..count], durations[..count]);
        }
        finally
        {
            foreach (var bitmap in decoded) bitmap?.Dispose();
        }
    }
}
