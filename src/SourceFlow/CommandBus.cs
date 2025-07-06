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

            if (!sagas.Any())
                return;

            var tasks = new List<Task>();
            foreach (var saga in sagas)
            {
                if (!await saga.CanHandleEvent(@event))
                    continue;

                tasks.Add(SagaHandle(saga, @event));
            }

            await Task.WhenAll(tasks);
        }

        private async Task SagaHandle<TEvent>(ISaga saga, TEvent @event) where TEvent : IEvent
        {
            // 1. handle event by Saga?
            await saga.HandleAsync(@event);

            // 2. When event is not replayed
            if (!@event.IsReplay)
            {
                // 2.1 Set event sequence no.
                @event.SequenceNo = await eventStore.GetNextSequenceNo(@event.Entity.Id);

                // 2.2. Append event to event store.
                await eventStore.AppendAsync(@event);
            }

            // 0. Log event.
            logger.LogInformation(string.Format($"Event published: {0} for Aggregate {1} with SequenceNo {2}",
                @event.GetType().Name, @event.Entity.Id, @event.SequenceNo));

            // 3. Project data view from Event.
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

            var tasks = new List<Task>();
            foreach (var @event in events)
                foreach (var saga in sagas)
                {
                    @event.IsReplay = true;
                    if (!await saga.CanHandleEvent(@event))
                        continue;

                    tasks.Add(SagaHandle(saga, @event));
                }

            await Task.WhenAll(tasks);
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