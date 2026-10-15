// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.WebSockets;
using System.Text;

namespace StreamEdge.Chat;

/// <summary>Twitch chat over IRC-on-WebSocket. Reads anonymously (the
/// <c>justinfan</c> login Twitch accepts without a token) until an OAuth token
/// is supplied; reconnects with backoff when the socket drops.</summary>
/// <remarks>Events are raised on the socket's thread; callers marshal to the
/// UI through <see cref="Ui.IUiDispatcher"/>.</remarks>
public sealed class TwitchChatClient : IAsyncDisposable
{
    public static readonly Uri Endpoint = new("wss://irc-ws.chat.twitch.tv:443");

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<string> _channels = new(StringComparer.OrdinalIgnoreCase);
    private ClientWebSocket? _socket;
    private string _nick = "";
    private Task? _loop;

    /// <summary>OAuth user token (without the "oauth:" prefix) and its login.
    /// Both null means read-only anonymous chat.</summary>
    public string? Token { get; init; }
    public string? Login { get; init; }

    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;

    /// <summary>ROOMSTATE for a joined channel: (channel login, room id).
    /// The room id is the broadcaster's user id, which the emote and badge
    /// services key on; it arrives right after each JOIN.</summary>
    public event Action<string, string>? RoomStateReceived;

    /// <summary>CLEARMSG / CLEARCHAT: lines to remove from the view.</summary>
    public event Action<ChatModeration>? ModerationReceived;

    /// <summary>USERSTATE / GLOBALUSERSTATE: how we appear (badges, colour).</summary>
    public event Action<ChatUserState>? UserStateReceived;

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_stop.Token));
    }

    public async Task JoinAsync(string channel)
    {
        channel = channel.Trim().TrimStart('#').ToLowerInvariant();
        lock (_channels)
            if (!_channels.Add(channel)) return;
        await SendAsync($"JOIN #{channel}").ConfigureAwait(false);
    }

    public async Task PartAsync(string channel)
    {
        channel = channel.Trim().TrimStart('#').ToLowerInvariant();
        lock (_channels)
            if (!_channels.Remove(channel)) return;
        await SendAsync($"PART #{channel}").ConfigureAwait(false);
    }

    public Task SendMessageAsync(string channel, string text)
    {
        if (Token is null) throw new InvalidOperationException("Sending needs a signed-in account.");
        return SendAsync($"PRIVMSG #{channel.TrimStart('#')} :{text}");
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke("Connecting…");
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(Endpoint, stop).ConfigureAwait(false);
                _socket = socket;
                await HandshakeAsync().ConfigureAwait(false);
                StatusChanged?.Invoke("Connected");
                delay = TimeSpan.FromSeconds(1);
                await ReadLoopAsync(socket, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException)
            {
                StatusChanged?.Invoke($"Disconnected: {ex.Message}");
            }
            finally
            {
                _socket = null;
            }

            try { await Task.Delay(delay, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    private async Task HandshakeAsync()
    {
        await SendAsync("CAP REQ :twitch.tv/tags twitch.tv/commands twitch.tv/membership").ConfigureAwait(false);
        if (Token is not null && Login is not null)
        {
            await SendAsync($"PASS oauth:{Token}").ConfigureAwait(false);
            _nick = Login;
            await SendAsync($"NICK {Login}").ConfigureAwait(false);
        }
        else
        {
            _nick = $"justinfan{Random.Shared.Next(10000, 99999)}";
            await SendAsync($"NICK {_nick}").ConfigureAwait(false);
        }
        string[] rejoin;
        lock (_channels) rejoin = [.. _channels];
        foreach (var channel in rejoin)
            await SendAsync($"JOIN #{channel}").ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken stop)
    {
        var buffer = new byte[16 * 1024];
        var pending = new StringBuilder();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, stop).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            // One frame may carry several lines.
            foreach (var line in pending.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                await HandleLineAsync(line).ConfigureAwait(false);
            pending.Clear();
        }
    }

    private async Task HandleLineAsync(string line)
    {
        var irc = IrcMessage.Parse(line);
        switch (irc.Command)
        {
            case "PING":
                await SendAsync($"PONG :{irc.Trailing}").ConfigureAwait(false);
                return;
            case "RECONNECT":
                // Twitch asks us to move to another server; dropping the
                // socket sends RunAsync round its reconnect loop.
                _socket?.Abort();
                return;
            case "USERSTATE" or "GLOBALUSERSTATE":
                if (ChatUserState.FromIrc(irc) is { } state) UserStateReceived?.Invoke(state);
                return;
            case "ROOMSTATE":
                // Mode changes (slow, sub-only) resend ROOMSTATE too; listeners
                // should treat repeats as idempotent.
                if (irc.Tag("room-id") is { } roomId && irc.Params.Count > 0)
                    RoomStateReceived?.Invoke(irc.Params[0].TrimStart('#'), roomId);
                return;
            case "CLEARMSG" or "CLEARCHAT":
                if (ChatModeration.FromIrc(irc) is { } moderation)
                    ModerationReceived?.Invoke(moderation);
                return;
        }
        // Our own JOIN/PART echoes are noise.
        if (irc.Command is "JOIN" or "PART" && string.Equals(irc.Nick, _nick, StringComparison.OrdinalIgnoreCase)) return;
        if (ChatMessage.FromIrc(irc, DateTimeOffset.Now) is { } message)
            MessageReceived?.Invoke(message);
    }

    private async Task SendAsync(string line)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open }) return;
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _stop.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _socket?.Abort();
        if (_loop is not null)
        {
            // Whatever the loop died of no longer matters once we're
            // shutting it down; disposal must not throw.
            try { await _loop.ConfigureAwait(false); }
            catch (Exception) { }
        }
        _stop.Dispose();
        _sendLock.Dispose();
    }
}
