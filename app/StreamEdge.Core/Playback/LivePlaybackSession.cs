// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;

namespace StreamEdge.Playback;

public enum PlaybackState
{
    Stopped,
    Resolving,
    Playing,
    Offline,
    Error,
}

/// <summary>How much of a stream a tab needs, which picks the variant.</summary>
public enum PlaybackTier
{
    /// <summary>The tab in front: the user's quality, with catch-up steps.</summary>
    Full,
    /// <summary>A picture-in-picture thumbnail: the smallest video variant.</summary>
    Thumbnail,
    /// <summary>A background tab with picture-in-picture off: nothing plays
    /// or downloads until it comes back.</summary>
    Off,
}

/// <summary>A snapshot for the UI.</summary>
public sealed record PlaybackStatus(
    PlaybackState State,
    string? Quality,
    double BufferSeconds,
    double? LatencySeconds,
    double Speed,
    bool AdFree,
    string Message)
{
    public static readonly PlaybackStatus Stopped = new(PlaybackState.Stopped, null, 0, null, 1, false, "");
}

/// <summary>Plays one live channel at the lowest latency it can hold:
/// resolves the stream (with the optional ad-free auth-token), feeds the
/// player through <see cref="LiveHlsReader"/> + <see cref="LocalStreamServer"/>,
/// and applies <see cref="CatchUpController"/>'s decisions.</summary>
/// <remarks>Not thread-safe: call everything, including <see cref="Tick"/>,
/// from one thread (the UI thread in the app). The reader runs on the thread
/// pool and only publishes plain fields.</remarks>
public sealed class LivePlaybackSession : IAsyncDisposable
{
    private static readonly TimeSpan LoadGrace = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan OfflineRetry = TimeSpan.FromSeconds(30);
    /// <summary>Playing with no new bytes for this long counts as stuck.
    /// Generous: a prefetch segment alone can take a few seconds to start.</summary>
    internal static TimeSpan NoProgressTimeout { get; set; } = TimeSpan.FromSeconds(12);

    /// <summary>Playing with the playback position stuck for this long counts
    /// as frozen, however many bytes are arriving. Longer than the bytes
    /// watchdog: a slow connection may buffer for a while before resuming.</summary>
    internal static TimeSpan NoPlaybackTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Audio and video further apart than this…</summary>
    internal const double SyncTolerance = 0.3;

