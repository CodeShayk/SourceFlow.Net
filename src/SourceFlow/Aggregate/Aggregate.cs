using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Aggregate
{
    /// <summary>
    /// Base class for aggregate roots in the event-driven architecture.
    /// </summary>
    /// <typeparam name="TEntity">Aggregate Entity type</typeparam>
    public abstract class Aggregate<TEntity> : IAggregate
        where TEntity : class, IEntity
    {
        /// <summary>
        /// The command publisher used to publish commands.
        /// </summary>
        protected ICommandPublisher commandPublisher;

        /// <summary>
        /// Logger for the aggregate root to log events and errors.
        /// </summary>
        protected ILogger<IAggregate> logger;

        protected Aggregate(ICommandPublisher commandPublisher, ILogger<IAggregate> logger)
        {
            this.commandPublisher = commandPublisher;
            this.logger = logger;
        }

        /// <summary>
        /// Replays the command stream for the aggregate root, restoring its state from past history.
        /// </summary>
        /// <param name="entityId">Unique Aggregate entity identifier.</param>
        /// <returns></returns>
        public Task ReplayCommands(int entityId)
        {
            return commandPublisher.ReplayCommands(entityId);
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

            return commandPublisher.Publish(command);
        }
    }
}