using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Cloud.AWS.Infrastructure;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Cloud.Configuration;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

[Trait("Category", "Unit")]
public class AwsBusBootstrapperTests
{
    private readonly Mock<IAmazonSQS> _mockSqsClient;
    private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
    private readonly Mock<ILogger<AwsBusBootstrapper>> _mockLogger;

    public AwsBusBootstrapperTests()
    {
        _mockSqsClient = new Mock<IAmazonSQS>();
        _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        _mockLogger = new Mock<ILogger<AwsBusBootstrapper>>();
    }

    private BusConfiguration BuildConfig(Action<BusConfigurationBuilder> configure)
    {
        var builder = new BusConfigurationBuilder();
        configure(builder);
        return builder.Build();
    }

    private AwsBusBootstrapper CreateBootstrapper(BusConfiguration config)
    {
        return new AwsBusBootstrapper(
            config,
            _mockSqsClient.Object,
            _mockSnsClient.Object,
            _mockLogger.Object);
    }

    private void SetupQueueResolution(string queueName, string queueUrl)
    {
        _mockSqsClient
            .Setup(x => x.GetQueueUrlAsync(queueName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueUrlResponse { QueueUrl = queueUrl });
    }

    private void SetupQueueArn(string queueUrl, string queueArn)
    {
        _mockSqsClient
            .Setup(x => x.GetQueueAttributesAsync(
                It.Is<GetQueueAttributesRequest>(r => r.QueueUrl == queueUrl),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueAttributesResponse
            {
                Attributes = new Dictionary<string, string>
                {
                    [QueueAttributeName.QueueArn] = queueArn
                }
            });
    }

    private void SetupTopicResolution(string topicName, string topicArn)
    {
        _mockSnsClient
            .Setup(x => x.CreateTopicAsync(topicName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateTopicResponse { TopicArn = topicArn });
    }

    // ── Validation Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_WithSubscribedTopicsButNoCommandQueues_ThrowsInvalidOperationException()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Subscribe.To.Topic("order-events"));

        var bootstrapper = CreateBootstrapper(config);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bootstrapper.StartAsync(CancellationToken.None));

        Assert.Contains("At least one command queue must be configured", ex.Message);
    }

    [Fact]
    public async Task StartAsync_WithNoSubscribedTopicsAndNoCommandQueues_DoesNotThrow()
    {
        // Arrange - only outbound event routing, no subscriptions or command queues
        var config = BuildConfig(bus => bus
            .Raise.Event<TestEvent>(t => t.Topic("order-events")));

        SetupTopicResolution("order-events", "arn:aws:sns:us-east-1:123456:order-events");

        var bootstrapper = CreateBootstrapper(config);

        // Act & Assert - should not throw
        await bootstrapper.StartAsync(CancellationToken.None);
    }

    // ── Subscription Tests ────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_WithSubscribedTopics_SubscribesFirstCommandQueueToEachTopic()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Listen.To
                .CommandQueue("orders.fifo")
            .Subscribe.To
                .Topic("order-events")
                .Topic("payment-events"));

        SetupQueueResolution("orders.fifo", "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo");
        SetupQueueArn("https://sqs.us-east-1.amazonaws.com/123456/orders.fifo",
            "arn:aws:sqs:us-east-1:123456:orders.fifo");
        SetupTopicResolution("order-events", "arn:aws:sns:us-east-1:123456:order-events");
        SetupTopicResolution("payment-events", "arn:aws:sns:us-east-1:123456:payment-events");

