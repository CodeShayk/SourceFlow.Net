using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SourceFlow.Projections;
using SourceFlow.Stores.EntityFramework.Extensions;
using SourceFlow.Stores.EntityFramework.Options;
using SourceFlow.Stores.EntityFramework.Services;
using SourceFlow.Stores.EntityFramework.Stores;
using SourceFlow.Stores.EntityFramework.Tests.TestModels;

namespace SourceFlow.Stores.EntityFramework.Tests.Stores
{
    [TestFixture]
    public class EfViewModelStoreIntegrationTests
    {
        private ServiceProvider? _serviceProvider;
        private IViewModelStore? _store;
        private ViewModelDbContext? _context;

        [SetUp]
        public void Setup()
        {
            // Clear any previous registrations
            ViewModelDbContext.ClearRegistrations();
            EntityDbContext.ClearRegistrations();

            // Register the test assembly for scanning
            ViewModelDbContext.RegisterAssembly(typeof(TestViewModel).Assembly);
            EntityDbContext.RegisterAssembly(typeof(TestEntity).Assembly);

            // Create a shared in-memory SQLite connection for all contexts to share the same database
            var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
            connection.Open();

            var services = new ServiceCollection();

            // Configure SQLite in-memory database for testing - using shared connection for all contexts
            // Use EnableServiceProviderCaching(false) to avoid EF Core 9.0 multiple provider conflicts
            services.AddDbContext<ViewModelDbContext>(options =>
                options.UseSqlite(connection)
                    .EnableServiceProviderCaching(false));

            // Register all contexts for testing (even though only ViewModelDbContext is used by the store)
            services.AddDbContext<CommandDbContext>(options =>
                options.UseSqlite(connection)
                    .EnableServiceProviderCaching(false));
            services.AddDbContext<EntityDbContext>(options =>
                options.UseSqlite(connection)
                    .EnableServiceProviderCaching(false));

            // Register SourceFlowEfOptions with default settings for tests
            var efOptions = new SourceFlowEfOptions();
            services.AddSingleton(efOptions);

            // Register common services manually (don't use AddSourceFlowEfStores as it would add SQL Server)
            services.AddScoped<IDatabaseResiliencePolicy, DatabaseResiliencePolicy>();
            services.AddScoped<IDatabaseTelemetryService, DatabaseTelemetryService>();
            services.AddScoped<ICommandStore, EfCommandStore>();
            services.AddScoped<IEntityStore, EfEntityStore>();
            services.AddScoped<IViewModelStore, EfViewModelStore>();

            _serviceProvider = services.BuildServiceProvider();

            // Create and open the in-memory database - ensure all contexts schemas are created
            var commandContext = _serviceProvider.GetRequiredService<CommandDbContext>();
            commandContext.Database.EnsureCreated(); // This creates the Commands schema

            var entityContext = _serviceProvider.GetRequiredService<EntityDbContext>();
            entityContext.Database.EnsureCreated(); // This creates the Entities schema
            entityContext.ApplyMigrations(); // Apply migrations for registered entity types

            _context = _serviceProvider.GetRequiredService<ViewModelDbContext>();
            _context.Database.EnsureCreated(); // This creates the ViewModels schema
            _context.ApplyMigrations(); // Apply migrations for registered view model types

            _store = _serviceProvider.GetRequiredService<IViewModelStore>();
        }

        [TearDown]
        public void TearDown()
        {
            _context?.Database.CloseConnection();
            _context?.Dispose();
            _serviceProvider?.Dispose();
        }

        [Test]
        public async Task Persist_NewViewModel_StoresViewModelInDatabase()
        {
            // Arrange
            var viewModel = new TestViewModel
            {
                Id = 1,
                Name = "Test ViewModel",
                Data = "Test Data",
                Count = 42
            };

            // Act
            await _store!.Persist(viewModel);

            // Assert
            var retrieved = await _store.Get<TestViewModel>(1);
            Assert.That(retrieved, Is.Not.Null);
            Assert.That(retrieved.Id, Is.EqualTo(1));
            Assert.That(retrieved.Name, Is.EqualTo("Test ViewModel"));
            Assert.That(retrieved.Data, Is.EqualTo("Test Data"));
            Assert.That(retrieved.Count, Is.EqualTo(42));
        }

