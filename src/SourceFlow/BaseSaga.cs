using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Events;
using SourceFlow.Impl;

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
        /// Provides access to the command publisher used to send commands within the application.
        /// </summary>
        /// <remarks>The command publisher is typically used to dispatch commands to their respective
        /// handlers. Derived classes can use this member to publish commands as part of their functionality.</remarks>
        protected ICommandPublisher commandPublisher;

        /// <summary>
        /// Represents the queue used to manage and process events.
        /// </summary>
        /// <remarks>This field is intended for internal use to handle event queuing operations.</remarks>
        protected IEventQueue eventQueue;

        /// <summary>
        /// Represents the repository used for accessing and managing domain entities.
        /// </summary>
        /// <remarks>This field is intended for internal use to interact with the domain repository. It
        /// provides access to the underlying data storage and retrieval mechanisms.</remarks>
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
        /// Determines whether the specified saga instance can handle the given event type.
        /// </summary>
        /// <param name="instance">The saga instance to evaluate. Must not be <see langword="null"/>.</param>
        /// <param name="eventType">The type of the event to check. Must not be <see langword="null"/>.</param>
        /// <returns><see langword="true"/> if the saga instance can handle the specified event type; otherwise, <see
        /// langword="false"/>.</returns>
        internal static bool CanHandle(ISaga instance, Type eventType)
        {
            if (instance == null || eventType == null)
                return false;

            var handlerType = typeof(ICommandHandler<>).MakeGenericType(eventType);
            return handlerType.IsAssignableFrom(instance.GetType());
        }

        /// <summary>
        /// Handles the specified command as part of the saga's workflow.
        /// </summary>
        /// <remarks>This method dynamically resolves the appropriate command handler for the given
        /// command type and invokes its <c>Handle</c> method. If the saga cannot handle the specified command, the
        /// method returns without performing any action.</remarks>
        /// <typeparam name="TCommand">The type of the command to handle.</typeparam>
        /// <param name="command">The command to be processed by the saga. Must not be <see langword="null"/>.</param>
        /// <returns></returns>
        async Task ISaga.Handle<TCommand>(TCommand command)
        {
            if (!CanHandle(this, command.GetType()))
                return;

            var method = typeof(ICommandHandler<>)
                        .MakeGenericType(command.GetType())
                        .GetMethod(nameof(ICommandHandler<TCommand>.Handle));

            var task = (Task)method.Invoke(this, new object[] { command });

            logger?.LogInformation("Action=Saga_Handled, Command={Command}, Aggregate={Aggregate}, SequenceNo={No}, Saga={Saga}, Handler:{Handler}",
                    command.GetType().Name, command.Entity.Type.Name, command.SequenceNo, this.GetType().Name, method.Name);

            await Task.Run(() => task);
        }

        /// <summary>
        /// Publishes the specified command to the command bus.
        /// </summary>
        /// <remarks>If the <paramref name="command"/> does not have an entity type specified, it will be
        /// automatically set to the type of <c>TAggregateEntity</c>.</remarks>
        /// <typeparam name="TCommand">The type of the command to publish. Must implement <see cref="ICommand"/>.</typeparam>
        /// <param name="command">The command to be published. Cannot be <see langword="null"/>.</param>
        /// <returns>A task that represents the asynchronous operation of publishing the command.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="command"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the <paramref name="command"/> does not have a valid source entity ID or if the entity type is not
        /// set.</exception>
        protected Task Publish<TCommand>(TCommand command)
            where TCommand : ICommand
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (command.Entity?.Id == null)
                throw new InvalidOperationException(nameof(command) + "requires source entity id");

            if (command.Entity.Type == null)
                command.Entity.Type = typeof(TAggregateEntity);

            return commandPublisher.Publish(command);
        }

        /// <summary>
        /// Publishes the specified event to notify subscribers.
        /// </summary>
        /// <typeparam name="TEvent">The type of the event to be raised.</typeparam>
        /// <typeparam name="TEntity">The type of the entity associated with the event.</typeparam>
        /// <param name="event">The event to be raised. Must not be <see langword="null"/> and must contain a valid payload.</param>
        /// <returns>A task that represents the asynchronous operation of raising the event.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="event"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the <paramref name="event"/> does not contain a valid payload.</exception>
        protected Task Raise<TEvent, TEntity>(TEvent @event)
            where TEntity : class, IEntity
            where TEvent : IEvent<TEntity>
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            if (@event.Payload == null)
                throw new InvalidOperationException(nameof(@event) + "event requires payload");

            return eventQueue.Enqueue(@event);
        }

        /// <summary>
        /// Retrieves an aggregate entity by its unique identifier.
        /// </summary>
        /// <param name="id">The unique identifier of the aggregate entity. Must be a positive integer.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the aggregate entity of type
        /// <typeparamref name="TAggregateEntity"/>.</returns>
        /// <exception cref="ArgumentException">Thrown if <paramref name="id"/> is less than or equal to zero.</exception>
        protected Task<TAggregateEntity> GetAggregate(int id)
        {
            if (id <= 0)
                throw new ArgumentException("Aggregate ID must be a positive integer.", nameof(id));

            return repository.Get<TAggregateEntity>(id);
        }

        /// <summary>
        /// Creates the specified aggregate entity and raises an event indicating that the entity has been created.
        /// </summary>
        /// <remarks>This method saves the provided aggregate entity to the underlying repository and
        /// raises an <see cref="EntityCreated{TAggregateEntity}"/> event to notify subscribers that the entity has been
        /// successfully created.</remarks>
        /// <param name="entity">The aggregate entity to be persisted. Cannot be <see langword="null"/>.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="entity"/> is <see langword="null"/>.</exception>
        protected async Task CreateAggregate(TAggregateEntity entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            await repository.Persist(entity);

            await Raise<EntityCreated<TAggregateEntity>, TAggregateEntity>(new EntityCreated<TAggregateEntity>(entity));
        }

        /// <summary>
        /// Updates the specified aggregate entity and raises an event indicating that the entity has been updated.
        /// </summary>
        /// <remarks>This method persists the provided aggregate entity to the repository and raises an
        /// <see cref="EntityUpdated{T}"/> event to signal that the entity has been updated. Ensure that the entity is
        /// properly initialized before calling this method.</remarks>
        /// <param name="entity">The aggregate entity to update. Cannot be <see langword="null"/>.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="entity"/> is <see langword="null"/>.</exception>
        protected async Task UpdateAggregate(TAggregateEntity entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            await repository.Persist(entity);

            await Raise<EntityUpdated<TAggregateEntity>, TAggregateEntity>(new EntityUpdated<TAggregateEntity>(entity));
        }

        /// <summary>
        /// Deletes the specified aggregate entity and raises an event to indicate the deletion.
        /// </summary>
        /// <remarks>This method performs the deletion asynchronously and raises an <see
        /// cref="EntityDeleted{T}"/> event to notify subscribers of the deletion. Ensure that the repository and event
        /// handling mechanisms are properly configured before calling this method.</remarks>
        /// <param name="entity">The aggregate entity to delete. Cannot be <see langword="null"/>.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="entity"/> is <see langword="null"/>.</exception>
        protected async Task DeleteAggregate(TAggregateEntity entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            await repository.Delete(entity);

            await Raise<EntityDeleted<TAggregateEntity>, TAggregateEntity>(new EntityDeleted<TAggregateEntity>(entity));
        }
    }
}