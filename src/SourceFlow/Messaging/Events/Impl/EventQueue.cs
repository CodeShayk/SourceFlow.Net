using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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
        /// Represents event dispather that can handle the dequeuing of events.
        /// </summary>
        public IEventDispatcher eventDispatcher;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventQueue"/> class with the specified logger.
        /// </summary>
        /// <param name="logger"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public EventQueue(IEventDispatcher eventDispatcher, ILogger<IEventQueue> logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
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

            await eventDispatcher.Dispatch(@event);

            logger?.LogInformation("Action=Event_Enqueue, Event={Event}, Payload={Payload}",
                @event.GetType().Name, @event.Payload.GetType().Name);
        }
    }
}