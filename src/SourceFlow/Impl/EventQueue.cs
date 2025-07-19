using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Impl
{
    internal class EventQueue : IEventQueue
    {
        /// <summary>
        /// Logger for the event queue to log events and errors.
        /// </summary>
        private readonly ILogger<EventQueue> logger;

        /// <summary>
        /// Collection of aggregates registered with the event queue.
        /// </summary>
        private readonly IEnumerable<IAggregateRoot> aggregates;

        /// <summary>
        /// Transform publisher used to publish events to view transforms.
        /// </summary>
        private readonly IViewPublisher viewPublisher;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventQueue"/> class with the specified aggregates, view
        /// publisher, and logger.
        /// </summary>
        /// <param name="aggregates">A collection of aggregate roots that the event queue will process. Cannot be null.</param>
        /// <param name="viewPublisher">The view publisher responsible for publishing events to views. Cannot be null.</param>
        /// <param name="logger">The logger used to log diagnostic and operational information. Cannot be null.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="aggregates"/>, <paramref name="viewPublisher"/>, or <paramref name="logger"/> is
        /// null.</exception>
        public EventQueue(IEnumerable<IAggregateRoot> aggregates, IViewPublisher viewPublisher, ILogger<EventQueue> logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.aggregates = aggregates ?? throw new ArgumentNullException(nameof(aggregates));
            this.viewPublisher = viewPublisher ?? throw new ArgumentNullException(nameof(viewPublisher));
        }

        /// <summary>
        /// Enqueues an event in order to publish to subcribers.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        public async Task Enqueue<TEvent>(TEvent @event)
            where TEvent : IEvent
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            await viewPublisher.Publish(@event);

            var tasks = new List<Task>();

            foreach (var aggregate in aggregates)
            {
                var handlerType = typeof(IEventHandler<>).MakeGenericType(@event.GetType());
                if (!handlerType.IsAssignableFrom(aggregate.GetType()))
                    continue;

                var method = typeof(IEventHandler<>)
                            .MakeGenericType(@event.GetType())
                            .GetMethod(nameof(IEventHandler<TEvent>.Handle));

                var task = (Task)method.Invoke(aggregate, new object[] { @event });

                logger?.LogInformation("Action=Event_Enqueue, Event={Event}, Aggregate={Aggregate}, Handler:{Handler}",
                        @event.GetType().Name, aggregate.GetType().Name, method.Name);

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
        }
    }
}