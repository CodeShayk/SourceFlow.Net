using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Base class for aggregate roots in the event-driven architecture.
    /// </summary>
    /// <typeparam name="TAggregate"></typeparam>
    public abstract class BaseAggregate<TAggregate> : IAggregateRoot
        where TAggregate : class, IIdentity, new()
    {
        public TAggregate State { get; protected set; }
        IIdentity IAggregateRoot.State { get { return State; } set { State = (TAggregate)value; } }

        /// <summary>
        /// The bus publisher used to publish events.
        /// </summary>
        protected IBusPublisher busPublisher;

        /// <summary>
        /// The events replayer used to replay event stream for given aggregate.
        /// </summary>
        protected IEventReplayer eventReplayer;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseAggregate{TAggregate}"/> class.
        /// </summary>
        protected BaseAggregate()
        {
            State = new TAggregate();
        }

        /// <summary>
        /// Applies an event to the aggregate root, updating its state accordingly.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        public abstract Task ApplyAsync(IEvent @event);

        /// <summary>
        /// Replays the event stream for the aggregate root, restoring its state from past events.
        /// </summary>
        /// <returns></returns>
        public Task ReplayEvents()
        {
            return eventReplayer.ReplayEventsAsync(State.Id);
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

            return busPublisher.PublishAsync(@event);
        }
    }
}