using SourceFlow.Messaging.Events;

namespace SourceFlow.Core.Tests.Messaging
{
    public class DummyEntity : IEntity
    {
        public int Id { get; set; }
    }

    public class DummyEvent : Event<DummyEntity>
    {
        public DummyEvent(DummyEntity payload) : base(payload)
        {
        }
    }

    [TestFixture]
    public class EventTests
    {
        [Test]
        public void Constructor_InitializesProperties()
        {
            var payload = new DummyEntity { Id = 99 };
            var ev = new DummyEvent(payload);
            Assert.IsNotNull(ev.Metadata);
            Assert.That(ev.Name, Is.EqualTo("DummyEvent"));
            Assert.That(ev.Payload, Is.SameAs(payload));
        }

        [Test]
        public void IEventPayload_GetSet_WorksCorrectly()
        {
            var payload = new DummyEntity { Id = 123 };
            var ev = new DummyEvent(new DummyEntity());
            ((IEvent)ev).Payload = payload;
            Assert.That(ev.Payload, Is.SameAs(payload));
            Assert.That(((IEvent)ev).Payload, Is.SameAs(payload));
        }
    }
}
