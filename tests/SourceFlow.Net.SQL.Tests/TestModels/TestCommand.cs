using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Net.SQL.Tests.TestModels
{
    /// <summary>
    /// Test payload for integration tests.
    /// </summary>
    public class TestPayload : IPayload
    {
        public string Action { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
    }

    /// <summary>
    /// Test command for integration tests.
    /// </summary>
    public class TestCommand : Command<TestPayload>
    {
        // Parameterless constructor for JSON deserialization
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
}
