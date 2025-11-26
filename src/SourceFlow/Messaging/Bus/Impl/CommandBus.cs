using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging.Bus;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;

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
        /// Telemetry service for observability.
        /// </summary>
        private readonly IDomainTelemetryService telemetry;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandBus"/> class.
        /// </summary>
        /// <param name="commandDispatcher"></param>
        /// <param name="commandStore"></param>
        /// <param name="logger"></param>
        /// <param name="telemetry"></param>
        public CommandBus(ICommandDispatcher commandDispatcher, ICommandStoreAdapter commandStore, ILogger<ICommandBus> logger, IDomainTelemetryService telemetry)
        {
            this.commandStore = commandStore ?? throw new ArgumentNullException(nameof(commandStore));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.commandDispatcher = commandDispatcher ?? throw new ArgumentNullException(nameof(commandDispatcher));
            this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
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
            await telemetry.TraceAsync(
                "sourceflow.commandbus.dispatch",
                async () =>
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
                },
                activity =>
                {
                    activity?.SetTag("command.type", command.GetType().Name);
                    activity?.SetTag("command.entity_id", command.Entity.Id);
                    activity?.SetTag("command.sequence_no", ((IMetadata)command).Metadata.SequenceNo);
                    activity?.SetTag("command.is_replay", ((IMetadata)command).Metadata.IsReplay);
                });

            // Record metric
            telemetry.RecordCommandExecuted(command.GetType().Name, command.Entity.Id);
        }

        /// <summary>
        /// Replays commands for a given aggregate.
        /// </summary>
        /// <param name="entityId">Unique aggregate entity id.</param>
        /// <returns></returns>
        async Task ICommandBus.Replay(int entityId)
        {
            await telemetry.TraceAsync(
                "sourceflow.commandbus.replay",
                async () =>
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
                },
                activity =>
                {
                    activity?.SetTag("entity_id", entityId);
                });
        }
    }
}