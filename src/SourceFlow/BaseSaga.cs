using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SourceFlow
{
    /// <summary>
    /// Base class for sagas in the event-driven architecture.
    /// </summary>
    /// <typeparam name="TAggregateRoot"></typeparam>
    public abstract class BaseSaga<TAggregateRoot> : ISaga<TAggregateRoot>
        where TAggregateRoot : IAggregateRoot
    {
        /// <summary>
        /// Collection of event handlers registered for this saga.
        /// </summary>
        protected ICollection<Tuple<Type, IEventHandler>> eventHandlers;

        /// <summary>
        /// The bus publisher used to publish events.
        /// </summary>
        protected IBusPublisher busPublisher;

        /// <summary>
        /// The bus subscriber used to subscribe saga to events.
        /// </summary>
        protected IBusSubscriber busSubscriber;

        /// <summary>
        /// Logger for the saga to log events and errors.
        /// </summary>
        protected ILogger logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseSaga{TAggregateRoot}"/> class.
        /// </summary>
        protected BaseSaga()
        {
            eventHandlers = new List<Tuple<Type, IEventHandler>>();
        }

        /// <summary>
        /// Checks if the given event handler is a generic event handler for the specified event type.
        /// </summary>
        /// <param name="instance"></param>
        /// <param name="eventType"></param>
        /// <returns></returns>
        private static bool IsGenericEventHandler(IEventHandler instance, Type eventType)
        {
            if (instance == null || eventType == null)
                return false;

            var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
            return handlerType.IsAssignableFrom(instance.GetType());
        }

        /// <summary>
        /// Handles the specified event asynchronously in the saga.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        async Task ISagaHandler.HandleAsync<TEvent>(TEvent @event)
        {
            var tasks = new List<Task>();

            foreach (var ehandler in eventHandlers)
            {
                if (!ehandler.Item1.Equals(@event.GetType()) ||
                        !IsGenericEventHandler(ehandler.Item2, @event.GetType()))
                    continue;

                var method = typeof(IEventHandler<>)
                            .MakeGenericType(@event.GetType())
                            .GetMethod(nameof(IEventHandler<TEvent>.HandleAsync));

                var task = (Task)method.Invoke(ehandler.Item2, new object[] { @event });
                tasks.Add(task);
            }

            if (!tasks.Any())
                return;

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Checks if the saga can handle the specified event.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        Task<bool> ISagaHandler.CanHandleEvent<TEvent>(TEvent @event)
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            var result = eventHandlers.Any(x => x.Item1.IsAssignableFrom(@event.GetType()));

            return Task.FromResult(result);
        }

        /// <summary>
        /// Registers an event handler for the specified event type.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="handler"></param>
        protected void RegisterEventHandler<TEvent>(IEventHandler<TEvent> handler)
            where TEvent : IEvent
        {
            if (handler != null)
                eventHandlers.Add(new Tuple<Type, IEventHandler>(typeof(TEvent), handler));
        }

        /// <summary>
        /// Publishes an event to all subscribers of the bus.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        protected Task PublishAsync<TEvent>(TEvent @event)
            where TEvent : IEvent
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            return busPublisher.PublishAsync(@event);
        }
    }
}