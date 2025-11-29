using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Core.Tests.Messaging
{
    public class DummyPayload : IPayload
    {
        public int EntityId { get; set; }
    }

    public class DummyCommand : Command<DummyPayload>
    {
        public DummyCommand(int entityId, DummyPayload payload) : base(entityId, payload)
        {
        }
    }

    [TestFixture]
    public class CommandTests
    {
        [Test]
        public void Constructor_InitializesProperties()
        {
            var payload = new DummyPayload { EntityId = 42 };
            var command = new DummyCommand(42, payload);
            Assert.IsNotNull(command.Metadata);
            Assert.That(command.Name, Is.EqualTo("DummyCommand"));
            Assert.That(command.Payload, Is.SameAs(payload));
        }

        [Test]
        public void ICommandPayload_GetSet_WorksCorrectly()
        {
            var payload = new DummyPayload { EntityId = 7 };
            var command = new DummyCommand(7, new DummyPayload());
            ((ICommand)command).Payload = payload;
            Assert.That(command.Payload, Is.SameAs(payload));
            Assert.That(((ICommand)command).Payload, Is.SameAs(payload));
        }
    }
}
