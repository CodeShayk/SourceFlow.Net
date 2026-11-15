using Npgsql;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.Tests.PostgreSql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PostgreSqlServer))]

namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>
/// One PostgreSQL server per test run: a Testcontainers container, or the server in <c>SOURCEFLOW_TEST_POSTGRES</c>.
/// Tests isolate themselves with a fresh schema each (<see cref="CreateStoreAsync"/>), so they never share state.
/// </summary>
public sealed class PostgreSqlServer : IAsyncLifetime
{
    /// <summary>The default image; the nightly matrix overrides it with <c>SOURCEFLOW_TEST_POSTGRES_IMAGE</c> (15, 16, 17).</summary>
    public const string DefaultImage = "postgres:17-alpine";

    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>A pooled data source for the superuser, shared by every test store.</summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    private readonly Lazy<Task> _started;

    public PostgreSqlServer()
    {
        _started = new Lazy<Task>(StartAsync);
    }

    // The server starts on first use, so test runs that select no PostgreSQL test never need Docker.
    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public Task EnsureStartedAsync()
    {
        return _started.Value;
    }

    private async Task StartAsync()
    {
        string? external = Environment.GetEnvironmentVariable("SOURCEFLOW_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
        }
        else
        {
            string image = Environment.GetEnvironmentVariable("SOURCEFLOW_TEST_POSTGRES_IMAGE") is { Length: > 0 } custom ? custom : DefaultImage;
            _container = new PostgreSqlBuilder(image).WithCommand("-c", "max_connections=300").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }

        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>A unique, valid schema name.</summary>
    public static string NewSchema(string prefix = "t")
    {
        return prefix + "_" + Guid.CreateVersion7().ToString("N");
    }

    /// <summary>
    /// True for the partitioned-events leg of the nightly matrix (<c>SOURCEFLOW_TEST_DATABASE=postgres:17-partitioned</c>):
    /// every store this fixture creates then partitions its events table (doc 05 §9).
    /// </summary>
    public static bool PartitionedVariant { get; } =
        Environment.GetEnvironmentVariable("SOURCEFLOW_TEST_DATABASE")?.EndsWith("-partitioned", StringComparison.Ordinal) == true;

    /// <summary>A store on a fresh, migrated schema.</summary>
    public async Task<PostgreSqlStore> CreateStoreAsync(TimeProvider? time = null, Action<PostgreSqlStorageOptions>? configure = null)
    {
        var options = new PostgreSqlStorageOptions { DataSource = DataSource, Schema = NewSchema(), PartitionEvents = PartitionedVariant };
        configure?.Invoke(options);
        var store = new PostgreSqlStore(options, time);
        await store.CreateMigrator().MigrateAsync(TestContext.Current.CancellationToken);
        return store;
    }

    private readonly string _roleSuffix = Guid.CreateVersion7().ToString("N")[..12];
    private readonly SemaphoreSlim _rolesGate = new(1, 1);
    private bool _rolesCreated;

    /// <summary>The application role: DML only, subject to row-level security (superusers always bypass it).</summary>
    public string AppRole => "sf_app_" + _roleSuffix;

    /// <summary>The cross-tenant reader role: DML and BYPASSRLS (ADR-022).</summary>
    public string SystemRole => "sf_system_" + _roleSuffix;

    /// <summary>The connection string of a role created by <see cref="CreateRowLevelSecurityStoreAsync"/>.</summary>
    public string ConnectionStringFor(string role)
    {
        return new NpgsqlConnectionStringBuilder(ConnectionString) { Username = role, Password = role }.ConnectionString;
    }

    /// <summary>
    /// A store with <see cref="TenantIsolation.RowLevelSecurity"/> on a fresh schema: migrated by the superuser (the owner),
    /// used through <see cref="AppRole"/>, with <see cref="SystemRole"/> for the global log.
    /// </summary>
    public async Task<PostgreSqlStore> CreateRowLevelSecurityStoreAsync(TimeProvider? time = null, string? schema = null)
    {
        schema ??= NewSchema("rls");
        await new PostgreSqlSchemaMigrator(DataSource, schema, TenantIsolation.RowLevelSecurity, PartitionedVariant).MigrateAsync(TestContext.Current.CancellationToken);
        await GrantAsync(schema);
        return new PostgreSqlStore(RowLevelSecurityOptions(schema), time);
    }

    /// <summary>Options for <see cref="AppRole"/> and <see cref="SystemRole"/> on a schema (call <see cref="GrantAsync"/> first).</summary>
    public PostgreSqlStorageOptions RowLevelSecurityOptions(string schema)
    {
        return new PostgreSqlStorageOptions
        {
            ConnectionString = ConnectionStringFor(AppRole),
            SystemConnectionString = ConnectionStringFor(SystemRole),
            Schema = schema,
            TenantIsolation = TenantIsolation.RowLevelSecurity,
            PartitionEvents = PartitionedVariant,
        };
    }

    /// <summary>Grants both test roles DML on a migrated schema.</summary>
    public async Task GrantAsync(string schema)
    {
        await _rolesGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (!_rolesCreated)
            {
                await ExecuteAsync($"CREATE ROLE \"{AppRole}\" LOGIN PASSWORD '{AppRole}'");
                await ExecuteAsync($"CREATE ROLE \"{SystemRole}\" LOGIN BYPASSRLS PASSWORD '{SystemRole}'");
                _rolesCreated = true;
            }
        }
        finally
        {
            _rolesGate.Release();
        }

        await ExecuteAsync($"""
            GRANT USAGE ON SCHEMA "{schema}" TO "{AppRole}", "{SystemRole}";
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA "{schema}" TO "{AppRole}", "{SystemRole}";
            GRANT USAGE ON ALL SEQUENCES IN SCHEMA "{schema}" TO "{AppRole}", "{SystemRole}";
            """);
    }

    /// <summary>Runs one SQL statement as the superuser.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>Base of PostgreSQL tests: starts the shared server before each test.</summary>
public abstract class PostgreSqlTest(PostgreSqlServer server) : IAsyncLifetime
{
    protected PostgreSqlServer Server { get; } = server;

    public virtual async ValueTask InitializeAsync()
    {
        await Server.EnsureStartedAsync();
    }

    public virtual ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>PostgreSQL tests that assert on the global log run one at a time: the safe horizon is cluster-wide (doc 05 §5.4).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlCollection
{
    public const string Name = "PostgreSQL";
}
