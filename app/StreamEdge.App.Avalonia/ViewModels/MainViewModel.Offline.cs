// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Streams;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>Offline channels in the list, when they were last live, and
/// content labels for live ones.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Who's followed and when channels were last live change slowly;
    /// both are fetched again at most this often.</summary>
    private static readonly TimeSpan OfflineRefresh = TimeSpan.FromMinutes(10);

    private IReadOnlyList<ChannelRef>? _followedChannels;
    private DateTimeOffset _followedChannelsAt;

    /// <summary>Last broadcasts by login, and when each was fetched.</summary>
    private readonly Dictionary<string, (LastBroadcast? Last, DateTimeOffset At)> _lastLive = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lastLiveLoading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Content classification labels for the live channels, from
    /// Helix "Get Channel Information" (one request per hundred).</summary>
    private async Task<IReadOnlyList<LiveStream>> WithLabelsAsync(HelixClient helix, IReadOnlyList<LiveStream> live)
    {
        var ids = live.Select(s => s.UserId).OfType<string>().ToList();
        if (ids.Count == 0) return live;
        try
        {
            var labels = await helix.GetContentLabelsAsync(ids, _stop.Token);
            return [.. live.Select(s => s.UserId is { } id && labels.TryGetValue(id, out var l) ? s with { ContentLabels = l } : s)];
        }
        catch (HttpRequestException)
        {
            return live;
        }
    }

    private LastBroadcast? LastLiveFor(string login) => _lastLive.TryGetValue(login, out var known) ? known.Last : null;

    /// <summary>Fetches last broadcasts for these logins where unknown or
    /// stale; true if any were fetched.</summary>
    private async Task<bool> LoadLastLiveAsync(IEnumerable<string> logins)
    {
        var now = DateTimeOffset.Now;
        var wanted = logins.Where(l => (!_lastLive.TryGetValue(l, out var k) || now - k.At >= OfflineRefresh) && _lastLiveLoading.Add(l))
            .ToList();
        if (wanted.Count == 0) return false;
        try
        {
            var found = await LastBroadcasts.FetchAsync(_http, wanted, _stop.Token);
            foreach (var login in wanted) _lastLive[login] = (found.GetValueOrDefault(login), now);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            foreach (var login in wanted) _lastLiveLoading.Remove(login);
        }
    }

    /// <summary>Followed channels that aren't live, for the list's offline
    /// rows: avatars, and when each was last live, which orders them.</summary>
    private async Task RefreshOfflineAsync(HelixClient helix, string me, IReadOnlyList<LiveStream> live)
    {
        if (!Streams.ShowOffline || _options.Demo) return;
        try
        {
            if (_followedChannels is null || DateTimeOffset.Now - _followedChannelsAt >= OfflineRefresh)
            {
                _followedChannels = await helix.GetFollowedChannelsAsync(me, _stop.Token);
                _followedChannelsAt = DateTimeOffset.Now;
            }
        }
        catch (HttpRequestException)
        {
            if (_followedChannels is null) return;
        }
        var liveLogins = live.Select(s => s.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var offline = _followedChannels.Where(c => !liveLogins.Contains(c.Login)).ToList();
        await LoadLastLiveAsync(offline.Select(c => c.Login));
        var rows = offline.Select(c => new LiveStream(c.Login, c.DisplayName, "", "", -1, default,
            UserId: c.Id, IsLive: false, LastLive: LastLiveFor(c.Login))).ToList();
        Streams.SetOffline(await WithAvatarsAsync(helix, rows), DateTimeOffset.Now);
    }

    /// <summary>The toggle went on: fill the offline rows now rather than at
    /// the next refresh.</summary>
    private void OnShowOfflineChanged()
    {
        if (Streams.ShowOffline) _ = RefreshStreamsAsync();
    }

    /// <summary>Open tabs whose channel is offline get their last broadcast
    /// for the hover card too, fetched once in a while.</summary>
    private async Task LoadWatchedLastLiveAsync(IEnumerable<string> offlineLogins)
    {
        if (_options.Demo) return;
        if (await LoadLastLiveAsync(offlineLogins)) UpdateWatched();
    }
}
