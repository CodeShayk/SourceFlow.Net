using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Cloud.Core.Configuration;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

public class BusConfigurationTests
{
    private BusConfiguration BuildConfig(Action<BusConfigurationBuilder> configure)
    {
        var builder = new BusConfigurationBuilder();
        configure(builder);
        return builder.Build();
    }

    // ── Builder Tests ─────────────────────────────────────────────────────

    [Fact]
    public void Builder_RegistersCommandRoutes()
    {
        // Act
        var config = BuildConfig(bus => bus
            .Send.Command<TestCommand>(q => q.Queue("orders.fifo")));

        // Assert
        var bootstrap = (IBusBootstrapConfiguration)config;
        Assert.Single(bootstrap.CommandTypeToQueueName);
        Assert.Equal("orders.fifo", bootstrap.CommandTypeToQueueName[typeof(TestCommand)]);
    }

    [Fact]
    public void Builder_RegistersEventRoutes()
    {
        // Act
        var config = BuildConfig(bus => bus
            .Raise.Event<TestEvent>(t => t.Topic("order-events")));

        // Assert
        var bootstrap = (IBusBootstrapConfiguration)config;
        Assert.Single(bootstrap.EventTypeToTopicName);
        Assert.Equal("order-events", bootstrap.EventTypeToTopicName[typeof(TestEvent)]);
    }

    [Fact]
    public void Builder_RegistersCommandListeningQueues()
    {
        // Act
        var config = BuildConfig(bus => bus
            .Listen.To
                .CommandQueue("orders.fifo")
                .CommandQueue("inventory.fifo"));

        // Assert
        var bootstrap = (IBusBootstrapConfiguration)config;
        Assert.Equal(2, bootstrap.CommandListeningQueueNames.Count);
        Assert.Equal("orders.fifo", bootstrap.CommandListeningQueueNames[0]);
        Assert.Equal("inventory.fifo", bootstrap.CommandListeningQueueNames[1]);
    }

    [Fact]
    public void Builder_RegistersSubscribedTopics()
    {
        // Act
        var config = BuildConfig(bus => bus
            .Subscribe.To
                .Topic("order-events")
                .Topic("payment-events"));

        // Assert
        var bootstrap = (IBusBootstrapConfiguration)config;
        Assert.Equal(2, bootstrap.SubscribedTopicNames.Count);
        Assert.Equal("order-events", bootstrap.SubscribedTopicNames[0]);
        Assert.Equal("payment-events", bootstrap.SubscribedTopicNames[1]);
    }

    [Fact]
    public void Builder_RejectsFullUrlAsQueueName()
    {
        Assert.Throws<ArgumentException>(() => BuildConfig(bus => bus
            .Send.Command<TestCommand>(q => q.Queue("https://sqs.us-east-1.amazonaws.com/123456/orders"))));
    }

    [Fact]
    public void Builder_RejectsFullArnAsTopicName()
    {
        Assert.Throws<ArgumentException>(() => BuildConfig(bus => bus
            .Raise.Event<TestEvent>(t => t.Topic("arn:aws:sns:us-east-1:123456:order-events"))));
    }

    // ── Pre-Bootstrap Guard Tests ─────────────────────────────────────────

    [Fact]
    public void GetQueueName_BeforeResolve_ThrowsInvalidOperationException()
    {
        var config = BuildConfig(bus => bus
            .Send.Command<TestCommand>(q => q.Queue("orders.fifo")));

        var commandRouting = (ICommandRoutingConfiguration)config;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            commandRouting.GetQueueName<TestCommand>());

