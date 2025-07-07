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
        /// The aggregate factory used to create aggregates.
        /// </summary>
        protected IAggregateFactory aggregateFactory;

        /// <summary>
        /// Logger for the command bus to log events and errors.
        /// </summary>
        private readonly ILogger<ICommandBus> logger;

        /// <summary>
        /// Collection of sagas registered with the command bus.
        /// </summary>
        private readonly ICollection<ISaga> sagas;

        private readonly ICollection<IDataView> dataViews;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandBus"/> class.
        /// </summary>
        /// <param name="eventStore"></param>
        /// <param name="aggregateFactory"></param>
        public CommandBus(IEventStore eventStore, IAggregateFactory aggregateFactory, ILogger<ICommandBus> logger)
        {
            this.eventStore = eventStore;
            this.aggregateFactory = aggregateFactory;
            this.logger = logger;
            this.sagas = new List<ISaga>();
            this.dataViews = new List<IDataView>();
        }

        /// <summary>
        /// Publishes an event to all subscribed sagas.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        async Task ICommandBus.PublishAsync<TEvent>(TEvent @event)
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            await PublishToSagas(@event);
            await PublishToDataViews(@event);
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

                if (!saga.Handlers.Any(x => x.Item1.IsAssignableFrom(@event.GetType())))
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
            await saga.HandleAsync(@event);

            // 3. When event is not replayed
            if (!@event.IsReplay)
            {
                // 3.1. Append event to event store.
                await eventStore.AppendAsync(@event);
            }
        }

        /// <summary>
        /// Publishes an event to all data views that are registered with the command bus.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        private async Task PublishToDataViews<TEvent>(TEvent @event) where TEvent : IEvent
        {
            if (!dataViews.Any())
                return;

            var tasks = new List<Task>();
            foreach (var dataView in dataViews)
            {
                if (dataView == null || dataView.Projections == null || !dataView.Projections.Any())
                    continue;

                if (!dataView.Projections.Any(x => x.Item1.IsAssignableFrom(@event.GetType())))
                    continue;

                logger?.LogInformation("Action=Projection_Dispatched, Event={Event}, Aggregate={Aggregate}, SequenceNo={No}, DataView={DataView}",
                    @event.GetType().Name, @event.Entity.Type.Name, @event.SequenceNo, dataView.GetType().Name);

                tasks.Add(dataView.TransformAsync(@event));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Replays events for a given aggregate.
        /// </summary>
        /// <param name="aggregateId">Unique aggregate entity id.</param>
        /// <returns></returns>
        async Task ICommandBus.ReplayEvents(int aggregateId)
        {
            var events = await eventStore.LoadAsync(aggregateId);

            if (events == null || !events.Any())
                return;

            foreach (var @event in events.ToList())
            {
                @event.IsReplay = true;
                await PublishToSagas(@event);
                await PublishToDataViews(@event);
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

        /// <summary>
        /// Registers a data view with the command bus.
        /// </summary>
        /// <param name="view"></param>
        void ICommandBus.RegisterView(IDataView view)
        {
            dataViews.Add(view);
        }
    }
}