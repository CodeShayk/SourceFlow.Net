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
        private readonly ICollection<ISagaHandler> sagas;

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
            this.sagas = new List<ISagaHandler>();
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

        private async Task SagaHandle<TEvent>(ISagaHandler saga, TEvent @event) where TEvent : IEvent
        {
            // 1. handle event by Saga?
            await saga.HandleAsync(@event);

            // 2. Set event sequence no.
            if (!@event.IsReplay)
            {
                @event.SequenceNo = await eventStore.GetNextSequenceNo(@event.AggregateId);

                // 3. Append event to event store.
                await eventStore.AppendAsync(@event);
            }
        }

        /// <summary>
        /// Replays events for a given aggregate.
        /// </summary>
        /// <param name="aggregateId"></param>
        /// <returns></returns>
        async Task ICommandBus.ReplayEvents(Guid aggregateId)
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
        void ICommandBus.RegisterSaga(ISagaHandler saga)
        {
            sagas.Add(saga);
        }
    }
}