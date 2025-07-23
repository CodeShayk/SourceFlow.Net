using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;
using SourceFlow.ViewModel;

namespace SourceFlow.Impl
{
    /// <summary>
    /// This dispatcher is responsible for dispatching events to the appropriate view projections.
    /// </summary>
    internal class ProjectionDispatcher : IEventDispatcher
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
        private readonly ILogger<IEventDispatcher> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProjectionDispatcher"/> class with the specified projections and logger.
        /// </summary>
        /// <param name="projections"></param>
        /// <param name="logger"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public ProjectionDispatcher(IEnumerable<IProjection> projections, ILogger<IEventDispatcher> logger)
        {
            this.projections = projections ?? throw new ArgumentNullException(nameof(projections));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Dispatches the event to all view projections that can handle it.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="event"></param>
        public void Dispatch(object sender, IEvent @event)
        {
            Dispatch(@event).GetAwaiter().GetResult();
            logger?.LogInformation("Action=Event_Dispatcher_Complete, Event={Event}, Sender:{sender}",
                      @event.Name, sender.GetType().Name);
        }

        /// <summary>
        /// Dispatch the event to all view projections that can handle it.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        public async Task Dispatch<TEvent>(TEvent @event)
           where TEvent : IEvent
        {
            var tasks = new List<Task>();

            foreach (var projection in projections)
            {
                var projectionType = typeof(IProjectOn<>).MakeGenericType(@event.GetType());
                if (!projectionType.IsAssignableFrom(projection.GetType()))
                    continue;

                var method = typeof(IProjectOn<>)
                           .MakeGenericType(@event.GetType())
                           .GetMethod(nameof(IProjectOn<TEvent>.Apply));

                var task = (Task)method.Invoke(projection, new object[] { @event });

                tasks.Add(task);

                logger?.LogInformation("Action=Event_Dispatcher_View, Event={Event}, Apply:{Apply}",
                        @event.Name, projection.GetType().Name);
            }

            if (!tasks.Any())
                return;

            await Task.WhenAll(tasks);
        }
    }
}