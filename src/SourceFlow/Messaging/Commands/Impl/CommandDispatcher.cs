using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Observability;
using SourceFlow.Performance;

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
        /// Telemetry service for observability.
        /// </summary>
        private readonly IDomainTelemetryService telemetry;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandDispatcher"/> class with the specified logger.
        /// </summary>
        /// <param name="commandSubscribers"></param>
        /// <param name="logger"></param>
        /// <param name="telemetry"></param>
        public CommandDispatcher(IEnumerable<ICommandSubscriber> commandSubscribers, ILogger<ICommandDispatcher> logger, IDomainTelemetryService telemetry)
        {
            this.logger = logger;
            this.commandSubscribers = commandSubscribers;
            this.telemetry = telemetry;
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
        /// <param name="command"></param>
        /// <returns></returns>
        private Task Send<TCommand>(TCommand command) where TCommand : ICommand
        {
            return telemetry.TraceAsync(
                "sourceflow.commanddispatcher.send",
                async () =>
                {
                    if (!commandSubscribers.Any())
                    {
                        logger?.LogInformation("Action=Command_Dispatcher, Command={Command}, Payload={Payload}, SequenceNo={No}, Message=No subscribers Found",
                        command.GetType().Name, command.Payload.GetType().Name, ((IMetadata)command).Metadata.SequenceNo);

                        return;
                    }

                    // Use ArrayPool-based optimization for task collection
                    await TaskBufferPool.ExecuteAsync(
                        commandSubscribers,
                        subscriber => subscriber.Subscribe(command));
                },
                activity =>
                {
                    activity?.SetTag("command.type", command.GetType().Name);
                    activity?.SetTag("command.entity_id", command.Entity.Id);
                    activity?.SetTag("subscribers.count", commandSubscribers.Count());
                });
        }
    }
}
