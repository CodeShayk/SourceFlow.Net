namespace SourceFlow
{
    public static class EventFactoryExtensions
    {
        public static ICommand Create<TCommand, TPayload>(this CommandBuild builder, TPayload payload)
            where TCommand : class, ICommand<TPayload>, new()
            where TPayload : class, IPayload, new()
        {
            var command = new TCommand
            {
                Entity = builder.Entity,
            };

            AssignPayload(command, payload);

            return command;
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
    }

    public static class Command
    {
        public static EventFactoryExtensions.CommandBuild For<TAggregate>(int aggregateId)
        {
            var builder = new EventFactoryExtensions.CommandBuild();
            builder.Entity = new Source(aggregateId, typeof(TAggregate));
            return builder;
        }
    }
}