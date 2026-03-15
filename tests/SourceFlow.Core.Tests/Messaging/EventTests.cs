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
    [Category("Unit")]
    public class EventTests
    {
        [Test]
        public void Constructor_InitializesProperties()
        {
            var entity = new DummyEntity { Id = 42 };
            var @event = new DummyEvent(entity);
            Assert.IsNotNull(@event.Metadata);
            Assert.That(@event.Name, Is.EqualTo("DummyEvent"));
        }
    }

}
