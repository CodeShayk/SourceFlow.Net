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
    public abstract class BaseSaga<TAggregateEntity> : ISaga<TAggregateEntity>
        where TAggregateEntity : class, IEntity
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
        /// The repository used to access and persist aggregate entity.
        /// </summary>
        protected IRepository repository;

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

            RegisterHandlers();
        }

        /// <summary>
        /// Registers all event handlers for the event types that this saga handles.
        /// </summary>
        private void RegisterHandlers()
        {
            var interfaces = this.GetType().GetInterfaces();
            foreach (var iface in interfaces)
            {
                if (iface.IsGenericType &&
                    iface.GetGenericTypeDefinition() == typeof(IEventHandler<>))
                {
                    eventHandlers.Add(new Tuple<Type, IEventHandler>(iface.GetGenericArguments()[0], (IEventHandler)this));
                }
            }
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
        async Task ISaga.HandleAsync<TEvent>(TEvent @event)
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
        Task<bool> ISaga.CanHandleEvent<TEvent>(TEvent @event)
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            var result = eventHandlers.Any(x => x.Item1.IsAssignableFrom(@event.GetType()));

            return Task.FromResult(result);
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

        /// <summary>
        /// Get aggregate associated with saga by Identifier.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        protected Task<TAggregateEntity> GetAggregate(int id)
        {
            return repository.GetByIdAsync<TAggregateEntity>(id);
        }

        /// <summary>
        /// Persist aggregate associated with saga.
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        protected Task PersistAggregate(TAggregateEntity entity)
        {
            return repository.PersistAsync(entity);
        }

        /// <summary>
        /// Delete aggregate associated with saga.
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        protected Task DeleteAggregate(TAggregateEntity entity)
        {
            return repository.DeleteAsync(entity);
        }
    }
}