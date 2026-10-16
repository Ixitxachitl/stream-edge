// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace StreamEdge.Streams;

/// <summary>One suggestion under the channel search box. <see cref="IsLive"/>
/// is null when unknown (signed out, the channel looked up by name only).</summary>
public sealed record ChannelSuggestion(
    string Login,
    string DisplayName,
    bool? IsLive = null,
    string Game = "",
    Uri? ProfileImage = null)
{
    /// <summary>"Go to channel" for the name exactly as typed, which may not
    /// exist: joining it is still the user's call.</summary>
    public bool IsTyped { get; init; }

    public string Initial => DisplayName.Length > 0 ? char.ToUpperInvariant(DisplayName[0]).ToString() : "?";

    /// <summary>Under the name: the game while live, else whether it's offline.</summary>
    public string Detail => IsTyped ? "Join this channel"
        : IsLive == true ? (Game.Length > 0 ? Game : "Live")
        : IsLive == false ? "Offline"
        : "";

    public bool ShowsLiveDot => IsLive == true;
}

/// <summary>Turning what was typed in the search box into channel suggestions.</summary>
public static partial class ChannelSearch
{
    [GeneratedRegex("^[A-Za-z0-9_]{1,25}$")]
    private static partial Regex LoginPattern();

    /// <summary>A Twitch login: letters, digits and underscores, up to 25.</summary>
    public static bool IsValidLogin(string text) => LoginPattern().IsMatch(text);

    /// <summary>The query without what people paste or type around a name:
    /// "#name", "@name", "twitch.tv/name".</summary>
    public static string Normalize(string query)
    {
        var q = query.Trim();
        var slash = q.IndexOf("twitch.tv/", StringComparison.OrdinalIgnoreCase);
        if (slash >= 0)
        {
            q = q[(slash + "twitch.tv/".Length)..];
            var end = q.IndexOfAny(['/', '?', '#']);
            if (end >= 0) q = q[..end];
        }
        return q.TrimStart('#', '@').Trim();
    }

    /// <summary>Whether a name could be what's being typed: the login or the
    /// display name contains it.</summary>
    public static bool Matches(string login, string displayName, string query) =>
        login.Contains(query, StringComparison.OrdinalIgnoreCase)
        || displayName.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>Best first, as twitch.tv's search box: the exact name, then
    /// names that start with the query, then the rest; live before offline
    /// within each, otherwise in the order given. The first of duplicates
    /// wins, but borrows what a later one knows (live state, avatar).</summary>
    public static IReadOnlyList<ChannelSuggestion> Rank(IEnumerable<ChannelSuggestion> candidates, string query, int max)
    {
        if (query.Length == 0) return [];
        var merged = new List<ChannelSuggestion>();
        var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (!at.TryGetValue(c.Login, out var i))
            {
                at[c.Login] = merged.Count;
                merged.Add(c);
                continue;
            }
            var kept = merged[i];
            merged[i] = kept with
            {
                IsLive = kept.IsLive ?? c.IsLive,
                Game = kept.Game.Length > 0 ? kept.Game : c.Game,
                ProfileImage = kept.ProfileImage ?? c.ProfileImage,
            };
        }
        return [.. merged
            .OrderBy(c => string.Equals(c.Login, query, StringComparison.OrdinalIgnoreCase) ? 0
                : c.Login.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                  || c.DisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(c => c.IsLive == true ? 0 : 1)
            .Take(max)];
    }
}
