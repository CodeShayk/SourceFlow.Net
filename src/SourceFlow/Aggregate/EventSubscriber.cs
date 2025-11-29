using System;

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging.Events;
using SourceFlow.Messaging.Events.Impl;

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
        /// Initializes a new instance of the <see cref="EventDispatcher"/> class with the specified aggregates and view views.
        /// </summary>
        /// <param name="aggregates"></param>
        /// <param name="logger"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public EventSubscriber(IEnumerable<IAggregate> aggregates, ILogger<IEventSubscriber> logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.aggregates = aggregates ?? throw new ArgumentNullException(nameof(aggregates));
        }

        /// <summary>
        /// Dequeues the event to all aggregates that can handle it.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        public Task Subscribe<TEvent>(TEvent @event) where TEvent : IEvent
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
