using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SourceFlow.Query;

namespace SourceFlow.Storage.SqlServer.Query;

/// <summary>
/// Runs SFQL queries in SQL Server (doc 14 §7.1, WP-4.4): compiles the bound query to one parameterised statement and
/// executes it on the query connection (<see cref="SqlServerStorageOptions.QueryConnectionString"/>, for example a
/// read-only secondary) with the query timeout (I-7). The statement is a single SELECT: it reads nothing it could
/// change. Aggregate folds, schema rows and constructs the compiler declines stay in the in-memory interpreter.
/// </summary>
internal sealed class SqlServerQueryEngine(SqlServerStore store) : IQueryEngine, IQueryExplainer
{
    public string EngineName => "sqlserver";

    public (string Statement, int ParameterCount) Explain(QueryExecution execution)
    {
        CompiledQuery compiled = SqlServerQueryCompiler.Compile(execution, store.Options.Schema);
        return (compiled.Sql, compiled.Parameters.Count);
    }

    public bool CanExecute(BoundQuery query)
    {
        return SqlServerQueryCompiler.CanCompile(query, store.Options.Schema);
    }

    public async IAsyncEnumerable<QueryResultRow> ExecuteAsync(QueryExecution execution, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CompiledQuery compiled = SqlServerQueryCompiler.Compile(execution, store.Options.Schema);
        await using var connection = new SqlConnection(store.Options.QueryConnectionString ?? store.Options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(compiled.Sql, connection)
        {
            CommandTimeout = Math.Max(1, (int)Math.Ceiling(store.Options.QueryTimeout.TotalSeconds)),
        };
        foreach (SqlParameter parameter in compiled.Parameters)
        {
            command.Parameters.Add(((ICloneable)parameter).Clone());
        }

        SqlDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException e) when (IsTimeout(e, cancellationToken))
        {
            throw new TimeoutException("The query exceeded its timeout.", e);
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

    private static bool IsTimeout(SqlException e, CancellationToken cancellationToken)
    {
        return e.Number == -2 && !cancellationToken.IsCancellationRequested;
    }

    private static async ValueTask<bool> ReadAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException e) when (IsTimeout(e, cancellationToken))
        {
            throw new TimeoutException("The query exceeded its timeout.", e);
        }
    }

    // Reads a value as the interpreter would produce it (doc 14 §9.5 value types).
    private static object? Read(SqlDataReader reader, ResultShape shape)
    {
        int i = shape.Column;
        if (shape.Position)
        {
            return reader.IsDBNull(i) ? null : new GlobalPosition((ulong)reader.GetInt64(i), (ulong)reader.GetInt64(i + 1));
        }

        if (reader.IsDBNull(i))
        {
            return null;
        }

        if (shape.Tags)
        {
            return JsonSerializer.Deserialize(reader.GetString(i), SqlServerJsonContext.Default.StringArray) ?? [];
        }

        object? value = reader.GetDataTypeName(i) switch
        {
            "nvarchar" or "varchar" or "nchar" or "char" when shape.Type is QueryType.Object or QueryType.Array => Json(reader.GetString(i), shape.Type),
            "nvarchar" or "varchar" or "nchar" or "char" => reader.GetString(i),
            "uniqueidentifier" => reader.GetGuid(i).ToString("D"),
            "bigint" => reader.GetInt64(i),
            "int" => (long)reader.GetInt32(i),
            "smallint" => (long)reader.GetInt16(i),
            "tinyint" => (long)reader.GetByte(i),
            "decimal" or "numeric" or "money" => reader.GetDecimal(i),
            "float" => (decimal)reader.GetDouble(i),
            "real" => (decimal)reader.GetFloat(i),
            "bit" => reader.GetBoolean(i),
            "datetimeoffset" => reader.GetDateTimeOffset(i).ToUniversalTime(),
            "datetime2" or "datetime" => new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(i), DateTimeKind.Utc)),
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
