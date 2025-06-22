using NUnit.Framework;
using System;

namespace SourceFlow.Core.Tests
{
    [TestFixture]
    public class BaseEventTests
    {
        private class TestEvent : BaseEvent
        {
            public override string EventType => nameof(TestEvent);
            public string Data { get; init; } = string.Empty;
        }

        [Test]
        public void BaseEvent_ShouldHaveUniqueEventId()
        {
            // Arrange & Act
            var event1 = new TestEvent { Data = "Test1" };
            var event2 = new TestEvent { Data = "Test2" };

            // Assert
            Assert.That(event1.EventId, Is.Not.EqualTo(event2.EventId));
            Assert.That(event1.EventId, Is.Not.EqualTo(Guid.Empty));
        }

        [Test]
        public void BaseEvent_ShouldHaveTimestamp()
        {
            // Arrange
            var beforeCreation = DateTime.UtcNow;

            // Act
            var @event = new TestEvent();
            var afterCreation = DateTime.UtcNow;

            // Assert
            Assert.That(@event.Timestamp, Is.GreaterThanOrEqualTo(beforeCreation));
            Assert.That(@event.Timestamp, Is.LessThanOrEqualTo(afterCreation));
        }

        [Test]
        public void BaseEvent_ShouldHaveDefaultVersion()
        {
            // Arrange & Act
            var @event = new TestEvent();

            // Assert
            Assert.That(@event.Version, Is.EqualTo(1));
        }
    }
}