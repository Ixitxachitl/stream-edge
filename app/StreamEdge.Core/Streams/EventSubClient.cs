// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamEdge.Streams;

/// <summary>A channel raided another: viewers on it move to the target.</summary>
public sealed record RaidEvent(string FromId, string FromLogin, string ToId, string ToLogin, string ToName, int Viewers);

/// <summary>A whisper (direct message) to the user.</summary>
public sealed record WhisperEvent(string Id, string FromId, string FromLogin, string FromName, string Text);

/// <summary>Twitch EventSub over WebSocket, for raids out of the channels the
/// user has open (<c>channel.raid</c> by <c>from_broadcaster_user_id</c>) and
/// whispers to the user (<c>user.whisper.message</c>). Needs a signed-in
/// user's token (whispers need <c>user:manage:whispers</c>); reconnects with
/// backoff and subscribes again, since Twitch drops a closed session's
/// subscriptions.</summary>
/// <remarks>Twitch charges one unit per channel watched this way and allows
/// ten per user, so channels past the tenth aren't watched until others
/// close; whispers, being the user's own, cost nothing. Events are raised on
/// the socket's thread.</remarks>
public sealed class EventSubClient(HttpClient http, string clientId, Func<string?> token) : IAsyncDisposable
{
    public static readonly Uri Endpoint = new("wss://eventsub.wss.twitch.tv/ws");
    private const string SubscriptionsUrl = "https://api.twitch.tv/helix/eventsub/subscriptions";

    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly HashSet<string> _watched = [];
    // Broadcaster id (or WhisperKey) → subscription id, for the ones Twitch accepted.
    private readonly Dictionary<string, string> _subscriptions = [];
    private readonly Queue<string> _recentIds = new();
    private string? _sessionId;
    private Task? _loop;

    public event Action<RaidEvent>? Raided;

    public event Action<WhisperEvent>? Whispered;

    private const string WhisperKey = "whispers";
    private string? _whisperUser;

