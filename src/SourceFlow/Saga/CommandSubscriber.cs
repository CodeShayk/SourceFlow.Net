using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Commands.Impl;

namespace SourceFlow.Saga
{
    /// <summary>
    /// This dispatcher is responsible for dispatching commands to registered sagas in an event-driven architecture.
    /// </summary>
    internal class CommandSubscriber : ICommandSubscriber
    {
        /// <summary>
        /// Collection of sagas registered with the dispatcher.
        /// </summary>
        private readonly IEnumerable<ISaga> sagas;

        /// <summary>
        /// Logger for the command dispatcher to log events and errors.
        /// </summary>
        private readonly ILogger<ICommandSubscriber> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandSubscriber"/> class with the specified logger.
        /// </summary>
        /// <param name="logger"></param>
        public CommandSubscriber(IEnumerable<ISaga> sagas, ILogger<ICommandSubscriber> logger)
        {
            this.logger = logger;
            this.sagas = sagas;
        }

             /// <summary>
        /// Publishes a command to all sagas that are registered with the command dispatcher.
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        public async Task Subscribe<TCommand>(TCommand command) where TCommand : ICommand
        {
            if (!sagas.Any())
            {
                logger?.LogInformation("Action=Command_Dispatcher, Command={Command}, Payload={Payload}, SequenceNo={No}, Message=No Sagas Found",
                command.GetType().Name, command.Payload.GetType().Name, ((IMetadata)command).Metadata.SequenceNo);

                return;
            }

            var tasks = new List<Task>();
            foreach (var saga in sagas)
            {
                if (saga == null || !Saga<IEntity>.CanHandle(saga, command.GetType()))
                    continue;

                tasks.Add(Send(saga, command));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Handles the command in the saga and appends it to the event store if not replayed.
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="saga"></param>
        /// <param name="event"></param>
        /// <returns></returns>
        private async Task Send<TCommand>(ISaga saga, TCommand command) where TCommand : ICommand
        {
            // 4. Log event.
            logger?.LogInformation("Action=Command_Dispatcher_Send, Command={Command}, Payload={Payload}, SequenceNo={No}, Saga={Saga}",
                command.GetType().Name, command.Payload.GetType().Name, ((IMetadata)command).Metadata.SequenceNo, saga.GetType().Name);

            // 2. handle event by Saga?
            await saga.Handle(command);
        }

        ///// <summary>
        ///// Registers a saga with the dispatcher.
        ///// </summary>
        ///// <param name="saga"></param>
        //public void Register(ISaga saga)
        //{
        //    sagas.Add(saga);
        //}
    }
}