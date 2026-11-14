using System.Text.RegularExpressions;

namespace SourceFlow.Storage.SqlServer;

/// <summary>Options of the SQL Server storage adapter (doc 05 §5.5, §11).</summary>
public sealed partial class SqlServerStorageOptions
{
    /// <summary>The connection string (SQL Server 2022 or later, or Azure SQL Database). Connections are pooled by SqlClient.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The database schema that holds the SourceFlow tables. Lower-case letters, digits and underscores.</summary>
    public string Schema { get; set; } = "sourceflow";

    /// <summary>
    /// Applies pending migrations at startup. Null (the default) means on in the Development environment and off
    /// elsewhere; production deployments run the migrator or apply its script (doc 05 §11).
    /// </summary>
    public bool? AutoMigrate { get; set; }

    /// <summary>The command timeout for SFQL queries (doc 14 §9.4: default 5 s, at most 30 s).</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The connection for SFQL queries (doc 14 §7.1, §10, Q-R3, CR-23): it must set <c>ApplicationIntent=ReadOnly</c>, so
    /// an availability group routes it to a readable secondary, and should log in as a user that can only read
    /// (<see cref="SqlServerSchemaMigrator.QueryGrantsScript"/>). Defaults to <see cref="ConnectionString"/> with
    /// <c>ApplicationIntent=ReadOnly</c>: queries never use the write connection as it is.
    /// </summary>
    public string? QueryConnectionString { get; set; }

    /// <summary>The connection string queries use: <see cref="QueryConnectionString"/>, or the read-intent form of <see cref="ConnectionString"/>.</summary>
    internal string QueryConnection => QueryConnectionString
        ?? new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString) { ApplicationIntent = Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly }.ConnectionString;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("SQL Server storage needs a ConnectionString.");
        }

        if (!SchemaName().IsMatch(Schema))
        {
            throw new InvalidOperationException($"The schema name '{Schema}' is not valid: use 1–63 lower-case letters, digits and underscores, starting with a letter or underscore.");
        }

        if (QueryTimeout <= TimeSpan.Zero || QueryTimeout > TimeSpan.FromSeconds(30))
        {
            throw new InvalidOperationException("QueryTimeout must be positive and at most 30 seconds.");
        }

        if (QueryConnectionString is { } query
            && new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(query).ApplicationIntent != Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly)
        {
            throw new InvalidOperationException("QueryConnectionString must set ApplicationIntent=ReadOnly: SFQL queries run on a read-only connection (doc 14 §7.1).");
        }
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaName();
}