    /// <summary>Receive whispers to this user (the signed-in one).</summary>
    public void WatchWhispers(string userId)
    {
        string? session;
        lock (_lock)
        {
            if (_whisperUser == userId) return;
            _whisperUser = userId;
            session = _sessionId;
        }
        if (session is not null) _ = SubscribeAsync(WhisperKey, session);
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Follow raids out of this broadcaster (a numeric user id).</summary>
    public void Watch(string broadcasterId)
    {
        string? session;
        lock (_lock)
        {
            if (!_watched.Add(broadcasterId)) return;
            session = _sessionId;
        }
        if (session is not null) _ = SubscribeAsync(broadcasterId, session);
    }

    public void Unwatch(string broadcasterId)
    {
        string? id;
        string? session;
        lock (_lock)
        {
            if (!_watched.Remove(broadcasterId)) return;
            _subscriptions.Remove(broadcasterId, out id);
            session = _sessionId;
        }
        if (id is not null) _ = UnsubscribeAsync(id, session);
    }

    // ----- Messages -----------------------------------------------------------

    /// <summary>The parts of an EventSub message this client uses.</summary>
    public sealed record Message(string Type, string? Id, string? SessionId = null, int? KeepaliveSeconds = null,
        Uri? ReconnectUrl = null, RaidEvent? Raid = null, string? RevokedSubscription = null, WhisperEvent? Whisper = null);

    /// <summary>Null for anything that isn't a well-formed EventSub message.</summary>
    public static Message? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return null;
            var meta = root["metadata"];
            var type = meta?["message_type"]?.GetValue<string>();
            if (type is null) return null;
            var id = meta?["message_id"]?.GetValue<string>();
            var payload = root["payload"];
            var session = payload?["session"];
            switch (type)
            {
                case "session_welcome":
                    return new(type, id, session?["id"]?.GetValue<string>(),
                        session?["keepalive_timeout_seconds"]?.GetValue<int>());
                case "session_reconnect":
                {
                    var ok = Uri.TryCreate(session?["reconnect_url"]?.GetValue<string>(), UriKind.Absolute, out var url);
                    return new(type, id, session?["id"]?.GetValue<string>(),
                        ReconnectUrl: ok && url!.Scheme == "wss" ? url : null);
                }
                case "notification":
                {
                    var subscription = meta?["subscription_type"]?.GetValue<string>();
                    if (payload?["event"] is not JsonObject e) return new(type, id);
                    if (subscription == "user.whisper.message")
                    {
                        var whisper = new WhisperEvent(Text(e, "whisper_id"), Text(e, "from_user_id"), Text(e, "from_user_login"),
                            Text(e, "from_user_name"), e["whisper"]?["text"]?.GetValue<string>() ?? "");
                        return whisper.FromLogin.Length == 0 ? new(type, id) : new(type, id, Whisper: whisper);
                    }
                    if (subscription != "channel.raid") return new(type, id);
                    var raid = new RaidEvent(Text(e, "from_broadcaster_user_id"), Text(e, "from_broadcaster_user_login"),
                        Text(e, "to_broadcaster_user_id"), Text(e, "to_broadcaster_user_login"), Text(e, "to_broadcaster_user_name"),
                        e["viewers"]?.GetValue<int>() ?? 0);
                    return raid.ToLogin.Length == 0 ? new(type, id) : new(type, id, Raid: raid);
                }
                case "revocation":
                    return new(type, id, RevokedSubscription: payload?["subscription"]?["id"]?.GetValue<string>());
                default:
                    return new(type, id);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string Text(JsonObject o, string key) => o[key]?.GetValue<string>() ?? "";

    private async Task RunAsync(CancellationToken stop)
    {
        var url = Endpoint;
        var delay = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested)
        {
            var moved = false;
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(url, stop).ConfigureAwait(false);
                // Until the welcome says otherwise.
                var keepalive = TimeSpan.FromSeconds(30);
                while (socket.State == WebSocketState.Open)
                {
                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    // Twitch sends something at least this often; silence
                    // past it means the connection is dead.
                    quiet.CancelAfter(keepalive + TimeSpan.FromSeconds(10));
                    var text = await ReceiveAsync(socket, quiet.Token).ConfigureAwait(false);
                    if (text is null || Parse(text) is not { } m) break;
                    if (!FirstSighting(m.Id)) continue;
                    switch (m.Type)
                    {
                        case "session_welcome":
                            if (m.KeepaliveSeconds is > 0 and var k) keepalive = TimeSpan.FromSeconds(k);
                            delay = TimeSpan.FromSeconds(1);
                            lock (_lock) _sessionId = m.SessionId;
                            // After a session_reconnect the subscriptions came
                            // along, so this only adds ones still missing.
                            if (m.SessionId is { } session) _ = SubscribeAllAsync(session);
                            break;
                        case "session_reconnect" when m.ReconnectUrl is { } next:
                            url = next;
                            moved = true;
                            break;
                        case "notification" when m.Raid is { } raid:
                            Raided?.Invoke(raid);
                            break;
                        case "notification" when m.Whisper is { } whisper:
                            Whispered?.Invoke(whisper);
                            break;
                        case "revocation" when m.RevokedSubscription is { } revoked:
                            lock (_lock)
                                foreach (var (broadcaster, sub) in _subscriptions.Where(p => p.Value == revoked).ToList())
                                    _subscriptions.Remove(broadcaster);
                            break;
                    }
                    if (moved) break;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
            {
                System.Diagnostics.Trace.WriteLine("eventsub: " + ex.Message);
            }
            if (moved) continue;

            // A new session starts with no subscriptions.
            url = Endpoint;
            lock (_lock)
            {
                _sessionId = null;
                _subscriptions.Clear();
            }
            try { await Task.Delay(delay, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        using var text = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            text.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(text.ToArray());
        }
    }

    /// <summary>Twitch may deliver a message twice; ids seen lately are skipped.</summary>
    private bool FirstSighting(string? id)
    {
        if (id is null) return true;
        if (_recentIds.Contains(id)) return false;
        _recentIds.Enqueue(id);
        if (_recentIds.Count > 100) _recentIds.Dequeue();
        return true;
    }

    private async Task SubscribeAllAsync(string session)
    {
        List<string> pending;
        lock (_lock)
        {
            pending = [.. _watched.Where(b => !_subscriptions.ContainsKey(b))];
            if (_whisperUser is not null && !_subscriptions.ContainsKey(WhisperKey)) pending.Insert(0, WhisperKey);
        }
        foreach (var broadcaster in pending) await SubscribeAsync(broadcaster, session).ConfigureAwait(false);
    }

    /// <param name="broadcasterId">A channel to follow raids out of, or
    /// <see cref="WhisperKey"/> for the user's whispers.</param>
    private async Task SubscribeAsync(string broadcasterId, string session)
    {
        if (token() is not { } bearer) return;
        string? whisperUser;
        lock (_lock) whisperUser = _whisperUser;
        var whispers = broadcasterId == WhisperKey;
        if (whispers && whisperUser is null) return;
        var body = new JsonObject
        {
            ["type"] = whispers ? "user.whisper.message" : "channel.raid",
            ["version"] = "1",
            ["condition"] = whispers
                ? new JsonObject { ["user_id"] = whisperUser }
                : new JsonObject { ["from_broadcaster_user_id"] = broadcasterId },
            ["transport"] = new JsonObject { ["method"] = "websocket", ["session_id"] = session },
        };
        try
        {
            using var request = Request(HttpMethod.Post, SubscriptionsUrl, bearer);
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, _stop.Token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(_stop.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 429: over the ten-channel allowance. Tried again when a
                // watched channel closes.
                System.Diagnostics.Trace.WriteLine($"eventsub subscribe {broadcasterId}: {(int)response.StatusCode} {json}");
                return;
            }
            var id = JsonNode.Parse(json)?["data"]?[0]?["id"]?.GetValue<string>();
            if (id is null) return;
            bool stale;
            lock (_lock)
            {
                stale = (whispers ? _whisperUser is null : !_watched.Contains(broadcasterId)) || _sessionId != session;
                if (!stale) _subscriptions[broadcasterId] = id;
            }
            // Closed while the request was out: take it back.
            if (stale) await UnsubscribeAsync(id, null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            System.Diagnostics.Trace.WriteLine("eventsub subscribe: " + ex.Message);
        }
    }

    /// <summary>Frees the slot; with a session, the next waiting channel takes it.</summary>
    private async Task UnsubscribeAsync(string subscriptionId, string? session)
    {
        if (token() is not { } bearer) return;
        try
        {
            using var request = Request(HttpMethod.Delete, SubscriptionsUrl + "?id=" + Uri.EscapeDataString(subscriptionId), bearer);
            using var response = await http.SendAsync(request, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return;
        }
        if (session is not null) await SubscribeAllAsync(session).ConfigureAwait(false);
    }

    private HttpRequestMessage Request(HttpMethod method, string url, string bearer)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Client-Id", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (Exception) { }
        }
        _stop.Dispose();
    }
}
