using Azure.Messaging.ServiceBus;
using Moq;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Messaging.Commands;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.Azure.Tests.Unit;

[Trait("Category", "Unit")]
public class AzureServiceBusCommandDispatcherTests
{
    private readonly Mock<ServiceBusClient> _mockServiceBusClient;
    private readonly Mock<ICommandRoutingConfiguration> _mockRoutingConfig;
    private readonly Mock<ILogger<AzureServiceBusCommandDispatcher>> _mockLogger;
    private readonly Mock<IDomainTelemetryService> _mockTelemetry;
    private readonly Mock<ServiceBusSender> _mockSender;

    public AzureServiceBusCommandDispatcherTests()
    {
        _mockServiceBusClient = new Mock<ServiceBusClient>();
        _mockRoutingConfig = new Mock<ICommandRoutingConfiguration>();
        _mockLogger = new Mock<ILogger<AzureServiceBusCommandDispatcher>>();
        _mockTelemetry = new Mock<IDomainTelemetryService>();
        _mockSender = new Mock<ServiceBusSender>();

        _mockServiceBusClient
            .Setup(x => x.CreateSender(It.IsAny<string>()))
            .Returns(_mockSender.Object);
    }

    [Fact]
    public async Task Dispatch_WhenShouldRouteFalse_ShouldNotSendMessage()
    {
        // Arrange
        var dispatcher = new AzureServiceBusCommandDispatcher(
            _mockServiceBusClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);

        var testCommand = new TestCommand { Entity = new EntityRef { Id = 1 }, Name = "TestCommand", Metadata = new TestCommandMetadata() };

        _mockRoutingConfig
            .Setup(x => x.ShouldRoute<TestCommand>())
            .Returns(false);

        // Act
        await dispatcher.Dispatch(testCommand);

        // Assert
        _mockSender.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispatch_WhenShouldRouteTrue_ShouldSendMessage()
    {
        // Arrange
        var dispatcher = new AzureServiceBusCommandDispatcher(
            _mockServiceBusClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);

        var testCommand = new TestCommand { Entity = new EntityRef { Id = 1 }, Name = "TestCommand", Metadata = new TestCommandMetadata() };

        _mockRoutingConfig
            .Setup(x => x.ShouldRoute<TestCommand>())
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

        var testCommand = new TestCommand { Entity = new EntityRef { Id = 1 }, Name = "TestCommand", Metadata = new TestCommandMetadata() };
        var queueName = "test-queue";

        _mockRoutingConfig
            .Setup(x => x.ShouldRoute<TestCommand>())
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
    public async Task Dispatch_WhenShouldRouteTrue_ShouldSetCorrectMessageProperties()
    {
        // Arrange
        var dispatcher = new AzureServiceBusCommandDispatcher(
            _mockServiceBusClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);

        var testCommand = new TestCommand { Entity = new EntityRef { Id = 1 }, Name = "TestCommand", Metadata = new TestCommandMetadata() };

        _mockRoutingConfig
            .Setup(x => x.ShouldRoute<TestCommand>())
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
}
