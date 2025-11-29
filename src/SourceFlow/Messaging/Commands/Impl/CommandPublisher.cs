using System;
using System.Threading.Tasks;
using SourceFlow.Messaging.Bus;

namespace SourceFlow.Messaging.Commands.Impl
{
    /// <summary>
    /// Implementation of the ICommandPublisher interface for publishing commands to bus.
    /// </summary>
    internal class CommandPublisher : ICommandPublisher
    {
        /// <summary>
        /// The command bus used to publish commands.
        /// </summary>
        private readonly ICommandBus commandBus;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandPublisher"/> class.
        /// </summary>
        /// <param name="commandBus"></param>
        public CommandPublisher(ICommandBus commandBus)
        {
            this.commandBus = commandBus;
        }

        /// <summary>
        /// Replays commands for a specific entity by its Id.
        /// </summary>
        /// <param name="entityId"></param>
        /// <returns></returns>
        public Task ReplayCommands(int entityId)
        {
            return commandBus.Replay(entityId);
        }

        /// <summary>
        /// Publishes a command to the command bus.
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="command"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        Task ICommandPublisher.Publish<TCommand>(TCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (command.Entity == null)
                throw new InvalidOperationException(nameof(command) + " requires entity reference.");

            if (!command.Entity.IsNew && command.Entity?.Id == null)
                throw new InvalidOperationException(nameof(command) + " requires entity id when not new entity.");

            return commandBus.Publish(command);
        }
    }
}
