using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System;
using Microsoft.Extensions.Logging;

namespace SourceFlow
{
    /// <summary>
    /// Command bus implementation that handles commands and events in an event-driven architecture.
    /// </summary>
    public class CommandBus : ICommandBus
    {
        /// <summary>
        /// The event store used to persist events.
        /// </summary>
        private readonly IEventStore eventStore;

        /// <summary>
        /// Logger for the command bus to log events and errors.
        /// </summary>
        private readonly ILogger<ICommandBus> logger;

        /// <summary>
        /// Collection of sagas registered with the command bus.
        /// </summary>
        private readonly ICollection<ISaga> sagas;

        private readonly IETLPublisher etlPublisher;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandBus"/> class.
        /// </summary>
        /// <param name="eventStore"></param>
        public CommandBus(IEventStore eventStore, IETLPublisher etlPublisher, ILogger<ICommandBus> logger)
        {
            this.eventStore = eventStore;
            this.logger = logger;
            this.sagas = new List<ISaga>();
            this.etlPublisher = etlPublisher;
        }

        /// <summary>
        /// Publishes an event to all subscribed sagas.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        async Task ICommandBus.Publish<TEvent>(TEvent @event)
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            await PublishToSagas(@event);
            await etlPublisher.Publish(@event);
        }

        /// <summary>
        /// Publishes an event to all sagas that are registered with the command bus.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        private async Task PublishToSagas<TEvent>(TEvent @event) where TEvent : IEvent
        {
            if (!sagas.Any())
                return;

            var tasks = new List<Task>();
            foreach (var saga in sagas)
            {
                if (saga == null || saga.Handlers == null || !saga.Handlers.Any())
                    continue;

                if (!saga.Handlers.Any(x => x.EventType.IsAssignableFrom(@event.GetType())))
                    continue;

                tasks.Add(SagaHandle(saga, @event));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Handles the event in the saga and appends it to the event store if not replayed.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="saga"></param>
        /// <param name="event"></param>
        /// <returns></returns>
        private async Task SagaHandle<TEvent>(ISaga saga, TEvent @event) where TEvent : IEvent
        {
            // 1. Set event sequence no.
            if (!@event.IsReplay)
                @event.SequenceNo = await eventStore.GetNextSequenceNo(@event.Entity.Id);

            // 4. Log event.
            logger?.LogInformation("Action=Command_Dispatched, Event={Event}, Aggregate={Aggregate}, SequenceNo={No}, Saga={Saga}",
                @event.GetType().Name, @event.Entity.Type.Name, @event.SequenceNo, saga.GetType().Name);

            // 2. handle event by Saga?
            await saga.Handle(@event);

            // 3. When event is not replayed
            if (!@event.IsReplay)
            {
                // 3.1. Append event to event store.
                await eventStore.Append(@event);
            }
        }

        /// <summary>
        /// Replays events for a given aggregate.
        /// </summary>
        /// <param name="aggregateId">Unique aggregate entity id.</param>
        /// <returns></returns>
        async Task ICommandBus.ReplayEvents(int aggregateId)
        {
            var events = await eventStore.Load(aggregateId);

            if (events == null || !events.Any())
                return;

            foreach (var @event in events.ToList())
            {
                @event.IsReplay = true;
                await PublishToSagas(@event);
                await etlPublisher.Publish(@event);
            }
        }

        /// <summary>
        /// Registers a saga with the command bus.
        /// </summary>
        /// <param name="saga"></param>
        void ICommandBus.RegisterSaga(ISaga saga)
        {
            sagas.Add(saga);
        }
    }
}