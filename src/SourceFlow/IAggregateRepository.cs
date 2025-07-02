using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for the aggregate repository in the event-driven architecture.
    /// </summary>
    public interface IAggregateRepository
    {
        /// <summary>
        /// Retrieves an aggregate root by its identifier.
        /// </summary>
        /// <param name="aggregateRoot"></param>
        /// <returns></returns>
        Task<IAggregateRoot> GetByIdAsync(AggregateReference aggregateRoot);

        /// <summary>
        /// Saves an aggregate root to the repository, persisting its state.
        /// </summary>
        /// <param name="aggregateRoot"></param>
        /// <returns></returns>
        Task SaveAsync(IAggregateRoot aggregateRoot);
    }
}