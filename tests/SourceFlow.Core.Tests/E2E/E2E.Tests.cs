using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Core.Tests.E2E.Aggregates;
using SourceFlow.Core.Tests.E2E.Projections;
using SourceFlow.Saga;

namespace SourceFlow.Core.Tests.E2E
{
    [TestFixture]
    public class ProgramIntegrationTests
    {
        private ServiceProvider _serviceProvider;
        private IAccountAggregate _accountAggregate;
        private ISaga _saga;
        private ILogger _logger;
        private IViewModelStoreAdapter _viewRepository;
        private int _accountId = 999;

        [SetUp]
        public void SetUp()
        {
            var services = new ServiceCollection();

            // Register logging with console provider
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            // Register SourceFlow and all required services
            // Pass the test assembly so it can discover E2E aggregates, sagas, and projections
            services.UseSourceFlow(Assembly.GetExecutingAssembly());

            _serviceProvider = services.BuildServiceProvider();

            _saga = _serviceProvider.GetRequiredService<ISaga>();
            _accountAggregate = _serviceProvider.GetRequiredService<IAccountAggregate>();
           
            _logger = _serviceProvider.GetRequiredService<ILogger<ProgramIntegrationTests>>();
            _viewRepository = _serviceProvider.GetRequiredService<IViewModelStoreAdapter>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_serviceProvider is not null)
                _serviceProvider.Dispose();
        }

        [Test]
        public async Task EndToEnd_AccountLifecycle_WorksAsExpected()
        {
            // Create account
            await _accountAggregate.CreateAccount(_accountId, "John Doe", 1000m);
            _logger.LogInformation("Action=Test_Create_Account, Account: {accountId}", _accountId);

            // Perform deposit
            var amount = 500m;
            _logger.LogInformation("Action=Test_Deposit, Amount={Amount}", amount);
            await _accountAggregate.Deposit(_accountId, amount);

            // Perform withdraw
            amount = 200m;
            _logger.LogInformation("Action=Test_Withdraw, Amount={Amount}", amount);
            await _accountAggregate.Withdraw(_accountId, amount);

            // Perform another deposit
            amount = 100m;
            _logger.LogInformation("Action=Test_Deposit, Amount={Amount}", amount);
            await _accountAggregate.Deposit(_accountId, amount);

            // Get current state and assertions
            var account = await _viewRepository.Find<AccountViewModel>(_accountId);
            Assert.That(account, Is.Not.Null);
            Assert.That(account.Id, Is.EqualTo(_accountId));
            Assert.That(account.AccountName, Is.EqualTo("John Doe"));
            Assert.That(account.CurrentBalance, Is.EqualTo(1000m + 500m - 200m + 100m));
            Assert.That(account.TransactionCount, Is.GreaterThanOrEqualTo(3));
            Assert.That(account.IsClosed, Is.False);

            // Replay account history (should not throw)
            Assert.DoesNotThrowAsync(async () => await _accountAggregate.RepayHistory(_accountId));

            // Fetch state again, should be the same
            var replayedAccount = await _viewRepository.Find<AccountViewModel>(_accountId);
            Assert.That(account.CurrentBalance, Is.EqualTo(replayedAccount.CurrentBalance));
            Assert.That(account.TransactionCount, Is.EqualTo(replayedAccount.TransactionCount));

            // CloseAccount account
            Assert.DoesNotThrowAsync(async () => await _accountAggregate.CloseAccount(_accountId, "Customer account close request"));

            // Final state
            var closedAccount = await _viewRepository.Find<AccountViewModel>(_accountId);
            Assert.That(closedAccount.IsClosed, Is.True);
        }
    }
}