        _mockSnsClient
            .Setup(x => x.SubscribeAsync(It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscribeResponse { SubscriptionArn = "arn:aws:sns:us-east-1:123456:sub" });

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert - subscribed both topics to the queue
        _mockSnsClient.Verify(x => x.SubscribeAsync(
            It.Is<SubscribeRequest>(r =>
                r.TopicArn == "arn:aws:sns:us-east-1:123456:order-events" &&
                r.Protocol == "sqs" &&
                r.Endpoint == "arn:aws:sqs:us-east-1:123456:orders.fifo"),
            It.IsAny<CancellationToken>()), Times.Once);

        _mockSnsClient.Verify(x => x.SubscribeAsync(
            It.Is<SubscribeRequest>(r =>
                r.TopicArn == "arn:aws:sns:us-east-1:123456:payment-events" &&
                r.Protocol == "sqs" &&
                r.Endpoint == "arn:aws:sqs:us-east-1:123456:orders.fifo"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_WithMultipleCommandQueues_UsesFirstQueueForSubscriptions()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Listen.To
                .CommandQueue("orders.fifo")
                .CommandQueue("inventory.fifo")
            .Subscribe.To
                .Topic("order-events"));

        SetupQueueResolution("orders.fifo", "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo");
        SetupQueueResolution("inventory.fifo", "https://sqs.us-east-1.amazonaws.com/123456/inventory.fifo");
        SetupQueueArn("https://sqs.us-east-1.amazonaws.com/123456/orders.fifo",
            "arn:aws:sqs:us-east-1:123456:orders.fifo");
        SetupTopicResolution("order-events", "arn:aws:sns:us-east-1:123456:order-events");

        _mockSnsClient
            .Setup(x => x.SubscribeAsync(It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscribeResponse { SubscriptionArn = "arn:aws:sns:us-east-1:123456:sub" });

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert - subscribed to the first queue (orders.fifo), not inventory.fifo
        _mockSnsClient.Verify(x => x.SubscribeAsync(
            It.Is<SubscribeRequest>(r =>
                r.Endpoint == "arn:aws:sqs:us-east-1:123456:orders.fifo"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Should never subscribe inventory queue
        _mockSnsClient.Verify(x => x.SubscribeAsync(
            It.Is<SubscribeRequest>(r =>
                r.Endpoint == "arn:aws:sqs:us-east-1:123456:inventory.fifo"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_WithNoSubscribedTopics_DoesNotCreateAnySubscriptions()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Send.Command<TestCommand>(q => q.Queue("orders.fifo"))
            .Listen.To.CommandQueue("orders.fifo"));

        SetupQueueResolution("orders.fifo", "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo");

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert - no SNS subscriptions created
        _mockSnsClient.Verify(x => x.SubscribeAsync(
            It.IsAny<SubscribeRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Resolve / Event Listening Tests ───────────────────────────────────

    [Fact]
    public async Task StartAsync_WithSubscribedTopics_ResolvesEventListeningUrlToFirstCommandQueue()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Listen.To
                .CommandQueue("orders.fifo")
            .Subscribe.To
                .Topic("order-events"));

        SetupQueueResolution("orders.fifo", "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo");
        SetupQueueArn("https://sqs.us-east-1.amazonaws.com/123456/orders.fifo",
            "arn:aws:sqs:us-east-1:123456:orders.fifo");
        SetupTopicResolution("order-events", "arn:aws:sns:us-east-1:123456:order-events");

        _mockSnsClient
            .Setup(x => x.SubscribeAsync(It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscribeResponse { SubscriptionArn = "arn:aws:sns:us-east-1:123456:sub" });

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert - event listening queues should return the first command queue URL
        var eventRouting = (IEventRoutingConfiguration)config;
        var listeningQueues = eventRouting.GetListeningQueues().ToList();
        Assert.Single(listeningQueues);
        Assert.Equal("https://sqs.us-east-1.amazonaws.com/123456/orders.fifo", listeningQueues[0]);
    }

    [Fact]
    public async Task StartAsync_WithNoSubscribedTopics_ResolvesEmptyEventListeningUrls()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Send.Command<TestCommand>(q => q.Queue("orders.fifo"))
            .Listen.To.CommandQueue("orders.fifo"));

        SetupQueueResolution("orders.fifo", "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo");

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert - no event listening URLs when no topics subscribed
        var eventRouting = (IEventRoutingConfiguration)config;
        var listeningQueues = eventRouting.GetListeningQueues().ToList();
        Assert.Empty(listeningQueues);
    }

    // ── Queue/Topic Resolution Tests ──────────────────────────────────────

    [Fact]
    public async Task StartAsync_CreatesQueueWhenNotFound()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Listen.To.CommandQueue("new-queue.fifo"));

        _mockSqsClient
            .Setup(x => x.GetQueueUrlAsync("new-queue.fifo", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new QueueDoesNotExistException("not found"));

        _mockSqsClient
            .Setup(x => x.CreateQueueAsync(It.IsAny<CreateQueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateQueueResponse
            {
                QueueUrl = "https://sqs.us-east-1.amazonaws.com/123456/new-queue.fifo"
            });

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert - queue was created with FIFO attributes
        _mockSqsClient.Verify(x => x.CreateQueueAsync(
            It.Is<CreateQueueRequest>(r =>
                r.QueueName == "new-queue.fifo" &&
                r.Attributes[QueueAttributeName.FifoQueue] == "true" &&
                r.Attributes[QueueAttributeName.ContentBasedDeduplication] == "true"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_ResolvesCommandRoutesAndListeningQueues()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Send.Command<TestCommand>(q => q.Queue("orders.fifo"))
            .Listen.To.CommandQueue("orders.fifo"));

        SetupQueueResolution("orders.fifo", "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo");

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert
        var commandRouting = (ICommandRoutingConfiguration)config;
        Assert.True(commandRouting.ShouldRoute<TestCommand>());
        Assert.Equal("https://sqs.us-east-1.amazonaws.com/123456/orders.fifo",
            commandRouting.GetQueueName<TestCommand>());

        var listeningQueues = commandRouting.GetListeningQueues().ToList();
        Assert.Single(listeningQueues);
        Assert.Equal("https://sqs.us-east-1.amazonaws.com/123456/orders.fifo", listeningQueues[0]);
    }
}
