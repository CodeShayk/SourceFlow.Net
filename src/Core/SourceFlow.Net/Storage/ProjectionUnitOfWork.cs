namespace SourceFlow.Storage;

/// <summary>
/// Creates and completes the unit of work of session projections (<see cref="ISessionProjection{TUnitOfWork}"/>), bound to
/// the storage session's connection and transaction. Storage packages register one per unit-of-work type, for example
/// <c>SourceFlow.Storage.EntityFrameworkCore</c> for a <c>DbContext</c>.
/// </summary>
/// <typeparam name="TUnitOfWork">The unit of work type.</typeparam>
public interface IProjectionUnitOfWork<TUnitOfWork>
    where TUnitOfWork : class
{
    /// <summary>Creates a unit of work on the session's connection and transaction; one per session.</summary>
    /// <param name="session">The storage session; its transaction must be used.</param>
    /// <param name="services">The per-batch DI scope.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask<TUnitOfWork> BeginAsync(IStorageSession session, IServiceProvider services, CancellationToken cancellationToken);

    /// <summary>Flushes the unit of work (for example <c>SaveChangesAsync</c>) before the session commits.</summary>
    ValueTask CompleteAsync(TUnitOfWork unitOfWork, CancellationToken cancellationToken);
}
