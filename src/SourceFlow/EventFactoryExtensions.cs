namespace SourceFlow
{
    public static class EventFactoryExtensions
    {
        public static IEvent Create<TEvent, TPayload>(this EventBuild eventBuild, TPayload payload)
            where TEvent : class, IEvent<TPayload>, new()
            where TPayload : class, IEventPayload, new()
        {
            var @event = new TEvent
            {
                Entity = eventBuild.Entity,
            };

            AssignPayload(@event, payload);

            return @event;
        }

        private static void AssignPayload<TPayload>(IEvent<TPayload> @event, TPayload payload)
            where TPayload : class, IEventPayload, new()
        {
            @event.Payload = payload;
        }

        public class EventBuild
        {
            public Source Entity { get; set; }
            public IEvent Event { get; set; }
        }
    }

    public static class Event
    {
        public static EventFactoryExtensions.EventBuild For<TAggregate>(int aggregateId)
        {
            var builder = new EventFactoryExtensions.EventBuild();
            builder.Entity = new Source(aggregateId, typeof(TAggregate));
            return builder;
        }
    }
}