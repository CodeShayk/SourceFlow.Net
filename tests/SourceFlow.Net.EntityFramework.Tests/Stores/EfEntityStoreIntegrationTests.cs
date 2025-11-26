using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SourceFlow.Stores.EntityFramework.Extensions;
using SourceFlow.Stores.EntityFramework.Options;
using SourceFlow.Stores.EntityFramework.Services;
using SourceFlow.Stores.EntityFramework.Stores;
using SourceFlow.Stores.EntityFramework.Tests.TestModels;

namespace SourceFlow.Stores.EntityFramework.Tests.Stores
{
    [TestFixture]
    public class EfEntityStoreIntegrationTests
    {
        private ServiceProvider? _serviceProvider;
        private IEntityStore? _store;
        private EntityDbContext? _context;

        [SetUp]
        public void Setup()
        {
            // Clear any previous registrations
            EntityDbContext.ClearRegistrations();
            ViewModelDbContext.ClearRegistrations();

            // Register the test assembly for scanning
            EntityDbContext.RegisterAssembly(typeof(TestEntity).Assembly);
            ViewModelDbContext.RegisterAssembly(typeof(TestViewModel).Assembly);

            // Create a shared in-memory SQLite connection for all contexts to share the same database
            var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
            connection.Open();

            var services = new ServiceCollection();

            // Configure SQLite in-memory database for testing - using shared connection for all contexts
            // Use EnableServiceProviderCaching(false) to avoid EF Core 9.0 multiple provider conflicts
            services.AddDbContext<EntityDbContext>(options =>
                options.UseSqlite(connection)
                    .EnableServiceProviderCaching(false));

            // Register all contexts for testing (even though only EntityDbContext is used by the store)
            services.AddDbContext<CommandDbContext>(options =>
                options.UseSqlite(connection)
                    .EnableServiceProviderCaching(false));
            services.AddDbContext<ViewModelDbContext>(options =>
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

            _context = _serviceProvider.GetRequiredService<EntityDbContext>();
            _context.Database.EnsureCreated(); // This creates the Entities schema
            _context.ApplyMigrations(); // Apply migrations for registered entity types

            var viewModelContext = _serviceProvider.GetRequiredService<ViewModelDbContext>();
            viewModelContext.Database.EnsureCreated(); // This creates the ViewModels schema
            viewModelContext.ApplyMigrations(); // Apply migrations for registered view model types

            _store = _serviceProvider.GetRequiredService<IEntityStore>();
        }

        [TearDown]
        public void TearDown()
        {
            _context?.Database.CloseConnection();
            _context?.Dispose();
            _serviceProvider?.Dispose();
        }

        [Test]
        public async Task Persist_NewEntity_StoresEntityInDatabase()
        {
            // Arrange
            var entity = new TestEntity
            {
                Id = 1,
                Name = "Test Entity",
                Description = "Test Description",
                Value = 42
            };

            // Act
            await _store!.Persist(entity);

            // Assert
            var retrieved = await _store.Get<TestEntity>(1);
            Assert.That(retrieved, Is.Not.Null);
            Assert.That(retrieved.Id, Is.EqualTo(1));
            Assert.That(retrieved.Name, Is.EqualTo("Test Entity"));
            Assert.That(retrieved.Description, Is.EqualTo("Test Description"));
            Assert.That(retrieved.Value, Is.EqualTo(42));
        }

        [Test]
        public async Task Persist_ExistingEntity_UpdatesEntity()
        {
            // Arrange
            var entity = new TestEntity
            {
                Id = 1,
                Name = "Original Name",
                Description = "Original Description",
                Value = 10
            };

            await _store!.Persist(entity);

            // Act - Update the entity
            entity.Name = "Updated Name";
            entity.Description = "Updated Description";
            entity.Value = 20;
            await _store.Persist(entity);

            // Assert
            var retrieved = await _store.Get<TestEntity>(1);
            Assert.That(retrieved.Name, Is.EqualTo("Updated Name"));
            Assert.That(retrieved.Description, Is.EqualTo("Updated Description"));
            Assert.That(retrieved.Value, Is.EqualTo(20));
        }

        [Test]
        public async Task Get_NonExistentEntity_ThrowsInvalidOperationException()
        {
            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _store!.Get<TestEntity>(999));

            Assert.That(ex!.Message, Does.Contain("not found"));
        }

        [Test]
        public async Task Delete_ExistingEntity_RemovesEntityFromDatabase()
        {
            // Arrange
            var entity = new TestEntity
            {
                Id = 1,
                Name = "Test Entity",
                Description = "Test Description",
                Value = 42
            };

            await _store!.Persist(entity);

            // Act
            await _store.Delete(entity);

            // Assert
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _store.Get<TestEntity>(1));
        }

        [Test]
        public void Delete_NonExistentEntity_ThrowsInvalidOperationException()
        {
            // Arrange
            var entity = new TestEntity { Id = 999, Name = "Non-existent" };

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _store!.Delete(entity));

            Assert.That(ex!.Message, Does.Contain("not found"));
        }

        [Test]
        public void Persist_EntityWithInvalidId_ThrowsArgumentException()
        {
            // Arrange
            var entity = new TestEntity { Id = 0, Name = "Invalid" };

            // Act & Assert
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await _store!.Persist(entity));
        }

        [Test]
        public void Persist_NullEntity_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _store!.Persist<TestEntity>(null!));
        }

        [Test]
        public void Get_InvalidId_ThrowsArgumentException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await _store!.Get<TestEntity>(0));
        }

        [Test]
        public async Task Persist_MultipleEntities_StoresAllEntities()
        {
            // Arrange & Act
            for (int i = 1; i <= 5; i++)
            {
                var entity = new TestEntity
                {
                    Id = i,
                    Name = $"Entity {i}",
                    Description = $"Description {i}",
                    Value = i * 10
                };
                await _store!.Persist(entity);
            }

            // Assert
            for (int i = 1; i <= 5; i++)
            {
                var retrieved = await _store!.Get<TestEntity>(i);
                Assert.That(retrieved.Id, Is.EqualTo(i));
                Assert.That(retrieved.Name, Is.EqualTo($"Entity {i}"));
                Assert.That(retrieved.Value, Is.EqualTo(i * 10));
            }
        }

        [Test]
        public async Task Persist_SameIdDifferentOperations_MaintainsDataIntegrity()
        {
            // Arrange
            var entity = new TestEntity
            {
                Id = 1,
                Name = "First Version",
                Description = "Description 1",
                Value = 100
            };

            // Act - Create
            await _store!.Persist(entity);
            var v1 = await _store.Get<TestEntity>(1);

            // Act - Update
            entity.Name = "Second Version";
            entity.Value = 200;
            await _store.Persist(entity);
            var v2 = await _store.Get<TestEntity>(1);

            // Assert
            Assert.That(v1.Name, Is.EqualTo("First Version"));
            Assert.That(v1.Value, Is.EqualTo(100));
            Assert.That(v2.Name, Is.EqualTo("Second Version"));
            Assert.That(v2.Value, Is.EqualTo(200));
        }
    }
}