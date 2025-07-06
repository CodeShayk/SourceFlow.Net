using System;
using System.Reflection;

namespace SourceFlow
{
    public static class EventFactory
    {
        public static IEvent Create<TEvent>(this EventBuild eventBuild, IEventPayload payload)
        where TEvent : class, IEvent, new()
        {
            var @event = new TEvent
            {
                Entity = eventBuild.Entity
            };

            @event = AssignPayload(@event, payload);

            return @event;
        }

        private static TEvent AssignPayload<TEvent>(TEvent @event, IEventPayload payload)
        where TEvent : class, IEvent, new()
        {
            return AssignPayload(@event, payload);
        }

        public static EventBuild Create<TEvent>(this EventBuild eventBuild)
        where TEvent : class, IEvent, new()
        {
            eventBuild.Event = new TEvent
            {
                Entity = eventBuild.Entity
            };

            return eventBuild;
        }

        public static IEvent With<TPayload>(this EventBuild eventBuild, TPayload payload)
            where TPayload : class, IEventPayload, new()
        {
            return AssignPayload(eventBuild.Event, payload);
        }

        private static IEvent AssignPayload(IEvent @event, IEventPayload payload)
        {
            var pload = payload;
            var pType = pload.GetType();
            var eType = @event.GetType();
            var eventType = typeof(BaseEvent<>).MakeGenericType(pType);

            if (!eType.IsAssignableFrom(eventType))
                throw new InvalidOperationException($"Payload type {pType.Name} is not compatible with the event type {eType.Name}.");

            @event.GetType()
                     .GetField("Payload", BindingFlags.Instance | BindingFlags.Public)
                     ?.SetValue(@event, pload);

            return @event;
        }

        public class EventBuild
        {
            public Source Entity { get; set; }
            public IEvent Event { get; set; }
        }
    }

    public static class Event
    {
        public static EventFactory.EventBuild For<TAggregate>(int aggregateId)
        {
            var builder = new EventFactory.EventBuild();
            builder.Entity = new Source(aggregateId, typeof(TAggregate));
            return builder;
        }
    }
}