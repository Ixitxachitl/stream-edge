// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>A native child window for mpv to render into (HWND on Windows,
/// an X11 window on Linux, an NSView on macOS), passed to it as <c>wid</c>.
///
/// Being native, nothing Avalonia draws can sit on top of it ("airspace"),
/// so status and controls live in the pane headers, and transient messages
/// and the hover info strip go through mpv's own OSD.
///
/// For the same reason Avalonia never sees the mouse over a video. mpv
/// disables its own window when embedded, so clicks and the wheel go to
/// this one; on Windows it is subclassed to report them as <see cref="Input"/>.</summary>
public sealed partial class PlayerSurface : NativeControlHost
{
    private IntPtr _handle;

    /// <summary>The native window exists; start a player on it.</summary>
    public event Action<long>? HandleCreated;

    /// <summary>The native window is about to go; the player must stop
    /// rendering into it first.</summary>
    public event Action? HandleDestroying;

    /// <summary>Click, double-click, wheel or Esc over this video, on the
    /// UI thread. Windows only for now.</summary>
    public event Action<VideoInput>? Input;

    /// <summary>The mouse moved onto this video (true) or left it (false),
    /// on the UI thread. Windows only for now.</summary>
    public event Action<bool>? Hover;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _handle = handle.Handle;
        if (OperatingSystem.IsWindows())
        {
            Hook();
            ApplyCorners();
        }
        HandleCreated?.Invoke(handle.Handle.ToInt64());
        return handle;
    }

    // ----- Rounded corners (Windows) ---------------------------------------

    private double _cornerRadius;

    /// <summary>Corners rounded like the panes around the video (logical
    /// pixels). The window is clipped to a rounded rectangle; mpv's own
    /// window inside it is clipped with it.</summary>
    public double CornerRadius
    {
        get => _cornerRadius;
        set
        {
            if (_cornerRadius == value) return;
            _cornerRadius = value;
            ApplyCornersSoon();
        }
    }

    /// <summary>Once layout has moved the native window (Avalonia does that
    /// after the arrange this comes from).</summary>
    private void ApplyCornersSoon() => Dispatcher.UIThread.Post(ApplyCorners, DispatcherPriority.Background);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            ApplyCornersSoon();
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            PixelSizeChanged?.Invoke((int)Math.Round(Bounds.Width * scale), (int)Math.Round(Bounds.Height * scale));
        }
    }

    /// <summary>Laid out at a new size, in device pixels, on the UI thread.</summary>
    public event Action<int, int>? PixelSizeChanged;

    /// <summary>Clips the window to its rounded rectangle at the size it's
    /// laid out at: again whenever that changes (and on WM_SIZE). From the
    /// layout, not the window: a video turning from main into a thumbnail
    /// had its clip worked out at the old, large size, and the native
    /// resize that followed didn't always come back as a WM_SIZE here.</summary>
    private void ApplyCorners()
    {
        if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero) return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var w = (int)Math.Round(Bounds.Width * scale);
        var h = (int)Math.Round(Bounds.Height * scale);
        if (w <= 0 || h <= 0) return;
        var d = (int)Math.Round(2 * _cornerRadius * scale);
        // The region's right and bottom edges are exclusive, hence the +1.
        // Square corners still get a (plain) region rather than none: a
        // main video with none, later made a thumbnail, kept presenting
        // past its new rounded region (Windows seems to switch an
        // unclipped video to a direct path that ignores regions set later).
        IntPtr Region() => d > 0 ? CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d) : CreateRectRgn(0, 0, w, h);
        // The holder Avalonia wraps the video in too: it paints black, and
        // once its video had been the main one its square corners showed
        // round a thumbnail clipped inside it. Each window owns its region.
        SetWindowRgn(_handle, Region(), true);
        if (GetParent(_handle) is var holder && holder != IntPtr.Zero) SetWindowRgn(holder, Region(), true);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        HandleDestroying?.Invoke();
        if (OperatingSystem.IsWindows()) Unhook();
        _handle = IntPtr.Zero;
        base.DestroyNativeControlCore(control);
    }

    // ----- Mouse over the video (Windows) ----------------------------------

    private WndProc? _wndProc;
    private IntPtr _oldWndProc;
    private long _lastDownTime;
    private (int X, int Y) _lastDownAt;
    private int _wheel;
    private bool _hovering;

    private void Hook()
    {
        // Kept in a field: the delegate must outlive the window.
        _wndProc = OnMessage;
        _oldWndProc = SetWindowLongPtr(_handle, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));
    }

    private void Unhook()
    {
        if (_oldWndProc == IntPtr.Zero) return;
        SetWindowLongPtr(_handle, GwlpWndProc, _oldWndProc);
        _oldWndProc = IntPtr.Zero;
        _wndProc = null;
    }

    /// <summary>A thumbnail: a press may become a drag (<see cref="DragMoved"/>,
    /// <see cref="DragEnded"/>), and a click is reported on release rather
    /// than on press, so starting a drag doesn't also switch tabs.</summary>
    public bool CanDrag { get; set; }

    /// <summary>The mouse moved this far (device pixels) since the button
    /// went down on a draggable video. Windows only.</summary>
    public event Action<PixelVector>? DragMoved;

    /// <summary>The button came up (or capture was lost) after a drag.</summary>
    public event Action? DragEnded;

    private bool _pressed;
    private bool _dragging;
    private Point32 _pressAt;

    /// <summary>The press is over: a drop, a click, or nothing (capture lost).</summary>
    private void EndPress(bool released)
    {
        if (!_pressed) return;
        _pressed = false;
        var dragged = _dragging;
        _dragging = false;
        if (dragged) DragEnded?.Invoke();
        else if (released) Input?.Invoke(VideoInput.Click);
    }

    private IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmMouseMove && _pressed && GetCursorPos(out var cursor))
        {
            // Screen coordinates: the window itself moves under the mouse.
            var delta = new PixelVector(cursor.X - _pressAt.X, cursor.Y - _pressAt.Y);
            if (!_dragging && (Math.Abs(delta.X) > GetSystemMetrics(SmCxDrag) || Math.Abs(delta.Y) > GetSystemMetrics(SmCyDrag)))
                _dragging = true;
            if (_dragging) DragMoved?.Invoke(delta);
        }
        if (msg == WmSize) ApplyCorners();
        switch (msg)
        {
            case WmLButtonDown when CanDrag:
                if (!GetCursorPos(out _pressAt)) break;
                _pressed = true;
                _dragging = false;
                SetCapture(hwnd);
                return IntPtr.Zero;
            case WmLButtonUp when _pressed:
                // Released first, so the capture change that follows finds
                // nothing left to end.
                EndPress(released: true);
                ReleaseCapture();
                return IntPtr.Zero;
            case WmCaptureChanged when _pressed:
                EndPress(released: false);
                break;
            case WmMouseMove when !_hovering:
                // Ask for a WM_MOUSELEAVE once the cursor goes.
                var track = new TrackMouseEventArgs
                {
                    Size = (uint)Marshal.SizeOf<TrackMouseEventArgs>(),
                    Flags = TmeLeave,
                    Window = hwnd,
                };
                _hovering = TrackMouseEvent(ref track);
                if (_hovering) Hover?.Invoke(true);
                break;
            case WmMouseLeave:
                _hovering = false;
                Hover?.Invoke(false);
                break;
            case WmLButtonDown:
                // The window class has no CS_DBLCLKS, so double-clicks are
                // timed here, with the user's own double-click settings.
                var at = ((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
                var now = Environment.TickCount64;
                var isDouble = now - _lastDownTime <= GetDoubleClickTime()
                    && Math.Abs(at.Item1 - _lastDownAt.X) <= GetSystemMetrics(SmCxDoubleClk) / 2
                    && Math.Abs(at.Item2 - _lastDownAt.Y) <= GetSystemMetrics(SmCyDoubleClk) / 2;
                _lastDownTime = isDouble ? 0 : now;
                _lastDownAt = at;
                Input?.Invoke(isDouble ? VideoInput.DoubleClick : VideoInput.Click);
                return IntPtr.Zero;
            case WmMouseWheel:
                // Precision touchpads send fractions of a notch; one step per
                // whole notch (120).
                _wheel += (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                for (; _wheel >= WheelDelta; _wheel -= WheelDelta) Input?.Invoke(VideoInput.WheelUp);
                for (; _wheel <= -WheelDelta; _wheel += WheelDelta) Input?.Invoke(VideoInput.WheelDown);
                return IntPtr.Zero;
            case WmKeyDown when wParam == VkEscape:
                Input?.Invoke(VideoInput.Escape);
                return IntPtr.Zero;
        }
        return CallWindowProc(_oldWndProc, hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int GwlpWndProc = -4;
    private const uint WmSize = 0x0005;
    private const uint WmKeyDown = 0x0100;
    private const uint WmMouseMove = 0x0200;
    private const uint WmMouseLeave = 0x02A3;
    private const uint TmeLeave = 0x00000002;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmCaptureChanged = 0x0215;
    private const int SmCxDrag = 68;
    private const int SmCyDrag = 69;
    private const uint WmMouseWheel = 0x020A;
    private const int WheelDelta = 120;
    private const int SmCxDoubleClk = 36;
    private const int SmCyDoubleClk = 37;
    private static readonly IntPtr VkEscape = 0x1B;

    /// <summary>SetWindowLongPtrW only exists in 64-bit user32; 32-bit
    /// Windows has SetWindowLongW, which the header's macro maps it to.</summary>
    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, index, value) : (IntPtr)SetWindowLong32(hWnd, index, value.ToInt32());

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong32(IntPtr hWnd, int index, int value);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial IntPtr CallWindowProc(IntPtr previous, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(IntPtr hWnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventArgs
    {
        public uint Size;
        public uint Flags;
        public IntPtr Window;
        public uint HoverTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackMouseEvent(ref TrackMouseEventArgs args);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point32 point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetCapture(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    /// <summary>Raises this video above sibling videos, for PIP thumbnails
    /// that overlap the main one. Avalonia's draw order doesn't apply to
    /// native windows.</summary>
    /// <remarks>Windows only for now: Avalonia hosts each native control in a
    /// holder window, and it's the holders that are siblings, so the holder
    /// is what gets raised. Linux (X11) and macOS are a TODO.</remarks>
    public void BringToFront()
    {
        if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero) return;
        var holder = GetParent(_handle);
        SetWindowPos(holder != IntPtr.Zero ? holder : _handle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetParent(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
