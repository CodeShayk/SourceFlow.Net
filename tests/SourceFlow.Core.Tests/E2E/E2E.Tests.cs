using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Core.Tests.E2E.Projections;
using SourceFlow.Core.Tests.E2E.Services;

namespace SourceFlow.Core.Tests.E2E
{
    [TestFixture]
    public class ProgramIntegrationTests
    {
        private ServiceProvider _serviceProvider;
        private IAccountService _accountService;
        private ISaga _saga;
        private ILogger _logger;
        private IViewRepository _viewRepository;

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
            services.UseSourceFlow();

            _serviceProvider = services.BuildServiceProvider();

            _accountService = _serviceProvider.GetRequiredService<IAccountService>();
            _saga = _serviceProvider.GetRequiredService<ISaga>();
            _logger = _serviceProvider.GetRequiredService<ILogger<ProgramIntegrationTests>>();
            _viewRepository = _serviceProvider.GetRequiredService<IViewRepository>();
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
            var accountId = await _accountService.CreateAccountAsync("John Doe", 1000m);
            _logger.LogInformation("Action=Test_Create_Account, Account: {accountId}", accountId);

            // Perform deposit
            var amount = 500m;
            _logger.LogInformation("Action=Test_Deposit, Amount={Amount}", amount);
            await _accountService.DepositAsync(accountId, amount);

            // Perform withdraw
            amount = 200m;
            _logger.LogInformation("Action=Test_Withdraw, Amount={Amount}", amount);
            await _accountService.WithdrawAsync(accountId, amount);

            // Perform another deposit
            amount = 100m;
            _logger.LogInformation("Action=Test_Deposit, Amount={Amount}", amount);
            await _accountService.DepositAsync(accountId, amount);

            // Get current state and assertions
            var account = await _viewRepository.Get<AccountViewModel>(accountId);
            Assert.That(account, Is.Not.Null);
            Assert.That(accountId, Is.EqualTo(account.Id));
            Assert.That("John Doe", Is.EqualTo(account.AccountName));
            Assert.That(1000m + 500m - 200m + 100m, Is.EqualTo(account.CurrentBalance));
            Assert.That(account.TransactionCount, Is.GreaterThanOrEqualTo(3));
            Assert.That(account.IsClosed, Is.False);

            // Replay account history (should not throw)
            Assert.DoesNotThrowAsync(async () => await _accountService.ReplayHistoryAsync(accountId));

            // Fetch state again, should be the same
            var replayedAccount = await _viewRepository.Get<AccountViewModel>(accountId);
            Assert.That(account.CurrentBalance, Is.EqualTo(replayedAccount.CurrentBalance));
            Assert.That(account.TransactionCount, Is.EqualTo(replayedAccount.TransactionCount));

            // Close account
            Assert.DoesNotThrowAsync(async () => await _accountService.CloseAccountAsync(accountId, "Customer account close request"));

            // Final state
            var closedAccount = await _viewRepository.Get<AccountViewModel>(accountId);
            Assert.That(closedAccount.IsClosed, Is.True);
        }
    }
}