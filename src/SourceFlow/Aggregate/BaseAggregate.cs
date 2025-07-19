using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;

namespace SourceFlow.Aggregate
{
    /// <summary>
    /// Base class for aggregate roots in the event-driven architecture.
    /// </summary>
    /// <typeparam name="TAggregateEntity"></typeparam>
    public abstract class BaseAggregate<TAggregateEntity> : IAggregateRoot
        where TAggregateEntity : class, IEntity
    {
        /// <summary>
        /// The command publisher used to publish commands.
        /// </summary>
        protected ICommandPublisher commandPublisher;

        /// <summary>
        /// The events replayer used to replay event stream for given aggregate.
        /// </summary>
        protected ICommandReplayer commandReplayer;

        /// <summary>
        /// Logger for the aggregate root to log events and errors.
        /// </summary>
        protected ILogger logger;

        /// <summary>
        /// Replays the event stream for the aggregate root, restoring its state from past events.
        /// </summary>
        /// <param name="AggregateId">Unique Aggregate entity identifier.</param>
        /// <returns></returns>
        public Task Replay(int AggregateId)
        {
            return commandReplayer.Replay(AggregateId);
        }

        /// <summary>
        /// Sends a command to command bus, allowing the command to be processed by subscribing sagas in the system.
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        protected Task Send(ICommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (command.Payload?.Id == null)
                throw new InvalidOperationException(nameof(command) + "requires Payload");

            return commandPublisher.Publish(command);
        }
    }
}