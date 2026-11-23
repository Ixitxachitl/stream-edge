// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamEdge.Playback;

namespace StreamEdge.Streams;

/// <summary>Channels going live and offline, as they do, over the event
/// socket twitch.tv's own pages use (its <c>video-playback-by-id</c> topic),
/// with the web client ID like <see cref="ChannelLookup"/>: public, so it
/// works signed out. EventSub has the same events but allows a user ten
/// channels; this takes <see cref="MaxChannels"/> on a connection.</summary>
/// <remarks>Undocumented, so it's an addition to polling the lists, never
/// instead of it: if Twitch changes it, channels are noticed at the next
/// poll as before. Reconnects with backoff and subscribes again. Events are
/// raised on the socket's thread.</remarks>
public sealed class StreamEventsClient : IAsyncDisposable
{
    /// <summary>Twitch refuses subscriptions past this many on a connection.</summary>
    public const int MaxChannels = 150;

    public static readonly Uri Endpoint = new("wss://hermes.twitch.tv/v1?clientId=" + TwitchPlayback.WebClientId);

    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _sending = new(1);
    private IReadOnlyList<string> _wanted = [];
    // For the connection in use: channel id → subscription id, and back.
    private readonly Dictionary<string, string> _subscriptions = [];
    private readonly Dictionary<string, string> _channels = [];
    private ClientWebSocket? _socket;
    private Task? _loop;

    /// <summary>A channel (its numeric id) started streaming.</summary>
    public event Action<string>? WentLive;

    /// <summary>A channel (its numeric id) stopped streaming.</summary>
    public event Action<string>? WentOffline;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>The channels to hear about, most wanted first: the first
    /// <see cref="MaxChannels"/> are watched, replacing whatever was.</summary>
    public void Watch(IEnumerable<string> channelIds)
    {
        var wanted = channelIds.Where(id => id.Length > 0).Distinct().Take(MaxChannels).ToList();
        lock (_lock) _wanted = wanted;
        _ = SyncAsync();
    }

    // ----- Messages -----------------------------------------------------------

    /// <summary>The parts of a message this client uses.</summary>
    /// <param name="SubscriptionId">Which subscription a response or an event is about.</param>
    /// <param name="Ok">A response: whether the request was accepted.</param>
    /// <param name="Event">An event's kind: "stream-up", "stream-down", "viewcount"…</param>
    public sealed record Message(string Type, int? KeepaliveSeconds = null, string? SubscriptionId = null,
        bool Ok = true, string? Event = null);

    /// <summary>Null for anything that isn't a well-formed message.</summary>
    public static Message? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["type"]?.GetValue<string>() is not { } type) return null;
            switch (type)
            {
                case "welcome":
                    return new(type, root["welcome"]?["keepaliveSec"]?.GetValue<int>());
                case "subscribeResponse" or "unsubscribeResponse":
                {
                    var response = root[type];
                    return new(type, SubscriptionId: response?["subscription"]?["id"]?.GetValue<string>(),
                        Ok: response?["result"]?.GetValue<string>() == "ok");
                }
                case "notification":
                {
                    var notification = root["notification"];
                    // The event itself is JSON in a string, as PubSub had it.
                    var inner = notification?["pubsub"]?.GetValue<string>();
                    return new(type, SubscriptionId: notification?["subscription"]?["id"]?.GetValue<string>(),
                        Event: inner is null ? null : JsonNode.Parse(inner)?["type"]?.GetValue<string>());
                }
                default:
                    return new(type);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A request as the socket takes it: every message carries its
    /// own id and the time. Public for tests.</summary>
    public static string Subscribe(string subscriptionId, string channelId) => Request("subscribe", new JsonObject
    {
        ["id"] = subscriptionId,
        ["type"] = "pubsub",
        ["pubsub"] = new JsonObject { ["topic"] = "video-playback-by-id." + channelId },
    });

    public static string Unsubscribe(string subscriptionId) =>
        Request("unsubscribe", new JsonObject { ["id"] = subscriptionId });

    private static string Request(string type, JsonObject body) => new JsonObject
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["type"] = type,
        ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
        [type] = body,
    }.ToJsonString();

    // ----- Connection -----------------------------------------------------------

    private async Task RunAsync(CancellationToken stop)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(Endpoint, stop).ConfigureAwait(false);
                // Until the welcome says otherwise.
                var keepalive = TimeSpan.FromSeconds(30);
                while (socket.State == WebSocketState.Open)
                {
                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    // Twitch sends something at least this often; silence
                    // past it means the connection is dead.
                    quiet.CancelAfter(keepalive + TimeSpan.FromSeconds(10));
                    var text = await ReceiveAsync(socket, quiet.Token).ConfigureAwait(false);
                    if (text is null) break;
                    if (Parse(text) is not { } m) continue;
                    // Told to move to another server: a new connection that
                    // subscribes afresh does as well, and the poll covers
                    // the moment between.
                    if (m.Type == "reconnect") break;
                    Handle(m, socket, ref keepalive, ref delay);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
            {
                System.Diagnostics.Trace.WriteLine("stream events: " + ex.Message);
            }

            // A new connection starts with no subscriptions.
            lock (_lock)
            {
                _socket = null;
                _subscriptions.Clear();
                _channels.Clear();
            }
            try { await Task.Delay(delay, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    private void Handle(Message m, ClientWebSocket socket, ref TimeSpan keepalive, ref TimeSpan delay)
    {
        switch (m.Type)
        {
            case "welcome":
                if (m.KeepaliveSeconds is > 0 and var k) keepalive = TimeSpan.FromSeconds(k);
                delay = TimeSpan.FromSeconds(1);
                lock (_lock) _socket = socket;
                _ = SyncAsync();
                break;
            case "subscribeResponse" when !m.Ok && m.SubscriptionId is { } refused:
                // Not watched after all; asked for again at the next Watch.
                lock (_lock)
                    if (_channels.Remove(refused, out var channel)) _subscriptions.Remove(channel);
                break;
            case "notification" when m.SubscriptionId is { } subscription && m.Event is "stream-up" or "stream-down":
            {
                string? channel;
                lock (_lock) _channels.TryGetValue(subscription, out channel);
                if (channel is null) break;
                if (m.Event == "stream-up") WentLive?.Invoke(channel);
                else WentOffline?.Invoke(channel);
                break;
            }
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

    /// <summary>Brings what's subscribed on the connection in line with what's
    /// wanted: drops the ones no longer wanted first, to make room.</summary>
    private async Task SyncAsync()
    {
        try
        {
            await _sending.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        try
        {
            ClientWebSocket? socket;
            var requests = new List<string>();
            lock (_lock)
            {
                socket = _socket;
                if (socket is null) return;
                var wanted = _wanted.ToHashSet();
                foreach (var (channel, subscription) in _subscriptions.Where(s => !wanted.Contains(s.Key)).ToList())
                {
                    _subscriptions.Remove(channel);
                    _channels.Remove(subscription);
                    requests.Add(Unsubscribe(subscription));
                }
                foreach (var channel in _wanted.Where(c => !_subscriptions.ContainsKey(c)))
                {
                    var subscription = Guid.NewGuid().ToString();
                    _subscriptions[channel] = subscription;
                    _channels[subscription] = channel;
                    requests.Add(Subscribe(subscription, channel));
                }
            }
            foreach (var request in requests)
                await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, _stop.Token)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException
                                       or ObjectDisposedException or InvalidOperationException)
        {
            // The connection went: the next one subscribes to everything.
            System.Diagnostics.Trace.WriteLine("stream events: " + ex.Message);
        }
        finally
        {
            _sending.Release();
        }
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
