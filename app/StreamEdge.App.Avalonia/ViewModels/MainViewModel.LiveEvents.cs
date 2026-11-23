// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Streams;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>Channels going live and offline as it happens
/// (<see cref="StreamEventsClient"/>), rather than at the next poll of the
/// lists: starred channels and open tabs first, then followed ones, as many
/// as a connection takes. The polls go on as before, for the rest and in
/// case the events stop coming.</summary>
public sealed partial class MainViewModel
{
    private StreamEventsClient? _streamEvents;

    /// <summary>Channel id → login, for the channels being listened for.</summary>
    private Dictionary<string, string> _eventLogins = [];

    /// <summary>The followed live channels as Helix last listed them: it
    /// runs a minute or two behind the events, which put it right.</summary>
    private IReadOnlyList<LiveStream> _polledLive = [];

    /// <summary>Recent events against each polled list: one each, since each
    /// list catches up with an event in its own time.</summary>
    private readonly RecentStreamEvents _followedEvents = new(), _starredEvents = new(), _tabEvents = new();

    /// <summary>Latest event per login, so a lookup that comes back after a
    /// newer event doesn't undo it.</summary>
    private readonly Dictionary<string, int> _eventCount = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long after a channel goes live to ask Twitch for its
    /// stream (title, game, viewers), until it has one to give.</summary>
    internal static IReadOnlyList<TimeSpan> WentLiveLookups { get; set; } =
        [TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8)];

    private StreamEventsClient CreateStreamEvents()
    {
        var client = new StreamEventsClient();
        client.WentLive += id => _ui.Post(() => OnStreamEvent(id, live: true));
        client.WentOffline += id => _ui.Post(() => OnStreamEvent(id, live: false));
        client.Start();
        return client;
    }

    /// <summary>Tells the socket which channels to report: starred ones,
    /// open tabs, followed ones that are live, then the other followed ones,
    /// most recently live first, up to what it takes.</summary>
    private void WatchStreamEvents()
    {
        if (_streamEvents is null) return;
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _followedChannels ?? []) ids[c.Login] = c.Id;
        foreach (var s in Streams.Followed.Concat(_starred.Values))
            if (s.UserId is { } id) ids[s.Login] = id;
        foreach (var t in Tabs)
            if (t.RoomId is { } room) ids[t.Login] = room;
        var order = _settings.Favorites
            .Concat(Tabs.Select(t => t.Login))
            .Concat(Streams.Followed.Select(s => s.Login))
            .Concat((_followedChannels ?? [])
                .OrderByDescending(c => LastLiveFor(c.Login)?.StartedAt ?? DateTimeOffset.MinValue)
                .Select(c => c.Login));
        var logins = new Dictionary<string, string>();
        var wanted = new List<string>();
        foreach (var login in order)
        {
            if (wanted.Count == StreamEventsClient.MaxChannels) break;
            if (ids.TryGetValue(login, out var id) && logins.TryAdd(id, login)) wanted.Add(id);
        }
        _eventLogins = logins;
        _streamEvents.Watch(wanted);
    }

    private void OnStreamEvent(string channelId, bool live)
    {
        if (!_eventLogins.TryGetValue(channelId, out var login)) return;
        if (live) _ = LookUpWentLiveAsync(channelId, login);
        else OnWentOffline(login);
    }

    /// <summary>A channel started streaming: its row, as soon as Twitch has
    /// the stream to describe, or failing that as live with what's known of
    /// the channel.</summary>
    private async Task LookUpWentLiveAsync(string channelId, string login)
    {
        var mine = _eventCount[login] = _eventCount.GetValueOrDefault(login) + 1;
        // An open tab plays now rather than at its next half-minute check.
        TabFor(login)?.Session?.RetryNow();
        LiveStream? row = null;
        try
        {
            foreach (var wait in WentLiveLookups)
            {
                await Task.Delay(wait, _stop.Token);
                try
                {
                    row = (await ChannelLookup.FetchAsync(_http, [login], _stop.Token)).FirstOrDefault(r => r.IsLive);
                }
                catch (HttpRequestException)
                {
                }
                if (row is not null || _eventCount.GetValueOrDefault(login) != mine) break;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        // It has gone offline (or live again) since.
        if (_eventCount.GetValueOrDefault(login) != mine) return;
        if (row is null)
        {
            var known = _starred.GetValueOrDefault(login);
            var tab = TabFor(login);
            row = new LiveStream(login,
                known?.DisplayName ?? _followedChannels?.FirstOrDefault(c => SameLogin(c.Login, login))?.DisplayName
                ?? tab?.DisplayName ?? login,
                "", "", -1, DateTimeOffset.Now, UserId: channelId,
                ProfileImage: known?.ProfileImage ?? _avatars.GetValueOrDefault(channelId) ?? tab?.Player.ProfileImage);
        }
        OnWentLive(row);
    }

    private static bool SameLogin(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A channel is live, as of now, with this row: listed (and
    /// alerted, if starred) without waiting for the lists to say so.</summary>
    public void OnWentLive(LiveStream row)
    {
        var now = DateTimeOffset.Now;
        _eventCount[row.Login] = _eventCount.GetValueOrDefault(row.Login) + 1;
        foreach (var events in new[] { _followedEvents, _starredEvents, _tabEvents }) events.WentLive(row, now);
        if (TabFor(row.Login) is { } tab)
        {
            // Its tab's row says so too, before the video has started.
            _watchedLive[row.Login] = row;
            tab.Session?.RetryNow();
        }
        ApplyStreamEvents();
    }

    /// <summary>A channel stopped streaming: off the live rows at once.</summary>
    public void OnWentOffline(string login)
    {
        var now = DateTimeOffset.Now;
        _eventCount[login] = _eventCount.GetValueOrDefault(login) + 1;
        foreach (var events in new[] { _followedEvents, _starredEvents, _tabEvents }) events.WentOffline(login, now);
        // Its last broadcast is now the one that just ended.
        _lastLive.Remove(login);
        _watchedLive.Remove(login);
        ApplyStreamEvents();
        // Its offline row (when it's followed, and those are listed) and
        // when it was last live come with a refresh; one for a burst of these.
        RefreshSoon();
    }

    /// <summary>The followed live list as polled, put right by the events
    /// since: a channel that went live is added if it's one of the followed.</summary>
    private IReadOnlyList<LiveStream> FollowedLive(DateTimeOffset now) =>
        _followedEvents.Apply(_polledLive, _followedLogins.Contains, now);

    /// <summary>A starred, unfollowed channel's row as looked up, put right
    /// by an event since.</summary>
    private LiveStream StarredNow(LiveStream row, DateTimeOffset now) =>
        _starredEvents.Live(row.Login, row, now) ?? (row.IsLive ? NotLive(row) : row);

    private static LiveStream NotLive(LiveStream row) =>
        new(row.Login, row.DisplayName, "", "", -1, default, UserId: row.UserId, ProfileImage: row.ProfileImage, IsLive: false);

    /// <summary>Redraws the lists after an event.</summary>
    private void ApplyStreamEvents()
    {
        var now = DateTimeOffset.Now;
        var followed = FollowedLive(now);
        if (!followed.Select(s => s.Login).SequenceEqual(Streams.Followed.Select(s => s.Login)))
            Streams.Set([.. followed.Select(WithSharedChat)], now);
        foreach (var (login, row) in _starred.ToList()) _starred[login] = StarredNow(row, now);
        foreach (var tab in Tabs) ShowChannelInfo(tab);
        UpdateWatched();
        // Not before the first refresh has said who was live already.
        if (_favouritesLive is not null) AlertGoLive([.. Streams.Followed, .. StarredLive]);
    }

    private bool _refreshQueued;

    /// <summary>A refresh in a moment, once however many ask for it.</summary>
    private void RefreshSoon()
    {
        // Not on the heels of the last one: a stream that keeps dropping
        // and coming back would have the lists reloaded every time.
        if (_refreshQueued || _options.Demo || DateTimeOffset.Now - _lastStreamRefresh < TimeSpan.FromSeconds(15)) return;
        _refreshQueued = true;
        _ = Task.Delay(TimeSpan.FromSeconds(2), _stop.Token).ContinueWith(t => _ui.Post(() =>
        {
            _refreshQueued = false;
            if (!t.IsCanceled) _ = RefreshStreamsAsync();
        }), TaskScheduler.Default);
    }
}
