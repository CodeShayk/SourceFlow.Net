namespace SourceFlow.Core.Tests
{
    using NUnit.Framework;
    using SourceFlow.Core.Impl;
    using SourceFlow.Core.Tests.Aggregates;
    using SourceFlow.Core.Tests.Impl;
    using SourceFlow.Core.Tests.Projections;
    using SourceFlow.Core.Tests.Services;
    using System;
    using System.Linq;
    using System.Threading.Tasks;

    // ====================================================================================
    // INTEGRATION TESTS
    // ====================================================================================

    [TestFixture]
    public class EventSourcingIntegrationTests
    {
        private InMemoryEventStore _eventStore;
        private EventSourcedRepository<BankAccount> _repository;
        private BankAccountService _service;
        private AccountSummaryProjectionHandler _projectionHandler;

        [SetUp]
        public void SetUp()
        {
            _eventStore = new InMemoryEventStore();
            _repository = new EventSourcedRepository<BankAccount>(_eventStore);
            _service = new BankAccountService(_repository);
            _projectionHandler = new AccountSummaryProjectionHandler();
        }

        [Test]
        public async Task FullWorkflow_ShouldWorkEndToEnd()
        {
            // Create account
            var accountId = await _service.CreateAccountAsync("John Doe", 1000);

            // Perform operations
            await _service.DepositAsync(accountId, 500);
            await _service.WithdrawAsync(accountId, 200);
            await _service.DepositAsync(accountId, 100);

            // Verify final state
            var account = await _service.GetAccountAsync(accountId);
            Assert.That(account.Balance, Is.EqualTo(1400));
            Assert.That(account.Version, Is.EqualTo(4));

            // Verify event history
            var events = await _eventStore.GetEventsAsync(accountId);
            Assert.That(events.Count(), Is.EqualTo(4));

            // Update projection with
        }
    }
}