using Xunit;
using Azure.Messaging.ServiceBus;
using Moq;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Configuration;
using SourceFlow.Cloud.Azure.Messaging.Events;
using SourceFlow.Cloud.Azure.Observability;
using SourceFlow.Observability;
using SourceFlow.Messaging.Events;
using SourceFlow.Messaging;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;

namespace SourceFlow.Cloud.Azure.Tests.Unit;

public class AzureServiceBusEventDispatcherTests
{
    private readonly Mock<ServiceBusClient> _mockServiceBusClient;
    private readonly Mock<IAzureEventRoutingConfiguration> _mockRoutingConfig;
    private readonly Mock<ILogger<AzureServiceBusEventDispatcher>> _mockLogger;
    private readonly Mock<IDomainTelemetryService> _mockTelemetry;
    private readonly Mock<ServiceBusSender> _mockSender;

    public AzureServiceBusEventDispatcherTests()
    {
        _mockServiceBusClient = new Mock<ServiceBusClient>();
        _mockRoutingConfig = new Mock<IAzureEventRoutingConfiguration>();
        _mockLogger = new Mock<ILogger<AzureServiceBusEventDispatcher>>();
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
        var dispatcher = new AzureServiceBusEventDispatcher(
            _mockServiceBusClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);

        var testEvent = new TestEvent { Name = "TestEvent", Payload = new TestEntity { Id = 1 }, Metadata = new TestEventMetadata() };

        _mockRoutingConfig
            .Setup(x => x.ShouldRouteToAzure<TestEvent>())
            .Returns(false);

        // Act
        await dispatcher.Dispatch(testEvent);

        // Assert
        _mockSender.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispatch_WhenRouteToAzureTrue_ShouldSendMessage()
    {
        // Arrange
        var dispatcher = new AzureServiceBusEventDispatcher(
            _mockServiceBusClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);

        var testEvent = new TestEvent { Name = "TestEvent", Payload = new TestEntity { Id = 1 }, Metadata = new TestEventMetadata() };

        _mockRoutingConfig
            .Setup(x => x.ShouldRouteToAzure<TestEvent>())
            .Returns(true);
        _mockRoutingConfig
            .Setup(x => x.GetTopicName<TestEvent>())
            .Returns("test-topic");

        // Act
        await dispatcher.Dispatch(testEvent);

        // Assert
        _mockSender.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_WhenSuccessful_ShouldSendMessageToTopic()
    {
        // Arrange
        var dispatcher = new AzureServiceBusEventDispatcher(
            _mockServiceBusClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);

        var testEvent = new TestEvent { Name = "TestEvent", Payload = new TestEntity { Id = 1 }, Metadata = new TestEventMetadata() };
        var topicName = "test-topic";

        _mockRoutingConfig
            .Setup(x => x.ShouldRouteToAzure<TestEvent>())
            .Returns(true);
        _mockRoutingConfig
            .Setup(x => x.GetTopicName<TestEvent>())
            .Returns(topicName);

        // Act
        await dispatcher.Dispatch(testEvent);

        // Assert - verify sender was created for correct topic
        _mockServiceBusClient.Verify(x => x.CreateSender(topicName), Times.Once);
    }

    [Fact]
    public async Task Dispatch_WhenRouteToAzureTrue_ShouldSetCorrectMessageProperties()
    {
        // Arrange
        var dispatcher = new AzureServiceBusEventDispatcher(
            _mockServiceBusClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);

        var testEvent = new TestEvent { Name = "TestEvent", Payload = new TestEntity { Id = 1 }, Metadata = new TestEventMetadata() };

        _mockRoutingConfig
            .Setup(x => x.ShouldRouteToAzure<TestEvent>())
            .Returns(true);
        _mockRoutingConfig
            .Setup(x => x.GetTopicName<TestEvent>())
            .Returns("test-topic");

        ServiceBusMessage? capturedMessage = null;
        _mockSender
            .Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, ct) => capturedMessage = msg);

        // Act
        await dispatcher.Dispatch(testEvent);

        // Assert
        Assert.NotNull(capturedMessage);
        Assert.Equal("application/json", capturedMessage.ContentType);
        Assert.Equal("TestEvent", capturedMessage.Subject);
        Assert.True(capturedMessage.ApplicationProperties.ContainsKey("EventType"));
        Assert.True(capturedMessage.ApplicationProperties.ContainsKey("EventName"));
        Assert.True(capturedMessage.ApplicationProperties.ContainsKey("SequenceNo"));
    }
}