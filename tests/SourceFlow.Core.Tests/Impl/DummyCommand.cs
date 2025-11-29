using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Core.Tests.Impl
{
    public class DummyCommand : ICommand
    {
        public DummyCommand()
        {
            Payload = new DummyPayload();
            Name = "DummyCommand";
            Metadata = new Metadata();
            Entity = new EntityRef { Id = 0 };
        }

        public IPayload Payload { get; set; }
        public string Name { get; set; }
        public Metadata Metadata { get; set; }
        public EntityRef Entity { get; set; }
    }

    internal class DummyPayload : IPayload
    {
        public int EntityId { get; set; }
    }
}