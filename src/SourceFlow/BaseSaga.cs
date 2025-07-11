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
        /// Collection of event handlers registered with this saga.
        /// </summary>
        public ICollection<SagaHandler> Handlers { get; } = new List<SagaHandler>();

        /// <summary>
        /// The bus publisher used to publish events.
        /// </summary>
        protected IBusPublisher busPublisher;

        /// <summary>
        /// The repository used to access and persist aggregate entity.
        /// </summary>
        protected IDomainRepository repository;

        /// <summary>
        /// Logger for the saga to log events and errors.
        /// </summary>
        protected ILogger logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseSaga{TAggregateRoot}"/> class.
        /// </summary>
        protected BaseSaga()
        {
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
                    Handlers.Add(new SagaHandler(iface.GetGenericArguments()[0], (IEventHandler)this));
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
        async Task ISaga.Handle<TEvent>(TEvent @event)
        {
            var tasks = new List<Task>();

            foreach (var handler in Handlers)
            {
                if (!handler.EventType.Equals(@event.GetType()) ||
                        !IsGenericEventHandler(handler.Handler, @event.GetType()))
                    continue;

                var method = typeof(IEventHandler<>)
                            .MakeGenericType(@event.GetType())
                            .GetMethod(nameof(IEventHandler<TEvent>.Handle));

                var task = (Task)method.Invoke(handler.Handler, new object[] { @event });

                logger?.LogInformation("Action=Saga_Handled, Event={Event}, Aggregate={Aggregate}, SequenceNo={No}, Saga={Saga}, Handler:{Handler}",
                        @event.GetType().Name, @event.Entity.Type.Name, @event.SequenceNo, this.GetType().Name, method.Name);

                tasks.Add(task);
            }

            if (!tasks.Any())
                return;

            await Task.WhenAll(tasks);
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

            return busPublisher.Publish(@event);
        }

        /// <summary>
        /// Get aggregate associated with saga by Identifier.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        protected Task<TAggregateEntity> GetAggregate(int id)
        {
            return repository.Get<TAggregateEntity>(id);
        }

        /// <summary>
        /// Persist aggregate associated with saga.
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        protected Task PersistAggregate(TAggregateEntity entity)
        {
            return repository.Persist(entity);
        }

        /// <summary>
        /// Delete aggregate associated with saga.
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        protected Task DeleteAggregate(TAggregateEntity entity)
        {
            return repository.Delete(entity);
        }
    }
}