using System.Data.Common;
using Microsoft.EntityFrameworkCore;
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

/// <summary>Creates <see cref="DbContext"/> instances on a storage session's connection and transaction.</summary>
/// <typeparam name="TDbContext">The context type.</typeparam>
public sealed class EfSessionContextFactory<TDbContext> : IProjectionUnitOfWork<TDbContext>
    where TDbContext : DbContext
{
    private readonly Action<DbContextOptionsBuilder<TDbContext>, DbConnection> _configure;
    private readonly Func<DbContextOptions<TDbContext>, TDbContext> _create;

    /// <summary>Creates the factory.</summary>
    /// <param name="configure">Selects the provider on the session's connection, for example <c>(o, c) =&gt; o.UseNpgsql(c)</c>.</param>
    /// <param name="create">Creates the context, for example <c>o =&gt; new ReportingDbContext(o)</c>.</param>
    public EfSessionContextFactory(Action<DbContextOptionsBuilder<TDbContext>, DbConnection> configure, Func<DbContextOptions<TDbContext>, TDbContext> create)
    {
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
        _create = create ?? throw new ArgumentNullException(nameof(create));
    }

    /// <summary>
    /// Creates a context that uses the session's connection and transaction (doc 05 §6). The context does not own the
    /// connection; dispose the context before the session.
    /// </summary>
    /// <exception cref="InvalidOperationException">The store has no connection (the in-memory store).</exception>
    public async ValueTask<TDbContext> CreateAsync(IStorageSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        DbConnection connection = session.Connection
            ?? throw new InvalidOperationException("EF Core projections need a relational store: this storage session has no connection (the in-memory store). Use UsePostgreSql or another relational adapter, or a document projection.");
        var options = new DbContextOptionsBuilder<TDbContext>();
        _configure(options, connection);
        TDbContext context = _create(options.Options);
        try
        {
            await context.Database.UseTransactionAsync(session.Transaction, cancellationToken).ConfigureAwait(false);
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
        return CreateAsync(session, cancellationToken);
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
        var factory = new EfSessionContextFactory<TDbContext>(configure, create);
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
