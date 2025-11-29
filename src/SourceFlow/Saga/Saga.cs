using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Saga
{
    /// <summary>
    /// Base class for sagas in the event-driven architecture.
    /// </summary>
    /// <typeparam name="TAggregate"></typeparam>
    public abstract class Saga<TAggregate> : ISaga<TAggregate>
        where TAggregate : class, IEntity
    {
        /// <summary>
        /// Provides access to the command publisher used to send commands within the application.
        /// </summary>
        /// <remarks>The command publisher is typically used to dispatch commands to their respective
        /// handlers. Derived classes can use this member to publish commands as part of their functionality.</remarks>
        protected Lazy<ICommandPublisher> commandPublisher;

        /// <summary>
        /// Represents the queue used to manage and process events.
        /// </summary>
        /// <remarks>This field is intended for internal use to handle event queuing operations.</remarks>
        protected IEventQueue eventQueue;

        /// <summary>
        /// Represents the entityStore used for accessing and managing domain entities.
        /// </summary>
        /// <remarks>This field is intended for internal use to interact with the domain entityStore. It
        /// provides access to the underlying data storage and retrieval mechanisms.</remarks>
        protected IEntityStoreAdapter entityStore;

        /// <summary>
        /// Logger for the saga to log events and errors.
        /// </summary>
        protected ILogger<ISaga> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="Saga{TAggregate}"/> class.
        /// </summary>
        public Saga(Lazy<ICommandPublisher> commandPublisher, IEventQueue eventQueue, IEntityStoreAdapter entityStore, ILogger<ISaga> logger)
        {
            this.commandPublisher = commandPublisher ?? throw new ArgumentNullException(nameof(commandPublisher));
            this.eventQueue = eventQueue ?? throw new ArgumentNullException(nameof(eventQueue));
            this.entityStore = entityStore ?? throw new ArgumentNullException(nameof(entityStore));
            this.logger = logger;
        }

        /// <summary>
        /// Determines whether the specified saga instance can handle the given event type.
        /// </summary>
        /// <param name="instance">The saga instance to evaluate. Must not be <see langword="null"/>.</param>
        /// <param name="commandType">The type of the command to check. Must not be <see langword="null"/>.</param>
        /// <returns><see langword="true"/> if the saga instance can handle the specified event type; otherwise, <see
        /// langword="false"/>.</returns>
        internal static bool CanHandle(ISaga instance, Type commandType)
        {
            if (instance == null || commandType == null)
                return false;

            var handlerType = typeof(IHandles<>).MakeGenericType(commandType);
            return handlerType.IsAssignableFrom(instance.GetType());
        }

        /// <summary>
        /// Handles the specified command as part of the saga's workflow.
        /// </summary>
        /// <remarks>This method dynamically resolves the appropriate command handler for the given
        /// command type and invokes its <c>On</c> method. If the saga cannot handle the specified command, the
        /// method returns without performing any action.</remarks>
        /// <typeparam name="TCommand">The type of the command to handle.</typeparam>
        /// <param name="command">The command to be processed by the saga. Must not be <see langword="null"/>.</param>
        /// <returns></returns>
        async Task ISaga.Handle<TCommand>(TCommand command)
        {

            if (!(this is IHandles<TCommand> handles))
            {
                logger?.LogWarning("Action=Saga_CannotHandle, Saga={Saga}, Command={Command}, Reason=NotImplementingIHandles",
                    GetType().Name, typeof(TCommand).Name);
                return;
            }

            logger?.LogInformation("Action=Saga_Starting, Saga={Saga}, Command={Command}",
                GetType().Name, typeof(TCommand).Name);

            TAggregate entity;
            if (command.Entity.IsNew)
                entity = InitialiseEntity(command.Entity.Id);
            else
                entity = await entityStore.Get<TAggregate>(command.Entity.Id);
            
             entity = (TAggregate) await handles.Handle(entity, command);

            logger?.LogInformation("Action=Saga_Handled, Command={Command}, Payload={Payload}, SequenceNo={No}, Saga={Saga}",
                    command.GetType().Name, command.Payload.GetType().Name, ((IMetadata)command).Metadata.SequenceNo, GetType().Name);

            if (entity != null)
                entity = await entityStore.Persist(entity);

            await RaiseEvent(command, entity);
        }

        private Task RaiseEvent<TCommand>(TCommand command, TAggregate entity) where TCommand : ICommand
        {
            try
            {
                var handlesWithEventInterface = this.GetType()
                    .GetInterfaces()
                    .FirstOrDefault(i =>
                        i.IsGenericType &&
                        i.GetGenericTypeDefinition() == typeof(IHandlesWithEvent<,>) &&
                        i.GetGenericArguments()[0].IsAssignableFrom(typeof(TCommand))
                    );

                if (handlesWithEventInterface != null)
                {
                    var eventType = handlesWithEventInterface.GetGenericArguments()[1];

                    object eventInstance = null;

                    // Try parameterless constructor first
                    try
                    {
                        eventInstance = Activator.CreateInstance(eventType);
                    }
                    catch
                    {
                        // Try constructor that accepts the aggregate/entity payload
                        var ctor = eventType.GetConstructors()
                            .FirstOrDefault(c =>
                            {
                                var ps = c.GetParameters();
                                return ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(entity.GetType());
                            });

                        if (ctor != null)
                        {
                            eventInstance = ctor.Invoke(new object[] { entity });
                        }
                    }

                    if (eventInstance is IEvent ev)
                    {
                        // Ensure payload set
                        if (ev.Payload == null && entity != null)
                            ev.Payload = entity;

                        // Call Raise with the concrete event type to preserve generics
                        var raiseMethod = this.GetType().GetMethod(nameof(Raise), BindingFlags.NonPublic | BindingFlags.Instance);
                        var genericRaiseMethod = raiseMethod.MakeGenericMethod(eventType);
                        return (Task)genericRaiseMethod.Invoke(this, new object[] { ev });
                    }
                }                
            }
            catch (Exception ex)
            {
                // Don't break saga processing if raising event fails; log the error.
                logger?.LogError(ex, "Action=Saga_RaiseEventFailed, Saga={Saga}, Command={Command}", GetType().Name, command.GetType().Name);
            }

            return Task.CompletedTask;
        }


        /// <summary>
        /// Initialises a new instance of the aggregate entity with the specified ID.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        /// 
        private TAggregate InitialiseEntity(int id)
        {
            var entity = Activator.CreateInstance(typeof(TAggregate), true);
            ((IEntity)entity).Id = id;
            return (TAggregate)entity;
        }

        /// <summary>
        /// Publishes the specified command to the command bus.
        /// </summary>
        /// <remarks>If the <paramref name="command"/> does not have an entity type specified, it will be
        /// automatically set to the type of <c>TEntity</c>.</remarks>
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
                throw new InvalidOperationException(nameof(command) + "requires entity reference with id");

            return commandPublisher.Value.Publish(command);
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
        protected Task Raise<TEvent>(TEvent @event)
            where TEvent : IEvent
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            if (@event.Payload == null)
                throw new InvalidOperationException(nameof(@event) + "event requires payload");

            return eventQueue.Enqueue(@event);
        }
    }
}