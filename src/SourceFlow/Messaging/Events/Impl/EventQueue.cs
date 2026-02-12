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
        /// Middleware pipeline components for event dispatch.
        /// </summary>
        private readonly IEnumerable<IEventDispatchMiddleware> middlewares;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventQueue"/> class with the specified logger.
        /// </summary>
        /// <param name="eventDispatchers"></param>
        /// <param name="logger"></param>
        /// <param name="telemetry"></param>
        /// <param name="middlewares"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public EventQueue(IEnumerable<IEventDispatcher> eventDispatchers, ILogger<IEventQueue> logger, IDomainTelemetryService telemetry, IEnumerable<IEventDispatchMiddleware> middlewares)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.eventDispatchers = eventDispatchers ?? throw new ArgumentNullException(nameof(eventDispatchers));
            this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            this.middlewares = middlewares ?? throw new ArgumentNullException(nameof(middlewares));
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
                    // Build the middleware pipeline: chain from last to first,
                    // with CoreEnqueue as the innermost delegate.
                    Func<TEvent, Task> pipeline = CoreEnqueue;

                    foreach (var middleware in middlewares.Reverse())
                    {
                        var next = pipeline;
                        pipeline = evt => middleware.InvokeAsync(evt, next);
                    }

                    await pipeline(@event);
                },
                activity =>
                {
                    activity?.SetTag("event.type", @event.GetType().Name);
                    activity?.SetTag("event.name", @event.Name);
                });
        }

        /// <summary>
        /// Core enqueue logic: dispatches the event to all registered event dispatchers.
        /// </summary>
        private async Task CoreEnqueue<TEvent>(TEvent @event) where TEvent : IEvent
        {
            var tasks = new List<Task>();
            foreach (var eventDispatcher in eventDispatchers)
                tasks.Add(DispatchEvent(@event, eventDispatcher));

            if (tasks.Any())
                await Task.WhenAll(tasks);
        }

        private Task DispatchEvent<TEvent>(TEvent @event, IEventDispatcher eventDispatcher) where TEvent : IEvent
        {
            logger?.LogInformation("Action=Event_Enqueue, Dispatcher={Dispatcher}, Event={Event}, Payload={Payload}", eventDispatcher.GetType().Name, @event.GetType().Name, @event.Payload.GetType().Name);
            return eventDispatcher.Dispatch(@event);
        }
    }
}
