// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamEdge.Chat;

/// <summary>Sections of the viewer list, in twitch.tv's order.</summary>
public enum ChatterRole
{
    Broadcaster,
    Moderator,
    Vip,
    Staff,
    User,
}

public sealed record Chatter(string Login, string DisplayName, ChatterRole Role);

/// <summary>Who is in a channel's chat, as far as chat itself shows: anyone
/// who has said something, plus the NAMES list and JOIN/PART that Twitch
/// sends (with twitch.tv/membership) for channels under about a thousand
/// chatters. Bigger channels get no JOINs, so their list is the people who
/// have spoken since the tab opened; the complete list (<see cref="FetchAsync"/>)
/// is only given to the channel's moderators.</summary>
public sealed class ChatterRoster(string channel)
{
    private readonly Dictionary<string, Chatter> _present = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _present.Count;

    /// <summary>Bumped on every change, so a view can tell when to redraw.</summary>
    public int Version { get; private set; }

    public void Observe(ChatMessage message)
    {
        if (message.Login is not { Length: > 0 } login) return;
        if (message.Kind == ChatMessageKind.Membership)
        {
            if (message.Text.StartsWith("PART", StringComparison.Ordinal))
            {
                if (_present.Remove(login)) Version++;
            }
            else
            {
                Add(login, null, []);
            }
            return;
        }
        // Messages, actions, and user notices (subs, raids) all mean they're here.
        Add(login, message.DisplayName, message.Badges);
    }

    /// <summary>IRC 353: the logins already in chat when we joined.</summary>
    public void AddNames(IEnumerable<string> logins)
    {
        foreach (var login in logins)
            if (login.Length > 0) Add(login, null, []);
    }

    /// <summary>Everyone, sorted into sections and then by name.</summary>
    public IReadOnlyList<Chatter> Snapshot() => Sort(_present.Values);

    /// <summary>The complete list from Helix: every login there, with the
    /// sections chat has shown for them (Helix doesn't say who's a mod).</summary>
    public IReadOnlyList<Chatter> Merge(IEnumerable<(string Login, string DisplayName)> chatters) =>
        Sort(chatters.Select(c => _present.TryGetValue(c.Login, out var known)
            ? known with { DisplayName = c.DisplayName }
            : new Chatter(c.Login, c.DisplayName, RoleOf(c.Login, []))));

    private void Add(string login, string? displayName, IReadOnlyList<string> badges)
    {
        var role = RoleOf(login, badges);
        if (_present.TryGetValue(login, out var known))
        {
            // A JOIN (no badges, no display name) mustn't undo what a message said.
            var merged = known with
            {
                DisplayName = displayName ?? known.DisplayName,
                Role = badges.Count > 0 ? role : (ChatterRole)Math.Min((int)known.Role, (int)role),
            };
            if (merged == known) return;
            _present[login] = merged;
        }
        else
        {
            _present[login] = new Chatter(login.ToLowerInvariant(), displayName ?? login, role);
        }
        Version++;
    }

    private ChatterRole RoleOf(string login, IReadOnlyList<string> badges)
    {
        if (string.Equals(login, channel, StringComparison.OrdinalIgnoreCase)) return ChatterRole.Broadcaster;
        bool Has(string set) => badges.Any(b => b.StartsWith(set + "/", StringComparison.Ordinal));
        return Has("broadcaster") ? ChatterRole.Broadcaster
            : Has("moderator") ? ChatterRole.Moderator
            : Has("vip") ? ChatterRole.Vip
            : Has("staff") || Has("admin") ? ChatterRole.Staff
            : ChatterRole.User;
    }

    private static List<Chatter> Sort(IEnumerable<Chatter> chatters) =>
        [.. chatters.OrderBy(c => c.Role).ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Helix stops paging here: a 10,000-name list is already more
    /// than anyone scrolls, and each page is a request.</summary>
    public const int MaxFetched = 10_000;

    /// <summary>Helix "Get Chatters": everyone connected to the channel's
    /// chat, lurkers included. Only for its broadcaster and moderators, with
    /// <c>moderator:read:chatters</c>; null when Twitch refuses.</summary>
    public static async Task<List<(string Login, string DisplayName)>?> FetchAsync(HttpClient http, string clientId,
        string token, string broadcasterId, string moderatorId, CancellationToken ct)
    {
        var chatters = new List<(string, string)>();
        string? cursor = null;
        do
        {
            var url = "https://api.twitch.tv/helix/chat/chatters?broadcaster_id=" + Uri.EscapeDataString(broadcasterId)
                + "&moderator_id=" + Uri.EscapeDataString(moderatorId) + "&first=1000"
                + (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Client-Id", clientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var page = ParsePage(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (page is null) return null;
            chatters.AddRange(page.Value.Chatters);
            cursor = page.Value.Cursor;
        } while (!string.IsNullOrEmpty(cursor) && chatters.Count < MaxFetched);
        return chatters;
    }

    internal static (List<(string Login, string DisplayName)> Chatters, string? Cursor)? ParsePage(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root?["data"] is not JsonArray data) return null;
            var chatters = data.OfType<JsonObject>()
                .Select(u => (Login: u["user_login"]?.GetValue<string>() ?? "", Name: u["user_name"]?.GetValue<string>()))
                .Where(u => u.Login.Length > 0)
                .Select(u => (u.Login, u.Name is { Length: > 0 } n ? n : u.Login))
                .ToList();
            return (chatters, root["pagination"]?["cursor"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
