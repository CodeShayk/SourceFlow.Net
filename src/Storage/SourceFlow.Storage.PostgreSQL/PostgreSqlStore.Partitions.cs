using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace SourceFlow.Storage.PostgreSQL;

/// <content>Partitioned events (<see cref="PostgreSqlStorageOptions.PartitionEvents"/>, doc 05 §9, WP-4.5).</content>
public sealed partial class PostgreSqlStore
{
    /// <summary>
    /// Creates the monthly partitions from the newest one up to <see cref="PostgreSqlStorageOptions.PartitionMonthsAhead"/>
    /// months after the current month (by this store's clock); returns how many were created. Safe to run concurrently.
    /// </summary>
    /// <exception cref="InvalidOperationException">The store is not configured with <see cref="PostgreSqlStorageOptions.PartitionEvents"/>.</exception>
    public async Task<int> EnsureEventPartitionsAsync(CancellationToken cancellationToken = default)
    {
        RequirePartitioning();
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT {Quoted}.sf_ensure_event_partitions(@at, @ahead)", connection);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("at", Time.GetUtcNow()));
        command.Parameters.Add(new NpgsqlParameter<int>("ahead", Options.PartitionMonthsAhead));
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>The events partitions, oldest first, including detached ones.</summary>
    public async Task<IReadOnlyList<EventPartition>> ListEventPartitionsAsync(CancellationToken cancellationToken = default)
    {
        RequirePartitioning();
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT name, from_at, to_at, detached_at FROM {Quoted}.event_partitions ORDER BY to_at", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var partitions = new List<EventPartition>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            partitions.Add(new EventPartition(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return partitions;
    }

    /// <summary>
    /// Detaches an old partition from the events table (doc 05 §9), so it can be archived and dropped. Every stream with
    /// events in it must be archived or deleted, and its whole range must be in the past; the detached table keeps its
    /// rows. Detached events no longer appear in stream reads, the global log or queries. It needs the table owner's rights.
    /// </summary>
    /// <exception cref="EventPartitionException">The partition is unknown, current, already detached, or holds events of open streams.</exception>
    public async Task DetachEventPartitionAsync(string name, CancellationToken cancellationToken = default)
    {
        RequirePartitioning();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!PartitionName().IsMatch(name))
        {
            throw new EventPartitionException($"'{name}' is not an events partition name.");
        }

        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var @lock = new NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('sourceflow.partitions:' || '{Quoted}', 0))", connection, transaction))
        {
            await @lock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset to;
        await using (var find = new NpgsqlCommand($"SELECT to_at, detached_at FROM {Quoted}.event_partitions WHERE name = @name", connection, transaction))
        {
            find.Parameters.Add(new NpgsqlParameter<string>("name", name));
            await using NpgsqlDataReader reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new EventPartitionException($"There is no events partition '{name}'.");
            }

            to = reader.GetFieldValue<DateTimeOffset>(0);
            if (!reader.IsDBNull(1))
            {
                throw new EventPartitionException($"The events partition '{name}' is already detached.");
            }
        }

        if (to > Time.GetUtcNow())
        {
            throw new EventPartitionException($"The events partition '{name}' ends at {to.ToString("O", CultureInfo.InvariantCulture)}, which is not in the past: it can still receive events.");
        }

        // The partition name was checked against event_partitions and the pattern above: safe as a quoted identifier.
        string table = $"{Quoted}.\"{name}\"";
        await using (var open = new NpgsqlCommand(
            $"""
            SELECT count(*) FROM (SELECT DISTINCT e.tenant_id, e.stream_id FROM {table} e) e
              LEFT JOIN {Quoted}.streams s ON s.tenant_id = e.tenant_id AND s.stream_id = e.stream_id
             WHERE s.stream_id IS NULL OR NOT (s.is_archived OR s.is_deleted)
            """, connection, transaction))
        {
            long streams = (long)(await open.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            if (streams > 0)
            {
                throw new EventPartitionException($"The events partition '{name}' holds events of {streams} stream(s) that are neither archived nor deleted. Archive them first (IEventStore.ArchiveStreamAsync).");
            }
        }

        await using (var detach = new NpgsqlCommand(
            $"ALTER TABLE {Quoted}.events DETACH PARTITION {table}; UPDATE {Quoted}.event_partitions SET detached_at = now() WHERE name = @name", connection, transaction))
        {
            detach.Parameters.Add(new NpgsqlParameter<string>("name", name));
            await detach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private string Quoted => "\"" + Options.Schema + "\"";

    private void RequirePartitioning()
    {
        if (!Options.PartitionEvents)
        {
            throw new InvalidOperationException("The events table is partitioned only with PostgreSqlStorageOptions.PartitionEvents.");
        }
    }

    [GeneratedRegex("^events_(initial|[0-9]{4}_[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex PartitionName();
}

/// <summary>Keeps monthly events partitions ahead of time (doc 05 §9): at startup, then every maintenance interval.</summary>
internal sealed partial class PostgreSqlPartitionMaintenance(PostgreSqlStore store, ILoggerFactory? loggers) : BackgroundService
{
    private readonly ILogger _logger = (ILogger?)loggers?.CreateLogger<PostgreSqlPartitionMaintenance>() ?? NullLogger.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int created = await store.EnsureEventPartitionsAsync(stoppingToken).ConfigureAwait(false);
                if (created > 0)
                {
                    PartitionsCreated(_logger, created, store.Options.Schema);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                PartitionsFailed(_logger, exception, store.Options.Schema);
            }

            try
            {
                await Task.Delay(store.Options.PartitionMaintenanceInterval, store.Time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "Created {Count} events partition(s) ahead in schema {Schema}.")]
    private static partial void PartitionsCreated(ILogger logger, int count, string schema);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Error, Message = "Creating events partitions ahead in schema {Schema} failed; inserts fail once the last partition ends.")]
    private static partial void PartitionsFailed(ILogger logger, Exception exception, string schema);
}
