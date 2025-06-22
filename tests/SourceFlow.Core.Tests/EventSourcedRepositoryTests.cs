namespace SourceFlow.Core.Tests
{
    using NUnit.Framework;
    using SourceFlow.Core.Impl;
    using SourceFlow.Core.Tests.Aggregates;
    using SourceFlow.Core.Tests.Impl;
    using System.Threading.Tasks;

    // ====================================================================================
    // EVENT SOURCED REPOSITORY TESTS
    // ====================================================================================

    [TestFixture]
    public class EventSourcedRepositoryTests
    {
        private InMemoryEventStore _eventStore;
        private EventSourcedRepository<BankAccount> _repository;

        [SetUp]
        public void SetUp()
        {
            _eventStore = new InMemoryEventStore();
            _repository = new EventSourcedRepository<BankAccount>(_eventStore);
        }

        [Test]
        public async Task GetByIdAsync_WithNonExistentId_ShouldReturnNull()
        {
            // Act
            var result = await _repository.GetByIdAsync("non-existent");

            // Assert
            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task SaveAsync_WithNewAggregate_ShouldSaveEvents()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);

            // Act
            await _repository.SaveAsync(account);

            // Assert
            Assert.That(account.UncommittedEvents, Is.Empty);

            var retrievedAccount = await _repository.GetByIdAsync("123");
            Assert.That(retrievedAccount, Is.Not.Null);
            Assert.That(retrievedAccount.AccountHolderName, Is.EqualTo("John Doe"));
            Assert.That(retrievedAccount.Balance, Is.EqualTo(1000));
        }

        [Test]
        public async Task SaveAsync_WithModifiedAggregate_ShouldSaveOnlyUncommittedEvents()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);
            await _repository.SaveAsync(account);

            // Act - Modify the account
            var retrievedAccount = await _repository.GetByIdAsync("123");
            retrievedAccount.Deposit(500);
            await _repository.SaveAsync(retrievedAccount);

            // Assert
            var finalAccount = await _repository.GetByIdAsync("123");
            Assert.That(finalAccount.Balance, Is.EqualTo(1500));
            Assert.That(finalAccount.Version, Is.EqualTo(2));
        }
    }
}