    /// <summary>…for this long (not a passing blip) reloads at the live
    /// edge, which starts the two together again.</summary>
    internal static TimeSpan OutOfSyncFor { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>At most one such reload this often, so a machine that can't
    /// keep up at all isn't reloaded over and over.</summary>
    internal static TimeSpan SyncReloadCooldown { get; set; } = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly IVideoPlayer _player;
    private readonly LocalStreamServer _server;
    private readonly CatchUpController _controller;
    private CancellationTokenSource _cts = new();
    private string? _login;
    private string? _authToken;
    private int _preferredIndex;
    private int _loadCount;
    private DateTimeOffset _loadedAt;
    private DateTimeOffset _retryAt = DateTimeOffset.MaxValue;
    private TimeSpan _retryDelay = TimeSpan.FromSeconds(2);
    private volatile LiveHlsReader? _reader;
    private volatile bool _playlistGone;

    public LivePlaybackSession(HttpClient http, IVideoPlayer player, CatchUpOptions? options = null)
    {
        _http = http;
        _player = player;
        _controller = new CatchUpController(options ?? new CatchUpOptions());
        // Only the newest load's request is served (see LocalStreamServer).
        _server = new LocalStreamServer(ServeAsync,
            target => target.EndsWith("?n=" + Volatile.Read(ref _loadCount), StringComparison.Ordinal));
    }

    /// <summary>"source", "720p60", "480p"…; matched loosely, and a cap
    /// where the channel doesn't have it (see <see cref="HlsPlaylist.Pick"/>).
    /// Changing it while playing moves there, unless this channel's quality
    /// was picked by hand.</summary>
    public string? PreferredQuality
    {
        get => _preferredQuality;
        set
        {
            if (_preferredQuality == value) return;
            _preferredQuality = value;
            if (_manualPick || Variants.Count == 0) return;
            var index = HlsPlaylist.Pick(Variants, value);
            if (index == _preferredIndex) return;
            _preferredIndex = index;
            _controller.Reset(DateTimeOffset.UtcNow, keepQuality: false);
            _controller.MaxDowngrade = MaxDowngradeFrom(index);
            if (Status.State == PlaybackState.Playing) ChangeVariant(DateTimeOffset.UtcNow);
        }
    }

    private string? _preferredQuality;

    /// <summary>The user picked this channel's quality from the menu.</summary>
    private bool _manualPick;

    public IReadOnlyList<HlsVariant> Variants { get; private set; } = [];

    public HlsVariant? Current { get; private set; }

    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Stopped;

    /// <summary>The channel was live when last checked: playing now, or it
    /// was when the tab was turned <see cref="PlaybackTier.Off"/> (it isn't
    /// checked again until it's back on).</summary>
    public bool LastKnownLive => Status.State == PlaybackState.Playing || (_tier == PlaybackTier.Off && _liveWhenHalted);

    private bool _liveWhenHalted;

    public event Action? StatusChanged;

    /// <summary>Raised on the reader's thread; marshal before touching UI.</summary>
    public event Action<string>? Notice;

    public async Task StartAsync(string login, string? authToken)
    {
        Stop();
        _login = login;
        _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken;
        _manualPick = false;
        _controller.Reset(DateTimeOffset.UtcNow, keepQuality: false);
        await ResolveAsync().ConfigureAwait(true);
    }

    /// <summary>Stops playing and downloading but keeps the channel, for
    /// <see cref="PlaybackTier.Off"/>.</summary>
    private void Halt()
    {
        _liveWhenHalted = Status.State == PlaybackState.Playing;
        _fellBehind = false;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _server.Disconnect();
        _player.Stop();
        Current = null;
        _probe = null;
        _probing = null;
        _retryAt = DateTimeOffset.MaxValue;
        Publish(Status with { State = PlaybackState.Stopped, Quality = null, Speed = 1, Message = "" });
    }

    public void Stop()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _server.Disconnect();
        _player.Stop();
        _login = null;
        Current = null;
        Variants = [];
        _retryAt = DateTimeOffset.MaxValue;
        Publish(PlaybackStatus.Stopped);
    }

    /// <summary>Manual quality pick; also becomes the preference for this
    /// channel so catch-up steps are measured from it.</summary>
    public void SelectQuality(int index)
    {
        if (index < 0 || index >= Variants.Count) return;
        _preferredIndex = index;
        _manualPick = true;
        _controller.Reset(DateTimeOffset.UtcNow, keepQuality: false);
        _controller.MaxDowngrade = MaxDowngradeFrom(index);
        ChangeVariant(DateTimeOffset.UtcNow);
    }

    /// <summary>Retry an offline or failed channel now, rather than at the
    /// next scheduled attempt; a playing one rejoins the live edge.</summary>
    public void RetryNow()
    {
        if (_login is null) return;
        if (Status.State is PlaybackState.Offline or PlaybackState.Error) _retryAt = DateTimeOffset.UtcNow;
        else if (Status.State == PlaybackState.Playing) Reload(DateTimeOffset.UtcNow);
    }

    /// <summary>Closed captions (CEA-608 carried in the video). Only the
    /// front tab shows them; a thumbnail is too small to read.</summary>
    public bool Captions
    {
        get => _captions;
        set
        {
            _captions = value;
            _player.SetCaptions(value && _tier == PlaybackTier.Full);
        }
    }

    private bool _captions;

