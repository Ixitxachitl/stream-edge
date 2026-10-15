// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace StreamEdge.Chat.Emotes;

/// <summary>Every third-party emote (and FFZ badge) the chat can show: the
/// global sets plus one entry per joined channel, keyed by Twitch room id.</summary>
/// <remarks>
/// <para>Typical use: call <see cref="LoadGlobalsAsync"/> once at start-up,
/// <see cref="LoadChannelAsync"/> when ROOMSTATE gives a joined channel's room
/// id, and pass <c>code =&gt; registry.Lookup(roomId, code)</c> to
/// <see cref="ChatTokenizer.Tokenize"/>. <see cref="Changed"/> fires after
/// each load so the view can re-tokenize lines already on screen.</para>
/// <para>Thread-safe: loads run on pool threads while the UI looks up.</para>
/// <para>A provider whose Use* flag is off is never fetched and never
/// matched; turning it on later needs another load call to fetch it.</para>
/// </remarks>
public sealed class EmoteRegistry(HttpClient http)
{
    private readonly object _gate = new();
    private readonly Dictionary<EmoteProvider, IReadOnlyDictionary<string, Emote>> _globals = [];
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);
    private FfzBadgeTable _ffzBadges = FfzBadgeTable.Empty;

    private sealed class Channel
    {
        public readonly Dictionary<EmoteProvider, IReadOnlyDictionary<string, Emote>> Sets = [];
        public Uri? FfzModeratorBadge;
        public Uri? FfzVipBadge;
    }

    private static readonly EmoteProvider[] Precedence = [EmoteProvider.SevenTv, EmoteProvider.Bttv, EmoteProvider.Ffz];

    public bool UseBttv { get; set; } = true;
    /// <summary>FFZ emotes and FFZ badges (including custom room mod/VIP badges).</summary>
    public bool UseFfz { get; set; } = true;
    public bool UseSevenTv { get; set; } = true;

    /// <summary>Raised (on a pool thread) after a load stores new data;
    /// the argument is the room id, or null for globals.</summary>
    public event Action<string?>? Changed;

    /// <summary>Copies the Use* toggles from settings.</summary>
    public void Apply(AppSettings settings)
    {
        UseBttv = settings.UseBttv;
        UseFfz = settings.UseFfz;
        UseSevenTv = settings.UseSevenTv;
    }

    public bool IsEnabled(EmoteProvider provider) => provider switch
    {
        EmoteProvider.Bttv => UseBttv,
        EmoteProvider.Ffz => UseFfz,
        EmoteProvider.SevenTv => UseSevenTv,
        _ => true,
    };

    /// <summary>The emote a word stands for in <paramref name="roomId"/>, or
    /// null. Channel sets beat global ones (a channel can shadow a global
    /// code on purpose); within each, 7TV &gt; BTTV &gt; FFZ, the order the
    /// popular extensions resolve clashes in.</summary>
    public Emote? Lookup(string? roomId, string code)
    {
        lock (_gate)
        {
            if (roomId is not null && _channels.TryGetValue(roomId, out var channel))
                foreach (var p in Precedence)
                    if (IsEnabled(p) && channel.Sets.TryGetValue(p, out var set) && set.TryGetValue(code, out var e))
                        return e;
            foreach (var p in Precedence)
                if (IsEnabled(p) && _globals.TryGetValue(p, out var set) && set.TryGetValue(code, out var e))
                    return e;
        }
        return null;
    }

    /// <summary>FFZ badges to draw for a sender: their FFZ-wide badges, empty when FFZ is off.</summary>
    public IReadOnlyList<BadgeImage> FfzBadgesFor(string? twitchUserId)
    {
        if (!UseFfz) return [];
        lock (_gate) return _ffzBadges.For(twitchUserId);
    }

    /// <summary>The broadcaster's custom FFZ moderator badge, which official
    /// FFZ draws instead of Twitch's green sword; null when none or FFZ is off.</summary>
    public Uri? FfzModeratorBadge(string roomId)
    {
        if (!UseFfz) return null;
        lock (_gate) return _channels.TryGetValue(roomId, out var c) ? c.FfzModeratorBadge : null;
    }

    /// <summary>As <see cref="FfzModeratorBadge"/>, for the VIP badge.</summary>
    public Uri? FfzVipBadge(string roomId)
    {
        if (!UseFfz) return null;
        lock (_gate) return _channels.TryGetValue(roomId, out var c) ? c.FfzVipBadge : null;
    }

    /// <summary>Fetches the global sets (and FFZ badges) of every enabled
    /// provider, in parallel. Never throws for network or JSON trouble: a
    /// provider that fails keeps whatever it had and the others still load.</summary>
    public async Task LoadGlobalsAsync(CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        if (UseSevenTv)
            tasks.Add(Guard("7TV global", async () => SetGlobal(EmoteProvider.SevenTv,
                await SevenTvEmotes.FetchGlobalAsync(http, ct).ConfigureAwait(false)), ct));
        if (UseBttv)
            tasks.Add(Guard("BTTV global", async () => SetGlobal(EmoteProvider.Bttv,
                await BttvEmotes.FetchGlobalAsync(http, ct).ConfigureAwait(false)), ct));
        if (UseFfz)
        {
            tasks.Add(Guard("FFZ global", async () => SetGlobal(EmoteProvider.Ffz,
                await FfzEmotes.FetchGlobalAsync(http, ct).ConfigureAwait(false)), ct));
            tasks.Add(Guard("FFZ badges", async () =>
            {
                var table = await FfzEmotes.FetchBadgesAsync(http, ct).ConfigureAwait(false);
                lock (_gate) _ffzBadges = table;
            }, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (tasks.Count > 0) Changed?.Invoke(null);
    }

    /// <summary>Fetches one channel's sets from every enabled provider.
    /// <paramref name="roomId"/> is the broadcaster's Twitch user id (the
    /// <c>room-id</c> of ROOMSTATE). Calling it again refreshes.</summary>
    public async Task LoadChannelAsync(string roomId, CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        if (UseSevenTv)
            tasks.Add(Guard($"7TV channel {roomId}", async () => SetChannel(roomId, EmoteProvider.SevenTv,
                await SevenTvEmotes.FetchChannelAsync(http, roomId, ct).ConfigureAwait(false)), ct));
        if (UseBttv)
            tasks.Add(Guard($"BTTV channel {roomId}", async () => SetChannel(roomId, EmoteProvider.Bttv,
                await BttvEmotes.FetchChannelAsync(http, roomId, ct).ConfigureAwait(false)), ct));
        if (UseFfz)
            tasks.Add(Guard($"FFZ channel {roomId}", async () =>
            {
                var room = await FfzEmotes.FetchChannelAsync(http, roomId, ct).ConfigureAwait(false);
                SetChannel(roomId, EmoteProvider.Ffz, room.Emotes);
                lock (_gate)
                {
                    var channel = ChannelFor(roomId);
                    channel.FfzModeratorBadge = room.ModeratorBadge;
                    channel.FfzVipBadge = room.VipBadge;
                }
            }, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (tasks.Count > 0) Changed?.Invoke(roomId);
    }

    /// <summary>Forgets a channel the user left.</summary>
    public void UnloadChannel(string roomId)
    {
        lock (_gate) _channels.Remove(roomId);
    }

    internal void SetGlobal(EmoteProvider provider, IEnumerable<Emote> emotes)
    {
        var map = ToMap(emotes);
        lock (_gate) _globals[provider] = map;
    }

    internal void SetChannel(string roomId, EmoteProvider provider, IEnumerable<Emote> emotes)
    {
        var map = ToMap(emotes);
        lock (_gate) ChannelFor(roomId).Sets[provider] = map;
    }

    internal void SetFfzBadges(FfzBadgeTable table)
    {
        lock (_gate) _ffzBadges = table;
    }

    private Channel ChannelFor(string roomId)
    {
        if (!_channels.TryGetValue(roomId, out var channel)) _channels[roomId] = channel = new Channel();
        return channel;
    }

    /// <summary>Codes are case-sensitive in every provider ("LUL" ≠ "lul");
    /// on a duplicate inside one provider the first wins.</summary>
    private static Dictionary<string, Emote> ToMap(IEnumerable<Emote> emotes)
    {
        var map = new Dictionary<string, Emote>(StringComparer.Ordinal);
        foreach (var e in emotes) map.TryAdd(e.Code, e);
        return map;
    }

    private static async Task Guard(string what, Func<Task> load, CancellationToken ct)
    {
        try
        {
            await load().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up (channel closed); nothing to report.
        }
        catch (Exception ex)
        {
            // HttpRequestException, JsonException, timeouts…: one provider
            // being down must not cost the user the others.
            Trace.TraceWarning($"Emotes: {what} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
