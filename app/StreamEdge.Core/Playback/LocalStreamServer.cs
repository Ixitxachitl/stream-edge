// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StreamEdge.Playback;

/// <summary>A one-client HTTP server on 127.0.0.1 that hands the player a
/// continuous MPEG-TS stream produced by <see cref="LiveHlsReader"/>.
///
/// The player could open Twitch's playlist itself, but then it would follow
/// standard HLS: wait for whole segments and ignore the prefetch entries.
/// Serving the stream ourselves keeps the low-latency fetching, ad skipping
/// and resync logic in this app. A raw socket rather than HttpListener, which
/// needs a URL reservation on Windows.</summary>
/// <remarks>Each new connection replaces the previous one, so "reload the
/// URL" in the player is how a resync or quality change takes effect.</remarks>
public sealed class LocalStreamServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<Stream, CancellationToken, Task> _session;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _current;
    private Task? _accept;

    /// <param name="session">Writes the stream for one connection until the
    /// token is cancelled (a newer connection arrived) or it has nothing more.</param>
    public LocalStreamServer(Func<Stream, CancellationToken, Task> session) => _session = session;

    public Uri Url { get; private set; } = new("http://127.0.0.1/");

    public event Action<Exception>? SessionFailed;

    public void Start()
    {
        if (_accept is not null) return;
        _listener.Start();
        Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/live.ts");
        _accept = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return;
            }

            // Newest connection wins.
            var previous = Interlocked.Exchange(ref _current, CancellationTokenSource.CreateLinkedTokenSource(stop));
            previous?.Cancel();
            _ = ServeAsync(client, _current!.Token);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        try
        {
            var stream = client.GetStream();
            await SkipRequestAsync(stream, ct).ConfigureAwait(false);
            var header = "HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
            await _session(stream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The player hung up (it's reloading, or closed).
        }
        catch (Exception ex)
        {
            SessionFailed?.Invoke(ex);
        }
    }

    /// <summary>Reads past the request headers. The request itself doesn't
    /// matter; there is only one thing to serve.</summary>
    private static async Task SkipRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var tail = 0;
        // Look for \r\n\r\n; give up after 8 KiB rather than trust the client.
        for (var i = 0; i < 8192 && tail < 4; i++)
        {
            if (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) == 0) return;
            tail = (buffer[0], tail) switch
            {
                ((byte)'\r', 0 or 2) => tail + 1,
                ((byte)'\n', 1 or 3) => tail + 1,
                ((byte)'\r', _) => 1,
                _ => 0,
            };
        }
    }

    /// <summary>Ends the current connection, if any.</summary>
    public void Disconnect() => Interlocked.Exchange(ref _current, null)?.Cancel();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        Disconnect();
        _listener.Stop();
        if (_accept is not null)
        {
            try { await _accept.ConfigureAwait(false); }
            catch (Exception) { }
        }
        _stop.Dispose();
    }
}
