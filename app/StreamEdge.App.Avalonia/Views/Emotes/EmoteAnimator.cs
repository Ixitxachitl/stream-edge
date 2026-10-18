// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;

namespace StreamEdge.AvaloniaApp.Views.Emotes;

/// <summary>The shared animation clock for animated emotes, in chat lines
/// and the chat box alike: ~30 fps while anything animated is on screen,
/// stopped otherwise. Everything reads the same clock, so the same emote
/// stays in step wherever it shows.</summary>
public static class EmoteAnimator
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly HashSet<Visual> Visuals = [];
    private static DispatcherTimer? _timer;

    public static long ElapsedMs => Clock.ElapsedMilliseconds;

    /// <summary>Redrawn every tick until <see cref="Unregister"/>.</summary>
    public static void Register(Visual visual)
    {
        Visuals.Add(visual);
        if (_timer is not null) return;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            foreach (var v in Visuals) v.InvalidateVisual();
        });
        _timer.Start();
    }

    public static void Unregister(Visual visual)
    {
        Visuals.Remove(visual);
        if (Visuals.Count > 0 || _timer is null) return;
        _timer.Stop();
        _timer = null;
    }

    /// <summary>For tests.</summary>
    public static bool IsRegistered(Visual visual) => Visuals.Contains(visual);
}
