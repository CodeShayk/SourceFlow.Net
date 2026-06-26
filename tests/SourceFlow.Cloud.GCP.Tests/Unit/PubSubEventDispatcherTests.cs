using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.GCP.Messaging.Events;
using SourceFlow.Cloud.GCP.Tests.TestHelpers;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.GCP.Tests.Unit;

[Trait("Category", TestCategories.Unit)]
public class PubSubEventDispatcherTests
{
    private static PubSubEventDispatcher CreateDispatcher(
        Mock<PublisherServiceApiClient> publisher,
        Mock<IEventRoutingConfiguration> routing)
        => new(
            publisher.Object,
            routing.Object,
            NullLogger<PubSubEventDispatcher>.Instance,
            Mock.Of<IDomainTelemetryService>());

    [Fact]
    public async Task Dispatch_SkipsPublish_When_ShouldRoute_IsFalse()
    {
        var publisher = new Mock<PublisherServiceApiClient>();
        var routing = new Mock<IEventRoutingConfiguration>();
        routing.Setup(r => r.ShouldRoute<TestEvent>()).Returns(false);

        await CreateDispatcher(publisher, routing).Dispatch(new TestEvent());

        publisher.Verify(p => p.PublishAsync(
            It.IsAny<TopicName>(), It.IsAny<IEnumerable<PubsubMessage>>(), It.IsAny<CallSettings>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispatch_Publishes_To_ResolvedTopic_With_EventAttributes()
    {
        var publisher = new Mock<PublisherServiceApiClient>();
        PubsubMessage? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<TopicName>(), It.IsAny<IEnumerable<PubsubMessage>>(), It.IsAny<CallSettings>()))
            .Callback<TopicName, IEnumerable<PubsubMessage>, CallSettings>((_, msgs, _) => captured = msgs.Single())
            .ReturnsAsync(new PublishResponse());

        var routing = new Mock<IEventRoutingConfiguration>();
        routing.Setup(r => r.ShouldRoute<TestEvent>()).Returns(true);
        routing.Setup(r => r.GetTopicName<TestEvent>())
            .Returns(TopicName.FromProjectTopic("test-project", "order-events").ToString());

        await CreateDispatcher(publisher, routing).Dispatch(new TestEvent { Name = "OrderCreated" });

        Assert.NotNull(captured);
        Assert.Equal(typeof(TestEvent).AssemblyQualifiedName, captured!.Attributes["EventType"]);
        Assert.Equal("OrderCreated", captured.Attributes["EventName"]);
    }
}
