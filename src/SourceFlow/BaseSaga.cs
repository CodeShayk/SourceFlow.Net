using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SourceFlow
{
    public abstract class BaseSaga<TAggregateRoot> : ISaga<TAggregateRoot>
        where TAggregateRoot : IAggregateRoot
    {
        protected ISagaBus sagaBus;
        protected List<IEventHandler> eventHandlers;
        private IEventStore eventStore;

        protected BaseSaga(ISagaBus sagaBus, IEventStore eventStore)
        {
            this.sagaBus = sagaBus;
            this.eventStore = eventStore;
            eventHandlers = new List<IEventHandler>();
            sagaBus.RegisterSaga(this);
        }

        async Task ISaga.HandleAsync(IDomainEvent @event)
        {
            if (!await CanHandleEvent(@event))
                return;

            var tasks = new List<Task>();

            foreach (var handler in eventHandlers.Where(handler => handler is IEventHandler<IDomainEvent> eventHandler)
                .Cast<IEventHandler<IDomainEvent>>()
                .ToList())
                tasks.Add(handler.HandleAsync(@event));

            await Task.WhenAll(tasks);

            var aggregateRoot = @event.Source;

            aggregateRoot.SequenceNo = @event.SequenceNo;
            await aggregateRoot.ApplyAsync(@event);

            await eventStore.AppendAsync(@event);
        }

        public abstract Task<bool> CanHandleEvent(IDomainEvent @event);

        protected void RegisterEventHandler<TEvent>(Func<ISagaBus, IEventHandler<TEvent>> eventHandler)
            where TEvent : IDomainEvent
        {
            var handler = eventHandler(sagaBus);

            if (handler != null)
                eventHandlers.Add(handler);
        }

        async Task ISaga<TAggregateRoot>.Replay(TAggregateRoot aggregateRoot)
        {
            var events = await eventStore.LoadAsync(aggregateRoot.State.Id);
            if (events == null)
                return;

            foreach (var @event in events)
            {
                @event.Source = aggregateRoot;

                aggregateRoot.SequenceNo = @event.SequenceNo;
                await aggregateRoot.ApplyAsync(@event);
            }
        }
    }
}