using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Cloud.AWS.Messaging.Events;
using SourceFlow.Cloud.AWS.Observability;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

[Trait("Category", "Unit")]
public class AwsSnsEventDispatcherTests
{
    private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
    private readonly Mock<IEventRoutingConfiguration> _mockRoutingConfig;
    private readonly Mock<ILogger<AwsSnsEventDispatcher>> _mockLogger;
    private readonly Mock<IDomainTelemetryService> _mockTelemetry;
    private readonly AwsSnsEventDispatcher _dispatcher;

    public AwsSnsEventDispatcherTests()
    {
        _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        _mockRoutingConfig = new Mock<IEventRoutingConfiguration>();
        _mockLogger = new Mock<ILogger<AwsSnsEventDispatcher>>();
        _mockTelemetry = new Mock<IDomainTelemetryService>();

        _dispatcher = new AwsSnsEventDispatcher(
            _mockSnsClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);
    }

    [Fact]
    public async Task Dispatch_WhenRouteToAwsIsFalse_ShouldNotPublishMessage()
    {
        // Arrange
        var @event = new TestEvent();
        _mockRoutingConfig.Setup(x => x.ShouldRoute<TestEvent>()).Returns(false);

        // Act
        await _dispatcher.Dispatch(@event);

        // Assert
        _mockSnsClient.Verify(x => x.PublishAsync(It.IsAny<PublishRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task Dispatch_WhenRouteToAwsIsTrue_ShouldPublishMessageWithCorrectAttributes()
    {
        // Arrange
        var @event = new TestEvent();
        var topicArn = "arn:aws:sns:us-east-1:123456:test-topic";

        _mockRoutingConfig.Setup(x => x.ShouldRoute<TestEvent>()).Returns(true);
        _mockRoutingConfig.Setup(x => x.GetTopicName<TestEvent>()).Returns(topicArn);

        _mockSnsClient.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), default))
            .ReturnsAsync(new PublishResponse { MessageId = "msg-123" });

        // Act
        await _dispatcher.Dispatch(@event);

        // Assert
        _mockSnsClient.Verify(x => x.PublishAsync(
            It.Is<PublishRequest>(r =>
                r.TopicArn == topicArn &&
                r.MessageAttributes.ContainsKey("EventType") &&
                r.MessageAttributes.ContainsKey("EventName") &&
                r.Subject == @event.Name),
            default), Times.Once);
    }

    [Fact]
    public async Task Dispatch_WhenSuccessful_ShouldCallSnsClient()
    {
        // Arrange
        var @event = new TestEvent();
        var topicArn = "arn:aws:sns:us-east-1:123456:test-topic";

        _mockRoutingConfig.Setup(x => x.ShouldRoute<TestEvent>()).Returns(true);
        _mockRoutingConfig.Setup(x => x.GetTopicName<TestEvent>()).Returns(topicArn);

        _mockSnsClient.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), default))
            .ReturnsAsync(new PublishResponse { MessageId = "msg-123" });

        // Act
        await _dispatcher.Dispatch(@event);

        // Assert - verify message was published
        _mockSnsClient.Verify(x => x.PublishAsync(
            It.Is<PublishRequest>(r => r.TopicArn == topicArn),
            default), Times.Once);
    }

    [Fact]
    public async Task Dispatch_WhenSnsClientThrowsException_ShouldPropagate()
    {
        // Arrange
        var @event = new TestEvent();
        var topicArn = "arn:aws:sns:us-east-1:123456:test-topic";

        _mockRoutingConfig.Setup(x => x.ShouldRoute<TestEvent>()).Returns(true);
        _mockRoutingConfig.Setup(x => x.GetTopicName<TestEvent>()).Returns(topicArn);

        _mockSnsClient.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), default))
            .ThrowsAsync(new Exception("SNS error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(async () => await _dispatcher.Dispatch(@event));
    }
}
