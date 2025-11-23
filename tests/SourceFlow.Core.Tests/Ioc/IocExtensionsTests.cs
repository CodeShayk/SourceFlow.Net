using Microsoft.Extensions.DependencyInjection;
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
    [TestFixture]
    public class IocExtensionsTests
    {
        // Note: UseSourceFlow requires implementations of IRepository, ICommandStore, and IViewProvider
        // which are not provided by the framework itself. These must be implemented by the consuming application.
        // Therefore, we cannot test the full registration without providing these implementations.

        [Test]
        public void UseSourceFlow_RegistersMultipleEventSubscribers()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(); // Add logging services

            // Act
            services.UseSourceFlow();

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            var eventSubscribers = serviceProvider.GetServices<IEventSubscriber>();

            // Should have at least 2 event subscribers (Aggregate and Projections)
            Assert.That(eventSubscribers, Is.Not.Null);
            Assert.That(eventSubscribers.Count(), Is.GreaterThanOrEqualTo(2),
                "Should have at least 2 event subscribers (Aggregate and Projections)");
        }
    }
}