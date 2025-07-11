using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SourceFlow
{
    /// <summary>
    /// Base class for aggregate roots in the event-driven architecture.
    /// </summary>
    /// <typeparam name="TAggregate"></typeparam>
    public abstract class BaseAggregate<TAggregate> : IAggregateRoot
        where TAggregate : class, IEntity
    {
        /// <summary>
        /// The bus publisher used to publish events.
        /// </summary>
        protected IBusPublisher busPublisher;

        /// <summary>
        /// The events replayer used to replay event stream for given aggregate.
        /// </summary>
        protected IEventReplayer eventReplayer;

        /// <summary>
        /// Logger for the aggregate root to log events and errors.
        /// </summary>
        protected ILogger logger;

        /// <summary>
        /// Replays the event stream for the aggregate root, restoring its state from past events.
        /// </summary>
        /// <param name="AggregateId">Unique Aggregate entity identifier.</param>
        /// <returns></returns>
        public Task ReplayEvents(int AggregateId)
        {
            return eventReplayer.ReplayEvents(AggregateId);
        }

        /// <summary>
        /// Publishes an event to all subscribers, allowing the event to be processed by other components in the system.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        protected Task PublishAsync(IEvent @event)
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            return busPublisher.Publish(@event);
        }
    }
}