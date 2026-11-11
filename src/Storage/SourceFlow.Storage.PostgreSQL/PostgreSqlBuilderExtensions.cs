using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;

namespace SourceFlow.Storage.PostgreSQL;

/// <summary>Registers PostgreSQL storage on the SourceFlow builder.</summary>
public static class PostgreSqlBuilderExtensions
{
    /// <summary>Uses PostgreSQL storage with a connection string.</summary>
    public static SourceFlowBuilder UsePostgreSql(this SourceFlowBuilder builder, string connectionString, Action<PostgreSqlStorageOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UsePostgreSql(options =>
        {
            options.ConnectionString = connectionString;
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Uses PostgreSQL storage. At host start the schema is migrated (<see cref="PostgreSqlStorageOptions.AutoMigrate"/>,
    /// default: Development only) or verified, before any SourceFlow component starts (doc 05 §11).
    /// </summary>
    public static SourceFlowBuilder UsePostgreSql(this SourceFlowBuilder builder, Action<PostgreSqlStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new PostgreSqlStorageOptions();
        configure(options);
        options.Validate();

        builder.Services.TryAddSingleton(sp => new PostgreSqlStore(options, sp.GetService<TimeProvider>()));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PostgreSqlSchemaStartup>(sp =>
            new PostgreSqlSchemaStartup(sp.GetRequiredService<PostgreSqlStore>(), sp.GetService<IHostEnvironment>())));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<SourceFlow.Query.IQueryEngine, Query.PostgreSqlQueryEngine>(sp =>
            new Query.PostgreSqlQueryEngine(sp.GetRequiredService<PostgreSqlStore>())));
        return builder.UseStorage(sp => sp.GetRequiredService<PostgreSqlStore>().ToPorts());
    }
}

/// <summary>
/// Migrates or verifies the schema in <see cref="IHostedLifecycleService.StartingAsync"/>, which the host runs before
/// any hosted service's <c>StartAsync</c>, so the SourceFlow host never starts against a missing schema.
/// </summary>
internal sealed class PostgreSqlSchemaStartup : IHostedLifecycleService
{
    private readonly PostgreSqlStore _store;
    private readonly IHostEnvironment? _environment;

    public PostgreSqlSchemaStartup(PostgreSqlStore store, IHostEnvironment? environment)
    {
        _store = store;
        _environment = environment;
    }

    /// <summary>True when the schema is migrated at startup.</summary>
    public bool AutoMigrate => _store.Options.AutoMigrate ?? _environment?.IsDevelopment() ?? false;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        PostgreSqlSchemaMigrator migrator = _store.CreateMigrator();
        if (AutoMigrate)
        {
            await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await migrator.VerifyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
