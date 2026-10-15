// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>A native child window for mpv to render into (HWND on Windows,
/// an X11 window on Linux, an NSView on macOS), passed to it as <c>wid</c>.
///
/// Being native, nothing Avalonia draws can sit on top of it ("airspace"),
/// so status and controls live in the pane headers, and transient messages
/// go through mpv's own OSD.</summary>
public sealed partial class PlayerSurface : NativeControlHost
{
    private IntPtr _handle;

    /// <summary>The native window exists; start a player on it.</summary>
    public event Action<long>? HandleCreated;

    /// <summary>The native window is about to go; the player must stop
    /// rendering into it first.</summary>
    public event Action? HandleDestroying;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _handle = handle.Handle;
        HandleCreated?.Invoke(handle.Handle.ToInt64());
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        HandleDestroying?.Invoke();
        _handle = IntPtr.Zero;
        base.DestroyNativeControlCore(control);
    }

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
