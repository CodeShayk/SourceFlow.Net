using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Aggregate;
using SourceFlow.Core.Tests.Impl;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Core.Tests.Aggregates
{
    [TestFixture]
    public class AggregateTests
    {
        private Mock<ICommandPublisher> commandPublisherMock;
        private Mock<ILogger<IAggregate>> loggerMock;
        private Lazy<ICommandPublisher> lazyCommandPublisher;
        private TestAggregate aggregate;

        [SetUp]
        public void Setup()
        {
            commandPublisherMock = new Mock<ICommandPublisher>();
            loggerMock = new Mock<ILogger<IAggregate>>();
            lazyCommandPublisher = new Lazy<ICommandPublisher>(() => commandPublisherMock.Object);
            aggregate = new TestAggregate(lazyCommandPublisher, loggerMock.Object);
        }

        [Test]
        public void Constructor_SetsCommandPublisher()
        {
            // Assert
            Assert.That(aggregate.GetCommandPublisher().Value, Is.EqualTo(commandPublisherMock.Object));
        }

        [Test]
        public void Constructor_SetsLogger()
        {
            // Assert
            Assert.That(aggregate.GetLogger(), Is.EqualTo(loggerMock.Object));
        }

        [Test]
        public async Task ReplayCommands_DelegatesToCommandPublisher()
        {
            // Arrange
            var entityId = 42;

            // Act
            await aggregate.ReplayCommands(entityId);

            // Assert
            commandPublisherMock.Verify(cp => cp.ReplayCommands(entityId), Times.Once);
        }

        [Test]
        public async Task Send_ValidCommand_DelegatesToCommandPublisher()
        {
            // Arrange
            var command = new DummyCommand();

            // Act
            await aggregate.SendCommand(command);

            // Assert
            commandPublisherMock.Verify(cp => cp.Publish(It.IsAny<ICommand>()), Times.Once);
        }

        [Test]
        public async Task Send_NullCommand_ThrowsArgumentNullException()
        {
            // Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await aggregate.SendCommand(null!));
        }

        [Test]
        public async Task Send_NullPayload_PublishesCommand()
        {
            // Arrange
            var command = new DummyCommand { Payload = null! };

            // This should delegate to publisher (no validation in Send method)
            await aggregate.SendCommand(command);

            // Assert
            commandPublisherMock.Verify(cp => cp.Publish(It.IsAny<ICommand>()), Times.Once);
        }

        // Test aggregate concrete implementation
        private class TestAggregate : Aggregate<TestEntity>
        {
            public TestAggregate(Lazy<ICommandPublisher> commandPublisher, ILogger<IAggregate> logger)
                : base(commandPublisher, logger)
            {
            }

            // Expose protected members for testing
            public Lazy<ICommandPublisher> GetCommandPublisher() => commandPublisher;

            public ILogger<IAggregate> GetLogger() => logger;

            // Expose Send method for testing
            public Task SendCommand(ICommand command) => Send(command);
        }

        private class TestEntity : IEntity
        {
            public int Id { get; set; } = 1;
        }
    }
}
