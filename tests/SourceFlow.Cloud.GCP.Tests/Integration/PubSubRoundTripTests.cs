using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.GCP.Configuration;
using SourceFlow.Cloud.GCP.Infrastructure;
using SourceFlow.Cloud.GCP.Messaging.Commands;
using SourceFlow.Cloud.GCP.Messaging.Events;
using SourceFlow.Cloud.GCP.Tests.TestHelpers;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.GCP.Tests.Integration;

/// <summary>
/// End-to-end tests against the Pub/Sub emulator: bootstrap provisioning, command publish→pull,
/// and event publish→pull. Skipped unless PUBSUB_EMULATOR_HOST is set.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public class PubSubRoundTripTests : IClassFixture<PubSubEmulatorFixture>
{
    private readonly PubSubEmulatorFixture _fixture;

    public PubSubRoundTripTests(PubSubEmulatorFixture fixture) => _fixture = fixture;

    private GcpOptions Options() => new() { ProjectId = _fixture.ProjectId };

    private async Task<BusConfiguration> BootstrapAsync(string commandQueue, string eventTopic)
    {
        var builder = new BusConfigurationBuilder();
        builder
            .Send.Command<TestCommand>(q => q.Queue(commandQueue))
            .Raise.Event<TestEvent>(t => t.Topic(eventTopic))
            .Listen.To.CommandQueue(commandQueue)
            .Subscribe.To.Topic(eventTopic);
        var busConfig = builder.Build();

        var bootstrapper = new GcpBusBootstrapper(
            busConfig, _fixture.Publisher!, _fixture.Subscriber!, Options(),
            NullLogger<GcpBusBootstrapper>.Instance);

        await bootstrapper.StartAsync(default);
        return busConfig;
    }

    [SkippableFact]
    public async Task Bootstrapper_Provisions_Topics_And_Subscriptions()
    {
        Skip.IfNot(_fixture.EmulatorAvailable, "PUBSUB_EMULATOR_HOST is not set.");

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
        var commandQueue = $"it-commands-{suffix}";
        var eventTopic = $"it-events-{suffix}";

        await BootstrapAsync(commandQueue, eventTopic);

        // Topics exist
        await _fixture.Publisher!.GetTopicAsync(TopicName.FromProjectTopic(_fixture.ProjectId, commandQueue));
        await _fixture.Publisher.GetTopicAsync(TopicName.FromProjectTopic(_fixture.ProjectId, eventTopic));

        // Subscriptions exist
        await _fixture.Subscriber!.GetSubscriptionAsync(SubscriptionName.FromProjectSubscription(_fixture.ProjectId, $"{commandQueue}-sub"));
        await _fixture.Subscriber.GetSubscriptionAsync(SubscriptionName.FromProjectSubscription(_fixture.ProjectId, $"{eventTopic}-sub"));
    }

    [SkippableFact]
    public async Task CommandDispatch_Publishes_Message_Pullable_From_Subscription()
    {
        Skip.IfNot(_fixture.EmulatorAvailable, "PUBSUB_EMULATOR_HOST is not set.");

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
        var commandQueue = $"it-commands-{suffix}";
        var busConfig = await BootstrapAsync(commandQueue, $"it-events-{suffix}");

        var dispatcher = new PubSubCommandDispatcher(
            _fixture.Publisher!, busConfig, NullLogger<PubSubCommandDispatcher>.Instance, Mock.Of<IDomainTelemetryService>());

        var command = new TestCommand
        {
            Name = "CreateOrder",
            Entity = new EntityRef { Id = 99 },
            Payload = new TestPayload { Data = "round-trip", Value = 5 }
        };
        command.Metadata.SequenceNo = 3;

        await dispatcher.Dispatch(command);

        var subscription = SubscriptionName.FromProjectSubscription(_fixture.ProjectId, $"{commandQueue}-sub");
        var messages = await _fixture.PullWithRetryAsync(subscription, expected: 1);

        var received = Assert.Single(messages);
        Assert.Equal(typeof(TestCommand).AssemblyQualifiedName, received.Message.Attributes["CommandType"]);
        Assert.Equal("99", received.Message.Attributes["EntityId"]);
        Assert.Equal("3", received.Message.Attributes["SequenceNo"]);
        // The command Name round-trips in the JSON body (the IPayload-typed Payload is
        // serialized by its declared interface type, matching the AWS base dispatcher).
        Assert.Contains("CreateOrder", received.Message.Data.ToStringUtf8());
    }

    [SkippableFact]
    public async Task EventDispatch_Publishes_Message_Pullable_From_Subscription()
    {
        Skip.IfNot(_fixture.EmulatorAvailable, "PUBSUB_EMULATOR_HOST is not set.");

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
        var eventTopic = $"it-events-{suffix}";
        var busConfig = await BootstrapAsync($"it-commands-{suffix}", eventTopic);

        var dispatcher = new PubSubEventDispatcher(
            _fixture.Publisher!, busConfig, NullLogger<PubSubEventDispatcher>.Instance, Mock.Of<IDomainTelemetryService>());

        await dispatcher.Dispatch(new TestEvent { Name = "OrderCreated", Payload = new TestEntity { Id = 7 } });

        var subscription = SubscriptionName.FromProjectSubscription(_fixture.ProjectId, $"{eventTopic}-sub");
        var messages = await _fixture.PullWithRetryAsync(subscription, expected: 1);

        var received = Assert.Single(messages);
        Assert.Equal(typeof(TestEvent).AssemblyQualifiedName, received.Message.Attributes["EventType"]);
        Assert.Equal("OrderCreated", received.Message.Attributes["EventName"]);
    }

    [SkippableFact]
    public async Task Bootstrap_Is_Idempotent_On_Repeated_Runs()
    {
        Skip.IfNot(_fixture.EmulatorAvailable, "PUBSUB_EMULATOR_HOST is not set.");

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
        var commandQueue = $"it-commands-{suffix}";
        var eventTopic = $"it-events-{suffix}";

        await BootstrapAsync(commandQueue, eventTopic);

        // Running again must not throw (AlreadyExists tolerated).
        var ex = await Record.ExceptionAsync(() => BootstrapAsync(commandQueue, eventTopic));
        Assert.Null(ex);
    }
}
