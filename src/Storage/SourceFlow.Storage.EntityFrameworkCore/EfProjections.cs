using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SourceFlow.Hosting;

namespace SourceFlow.Storage.EntityFrameworkCore;

/// <summary>
/// An EF Core projection (doc 03 §6.3): writes relational read models through a <typeparamref name="TDbContext"/> bound to
/// the storage session, so the read model commits with the events (inline) or with the subscription checkpoint (async).
/// Mark the class <c>[Projection("name")]</c>, make it <c>partial</c>, and declare
/// <c>When(TEvent e, TDbContext db, EventContext context, CancellationToken cancellationToken)</c> methods returning
/// <see cref="ValueTask"/> or <see cref="Task"/>.
/// </summary>
/// <remarks>
/// Tracked changes are saved once, before the commit. Set-based statements (<c>ExecuteUpdateAsync</c>) run immediately,
/// so do not mix them with tracked changes to the same rows in one projection.
/// </remarks>
/// <typeparam name="TDbContext">The read-model context.</typeparam>
public interface IEfProjection<TDbContext> : ISessionProjection<TDbContext>
    where TDbContext : DbContext;

/// <summary>
/// A <see cref="DbContext"/> whose tables live in a schema chosen per read-model generation (doc 06 §6): map the model
/// with <c>modelBuilder.HasDefaultSchema(Schema)</c>. The model is cached per schema.
/// </summary>
public interface IGenerationSchemaContext
{
    /// <summary>The schema this context maps its tables to.</summary>
    string Schema { get; }
}

/// <summary>Schema-per-generation settings for EF Core projections (doc 06 §6, ADR-032).</summary>
public sealed class EfGenerationOptions
{
    /// <summary>The schema of a generation; by default <see cref="EfGenerations.DefaultSchema"/>.</summary>
    public Func<ReadModelGeneration, string> Schema { get; set; } = EfGenerations.DefaultSchema;

    /// <summary>
    /// The statement that drops a retired generation's schema (PostgreSQL by default:
    /// <c>DROP SCHEMA IF EXISTS "s" CASCADE</c>). The schema is a generated identifier, never user input.
    /// </summary>
    public Func<string, string> DropSchemaSql { get; set; } = schema => $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";

    /// <summary>
    /// The statement that creates a generation's schema before EF's create script runs
    /// (<c>CREATE SCHEMA IF NOT EXISTS "s"</c> by default).
    /// </summary>
    public Func<string, string> CreateSchemaSql { get; set; } = schema => $"CREATE SCHEMA IF NOT EXISTS \"{schema}\"";
}

/// <summary>Schema names for EF Core read-model generations.</summary>
public static class EfGenerations
{
    /// <summary>
    /// <c>{name}_v{version}</c> (<c>_{number}</c> for a rebuild), lower case, with every character outside
    /// <c>[a-z0-9_]</c> replaced by <c>_</c>: <c>account-activity@2</c> → <c>account_activity_v2</c>.
    /// </summary>
    public static string DefaultSchema(ReadModelGeneration generation)
    {
        var name = new StringBuilder(generation.Name.Length + 8);
        foreach (char c in generation.Name.ToLowerInvariant())
        {
            name.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' ? c : '_');
        }

        name.Append(CultureInfo.InvariantCulture, $"_v{generation.Version}");
        if (generation.Number > 1)
        {
            name.Append(CultureInfo.InvariantCulture, $"_{generation.Number}");
        }

        return name.ToString();
    }
}

/// <summary>Caches EF models per <see cref="IGenerationSchemaContext.Schema"/>, so each generation maps its own schema.</summary>
public sealed class GenerationModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (context.GetType(), (context as IGenerationSchemaContext)?.Schema, designTime);
    }
}

