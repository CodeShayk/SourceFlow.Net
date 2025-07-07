namespace SourceFlow.Core.Tests
{
    using NUnit.Framework;
    using SourceFlow.Core.Tests.Events;
    using SourceFlow.Core.Tests.Projections;
    using System;
    using System.Linq;
    using System.Threading.Tasks;

    // ====================================================================================
    // PROJECTION HANDLER TESTS
    // ====================================================================================

    [TestFixture]
    public class AccountSummaryProjectionHandlerTests
    {
        private AccountSummaryProjectionHandler _handler;

        [SetUp]
        public void SetUp()
        {
            _handler = new AccountSummaryProjectionHandler();
        }

        [Test]
        public async Task HandleAsync_BankAccountCreated_ShouldCreateProjection()
        {
            // Arrange
            var @event = new BankAccountCreated
            {
                AccountId = "123",
                AccountHolderName = "John Doe",
                InitialBalance = 1000,
                Timestamp = DateTime.UtcNow
            };

            // Act
            await _handler.HandleAsync(@event);

            // Assert
            var projection = _handler.GetProjection("123");
            Assert.That(projection, Is.Not.Null);
            Assert.That(projection.AccountId, Is.EqualTo("123"));
            Assert.That(projection.AccountHolderName, Is.EqualTo("John Doe"));
            Assert.That(projection.CurrentBalance, Is.EqualTo(1000));
            Assert.That(projection.TransactionCount, Is.EqualTo(1));
            Assert.That(projection.IsClosed, Is.False);
        }

        [Test]
        public async Task HandleAsync_MoneyDeposited_ShouldUpdateProjection()
        {
            // Arrange
            var createdEvent = new BankAccountCreated
            {
                AccountId = "123",
                AccountHolderName = "John Doe",
                InitialBalance = 1000
            };
            await _handler.HandleAsync(createdEvent);

            var depositEvent = new MoneyDeposited
            {
                AccountId = "123",
                Amount = 500,
                NewBalance = 1500,
                Timestamp = DateTime.UtcNow
            };

            // Act
            await _handler.HandleAsync(depositEvent);

            // Assert
            var projection = _handler.GetProjection("123");
            Assert.That(projection.CurrentBalance, Is.EqualTo(1500));
            Assert.That(projection.TransactionCount, Is.EqualTo(2));
        }

        [Test]
        public async Task HandleAsync_MoneyWithdrawn_ShouldUpdateProjection()
        {
            // Arrange
            var createdEvent = new BankAccountCreated
            {
                AccountId = "123",
                AccountHolderName = "John Doe",
                InitialBalance = 1000
            };
            await _handler.HandleAsync(createdEvent);

            var withdrawEvent = new MoneyWithdrawn
            {
                AccountId = "123",
                Amount = 200,
                NewBalance = 800,
                Timestamp = DateTime.UtcNow
            };

            // Act
            await _handler.HandleAsync(withdrawEvent);

            // Assert
            var projection = _handler.GetProjection("123");
            Assert.That(projection.CurrentBalance, Is.EqualTo(800));
            Assert.That(projection.TransactionCount, Is.EqualTo(2));
        }

        [Test]
        public async Task HandleAsync_AccountClosed_ShouldMarkAsClosed()
        {
            // Arrange
            var createdEvent = new BankAccountCreated
            {
                AccountId = "123",
                AccountHolderName = "John Doe",
                InitialBalance = 1000
            };
            await _handler.HandleAsync(createdEvent);

            var closedEvent = new AccountClosed
            {
                AccountId = "123",
                Reason = "Customer request",
                Timestamp = DateTime.UtcNow
            };

            // Act
            await _handler.HandleAsync(closedEvent);

            // Assert
            var projection = _handler.GetProjection("123");
            Assert.That(projection.IsClosed, Is.True);
            Assert.That(projection.ClosureReason, Is.EqualTo("Customer request"));
        }

        [Test]
        public void GetProjection_WithNonExistentAccount_ShouldReturnNull()
        {
            // Act
            var projection = _handler.GetProjection("non-existent");

            // Assert
            Assert.That(projection, Is.Null);
        }

        [Test]
        public async Task GetAllProjections_ShouldReturnAllProjections()
        {
            // Arrange
            var event1 = new BankAccountCreated { AccountId = "123", AccountHolderName = "John", InitialBalance = 1000 };
            var event2 = new BankAccountCreated { AccountId = "456", AccountHolderName = "Jane", InitialBalance = 2000 };

            await _handler.HandleAsync(event1);
            await _handler.HandleAsync(event2);

            // Act
            var projections = _handler.GetAllProjections();

            // Assert
            Assert.That(projections.Count(), Is.EqualTo(2));
            Assert.That(projections.Any(p => p.AccountId == "123"), Is.True);
            Assert.That(projections.Any(p => p.AccountId == "456"), Is.True);
        }
    }
}