    private async Task ResolveAsync()
    {
        if (_login is not { } login || _tier == PlaybackTier.Off) return;
        var ct = _cts.Token;
        Publish(Status with { State = PlaybackState.Resolving, Message = "Resolving stream…" });
        try
        {
            var token = await new TwitchPlayback(_http).GetAccessTokenAsync(login, _authToken, ct).ConfigureAwait(true);
            if (token is null)
            {
                GoOffline();
                return;
            }
            var master = TwitchPlayback.UsherUrl(login, token);
            using var response = await _http.GetAsync(master, ct).ConfigureAwait(true);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                GoOffline();
                return;
            }
            response.EnsureSuccessStatusCode();
            Variants = HlsPlaylist.ParseMaster(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(true), master);
            if (Variants.Count == 0)
            {
                GoOffline();
                return;
            }
            // Keep a manual pick across token refreshes and time spent off;
            // otherwise use the preference.
            if (!_manualPick || _preferredIndex >= Variants.Count)
                _preferredIndex = HlsPlaylist.Pick(Variants, PreferredQuality);
            _controller.MaxDowngrade = MaxDowngradeFrom(_preferredIndex);
            _server.Start();
            _retryDelay = TimeSpan.FromSeconds(2);
            Reload(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Anything else, including HttpClient's timeout (which surfaces
            // as a cancellation nobody asked for), must schedule a retry: the
            // retry timer is already cleared, so returning here would leave
            // the tab stuck for good.
            ScheduleRetry(DateTimeOffset.UtcNow, ex is OperationCanceledException
                ? "Timed out reaching Twitch, retrying…"
                : "Network error: " + ex.Message);
        }
    }

    /// <summary>Video variants below <paramref name="index"/>; audio-only
    /// is never chosen automatically.</summary>
    private int MaxDowngradeFrom(int index) =>
        _tier != PlaybackTier.Full ? 0 : Math.Max(0, Variants.Count(v => !v.IsAudioOnly) - 1 - index);

    /// <summary>Front, thumbnail or off. Between front and thumbnail it moves
    /// to the matching variant, in place where it can; off stops everything,
    /// and coming back reconnects at the live edge with a fresh token.</summary>
    public PlaybackTier Tier
    {
        get => _tier;
        set
        {
            if (_tier == value) return;
            var wasOff = _tier == PlaybackTier.Off;
            _tier = value;
            if (value == PlaybackTier.Off)
            {
                Halt();
                return;
            }
            if (wasOff)
            {
                _controller.Reset(DateTimeOffset.UtcNow, keepQuality: false);
                _player.SetCaptions(_captions && value == PlaybackTier.Full);
                _ = ResolveAsync();
                return;
            }
            // Quality steps only make sense for the tab being watched; other
            // tiers already sit at the bottom. Jumping to live still applies.
            _controller.Reset(DateTimeOffset.UtcNow, keepQuality: value == PlaybackTier.Full);
            _controller.MaxDowngrade = MaxDowngradeFrom(_preferredIndex);
            _player.SetCaptions(_captions && value == PlaybackTier.Full);
            if (Variants.Count > 0 && Status.State == PlaybackState.Playing) ChangeVariant(DateTimeOffset.UtcNow);
        }
    }

    private PlaybackTier _tier;

    /// <summary>Variant index for the current tier.</summary>
    private int TierIndex()
    {
        var lastVideo = Variants.Count - 1;
        while (lastVideo > 0 && Variants[lastVideo].IsAudioOnly) lastVideo--;
        return _tier switch
        {
            PlaybackTier.Thumbnail or PlaybackTier.Off => lastVideo,
            _ => Math.Min(_preferredIndex + _controller.QualityOffset, Math.Max(0, Variants.Count - 1)),
        };
    }

    /// <summary>Moves to the variant the tier and catch-up call for. Between
    /// two video variants that's seamless: the running reader carries on
    /// from the new playlist at the next segment, and the player only sees
    /// the resolution change. To or from audio-only the video track appears
    /// or vanishes, which needs a reload.</summary>
    private DateTimeOffset? _outOfSyncSince;
    private DateTimeOffset _lastSyncReload = DateTimeOffset.MinValue;

    /// <summary>Audio and video drifted apart and stayed apart (the decoder
    /// fell behind, or an ad break or quality change left them offset):
    /// reload at the live edge. Only for the tab being watched and heard,
    /// and not while stalled or in an ad. True if it reloaded.</summary>
    private bool CheckSync(DateTimeOffset now, VideoPlayerStats stats, LiveHlsReader? reader)
    {
        var apart = _tier == PlaybackTier.Full && !stats.Stalled && !(reader?.InAdBreak ?? false)
                    && now - _loadedAt >= LoadGrace && Math.Abs(stats.AvSync ?? 0) > SyncTolerance;
        if (!apart)
        {
            _outOfSyncSince = null;
            return false;
        }
        _outOfSyncSince ??= now;
        if (now - _outOfSyncSince < OutOfSyncFor || now - _lastSyncReload < SyncReloadCooldown) return false;
        _outOfSyncSince = null;
        _lastSyncReload = now;
        _player.ShowMessage("Resyncing audio");
        Reload(now);
        return true;
    }

