using Npgsql;

namespace SourceFlow.Storage.PostgreSQL;

/// <content>
/// Tenant-scoped statements (ADR-022). With <see cref="TenantIsolation.RowLevelSecurity"/> a statement on a tenant table
/// is sent in one batch after <c>set_config('sourceflow.tenant', tenant, true)</c>. The batch is one implicit
/// transaction, or part of the session's explicit one, so the setting covers exactly that work and never leaks to the
/// next user of a pooled connection.
/// </content>
public sealed partial class PostgreSqlStore
{
    /// <summary>The connections for cross-tenant readers (the global log); a BYPASSRLS role with row-level security.</summary>
    public NpgsqlDataSource SystemDataSource { get; }

    internal bool RowLevelSecurity => Options.RowLevelSecurity;

    internal static NpgsqlBatchCommand SetTenantCommand(string tenantId)
    {
        var command = new NpgsqlBatchCommand("SELECT set_config('sourceflow.tenant', @tenant, true)");
        command.Parameters.Add(new NpgsqlParameter<string>("tenant", tenantId));
        return command;
    }

    // Runs a tenant-scoped query; the returned reader is positioned on the query's result. setTenant is false inside a
    // session transaction that has already set the tenant.
    internal async ValueTask<TenantReader> ReadTenantAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string tenantId, bool setTenant,
        string sql, NpgsqlParameter[] parameters, CancellationToken cancellationToken)
    {
        var batch = new NpgsqlBatch(connection, transaction);
        bool prefixed = RowLevelSecurity && setTenant;
        if (prefixed)
        {
            batch.BatchCommands.Add(SetTenantCommand(tenantId));
        }

        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddRange(parameters);
        batch.BatchCommands.Add(command);
        try
        {
            NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (prefixed)
            {
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            }

            return new TenantReader(batch, reader);
        }
        catch
        {
            await batch.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs a tenant-scoped statement on a new connection; returns the rows it affected.</summary>
    internal async ValueTask<int> ExecuteTenantAsync(string tenantId, string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(connection);
        if (RowLevelSecurity)
        {
            batch.BatchCommands.Add(SetTenantCommand(tenantId));
        }

        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddRange(parameters);
        batch.BatchCommands.Add(command);
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return command.RecordsAffected;
    }

    /// <summary>A reader with the batch that owns it.</summary>
    internal sealed class TenantReader(NpgsqlBatch batch, NpgsqlDataReader reader) : IAsyncDisposable
    {
        public NpgsqlDataReader Reader { get; } = reader;

        public async ValueTask DisposeAsync()
        {
            await Reader.DisposeAsync().ConfigureAwait(false);
            await batch.DisposeAsync().ConfigureAwait(false);
        }
    }
}
