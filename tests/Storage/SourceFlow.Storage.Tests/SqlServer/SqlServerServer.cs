using Microsoft.Data.SqlClient;
using SourceFlow.Storage.SqlServer;
using SourceFlow.Storage.Tests.SqlServer;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(SqlServerServer))]

namespace SourceFlow.Storage.Tests.SqlServer;

/// <summary>
/// One SQL Server per test run: a Testcontainers container (SQL Server 2022), or the server in
/// <c>SOURCEFLOW_TEST_SQLSERVER</c>. Tests isolate themselves with a fresh schema each, so they never share state. It
/// starts on first use, so runs that select no SQL Server test never need Docker.
/// </summary>
public sealed class SqlServerServer : IAsyncLifetime
{
    /// <summary>The default image; the nightly matrix overrides it with <c>SOURCEFLOW_TEST_SQLSERVER_IMAGE</c> (2022, 2025).</summary>
    public const string DefaultImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    private readonly Lazy<Task> _started;
    private MsSqlContainer? _container;

    public SqlServerServer()
    {
        _started = new Lazy<Task>(StartAsync);
    }

    public string ConnectionString { get; private set; } = string.Empty;

    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public Task EnsureStartedAsync()
    {
        return _started.Value;
    }

    public async ValueTask DisposeAsync()
    {
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

    /// <summary>A store on a fresh, migrated schema.</summary>
    public async Task<SqlServerStore> CreateStoreAsync(TimeProvider? time = null, string? schema = null)
    {
        var store = new SqlServerStore(new SqlServerStorageOptions { ConnectionString = ConnectionString, Schema = schema ?? NewSchema() }, time);
        await store.CreateMigrator().MigrateAsync(TestContext.Current.CancellationToken);
        return store;
    }

    /// <summary>Runs one statement.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task StartAsync()
    {
        string? external = Environment.GetEnvironmentVariable("SOURCEFLOW_TEST_SQLSERVER");
        string server;
        if (!string.IsNullOrWhiteSpace(external))
        {
            server = external;
        }
        else
        {
            string image = Environment.GetEnvironmentVariable("SOURCEFLOW_TEST_SQLSERVER_IMAGE") is { Length: > 0 } custom ? custom : DefaultImage;
            _container = new MsSqlBuilder(image).Build();
            await _container.StartAsync();
            server = _container.GetConnectionString();
        }

        // A database of our own with snapshot reads (doc 05 §5.5 recommends READ_COMMITTED_SNAPSHOT).
        var builder = new SqlConnectionStringBuilder(server) { TrustServerCertificate = true };
        string database = string.IsNullOrEmpty(builder.InitialCatalog) || builder.InitialCatalog == "master" ? "sourceflow" : builder.InitialCatalog;
        builder.InitialCatalog = "master";
        await using (var connection = new SqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                $"IF DB_ID(N'{database}') IS NULL BEGIN CREATE DATABASE [{database}]; ALTER DATABASE [{database}] SET READ_COMMITTED_SNAPSHOT ON; END",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        builder.InitialCatalog = database;
        builder.MaxPoolSize = 300;
        ConnectionString = builder.ConnectionString;
    }
}

/// <summary>Base of SQL Server tests: starts the shared server before each test.</summary>
public abstract class SqlServerTest(SqlServerServer server) : IAsyncLifetime
{
    protected SqlServerServer Server { get; } = server;

    public virtual async ValueTask InitializeAsync()
    {
        await Server.EnsureStartedAsync();
    }

    public virtual ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>SQL Server tests that assert on the global log run one at a time: the horizon is database-wide.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlServerCollection
{
    public const string Name = "SqlServer";
}
