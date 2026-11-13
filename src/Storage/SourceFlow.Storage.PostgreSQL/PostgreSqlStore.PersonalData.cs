using Npgsql;
using NpgsqlTypes;
using SourceFlow.Security;

namespace SourceFlow.Storage.PostgreSQL;

/// <content>Crypto-shredding storage (doc 10 §4, WP-5.2): <c>data_keys</c> and <c>subject_streams</c>, both per tenant.</content>
public sealed partial class PostgreSqlStore : IPersonalDataKeyStore
{
    /// <inheritdoc />
    public async ValueTask<SubjectKey?> GetKeyAsync(string tenantId, string subjectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT key_ref, wrapped_key, created_at, shredded_at FROM {Quoted}.data_keys WHERE tenant_id = @tenant AND subject_id = @subject", connection);
        command.Parameters.Add(new NpgsqlParameter<string>("tenant", tenantId));
        command.Parameters.Add(new NpgsqlParameter<string>("subject", subjectId));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SubjectKey(tenantId, subjectId, reader.GetString(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3))
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<SubjectKey> AddKeyAsync(SubjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using (NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = new NpgsqlCommand(
            $"INSERT INTO {Quoted}.data_keys (tenant_id, subject_id, key_ref, wrapped_key, created_at) VALUES (@tenant, @subject, @ref, @wrapped, @at) ON CONFLICT (tenant_id, subject_id) DO NOTHING", connection))
        {
            command.Parameters.Add(new NpgsqlParameter<string>("tenant", key.TenantId));
            command.Parameters.Add(new NpgsqlParameter<string>("subject", key.SubjectId));
            command.Parameters.Add(new NpgsqlParameter<string>("ref", key.KeyReference));
            command.Parameters.Add(new NpgsqlParameter<byte[]>("wrapped", key.WrappedKey.ToArray()));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("at", key.CreatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await GetKeyAsync(key.TenantId, key.SubjectId, cancellationToken).ConfigureAwait(false) ?? key;
    }

    /// <inheritdoc />
    public async ValueTask ShredKeyAsync(string tenantId, string subjectId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {Quoted}.data_keys (tenant_id, subject_id, key_ref, wrapped_key, created_at, shredded_at) VALUES (@tenant, @subject, '', '\x', @at, @at)
            ON CONFLICT (tenant_id, subject_id) DO UPDATE SET wrapped_key = '\x', shredded_at = COALESCE({Quoted}.data_keys.shredded_at, @at)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter<string>("tenant", tenantId));
        command.Parameters.Add(new NpgsqlParameter<string>("subject", subjectId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("at", at));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask RecordSubjectStreamsAsync(string tenantId, string subjectId, IReadOnlyCollection<StreamId> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        if (streams.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        return IgnoreCount(ExecuteTenantAsync(tenantId,
            $"INSERT INTO {Quoted}.subject_streams (tenant_id, subject_id, stream_id) SELECT @tenant, @subject, unnest(@streams) ON CONFLICT DO NOTHING",
            cancellationToken,
            new NpgsqlParameter<string>("tenant", tenantId),
            new NpgsqlParameter<string>("subject", subjectId),
            new NpgsqlParameter<string[]>("streams", [.. streams.Select(s => s.Value)]) { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text }));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<StreamId>> GetSubjectStreamsAsync(string tenantId, string subjectId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using TenantReader scope = await ReadTenantAsync(connection, null, tenantId, setTenant: true,
            $"SELECT stream_id FROM {Quoted}.subject_streams WHERE tenant_id = @tenant AND subject_id = @subject ORDER BY stream_id",
            [new NpgsqlParameter<string>("tenant", tenantId), new NpgsqlParameter<string>("subject", subjectId)], cancellationToken).ConfigureAwait(false);
        var streams = new List<StreamId>();
        while (await scope.Reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            streams.Add(new StreamId(scope.Reader.GetString(0)));
        }

        return streams;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        return IgnoreCount(ExecuteTenantAsync(tenantId, $"DELETE FROM {Quoted}.snapshots WHERE tenant_id = @tenant AND stream_id = @stream", cancellationToken,
            new NpgsqlParameter<string>("tenant", tenantId), new NpgsqlParameter<string>("stream", stream.Value)));
    }
}
