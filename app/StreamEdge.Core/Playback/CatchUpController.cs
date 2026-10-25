// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Playback;

/// <summary>Tuning for <see cref="CatchUpController"/>. Close to live, but
/// steady: the buffer swings by about a segment (2 s) as Twitch's segments
/// arrive in bursts, so every threshold sits well clear of that swing, and
/// decisions use a smoothed buffer rather than each reading.</summary>
public sealed record CatchUpOptions
{
    /// <summary>Buffer to aim for, in seconds. Less than this and the normal
    /// burstiness of segment delivery stalls playback.</summary>
    public double TargetBuffer { get; init; } = 2.0;

    /// <summary>Start speeding up once the smoothed buffer passes this, a
    /// segment above the target…</summary>
    public double SpeedUpAbove { get; init; } = 4.0;

    /// <summary>…at this rate, gentle enough not to notice…</summary>
    public double CatchUpSpeed { get; init; } = 1.08;

    /// <summary>…or this one when well behind (past halfway to a resync).
    /// Two fixed rates rather than a sliding scale: every rate change is a
    /// small audible hitch, so there should be few of them.</summary>
    public double FastCatchUpSpeed { get; init; } = 1.15;

    /// <summary>Further behind than this, speeding up would take too long:
    /// drop everything buffered and rejoin at the live edge.</summary>
    public double ResyncAbove { get; init; } = 10.0;

    /// <summary>The downloader being this many whole segments behind the
    /// playlist's edge also forces a resync.</summary>
    public int ResyncSegmentsBehind { get; init; } = 4;

