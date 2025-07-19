using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Aggregate;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;
using SourceFlow.ViewModel;

namespace SourceFlow.Impl
{
    /// <summary>
    /// EventQueue is responsible for managing the flow of events in an event-driven architecture.
    /// </summary>
    internal class EventQueue : IEventQueue
    {
        /// <summary>
        /// Logger for the event queue to log events and errors.
        /// </summary>
        private readonly ILogger<EventQueue> logger;

        /// <summary>
        /// Represents a collection of view transforms used to modify or manipulate views.
        /// </summary>
        /// <remarks>This collection contains instances of objects implementing the <see
        /// cref="IProjection"/> interface. Each projection in the collection can be applied to alter the appearance
        /// or behavior of a view.</remarks>
        private IEnumerable<IProjection> viewProjections;

        /// <summary>
        /// Represents a collection of aggregate root objects.
        /// </summary>
        /// <remarks>This field holds a read-only collection of objects that implement the <see cref="IAggregateRoot"/>
        /// interface. It is intended to be used internally to manage or process aggregate roots within the context of the
        /// application.</remarks>
        private readonly IEnumerable<IAggregateRoot> aggregates;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventQueue"/> class with the specified aggregates and view projections.
        /// </summary>
        /// <param name="aggregates"></param>
        /// <param name="viewProjections"></param>
        /// <param name="logger"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public EventQueue(IEnumerable<IAggregateRoot> aggregates, IEnumerable<IProjection> viewProjections, ILogger<EventQueue> logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.aggregates = aggregates ?? throw new ArgumentNullException(nameof(aggregates));
            this.viewProjections = viewProjections ?? throw new ArgumentNullException(nameof(viewProjections));
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

            await DequeueToViews(@event);
            await DequeueToAggregates(@event);
        }

        /// <summary>
        /// Dequeues the event to all aggregates that can handle it.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        private async Task DequeueToAggregates<TEvent>(TEvent @event)
            where TEvent : IEvent
        {
            var tasks = new List<Task>();

            foreach (var aggregate in aggregates)
            {
                var handlerType = typeof(ISubscribes<>).MakeGenericType(@event.GetType());
                if (!handlerType.IsAssignableFrom(aggregate.GetType()))
                    continue;

                var method = typeof(ISubscribes<>)
                            .MakeGenericType(@event.GetType())
                            .GetMethod(nameof(ISubscribes<TEvent>.Handle));

                var task = (Task)method.Invoke(aggregate, new object[] { @event });

                tasks.Add(task);

                logger?.LogInformation("Action=Event_Enqueue, Event={Event}, Aggregate={Aggregate}, Handler:{Handler}",
                       @event.GetType().Name, aggregate.GetType().Name, method.Name);
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Dequeues the event to all view projections that can handle it.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        public async Task DequeueToViews<TEvent>(TEvent @event)
           where TEvent : IEvent
        {
            var tasks = new List<Task>();

            foreach (var projection in viewProjections)
            {
                var projectionType = typeof(IProjectOn<>).MakeGenericType(@event.GetType());
                if (!projectionType.IsAssignableFrom(projection.GetType()))
                    continue;

                var method = typeof(IProjectOn<>)
                           .MakeGenericType(@event.GetType())
                           .GetMethod(nameof(IProjectOn<TEvent>.Apply));

                var task = (Task)method.Invoke(projection, new object[] { @event });

                tasks.Add(task);

                logger?.LogInformation("Action=View_Projection, Event={Event}, Apply:{Apply}",
                        @event.Name, projection.GetType().Name);
            }

            if (!tasks.Any())
                return;

            await Task.WhenAll(tasks);
        }
    }
}