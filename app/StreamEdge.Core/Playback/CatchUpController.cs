// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Playback;

/// <summary>Tuning for <see cref="CatchUpController"/>. Defaults favour being
/// close to live over smoothness and quality.</summary>
public sealed record CatchUpOptions
{
    /// <summary>Buffer to aim for, in seconds. Less than this and a network
    /// hiccup stalls playback; more is latency for nothing.</summary>
    public double TargetBuffer { get; init; } = 1.0;

    /// <summary>Start speeding up once the buffer passes this…</summary>
    public double SpeedUpAbove { get; init; } = 2.0;

    /// <summary>…up to this rate (audio stays pitch-corrected).</summary>
    public double MaxSpeed { get; init; } = 1.25;

    /// <summary>Further behind than this, speeding up would take too long:
    /// drop everything buffered and rejoin at the live edge.</summary>
    public double ResyncAbove { get; init; } = 6.0;

    /// <summary>The downloader being this many whole segments behind the
    /// playlist's edge also forces a resync.</summary>
    public int ResyncSegmentsBehind { get; init; } = 3;

    /// <summary>Don't resync again within this long of the last one.</summary>
    public TimeSpan ResyncCooldown { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>This many stalls inside <see cref="StallWindow"/> drops one
    /// quality step: catching up beats resolution.</summary>
    public int StallsForDowngrade { get; init; } = 2;

    public TimeSpan StallWindow { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Stall-free this long after a downgrade, try one step back up.</summary>
    public TimeSpan UpgradeAfter { get; init; } = TimeSpan.FromMinutes(3);
}

/// <summary>What the player is doing right now, sampled a few times a second.</summary>
/// <param name="BufferSeconds">Seconds of media buffered ahead of the playhead.</param>
/// <param name="Stalled">Playback paused waiting for data.</param>
/// <param name="SegmentsBehind">Complete segments the downloader hasn't fetched yet.</param>
/// <param name="InAdBreak">Twitch is serving an ad break; stalls don't count.</param>
public readonly record struct PlaybackSample(
    DateTimeOffset At, double BufferSeconds, bool Stalled, int SegmentsBehind, bool InAdBreak = false);

public enum CatchUpAction
{
    None,
    /// <summary>Reload at the live edge, discarding the buffer.</summary>
    Resync,
    /// <summary>Switch to the next lower quality (and rejoin at the edge).</summary>
    QualityDown,
    /// <summary>Try the next higher quality, up to the user's preference.</summary>
    QualityUp,
}

public readonly record struct CatchUpDecision(double Speed, CatchUpAction Action, string? Notice);

/// <summary>Decides playback speed, resyncs and quality steps from player
/// samples. Latency first: buffer beyond the target is played off faster,
/// big lags jump straight to live, and repeated stalls cost a quality step
/// rather than growing the buffer.</summary>
/// <remarks>Stateful but pure: no clocks or I/O, so every rule is testable
/// by feeding samples.</remarks>
public sealed class CatchUpController(CatchUpOptions options)
{
    private readonly Queue<DateTimeOffset> _stalls = new();
    private bool _wasStalled;
    private bool _speeding;
    private DateTimeOffset _lastResync = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStallOrStep = DateTimeOffset.MinValue;

    public CatchUpOptions Options => options;

    /// <summary>Steps below the user's preferred quality (0 = at preference).</summary>
    public int QualityOffset { get; private set; }

    /// <summary>How many steps down are possible from the preference.</summary>
    public int MaxDowngrade { get; set; }

    public double Speed { get; private set; } = 1.0;

    /// <summary>Call after any reload the controller didn't ask for (new
    /// channel, manual quality change) so stale stall history doesn't count.</summary>
    public void Reset(DateTimeOffset now, bool keepQuality = true)
    {
        _stalls.Clear();
        _wasStalled = false;
        _speeding = false;
        Speed = 1.0;
        _lastResync = now;
        _lastStallOrStep = now;
        if (!keepQuality) QualityOffset = 0;
    }

    public CatchUpDecision Update(PlaybackSample s)
    {
        // --- Stalls → quality ------------------------------------------------
        var stallStarted = s.Stalled && !_wasStalled;
        _wasStalled = s.Stalled;
        if (stallStarted && !s.InAdBreak)
        {
            _stalls.Enqueue(s.At);
            _lastStallOrStep = s.At;
        }
        while (_stalls.Count > 0 && s.At - _stalls.Peek() > options.StallWindow) _stalls.Dequeue();

        if (_stalls.Count >= options.StallsForDowngrade && QualityOffset < MaxDowngrade)
        {
            QualityOffset++;
            _stalls.Clear();
            _lastStallOrStep = s.At;
            _lastResync = s.At;
            return Set(1.0, CatchUpAction.QualityDown, "Lagging: lowering quality to keep up");
        }

        if (QualityOffset > 0 && !s.Stalled && s.At - _lastStallOrStep >= options.UpgradeAfter)
        {
            QualityOffset--;
            _lastStallOrStep = s.At;
            _lastResync = s.At;
            return Set(1.0, CatchUpAction.QualityUp, "Stable: raising quality");
        }

        // --- Way behind → jump to live ------------------------------------
        var farBehind = s.BufferSeconds > options.ResyncAbove || s.SegmentsBehind >= options.ResyncSegmentsBehind;
        if (farBehind && !s.Stalled && s.At - _lastResync >= options.ResyncCooldown)
        {
            _lastResync = s.At;
            _speeding = false;
            return Set(1.0, CatchUpAction.Resync, $"Jumping to live ({s.BufferSeconds:0.0}s behind)");
        }

        // --- A little behind → play faster ----------------------------------
        if (s.Stalled) return Set(1.0, CatchUpAction.None, null);
        if (!_speeding && s.BufferSeconds > options.SpeedUpAbove) _speeding = true;
        if (_speeding && s.BufferSeconds <= options.TargetBuffer) _speeding = false;
        if (!_speeding) return Set(1.0, CatchUpAction.None, null);

        // Proportional: just past the threshold barely speeds up; near the
        // resync point runs at MaxSpeed. Never below 1.05, which is the
        // slowest rate that still drains a buffer in reasonable time.
        var span = Math.Max(0.1, options.ResyncAbove - options.TargetBuffer);
        var t = Math.Clamp((s.BufferSeconds - options.TargetBuffer) / span, 0, 1);
        var speed = Math.Round(Math.Max(1.05, 1 + t * (options.MaxSpeed - 1)), 2);
        return Set(speed, CatchUpAction.None, null);
    }

    private CatchUpDecision Set(double speed, CatchUpAction action, string? notice)
    {
        Speed = speed;
        return new CatchUpDecision(speed, action, notice);
    }
}