    private void ChangeVariant(DateTimeOffset now)
    {
        if (Variants.Count == 0) return;
        var target = Variants[TierIndex()];
        if (target == Current) return;
        ApplyResumeBuffer();
        if (_reader is { IsFragmentedMp4: false } reader && Current is { IsAudioOnly: false } && !target.IsAudioOnly
            && Status.State == PlaybackState.Playing)
        {
            Current = target;
            reader.SwitchTo(target.Uri);
            ResetDelivery();
            Publish(Status with { Quality = target.Name });
            return;
        }
        Reload(now);
    }

    // ----- Bandwidth --------------------------------------------------------

    /// <summary>How long delivery is watched before judging it.</summary>
    internal static TimeSpan DeliveryWindow { get; set; } = TimeSpan.FromSeconds(8);

    private readonly Queue<(DateTimeOffset At, double Media)> _delivery = new();

    /// <summary>Seconds of video delivered per second of real time over the
    /// last <see cref="DeliveryWindow"/>, or null until there's a full window.</summary>
    public double? DeliveryRate { get; private set; }

    private void ResetDelivery()
    {
        _delivery.Clear();
        DeliveryRate = null;
    }

    /// <summary>The connection can't keep up with this quality: rather than
    /// wait for a run of stalls and step down one level at a time (a reload
    /// each, on MP4 channels), go straight to the highest quality that fits
    /// what's actually arriving. True if it changed quality.</summary>
    private bool CheckBandwidth(DateTimeOffset now, LiveHlsReader? reader)
    {
        if (reader is null || reader.InAdBreak || _tier != PlaybackTier.Full || Current is not { } current)
        {
            ResetDelivery();
            return false;
        }
        _delivery.Enqueue((now, reader.MediaSecondsWritten));
        // Keep one sample from just beyond the window, so a full window always
        // spans at least the whole period, whatever the tick timing.
        while (_delivery.Count > 2 && now - _delivery.ElementAt(1).At >= DeliveryWindow) _delivery.Dequeue();
        var (firstAt, firstMedia) = _delivery.Peek();
        var span = (now - firstAt).TotalSeconds;
        if (span < DeliveryWindow.TotalSeconds) return false;
        var rate = (reader.MediaSecondsWritten - firstMedia) / span;
        DeliveryRate = rate;
        if (rate >= 0.85) return false;

        // Bitrate that fits: what this quality needs, scaled by how much of
        // it is getting through, with headroom.
        var fits = current.Bandwidth * Math.Max(rate, 0.05) * 0.8;
        var index = Variants.ToList().IndexOf(current);
        var target = -1;
        for (var i = index + 1; i < Variants.Count; i++)
        {
            if (Variants[i].IsAudioOnly) continue;
            target = i;
            if (Variants[i].Bandwidth <= fits) break;
        }
        if (target < 0 || !_controller.SetQualityOffset(target - _preferredIndex, now)) return false;

        _player.ShowMessage($"Slow connection: switching to {Variants[target].Name}");
        ChangeVariant(now);
        ResetDelivery();
        return true;
    }

    // ----- Stepping back up ---------------------------------------------------

    /// <summary>How often, at most, the next quality up is probed while below
    /// the preferred one.</summary>
    internal static TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A probe segment downloading in at most this share of its
    /// length, alongside the running stream, says the step up fits with room
    /// to spare.</summary>
    private const double ProbeFits = 0.6;

    private Task<ProbeResult?>? _probe;
    private HlsVariant? _probing;
    private DateTimeOffset _lastProbe = DateTimeOffset.MinValue;