        [Test]
        public async Task Persist_ExistingViewModel_UpdatesViewModel()
        {
            // Arrange
            var viewModel = new TestViewModel
            {
                Id = 1,
                Name = "Original Name",
                Data = "Original Data",
                Count = 10
            };

            await _store!.Persist(viewModel);

            // Act - Update the view model
            viewModel.Name = "Updated Name";
            viewModel.Data = "Updated Data";
            viewModel.Count = 20;
            await _store.Persist(viewModel);

            // Assert
            var retrieved = await _store.Get<TestViewModel>(1);
            Assert.That(retrieved.Name, Is.EqualTo("Updated Name"));
            Assert.That(retrieved.Data, Is.EqualTo("Updated Data"));
            Assert.That(retrieved.Count, Is.EqualTo(20));
        }

        [Test]
        public async Task Get_NonExistentViewModel_ThrowsInvalidOperationException()
        {
            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _store!.Get<TestViewModel>(999));

            Assert.That(ex!.Message, Does.Contain("not found"));
        }

        [Test]
        public async Task Delete_ExistingViewModel_RemovesViewModelFromDatabase()
        {
            // Arrange
            var viewModel = new TestViewModel
            {
                Id = 1,
                Name = "Test ViewModel",
                Data = "Test Data",
                Count = 42
            };

            await _store!.Persist(viewModel);

            // Act
            await _store.Delete(viewModel);

            // Assert
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _store.Get<TestViewModel>(1));
        }

        [Test]
        public void Delete_NonExistentViewModel_ThrowsInvalidOperationException()
        {
            // Arrange
            var viewModel = new TestViewModel { Id = 999, Name = "Non-existent" };

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _store!.Delete(viewModel));

            Assert.That(ex!.Message, Does.Contain("not found"));
        }

        [Test]
        public void Persist_ViewModelWithInvalidId_ThrowsArgumentException()
        {
            // Arrange
            var viewModel = new TestViewModel { Id = 0, Name = "Invalid" };

            // Act & Assert
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await _store!.Persist(viewModel));
        }

        [Test]
        public void Persist_NullViewModel_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _store!.Persist<TestViewModel>(null!));
        }

        [Test]
        public void Get_InvalidId_ThrowsArgumentException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await _store!.Get<TestViewModel>(0));
        }

        [Test]
        public async Task Persist_MultipleViewModels_StoresAllViewModels()
        {
            // Arrange & Act
            for (int i = 1; i <= 5; i++)
            {
                var viewModel = new TestViewModel
                {
                    Id = i,
                    Name = $"ViewModel {i}",
                    Data = $"Data {i}",
                    Count = i * 10
                };
                await _store!.Persist(viewModel);
            }

            // Assert
            for (int i = 1; i <= 5; i++)
            {
                var retrieved = await _store!.Get<TestViewModel>(i);
                Assert.That(retrieved.Id, Is.EqualTo(i));
                Assert.That(retrieved.Name, Is.EqualTo($"ViewModel {i}"));
                Assert.That(retrieved.Count, Is.EqualTo(i * 10));
            }
        }

        [Test]
        public async Task Persist_SameIdDifferentOperations_MaintainsDataIntegrity()
        {
            // Arrange
            var viewModel = new TestViewModel
            {
                Id = 1,
                Name = "First Version",
                Data = "Data 1",
                Count = 100
            };

            // Act - Create
            await _store!.Persist(viewModel);
            var v1 = await _store.Get<TestViewModel>(1);

            // Act - Update
            viewModel.Name = "Second Version";
            viewModel.Count = 200;
            await _store.Persist(viewModel);
            var v2 = await _store.Get<TestViewModel>(1);

            // Assert
            Assert.That(v1.Name, Is.EqualTo("First Version"));
            Assert.That(v1.Count, Is.EqualTo(100));
            Assert.That(v2.Name, Is.EqualTo("Second Version"));
            Assert.That(v2.Count, Is.EqualTo(200));
        }
    }
}