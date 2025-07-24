using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.Messaging
{
    public class DummyPayload : IPayload
    {
        public int Id { get; set; }
    }

    public class DummyCommand : Command<DummyPayload>
    {
        public DummyCommand(DummyPayload payload) : base(payload)
        {
        }
    }

    [TestFixture]
    public class CommandTests
    {
        [Test]
        public void Constructor_InitializesProperties()
        {
            var payload = new DummyPayload { Id = 42 };
            var command = new DummyCommand(payload);
            Assert.IsNotNull(command.Metadata);
            Assert.AreEqual("DummyCommand", command.Name);
            Assert.AreSame(payload, command.Payload);
        }

        [Test]
        public void ICommandPayload_GetSet_WorksCorrectly()
        {
            var payload = new DummyPayload { Id = 7 };
            var command = new DummyCommand(new DummyPayload());
            ((ICommand)command).Payload = payload;
            Assert.AreSame(payload, command.Payload);
            Assert.AreSame(payload, ((ICommand)command).Payload);
        }
    }
}