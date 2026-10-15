// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>A native child window for mpv to render into (HWND on Windows,
/// an X11 window on Linux, an NSView on macOS), passed to it as <c>wid</c>.
///
/// Being native, nothing Avalonia draws can sit on top of it ("airspace"),
/// so status and controls live in the pane headers, and transient messages
/// go through mpv's own OSD.
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

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _handle = handle.Handle;
        if (OperatingSystem.IsWindows()) Hook();
        HandleCreated?.Invoke(handle.Handle.ToInt64());
        return handle;
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

    private IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
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
    private const uint WmKeyDown = 0x0100;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmMouseWheel = 0x020A;
    private const int WheelDelta = 120;
    private const int SmCxDoubleClk = 36;
    private const int SmCyDoubleClk = 37;
    private static readonly IntPtr VkEscape = 0x1B;

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial IntPtr CallWindowProc(IntPtr previous, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

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
