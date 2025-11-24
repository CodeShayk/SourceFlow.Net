using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SourceFlow.Aggregate;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;
using SourceFlow.Projections;
using SourceFlow.Saga;

namespace SourceFlow.Tests.Ioc
{
    // Test implementations for required interfaces
    public class TestRepository : IEntityStoreAdapter
    {
        public Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity
        {
            return Task.FromResult<TEntity>(null);
        }

        public Task Persist<TEntity>(TEntity entity) where TEntity : IEntity
        {
            return Task.CompletedTask;
        }

        public Task Delete<TEntity>(TEntity entity) where TEntity : IEntity
        {
            return Task.CompletedTask;
        }
    }

    public class TestCommandStore : ICommandStoreAdapter
    {
        public Task Append(ICommand command)
        {
            return Task.CompletedTask;
        }

        public Task<IEnumerable<ICommand>> Load(int aggregateId)
        {
            return Task.FromResult<IEnumerable<ICommand>>(new List<ICommand>());
        }

        public Task<int> GetNextSequenceNo(int aggregateId)
        {
            return Task.FromResult(0);
        }
    }

    public class TestViewProvider : IViewModelStoreAdapter
    {
        public Task<TViewModel> Find<TViewModel>(int id) where TViewModel : class, IViewModel
        {
            return Task.FromResult<TViewModel>(null);
        }

        public Task Persist<TViewModel>(TViewModel model) where TViewModel : IViewModel
        {
            return Task.CompletedTask;
        }
    }

    [TestFixture]
    public class IocExtensionsTests
    {
        private ServiceCollection _services;
        private ServiceProvider _serviceProvider;

        [SetUp]
        public void SetUp()
        {
            _services = new ServiceCollection();
            _services.AddLogging(); // Add logging services

            // Register test implementations for required interfaces
            _services.AddSingleton<IEntityStoreAdapter, TestRepository>();
            _services.AddSingleton<ICommandStoreAdapter, TestCommandStore>();
            _services.AddSingleton<IViewModelStoreAdapter, TestViewProvider>();
        }

        [TearDown]
        public void TearDown()
        {
            _serviceProvider?.Dispose();
        }

        [Test]
        public void UseSourceFlow_RegistersMultipleEventSubscribers()
        {
            // Arrange
            // Test implementations already registered in SetUp

            // Act
            _services.UseSourceFlow();

            // Assert
            _serviceProvider = _services.BuildServiceProvider();
            var eventSubscribers = _serviceProvider.GetServices<IEventSubscriber>();

            // Should have at least 2 event subscribers (Aggregate and Projections)
            Assert.That(eventSubscribers, Is.Not.Null);
            Assert.That(eventSubscribers.Count(), Is.GreaterThanOrEqualTo(2),
                "Should have at least 2 event subscribers (Aggregate and Projections)");
        }

        [Test]
        public void UseSourceFlow_RegistersCommandSubscriber()
        {
            // Arrange
            // Test implementations already registered in SetUp

            // Act
            _services.UseSourceFlow();

            // Assert
            _serviceProvider = _services.BuildServiceProvider();
            var commandSubscriber = _serviceProvider.GetService<ICommandSubscriber>();
            
            Assert.That(commandSubscriber, Is.Not.Null, "ICommandSubscriber should be registered");
        }

        [Test]
        public void UseSourceFlow_RegistersCommandDispatcher()
        {
            // Arrange
            // Test implementations already registered in SetUp

            // Act
            _services.UseSourceFlow();

            // Assert
            _serviceProvider = _services.BuildServiceProvider();
            var commandDispatcher = _serviceProvider.GetService<ICommandDispatcher>();
            
            Assert.That(commandDispatcher, Is.Not.Null, "ICommandDispatcher should be registered");
        }

        [Test]
        public void UseSourceFlow_RegistersCommandBus()
        {
            // Arrange
            // Test implementations already registered in SetUp

            // Act
            _services.UseSourceFlow();

            // Assert
            _serviceProvider = _services.BuildServiceProvider();
            var commandBus = _serviceProvider.GetService<ICommandBus>();
            
            Assert.That(commandBus, Is.Not.Null, "ICommandBus should be registered");
        }

