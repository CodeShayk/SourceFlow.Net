using Npgsql;

namespace SourceFlow.Storage.PostgreSQL;

/// <content>
/// Operational readings for the health checks and gauges (doc 08 §3.2, §5). Each is one aggregate statement on the
/// system connections (cross-tenant under row-level security); the core samples them on an interval.
/// </content>
public sealed partial class PostgreSqlStore : IStoreMonitor
{
    /// <inheritdoc />
    public async ValueTask<StoreProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        await using (NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var ping = new NpgsqlCommand("SELECT 1", connection))
        {
            await ping.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        PostgreSqlSchemaMigrator migrator = CreateMigrator();
        try
        {
            await migrator.VerifyAsync(cancellationToken).ConfigureAwait(false);
            return new StoreProbe(SchemaCurrent: true, $"Schema '{Options.Schema}' is at V{migrator.LatestVersion:D3} or later.");
        }
        catch (SchemaMigrationException e)
        {
            return new StoreProbe(SchemaCurrent: false, e.Message);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxBacklog>> GetOutboxBacklogAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await SystemDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.OutboxBacklog, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var backlog = new List<OutboxBacklog>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            backlog.Add(new OutboxBacklog(reader.GetInt16(0), checked((int)reader.GetInt64(1)), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return backlog;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, int>> CountParkedDeadLettersAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await SystemDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.ParkedDeadLettersByConsumer, connection);
        command.Parameters.Add(new NpgsqlParameter<short>("status", (short)DeadLetterStatus.Parked));
        return await ReadCountsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<long> CountEventsAfterAsync(GlobalPosition after, BucketRange buckets, long cap, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await SystemDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.CountEventsAfter, connection);
        command.Parameters.Add(Xid("tx", after.Commit));
        command.Parameters.Add(new NpgsqlParameter<long>("seq", checked((long)Math.Min(after.Sequence, long.MaxValue))));
        command.Parameters.Add(new NpgsqlParameter<short>("lo", checked((short)buckets.Low)));
        command.Parameters.Add(new NpgsqlParameter<short>("hi", checked((short)buckets.High)));
        command.Parameters.Add(new NpgsqlParameter<long>("cap", cap));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, int>> CountActiveProcessesAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await SystemDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.ActiveProcessesByType, connection);
        return await ReadCountsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReadCountsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[reader.GetString(0)] = checked((int)reader.GetInt64(1));
        }

        return counts;
    }
}
