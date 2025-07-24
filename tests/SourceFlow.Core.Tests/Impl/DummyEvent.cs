using SourceFlow.Aggregate;
using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.Impl
{
    public class DummyEvent : IEvent
    {
        public DummyEvent()
        {
            Payload = new DummyEntity();
            Metadata = new Metadata();
        }

        public IEntity Payload { get; set; }
        public string Name { get; set; } = "TestEvent";
        public Metadata Metadata { get; set; }
    }

    public class DummyEntity : IEntity
    {
        public int Id { get; set; } = 1;
    }
}