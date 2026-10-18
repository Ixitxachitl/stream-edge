// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace StreamEdge.Playback.Mpv;

/// <summary><see cref="IVideoPlayer"/> over libmpv, tuned for latency.
/// libmpv's client API is thread-safe, so calls may come from any thread.</summary>
public sealed class MpvPlayer : IVideoPlayer, IDisposable
{
    private IntPtr _ctx;

    /// <param name="windowId">Native window to draw into (HWND, X11 window,
    /// NSView). Null lets mpv open its own window, which is only useful in tests.</param>
    public MpvPlayer(long? windowId)
    {
        _ctx = MpvNative.mpv_create();
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed.");

        if (windowId is { } wid) Option("wid", wid.ToString(CultureInfo.InvariantCulture));
        foreach (var (name, value) in Options) Option(name, value);
        // Debugging aid: STREAMEDGE_MPV_LOG=<file> writes mpv's own log there.
        if (Environment.GetEnvironmentVariable("STREAMEDGE_MPV_LOG") is { Length: > 0 } log)
        {
            Option("log-file", log);
            Option("msg-level", "all=v");
        }

        var rc = MpvNative.mpv_initialize(_ctx);
        if (rc < 0) throw new InvalidOperationException("mpv_initialize: " + MpvNative.ErrorString(rc));
    }

    /// <summary>Latency over smoothness. Applied in order, so later lines
    /// override what the low-latency profile sets.</summary>
    internal static readonly (string Name, string Value)[] Options =
    [
        // Embedded player: no mpv UI, keys, or terminal.
        ("terminal", "no"),
        ("msg-level", "all=warn"),
        ("osc", "no"),
        ("input-default-bindings", "no"),
        ("input-vo-keyboard", "no"),
        ("input-cursor", "no"),
        ("cursor-autohide", "no"),
        ("idle", "yes"),
        ("keep-open", "no"),
        ("force-window", "yes"),
        ("ytdl", "no"),
        ("audio-client-name", "StreamEdge"),
        ("hwdec", "auto-safe"),
        // Direct3D 11, mpv's own choice on Windows. Not OpenGL: mpv tries its
        // ANGLE context first, and the shinchiro builds don't ship
        // libEGL.dll, so that fails, and mpv's cleanup of the failed attempt
        // calls the never-loaded eglGetProcAddress, a null pointer, killing
        // the process on every video. The dxgi.dll crashes once blamed on
        // D3D11 came from RivaTuner's overlay hooking the swap chain resize
        // as videos switch between full size and PIP (see OverlayCheck).
        ("gpu-api", "d3d11"),

        // mpv's own low-latency preset: no audio buffer, single-threaded
        // decode, minimal demuxer probing, no frame interpolation.
        ("profile", "low-latency"),
        // ...nor its single decoding thread: a thumbnail brought to the front
        // goes from 160p to 1080p60 in place, and one thread decoding that in
        // software falls behind the audio.
        ("vd-lavc-threads", "0"),
        // ...but not its zero audio buffer: 100 ms costs nothing noticeable
        // in latency and stops audio crackling when a burst arrives late.
        ("audio-buffer", "0.1"),
        // No forced demuxer: Twitch serves MPEG-TS on some channels and
        // fragmented MP4 on others, and forcing mpegts made the MP4 ones
        // undecodable (black, reloading every few seconds).

        // Keep a demuxer cache so buffered time is measurable. After a stall,
        // resume once a second is buffered: resuming on the first frames
        // (0.3 s, as before) turned one stall into a run of micro-stutters.
        // CatchUpController treats repeated stalls as a reason to drop
        // quality, and raises this wait a little (SetResumeBuffer) while the
        // connection struggles.
        ("cache", "yes"),
        ("cache-pause", "yes"),
        // Start once a second is buffered rather than on the first frames,
        // so playback doesn't begin with no cushion at all.
        ("cache-pause-initial", "yes"),
        ("cache-pause-wait", "1.0"),
        // Live has no back-buffer worth keeping.
        ("demuxer-max-back-bytes", "0"),
        // Speed changes keep pitch (scaletempo2) instead of chipmunking.
        ("audio-pitch-correction", "yes"),
        // Drop late frames rather than fall further behind, before decoding
        // them too when the decoder is what's slow.
        ("framedrop", "decoder+vo"),

        // Twitch carries closed captions as CEA-608 inside the H.264 stream;
        // this gives every video a caption track up front, so turning CC on
        // is just selecting it (sid), with nothing to wait for.
        ("sub-create-cc-track", "yes"),
        ("sid", "no"),
        // Readable on a 480-pixel-tall bar, with a backing box like twitch.tv.
        ("sub-font-size", "34"),
        ("sub-border-style", "background-box"),
        ("sub-back-color", "#C0000000"),
        ("osd-level", "1"),
        // OSD text ("Ad break", "Volume 70%") in real pixels: scaled by
        // window height, as mpv does by default, it shrank to a few pixels
        // in a PIP thumbnail.
        ("osd-scale-by-window", "no"),
        ("osd-font-size", "22"),
        ("osd-duration", "1500"),
    ];

    /// <summary>True if libmpv can be found on this machine.</summary>
    public static bool IsAvailable(out string? error)
    {
        try
        {
            var ctx = MpvNative.mpv_create();
            if (ctx == IntPtr.Zero)
            {
                error = "mpv_create failed.";
                return false;
            }
            MpvNative.mpv_terminate_destroy(ctx);
            error = null;
            return true;
        }
        catch (DllNotFoundException)
        {
            error = OperatingSystem.IsWindows()
                ? "libmpv-2.dll not found. Run scripts/fetch-libmpv.ps1 and rebuild."
                : OperatingSystem.IsMacOS() ? "libmpv not found. Install it with: brew install mpv"
                : "libmpv not found. Install it, e.g.: sudo apt install libmpv2";
            return false;
        }
    }

