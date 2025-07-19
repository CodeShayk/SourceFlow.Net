using SourceFlow.Aggregate;

namespace SourceFlow.Messaging
{
    public static class MessageExtensions
    {
        public static ICommand Create<TEvent, TPayload>(this CommandBuild builder, TPayload payload)
            where TEvent : class, ICommand<TPayload>, new()
            where TPayload : class, IPayload, new()
        {
            var command = new TEvent
            {
                Entity = builder.Entity,
            };

            AssignPayload(command, payload);

            return command;
        }

        public static TEvent Create<TEvent>(this EventBuild builder)
           where TEvent : class, IEvent, new()
        {
            var @event = new TEvent
            {
                Payload = builder.Entity,
            };

            return @event;
        }

        private static void AssignPayload<TPayload>(ICommand<TPayload> command, TPayload payload)
            where TPayload : class, IPayload, new()
        {
            command.Payload = payload;
        }

        public class CommandBuild
        {
            public Source Entity { get; set; }
            public ICommand Command { get; set; }
        }

        public class EventBuild
        {
            public IEntity Entity { get; set; }
        }
    }

    public static class Command
    {
        public static MessageExtensions.CommandBuild For<TAggregate>(int aggregateId)
        {
            var builder = new MessageExtensions.CommandBuild();
            builder.Entity = new Source(aggregateId, typeof(TAggregate));
            return builder;
        }
    }

    public static class Event
    {
        public static MessageExtensions.EventBuild For<TEntity>(TEntity entity)
            where TEntity : IEntity
        {
            var builder = new MessageExtensions.EventBuild();
            builder.Entity = entity;
            return builder;
        }
    }
}