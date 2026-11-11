using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SourceFlow.Cloud.Tests.Azure;

/// <summary>
/// A TCP forwarder in front of the broker, for real outages: while <see cref="Unavailable"/> it drops every open
/// connection and refuses new ones, so the client must reconnect afterwards (TCK-32).
/// </summary>
internal sealed class TcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _open = new();
    private readonly Task _accepting;
    private volatile bool _unavailable;

    public TcpProxy(string host, int port)
    {
        _host = host;
        _port = port;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public bool Unavailable
    {
        get => _unavailable;
        set
        {
            _unavailable = value;
            if (value)
            {
                foreach (TcpClient client in _open.Keys)
                {
                    Close(client);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        foreach (TcpClient client in _open.Keys)
        {
            Close(client);
        }

        try
        {
            await _accepting;
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // stopped
        }

        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient inbound = await _listener.AcceptTcpClientAsync(_stopping.Token);
            if (_unavailable)
            {
                inbound.Client.LingerState = new LingerOption(true, 0);   // reset: connection refused
                inbound.Dispose();
                continue;
            }

            _ = PipeAsync(inbound);
        }
    }

    private async Task PipeAsync(TcpClient inbound)
    {
        var outbound = new TcpClient();
        _open[inbound] = 0;
        _open[outbound] = 0;
        try
        {
            await outbound.ConnectAsync(_host, _port, _stopping.Token);
            NetworkStream a = inbound.GetStream();
            NetworkStream b = outbound.GetStream();
            await Task.WhenAny(a.CopyToAsync(b, _stopping.Token), b.CopyToAsync(a, _stopping.Token));
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // a dropped connection
        }
        finally
        {
            Close(inbound);
            Close(outbound);
        }
    }

    private void Close(TcpClient client)
    {
        _open.TryRemove(client, out _);
        try
        {
            if (client.Client is { } socket)
            {
                socket.LingerState = new LingerOption(true, 0);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // already closed
        }

        client.Dispose();
    }
}