        Assert.Contains("has not been bootstrapped yet", ex.Message);
    }

    [Fact]
    public void GetTopicName_BeforeResolve_ThrowsInvalidOperationException()
    {
        var config = BuildConfig(bus => bus
            .Raise.Event<TestEvent>(t => t.Topic("order-events")));

        var eventRouting = (IEventRoutingConfiguration)config;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            eventRouting.GetTopicName<TestEvent>());

        Assert.Contains("has not been bootstrapped yet", ex.Message);
    }

    [Fact]
    public void EventRouting_GetListeningQueues_BeforeResolve_ThrowsInvalidOperationException()
    {
        var config = BuildConfig(bus => bus
            .Subscribe.To.Topic("order-events"));

        var eventRouting = (IEventRoutingConfiguration)config;

        Assert.Throws<InvalidOperationException>(() =>
            eventRouting.GetListeningQueues());
    }

    // ── Post-Bootstrap Tests ──────────────────────────────────────────────

    [Fact]
    public void EventRouting_GetListeningQueues_AfterResolve_ReturnsEventListeningUrls()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Listen.To.CommandQueue("orders.fifo")
            .Subscribe.To.Topic("order-events"));

        var bootstrap = (IBusBootstrapConfiguration)config;
        bootstrap.Resolve(
            commandRoutes: new Dictionary<Type, string>(),
            eventRoutes: new Dictionary<Type, string>(),
            commandListeningUrls: new List<string> { "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo" },
            subscribedTopicArns: new List<string> { "arn:aws:sns:us-east-1:123456:order-events" },
            eventListeningUrls: new List<string> { "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo" });

        // Act
        var eventRouting = (IEventRoutingConfiguration)config;
        var listeningQueues = eventRouting.GetListeningQueues().ToList();

        // Assert
        Assert.Single(listeningQueues);
        Assert.Equal("https://sqs.us-east-1.amazonaws.com/123456/orders.fifo", listeningQueues[0]);
    }

    [Fact]
    public void EventRouting_GetListeningQueues_AfterResolveWithNoTopics_ReturnsEmpty()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Listen.To.CommandQueue("orders.fifo"));

        var bootstrap = (IBusBootstrapConfiguration)config;
        bootstrap.Resolve(
            commandRoutes: new Dictionary<Type, string>(),
            eventRoutes: new Dictionary<Type, string>(),
            commandListeningUrls: new List<string> { "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo" },
            subscribedTopicArns: new List<string>(),
            eventListeningUrls: new List<string>());

        // Act
        var eventRouting = (IEventRoutingConfiguration)config;
        var listeningQueues = eventRouting.GetListeningQueues().ToList();

        // Assert
        Assert.Empty(listeningQueues);
    }

    [Fact]
    public void CommandRouting_AfterResolve_ReturnsCorrectQueueUrl()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Send.Command<TestCommand>(q => q.Queue("orders.fifo"))
            .Listen.To.CommandQueue("orders.fifo"));

        var bootstrap = (IBusBootstrapConfiguration)config;
        bootstrap.Resolve(
            commandRoutes: new Dictionary<Type, string>
            {
                [typeof(TestCommand)] = "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo"
            },
            eventRoutes: new Dictionary<Type, string>(),
            commandListeningUrls: new List<string> { "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo" },
            subscribedTopicArns: new List<string>(),
            eventListeningUrls: new List<string>());

        // Act
        var commandRouting = (ICommandRoutingConfiguration)config;

        // Assert
        Assert.True(commandRouting.ShouldRoute<TestCommand>());
        Assert.Equal("https://sqs.us-east-1.amazonaws.com/123456/orders.fifo",
            commandRouting.GetQueueName<TestCommand>());
    }

    [Fact]
    public void EventRouting_GetSubscribedTopics_AfterResolve_ReturnsResolvedArns()
    {
        // Arrange
        var config = BuildConfig(bus => bus
            .Listen.To.CommandQueue("orders.fifo")
            .Subscribe.To.Topic("order-events"));

        var bootstrap = (IBusBootstrapConfiguration)config;
        bootstrap.Resolve(
            commandRoutes: new Dictionary<Type, string>(),
            eventRoutes: new Dictionary<Type, string>(),
            commandListeningUrls: new List<string> { "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo" },
            subscribedTopicArns: new List<string> { "arn:aws:sns:us-east-1:123456:order-events" },
            eventListeningUrls: new List<string> { "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo" });

        // Act
        var eventRouting = (IEventRoutingConfiguration)config;
        var subscribedTopics = eventRouting.GetSubscribedTopics().ToList();

        // Assert
        Assert.Single(subscribedTopics);
        Assert.Equal("arn:aws:sns:us-east-1:123456:order-events", subscribedTopics[0]);
    }
}
