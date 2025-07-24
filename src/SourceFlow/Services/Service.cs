using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Aggregate;

namespace SourceFlow.Services
{
    /// <summary>
    /// Base class for services in the event-driven architecture.
    /// </summary>
    public abstract class Service : IService
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
        /// <typeparam name="TAggregate"></typeparam>
        /// <returns>Implementation of IAggregate</returns>
        public async Task<TAggregate> CreateAggregate<TAggregate>() where TAggregate : class, IAggregate
        {
            var aggregate = await aggregateFactory.Create<TAggregate>();
            return aggregate;
        }
    }
}