    /// <summary>Don't resync again within this long of the last one.</summary>
    public TimeSpan ResyncCooldown { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>After a (re)load, stalls don't count for this long: the first
    /// seconds always start from an empty buffer.</summary>
    public TimeSpan StartupGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>This many stalls inside <see cref="StallWindow"/> drops one
    /// quality step: catching up beats resolution, but one bad moment
    /// isn't a trend.</summary>
    public int StallsForDowngrade { get; init; } = 3;

    public TimeSpan StallWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Stall-free this long after a downgrade, try one step back up
    /// (the fallback when a probe can't tell; see <see cref="ProbeAfter"/>).</summary>
    public TimeSpan UpgradeAfter { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Stall-free this long after a step, the next quality up may be
    /// probed: one of its segments timed to see whether it would fit.</summary>
    public TimeSpan ProbeAfter { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>A step up that drops back within this long failed: the
    /// connection is borderline for it.</summary>
    public TimeSpan UpgradeFailWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Each failed step up in a row doubles the waits above, up to
    /// this many times (3 min → 6 → 12 → 24), so a borderline connection
    /// settles instead of bouncing between qualities.</summary>
    public int MaxBackoffDoublings { get; init; } = 3;

    /// <summary>No speeding up this soon after a stall: on a connection that
    /// just ran dry, the cushion that built up since is the only thing
    /// between it and the next stall; latency can wait.</summary>
    public TimeSpan NoCatchUpAfterStall { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Time constant of the buffer smoothing: long enough to see
    /// through a segment's swing, short enough to notice real drift.</summary>
    public TimeSpan Smoothing { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Buffered seconds the player waits for before starting or
    /// resuming after a stall, on a good connection (mpv's cache-pause-wait).</summary>
    public double ResumeBuffer { get; init; } = 1.0;

    /// <summary>Each stall, or drop in quality for bandwidth, adds this much
    /// cushion: the player waits for that much more before resuming, and
    /// every buffer threshold above moves up by it, so catch-up doesn't play
    /// it straight off again. A bad connection trades a little latency for
    /// smoother playback, including across the quality changes it causes…</summary>
    public double CushionStep { get; init; } = 1.0;

    /// <summary>…up to this much…</summary>
    public double MaxCushion { get; init; } = 2.0;

    /// <summary>…and gives a step back after this long without a stall.</summary>
    public TimeSpan CushionDecay { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Low latency off: the quality picked holds whatever the
    /// connection does, and a deep buffer rides out the hiccups instead.
    /// Playback starts several seconds back, each stall waits for more,
    /// catch-up only plays off a buffer far deeper than needed, and it
    /// jumps to live only a minute behind.</summary>
    public static CatchUpOptions Steady { get; } = new()
    {
        TargetBuffer = 6.0,
        SpeedUpAbove = 20.0,
        CatchUpSpeed = 1.05,
        FastCatchUpSpeed = 1.05,
        ResyncAbove = 60.0,
        ResyncSegmentsBehind = 10,
        StallsForDowngrade = int.MaxValue,
        ResumeBuffer = 4.0,
        CushionStep = 2.0,
        MaxCushion = 8.0,
    };
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
    /// <summary>Switch to the next lower quality.</summary>
    QualityDown,
    /// <summary>Try the next higher quality, up to the user's preference.</summary>
    QualityUp,
}

public readonly record struct CatchUpDecision(double Speed, CatchUpAction Action, string? Notice);

/// <summary>Decides playback speed, resyncs and quality steps from player
/// samples. Latency first, but steadily: buffer well beyond the target is
/// played off a little faster, big lags jump straight to live, and a run
/// of stalls costs a quality step rather than growing the buffer.</summary>
/// <remarks>Stateful but pure: no clocks or I/O, so every rule is testable
/// by feeding samples.</remarks>
public sealed class CatchUpController(CatchUpOptions options)
{
    private readonly Queue<DateTimeOffset> _stalls = new();
    private bool _wasStalled;
    private bool _speeding;
    private double? _smoothed;
    private DateTimeOffset _lastSample;
    private DateTimeOffset _lastResync = DateTimeOffset.MinValue;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStallOrStep = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStall = DateTimeOffset.MinValue;
    private DateTimeOffset _lastUpgrade = DateTimeOffset.MinValue;

    /// <summary>Changeable while playing (low latency turned on or off);
    /// call <see cref="Reset"/> after.</summary>
    public CatchUpOptions Options
    {
        get => options;
        set => options = value;
    }

    /// <summary>Steps below the user's preferred quality (0 = at preference).</summary>
    public int QualityOffset { get; private set; }

    /// <summary>How many steps down are possible from the preference.</summary>
    public int MaxDowngrade { get; set; }

    public double Speed { get; private set; } = 1.0;

    /// <summary>Steps up in a row that soon dropped back.</summary>
    public int FailedUpgrades { get; private set; }

    private double Backoff => Math.Pow(2, Math.Min(FailedUpgrades, options.MaxBackoffDoublings));

    /// <summary>How long stall-free before the timer steps up, after backoff.</summary>
    public TimeSpan UpgradeWait => options.UpgradeAfter * Backoff;

    /// <summary>Below the preferred quality and steady long enough (after
    /// backoff) to try the next one up with a probe.</summary>
    public bool MayProbe(DateTimeOffset now) =>
        QualityOffset > 0 && !_wasStalled && now - _lastStallOrStep >= options.ProbeAfter * Backoff;

    /// <summary>The smoothed buffer decisions are made on.</summary>
    public double SmoothedBuffer => _smoothed ?? 0;

    /// <summary>Extra buffer held because the connection has been struggling
    /// (see <see cref="CatchUpOptions.CushionStep"/>).</summary>
    public double Cushion { get; private set; }

    /// <summary>What the player should wait for before resuming after a stall.</summary>
    public double ResumeBuffer => options.ResumeBuffer + Cushion;

    private DateTimeOffset _lastCushionChange;

    private void RaiseCushion(DateTimeOffset now)
    {
        Cushion = Math.Min(options.MaxCushion, Cushion + options.CushionStep);
        _lastCushionChange = now;
    }

    /// <summary>Call after any reload (new channel, manual quality change,
    /// resync) so stale history and the empty start don't count.</summary>
    public void Reset(DateTimeOffset now, bool keepQuality = true)
    {
        _stalls.Clear();
        _wasStalled = false;
        _speeding = false;
        _smoothed = null;
        Speed = 1.0;
        _lastResync = now;
        _loadedAt = now;
        _lastStallOrStep = now;
        _lastStall = DateTimeOffset.MinValue;
        if (keepQuality) return;
        // A new channel or the user's own pick: nothing learnt carries over.
        QualityOffset = 0;
        Cushion = 0;
        FailedUpgrades = 0;
        _lastUpgrade = DateTimeOffset.MinValue;
        _upgradeHolding = false;
    }

    /// <summary>Jumps straight to a quality step (from a bandwidth estimate)
    /// rather than stepping one at a time; counts as a step for the upgrade
    /// timer. Returns whether it changed.</summary>
    public bool SetQualityOffset(int offset, DateTimeOffset now)
    {
        offset = Math.Clamp(offset, 0, MaxDowngrade);
        if (offset == QualityOffset) return false;
        if (offset > QualityOffset)
        {
            NoteDowngrade(now);
            RaiseCushion(now);
        }
        else NoteUpgrade(now);
        QualityOffset = offset;
        _stalls.Clear();
        _lastStallOrStep = now;
        return true;
    }

    /// <summary>Sets the steps down without counting as a step: for starting
    /// below the preference, or when what the steps count from moves.</summary>
    public void Rebase(int offset) => QualityOffset = Math.Clamp(offset, 0, MaxDowngrade);

    private void NoteUpgrade(DateTimeOffset now)
    {
        _lastUpgrade = now;
        _upgradeHolding = true;
    }

    /// <summary>The last step up hasn't been undone yet.</summary>
    private bool _upgradeHolding;

    /// <summary>Dropping soon after a step up means that step failed.</summary>
    private void NoteDowngrade(DateTimeOffset now)
    {
        if (_upgradeHolding && now - _lastUpgrade <= options.UpgradeFailWindow) FailedUpgrades++;
        _upgradeHolding = false;
    }

    /// <summary>A step up still in place past the failure window, with no
    /// stall since: it held, and the backoff starts over.</summary>
    private void ForgiveIfHeld(DateTimeOffset now)
    {
        if (FailedUpgrades > 0 && _upgradeHolding && now - _lastUpgrade > options.UpgradeFailWindow
            && now - _lastStallOrStep > options.UpgradeFailWindow)
            FailedUpgrades = 0;
    }

    public CatchUpDecision Update(PlaybackSample s)
    {
        // --- Smoothed buffer --------------------------------------------------
        if (_smoothed is not { } previous)
        {
            _smoothed = s.BufferSeconds;
        }
        else
        {
            var dt = Math.Max(0, (s.At - _lastSample).TotalSeconds);
            var alpha = 1 - Math.Exp(-dt / Math.Max(0.1, options.Smoothing.TotalSeconds));
            _smoothed = previous + alpha * (s.BufferSeconds - previous);
        }
        _lastSample = s.At;
        var buffer = _smoothed.Value;

        // --- Stalls → quality -------------------------------------------------
        var stallStarted = s.Stalled && !_wasStalled;
        _wasStalled = s.Stalled;
        if (stallStarted) _lastStall = s.At;
        if (stallStarted && !s.InAdBreak && s.At - _loadedAt >= options.StartupGrace)
        {
            _stalls.Enqueue(s.At);
            _lastStallOrStep = s.At;
            RaiseCushion(s.At);
        }
        while (_stalls.Count > 0 && s.At - _stalls.Peek() > options.StallWindow) _stalls.Dequeue();
        if (Cushion > 0 && !s.Stalled && s.At - _lastCushionChange >= options.CushionDecay
            && s.At - _lastStall >= options.CushionDecay)
        {
            Cushion = Math.Max(0, Cushion - options.CushionStep);
            _lastCushionChange = s.At;
        }

        if (_stalls.Count >= options.StallsForDowngrade && QualityOffset < MaxDowngrade)
        {
            NoteDowngrade(s.At);
            QualityOffset++;
            _stalls.Clear();
            _lastStallOrStep = s.At;
            return Set(1.0, CatchUpAction.QualityDown, "Lagging: lowering quality to keep up");
        }

        ForgiveIfHeld(s.At);
        if (QualityOffset > 0 && !s.Stalled && s.At - _lastStallOrStep >= UpgradeWait)
        {
            NoteUpgrade(s.At);
            QualityOffset--;
            _lastStallOrStep = s.At;
            return Set(1.0, CatchUpAction.QualityUp, "Stable: raising quality");
        }

        // --- Way behind → jump to live -------------------------------------
        // On the raw reading as well as the smoothed one: a big jump (after a
        // long stall) shouldn't wait for the average to catch up.
        var resyncAbove = options.ResyncAbove + Cushion;
        var speedUpAbove = options.SpeedUpAbove + Cushion;
        var farBehind = Math.Min(buffer, s.BufferSeconds) > resyncAbove
                        || s.SegmentsBehind >= options.ResyncSegmentsBehind;
        if (farBehind && !s.Stalled && s.At - _lastResync >= options.ResyncCooldown)
        {
            _lastResync = s.At;
            _speeding = false;
            return Set(1.0, CatchUpAction.Resync, $"Jumping to live ({buffer:0.0}s behind)");
        }

        // --- A little behind → play faster ----------------------------------
        if (s.Stalled) return Set(1.0, CatchUpAction.None, null);
        if (!_speeding && buffer > speedUpAbove && s.At - _lastStall >= options.NoCatchUpAfterStall) _speeding = true;
        if (_speeding && buffer <= options.TargetBuffer + Cushion + 0.5) _speeding = false;
        if (!_speeding) return Set(1.0, CatchUpAction.None, null);

        var wellBehind = buffer > (speedUpAbove + resyncAbove) / 2;
        return Set(wellBehind ? options.FastCatchUpSpeed : options.CatchUpSpeed, CatchUpAction.None, null);
    }

    private CatchUpDecision Set(double speed, CatchUpAction action, string? notice)
    {
        Speed = speed;
        return new CatchUpDecision(speed, action, notice);
    }
}
