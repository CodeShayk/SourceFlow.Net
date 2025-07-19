using System;
using System.Threading.Tasks;
using SourceFlow.Messaging.Bus;

namespace SourceFlow.Impl
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
        /// Publishes a command to the command bus.
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="command"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        async Task ICommandPublisher.Publish<TCommand>(TCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (command.Payload?.Id == null)
                throw new InvalidOperationException(nameof(command) + "requires payload entity with id");

            await commandBus.Publish(command);
        }
    }
}