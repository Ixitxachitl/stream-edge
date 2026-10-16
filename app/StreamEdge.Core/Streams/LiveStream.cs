// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Ui;

namespace StreamEdge.Streams;

/// <summary>One live channel in the stream list, as Helix "Get Followed
/// Streams" describes it, plus the channel's avatar from "Get Users".
/// Channels open in a tab but not followed are listed too, live or not
/// (<see cref="IsLive"/>); a negative <see cref="Viewers"/> is unknown.</summary>
public sealed record LiveStream(
    string Login,
    string DisplayName,
    string Title,
    string Game,
    int Viewers,
    DateTimeOffset StartedAt,
    bool IsFavorite = false,
    string? UserId = null,
    Uri? ProfileImage = null,
    bool IsLive = true)
{
    public TimeSpan Uptime(DateTimeOffset now) => now - StartedAt;

    /// <summary>The list header: "CometCrumb (57 | 2.4h)".</summary>
    public string Header(DateTimeOffset now) =>
        !IsLive || Viewers < 0 ? DisplayName : $"{DisplayName} ({UiFormats.Viewers(Viewers)} | {UiFormats.Uptime(Uptime(now))})";
}

public enum StreamSortOrder
{
    Viewers,
    Name,
    Uptime,
    Game,
}

public static class StreamSort
{
    /// <summary>Favourites first, then the chosen order; ties fall back to
    /// name so the list doesn't shuffle on every refresh.</summary>
    public static IEnumerable<LiveStream> Apply(IEnumerable<LiveStream> streams, StreamSortOrder order)
    {
        var favouritesFirst = streams.OrderByDescending(s => s.IsFavorite);
        var sorted = order switch
        {
            StreamSortOrder.Viewers => favouritesFirst.ThenByDescending(s => s.Viewers),
            // Longest-running first: oldest start time.
            StreamSortOrder.Uptime => favouritesFirst.ThenBy(s => s.StartedAt),
            StreamSortOrder.Game => favouritesFirst.ThenBy(s => s.Game, StringComparer.OrdinalIgnoreCase),
            _ => favouritesFirst,
        };
        return sorted.ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase);
    }
}
