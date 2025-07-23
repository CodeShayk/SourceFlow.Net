using System.Threading.Tasks;
using System.Linq;
using System;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;

namespace SourceFlow.Impl
{
    /// <summary>
    /// Command bus implementation that handles commands and events in an event-driven architecture.
    /// </summary>
    internal class CommandBus : ICommandBus
    {
        /// <summary>
        /// The command store used to persist commands.
        /// </summary>
        private readonly ICommandStore commandStore;

        /// <summary>
        /// Logger for the command bus to log events and errors.
        /// </summary>
        private readonly ILogger<ICommandBus> logger;

        /// <summary>
        /// Represents command dispathers that can handle the publishing of commands.
        /// </summary>
        public event EventHandler<ICommand> Dispatchers;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandBus"/> class.
        /// </summary>
        /// <param name="commandStore"></param>
        /// <param name="logger"></param>
        public CommandBus(ICommandStore commandStore, ILogger<ICommandBus> logger)
        {
            this.commandStore = commandStore;
            this.logger = logger;
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

            await Dispatch(command);
        }

        private async Task Dispatch<TCommand>(TCommand command) where TCommand : ICommand
        {
            // 1. Set event sequence no.
            if (!((IMetadata)command).Metadata.IsReplay)
                ((IMetadata)command).Metadata.SequenceNo = await commandStore.GetNextSequenceNo(command.Payload.Id);

            // 2. Dispatch command to handlers.
            Dispatchers?.Invoke(this, command);

            // 3. Log event.
            logger?.LogInformation("Action=Command_Dispatched, Command={Command}, Payload={Payload}, SequenceNo={No}, Saga={Saga}",
                command.GetType().Name, command.Payload.GetType().Name, ((IMetadata)command).Metadata.SequenceNo);

            // 4. When event is not replayed
            if (!((IMetadata)command).Metadata.IsReplay)
                // 4.1. Append event to event store.
                await commandStore.Append(command);
        }

        /// <summary>
        /// Replays commands for a given aggregate.
        /// </summary>
        /// <param name="aggregateId">Unique aggregate entity id.</param>
        /// <returns></returns>
        async Task ICommandBus.Replay(int aggregateId)
        {
            var commands = await commandStore.Load(aggregateId);

            if (commands == null || !commands.Any())
                return;

            foreach (var command in commands.ToList())
            {
                ((IMetadata)command).Metadata.IsReplay = true;
                await Dispatch(command);
            }
        }
    }
}