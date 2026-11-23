// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Streams;

/// <summary>What <see cref="StreamEventsClient"/> said lately that a polled
/// list may not show yet: Helix can take a minute or two to list a stream
/// that just started, or to drop one that just ended. An event stands until
/// the poll agrees with it, and for <see cref="Hold"/> at most, after which
/// the poll is believed again.</summary>
public sealed class RecentStreamEvents
{
    public static readonly TimeSpan Hold = TimeSpan.FromMinutes(5);

    /// <summary>By login: the row of a channel that went live, or null for
    /// one that went offline, and when.</summary>
    private readonly Dictionary<string, (LiveStream? Row, DateTimeOffset At)> _events = new(StringComparer.OrdinalIgnoreCase);

    public void WentLive(LiveStream row, DateTimeOffset now) => _events[row.Login] = (row, now);

    public void WentOffline(string login, DateTimeOffset now) => _events[login] = (null, now);

    public void Clear() => _events.Clear();

    /// <summary>The channel's row if it's live as far as is known, else null:
    /// what was polled (null, or not live, for a channel the poll doesn't
    /// have live), unless an event since says otherwise.</summary>
    public LiveStream? Live(string login, LiveStream? polled, DateTimeOffset now)
    {
        if (polled is { IsLive: false }) polled = null;
        if (!_events.TryGetValue(login, out var e)) return polled;
        var agrees = e.Row is null == polled is null;
        if (!agrees && now - e.At <= Hold) return e.Row;
        _events.Remove(login);
        return polled;
    }

    /// <summary>A polled list of live channels, put right by the events:
    /// without the ones that have gone offline, and with the ones that went
    /// live and <paramref name="belongs"/> in this list (by login), last.</summary>
    public IReadOnlyList<LiveStream> Apply(IReadOnlyList<LiveStream> polled, Func<string, bool> belongs, DateTimeOffset now)
    {
        var live = new List<LiveStream>(polled.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in polled)
        {
            seen.Add(s.Login);
            if (Live(s.Login, s, now) is { } row) live.Add(row);
        }
        foreach (var (login, e) in _events.ToList())
        {
            if (seen.Contains(login) || !belongs(login)) continue;
            if (Live(login, null, now) is { } row) live.Add(row);
        }
        return live;
    }
}
