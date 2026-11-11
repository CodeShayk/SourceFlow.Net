using Npgsql;

namespace SourceFlow.Storage.PostgreSQL;

/// <content>Commit notifications (doc 06 §2): in-process callbacks after each local commit, plus a dedicated LISTEN
/// connection that relays commits made by other processes. The LISTEN connection reconnects after failures; meanwhile
/// subscribers fall back to their polling interval, so a lost notification only delays work.</content>
public sealed partial class PostgreSqlStore
{
    private readonly Notifier _notifications;

    /// <summary>Identifies this store instance in NOTIFY payloads, so it ignores its own commits on the LISTEN connection.</summary>
    internal string Origin { get; } = Guid.CreateVersion7().ToString("N");

    /// <inheritdoc />
    public IDisposable Subscribe(Action onCommit)
    {
        ArgumentNullException.ThrowIfNull(onCommit);
        return _notifications.Subscribe(onCommit);
    }

    internal void RaiseCommitted()
    {
        _notifications.Raise();
    }

    private sealed class Notifier : IAsyncDisposable
    {
        private readonly PostgreSqlStore _store;
        private readonly Lock _gate = new();
        private readonly List<Action> _subscribers = [];
        private readonly CancellationTokenSource _stop = new();
        private Task? _listener;

        public Notifier(PostgreSqlStore store)
        {
            _store = store;
        }

        public IDisposable Subscribe(Action onCommit)
        {
            lock (_gate)
            {
                _subscribers.Add(onCommit);
                _listener ??= Task.Run(() => ListenAsync(_stop.Token));
            }

            return new Unsubscriber(this, onCommit);
        }

        public void Raise()
        {
            Action[] subscribers;
            lock (_gate)
            {
                subscribers = [.. _subscribers];
            }

            foreach (Action subscriber in subscribers)
            {
                subscriber();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            if (_listener is not null)
            {
                try
                {
                    await _listener.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _stop.Dispose();
        }

        private async Task ListenAsync(CancellationToken cancellationToken)
        {
            PostgreSqlStorageOptions options = _store.Options;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using NpgsqlConnection connection = options.ListenConnectionString is { } listen
                        ? new NpgsqlConnection(listen)
                        : _store.DataSource.CreateConnection();
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                    connection.Notification += (_, e) =>
                    {
                        if (e.Payload != _store.Origin)
                        {
                            Raise();
                        }
                    };

                    await using (var command = new NpgsqlCommand("LISTEN \"" + options.Channel + "\"", connection))
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    // Commits may have happened while the connection was down.
                    Raise();
                    while (true)
                    {
                        await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is NpgsqlException or System.Net.Sockets.SocketException or IOException or InvalidOperationException or TimeoutException)
                {
                    // Infrastructure wait: the system clock, never the (possibly fake) business clock.
                    try
                    {
                        await Task.Delay(options.ListenReconnectDelay, TimeProvider.System, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private sealed class Unsubscriber(Notifier notifier, Action callback) : IDisposable
        {
            public void Dispose()
            {
                lock (notifier._gate)
                {
                    notifier._subscribers.Remove(callback);
                }
            }
        }
    }
}
