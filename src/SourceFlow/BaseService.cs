using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SourceFlow
{
    /// <summary>
    /// Base class for services in the event-driven architecture.
    /// </summary>
    public abstract class BaseService : IService
    {
        /// <summary>
        /// Factory for creating aggregate roots.
        /// </summary>
        protected IAggregateFactory aggregateFactory;

        /// <summary>
        /// Logger for the service to log events and errors.
        /// </summary>
        protected ILogger logger;

        /// <summary>
        /// Creates an initialised aggregate root.
        /// </summary>
        /// <typeparam name="TAggregateRoot"></typeparam>
        /// <returns>Implementation of IAggregateRoot</returns>
        public async Task<TAggregateRoot> CreateAggregate<TAggregateRoot>() where TAggregateRoot : IAggregateRoot
        {
            var aggregate = await aggregateFactory.Create<TAggregateRoot>();
            return aggregate;
        }
    }
}