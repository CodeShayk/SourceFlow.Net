using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.GCP.Messaging.Commands;
using SourceFlow.Cloud.GCP.Tests.TestHelpers;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.GCP.Tests.Unit;

[Trait("Category", TestCategories.Unit)]
public class PubSubCommandDispatcherTests
{
    private static PubSubCommandDispatcher CreateDispatcher(
        Mock<PublisherServiceApiClient> publisher,
        Mock<ICommandRoutingConfiguration> routing)
        => new(
            publisher.Object,
            routing.Object,
            NullLogger<PubSubCommandDispatcher>.Instance,
            Mock.Of<IDomainTelemetryService>());

    [Fact]
    public async Task Dispatch_SkipsPublish_When_ShouldRoute_IsFalse()
    {
        var publisher = new Mock<PublisherServiceApiClient>();
        var routing = new Mock<ICommandRoutingConfiguration>();
        routing.Setup(r => r.ShouldRoute<TestCommand>()).Returns(false);

        await CreateDispatcher(publisher, routing).Dispatch(new TestCommand());

        publisher.Verify(p => p.PublishAsync(
            It.IsAny<TopicName>(), It.IsAny<IEnumerable<PubsubMessage>>(), It.IsAny<CallSettings>()),
            Times.Never);
        routing.Verify(r => r.GetQueueName<TestCommand>(), Times.Never);
    }

    [Fact]
    public async Task Dispatch_Publishes_To_ResolvedTopic_With_Attributes()
    {
        var publisher = new Mock<PublisherServiceApiClient>();
        PubsubMessage? captured = null;
        TopicName? capturedTopic = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<TopicName>(), It.IsAny<IEnumerable<PubsubMessage>>(), It.IsAny<CallSettings>()))
            .Callback<TopicName, IEnumerable<PubsubMessage>, CallSettings>((t, msgs, _) =>
            {
                capturedTopic = t;
                captured = msgs.Single();
            })
            .ReturnsAsync(new PublishResponse());

        var routing = new Mock<ICommandRoutingConfiguration>();
        routing.Setup(r => r.ShouldRoute<TestCommand>()).Returns(true);
        routing.Setup(r => r.GetQueueName<TestCommand>())
            .Returns(TopicName.FromProjectTopic("test-project", "orders").ToString());

        var command = new TestCommand { Entity = new SourceFlow.Messaging.Commands.EntityRef { Id = 42 } };
        command.Metadata.SequenceNo = 7;

        await CreateDispatcher(publisher, routing).Dispatch(command);

        Assert.NotNull(captured);
        Assert.Equal("orders", capturedTopic!.TopicId);
        Assert.Equal(typeof(TestCommand).AssemblyQualifiedName, captured!.Attributes["CommandType"]);
        Assert.Equal("42", captured.Attributes["EntityId"]);
        Assert.Equal("7", captured.Attributes["SequenceNo"]);
        Assert.Contains("TestCommand", captured.Data.ToStringUtf8());
    }
}
