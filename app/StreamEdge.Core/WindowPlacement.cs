// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge;

public enum WindowMode
{
    Normal,
    Maximized,
    FullScreen,
}

/// <summary>The main window's last position and size. <see cref="X"/>/<see cref="Y"/>
/// are physical pixels (the OS's coordinates for the window's corner);
/// <see cref="Width"/>/<see cref="Height"/> are the client size in DIPs. They
/// are the "normal" bounds even while maximized, so un-maximizing after a
/// restart returns to them.</summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, WindowMode Mode = WindowMode.Normal)
{
    /// <summary>Whether enough of the window would be on one of
    /// <paramref name="screens"/> (working areas, physical pixels) to grab
    /// it: at least 100×50 px of it.</summary>
    public bool IsVisibleOn(IEnumerable<(int X, int Y, int Width, int Height)> screens, double scaling)
    {
        var w = (int)(Width * scaling);
        var h = (int)(Height * scaling);
        foreach (var s in screens)
        {
            var overlapW = Math.Min(X + w, s.X + s.Width) - Math.Max(X, s.X);
            var overlapH = Math.Min(Y + h, s.Y + s.Height) - Math.Max(Y, s.Y);
            if (overlapW >= 100 && overlapH >= 50) return true;
        }
        return false;
    }

    /// <summary>These bounds on the screen whose working area (physical
    /// pixels) is <paramref name="area"/>: unchanged if the window's centre
    /// is already on it, else centred on it and shrunk to fit. For a window
    /// maximized or full screen on another display than the one its normal
    /// bounds are on, so it comes back on that display.</summary>
    public WindowPlacement OnScreen((int X, int Y, int Width, int Height) area, double scaling)
    {
        var (cx, cy) = (X + Width * scaling / 2, Y + Height * scaling / 2);
        if (cx >= area.X && cx < area.X + area.Width && cy >= area.Y && cy < area.Y + area.Height) return this;
        var width = Math.Min(Width, area.Width / scaling);
        var height = Math.Min(Height, area.Height / scaling);
        return this with
        {
            X = area.X + (int)((area.Width - width * scaling) / 2),
            Y = area.Y + (int)((area.Height - height * scaling) / 2),
            Width = width,
            Height = height,
        };
    }
}
