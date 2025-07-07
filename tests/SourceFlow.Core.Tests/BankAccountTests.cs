namespace SourceFlow.Core.Tests
{
    using NUnit.Framework;
    using SourceFlow.Core.Tests.Aggregates;
    using SourceFlow.Core.Tests.Events;
    using System;
    using System.Linq;

    // ====================================================================================
    // BANK ACCOUNT AGGREGATE TESTS
    // ====================================================================================

    [TestFixture]
    public class BankAccountTests
    {
        [Test]
        public void Create_WithValidParameters_ShouldCreateAccount()
        {
            // Act
            var account = BankAccount.Create("123", "John Doe", 1000);

            // Assert
            Assert.That(account.Id, Is.EqualTo("123"));
            Assert.That(account.AccountHolderName, Is.EqualTo("John Doe"));
            Assert.That(account.Balance, Is.EqualTo(1000));
            Assert.That(account.IsClosed, Is.False);
            Assert.That(account.Version, Is.EqualTo(1));
            Assert.That(account.UncommittedEvents.Count, Is.EqualTo(1));
            Assert.That(account.UncommittedEvents.First(), Is.TypeOf<BankAccountCreated>());
        }

        [Test]
        public void Create_WithEmptyAccountId_ShouldThrowException()
        {
            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() =>
                BankAccount.Create("", "John Doe", 1000));

            Assert.That(ex.ParamName, Is.EqualTo("accountId"));
        }

        [Test]
        public void Create_WithEmptyAccountHolderName_ShouldThrowException()
        {
            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() =>
                BankAccount.Create("123", "", 1000));

            Assert.That(ex.ParamName, Is.EqualTo("accountHolderName"));
        }

        [Test]
        public void Create_WithNegativeInitialBalance_ShouldThrowException()
        {
            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() =>
                BankAccount.Create("123", "John Doe", -100));

            Assert.That(ex.ParamName, Is.EqualTo("initialBalance"));
        }

        [Test]
        public void Deposit_WithValidAmount_ShouldIncreaseBalance()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);
            account.MarkEventsAsCommitted();

            // Act
            account.Deposit(500);

            // Assert
            Assert.That(account.Balance, Is.EqualTo(1500));
            Assert.That(account.Version, Is.EqualTo(2));
            Assert.That(account.UncommittedEvents.Count, Is.EqualTo(1));

            var depositEvent = account.UncommittedEvents.First() as MoneyDeposited;
            Assert.That(depositEvent, Is.Not.Null);
            Assert.That(depositEvent.Amount, Is.EqualTo(500));
            Assert.That(depositEvent.NewBalance, Is.EqualTo(1500));
        }

        [Test]
        public void Deposit_WithZeroAmount_ShouldThrowException()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);

            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() => account.Deposit(0));
            Assert.That(ex.ParamName, Is.EqualTo("amount"));
        }

        [Test]
        public void Deposit_WithNegativeAmount_ShouldThrowException()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);

            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() => account.Deposit(-100));
            Assert.That(ex.ParamName, Is.EqualTo("amount"));
        }

        [Test]
        public void Deposit_OnClosedAccount_ShouldThrowException()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);
            account.Close("Customer request");

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() => account.Deposit(100));
            Assert.That(ex.Message, Does.Contain("closed account"));
        }

        [Test]
        public void Withdraw_WithValidAmount_ShouldDecreaseBalance()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);
            account.MarkEventsAsCommitted();

            // Act
            account.Withdraw(300);

            // Assert
            Assert.That(account.Balance, Is.EqualTo(700));
            Assert.That(account.Version, Is.EqualTo(2));
            Assert.That(account.UncommittedEvents.Count, Is.EqualTo(1));

            var withdrawEvent = account.UncommittedEvents.First() as MoneyWithdrawn;
            Assert.That(withdrawEvent, Is.Not.Null);
            Assert.That(withdrawEvent.Amount, Is.EqualTo(300));
            Assert.That(withdrawEvent.NewBalance, Is.EqualTo(700));
        }

        [Test]
        public void Withdraw_WithAmountGreaterThanBalance_ShouldThrowException()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() => account.Withdraw(1500));
            Assert.That(ex.Message, Does.Contain("Insufficient funds"));
        }

        [Test]
        public void Withdraw_WithZeroAmount_ShouldThrowException()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);

            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() => account.Withdraw(0));
            Assert.That(ex.ParamName, Is.EqualTo("amount"));
        }

        [Test]
        public void Withdraw_OnClosedAccount_ShouldThrowException()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);
            account.Close("Customer request");

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() => account.Withdraw(100));
            Assert.That(ex.Message, Does.Contain("closed account"));
        }

        [Test]
        public void Close_WithValidReason_ShouldCloseAccount()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);
            account.MarkEventsAsCommitted();

            // Act
            account.Close("Customer request");

            // Assert
            Assert.That(account.IsClosed, Is.True);
            Assert.That(account.Version, Is.EqualTo(2));
            Assert.That(account.UncommittedEvents.Count, Is.EqualTo(1));

            var closeEvent = account.UncommittedEvents.First() as AccountClosed;
            Assert.That(closeEvent, Is.Not.Null);
            Assert.That(closeEvent.Reason, Is.EqualTo("Customer request"));
        }

        [Test]
        public void Close_WithEmptyReason_ShouldThrowException()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);

            // Act & Assert
            var ex = Assert.Throws<ArgumentException>(() => account.Close(""));
            Assert.That(ex.ParamName, Is.EqualTo("reason"));
        }

        [Test]
        public void Close_OnAlreadyClosedAccount_ShouldThrowException()
        {
            // Arrange
            var account = BankAccount.Create("123", "John Doe", 1000);
            account.Close("First closure");

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() => account.Close("Second closure"));
            Assert.That(ex.Message, Does.Contain("already closed"));
        }

        [Test]
        public void LoadFromHistory_ShouldReconstructAccountState()
        {
            // Arrange
            var account = new BankAccount();
            var events = new IEvent[]
            {
            new BankAccountCreated { AccountId = "123", AccountHolderName = "John Doe", InitialBalance = 1000 },
            new MoneyDeposited { AccountId = "123", Amount = 500, NewBalance = 1500 },
            new MoneyWithdrawn { AccountId = "123", Amount = 200, NewBalance = 1300 },
            new AccountClosed { AccountId = "123", Reason = "Customer request" }
            };

            // Act
            account.LoadFromHistory(events);

            // Assert
            Assert.That(account.Id, Is.EqualTo("123"));
            Assert.That(account.AccountHolderName, Is.EqualTo("John Doe"));
            Assert.That(account.Balance, Is.EqualTo(1300));
            Assert.That(account.IsClosed, Is.True);
            Assert.That(account.Version, Is.EqualTo(4));
            Assert.That(account.UncommittedEvents, Is.Empty);
        }
    }
}