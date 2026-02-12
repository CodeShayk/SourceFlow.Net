using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Aggregate
{
    /// <summary>
    /// This subscriber is responsible for dispatching event to subscribing aggregates.
    /// </summary>
    internal class EventSubscriber : IEventSubscriber
    {
        /// <summary>
        /// Logger for the event queue to log events and errors.
        /// </summary>
        private readonly ILogger<IEventSubscriber> logger;

        /// <summary>
        /// Represents a collection of aggregate root objects.
        /// </summary>
        /// <remarks>This field holds a read-only collection of objects that implement the <see cref="IAggregate"/>
        /// interface. It is intended to be used internally to manage or process aggregate roots within the context of the
        /// application.</remarks>
        private readonly IEnumerable<IAggregate> aggregates;

        /// <summary>
        /// Middleware pipeline components for event subscribe.
        /// </summary>
        private readonly IEnumerable<IEventSubscribeMiddleware> middlewares;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventSubscriber"/> class with the specified aggregates and logger.
        /// </summary>
        /// <param name="aggregates"></param>
        /// <param name="logger"></param>
        /// <param name="middlewares"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public EventSubscriber(IEnumerable<IAggregate> aggregates, ILogger<IEventSubscriber> logger, IEnumerable<IEventSubscribeMiddleware> middlewares)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.aggregates = aggregates ?? throw new ArgumentNullException(nameof(aggregates));
            this.middlewares = middlewares ?? throw new ArgumentNullException(nameof(middlewares));
        }

        /// <summary>
        /// Dequeues the event to all aggregates that can handle it.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        public Task Subscribe<TEvent>(TEvent @event) where TEvent : IEvent
        {
            // Build the middleware pipeline: chain from last to first,
            // with CoreSubscribe as the innermost delegate.
            Func<TEvent, Task> pipeline = CoreSubscribe;

            foreach (var middleware in middlewares.Reverse())
            {
                var next = pipeline;
                pipeline = evt => middleware.InvokeAsync(evt, next);
            }

            return pipeline(@event);
        }

        /// <summary>
        /// Core subscribe logic: dispatches event to matching aggregates.
        /// </summary>
        private Task CoreSubscribe<TEvent>(TEvent @event) where TEvent : IEvent
        {
            var tasks = new List<Task>();

            foreach (var aggregate in aggregates)
            {
                if (!(aggregate is ISubscribes<TEvent> eventSubscriber))
                    continue;

                tasks.Add(eventSubscriber.On(@event));

                logger?.LogInformation("Action=Event_Disptcher_Aggregate, Event={Event}, Aggregate={Aggregate}",
                    typeof(TEvent).Name, aggregate.GetType().Name);
            }

            return Task.WhenAll(tasks);
        }
    }
}
