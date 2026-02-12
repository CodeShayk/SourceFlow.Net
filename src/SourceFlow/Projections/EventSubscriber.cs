using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Projections
{
    /// <summary>
    /// This subscriber is responsible for subsribing events to apply view views.
    /// </summary>
    internal class EventSubscriber : IEventSubscriber
    {
        /// <summary>
        /// Represents a collection of transforms used to modify or manipulate views.
        /// </summary>
        /// <remarks>This collection contains instances of objects implementing the <see
        /// cref="IView"/> interface. Each view in the collection can be applied to alter the appearance
        /// or behavior of a view.</remarks>
        private IEnumerable<IView> views;

        /// <summary>
        /// Logger for the event queue to log events and errors.
        /// </summary>
        private readonly ILogger<IEventSubscriber> logger;

        /// <summary>
        /// Middleware pipeline components for event subscribe.
        /// </summary>
        private readonly IEnumerable<IEventSubscribeMiddleware> middlewares;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventSubscriber"/> class with the specified views and logger.
        /// </summary>
        /// <param name="views"></param>
        /// <param name="logger"></param>
        /// <param name="middlewares"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public EventSubscriber(IEnumerable<IView> views, ILogger<IEventSubscriber> logger, IEnumerable<IEventSubscribeMiddleware> middlewares)
        {
            this.views = views ?? throw new ArgumentNullException(nameof(views));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.middlewares = middlewares ?? throw new ArgumentNullException(nameof(middlewares));
        }

        /// <summary>
        /// Dispatch the event to all view views that can handle it.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        public Task Subscribe<TEvent>(TEvent @event)
            where TEvent : IEvent
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
        /// Core subscribe logic: dispatches event to matching views.
        /// </summary>
        private Task CoreSubscribe<TEvent>(TEvent @event) where TEvent : IEvent
        {
            if (!views.Any())
            {
                logger?.LogInformation("Action=Command_Dispatcher, Command={Command}, Payload={Payload}, SequenceNo={No}, Message=No Sagas Found",
                @event.GetType().Name, @event.Payload.GetType().Name, ((IMetadata)@event).Metadata.SequenceNo);

                return Task.CompletedTask;
            }

            var tasks = new List<Task>();
            foreach (var view in views)
            {
                if (view == null || !View<IViewModel>.CanHandle(view, @event.GetType()))
                    continue;

                logger?.LogInformation("Action=Projection_Apply, Event={Event}, Projection={Projection}, SequenceNo={No}",
                    @event.GetType().Name, view.GetType().Name, ((IMetadata)@event).Metadata.SequenceNo);

                tasks.Add(view.Apply(@event));
            }

            return Task.WhenAll(tasks);
        }
    }
}
