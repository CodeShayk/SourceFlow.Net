using System;
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
        }

        /// <summary>
        /// Checks if the given event handler is a generic event handler for the specified event type.
        /// </summary>
        /// <param name="instance"></param>
        /// <param name="eventType"></param>
        /// <returns></returns>
        internal static bool CanHandle(ISaga instance, Type eventType)
        {
            if (instance == null || eventType == null)
                return false;

            var handlerType = typeof(ISagaHandler<>).MakeGenericType(eventType);
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
            if (!CanHandle(this, @event.GetType()))
                return;

            var method = typeof(ISagaHandler<>)
                        .MakeGenericType(@event.GetType())
                        .GetMethod(nameof(ISagaHandler<TEvent>.Handle));

            var task = (Task)method.Invoke(this, new object[] { @event });

            logger?.LogInformation("Action=Saga_Handled, Event={Event}, Aggregate={Aggregate}, SequenceNo={No}, Saga={Saga}, Handler:{Handler}",
                    @event.GetType().Name, @event.Entity.Type.Name, @event.SequenceNo, this.GetType().Name, method.Name);

            await Task.Run(() => task);
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

            if (@event.Entity?.Id == null)
                throw new InvalidOperationException(nameof(@event) + "requires source entity id");

            if (@event.Entity.Type == null)
                @event.Entity.Type = typeof(TAggregateEntity);

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