        [Test]
        public void UseSourceFlow_RegistersCommandPublisher()
        {
            // Arrange
            // Test implementations already registered in SetUp

            // Act
            _services.UseSourceFlow();

            // Assert
            _serviceProvider = _services.BuildServiceProvider();
            var commandPublisher = _serviceProvider.GetService<ICommandPublisher>();
            
            Assert.That(commandPublisher, Is.Not.Null, "ICommandPublisher should be registered");
        }

        [Test]
        public void UseSourceFlow_RegistersEventDispatcher()
        {
            // Arrange
            // Test implementations already registered in SetUp

            // Act
            _services.UseSourceFlow();

            // Assert
            _serviceProvider = _services.BuildServiceProvider();
            var eventDispatcher = _serviceProvider.GetService<IEventDispatcher>();
            
            Assert.That(eventDispatcher, Is.Not.Null, "IEventDispatcher should be registered");
        }

        [Test]
        public void UseSourceFlow_RegistersEventQueue()
        {
            // Arrange
            // Test implementations already registered in SetUp

            // Act
            _services.UseSourceFlow();

            // Assert
            _serviceProvider = _services.BuildServiceProvider();
            var eventQueue = _serviceProvider.GetService<IEventQueue>();
            
            Assert.That(eventQueue, Is.Not.Null, "IEventQueue should be registered");
        }

        [Test]
        public void UseSourceFlow_RegistersRequiredInfrastructureServices()
        {
            // Arrange
            // Test implementations already registered in SetUp

            // Act
            _services.UseSourceFlow();

            // Assert
            _serviceProvider = _services.BuildServiceProvider();
            
            // Check that all infrastructure services are registered
            Assert.That(_serviceProvider.GetService<IEntityStoreAdapter>(), Is.Not.Null, "IEntityStore should be registered");
            Assert.That(_serviceProvider.GetService<ICommandStoreAdapter>(), Is.Not.Null, "ICommandStore should be registered");
            Assert.That(_serviceProvider.GetService<IViewModelStoreAdapter>(), Is.Not.Null, "IViewModelStore should be registered");
        }

        [Test]
        public void UseSourceFlow_RegistersAggregateFactory()
        {
            // Arrange
            // Test implementations already registered in SetUp

            // Act
            _services.UseSourceFlow();

            // Assert
            _serviceProvider = _services.BuildServiceProvider();
            var aggregateFactory = _serviceProvider.GetService<IAggregateFactory>();
            
            Assert.That(aggregateFactory, Is.Not.Null, "IAggregateFactory should be registered");
        }

        [Test]
        public void UseSourceFlow_RegistersAllServices_WithoutThrowing()
        {
            // Arrange
            // Test implementations already registered in SetUp

            // Act & Assert - should not throw
            Assert.DoesNotThrow(() =>
            {
                _services.UseSourceFlow();
                _serviceProvider = _services.BuildServiceProvider();
                
                // Try to resolve all major services to ensure they can be created
                _ = _serviceProvider.GetService<ICommandSubscriber>();
                _ = _serviceProvider.GetService<ICommandDispatcher>();
                _ = _serviceProvider.GetService<ICommandBus>();
                _ = _serviceProvider.GetService<ICommandPublisher>();
                _ = _serviceProvider.GetService<IEventSubscriber>();
                _ = _serviceProvider.GetService<IEventDispatcher>();
                _ = _serviceProvider.GetService<IEventQueue>();
                _ = _serviceProvider.GetService<Aggregate.EventSubscriber>();
                _ = _serviceProvider.GetService<Projections.EventSubscriber>();
            });
        }

        [Test]
        public void UseSourceFlow_RegistersEventSubscribersAsEnumerable()
        {
            // Arrange
            // Test implementations already registered in SetUp

            // Act
            _services.UseSourceFlow();

            // Assert
            _serviceProvider = _services.BuildServiceProvider();
            var eventSubscribers = _serviceProvider.GetServices<IEventSubscriber>();
            
            Assert.That(eventSubscribers, Is.Not.Null, "IEventSubscriber enumerable should not be null");
            Assert.That(eventSubscribers.Count(), Is.GreaterThanOrEqualTo(2), 
                "Should have at least 2 IEventSubscriber implementations");
        }
    }
}