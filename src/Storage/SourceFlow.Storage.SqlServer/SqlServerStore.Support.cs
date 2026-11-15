using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SourceFlow.Storage.SqlServer;

/// <content>
/// Commit notifications (doc 06 §2) and operational readings (doc 08 §3.2, §5). Notifications are in-process: other
/// processes find new events on their polling interval. Readings are aggregate statements sampled by the core monitor.
/// </content>
public sealed partial class SqlServerStore : IStoreMonitor
{
    private readonly Lock _gate = new();
    private readonly List<Action> _subscribers = [];

    /// <inheritdoc />
    public IDisposable Subscribe(Action onCommit)
    {
        ArgumentNullException.ThrowIfNull(onCommit);
        lock (_gate)
        {
            _subscribers.Add(onCommit);
        }

        return new Unsubscriber(this, onCommit);
    }

    internal void RaiseCommitted()
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

    /// <inheritdoc />
    public async ValueTask<StoreProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        await using (SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (SqlCommand ping = Command("SELECT 1", connection, null))
        {
            await ping.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        SqlServerSchemaMigrator migrator = CreateMigrator();
        try
        {
            await migrator.VerifyAsync(cancellationToken).ConfigureAwait(false);
            return new StoreProbe(SchemaCurrent: true, $"Schema '{Options.Schema}' is at V{migrator.LatestVersion:D3} or later.");
        }
        catch (SqlServerSchemaException e)
        {
            return new StoreProbe(SchemaCurrent: false, e.Message);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxBacklog>> GetOutboxBacklogAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.OutboxBacklog, connection, null);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var backlog = new List<OutboxBacklog>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            backlog.Add(new OutboxBacklog(reader.GetInt16(0), checked((int)reader.GetInt64(1)), reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2)));
        }

        return backlog;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, int>> CountParkedDeadLettersAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.ParkedDeadLettersByConsumer, connection, null, Int16("@status", (int)DeadLetterStatus.Parked));
        return await ReadCountsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<long> CountEventsAfterAsync(GlobalPosition after, BucketRange buckets, long cap, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.CountEventsAfter, connection, null,
            Int64("@rv", checked((long)after.Commit)), Int16("@lo", buckets.Low), Int16("@hi", buckets.High), Int64("@cap", cap));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// How long the oldest transaction writing to this database has run (it holds <c>MIN_ACTIVE_ROWVERSION()</c> back,
    /// doc 05 §5.4). Needs VIEW SERVER STATE, or VIEW DATABASE PERFORMANCE STATE on Azure SQL Database; zero without it.
    /// </summary>
    public async ValueTask<TimeSpan> GetHorizonAgeAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.HorizonAge, connection, null);
        try
        {
            object? milliseconds = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return TimeSpan.FromMilliseconds(milliseconds is long ms ? Math.Max(0, ms) : 0);
        }
        catch (SqlException e) when (e.Number is 297 or 300)
        {
            return TimeSpan.Zero;   // no permission to read the transaction views
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, int>> CountActiveProcessesAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.ActiveProcessesByType, connection, null);
        return await ReadCountsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<Workflows.WorkflowStatusCount>> CountActiveWorkflowsAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.ActiveWorkflows, connection, null);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var counts = new List<Workflows.WorkflowStatusCount>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts.Add(new Workflows.WorkflowStatusCount(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), checked((int)reader.GetInt64(3))));
        }

        return [.. counts.OrderBy(c => c.Workflow, StringComparer.Ordinal).ThenBy(c => c.Version).ThenBy(c => c.Status, StringComparer.Ordinal)];
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReadCountsAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[reader.GetString(0)] = checked((int)reader.GetInt64(1));
        }

        return counts;
    }

    private sealed class Unsubscriber(SqlServerStore store, Action callback) : IDisposable
    {
        public void Dispose()
        {
            lock (store._gate)
            {
                store._subscribers.Remove(callback);
            }
        }
    }
}

/// <summary>Rows as one JSON array for <c>OPENJSON</c>: many rows in one parameter (no table types, AOT-safe).</summary>
internal static class Rows
{
    private static readonly JsonWriterOptions Options = new() { Indented = false };

    public static string Write<T>(IEnumerable<T> items, Action<Utf8JsonWriter, T> write)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartArray();
            foreach (T item in items)
            {
                writer.WriteStartObject();
                write(writer, item);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