    private void Option(string name, string value)
    {
        var rc = MpvNative.mpv_set_option_string(_ctx, name, value);
        // An option this build doesn't know (older mpv) shouldn't stop playback.
        if (rc < 0) System.Diagnostics.Trace.WriteLine($"mpv option {name}={value}: {MpvNative.ErrorString(rc)}");
    }

    private void Command(string command)
    {
        if (_ctx == IntPtr.Zero) return;
        MpvNative.mpv_command_string(_ctx, command);
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public void Load(Uri url) => Command("loadfile " + Quote(url.AbsoluteUri) + " replace");

    public void Stop() => Command("stop");

    public void SetSpeed(double speed)
    {
        if (_ctx == IntPtr.Zero) return;
        MpvNative.mpv_set_property_string(_ctx, "speed", speed.ToString("0.00", CultureInfo.InvariantCulture));
    }

    public void SetResumeBuffer(double seconds)
    {
        if (_ctx == IntPtr.Zero) return;
        MpvNative.mpv_set_property_string(_ctx, "cache-pause-wait", seconds.ToString("0.0", CultureInfo.InvariantCulture));
    }

    public void SetVolume(int percent)
    {
        if (_ctx == IntPtr.Zero) return;
        MpvNative.mpv_set_property_string(_ctx, "volume", Math.Clamp(percent, 0, 130).ToString(CultureInfo.InvariantCulture));
    }

    public void SetMute(bool mute)
    {
        if (_ctx == IntPtr.Zero) return;
        MpvNative.mpv_set_property_string(_ctx, "mute", mute ? "yes" : "no");
    }

    public void ShowMessage(string text) => Command("show-text " + Quote(text) + " 1500");

    public void SetCaptions(bool on)
    {
        if (_ctx == IntPtr.Zero) return;
        // Setting the property also sets the option, so it holds across the
        // reloads a resync does.
        MpvNative.mpv_set_property_string(_ctx, "sid", on ? "1" : "no");
    }

    public void SetStatusText(string? text)
    {
        if (_ctx == IntPtr.Zero) return;
        // osd-msg1 stays on screen at osd-level 1 until replaced.
        MpvNative.mpv_set_property_string(_ctx, "osd-msg1", text ?? "");
    }

    public void SetInfoStrip(InfoStrip? strip)
    {
        if (_ctx == IntPtr.Zero) return;
        if (strip is null)
        {
            Command("osd-overlay 1 none \"\"");
            return;
        }
        // osd-overlay id format data res_x res_y z hidden compute_bounds;
        // res_x 0 takes the window's aspect. First lay out just the text,
        // hidden, to learn how many lines it wrapped to, then draw the band
        // to fit behind it.
        var res = InfoStripAss.ResY.ToString(CultureInfo.InvariantCulture);
        var text = InfoStripAss.TextEvent(strip);
        var bounds = MpvNative.CommandForInts(_ctx, "osd-overlay", "1", "ass-events", text, "0", res, "0", "yes", "yes");
        var top = bounds is not null && bounds.TryGetValue("y0", out var y0) ? (int)y0 : InfoStripAss.TwoLineTop;
        MpvNative.CommandForInts(_ctx, "osd-overlay", "1", "ass-events", InfoStripAss.Build(strip, top), "0", res, "0", "no", "no");
    }

    /// <summary>Any numeric mpv property ("frame-drop-count",
    /// "decoder-frame-drop-count", "avsync"…), or null; for diagnostics.</summary>
    public double? Diagnostic(string property) =>
        _ctx != IntPtr.Zero && MpvNative.mpv_get_property(_ctx, property, MpvNative.FormatDouble, out var v) >= 0 ? v : null;

    /// <summary>A text mpv property ("hwdec-current", "video-params/h"…), or null.</summary>
    public string? DiagnosticText(string property)
    {
        if (_ctx == IntPtr.Zero) return null;
        var ptr = MpvNative.mpv_get_property_string(_ctx, property);
        if (ptr == IntPtr.Zero) return null;
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr); }
        finally { MpvNative.mpv_free(ptr); }
    }

    /// <summary>For smoke tests: saves the current video frame.</summary>
    public void Screenshot(string path) => Command("screenshot-to-file " + Quote(path) + " video");

    public VideoPlayerStats GetStats()
    {
        if (_ctx == IntPtr.Zero) return new VideoPlayerStats(0, false, true);
        var buffer = MpvNative.mpv_get_property(_ctx, "demuxer-cache-duration", MpvNative.FormatDouble, out var d) >= 0 ? d : 0;
        var stalled = MpvNative.mpv_get_property_flag(_ctx, "paused-for-cache", MpvNative.FormatFlag, out var p) >= 0 && p != 0;
        var idle = MpvNative.mpv_get_property_flag(_ctx, "idle-active", MpvNative.FormatFlag, out var i) >= 0 && i != 0;
        double? position = MpvNative.mpv_get_property(_ctx, "time-pos", MpvNative.FormatDouble, out var t) >= 0 ? t : null;
        double? avsync = MpvNative.mpv_get_property(_ctx, "avsync", MpvNative.FormatDouble, out var a) >= 0 ? a : null;
        return new VideoPlayerStats(buffer, stalled, idle, position, avsync);
    }

    public void Dispose()
    {
        var ctx = _ctx;
        _ctx = IntPtr.Zero;
        // Must happen before the host window is destroyed: mpv is still
        // rendering into it until this returns.
        if (ctx != IntPtr.Zero) MpvNative.mpv_terminate_destroy(ctx);
    }
}