    /// <summary>While below the preferred quality: now and then, time one
    /// segment of the next quality up, and step up at once if it fits rather
    /// than wait out the stall-free timer. True if it changed quality.</summary>
    private bool CheckProbe(DateTimeOffset now, LiveHlsReader? reader)
    {
        if (_probe is { IsCompleted: true } done)
        {
            _probe = null;
            var target = _probing;
            _probing = null;
            if (!done.IsCompletedSuccessfully || done.Result is not { } result || target is null || Current is not { } current)
                return false;
            var variants = Variants.ToList();
            // Only if it's still the step above what's playing.
            if (result.LoadRatio > ProbeFits || variants.IndexOf(target) != UpFrom(variants.IndexOf(current))
                || !_controller.SetQualityOffset(_controller.QualityOffset - 1, now))
                return false;
            _player.ShowMessage($"Connection recovered: raising quality to {target.Name}");
            ChangeVariant(now);
            return true;
        }
        if (_probe is not null || _tier != PlaybackTier.Full || reader is null || reader.InAdBreak
            || Current is not { } playing || now - _lastProbe < ProbeInterval || !_controller.MayProbe(now))
            return false;
        var up = UpFrom(Variants.ToList().IndexOf(playing));
        if (up < 0 || up < _preferredIndex) return false;
        _lastProbe = now;
        _probing = Variants[up];
        _probe = BandwidthProbe.MeasureAsync(_http, Variants[up].Uri, _cts.Token);
        return false;
    }

    /// <summary>The next video variant above <paramref name="index"/>, or -1.</summary>
    private int UpFrom(int index)
    {
        var up = index - 1;
        while (up >= 0 && Variants[up].IsAudioOnly) up--;
        return up;
    }

    private void Reload(DateTimeOffset now)
    {
        if (_tier == PlaybackTier.Off) return;
        ResetDelivery();
        ApplyResumeBuffer();
        var index = TierIndex();
        Current = Variants.Count > 0 ? Variants[index] : null;
        if (Current is null) return;
        _playlistGone = false;
        _loadedAt = now;
        // Both watchdogs measure from this load.
        _lastProgressAt = _lastPlayedAt = now;
        _lastPosition = null;
        _retryAt = DateTimeOffset.MaxValue;
        _player.SetSpeed(1.0);
        // A fresh URL each time makes the player drop its buffer and reconnect,
        // which is what starts a new reader at the live edge.
        _player.Load(new Uri(_server.Url, $"?n={++_loadCount}"));
        Publish(Status with
        {
            State = PlaybackState.Playing,
            Quality = Current.Name,
            AdFree = _authToken is not null,
            Speed = 1,
            Message = "",
        });
    }

    private async Task ServeAsync(Stream output, CancellationToken ct)
    {
        if (Current is not { } variant) return;
        var reader = new LiveHlsReader(_http, variant.Uri);
        reader.Notice += m => Notice?.Invoke(m);
        _reader = reader;
        try
        {
            await reader.RunAsync(output, ct).ConfigureAwait(false);
        }
        catch (PlaylistGoneException)
        {
            // Token expired or stream ended; Tick notices the player going
            // idle and re-resolves.
            _playlistGone = true;
        }
        catch (FellBehindException)
        {
            // Tick reloads at the live edge.
            _fellBehind = true;
        }
        finally
        {
            if (_reader == reader) _reader = null;
        }
    }

    /// <summary>Call a few times a second.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (_login is null) return;

        if (now >= _retryAt)
        {
            _retryAt = DateTimeOffset.MaxValue;
            _ = ResolveAsync();
            return;
        }
        if (Status.State != PlaybackState.Playing) return;

        var stats = _player.GetStats();
        var reader = _reader;

        // The reader lost its place after an outage: a clean load at the
        // live edge rather than a jump in time mid-stream.
        if (_fellBehind)
        {
            _fellBehind = false;
            _player.ShowMessage("Rejoining live");
            Reload(now);
            return;
        }

        // The player went idle: the reader hit the end of the playlist or
        // its URL expired. Resolve again rather than leave a black pane.
        if (stats.Idle && now - _loadedAt > LoadGrace)
        {
            ScheduleRetry(now, _playlistGone ? "Reconnecting…" : "Stream interrupted, reconnecting…");
            return;
        }

        // Watchdog: playing, but no bytes for a while (a stalled connection,
        // a CDN edge that stopped answering, a token that quietly expired).
        // Reconnect from scratch, with a fresh token.
        var bytes = reader?.BytesWritten ?? 0;
        if (bytes != _lastBytes || now - _loadedAt < LoadGrace)
        {
            _lastBytes = bytes;
            _lastProgressAt = now;
        }
        else if (now - _lastProgressAt > NoProgressTimeout && !(reader?.InAdBreak ?? false))
        {
            Reconnect(now, "Stream stalled, reconnecting…");
            return;
        }

