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

    private static readonly HttpClient Http = CreateHttp();
    private static readonly ConcurrentDictionary<Uri, Lazy<Task<EmoteImage?>>> Loaded = new();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("StreamEdge/0.1");
        return http;
    }

    /// <summary>Already decoded, or null (and then <see cref="GetAsync"/>).</summary>
    public static EmoteImage? TryGet(Uri url) =>
        Loaded.TryGetValue(url, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully
            ? lazy.Value.Result
            : null;

    public static Task<EmoteImage?> GetAsync(Uri url) =>
        Loaded.GetOrAdd(url, u => new Lazy<Task<EmoteImage?>>(() => Task.Run(() => LoadAsync(u)))).Value;

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

        using var response = await Http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
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
                frames[i] = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, bitmap.GetPixels(),
                    new PixelSize(info.Width, info.Height), new Vector(96, 96), bitmap.RowBytes);
            }
            return new EmoteImage(frames[..count], durations[..count]);
        }
        finally
        {
            foreach (var bitmap in decoded) bitmap?.Dispose();
        }
    }
}
