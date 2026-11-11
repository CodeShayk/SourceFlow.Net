using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using SourceFlow.Query;

namespace SourceFlow.Storage.PostgreSQL.Query;

/// <summary>
/// Runs SFQL queries in PostgreSQL (doc 14 §7.1, WP-2.8): compiles the bound query to one parameterised statement and
/// executes it in a read-only transaction with a statement timeout (I-7), setting the tenant for row-level security
/// (ADR-022). Aggregate folds and schema rows stay in the in-memory interpreter.
/// </summary>
internal sealed class PostgreSqlQueryEngine(PostgreSqlStore store) : IQueryEngine
{
    public bool CanExecute(BoundQuery query)
    {
        return PostgreSqlQueryCompiler.CanCompile(query);
    }

    public async IAsyncEnumerable<QueryResultRow> ExecuteAsync(QueryExecution execution, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CompiledQuery compiled = PostgreSqlQueryCompiler.Compile(execution, store.Options.Schema);
        bool allTenants = execution.Query.Options.AllTenants;
        NpgsqlDataSource source = allTenants ? store.SystemDataSource : store.DataSource;
        await using NpgsqlConnection connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var setup = new NpgsqlBatch(connection, transaction))
        {
            setup.BatchCommands.Add(new NpgsqlBatchCommand("SET TRANSACTION READ ONLY"));
            var timeout = new NpgsqlBatchCommand("SELECT set_config('statement_timeout', @timeout, true)");
            timeout.Parameters.Add(new NpgsqlParameter<string>("timeout", ((long)store.Options.QueryTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)));
            setup.BatchCommands.Add(timeout);
            if (store.RowLevelSecurity && !allTenants)
            {
                setup.BatchCommands.Add(PostgreSqlStore.SetTenantCommand(execution.Tenant));
            }

            await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand(compiled.Sql, connection, transaction);
        foreach (NpgsqlParameter parameter in compiled.Parameters)
        {
            command.Parameters.Add(parameter);
        }
        NpgsqlDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.QueryCanceled && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The query exceeded its statement timeout.", e);
        }

        await using (reader.ConfigureAwait(false))
        {
            while (await ReadAsync(reader, cancellationToken).ConfigureAwait(false))
            {
                yield return new QueryResultRow(
                    [.. compiled.Outputs.Select(s => Read(reader, s))],
                    [.. compiled.Keys.Select(s => Read(reader, s))]);
            }
        }
    }

    private static async ValueTask<bool> ReadAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.QueryCanceled && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The query exceeded its statement timeout.", e);
        }
    }

    // Reads a value as the interpreter would produce it (doc 14 §9.5 value types).
    private static object? Read(NpgsqlDataReader reader, ResultShape shape)
    {
        int i = shape.Column;
        if (shape.Position)
        {
            return reader.IsDBNull(i) ? null : new GlobalPosition(reader.GetFieldValue<ulong>(i), (ulong)reader.GetInt64(i + 1));
        }

        if (reader.IsDBNull(i))
        {
            return null;
        }

        object? value = reader.GetDataTypeName(i) switch
        {
            "text" or "character varying" or "uuid" => reader.GetFieldValue<string>(i),
            "bigint" => reader.GetInt64(i),
            "integer" => (long)reader.GetInt32(i),
            "smallint" => (long)reader.GetInt16(i),
            "numeric" => reader.GetDecimal(i),
            "double precision" => (decimal)reader.GetDouble(i),
            "boolean" => reader.GetBoolean(i),
            "timestamp with time zone" => new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(i), DateTimeKind.Utc)),
            "interval" => reader.GetFieldValue<TimeSpan>(i),
            "text[]" => reader.GetFieldValue<string[]>(i),
            "json" or "jsonb" => Json(reader.GetFieldValue<string>(i), shape.Type),
            _ => reader.GetValue(i),
        };

        return shape.Type switch
        {
            QueryType.Integer when value is decimal d && d == decimal.Truncate(d) && d >= long.MinValue && d <= long.MaxValue => (long)d,
            QueryType.Decimal when value is long l => (decimal)l,
            _ => value,
        };
    }

    private static object? Json(string text, QueryType type)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement element = document.RootElement;
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Object or JsonValueKind.Array => element.Clone(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when type != QueryType.Decimal && element.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number => element.GetDecimal(),
            _ => element.GetString(),
        };
    }
}
