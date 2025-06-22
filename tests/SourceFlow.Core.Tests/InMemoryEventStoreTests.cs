namespace SourceFlow.Core.Tests
{
    using NUnit.Framework;
    using SourceFlow.Core.Tests.Events;
    using SourceFlow.Core.Tests.Impl;
    using System;
    using System.Linq;
    using System.Threading.Tasks;

    // ====================================================================================
    // IN-MEMORY EVENT STORE TESTS
    // ====================================================================================

    [TestFixture]
    public class InMemoryEventStoreTests
    {
        private InMemoryEventStore _eventStore;

        [SetUp]
        public void SetUp()
        {
            _eventStore = new InMemoryEventStore();
        }

        [Test]
        public async Task SaveEventsAsync_WithNewStream_ShouldSaveEvents()
        {
            // Arrange
            var streamId = "test-stream";
            var events = new IEvent[]
            {
            new BankAccountCreated { AccountId = streamId, AccountHolderName = "John", InitialBalance = 1000 }
            };

            // Act
            await _eventStore.SaveEventsAsync(streamId, events, 0);
            var retrievedEvents = await _eventStore.GetEventsAsync(streamId);

            // Assert
            Assert.That(retrievedEvents.Count(), Is.EqualTo(1));
            var savedEvent = retrievedEvents.First() as BankAccountCreated;
            Assert.That(savedEvent, Is.Not.Null);
            Assert.That(savedEvent.AccountId, Is.EqualTo(streamId));
        }

        [Test]
        public async Task SaveEventsAsync_WithWrongExpectedVersion_ShouldThrowException()
        {
            // Arrange
            var streamId = "test-stream";
            var events = new IEvent[]
            {
            new BankAccountCreated { AccountId = streamId, AccountHolderName = "John", InitialBalance = 1000 }
            };
            await _eventStore.SaveEventsAsync(streamId, events, 0);

            var newEvents = new IEvent[]
            {
            new MoneyDeposited { AccountId = streamId, Amount = 100, NewBalance = 1100 }
            };

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                () => _eventStore.SaveEventsAsync(streamId, newEvents, 0)); // Wrong expected version

            Assert.That(ex.Message, Does.Contain("Concurrency conflict"));
        }

        [Test]
        public async Task GetEventsAsync_WithNonExistentStream_ShouldReturnEmpty()
        {
            // Act
            var events = await _eventStore.GetEventsAsync("non-existent");

            // Assert
            Assert.That(events, Is.Empty);
        }

        [Test]
        public async Task GetEventsAsync_WithFromVersion_ShouldReturnEventsFromSpecifiedVersion()
        {
            // Arrange
            var streamId = "test-stream";
            var events = new IEvent[]
            {
            new BankAccountCreated { AccountId = streamId, AccountHolderName = "John", InitialBalance = 1000 },
            new MoneyDeposited { AccountId = streamId, Amount = 100, NewBalance = 1100 },
            new MoneyWithdrawn { AccountId = streamId, Amount = 50, NewBalance = 1050 }
            };
            await _eventStore.SaveEventsAsync(streamId, events, 0);

            // Act
            var retrievedEvents = await _eventStore.GetEventsAsync(streamId, 1);

            // Assert
            Assert.That(retrievedEvents.Count(), Is.EqualTo(2));
            Assert.That(retrievedEvents.First(), Is.TypeOf<MoneyDeposited>());
        }
    }
}