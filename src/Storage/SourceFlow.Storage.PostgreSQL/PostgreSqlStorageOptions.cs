using System.Text.RegularExpressions;
using Npgsql;

namespace SourceFlow.Storage.PostgreSQL;

/// <summary>Options of the PostgreSQL storage adapter (doc 05 §2, §11).</summary>
public sealed partial class PostgreSqlStorageOptions
{
    /// <summary>The connection string. Ignored when <see cref="DataSource"/> is set.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// An existing data source (pooled). When set, the application owns it; otherwise the adapter builds one from
    /// <see cref="ConnectionString"/> and disposes it.
    /// </summary>
    public NpgsqlDataSource? DataSource { get; set; }

    /// <summary>The database schema that holds the SourceFlow tables. Lower-case letters, digits and underscores.</summary>
    public string Schema { get; set; } = "sourceflow";

    /// <summary>
    /// Applies pending migrations at startup. Null (the default) means on in the Development environment and off
    /// elsewhere; production deployments run <c>dotnet sourceflow db migrate</c> or apply <c>db script</c> output (doc 05 §11).
    /// </summary>
    public bool? AutoMigrate { get; set; }

    /// <summary>
    /// A separate connection string for the LISTEN connection, for deployments behind a transaction-pooling PgBouncer
    /// that cannot hold LISTEN (doc 06 §2). Defaults to <see cref="ConnectionString"/>.
    /// </summary>
    public string? ListenConnectionString { get; set; }

    /// <summary>The delay before the LISTEN connection reconnects after a failure. Subscribers keep polling meanwhile.</summary>
    public TimeSpan ListenReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The notification channel; defaults to the schema name.</summary>
    public string? NotificationChannel { get; set; }

    /// <summary>
    /// How tenants are isolated (ADR-022). <see cref="TenantIsolation.RowLevelSecurity"/> enables and forces row-level
    /// security on the tenant tables. Every tenant-scoped statement then runs with <c>sourceflow.tenant</c> set for its
    /// transaction, and the application role sees only that tenant's rows. Cross-tenant log reads use
    /// <see cref="SystemConnectionString"/> or <see cref="SystemDataSource"/>.
    /// </summary>
    public TenantIsolation TenantIsolation { get; set; } = TenantIsolation.Runtime;

    /// <summary>
    /// The connection string for cross-tenant readers (subscriptions reading the global log). It must be a role with
    /// <c>BYPASSRLS</c> and is required with <see cref="TenantIsolation.RowLevelSecurity"/>. Defaults to the main connection.
    /// </summary>
    public string? SystemConnectionString { get; set; }

    /// <summary>An existing data source for cross-tenant readers; takes precedence over <see cref="SystemConnectionString"/>.</summary>
    public NpgsqlDataSource? SystemDataSource { get; set; }

    internal string Channel => NotificationChannel ?? Schema;

    internal bool RowLevelSecurity => TenantIsolation == TenantIsolation.RowLevelSecurity;

    internal void Validate()
    {
        if (DataSource is null && string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("PostgreSQL storage needs a ConnectionString or a DataSource.");
        }

        if (!SchemaName().IsMatch(Schema))
        {
            throw new InvalidOperationException($"The schema name '{Schema}' is not valid: use 1–63 lower-case letters, digits and underscores, starting with a letter or underscore.");
        }

        if (!SchemaName().IsMatch(Channel))
        {
            throw new InvalidOperationException($"The notification channel '{Channel}' is not valid: use 1–63 lower-case letters, digits and underscores.");
        }

        if (ListenReconnectDelay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ListenReconnectDelay must be positive.");
        }

        if (RowLevelSecurity && SystemDataSource is null && string.IsNullOrWhiteSpace(SystemConnectionString))
        {
            throw new InvalidOperationException(
                "TenantIsolation.RowLevelSecurity needs SystemConnectionString or SystemDataSource: a role with BYPASSRLS for the cross-tenant global log (ADR-022).");
        }
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaName();
}

/// <summary>How tenants are isolated in shared tables (ADR-022).</summary>
public enum TenantIsolation
{
    /// <summary>Every query filters by tenant; the database does not enforce it.</summary>
    Runtime = 0,

    /// <summary>
    /// PostgreSQL row-level security as well: a query that forgets the tenant predicate, or forges another tenant's
    /// key, still sees only the session tenant's rows.
    /// </summary>
    RowLevelSecurity = 1,
}
