using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using SourceFlow.Cloud.AWS.Infrastructure;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

[Trait("Category", "Unit")]
public class AwsHealthCheckTests
{
    private readonly Mock<IAmazonSQS> _mockSqsClient;
    private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
    private readonly Mock<ICommandRoutingConfiguration> _mockCommandRoutingConfig;
    private readonly Mock<IEventRoutingConfiguration> _mockEventRoutingConfig;

    public AwsHealthCheckTests()
    {
        _mockSqsClient = new Mock<IAmazonSQS>();
        _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        _mockCommandRoutingConfig = new Mock<ICommandRoutingConfiguration>();
        _mockEventRoutingConfig = new Mock<IEventRoutingConfiguration>();
    }

    [Fact]
    public async Task CheckHealthAsync_SqsAndSnsReachable_ReturnsHealthy()
    {
        // Arrange
        var queueUrl = "https://sqs.us-east-1.amazonaws.com/123456/my-queue";

        _mockCommandRoutingConfig
            .Setup(x => x.GetListeningQueues())
            .Returns(new[] { queueUrl });

        _mockSqsClient
            .Setup(x => x.GetQueueAttributesAsync(queueUrl, It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueAttributesResponse
            {
                Attributes = new Dictionary<string, string> { ["QueueArn"] = "arn:aws:sqs:us-east-1:123456:my-queue" }
            });

        // No listening queues for events → SNS list topics will not be called
        _mockEventRoutingConfig
            .Setup(x => x.GetListeningQueues())
            .Returns(Enumerable.Empty<string>());

        var healthCheck = CreateHealthCheck();

        // Act
        var result = await healthCheck.CheckHealthAsync(CreateContext());

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_SqsGetQueueAttributesThrows_ReturnsUnhealthy()
    {
        // Arrange
        var queueUrl = "https://sqs.us-east-1.amazonaws.com/123456/missing-queue";

        _mockCommandRoutingConfig
            .Setup(x => x.GetListeningQueues())
            .Returns(new[] { queueUrl });

        _mockSqsClient
            .Setup(x => x.GetQueueAttributesAsync(queueUrl, It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new QueueDoesNotExistException("Queue does not exist"));

        _mockEventRoutingConfig
            .Setup(x => x.GetListeningQueues())
            .Returns(Enumerable.Empty<string>());

        var healthCheck = CreateHealthCheck();

        // Act
        var result = await healthCheck.CheckHealthAsync(CreateContext());

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_NoQueuesConfigured_ReturnsHealthy()
    {
        // Arrange: nothing configured — nothing to check
        _mockCommandRoutingConfig
            .Setup(x => x.GetListeningQueues())
            .Returns(Enumerable.Empty<string>());

        _mockEventRoutingConfig
            .Setup(x => x.GetListeningQueues())
            .Returns(Enumerable.Empty<string>());

        var healthCheck = CreateHealthCheck();

        // Act
        var result = await healthCheck.CheckHealthAsync(CreateContext());

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);

        // Neither SQS nor SNS clients were called
        _mockSqsClient.Verify(
            x => x.GetQueueAttributesAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockSnsClient.Verify(
            x => x.ListTopicsAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckHealthAsync_SnsListTopicsThrows_ReturnsUnhealthy()
    {
        // Arrange: no command queues, but event listening queues are configured
        _mockCommandRoutingConfig
            .Setup(x => x.GetListeningQueues())
            .Returns(Enumerable.Empty<string>());

        _mockEventRoutingConfig
            .Setup(x => x.GetListeningQueues())
            .Returns(new[] { "https://sqs.us-east-1.amazonaws.com/123456/events-queue" });

        _mockSnsClient
            .Setup(x => x.ListTopicsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("SNS not reachable"));

        var healthCheck = CreateHealthCheck();

        // Act
        var result = await healthCheck.CheckHealthAsync(CreateContext());

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private AwsHealthCheck CreateHealthCheck() =>
        new AwsHealthCheck(
            _mockSqsClient.Object,
            _mockSnsClient.Object,
            _mockCommandRoutingConfig.Object,
            _mockEventRoutingConfig.Object);

    private static HealthCheckContext CreateContext() =>
        new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "aws",
                Mock.Of<IHealthCheck>(),
                HealthStatus.Unhealthy,
                null)
        };
}
