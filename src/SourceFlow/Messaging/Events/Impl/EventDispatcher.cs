using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
namespace SourceFlow.Messaging.Events.Impl
{
    internal class EventDispatcher : IEventDispatcher
    {
        /// <summary>
        /// Represents a collection of subscribers interested in the event.
        /// </summary>
        /// <remarks>This collection contains instances of objects implementing the <see cref="IEventSubscriber"/> interface. Each subscribers in the collection subscribes to events of interest.</remarks>
        private IEnumerable<IEventSubscriber> subscribers;

        /// <summary>
        /// Logger for the event queue to log events and errors.
        /// </summary>
        private readonly ILogger<IEventDispatcher> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventDispatcher"/> class with the specified subscribers and logger.
        /// </summary>
        /// <param name="subscribers"></param>
        /// <param name="logger"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public EventDispatcher(IEnumerable<IEventSubscriber> subscribers, ILogger<IEventDispatcher> logger)
        {
            this.subscribers = subscribers ?? throw new ArgumentNullException(nameof(subscribers));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Dispatch the event to all subscribers that can handle it.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        public Task Dispatch<TEvent>(TEvent @event) where TEvent : IEvent
        {
            var tasks = new List<Task>();

            foreach (var subscriber in subscribers)
            {
                tasks.Add(subscriber.Subscribe(@event));

                logger?.LogInformation("Action=Event_Dispatcher, Event={Event}, Subscriber:{subscriber}",
                        @event.Name, subscribers.GetType().Name);
            }

            if (!tasks.Any())
                return Task.CompletedTask;

            return Task.WhenAll(tasks);
        }
    }
}