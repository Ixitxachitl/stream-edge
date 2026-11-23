// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Streams;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>Starred channels the user doesn't follow: while live, listed
/// with the open but unfollowed tabs at the top of the list, tab or no tab;
/// otherwise among the offline rows, when those are listed.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The row of each starred channel known not to be followed, by
    /// login: from the last lookup, or as its tab last showed it.</summary>
    private readonly Dictionary<string, LiveStream> _starred = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every followed channel, live or not, asked again at most
    /// every <see cref="OfflineRefresh"/>; false while Twitch hasn't said.</summary>
    private async Task<bool> LoadFollowedChannelsAsync(HelixClient helix, string me)
    {
        if (_followedChannels is not null && DateTimeOffset.Now - _followedChannelsAt < OfflineRefresh) return true;
        try
        {
            _followedChannels = await helix.GetFollowedChannelsAsync(me, _stop.Token);
            _followedChannelsAt = DateTimeOffset.Now;
            _followedLogins = _followedChannels.Select(c => c.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (HttpRequestException)
        {
            // The last answer, if there was one, still stands.
        }
        return _followedChannels is not null;
    }

    /// <summary>Looks up the starred channels that aren't followed (signed
    /// out, that's every starred one): who they are and whether they're
    /// live. One request, however many there are.</summary>
    private async Task RefreshStarredAsync(HelixClient? helix, string? me)
    {
        if (_options.Demo) return;
        var followedLive = Streams.Followed.Select(s => s.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = _settings.Favorites.Where(f => !followedLive.Contains(f)).ToList();
        if (helix is not null && me is not null && wanted.Count > 0)
        {
            if (await LoadFollowedChannelsAsync(helix, me)) wanted.RemoveAll(_followedLogins.Contains);
            // Who's followed isn't known: only the ones already listed.
            else wanted.RemoveAll(f => !_starred.ContainsKey(f));
        }
        foreach (var gone in _starred.Keys.Except(wanted, StringComparer.OrdinalIgnoreCase).ToList()) _starred.Remove(gone);
        if (wanted.Count > 0)
        {
            try
            {
                var rows = await ChannelLookup.FetchAsync(_http, wanted, _stop.Token);
                if (helix is not null) rows = await WithLabelsAsync(helix, rows);
                _starred.Clear();
                // Still starred: the answer can come back after an unstar.
                // An event since the lookup was asked has the last word.
                foreach (var row in rows.Where(r => _settings.IsFavorite(r.Login)))
                    _starred[row.Login] = StarredNow(row, DateTimeOffset.Now);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Rows keep what they showed; the next refresh tries again.
            }
        }
        UpdateWatched();
    }

    /// <summary>Starred, unfollowed channels that are live, for the go-live alert.</summary>
    private IEnumerable<LiveStream> StarredLive => _starred.Values.Where(s => s.IsLive);
}
