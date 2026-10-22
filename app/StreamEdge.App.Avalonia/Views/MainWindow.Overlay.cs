// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>The videos are native windows, and nothing this window draws can
/// cover them. On Windows a second window does: borderless, transparent,
/// owned by this one (so always just above it), lying exactly over the panes
/// and moving with them. It draws the videos' smooth corners
/// (<see cref="VideoCorners"/>) and, while a pane is dragged, the preview of
/// where it lands, so the videos keep playing under it. The mouse passes
/// through it to the app and the videos below.
///
/// Elsewhere (Linux, macOS, the headless tests) there's no overlay: the
/// preview stays in this window and the videos step aside while dragging.</summary>
public partial class MainWindow
{
    private Window? _overlayWindow;
    private readonly VideoCorners _corners = new() { IsHitTestVisible = false };

    private void CreateOverlay()
    {
        if (TryGetPlatformHandle()?.HandleDescriptor != "HWND") return;
        ((Panel)DockOverlay.Parent!).Children.Remove(DockOverlay);
        _overlayWindow = new Window
        {
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            Content = new Panel { Children = { _corners, DockOverlay } },
        };
        PlaceOverlay();
        _overlayWindow.Show(this);
        if (_overlayWindow.TryGetPlatformHandle() is { } handle) OverlayInput.PassThrough(handle.Handle);
        // The overlay rounds the main video; a thumbnail keeps its clip (it
        // lies over the main video, so its corners can't be painted over).
        Videos.FrontCornerRadius = 0;
        Videos.InvalidateArrange();
        Videos.Placed += SyncOverlay;
        // The pane can move without the videos in it being placed again (a
        // splitter dragged beside it, the video turned back on at the same
        // size): their corners must move with it.
        PlayerPane.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty || e.Property == IsVisibleProperty) SyncOverlay();
        };
        PositionChanged += (_, _) => PlaceOverlay();
        Resized += (_, _) => SyncOverlay();
        ScalingChanged += (_, _) => SyncOverlay();
        SyncOverlay();
    }

    /// <summary>Over the layout, wherever this window is.</summary>
    private void PlaceOverlay()
    {
        if (_overlayWindow is null) return;
        _overlayWindow.Position = this.PointToScreen(Layout.TranslatePoint(default, this) ?? default);
        _overlayWindow.Width = Layout.Bounds.Width;
        _overlayWindow.Height = Layout.Bounds.Height;
    }

    /// <summary>Follows the videos: where they are, and what lies behind each
    /// corner of the main one.</summary>
    private void SyncOverlay()
    {
        if (_overlayWindow is null) return;
        PlaceOverlay();
        var ground = (IBrush?)this.FindResource("SeWindowBg") ?? Brushes.Black;
        var pane = PlayerPane.TranslatePoint(default, Layout) is { } at ? new Rect(at, PlayerPane.Bounds.Size) : default;
        var videos = new List<VideoCorners.Video>();
        foreach (var (rect, front) in Videos.Shown)
        {
            if (Videos.TranslatePoint(rect.Position, Layout) is not { } origin) continue;
            var r = new Rect(origin, rect.Size);
            // Where the video meets the pane's own corner, the window's ground
            // shows there; otherwise the pane's black.
            IBrush Behind(Point corner, Point paneCorner) =>
                Math.Abs(corner.X - paneCorner.X) < 1.5 && Math.Abs(corner.Y - paneCorner.Y) < 1.5 ? ground : Brushes.Black;
            videos.Add(new VideoCorners.Video(r, front,
            [
                Behind(r.TopLeft, pane.TopLeft), Behind(r.TopRight, pane.TopRight),
                Behind(r.BottomRight, pane.BottomRight), Behind(r.BottomLeft, pane.BottomLeft),
            ]));
        }
        _corners.Videos = videos;
    }
}

/// <summary>Lets the mouse through the overlay window: it answers every hit
/// test "transparent", so Windows hands the mouse to this thread's windows
/// below it (the app, and the videos, whose input this thread handles).</summary>
internal static partial class OverlayInput
{
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Kept for the window's life: Windows calls it.
    private static WndProc? _proc;
    private static IntPtr _previous;

    private const int GwlpWndProc = -4;
    private const uint WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    public static void PassThrough(IntPtr hwnd)
    {
        _proc = (h, msg, w, l) => msg == WmNcHitTest ? HtTransparent : CallWindowProc(_previous, h, msg, w, l);
        _previous = IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_proc))
            : SetWindowLong32(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_proc).ToInt32());
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong32(IntPtr hWnd, int index, int value);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial IntPtr CallWindowProc(IntPtr previous, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
