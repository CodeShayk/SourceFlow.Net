using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SourceFlow.Saga;
using SourceFlow.Stores.EntityFramework;
using SourceFlow.Stores.EntityFramework.Extensions;
using SourceFlow.Stores.EntityFramework.Tests.E2E.Aggregates;
using SourceFlow.Stores.EntityFramework.Tests.E2E.Projections;

namespace SourceFlow.Stores.EntityFramework.Tests.E2E
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
            // Clear any previous registrations
            EntityDbContext.ClearRegistrations();
            ViewModelDbContext.ClearRegistrations();

            // Register the test assembly for scanning
            EntityDbContext.RegisterAssembly(typeof(BankAccount).Assembly);
            ViewModelDbContext.RegisterAssembly(typeof(AccountViewModel).Assembly);

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
            // Register EF store implementations with SQLite with sensitive data logging.
            services.AddSourceFlowEfStoresWithCustomProvider(options =>
                options.UseSqlite("DataSource=sourceflow.db")
                       .EnableSensitiveDataLogging()
                       .LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information));

            _serviceProvider = services.BuildServiceProvider();

            // Ensure all database schemas are created fresh for each test
            // Delete the database once (all contexts share the same database file)
            var commandContext = _serviceProvider.GetRequiredService<CommandDbContext>();
            commandContext.Database.EnsureDeleted();

            // Create all tables for each context
            commandContext.Database.EnsureCreated();

            var entityContext = _serviceProvider.GetRequiredService<EntityDbContext>();
            entityContext.Database.EnsureCreated();
            entityContext.ApplyMigrations(); // On migrations for registered entity types

            var viewModelContext = _serviceProvider.GetRequiredService<ViewModelDbContext>();
            viewModelContext.Database.EnsureCreated();
            viewModelContext.ApplyMigrations(); // On migrations for registered view model types

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