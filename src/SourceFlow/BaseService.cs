using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Base class for services in the event-driven architecture.
    /// </summary>
    public abstract class BaseService : IService
    {
        /// <summary>
        /// Repository for managing aggregate roots.
        /// </summary>
        protected IAggregateRepository aggregateRepository;

        /// <summary>
        /// Factory for creating aggregate roots.
        /// </summary>
        protected IAggregateFactory aggregateFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseService"/> class.
        /// </summary>
        public BaseService()
        {
        }

        /// <summary>
        /// Retrieves an aggregate root by its identifier.
        /// </summary>
        /// <typeparam name="TAggregateRoot"></typeparam>
        /// <param name="id"></param>
        /// <returns></returns>
        public async Task<TAggregateRoot> GetAggregate<TAggregateRoot>(Guid id) where TAggregateRoot : IAggregateRoot
        {
            var aggregateRoot = await aggregateRepository.GetByIdAsync(new AggregateReference(id, typeof(TAggregateRoot)));
            return (TAggregateRoot)aggregateRoot;
        }

        /// <summary>
        /// Saves an aggregate root to the repository, persisting its state.
        /// </summary>
        /// <typeparam name="TAggregateRoot"></typeparam>
        /// <param name="aggregateRoot"></param>
        /// <returns></returns>
        public Task SaveAggregate<TAggregateRoot>(TAggregateRoot aggregateRoot) where TAggregateRoot : IAggregateRoot
        {
            return aggregateRepository.SaveAsync(aggregateRoot);
        }

        /// <summary>
        /// Creates a new aggregate root with the specified state.
        /// </summary>
        /// <typeparam name="TAggregateRoot"></typeparam>
        /// <param name="state"></param>
        /// <returns></returns>
        public async Task<TAggregateRoot> CreateAggregate<TAggregateRoot>(IIdentity state = null) where TAggregateRoot : IAggregateRoot
        {
            var aggregate = await aggregateFactory.CreateAsync<TAggregateRoot>(state);
            aggregate.State = state;
            return aggregate;
        }
    }
}