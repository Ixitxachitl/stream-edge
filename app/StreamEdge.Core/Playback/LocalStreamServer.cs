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
    private readonly Func<string, bool>? _isCurrent;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _current;
    private Task? _accept;

    /// <param name="session">Writes the stream for one connection until the
    /// token is cancelled (a newer connection arrived) or it has nothing more.</param>
    /// <param name="isCurrent">Whether a request target ("/live.ts?n=3") is
    /// for the current load; others are turned away. Null serves everything.</param>
    public LocalStreamServer(Func<Stream, CancellationToken, Task> session, Func<string, bool>? isCurrent = null)
    {
        _session = session;
        _isCurrent = isCurrent;
    }

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

            _ = ServeAsync(client, stop);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        using var _ = client;
        client.NoDelay = true;
        CancellationTokenSource? mine = null;
        try
        {
            var stream = client.GetStream();
            using (var reading = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                reading.CancelAfter(TimeSpan.FromSeconds(5));
                var target = await ReadRequestTargetAsync(stream, reading.Token).ConfigureAwait(false);
                // Two reloads close together race to connect, and the
                // abandoned one can arrive last. Serve only the request for
                // the current load, so a stale one can't cut off the real one.
                if (_isCurrent is not null && !_isCurrent(target))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 410 Gone\r\nConnection: close\r\n\r\n"), stop)
                        .ConfigureAwait(false);
                    return;
                }
            }

            // The current request replaces whatever was being served.
            mine = CancellationTokenSource.CreateLinkedTokenSource(stop);
            Interlocked.Exchange(ref _current, mine)?.Cancel();
            var ct = mine.Token;
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

    /// <summary>Reads the request headers and returns the request target
    /// ("/live.ts?n=3"), or "" if the request was malformed.</summary>
    private static async Task<string> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var line = new StringBuilder();
        var firstLineDone = false;
        var tail = 0;
        // Look for \r\n\r\n; give up after 8 KiB rather than trust the client.
        for (var i = 0; i < 8192 && tail < 4; i++)
        {
            if (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) == 0) break;
            if (!firstLineDone)
            {
                if (buffer[0] is (byte)'\r' or (byte)'\n') firstLineDone = true;
                else line.Append((char)buffer[0]);
            }
            tail = (buffer[0], tail) switch
            {
                ((byte)'\r', 0 or 2) => tail + 1,
                ((byte)'\n', 1 or 3) => tail + 1,
                ((byte)'\r', _) => 1,
                _ => 0,
            };
        }
        // "GET /live.ts?n=3 HTTP/1.1"
        return line.ToString().Split(' ') is [_, var target, ..] ? target : "";
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
