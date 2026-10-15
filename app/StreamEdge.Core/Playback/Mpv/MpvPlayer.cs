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

        foreach (var (key, input) in Bindings)
            Command($"keybind {key} \"script-message streamedge {input}\"");
        _events = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _events.Start();
    }

    /// <summary>Mouse and keys over the video, reported as <see cref="InputReceived"/>.
    /// The video is a native window that takes these before the app could,
    /// so mpv catches them and passes them on.</summary>
    internal static readonly (string Key, VideoInput Input)[] Bindings =
    [
        ("MBTN_LEFT", VideoInput.Click),
        ("MBTN_LEFT_DBL", VideoInput.DoubleClick),
        ("WHEEL_UP", VideoInput.WheelUp),
        ("WHEEL_DOWN", VideoInput.WheelDown),
        ("ESC", VideoInput.Escape),
    ];

    /// <summary>Raised on mpv's event thread; marshal before touching UI.</summary>
    public event Action<VideoInput>? InputReceived;

    private readonly Thread _events;
    private volatile bool _closing;

    private void EventLoop()
    {
        while (!_closing)
        {
            var ctx = _ctx;
            if (ctx == IntPtr.Zero) return;
            var ev = MpvNative.mpv_wait_event(ctx, 1.0);
            if (ev == IntPtr.Zero) continue;
            // mpv_event: int event_id; int error; uint64 reply_userdata; void* data.
            switch (System.Runtime.InteropServices.Marshal.ReadInt32(ev))
            {
                case MpvNative.EventShutdown:
                    return;
                case MpvNative.EventClientMessage:
                    var data = System.Runtime.InteropServices.Marshal.ReadIntPtr(ev, 16);
                    if (ClientMessage(data) is ["streamedge", var name, ..] && Enum.TryParse<VideoInput>(name, out var input))
                        InputReceived?.Invoke(input);
                    break;
            }
        }
    }

    /// <summary>mpv_event_client_message: int num_args; const char** args.</summary>
    private static string[] ClientMessage(IntPtr data)
    {
        if (data == IntPtr.Zero) return [];
        var count = System.Runtime.InteropServices.Marshal.ReadInt32(data);
        var args = System.Runtime.InteropServices.Marshal.ReadIntPtr(data, IntPtr.Size);
        var result = new string[Math.Clamp(count, 0, 16)];
        for (var i = 0; i < result.Length; i++)
            result[i] = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(
                System.Runtime.InteropServices.Marshal.ReadIntPtr(args, i * IntPtr.Size)) ?? "";
        return result;
    }

    /// <summary>Latency over smoothness. Applied in order, so later lines
    /// override what the low-latency profile sets.</summary>
    internal static readonly (string Name, string Value)[] Options =
    [
        // Embedded player: no mpv UI or terminal, and none of mpv's own key
        // or mouse bindings. The video window does take input, but only the
        // bindings in Bindings below, which report back to the app.
        ("terminal", "no"),
        ("msg-level", "all=warn"),
        ("osc", "no"),
        ("input-default-bindings", "no"),
        ("input-vo-keyboard", "yes"),
        ("input-cursor", "yes"),
        ("cursor-autohide", "1000"),
        ("idle", "yes"),
        ("keep-open", "no"),
        ("force-window", "yes"),
        ("ytdl", "no"),
        ("audio-client-name", "StreamEdge"),
        ("hwdec", "auto-safe"),
        // OpenGL rather than mpv's default D3D11 on Windows. Several players
        // rendering into child windows that get moved and raised (tabs, PIP)
        // crashed inside dxgi.dll, intermittently, with D3D11 in both flip
        // and blit modes. OpenGL is mpv's default elsewhere anyway.
        ("gpu-api", "opengl"),

        // mpv's own low-latency preset: no audio buffer, single-threaded
        // decode, minimal demuxer probing, no frame interpolation.
        ("profile", "low-latency"),
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
        // quality rather than to buffer more.
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
        // Drop late frames rather than fall further behind.
        ("framedrop", "vo"),

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
        ("osd-font-size", "26"),
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
        return new VideoPlayerStats(buffer, stalled, idle);
    }

    public void Dispose()
    {
        // Stop the event thread first: it must not be inside mpv_wait_event
        // when the handle is destroyed.
        _closing = true;
        if (_ctx != IntPtr.Zero) MpvNative.mpv_wakeup(_ctx);
        _events.Join(TimeSpan.FromSeconds(2));
        var ctx = _ctx;
        _ctx = IntPtr.Zero;
        // Must happen before the host window is destroyed: mpv is still
        // rendering into it until this returns.
        if (ctx != IntPtr.Zero) MpvNative.mpv_terminate_destroy(ctx);
    }
}

/// <summary>Mouse and keyboard input on a video, from mpv.</summary>
public enum VideoInput
{
    Click,
    DoubleClick,
    WheelUp,
    WheelDown,
    Escape,
}
