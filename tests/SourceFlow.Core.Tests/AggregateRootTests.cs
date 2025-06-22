namespace SourceFlow.Core.Tests
{
    using NUnit.Framework;

    // ====================================================================================
    // AGGREGATE ROOT TESTS
    // ====================================================================================

    [TestFixture]
    public class AggregateRootTests
    {
        private class TestAggregate : AggregateRoot
        {
            public string Name { get; private set; } = string.Empty;
            public int Counter { get; private set; }

            public void ChangeName(string name)
            {
                RaiseEvent(new NameChangedEvent { Name = name });
            }

            public void IncrementCounter()
            {
                RaiseEvent(new CounterIncrementedEvent());
            }

            protected override void ApplyEvent(IEvent @event)
            {
                switch (@event)
                {
                    case NameChangedEvent nameChanged:
                        Name = nameChanged.Name;
                        break;

                    case CounterIncrementedEvent:
                        Counter++;
                        break;
                }
            }
        }

        private class NameChangedEvent : BaseEvent
        {
            public override string EventType => nameof(NameChangedEvent);
            public string Name { get; init; } = string.Empty;
        }

        private class CounterIncrementedEvent : BaseEvent
        {
            public override string EventType => nameof(CounterIncrementedEvent);
        }

        [Test]
        public void AggregateRoot_WhenCreated_ShouldHaveZeroVersion()
        {
            // Arrange & Act
            var aggregate = new TestAggregate();

            // Assert
            Assert.That(aggregate.Version, Is.EqualTo(0));
            Assert.That(aggregate.UncommittedEvents, Is.Empty);
        }

        [Test]
        public void AggregateRoot_WhenEventRaised_ShouldIncrementVersion()
        {
            // Arrange
            var aggregate = new TestAggregate();

            // Act
            aggregate.ChangeName("Test");

            // Assert
            Assert.That(aggregate.Version, Is.EqualTo(1));
            Assert.That(aggregate.UncommittedEvents.Count, Is.EqualTo(1));
        }

        [Test]
        public void AggregateRoot_WhenEventRaised_ShouldApplyEventToState()
        {
            // Arrange
            var aggregate = new TestAggregate();

            // Act
            aggregate.ChangeName("TestName");

            // Assert
            Assert.That(aggregate.Name, Is.EqualTo("TestName"));
        }

        [Test]
        public void AggregateRoot_WhenMultipleEventsRaised_ShouldTrackAllEvents()
        {
            // Arrange
            var aggregate = new TestAggregate();

            // Act
            aggregate.ChangeName("Test");
            aggregate.IncrementCounter();
            aggregate.IncrementCounter();

            // Assert
            Assert.That(aggregate.Version, Is.EqualTo(3));
            Assert.That(aggregate.UncommittedEvents.Count, Is.EqualTo(3));
            Assert.That(aggregate.Name, Is.EqualTo("Test"));
            Assert.That(aggregate.Counter, Is.EqualTo(2));
        }

        [Test]
        public void AggregateRoot_WhenEventsMarkedAsCommitted_ShouldClearUncommittedEvents()
        {
            // Arrange
            var aggregate = new TestAggregate();
            aggregate.ChangeName("Test");

            // Act
            aggregate.MarkEventsAsCommitted();

            // Assert
            Assert.That(aggregate.UncommittedEvents, Is.Empty);
            Assert.That(aggregate.Version, Is.EqualTo(1)); // Version should remain unchanged
        }

        [Test]
        public void AggregateRoot_WhenLoadedFromHistory_ShouldReconstructState()
        {
            // Arrange
            var aggregate = new TestAggregate();
            var events = new IEvent[]
            {
            new NameChangedEvent { Name = "TestName" },
            new CounterIncrementedEvent(),
            new CounterIncrementedEvent()
            };

            // Act
            aggregate.LoadFromHistory(events);

            // Assert
            Assert.That(aggregate.Version, Is.EqualTo(3));
            Assert.That(aggregate.Name, Is.EqualTo("TestName"));
            Assert.That(aggregate.Counter, Is.EqualTo(2));
            Assert.That(aggregate.UncommittedEvents, Is.Empty);
        }
    }
}