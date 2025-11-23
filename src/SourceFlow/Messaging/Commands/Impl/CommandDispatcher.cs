using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Messaging.Commands.Impl
{
    /// <summary>
    /// This dispatcher is responsible for dispatching commands to registered sagas in an event-driven architecture.
    /// </summary>
    internal class CommandDispatcher : ICommandDispatcher
    {
        /// <summary>
        /// Collection of sagas registered with the dispatcher.
        /// </summary>
        private readonly IEnumerable<ICommandSubscriber> commandSubscribers;

        /// <summary>
        /// Logger for the command dispatcher to log events and errors.
        /// </summary>
        private readonly ILogger<ICommandDispatcher> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandDispatcher"/> class with the specified logger.
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="commandSubscribers"></param>
        public CommandDispatcher(IEnumerable<ICommandSubscriber> commandSubscribers, ILogger<ICommandDispatcher> logger)
        {
            this.logger = logger;
            this.commandSubscribers = commandSubscribers;
        }

        /// <summary>
        /// Dispatches a command to all sagas that are registered with the command dispatcher.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="command"></param>
        public Task Dispatch<TCommand>(TCommand command) where TCommand : ICommand
        {
            return Send(command);
        }

        /// <summary>
        /// Publishes a command to all sagas that are registered with the command dispatcher.
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        private Task Send<TCommand>(TCommand command) where TCommand : ICommand
        {
            if (!commandSubscribers.Any())
            {
                logger?.LogInformation("Action=Command_Dispatcher, Command={Command}, Payload={Payload}, SequenceNo={No}, Message=No subscribers Found",
                command.GetType().Name, command.Payload.GetType().Name, ((IMetadata)command).Metadata.SequenceNo);

                return Task.CompletedTask;
            }

            var tasks = new List<Task>();

            foreach (var subscriber in commandSubscribers)
                tasks.Add(subscriber.Subscribe(command));
            

            return Task.WhenAll(tasks);
        }       
    }
}