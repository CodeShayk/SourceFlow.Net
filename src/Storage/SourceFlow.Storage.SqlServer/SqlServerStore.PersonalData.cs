using System.Text.Json;
using Microsoft.Data.SqlClient;
using SourceFlow.Security;

namespace SourceFlow.Storage.SqlServer;

/// <content>Crypto-shredding storage (doc 10 §4, WP-5.2): <c>data_keys</c> (global) and <c>subject_streams</c> (per tenant).</content>
public sealed partial class SqlServerStore : IPersonalDataKeyStore
{
    private string Keys => $"[{Options.Schema}].data_keys";

    /// <inheritdoc />
    public async ValueTask<SubjectKey?> GetKeyAsync(string subjectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command($"SELECT key_ref, wrapped_key, created_at, shredded_at FROM {Keys} WHERE subject_id = @subject", connection, null, Text("@subject", subjectId));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SubjectKey(subjectId, reader.GetString(0), (byte[])reader.GetValue(1), reader.GetDateTimeOffset(2), reader.IsDBNull(3) ? null : reader.GetDateTimeOffset(3))
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<SubjectKey> AddKeyAsync(SubjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await ExecuteAsync(
            $"INSERT INTO {Keys} (subject_id, key_ref, wrapped_key, created_at) SELECT @subject, @ref, @wrapped, @at WHERE NOT EXISTS (SELECT 1 FROM {Keys} WITH (UPDLOCK, HOLDLOCK) WHERE subject_id = @subject)",
            cancellationToken, Text("@subject", key.SubjectId), Text("@ref", key.KeyReference), Binary("@wrapped", key.WrappedKey.ToArray()), At("@at", key.CreatedAt)).ConfigureAwait(false);
        return await GetKeyAsync(key.SubjectId, cancellationToken).ConfigureAwait(false) ?? key;
    }

    /// <inheritdoc />
    public ValueTask ShredKeyAsync(string subjectId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        return ExecuteAsync(
            $"""
            MERGE {Keys} WITH (HOLDLOCK) AS k USING (SELECT @subject AS subject_id) AS s ON k.subject_id = s.subject_id
            WHEN MATCHED THEN UPDATE SET wrapped_key = 0x, shredded_at = COALESCE(k.shredded_at, @at)
            WHEN NOT MATCHED THEN INSERT (subject_id, key_ref, wrapped_key, created_at, shredded_at) VALUES (@subject, N'', 0x, @at, @at);
            """,
            cancellationToken, Text("@subject", subjectId), At("@at", at));
    }

    /// <inheritdoc />
    public ValueTask RecordSubjectStreamsAsync(string tenantId, string subjectId, IReadOnlyCollection<StreamId> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        if (streams.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        string json = JsonSerializer.Serialize([.. streams.Select(s => s.Value)], SqlServerJsonContext.Default.StringArray);
        return ExecuteAsync(
            $"""
            INSERT INTO [{Options.Schema}].subject_streams (tenant_id, subject_id, stream_id)
            SELECT @tenant, @subject, j.s FROM OPENJSON(@streams) WITH (s nvarchar(200) COLLATE Latin1_General_100_BIN2 '$') j
             WHERE NOT EXISTS (SELECT 1 FROM [{Options.Schema}].subject_streams x WITH (UPDLOCK, HOLDLOCK)
                                WHERE x.tenant_id = @tenant AND x.subject_id = @subject AND x.stream_id = j.s)
            """,
            cancellationToken, Text("@tenant", tenantId), Text("@subject", subjectId), MaxText("@streams", json));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<StreamId>> GetSubjectStreamsAsync(string tenantId, string subjectId, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command($"SELECT stream_id FROM [{Options.Schema}].subject_streams WHERE tenant_id = @tenant AND subject_id = @subject ORDER BY stream_id",
            connection, null, Text("@tenant", tenantId), Text("@subject", subjectId));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var streams = new List<StreamId>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            streams.Add(new StreamId(reader.GetString(0)));
        }

        return streams;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        return ExecuteAsync($"DELETE FROM [{Options.Schema}].snapshots WHERE tenant_id = @tenant AND stream_id = @stream", cancellationToken,
            Text("@tenant", tenantId), Text("@stream", stream.Value));
    }
}
