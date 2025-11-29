using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Projections;

namespace SourceFlow.Stores.EntityFramework.Tests.TestModels
{
    public class TestEntity : IEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Value { get; set; }
    }

    public class TestPayload : IPayload
    {
        public string Action { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
    }

    public class TestCommand : Command<TestPayload>
    {
        // Parameterless constructor for EF and JSON deserialization
        public TestCommand() : base(false, new TestPayload())
        {
        }

        public TestCommand(int entityId, TestPayload payload) : base(entityId, payload)
        {
        }

        public TestCommand(bool newEntity, TestPayload payload) : base(newEntity, payload)
        {
        }
    }

    public class TestViewModel : IViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
