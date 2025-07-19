using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System;
using Microsoft.Extensions.Logging;
using SourceFlow.Aggregate;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;
using SourceFlow.Saga;

namespace SourceFlow.Impl
{
    /// <summary>
    /// Command bus implementation that handles commands and events in an event-driven architecture.
    /// </summary>
    internal class CommandBus : ICommandBus
    {
        /// <summary>
        /// The event store used to persist events.
        /// </summary>
        private readonly IEventStore eventStore;

        /// <summary>
        /// Logger for the command bus to log events and errors.
        /// </summary>
        private readonly ILogger<ICommandBus> logger;

        /// <summary>
        /// Collection of sagas registered with the command bus.
        /// </summary>
        private readonly ICollection<ISaga> sagas;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandBus"/> class.
        /// </summary>
        /// <param name="eventStore"></param>
        /// <param name="logger"></param>
        public CommandBus(IEventStore eventStore, ILogger<ICommandBus> logger)
        {
            this.eventStore = eventStore;
            this.logger = logger;
            sagas = new List<ISaga>();
        }

        /// <summary>
        /// Publishes a command to all subscribed sagas.
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        async Task ICommandBus.Publish<TCommand>(TCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            await PublishToSagas(command);
        }

        /// <summary>
        /// Publishes a command to all sagas that are registered with the command bus.
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        private async Task PublishToSagas<TCommand>(TCommand command) where TCommand : ICommand
        {
            if (!sagas.Any())
                return;

            var tasks = new List<Task>();
            foreach (var saga in sagas)
            {
                if (saga == null || !BaseSaga<IEntity>.CanHandle(saga, command.GetType()))
                    continue;

                tasks.Add(SagaHandle(saga, command));
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
        private async Task SagaHandle<TCommand>(ISaga saga, TCommand command) where TCommand : ICommand
        {
            // 1. Set event sequence no.
            if (!command.IsReplay)
                command.SequenceNo = await eventStore.GetNextSequenceNo(command.Payload.Id);

            // 4. Log event.
            logger?.LogInformation("Action=Command_Dispatched, Command={Command}, Payload={Payload}, SequenceNo={No}, Saga={Saga}",
                command.GetType().Name, command.Payload.GetType().Name, command.SequenceNo, saga.GetType().Name);

            // 2. handle event by Saga?
            await saga.Handle(command);

            // 3. When event is not replayed
            if (!command.IsReplay)
                // 3.1. Append event to event store.
                await eventStore.Append(command);
        }

        /// <summary>
        /// Replays commands for a given aggregate.
        /// </summary>
        /// <param name="aggregateId">Unique aggregate entity id.</param>
        /// <returns></returns>
        async Task ICommandBus.Replay(int aggregateId)
        {
            var commands = await eventStore.Load(aggregateId);

            if (commands == null || !commands.Any())
                return;

            foreach (var command in commands.ToList())
            {
                command.IsReplay = true;
                await PublishToSagas(command);
            }
        }

        /// <summary>
        /// Registers a saga with the command bus.
        /// </summary>
        /// <param name="saga"></param>
        void ICommandBus.RegisterSaga(ISaga saga)
        {
            sagas.Add(saga);
        }
    }
}