// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Platform;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>A native child window for mpv to render into (HWND on Windows,
/// an X11 window on Linux, an NSView on macOS), passed to it as <c>wid</c>.
///
/// Being native, nothing Avalonia draws can sit on top of it ("airspace"),
/// so status and controls live in the pane headers, and transient messages
/// go through mpv's own OSD.</summary>
public sealed class PlayerSurface : NativeControlHost
{
    /// <summary>The native window exists; start a player on it.</summary>
    public event Action<long>? HandleCreated;

    /// <summary>The native window is about to go; the player must stop
    /// rendering into it first.</summary>
    public event Action? HandleDestroying;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        HandleCreated?.Invoke(handle.Handle.ToInt64());
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        HandleDestroying?.Invoke();
        base.DestroyNativeControlCore(control);
    }
}
