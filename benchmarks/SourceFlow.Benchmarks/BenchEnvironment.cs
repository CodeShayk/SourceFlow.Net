using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SourceFlow.Hosting;
using SourceFlow.Storage.PostgreSQL;
using Testcontainers.PostgreSql;

namespace SourceFlow.Benchmarks;

/// <summary>The PostgreSQL server and SourceFlow hosts used by the component benchmarks.</summary>
internal static class BenchEnvironment
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? _container;
    private static NpgsqlDataSource? _dataSource;

    /// <summary>A pooled superuser data source: a container started once per process, or <c>SOURCEFLOW_BENCH_POSTGRES</c>.</summary>
    public static async Task<NpgsqlDataSource> DatabaseAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_dataSource is not null)
            {
                return _dataSource;
            }

            string? external = Environment.GetEnvironmentVariable("SOURCEFLOW_BENCH_POSTGRES");
            string connectionString;
            if (!string.IsNullOrWhiteSpace(external))
            {
                connectionString = external;
            }
            else
            {
                _container = new PostgreSqlBuilder("postgres:17-alpine").WithCommand("-c", "max_connections=300").Build();
                await _container.StartAsync();
                connectionString = _container.GetConnectionString();
            }

            _dataSource = NpgsqlDataSource.Create(connectionString);
            return _dataSource;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task StopAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
            _dataSource = null;
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    /// <summary>A migrated store on a fresh schema.</summary>
    public static async Task<PostgreSqlStore> StoreAsync()
    {
        NpgsqlDataSource dataSource = await DatabaseAsync();
        var store = new PostgreSqlStore(new PostgreSqlStorageOptions { DataSource = dataSource, Schema = "bench_" + Guid.CreateVersion7().ToString("N") });
        await store.CreateMigrator().MigrateAsync();
        return store;
    }

    /// <summary>A built (not started) host on in-memory storage.</summary>
    public static IHost InMemoryHost(Action<SourceFlowBuilder>? configure = null)
    {
        return Host(sf =>
        {
            sf.UseInMemoryStorage();
            configure?.Invoke(sf);
        });
    }

    /// <summary>A built (not started) host on PostgreSQL storage, on a fresh schema migrated at start.</summary>
    public static async Task<IHost> PostgreSqlHostAsync(Action<SourceFlowBuilder>? configure = null)
    {
        NpgsqlDataSource dataSource = await DatabaseAsync();
        string schema = "bench_" + Guid.CreateVersion7().ToString("N");
        await new PostgreSqlSchemaMigrator(dataSource, schema).MigrateAsync();
        return Host(sf =>
        {
            sf.UsePostgreSql(o =>
            {
                o.DataSource = dataSource;
                o.Schema = schema;
                o.AutoMigrate = false;
            });
            configure?.Invoke(sf);
        });
    }

    private static IHost Host(Action<SourceFlowBuilder> configure)
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "bench";
            sf.AddModule<BenchModule>();
            sf.UseInMemoryTransport();
            configure(sf);
        });
        return builder.Build();
    }
}
