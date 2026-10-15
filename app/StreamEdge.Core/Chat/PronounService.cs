// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using StreamEdge.Chat.Emotes;

namespace StreamEdge.Chat;

/// <summary>One entry of the alejo.io pronoun list.</summary>
public sealed record Pronoun(string Id, string Subject, string Object, bool Singular);

/// <summary>User pronouns from pronouns.alejo.io (v1), the service the
/// popular "Chat Pronouns" extension reads, so chatters set them once.</summary>
/// <remarks>
/// Results are cached per login for the service's lifetime, including "has
/// none", since every chat line would otherwise be a request. Concurrent
/// asks for one login share a single request. Network failures are not
/// cached: the next ask retries. Thread-safe.
/// </remarks>
public sealed class PronounService(HttpClient http)
{
    public const string PronounsUrl = "https://api.pronouns.alejo.io/v1/pronouns";

    public static string UserUrl(string login) =>
        $"https://api.pronouns.alejo.io/v1/users/{Uri.EscapeDataString(login)}";

    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _listGate = new();
    private Task<IReadOnlyDictionary<string, Pronoun>>? _list;

    /// <summary>The display text ("He/Him", "She/They", "Any") for
    /// <paramref name="login"/>, or null when they have none set (or the
    /// service is unreachable). Cancelling <paramref name="ct"/> abandons the
    /// wait, not the shared request.</summary>
    public Task<string?> GetAsync(string login, CancellationToken ct = default)
    {
        login = login.Trim().ToLowerInvariant();
        if (login.Length == 0) return Task.FromResult<string?>(null);
        // Lazy: GetOrAdd may run its factory twice under a race; only the
        // stored Lazy's request ever starts.
        var entry = _users.GetOrAdd(login, l => new Lazy<Task<string?>>(() => FetchUserAsync(l)));
        return AwaitAsync(login, entry, ct);
    }

    /// <summary>Synchronous peek for rendering: true when the answer is
    /// already known (<paramref name="pronoun"/> may still be null = none).</summary>
    public bool TryGetCached(string login, out string? pronoun)
    {
        if (_users.TryGetValue(login.Trim(), out var entry) && entry.IsValueCreated
            && entry.Value.IsCompletedSuccessfully)
        {
            pronoun = entry.Value.Result;
            return true;
        }
        pronoun = null;
        return false;
    }

    /// <summary>"Subject/Object", "Subject/AltSubject" when there's an alt,
    /// or just the subject for singular ones like "Any".</summary>
    public static string Format(Pronoun primary, Pronoun? alt = null) =>
        alt is not null ? $"{primary.Subject}/{alt.Subject}"
        : primary.Singular ? primary.Subject
        : $"{primary.Subject}/{primary.Object}";

    /// <summary>Parses the pronoun list: an object keyed by id.</summary>
    public static IReadOnlyDictionary<string, Pronoun> ParseList(string json)
    {
        var map = new Dictionary<string, Pronoun>(StringComparer.Ordinal);
        if (JsonRead.Parse(json) is not JsonObject root) return map;
        foreach (var (key, value) in root)
        {
            var subject = value.Str("subject");
            if (subject is null) continue;
            var id = value.Str("name") ?? key;
            map[id] = new Pronoun(id, subject, value.Str("object") ?? subject, value.Bool("singular"));
        }
        return map;
    }

    /// <summary>Parses a user record into (pronoun_id, alt_pronoun_id).</summary>
    public static (string? PronounId, string? AltPronounId) ParseUser(string json)
    {
        var root = JsonRead.Parse(json);
        return (root.Str("pronoun_id"), root.Str("alt_pronoun_id"));
    }

    private async Task<string?> AwaitAsync(string login, Lazy<Task<string?>> entry, CancellationToken ct)
    {
        try
        {
            return await entry.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trace.TraceWarning($"Pronouns: {login} failed: {ex.GetType().Name}: {ex.Message}");
            // Not cached: forget this attempt (only if it is still the stored
            // one) so the next line from this user retries.
            _users.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(login, entry));
            return null;
        }
    }

    /// <summary>Throws on network/JSON failure (so it isn't cached); null
    /// only for a definite "no pronouns" (404 or an id we don't know).</summary>
    private async Task<string?> FetchUserAsync(string login)
    {
        var list = await ListAsync().ConfigureAwait(false);
        var json = await Http.GetOrNullAsync(http, UserUrl(login), CancellationToken.None).ConfigureAwait(false);
        if (json is null) return null;
        var (id, altId) = ParseUser(json);
        if (id is null || !list.TryGetValue(id, out var primary)) return null;
        var alt = altId is not null && list.TryGetValue(altId, out var a) ? a : null;
        return Format(primary, alt);
    }

    private Task<IReadOnlyDictionary<string, Pronoun>> ListAsync()
    {
        lock (_listGate)
        {
            // A failed list fetch is retried by the next caller.
            if (_list is null || _list.IsFaulted || _list.IsCanceled) _list = FetchListAsync();
            return _list;
        }
    }

    private async Task<IReadOnlyDictionary<string, Pronoun>> FetchListAsync()
    {
        var json = await Http.GetOrNullAsync(http, PronounsUrl, CancellationToken.None).ConfigureAwait(false);
        return json is null ? new Dictionary<string, Pronoun>() : ParseList(json);
    }
}