        // And the other end: bytes going in, but the playback position not
        // moving (a demuxer that can't make sense of what it's given, or a
        // connection that trickles just enough to look alive). The bytes
        // check above can't see either; this does, whatever the cause.
        var position = stats.Position;
        if (position != _lastPosition || now - _loadedAt < LoadGrace || (reader?.InAdBreak ?? false))
        {
            _lastPosition = position;
            _lastPlayedAt = now;
        }
        else if (now - _lastPlayedAt > NoPlaybackTimeout)
        {
            Reconnect(now, "Playback stuck, reconnecting…");
            return;
        }

        if (CheckSync(now, stats, reader)) return;
        if (CheckBandwidth(now, reader)) return;
        if (CheckProbe(now, reader)) return;

        var decision = _controller.Update(new PlaybackSample(
            now, stats.BufferSeconds, stats.Stalled, reader?.SegmentsBehind ?? 0, reader?.InAdBreak ?? false));

        if (decision.Notice is { } notice) _player.ShowMessage(notice);
        ApplyResumeBuffer();
        switch (decision.Action)
        {
            case CatchUpAction.Resync:
                Reload(now);
                break;
            case CatchUpAction.QualityDown:
            case CatchUpAction.QualityUp:
                ChangeVariant(now);
                break;
            default:
                if (Math.Abs(decision.Speed - Status.Speed) > 0.001) _player.SetSpeed(decision.Speed);
                break;
        }

        var latency = reader?.FeedWallClock(now) is { } fed
            ? (now - fed).TotalSeconds + stats.BufferSeconds
            : (double?)null;
        Publish(Status with
        {
            BufferSeconds = stats.BufferSeconds,
            LatencySeconds = latency is > 0 and < 120 ? latency : null,
            Speed = decision.Speed,
            Quality = Current?.Name,
            Message = decision.Notice ?? (stats.Stalled ? "Buffering…" : reader?.InAdBreak == true ? "Ad break" : ""),
        });

        // An "Ad break" label on the video while ads play, as twitch.tv
        // shows one, so an ad isn.t mistaken for the stream.
        var inAd = reader?.InAdBreak == true;
        if (inAd != _showingAd)
        {
            _showingAd = inAd;
            _player.SetStatusText(inAd ? "Ad break" : null);
        }
    }

    private bool _showingAd;
    private long _lastBytes = -1;
    private DateTimeOffset _lastProgressAt;
    private double? _lastPosition;
    private DateTimeOffset _lastPlayedAt;
    private volatile bool _fellBehind;

    /// <summary>Drops the connection and starts over with a fresh token and
    /// fresh connections, at once.</summary>
    private void Reconnect(DateTimeOffset now, string message)
    {
        _server.Disconnect();
        ScheduleRetry(now, message);
        _retryAt = now;
    }

    private double _resumeBuffer = double.NaN;

    /// <summary>Hands the controller's cushion to the player: it waits for
    /// that much before starting or resuming, so a struggling connection
    /// gets a little more buffer to ride out the next hiccup.</summary>
    private void ApplyResumeBuffer()
    {
        var seconds = _controller.ResumeBuffer;
        if (Math.Abs(seconds - _resumeBuffer) < 0.01) return;
        _resumeBuffer = seconds;
        _player.SetResumeBuffer(seconds);
    }

    private void GoOffline()
    {
        Current = null;
        _player.Stop();
        Publish(Status with { State = PlaybackState.Offline, Quality = null, Message = "Offline" });
        _retryAt = DateTimeOffset.UtcNow + OfflineRetry;
    }

    private void ScheduleRetry(DateTimeOffset now, string message)
    {
        Publish(Status with { State = PlaybackState.Error, Message = message });
        _retryAt = now + _retryDelay;
        _retryDelay = TimeSpan.FromSeconds(Math.Min(_retryDelay.TotalSeconds * 2, 30));
    }

    private void Publish(PlaybackStatus status)
    {
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _server.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
