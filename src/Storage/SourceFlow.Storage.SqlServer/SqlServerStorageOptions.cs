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
    /// A read-only connection string for SFQL queries (for example with <c>ApplicationIntent=ReadOnly</c> to an
    /// availability-group secondary, doc 14 §10). Defaults to <see cref="ConnectionString"/>.
    /// </summary>
    public string? QueryConnectionString { get; set; }

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
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaName();
}
