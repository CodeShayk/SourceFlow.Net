using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Projections
{
    /// <summary>
    /// This subscriber is responsible for subsribing events to apply view projections.
    /// </summary>
    internal class EventSubscriber : IEventSubscriber
    {
        /// <summary>
        /// Represents a collection of view transforms used to modify or manipulate views.
        /// </summary>
        /// <remarks>This collection contains instances of objects implementing the <see
        /// cref="IProjection"/> interface. Each projection in the collection can be applied to alter the appearance
        /// or behavior of a view.</remarks>
        private IEnumerable<IProjection> projections;

        /// <summary>
        /// Logger for the event queue to log events and errors.
        /// </summary>
        private readonly ILogger<IEventSubscriber> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventSubscriber"/> class with the specified projections and logger.
        /// </summary>
        /// <param name="projections"></param>
        /// <param name="logger"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public EventSubscriber(IEnumerable<IProjection> projections, ILogger<IEventSubscriber> logger)
        {
            this.projections = projections ?? throw new ArgumentNullException(nameof(projections));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Dispatch the event to all view projections that can handle it.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        public async Task Subscribe<TEvent>(TEvent @event)
           where TEvent : IEvent
        {
            var tasks = new List<Task>();

            foreach (var projection in projections)
            {
               if (!(projection is IProjectOn<TEvent> eventSubscriber))
                    continue;

                tasks.Add(eventSubscriber.Apply(@event));

                logger?.LogInformation("Action=Event_Dispatcher_View, Event={Event}, Apply:{Apply}",
                        @event.Name, projection.GetType().Name);
            }

            if (!tasks.Any())
                return;

            await Task.WhenAll(tasks);
        }
    }
}