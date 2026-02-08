using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.AWS.Messaging.Commands;
using SourceFlow.Cloud.AWS.Observability;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

public class AwsSqsCommandDispatcherTests
{
    private readonly Mock<IAmazonSQS> _mockSqsClient;
    private readonly Mock<IAwsCommandRoutingConfiguration> _mockRoutingConfig;
    private readonly Mock<ILogger<AwsSqsCommandDispatcher>> _mockLogger;
    private readonly Mock<IDomainTelemetryService> _mockTelemetry;
    private readonly AwsSqsCommandDispatcher _dispatcher;

    public AwsSqsCommandDispatcherTests()
    {
        _mockSqsClient = new Mock<IAmazonSQS>();
        _mockRoutingConfig = new Mock<IAwsCommandRoutingConfiguration>();
        _mockLogger = new Mock<ILogger<AwsSqsCommandDispatcher>>();
        _mockTelemetry = new Mock<IDomainTelemetryService>();

        _dispatcher = new AwsSqsCommandDispatcher(
            _mockSqsClient.Object,
            _mockRoutingConfig.Object,
            _mockLogger.Object,
            _mockTelemetry.Object);
    }

    [Fact]
    public async Task Dispatch_WhenRouteToAwsIsFalse_ShouldNotSendMessage()
    {
        // Arrange
        var command = new TestCommand();
        _mockRoutingConfig.Setup(x => x.ShouldRouteToAws<TestCommand>()).Returns(false);

        // Act
        await _dispatcher.Dispatch(command);

        // Assert
        _mockSqsClient.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task Dispatch_WhenRouteToAwsIsTrue_ShouldSendMessageWithCorrectAttributes()
    {
        // Arrange
        var command = new TestCommand();
        var queueUrl = "https://sqs.us-east-1.amazonaws.com/123456/test-queue";

        _mockRoutingConfig.Setup(x => x.ShouldRouteToAws<TestCommand>()).Returns(true);
        _mockRoutingConfig.Setup(x => x.GetQueueUrl<TestCommand>()).Returns(queueUrl);

        _mockSqsClient.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), default))
            .ReturnsAsync(new SendMessageResponse());

        // Act
        await _dispatcher.Dispatch(command);

        // Assert
        _mockSqsClient.Verify(x => x.SendMessageAsync(
            It.Is<SendMessageRequest>(r =>
                r.QueueUrl == queueUrl &&
                r.MessageAttributes.ContainsKey("CommandType") &&
                r.MessageAttributes.ContainsKey("EntityId") &&
                r.MessageAttributes.ContainsKey("SequenceNo") &&
                r.MessageGroupId != null),
            default), Times.Once);
    }

    [Fact]
    public async Task Dispatch_WhenSuccessful_ShouldCallSqsClient()
    {
        // Arrange
        var command = new TestCommand();
        var queueUrl = "https://sqs.us-east-1.amazonaws.com/123456/test-queue";

        _mockRoutingConfig.Setup(x => x.ShouldRouteToAws<TestCommand>()).Returns(true);
        _mockRoutingConfig.Setup(x => x.GetQueueUrl<TestCommand>()).Returns(queueUrl);

        _mockSqsClient.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), default))
            .ReturnsAsync(new SendMessageResponse());

        // Act
        await _dispatcher.Dispatch(command);

        // Assert - verify message was sent
        _mockSqsClient.Verify(x => x.SendMessageAsync(
            It.Is<SendMessageRequest>(r => r.QueueUrl == queueUrl),
            default), Times.Once);
    }

    [Fact]
    public async Task Dispatch_WhenSqsClientThrowsException_ShouldPropagate()
    {
        // Arrange
        var command = new TestCommand();
        var queueUrl = "https://sqs.us-east-1.amazonaws.com/123456/test-queue";

        _mockRoutingConfig.Setup(x => x.ShouldRouteToAws<TestCommand>()).Returns(true);
        _mockRoutingConfig.Setup(x => x.GetQueueUrl<TestCommand>()).Returns(queueUrl);

        _mockSqsClient.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), default))
            .ThrowsAsync(new Exception("SQS error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(async () => await _dispatcher.Dispatch(command));
    }
}
