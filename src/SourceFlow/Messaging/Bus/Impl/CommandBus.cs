using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging.Bus;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Messaging.Bus.Impl
{
    /// <summary>
    /// Command bus implementation that handles commands and events in an event-driven architecture.
    /// </summary>
    internal class CommandBus : ICommandBus
    {
        /// <summary>
        /// The command store used to persist commands.
        /// </summary>
        private readonly ICommandStoreAdapter commandStore;

        /// <summary>
        /// Logger for the command bus to log events and errors.
        /// </summary>
        private readonly ILogger<ICommandBus> logger;

        /// <summary>
        /// Represents command dispathers that can handle the publishing of commands.
        /// </summary>
        public ICommandDispatcher commandDispatcher;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandBus"/> class.
        /// </summary>
        /// <param name="commandStore"></param>
        /// <param name="logger"></param>
        public CommandBus(ICommandDispatcher commandDispatcher, ICommandStoreAdapter commandStore, ILogger<ICommandBus> logger)
        {
            this.commandStore = commandStore ?? throw new ArgumentNullException(nameof(commandStore));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));            
            this.commandDispatcher = commandDispatcher ?? throw new ArgumentNullException(nameof(commandDispatcher));
        }

        /// <summary>
        /// Publishes a command to all subscribed sagas.
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        Task ICommandBus.Publish<TCommand>(TCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            return Dispatch(command);
        }

        private async Task Dispatch<TCommand>(TCommand command) where TCommand : ICommand
        {
            // 1. Set event sequence no.
            if (!((IMetadata)command).Metadata.IsReplay)
                ((IMetadata)command).Metadata.SequenceNo = await commandStore.GetNextSequenceNo(command.Entity.Id);

            // 2. Dispatch command to handlers.
            await commandDispatcher.Dispatch(command);

            // 3. Log event.
            logger?.LogInformation("Action=Command_Dispatched, Command={Command}, Payload={Payload}, SequenceNo={No}",
                command.GetType().Name, command.Payload.GetType().Name, ((IMetadata)command).Metadata.SequenceNo);

            // 4. When event is not replayed
            if (!((IMetadata)command).Metadata.IsReplay)
                // 4.1. Append event to event store.
                await commandStore.Append(command);
        }

        /// <summary>
        /// Replays commands for a given aggregate.
        /// </summary>
        /// <param name="entityId">Unique aggregate entity id.</param>
        /// <returns></returns>
        async Task ICommandBus.Replay(int entityId)
        {
            var commands = await commandStore.Load(entityId);

            if (commands == null || !commands.Any())
                return;

            foreach (var command in commands.ToList())
            {
                command.Metadata.IsReplay = true;

                // Call Dispatch with the concrete command type to preserve generics
                var commandType = command.GetType();
                var dispatchMethod = this.GetType().GetMethod(nameof(Dispatch),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var genericDispatchMethod = dispatchMethod.MakeGenericMethod(commandType);
                await (Task)genericDispatchMethod.Invoke(this, new object[] { command });
            }
        }
    }
}