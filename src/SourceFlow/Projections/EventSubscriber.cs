using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;
using SourceFlow.Saga;

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
        /// Initializes a new instance of the <see cref="EventSubscriber"/> class with the specified views and logger.
        /// </summary>
        /// <param name="views"></param>
        /// <param name="logger"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public EventSubscriber(IEnumerable<IView> views, ILogger<IEventSubscriber> logger)
        {
            this.views = views ?? throw new ArgumentNullException(nameof(views));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            if (!views.Any())
            {
                logger?.LogInformation("Action=Command_Dispatcher, Command={Command}, Payload={Payload}, SequenceNo={No}, Message=No Sagas Found",
                @event.GetType().Name, @event.Payload.GetType().Name, ((IMetadata)@event).Metadata.SequenceNo);

                return Task.CompletedTask;
            }

            var tasks = new List<Task>();
            foreach (var view in views)
            {
                if (view == null || !View.CanHandle(view, @event.GetType()))
                    continue;

                logger?.LogInformation("Action=Projection_Apply, Event={Event}, Projection={Projection}, SequenceNo={No}",
                    @event.GetType().Name, view.GetType().Name, ((IMetadata)@event).Metadata.SequenceNo);

                tasks.Add(view.Apply(@event));
            }

            return Task.WhenAll(tasks);            
        }        
    }
}