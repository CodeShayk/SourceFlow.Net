#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Stores.EntityFramework.Options;
using SourceFlow.Stores.EntityFramework.Services;
using SourceFlow.Stores.EntityFramework.Stores;
using SourceFlow.Stores.EntityFramework.Tests.TestModels;

namespace SourceFlow.Stores.EntityFramework.Tests.Stores
{
    [TestFixture]
    public class EfCommandStoreIntegrationTests
    {
        private ServiceProvider? _serviceProvider;
        private ICommandStore? _store;
        private CommandDbContext? _context;

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

            // Configure and register the specific DbContext instances with shared connection
            // Use EnableServiceProviderCaching(false) to avoid EF Core 9.0 multiple provider conflicts
            services.AddDbContext<CommandDbContext>(options =>
                options.UseSqlite(connection)
                    .EnableServiceProviderCaching(false));

            services.AddDbContext<EntityDbContext>(options =>
                options.UseSqlite(connection)
                    .EnableServiceProviderCaching(false));

            services.AddDbContext<ViewModelDbContext>(options =>
                options.UseSqlite(connection)
                    .EnableServiceProviderCaching(false));

            // Register SourceFlowEfOptions with resilience and observability disabled for tests
            var options = new SourceFlowEfOptions
            {
                Resilience = { Enabled = false },
                Observability = { Enabled = false }
            };
            services.AddSingleton(options);

            // Register required services (resilience policy and telemetry service)
            services.AddScoped<IDatabaseResiliencePolicy, DatabaseResiliencePolicy>();
            services.AddScoped<IDatabaseTelemetryService, DatabaseTelemetryService>();

            // Register the stores that will use these specific DbContext instances
            services.AddScoped<ICommandStore, EfCommandStore>();
            services.AddScoped<IEntityStore, EfEntityStore>();
            services.AddScoped<IViewModelStore, EfViewModelStore>();

            _serviceProvider = services.BuildServiceProvider();

            // Create and open the in-memory database - schema needs to be created for all contexts
            var commandContext = _serviceProvider.GetRequiredService<CommandDbContext>();
            commandContext.Database.EnsureCreated(); // This creates the Commands schema

            var entityContext = _serviceProvider.GetRequiredService<EntityDbContext>();
            entityContext.Database.EnsureCreated(); // This creates the Entities schema
            entityContext.ApplyMigrations(); // On migrations for registered entity types

            var viewModelContext = _serviceProvider.GetRequiredService<ViewModelDbContext>();
            viewModelContext.Database.EnsureCreated(); // This creates the ViewModels schema
            viewModelContext.ApplyMigrations(); // On migrations for registered view model types

            _context = commandContext;
            _store = _serviceProvider.GetRequiredService<ICommandStore>();
        }

        [TearDown]
        public void TearDown()
        {
            _context?.Database.CloseConnection();
            _context?.Dispose();
            _serviceProvider?.Dispose();
        }

        [Test]
        public async Task Append_ValidCommand_StoresCommandInDatabase()
        {
            // Arrange
            var commandData = new CommandData
            {
                EntityId = 1,
                SequenceNo = 1,
                CommandName = "TestCommand",
                CommandType = typeof(TestCommand).AssemblyQualifiedName ?? string.Empty,
                PayloadType = typeof(TestPayload).AssemblyQualifiedName ?? string.Empty,
                PayloadData = System.Text.Json.JsonSerializer.Serialize(new TestPayload { Action = "Create", Data = "Test data" }),
                Metadata = System.Text.Json.JsonSerializer.Serialize(new Metadata { SequenceNo = 1 }),
                Timestamp = DateTime.UtcNow
            };

            // Act
            await _store!.Append(commandData);

            // Assert
            var commands = await _store.Load(1);
            var commandsList = commands.ToList();

            Assert.That(commandsList, Has.Count.EqualTo(1));
            Assert.That(commandsList[0].EntityId, Is.EqualTo(1));
            Assert.That(commandsList[0].SequenceNo, Is.EqualTo(1));
        }

