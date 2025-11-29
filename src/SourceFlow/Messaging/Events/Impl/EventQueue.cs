using System;
using System.Collections.Generic;
using System.Linq;
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
        /// Represents event dispathers that can handle the dequeuing of events.
        /// </summary>
        public IEnumerable<IEventDispatcher> eventDispatchers;

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
        public EventQueue(IEnumerable<IEventDispatcher> eventDispatchers, ILogger<IEventQueue> logger, IDomainTelemetryService telemetry)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.eventDispatchers = eventDispatchers ?? throw new ArgumentNullException(nameof(eventDispatchers));
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
                    var tasks = new List<Task>();
                    foreach (var eventDispatcher in eventDispatchers)                    
                        tasks.Add(DispatchEvent(@event, eventDispatcher));                    

                    if (tasks.Any())
                        await Task.WhenAll(tasks);
                },
                activity =>
                {
                    activity?.SetTag("event.type", @event.GetType().Name);
                    activity?.SetTag("event.name", @event.Name);
                });
        }

        private Task DispatchEvent<TEvent>(TEvent @event, IEventDispatcher eventDispatcher) where TEvent : IEvent
        {
            logger?.LogInformation("Action=Event_Enqueue, Dispatcher={Dispatcher}, Event={Event}, Payload={Payload}", eventDispatcher.GetType().Name, @event.GetType().Name, @event.Payload.GetType().Name);
            return eventDispatcher.Dispatch(@event);
        }
    }
}