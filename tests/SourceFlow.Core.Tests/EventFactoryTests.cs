using NUnit.Framework;
using SourceFlow;

namespace SourceFlow.Tests
{
    public class EventFactoryTests
    {
        public class TestEvent : IEvent<TestPayload>
        {
            public Source Entity { get; set; }
            public TestPayload Payload { get; set; }

            public Guid EventId { get; set; }

            public bool IsReplay { get; set; }

            public DateTime OccurredOn { get; set; }

            public int SequenceNo { get; set; }
        }

        public class TestPayload : IEventPayload
        { }

        [Test]
        public void Create_ShouldAssignEntityAndPayload()
        {
            var builder = new EventFactory.EventBuild { Entity = new Source(1, typeof(object)) };
            var payload = new TestPayload();
            var @event = builder.Create<TestEvent, TestPayload>(payload) as TestEvent;
            Assert.That(@event, Is.Not.Null);
            Assert.That(builder.Entity, Is.EqualTo(@event.Entity));
            Assert.That(payload, Is.EqualTo(@event.Payload));
        }
    }
}