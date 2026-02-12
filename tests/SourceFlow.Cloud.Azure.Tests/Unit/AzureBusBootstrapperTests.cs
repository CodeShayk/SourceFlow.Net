using global::Azure;
using global::Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Moq;
using SourceFlow.Cloud.Azure.Infrastructure;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using SourceFlow.Cloud.Core.Configuration;

namespace SourceFlow.Cloud.Azure.Tests.Unit;

public class AzureBusBootstrapperTests
{
    private readonly Mock<ServiceBusAdministrationClient> _mockAdminClient;
    private readonly Mock<ILogger<AzureBusBootstrapper>> _mockLogger;

    public AzureBusBootstrapperTests()
    {
        _mockAdminClient = new Mock<ServiceBusAdministrationClient>();
        _mockLogger = new Mock<ILogger<AzureBusBootstrapper>>();
    }

    private BusConfiguration BuildConfig(Action<BusConfigurationBuilder> configure)
    {
        var builder = new BusConfigurationBuilder();
        configure(builder);
        return builder.Build();
    }

    private AzureBusBootstrapper CreateBootstrapper(BusConfiguration config)
    {
        return new AzureBusBootstrapper(
            config,
            _mockAdminClient.Object,
            _mockLogger.Object);
    }

    private void SetupQueueExists(string queueName, bool exists)
    {
        _mockAdminClient
            .Setup(x => x.QueueExistsAsync(queueName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(global::Azure.Response.FromValue(exists, null!));
    }

    private void SetupTopicExists(string topicName, bool exists)
    {
        _mockAdminClient
            .Setup(x => x.TopicExistsAsync(topicName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(global::Azure.Response.FromValue(exists, null!));
    }

    private void SetupSubscriptionExists(string topicName, string subscriptionName, bool exists)
    {
        _mockAdminClient
            .Setup(x => x.SubscriptionExistsAsync(topicName, subscriptionName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(global::Azure.Response.FromValue(exists, null!));
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
        // Arrange - only outbound event routing
        var config = BuildConfig(bus => bus
            .Raise.Event<TestEvent>(t => t.Topic("order-events")));

        SetupTopicExists("order-events", false);
        _mockAdminClient
            .Setup(x => x.CreateTopicAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((global::Azure.Response<TopicProperties>)null!);

        var bootstrapper = CreateBootstrapper(config);

        // Act & Assert - should not throw
        await bootstrapper.StartAsync(CancellationToken.None);
    }

    // ── Queue Creation Tests ──────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_CreatesQueueWhenNotExists()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Listen.To.CommandQueue("orders"));

        SetupQueueExists("orders", false);
        _mockAdminClient
            .Setup(x => x.CreateQueueAsync(It.IsAny<CreateQueueOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((global::Azure.Response<QueueProperties>)null!);

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert
        _mockAdminClient.Verify(x => x.CreateQueueAsync(
            It.Is<CreateQueueOptions>(o => o.Name == "orders"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_SkipsQueueCreationWhenExists()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Listen.To.CommandQueue("orders"));

        SetupQueueExists("orders", true);

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert - should not create
        _mockAdminClient.Verify(x => x.CreateQueueAsync(
            It.IsAny<CreateQueueOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Topic Creation Tests ──────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_CreatesTopicWhenNotExists()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Raise.Event<TestEvent>(t => t.Topic("order-events")));

        SetupTopicExists("order-events", false);
        _mockAdminClient
            .Setup(x => x.CreateTopicAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((global::Azure.Response<TopicProperties>)null!);

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert
        _mockAdminClient.Verify(x => x.CreateTopicAsync(
            "order-events",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Subscription Tests ────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_WithSubscribedTopics_CreatesSubscriptionForwardingToFirstQueue()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Listen.To.CommandQueue("orders")
            .Subscribe.To
                .Topic("order-events")
                .Topic("payment-events"));

        SetupQueueExists("orders", true);
        SetupTopicExists("order-events", true);
        SetupTopicExists("payment-events", true);
        SetupSubscriptionExists("order-events", "fwd-to-orders", false);
        SetupSubscriptionExists("payment-events", "fwd-to-orders", false);

        _mockAdminClient
            .Setup(x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((global::Azure.Response<SubscriptionProperties>)null!);

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert - both topics get subscriptions forwarding to "orders"
        _mockAdminClient.Verify(x => x.CreateSubscriptionAsync(
            It.Is<CreateSubscriptionOptions>(o =>
                o.TopicName == "order-events" &&
                o.SubscriptionName == "fwd-to-orders" &&
                o.ForwardTo == "orders"),
            It.IsAny<CancellationToken>()), Times.Once);

        _mockAdminClient.Verify(x => x.CreateSubscriptionAsync(
            It.Is<CreateSubscriptionOptions>(o =>
                o.TopicName == "payment-events" &&
                o.SubscriptionName == "fwd-to-orders" &&
                o.ForwardTo == "orders"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_WithMultipleCommandQueues_UsesFirstQueueForSubscriptions()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Listen.To
                .CommandQueue("orders")
                .CommandQueue("inventory")
            .Subscribe.To
                .Topic("order-events"));

        SetupQueueExists("orders", true);
        SetupQueueExists("inventory", true);
        SetupTopicExists("order-events", true);
        SetupSubscriptionExists("order-events", "fwd-to-orders", false);

        _mockAdminClient
            .Setup(x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((global::Azure.Response<SubscriptionProperties>)null!);

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert - subscription forwards to first queue "orders", not "inventory"
        _mockAdminClient.Verify(x => x.CreateSubscriptionAsync(
            It.Is<CreateSubscriptionOptions>(o => o.ForwardTo == "orders"),
            It.IsAny<CancellationToken>()), Times.Once);

        _mockAdminClient.Verify(x => x.CreateSubscriptionAsync(
            It.Is<CreateSubscriptionOptions>(o => o.ForwardTo == "inventory"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_WithNoSubscribedTopics_DoesNotCreateSubscriptions()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Send.Command<TestCommand>(q => q.Queue("orders"))
            .Listen.To.CommandQueue("orders"));

        SetupQueueExists("orders", true);

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert
        _mockAdminClient.Verify(x => x.CreateSubscriptionAsync(
            It.IsAny<CreateSubscriptionOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Resolve / Event Listening Tests ───────────────────────────────────

    [Fact]
    public async Task StartAsync_WithSubscribedTopics_ResolvesEventListeningToFirstCommandQueue()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Listen.To.CommandQueue("orders")
            .Subscribe.To.Topic("order-events"));

        SetupQueueExists("orders", true);
        SetupTopicExists("order-events", true);
        SetupSubscriptionExists("order-events", "fwd-to-orders", true);

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert
        var eventRouting = (IEventRoutingConfiguration)config;
        var listeningQueues = eventRouting.GetListeningQueues().ToList();
        Assert.Single(listeningQueues);
        Assert.Equal("orders", listeningQueues[0]);
    }

    [Fact]
    public async Task StartAsync_WithNoSubscribedTopics_ResolvesEmptyEventListeningQueues()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Send.Command<TestCommand>(q => q.Queue("orders"))
            .Listen.To.CommandQueue("orders"));

        SetupQueueExists("orders", true);

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert
        var eventRouting = (IEventRoutingConfiguration)config;
        var listeningQueues = eventRouting.GetListeningQueues().ToList();
        Assert.Empty(listeningQueues);
    }

    [Fact]
    public async Task StartAsync_ResolvesCommandRoutesAndListeningQueues()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Send.Command<TestCommand>(q => q.Queue("orders"))
            .Listen.To.CommandQueue("orders"));

        SetupQueueExists("orders", true);

        var bootstrapper = CreateBootstrapper(config);

        // Act
        await bootstrapper.StartAsync(CancellationToken.None);

        // Assert
        var commandRouting = (ICommandRoutingConfiguration)config;
        Assert.True(commandRouting.ShouldRoute<TestCommand>());
        Assert.Equal("orders", commandRouting.GetQueueName<TestCommand>());

        var listeningQueues = commandRouting.GetListeningQueues().ToList();
        Assert.Single(listeningQueues);
        Assert.Equal("orders", listeningQueues[0]);
    }
}
