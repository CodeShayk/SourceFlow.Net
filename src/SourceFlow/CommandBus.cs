using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System;

namespace SourceFlow
{
    public class CommandBus : ICommandBus
    {
        private readonly IEventStore eventStore;
        protected IAggregateFactory aggregateFactory;
        private readonly ICollection<ISagaHandler> sagas;

        public CommandBus(IEventStore eventStore, IAggregateFactory aggregateFactory)
        {
            this.eventStore = eventStore;
            this.aggregateFactory = aggregateFactory;
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

            await HandleEvent<TEvent>(@event);
        }

        private async Task HandleEvent<TEvent>(TEvent @event) where TEvent : IEvent
        {
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