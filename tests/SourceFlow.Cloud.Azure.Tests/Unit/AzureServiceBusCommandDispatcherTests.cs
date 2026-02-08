using Xunit;
using Azure.Messaging.ServiceBus;
using Moq;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Configuration;
using SourceFlow.Cloud.Azure.Messaging.Commands;
using SourceFlow.Cloud.Azure.Observability;
using SourceFlow.Observability;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging;

namespace SourceFlow.Cloud.Azure.Tests.Unit;

public class AzureServiceBusCommandDispatcherTests
{
    private readonly Mock<ServiceBusClient> _mockServiceBusClient;
    private readonly Mock<IAzureCommandRoutingConfiguration> _mockRoutingConfig;
    private readonly Mock<ILogger<AzureServiceBusCommandDispatcher>> _mockLogger;
    private readonly Mock<IDomainTelemetryService> _mockTelemetry;
    private readonly Mock<ServiceBusSender> _mockSender;

    public AzureServiceBusCommandDispatcherTests()
    {
        _mockServiceBusClient = new Mock<ServiceBusClient>();
        _mockRoutingConfig = new Mock<IAzureCommandRoutingConfiguration>();
        _mockLogger = new Mock<ILogger<AzureServiceBusCommandDispatcher>>();
        _mockTelemetry = new Mock<IDomainTelemetryService>();
        _mockSender = new Mock<ServiceBusSender>();

        _mockServiceBusClient
            .Setup(x => x.CreateSender(It.IsAny<string>()))
            .Returns(_mockSender.Object);
    }

    [Fact]
    public async Task Dispatch_WhenRouteToAzureFalse_ShouldNotSendMessage()
    {
        // Arrange
        var dispatcher = new AzureServiceBusCommandDispatcher(
            _mockServiceBusClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);

        var testCommand = new TestCommand { Entity = new EntityRef { Id = 1 }, Name = "TestCommand", Metadata = new TestMetadata() };

        _mockRoutingConfig
            .Setup(x => x.ShouldRouteToAzure<TestCommand>())
            .Returns(false);

        // Act
        await dispatcher.Dispatch(testCommand);

        // Assert
        _mockSender.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispatch_WhenRouteToAzureTrue_ShouldSendMessage()
    {
        // Arrange
        var dispatcher = new AzureServiceBusCommandDispatcher(
            _mockServiceBusClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);

        var testCommand = new TestCommand { Entity = new EntityRef { Id = 1 }, Name = "TestCommand", Metadata = new TestMetadata() };

        _mockRoutingConfig
            .Setup(x => x.ShouldRouteToAzure<TestCommand>())
            .Returns(true);
        _mockRoutingConfig
            .Setup(x => x.GetQueueName<TestCommand>())
            .Returns("test-queue");

        // Act
        await dispatcher.Dispatch(testCommand);

        // Assert
        _mockSender.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_WhenSuccessful_ShouldSendMessageToQueue()
    {
        // Arrange
        var dispatcher = new AzureServiceBusCommandDispatcher(
            _mockServiceBusClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);

        var testCommand = new TestCommand { Entity = new EntityRef { Id = 1 }, Name = "TestCommand", Metadata = new TestMetadata() };
        var queueName = "test-queue";

        _mockRoutingConfig
            .Setup(x => x.ShouldRouteToAzure<TestCommand>())
            .Returns(true);
        _mockRoutingConfig
            .Setup(x => x.GetQueueName<TestCommand>())
            .Returns(queueName);

        // Act
        await dispatcher.Dispatch(testCommand);

        // Assert - verify sender was created for correct queue
        _mockServiceBusClient.Verify(x => x.CreateSender(queueName), Times.Once);
    }

    [Fact]
    public async Task Dispatch_WhenRouteToAzureTrue_ShouldSetCorrectMessageProperties()
    {
        // Arrange
        var dispatcher = new AzureServiceBusCommandDispatcher(
            _mockServiceBusClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);

        var testCommand = new TestCommand { Entity = new EntityRef { Id = 1 }, Name = "TestCommand", Metadata = new TestMetadata() };

        _mockRoutingConfig
            .Setup(x => x.ShouldRouteToAzure<TestCommand>())
            .Returns(true);
        _mockRoutingConfig
            .Setup(x => x.GetQueueName<TestCommand>())
            .Returns("test-queue");

        ServiceBusMessage? capturedMessage = null;
        _mockSender
            .Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, ct) => capturedMessage = msg);

        // Act
        await dispatcher.Dispatch(testCommand);

        // Assert
        Assert.NotNull(capturedMessage);
        Assert.Equal("application/json", capturedMessage.ContentType);
        Assert.Equal("TestCommand", capturedMessage.Subject);
        Assert.Equal("1", capturedMessage.SessionId);
        Assert.True(capturedMessage.ApplicationProperties.ContainsKey("CommandType"));
        Assert.True(capturedMessage.ApplicationProperties.ContainsKey("EntityId"));
        Assert.True(capturedMessage.ApplicationProperties.ContainsKey("SequenceNo"));
    }

    // Helper classes for testing
    private class TestCommand : ICommand
    {
        public IPayload Payload { get; set; } = null!;
        public EntityRef Entity { get; set; } = null!;
        public string Name { get; set; } = null!;
        public Metadata Metadata { get; set; } = null!;
    }

    private class TestEntity : IEntity
    {
        public int Id { get; set; }
    }

    private class TestMetadata : Metadata
    {
    }
}