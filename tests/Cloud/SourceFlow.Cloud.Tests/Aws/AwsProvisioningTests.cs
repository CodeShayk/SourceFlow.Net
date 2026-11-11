using Amazon.SimpleNotificationService.Model;
using SourceFlow.Cloud.Aws;
using SourceFlow.Messaging;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Tests.Aws;

/// <summary>Provisioning plans on LocalStack: FIFO/standard compatibility and the GAP-02 regression.</summary>
[Trait("Cloud", "Aws")]
[Trait("Requires", "LocalStack")]
public sealed class AwsProvisioningTests(LocalStackServer localStack)
{
    [Fact]
    [Trait("Id", "UT-AWS-04")]
    [Trait("Covers", "ADR-017;doc 07 4")]
    public async Task AFifoEndpoint_OnAStandardTopic_IsRejectedByValidate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AwsTransportFixture f = await AwsTransportFixture.LocalStackAsync(localStack);
        string topic = f.Prefix + ".prices";
        await f.Transport.ProvisionAsync(new TransportTopology(f.Prefix + ".publisher", [topic], [], []), ProvisioningMode.CreateIfMissing, ct);
        var ordered = new TransportTopology(f.Prefix + ".ordered-reader", [], [topic], []) { OrderedEndpoint = true };

        TopologyValidationException error = await Should.ThrowAsync<TopologyValidationException>(() => f.Transport.ProvisionAsync(ordered, ProvisioningMode.CreateIfMissing, ct).AsTask());

        TopologyProblem mismatch = error.Problems.Single(p => p.Kind == TopologyProblemKind.OrderingMismatch);
        mismatch.Resource.ShouldBe($"{topic} → {ordered.Endpoint}");
        mismatch.Detail.ShouldContain("standard topic");
        mismatch.Detail.ShouldContain("FIFO queue");
        error.Problems.ShouldContain(p => p.Kind == TopologyProblemKind.MissingSubscription);   // never subscribed across the mismatch
    }

    [Fact]
    [Trait("Id", "UT-AWS-04")]
    [Trait("Covers", "ADR-017;doc 07 4")]
    public async Task OrderedTopologies_GetFifoQueuesTopicsAndDeadLetterQueues()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AwsTransportFixture f = await AwsTransportFixture.LocalStackAsync(localStack);
        string endpoint = f.Prefix + ".ledger";
        string topic = f.Prefix + ".ledger-events";
        var topology = new TransportTopology(endpoint, [topic], [topic], []) { OrderedEndpoint = true, OrderedTopics = [topic], MaxDeliveries = 6 };

        await f.Transport.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, ct);
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.Validate, ct);

        AwsTransport aws = (AwsTransport)f.Transport;
        string queueUrl = (await aws.Sqs.GetQueueUrlAsync(AwsNaming.Queue("", endpoint, ordered: true), ct)).QueueUrl;
        Dictionary<string, string> attributes = (await aws.Sqs.GetQueueAttributesAsync(queueUrl, ["All"], ct)).Attributes;
        attributes["FifoQueue"].ShouldBe("true");
        attributes["RedrivePolicy"].ShouldContain(AwsNaming.DeadLetterQueue("", endpoint, ordered: true));
        attributes["RedrivePolicy"].ShouldContain("6");
        (await aws.Sns.ListTopicsAsync(new ListTopicsRequest(), ct)).Topics.ShouldContain(t => t.TopicArn.EndsWith(":" + AwsNaming.Topic("", topic, ordered: true), StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Id", "UT-AWS-05")]
    [Trait("Covers", "GAP-02;RT-R4;doc 07 4")]
    public async Task OneEndpoint_OneReceiver_RawDelivery_CommandsAndEventsToldApartByType()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AwsTransportFixture f = await AwsTransportFixture.LocalStackAsync(localStack);
        string endpoint = f.Prefix + ".notifications";
        string accounts = f.Prefix + ".accounts-events";
        string transfers = f.Prefix + ".transfers-events";
        var topology = new TransportTopology(endpoint, [], [accounts, transfers], []);
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, ct);

        // Every subscription delivers raw CloudEvents to the one endpoint queue (v2 wrapped them and split the queues).
        AwsTransport aws = (AwsTransport)f.Transport;
        string queueUrl = (await aws.Sqs.GetQueueUrlAsync(AwsNaming.Queue("", endpoint, ordered: false), ct)).QueueUrl;
        string queueArn = (await aws.Sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"], ct)).Attributes["QueueArn"];
        foreach (string topic in new[] { accounts, transfers })
        {
            string topicArn = (await aws.Sns.ListTopicsAsync(new ListTopicsRequest(), ct)).Topics.Single(t => AwsNaming.NameOfArn(t.TopicArn) == AwsNaming.Topic("", topic, false)).TopicArn;
            Subscription subscription = (await aws.Sns.ListSubscriptionsByTopicAsync(topicArn, ct)).Subscriptions.ShouldHaveSingleItem();
            subscription.Endpoint.ShouldBe(queueArn);
            (await aws.Sns.GetSubscriptionAttributesAsync(subscription.SubscriptionArn, ct)).Attributes["RawMessageDelivery"].ShouldBe("true");
        }

        await using ITransportReceiver receiver = await f.Transport.CreateReceiverAsync(new ReceiverOptions(endpoint, [accounts, transfers]), ct);
        MessageEnvelope command = AwsMocks.Envelope(type: "notifications.send");
        MessageEnvelope opened = AwsMocks.Envelope(type: "accounts.opened");
        MessageEnvelope moved = AwsMocks.Envelope(type: "transfers.completed");
        await f.Transport.SendAsync(endpoint, [command], ct);
        await f.Transport.PublishAsync(accounts, [opened], ct);
        await f.Transport.PublishAsync(transfers, [moved], ct);

        var received = new List<MessageEnvelope>();
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(20);
        while (received.Count < 3 && TimeProvider.System.GetUtcNow() < deadline)
        {
            foreach (ReceivedMessage message in await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), ct))
            {
                received.Add(message.Envelope);
                await message.CompleteAsync(ct);
            }
        }

        received.Select(e => e.CloudEventType).Order().ShouldBe(["accounts.opened.v1", "notifications.send.v1", "transfers.completed.v1"]);
        received.ShouldBe([command, opened, moved], ignoreOrder: true);
    }
}
