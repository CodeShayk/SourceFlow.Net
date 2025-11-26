using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Observability;

namespace SourceFlow.Messaging.Events.Impl
{
    /// <summary>
    /// EventQueue is responsible for managing the flow of events in an event-driven architecture.
    /// </summary>
    internal class EventQueue : IEventQueue
    {
        /// <summary>
        /// Logger for the event queue to log events and errors.
        /// </summary>
        private readonly ILogger<IEventQueue> logger;

        /// <summary>
        /// Represents event dispather that can handle the dequeuing of events.
        /// </summary>
        public IEventDispatcher eventDispatcher;

        /// <summary>
        /// Telemetry service for observability.
        /// </summary>
        private readonly IDomainTelemetryService telemetry;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventQueue"/> class with the specified logger.
        /// </summary>
        /// <param name="eventDispatcher"></param>
        /// <param name="logger"></param>
        /// <param name="telemetry"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public EventQueue(IEventDispatcher eventDispatcher, ILogger<IEventQueue> logger, IDomainTelemetryService telemetry)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
            this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        }

        /// <summary>
        /// Enqueues an event in order to publish to subcribers.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        public Task Enqueue<TEvent>(TEvent @event)
            where TEvent : IEvent
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            return telemetry.TraceAsync(
                "sourceflow.eventqueue.enqueue",
                async () =>
                {
                    logger?.LogInformation("Action=Event_Enqueue, Event={Event}, Payload={Payload}",
                      @event.GetType().Name, @event.Payload.GetType().Name);

                    await eventDispatcher.Dispatch(@event);
                },
                activity =>
                {
                    activity?.SetTag("event.type", @event.GetType().Name);
                    activity?.SetTag("event.name", @event.Name);
                });
        }
    }
}