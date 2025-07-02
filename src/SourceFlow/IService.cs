using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for the service layer in the event-driven architecture.
    /// </summary>
    public interface IService
    {
        /// <summary>
        /// Creates a new aggregate root instance with the specified state.
        /// </summary>
        /// <typeparam name="TAggregateRoot"></typeparam>
        /// <param name="state"></param>
        /// <returns></returns>
        Task<TAggregateRoot> CreateAggregate<TAggregateRoot>(IIdentity state = null) where TAggregateRoot : IAggregateRoot;

        /// <summary>
        /// Retrieves an existing aggregate from repository by its identifier.
        /// </summary>
        /// <typeparam name="TAggregateRoot"></typeparam>
        /// <param name="id"></param>
        /// <returns></returns>
        Task<TAggregateRoot> GetAggregate<TAggregateRoot>(Guid id) where TAggregateRoot : IAggregateRoot;

        /// <summary>
        /// Saves the specified aggregate root to the repository.
        /// </summary>
        /// <typeparam name="TAggregateRoot"></typeparam>
        /// <param name="aggregateRoot"></param>
        /// <returns></returns>
        Task SaveAggregate<TAggregateRoot>(TAggregateRoot aggregateRoot) where TAggregateRoot : IAggregateRoot;
    }
}