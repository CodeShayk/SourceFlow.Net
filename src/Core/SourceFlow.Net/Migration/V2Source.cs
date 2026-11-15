using System.Data.Common;
using System.Globalization;

namespace SourceFlow.Migration;

/// <summary>The SQL dialect of a v2 database (v2's EF Core store ran on any relational provider).</summary>
public enum V2Dialect
{
    /// <summary>PostgreSQL (Npgsql).</summary>
    PostgreSql,

    /// <summary>SQL Server (Microsoft.Data.SqlClient).</summary>
    SqlServer,
}

/// <summary>
/// A v2 database, read with plain SQL (doc 12 §3): the EF <c>CommandRecord</c> table and one table per v2 entity type.
/// No v2 assembly is referenced; entity rows are read by <see cref="IV2Entity{TSelf}.Read"/>.
/// </summary>
public sealed class V2Source
{
    /// <summary>The default schema of the read-only audit copy that <c>migrate v2 archive</c> writes.</summary>
    public const string DefaultArchiveSchema = "sourceflow_v2_archive";

    /// <summary>The audit copy of the command table.</summary>
    public const string ArchiveTable = "commands";

    /// <summary>Creates a source.</summary>
    /// <param name="dataSource">The v2 database. The caller owns it.</param>
    /// <param name="dialect">Its SQL dialect.</param>
    public V2Source(DbDataSource dataSource, V2Dialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DataSource = dataSource;
        Dialect = dialect;
    }

    /// <summary>The v2 database.</summary>
    public DbDataSource DataSource { get; }

    /// <summary>The SQL dialect.</summary>
    public V2Dialect Dialect { get; }

    /// <summary>
    /// The schema of the v2 tables (v2's <c>TableNamingConvention.SchemaName</c>), or null for the connection's default
    /// schema (<c>public</c>, <c>dbo</c>).
    /// </summary>
    public string? Schema { get; init; }

    /// <summary>
    /// The command table's name after v2's naming convention (prefix, suffix, casing, pluralisation); <c>CommandRecord</c>
    /// by default.
    /// </summary>
    public string CommandTable { get; init; } = "CommandRecord";

    /// <summary>The schema of the archive (<c>sourceflow_v2_archive</c> by default), in the v2 database.</summary>
    public string ArchiveSchema { get; init; } = DefaultArchiveSchema;

    /// <summary>Quotes an identifier.</summary>
    internal string Quote(string identifier)
    {
        return Dialect == V2Dialect.SqlServer
            ? "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]"
            : "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>A v2 table, schema-qualified when <see cref="Schema"/> is set.</summary>
    internal string Table(string name)
    {
        return Schema is null ? Quote(name) : Quote(Schema) + "." + Quote(name);
    }

    internal string Commands => Table(CommandTable);

    internal string Archive => Quote(ArchiveSchema) + "." + Quote(ArchiveTable);

    /// <summary>The schema that holds the v2 tables, as a SQL expression.</summary>
    internal string SchemaExpression => Schema is not null
        ? "'" + Schema.Replace("'", "''", StringComparison.Ordinal) + "'"
        : Dialect == V2Dialect.SqlServer ? "SCHEMA_NAME()" : "current_schema()";

    /// <summary>A keyset page: rows of <paramref name="table"/> with <c>Id</c> above <c>@after</c>, in id order.</summary>
    internal string Page(string table, int size)
    {
        string n = size.ToString(CultureInfo.InvariantCulture);
        return Dialect == V2Dialect.SqlServer
            ? $"SELECT TOP ({n}) * FROM {table} WHERE {Quote("Id")} > @after ORDER BY {Quote("Id")}"
            : $"SELECT * FROM {table} WHERE {Quote("Id")} > @after ORDER BY {Quote("Id")} LIMIT {n}";
    }

    internal static DbCommand Command(DbConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    internal async Task<long> ScalarAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        DbConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            DbCommand command = Command(connection, sql, parameters);
            await using (command.ConfigureAwait(false))
            {
                object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }
    }

    internal async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        DbConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            DbCommand command = Command(connection, sql);
            await using (command.ConfigureAwait(false))
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The base tables of the v2 schema, excluding the command table.</summary>
    internal async Task<IReadOnlyList<string>> TablesAsync(CancellationToken cancellationToken)
    {
        string sql = $"SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_SCHEMA = {SchemaExpression} ORDER BY TABLE_NAME";
        var tables = new List<string>();
        DbConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            DbCommand command = Command(connection, sql);
            await using (command.ConfigureAwait(false))
            {
                DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        string table = reader.GetString(0);
                        if (!string.Equals(table, CommandTable, StringComparison.Ordinal))
                        {
                            tables.Add(table);
                        }
                    }
                }
            }
        }

        return tables;
    }

    /// <summary>The columns of a v2 table, in ordinal order.</summary>
    internal async Task<IReadOnlyList<V2Column>> ColumnsAsync(string table, CancellationToken cancellationToken)
    {
        string sql = $"SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = {SchemaExpression} AND TABLE_NAME = @table ORDER BY ORDINAL_POSITION";
        var columns = new List<V2Column>();
        DbConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            DbCommand command = Command(connection, sql, ("@table", table));
            await using (command.ConfigureAwait(false))
            {
                DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        columns.Add(new V2Column(reader.GetString(0), reader.GetString(1), string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
        }

        return columns;
    }
}

/// <summary>A column of a v2 table.</summary>
/// <param name="Name">The column name.</param>
/// <param name="DataType">The database type (<c>INFORMATION_SCHEMA.COLUMNS.DATA_TYPE</c>).</param>
/// <param name="Nullable">True when the column allows null.</param>
public sealed record V2Column(string Name, string DataType, bool Nullable);
