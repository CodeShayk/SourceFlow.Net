using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SourceFlow
{
    public abstract class BaseSaga<TAggregateRoot> : ISaga<TAggregateRoot>
        where TAggregateRoot : IAggregateRoot
    {
        protected ICollection<Tuple<Type, IEventHandler>> eventHandlers;
        protected IBusPublisher busPublisher;
        protected IBusSubscriber busSubscriber;

        protected BaseSaga()
        {
            eventHandlers = new List<Tuple<Type, IEventHandler>>();
        }

        public static bool IsGenericEventHandler(IEventHandler instance, Type eventType)
        {
            if (instance == null || eventType == null)
                return false;

            var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
            return handlerType.IsAssignableFrom(instance.GetType());
        }

        async Task ISagaHandler.HandleAsync<TEvent>(TEvent @event)
        {
            var tasks = new List<Task>();

            foreach (var ehandler in eventHandlers)
            {
                if (!ehandler.Item1.Equals(@event.GetType()) ||
                        !IsGenericEventHandler(ehandler.Item2, @event.GetType()))
                    continue;

                var method = typeof(IEventHandler<>)
                            .MakeGenericType(@event.GetType())
                            .GetMethod(nameof(IEventHandler<TEvent>.HandleAsync));

                var task = (Task)method.Invoke(ehandler.Item2, new object[] { @event });
                tasks.Add(task);
            }

            if (!tasks.Any())
                return;

            await Task.WhenAll(tasks);
        }

        Task<bool> ISagaHandler.CanHandleEvent<TEvent>(TEvent @event)
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            var result = eventHandlers.Any(x => x.Item1.IsAssignableFrom(@event.GetType()));

            return Task.FromResult(result);
        }

        protected void RegisterEventHandler<TEvent>(IEventHandler<TEvent> handler)
            where TEvent : IEvent
        {
            if (handler != null)
                eventHandlers.Add(new Tuple<Type, IEventHandler>(typeof(TEvent), handler));
        }

        protected Task PublishAsync<TEvent>(TEvent @event)
            where TEvent : IEvent
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            return busPublisher.PublishAsync(@event);
        }
    }
}