using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;

namespace SourceFlow.Storage.SqlServer;

/// <summary>Registers SQL Server storage on the SourceFlow builder.</summary>
public static class SqlServerBuilderExtensions
{
    /// <summary>Uses SQL Server storage with a connection string.</summary>
    public static SourceFlowBuilder UseSqlServer(this SourceFlowBuilder builder, string connectionString, Action<SqlServerStorageOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UseSqlServer(options =>
        {
            options.ConnectionString = connectionString;
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Uses SQL Server storage. At host start the schema is migrated (<see cref="SqlServerStorageOptions.AutoMigrate"/>,
    /// default: Development only) or verified, before any SourceFlow component starts (doc 05 §11).
    /// </summary>
    public static SourceFlowBuilder UseSqlServer(this SourceFlowBuilder builder, Action<SqlServerStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new SqlServerStorageOptions();
        configure(options);
        options.Validate();

        builder.Services.TryAddSingleton(sp => new SqlServerStore(options, sp.GetService<TimeProvider>()));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<SourceFlow.Query.IQueryEngine, Query.SqlServerQueryEngine>(sp =>
            new Query.SqlServerQueryEngine(sp.GetRequiredService<SqlServerStore>())));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqlServerSchemaStartup>(sp =>
            new SqlServerSchemaStartup(sp.GetRequiredService<SqlServerStore>(), sp.GetService<IHostEnvironment>())));
        return builder.UseStorage(sp => sp.GetRequiredService<SqlServerStore>().ToPorts());
    }
}

/// <summary>Migrates or verifies the schema before any hosted service starts (<see cref="IHostedLifecycleService.StartingAsync"/>).</summary>
internal sealed class SqlServerSchemaStartup(SqlServerStore store, IHostEnvironment? environment) : IHostedLifecycleService
{
    /// <summary>True when the schema is migrated at startup.</summary>
    public bool AutoMigrate => store.Options.AutoMigrate ?? environment?.IsDevelopment() ?? false;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        SqlServerSchemaMigrator migrator = store.CreateMigrator();
        if (AutoMigrate)
        {
            await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await migrator.VerifyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
