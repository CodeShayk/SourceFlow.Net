namespace SourceFlow.Core.Tests
{
    using NUnit.Framework;
    using SourceFlow.Core.Impl;
    using SourceFlow.Core.Tests.Aggregates;
    using SourceFlow.Core.Tests.Impl;
    using SourceFlow.Core.Tests.Services;
    using System;
    using System.Threading.Tasks;

    // ====================================================================================
    // BANK ACCOUNT SERVICE TESTS
    // ====================================================================================

    [TestFixture]
    public class BankAccountServiceTests
    {
        private InMemoryEventStore _eventStore;
        private EventSourcedRepository<BankAccount> _repository;
        private BankAccountService _service;

        [SetUp]
        public void SetUp()
        {
            _eventStore = new InMemoryEventStore();
            _repository = new EventSourcedRepository<BankAccount>(_eventStore);
            _service = new BankAccountService(_repository);
        }

        [Test]
        public async Task CreateAccountAsync_WithValidParameters_ShouldCreateAccount()
        {
            // Act
            var accountId = await _service.CreateAccountAsync("John Doe", 1000);

            // Assert
            Assert.That(accountId, Is.Not.Null);
            Assert.That(accountId, Is.Not.Empty);

            var account = await _service.GetAccountAsync(accountId);
            Assert.That(account, Is.Not.Null);
            Assert.That(account.AccountHolderName, Is.EqualTo("John Doe"));
            Assert.That(account.Balance, Is.EqualTo(1000));
        }

        [Test]
        public async Task DepositAsync_WithValidAccountAndAmount_ShouldIncreaseBalance()
        {
            // Arrange
            var accountId = await _service.CreateAccountAsync("John Doe", 1000);

            // Act
            await _service.DepositAsync(accountId, 500);

            // Assert
            var account = await _service.GetAccountAsync(accountId);
            Assert.That(account.Balance, Is.EqualTo(1500));
        }

        [Test]
        public async Task DepositAsync_WithNonExistentAccount_ShouldThrowException()
        {
            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                () => _service.DepositAsync("non-existent", 100));

            Assert.That(ex.Message, Does.Contain("not found"));
        }

        [Test]
        public async Task WithdrawAsync_WithValidAccountAndAmount_ShouldDecreaseBalance()
        {
            // Arrange
            var accountId = await _service.CreateAccountAsync("John Doe", 1000);

            // Act
            await _service.WithdrawAsync(accountId, 300);

            // Assert
            var account = await _service.GetAccountAsync(accountId);
            Assert.That(account.Balance, Is.EqualTo(700));
        }

        [Test]
        public async Task WithdrawAsync_WithNonExistentAccount_ShouldThrowException()
        {
            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                () => _service.WithdrawAsync("non-existent", 100));

            Assert.That(ex.Message, Does.Contain("not found"));
        }

        [Test]
        public async Task CloseAccountAsync_WithValidAccount_ShouldCloseAccount()
        {
            // Arrange
            var accountId = await _service.CreateAccountAsync("John Doe", 1000);

            // Act
            await _service.CloseAccountAsync(accountId, "Customer request");

            // Assert
            var account = await _service.GetAccountAsync(accountId);
            Assert.That(account.IsClosed, Is.True);
        }

        [Test]
        public async Task CloseAccountAsync_WithNonExistentAccount_ShouldThrowException()
        {
            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                () => _service.CloseAccountAsync("non-existent", "reason"));

            Assert.That(ex.Message, Does.Contain("not found"));
        }

        [Test]
        public async Task GetAccountAsync_WithNonExistentAccount_ShouldReturnNull()
        {
            // Act
            var account = await _service.GetAccountAsync("non-existent");

            // Assert
            Assert.That(account, Is.Null);
        }
    }
}