        [Test]
        public async Task Append_MultipleCommands_StoresAllCommandsInOrder()
        {
            // Arrange
            var commandData1 = new CommandData
            {
                EntityId = 1,
                SequenceNo = 1,
                CommandName = "TestCommand",
                CommandType = typeof(TestCommand).AssemblyQualifiedName ?? string.Empty,
                PayloadType = typeof(TestPayload).AssemblyQualifiedName ?? string.Empty,
                PayloadData = System.Text.Json.JsonSerializer.Serialize(new TestPayload { Action = "Create", Data = "First" }),
                Metadata = System.Text.Json.JsonSerializer.Serialize(new Metadata { SequenceNo = 1 }),
                Timestamp = DateTime.UtcNow
            };

            var commandData2 = new CommandData
            {
                EntityId = 1,
                SequenceNo = 2,
                CommandName = "TestCommand",
                CommandType = typeof(TestCommand).AssemblyQualifiedName ?? string.Empty,
                PayloadType = typeof(TestPayload).AssemblyQualifiedName ?? string.Empty,
                PayloadData = System.Text.Json.JsonSerializer.Serialize(new TestPayload { Action = "Update", Data = "Second" }),
                Metadata = System.Text.Json.JsonSerializer.Serialize(new Metadata { SequenceNo = 2 }),
                Timestamp = DateTime.UtcNow
            };

            var commandData3 = new CommandData
            {
                EntityId = 1,
                SequenceNo = 3,
                CommandName = "TestCommand",
                CommandType = typeof(TestCommand).AssemblyQualifiedName ?? string.Empty,
                PayloadType = typeof(TestPayload).AssemblyQualifiedName ?? string.Empty,
                PayloadData = System.Text.Json.JsonSerializer.Serialize(new TestPayload { Action = "Delete", Data = "Third" }),
                Metadata = System.Text.Json.JsonSerializer.Serialize(new Metadata { SequenceNo = 3 }),
                Timestamp = DateTime.UtcNow
            };

            // Act
            await _store!.Append(commandData1);
            await _store.Append(commandData2);
            await _store.Append(commandData3);

            // Assert
            var commands = await _store.Load(1);
            var commandsList = commands.ToList();

            Assert.That(commandsList, Has.Count.EqualTo(3));
            Assert.That(commandsList[0].SequenceNo, Is.EqualTo(1));
            Assert.That(commandsList[1].SequenceNo, Is.EqualTo(2));
            Assert.That(commandsList[2].SequenceNo, Is.EqualTo(3));
        }

        [Test]
        public async Task Load_NonExistentEntity_ReturnsEmptyList()
        {
            // Act
            var commands = await _store!.Load(999);

            // Assert
            Assert.That(commands, Is.Empty);
        }

        [Test]
        public async Task Append_CommandsForDifferentEntities_StoresSeparately()
        {
            // Arrange
            var commandData1 = new CommandData
            {
                EntityId = 1,
                SequenceNo = 1,
                CommandName = "TestCommand",
                CommandType = typeof(TestCommand).AssemblyQualifiedName ?? string.Empty,
                PayloadType = typeof(TestPayload).AssemblyQualifiedName ?? string.Empty,
                PayloadData = System.Text.Json.JsonSerializer.Serialize(new TestPayload { Action = "Create", Data = "Entity 1" }),
                Metadata = System.Text.Json.JsonSerializer.Serialize(new Metadata { SequenceNo = 1 }),
                Timestamp = DateTime.UtcNow
            };

            var commandData2 = new CommandData
            {
                EntityId = 2,
                SequenceNo = 1,
                CommandName = "TestCommand",
                CommandType = typeof(TestCommand).AssemblyQualifiedName ?? string.Empty,
                PayloadType = typeof(TestPayload).AssemblyQualifiedName ?? string.Empty,
                PayloadData = System.Text.Json.JsonSerializer.Serialize(new TestPayload { Action = "Create", Data = "Entity 2" }),
                Metadata = System.Text.Json.JsonSerializer.Serialize(new Metadata { SequenceNo = 1 }),
                Timestamp = DateTime.UtcNow
            };

            // Act
            await _store!.Append(commandData1);
            await _store.Append(commandData2);

            // Assert
            var commands1 = await _store.Load(1);
            var commands2 = await _store.Load(2);

            Assert.That(commands1.Count(), Is.EqualTo(1));
            Assert.That(commands2.Count(), Is.EqualTo(1));
            Assert.That(commands1.First().EntityId, Is.EqualTo(1));
            Assert.That(commands2.First().EntityId, Is.EqualTo(2));
        }

        [Test]
        public void Append_NullCommand_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _store!.Append(null!));
        }

        [Test]
        public async Task Load_AfterMultipleAppends_ReturnsCommandsInCorrectOrder()
        {
            // Arrange
            for (int i = 1; i <= 10; i++)
            {
                var commandData = new CommandData
                {
                    EntityId = 1,
                    SequenceNo = i,
                    CommandName = "TestCommand",
                    CommandType = typeof(TestCommand).AssemblyQualifiedName ?? string.Empty,
                    PayloadType = typeof(TestPayload).AssemblyQualifiedName ?? string.Empty,
                    PayloadData = System.Text.Json.JsonSerializer.Serialize(new TestPayload { Action = $"Action{i}", Data = $"Data{i}" }),
                    Metadata = System.Text.Json.JsonSerializer.Serialize(new Metadata { SequenceNo = i }),
                    Timestamp = DateTime.UtcNow
                };
                await _store!.Append(commandData);
            }

            // Act
            var commands = await _store!.Load(1);
            var commandsList = commands.ToList();

            // Assert
            Assert.That(commandsList, Has.Count.EqualTo(10));
            for (int i = 0; i < 10; i++)
            {
                Assert.That(commandsList[i].SequenceNo, Is.EqualTo(i + 1));
            }
        }
    }
}