/// <summary>Creates <see cref="DbContext"/> instances on a storage session's connection and transaction.</summary>
/// <typeparam name="TDbContext">The context type.</typeparam>
public sealed class EfSessionContextFactory<TDbContext> : IProjectionUnitOfWork<TDbContext>, IProjectionGenerationStore
    where TDbContext : DbContext
{
    private readonly Action<DbContextOptionsBuilder<TDbContext>, DbConnection> _configure;
    private readonly Func<DbContextOptions<TDbContext>, TDbContext> _create;
    private readonly Func<DbContextOptions<TDbContext>, string, TDbContext>? _createForSchema;
    private readonly EfGenerationOptions? _generations;
    private readonly ConcurrentDictionary<string, bool> _created = new(StringComparer.Ordinal);

    /// <summary>Creates the factory.</summary>
    /// <param name="configure">Selects the provider on the session's connection, for example <c>(o, c) =&gt; o.UseNpgsql(c)</c>.</param>
    /// <param name="create">Creates the context, for example <c>o =&gt; new ReportingDbContext(o)</c>.</param>
    public EfSessionContextFactory(Action<DbContextOptionsBuilder<TDbContext>, DbConnection> configure, Func<DbContextOptions<TDbContext>, TDbContext> create)
    {
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
        _create = create ?? throw new ArgumentNullException(nameof(create));
    }

    private EfSessionContextFactory(Action<DbContextOptionsBuilder<TDbContext>, DbConnection> configure, Func<DbContextOptions<TDbContext>, string, TDbContext> createForSchema, EfGenerationOptions generations)
    {
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
        _createForSchema = createForSchema ?? throw new ArgumentNullException(nameof(createForSchema));
        _generations = generations ?? throw new ArgumentNullException(nameof(generations));
        _create = options => _createForSchema(options, string.Empty);
    }

    /// <summary>
    /// A factory that writes each read-model generation to its own schema (doc 06 §6), so EF projections are rebuilt
    /// blue/green: the context must implement <see cref="IGenerationSchemaContext"/> and map its tables with
    /// <c>HasDefaultSchema(Schema)</c>. A generation's tables are created from the EF model on first use, and a retired
    /// generation's schema is dropped after <c>RetainPreviousFor</c>.
    /// </summary>
    /// <param name="configure">Selects the provider on a connection, for example <c>(o, c) =&gt; o.UseNpgsql(c)</c>.</param>
    /// <param name="create">Creates the context for a schema, for example <c>(o, schema) =&gt; new ReportingDbContext(o, schema)</c>.</param>
    /// <param name="generations">Schema naming and DDL; the defaults suit PostgreSQL.</param>
    public static EfSessionContextFactory<TDbContext> PerGeneration(
        Action<DbContextOptionsBuilder<TDbContext>, DbConnection> configure,
        Func<DbContextOptions<TDbContext>, string, TDbContext> create,
        EfGenerationOptions? generations = null)
    {
        return new EfSessionContextFactory<TDbContext>(configure, create, generations ?? new EfGenerationOptions());
    }

    /// <summary>True when each generation has its own schema.</summary>
    public bool SchemaPerGeneration => _generations is not null;

    /// <inheritdoc />
    bool IProjectionGenerationStore.SeparatesGenerations => SchemaPerGeneration;

    /// <summary>The schema of <paramref name="generation"/> (schema-per-generation factories only).</summary>
    public string SchemaOf(ReadModelGeneration generation)
    {
        return (_generations ?? throw new InvalidOperationException("This factory does not keep a schema per generation; create it with PerGeneration."))
            .Schema(generation);
    }

    /// <summary>
    /// Creates a context over <paramref name="connection"/> for reading <paramref name="generation"/> (resolve the serving
    /// generation with <c>IReadModelResolver</c>). The caller owns and disposes the context.
    /// </summary>
    public TDbContext CreateForGeneration(DbConnection connection, ReadModelGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Build(connection, SchemaOf(generation));
    }

    /// <summary>
    /// Creates a context that uses the session's connection and transaction (doc 05 §6). The context does not own the
    /// connection; dispose the context before the session.
    /// </summary>
    /// <exception cref="InvalidOperationException">The store has no connection (the in-memory store).</exception>
    public async ValueTask<TDbContext> CreateAsync(IStorageSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await CreateAsync(session, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a context for <paramref name="generation"/> on the session's connection and transaction; with a
    /// schema-per-generation factory the generation's schema and tables are created when missing.
    /// </summary>
    public async ValueTask<TDbContext> CreateAsync(IStorageSession session, ReadModelGeneration? generation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        DbConnection connection = session.Connection
            ?? throw new InvalidOperationException("EF Core projections need a relational store: this storage session has no connection (the in-memory store). Use UsePostgreSql or another relational adapter, or a document projection.");
        string? schema = null;
        if (_generations is not null)
        {
            schema = _generations.Schema(generation ?? throw new InvalidOperationException($"{typeof(TDbContext).Name} keeps a schema per generation, but no generation is being written."));
        }

        TDbContext context = Build(connection, schema);
        try
        {
            await context.Database.UseTransactionAsync(session.Transaction, cancellationToken).ConfigureAwait(false);
            // Only a schema seen committed is cached: one created here rolls back with a failed batch.
            if (schema is not null && !_created.ContainsKey(schema)
                && await EnsureSchemaAsync(context, connection, session.Transaction, schema, cancellationToken).ConfigureAwait(false))
            {
                _created[schema] = true;
            }

            return context;
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    ValueTask<TDbContext> IProjectionUnitOfWork<TDbContext>.BeginAsync(IStorageSession session, IServiceProvider services, CancellationToken cancellationToken)
    {
        return CreateAsync(session, services.GetService<ProjectionTarget>()?.Generation, cancellationToken);
    }

    private TDbContext Build(DbConnection connection, string? schema)
    {
        var options = new DbContextOptionsBuilder<TDbContext>();
        _configure(options, connection);
        if (schema is null || _createForSchema is null)
        {
            return _create(options.Options);
        }

        options.ReplaceService<IModelCacheKeyFactory, GenerationModelCacheKeyFactory>();
        TDbContext context = _createForSchema(options.Options, schema);
        if (context is not IGenerationSchemaContext mapped || mapped.Schema != schema)
        {
            context.Dispose();
            throw new InvalidOperationException($"{typeof(TDbContext).Name} must implement IGenerationSchemaContext and map its tables to the schema it is created with (HasDefaultSchema(Schema)).");
        }

        return context;
    }

    // Creates the generation's schema and, when it has no tables yet, EF's own create script for the model (no user input).
    // Returns true when the tables already existed.
    private async Task<bool> EnsureSchemaAsync(TDbContext context, DbConnection connection, DbTransaction? transaction, string schema, CancellationToken cancellationToken)
    {
        await using DbCommand exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema";
        DbParameter parameter = exists.CreateParameter();
        parameter.ParameterName = "schema";
        parameter.Value = schema;
        exists.Parameters.Add(parameter);
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
        {
            return true;
        }

        await using DbCommand create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = _generations!.CreateSchemaSql(schema) + ";\n" + context.Database.GenerateCreateScript();
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <inheritdoc />
    async ValueTask IProjectionGenerationStore.DropGenerationAsync(ReadModelGeneration generation, IStorageSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        string schema = SchemaOf(generation);
        DbConnection connection = session.Connection ?? throw new InvalidOperationException("EF Core projections need a relational store.");
        await using DbCommand drop = connection.CreateCommand();
        drop.Transaction = session.Transaction;
        drop.CommandText = _generations!.DropSchemaSql(schema);
        await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _created.TryRemove(schema, out _);
    }

    /// <inheritdoc />
    async ValueTask IProjectionUnitOfWork<TDbContext>.CompleteAsync(TDbContext unitOfWork, CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Registers EF Core projections on the SourceFlow builder.</summary>
public static class EfProjectionBuilderExtensions
{
    /// <summary>
    /// Lets <see cref="IEfProjection{TDbContext}"/> projections of <typeparamref name="TDbContext"/> run, on the storage
    /// session's connection and transaction. The projections themselves are registered by their module and configured
    /// inline or async with <c>Projections(p =&gt; p.Add&lt;T&gt;())</c> like any projection.
    /// </summary>
    /// <param name="builder">The SourceFlow builder.</param>
    /// <param name="configure">Selects the provider on the session's connection, for example <c>(o, c) =&gt; o.UseNpgsql(c)</c>.</param>
    /// <param name="create">Creates the context, for example <c>o =&gt; new ReportingDbContext(o)</c>.</param>
    public static SourceFlowBuilder UseEntityFrameworkProjections<TDbContext>(
        this SourceFlowBuilder builder,
        Action<DbContextOptionsBuilder<TDbContext>, DbConnection> configure,
        Func<DbContextOptions<TDbContext>, TDbContext> create)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseEntityFrameworkProjections(new EfSessionContextFactory<TDbContext>(configure, create));
    }

    /// <summary>
    /// Lets <see cref="IEfProjection{TDbContext}"/> projections of <typeparamref name="TDbContext"/> run with a schema per
    /// read-model generation, so a version bump or <c>dotnet sourceflow projection rebuild</c> builds the new generation
    /// beside the serving one and switches reads when it has caught up (doc 06 §6). The context implements
    /// <see cref="IGenerationSchemaContext"/>.
    /// </summary>
    /// <param name="builder">The SourceFlow builder.</param>
    /// <param name="configure">Selects the provider on the session's connection, for example <c>(o, c) =&gt; o.UseNpgsql(c)</c>.</param>
    /// <param name="create">Creates the context for a schema, for example <c>(o, schema) =&gt; new ReportingDbContext(o, schema)</c>.</param>
    /// <param name="generations">Schema naming and DDL; the defaults suit PostgreSQL.</param>
    public static SourceFlowBuilder UseEntityFrameworkProjections<TDbContext>(
        this SourceFlowBuilder builder,
        Action<DbContextOptionsBuilder<TDbContext>, DbConnection> configure,
        Func<DbContextOptions<TDbContext>, string, TDbContext> create,
        EfGenerationOptions? generations = null)
        where TDbContext : DbContext
    {
        return builder.UseEntityFrameworkProjections(EfSessionContextFactory<TDbContext>.PerGeneration(configure, create, generations));
    }

    private static SourceFlowBuilder UseEntityFrameworkProjections<TDbContext>(this SourceFlowBuilder builder, EfSessionContextFactory<TDbContext> factory)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton(factory);
        builder.Services.TryAddSingleton<IProjectionUnitOfWork<TDbContext>>(factory);
        return builder;
    }
}

/// <summary>A read-model row that records the source stream version it reflects (doc 06 §5).</summary>
public interface IVersionedReadModel
{
    /// <summary>The source stream version (or write count) the row reflects.</summary>
    long Version { get; set; }
}

/// <summary>Version-guarded upserts for EF Core read models (WP-2.7).</summary>
public static class IdempotentUpsert
{
    /// <summary>
    /// Applies a change only when <paramref name="version"/> is newer than the stored row, so replays and rebuilds are
    /// harmless. <paramref name="apply"/> receives the tracked row (null when absent) and returns the row to store; the
    /// row's <see cref="IVersionedReadModel.Version"/> is set to <paramref name="version"/>.
    /// </summary>
    /// <returns>True when the change was applied; false when the stored row is as new or newer.</returns>
    public static async ValueTask<bool> UpsertAsync<TEntity>(this DbSet<TEntity> set, object key, long version, Func<TEntity?, TEntity> apply, CancellationToken cancellationToken)
        where TEntity : class, IVersionedReadModel
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(apply);
        TEntity? existing = await set.FindAsync([key], cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Version >= version)
        {
            return false;
        }

        TEntity next = apply(existing);
        next.Version = version;
        if (existing is null)
        {
            set.Add(next);
        }
        else if (!ReferenceEquals(existing, next))
        {
            set.Entry(existing).CurrentValues.SetValues(next);
        }

        return true;
    }
}
