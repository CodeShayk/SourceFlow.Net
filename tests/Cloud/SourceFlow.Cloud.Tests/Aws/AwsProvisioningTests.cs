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
    [Fact]
    [Trait("Id", "UT-AWS-35")]
    [Trait("Covers", "doc 07 5")]
    public async Task AnExistingQueuePolicyStatement_SurvivesCreateIfMissing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AwsTransportFixture f = await AwsTransportFixture.LocalStackAsync(localStack);
        string endpoint = f.Prefix + ".shared";
        string topic = f.Prefix + ".shared-events";
        await f.Transport.ProvisionAsync(new TransportTopology(endpoint, [], [], []), ProvisioningMode.CreateIfMissing, ct);
        AwsTransport aws = f.Aws;
        string queueUrl = (await aws.Sqs.GetQueueUrlAsync(AwsNaming.Queue("", endpoint, ordered: false), ct)).QueueUrl;
        string queueArn = (await aws.Sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"], ct)).Attributes["QueueArn"];

        // Another team's grant, made outside SourceFlow: a different account may send to the queue.
        string crossAccount = $$"""{"Version":"2012-10-17","Statement":[{"Sid":"partner-account","Effect":"Allow","Principal":{"AWS":"arn:aws:iam::111122223333:root"},"Action":"sqs:SendMessage","Resource":"{{queueArn}}"}]}""";
        await aws.Sqs.SetQueueAttributesAsync(queueUrl, new Dictionary<string, string> { ["Policy"] = crossAccount }, ct);

        // CR-24: subscribing adds the SourceFlow statement by its Sid and keeps the other one.
        await f.Transport.ProvisionAsync(new TransportTopology(endpoint, [], [topic], []), ProvisioningMode.CreateIfMissing, ct);
        string policy = (await aws.Sqs.GetQueueAttributesAsync(queueUrl, ["Policy"], ct)).Attributes["Policy"];
        System.Text.Json.Nodes.JsonArray statements = System.Text.Json.Nodes.JsonNode.Parse(policy)!["Statement"]!.AsArray();
        statements.Select(s => (string?)s!["Sid"]).Order(StringComparer.Ordinal).ShouldBe(["partner-account", "sourceflow-sns-to-sqs"]);
        policy.ShouldContain("arn:aws:iam::111122223333:root");
        await f.Transport.ProvisionAsync(new TransportTopology(endpoint, [], [topic], []), ProvisioningMode.Validate, ct);

        // A second topic replaces the SourceFlow statement; the partner's stays.
        string second = f.Prefix + ".more-events";
        await f.Transport.ProvisionAsync(new TransportTopology(endpoint, [], [topic, second], []), ProvisioningMode.CreateIfMissing, ct);
        statements = System.Text.Json.Nodes.JsonNode.Parse((await aws.Sqs.GetQueueAttributesAsync(queueUrl, ["Policy"], ct)).Attributes["Policy"])!["Statement"]!.AsArray();
        statements.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Id", "UT-AWS-36")]
    [Trait("Covers", "ADR-017;doc 07 5")]
    public async Task RequireEncryption_ValidateReportsUnencryptedQueuesAndTopics_AndCreateEncrypts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AwsTransportFixture f = await AwsTransportFixture.LocalStackAsync(localStack);
        string plain = f.Prefix + ".plain";
        string plainTopic = f.Prefix + ".plain-events";
        var plainTopology = new TransportTopology(plain, [plainTopic], [], []);
        await f.Transport.ProvisionAsync(plainTopology, ProvisioningMode.CreateIfMissing, ct);
        string queueUrl = (await f.Aws.Sqs.GetQueueUrlAsync(AwsNaming.Queue("", plain, ordered: false), ct)).QueueUrl;
        await f.Aws.Sqs.SetQueueAttributesAsync(queueUrl, new Dictionary<string, string> { ["SqsManagedSseEnabled"] = "false" }, ct);

        // CR-41: resources made without encryption are reported, not changed.
        var strict = new AwsTransport(f.Aws.Sqs, f.Aws.Sns, new AwsTransportOptions { RequireEncryption = true });
        TopologyValidationException error = await Should.ThrowAsync<TopologyValidationException>(() => strict.ProvisionAsync(plainTopology, ProvisioningMode.Validate, ct).AsTask());
        error.Problems.ShouldContain(p => p.Kind == TopologyProblemKind.Unencrypted && p.Resource == plain);
        error.Problems.ShouldContain(p => p.Kind == TopologyProblemKind.Unencrypted && p.Resource == plainTopic && p.Detail.Contains("no key", StringComparison.Ordinal));

        // Created with the requirement, they are encrypted and validate.
        var encrypted = new TransportTopology(f.Prefix + ".sealed", [f.Prefix + ".sealed-events"], [], []);
        await strict.ProvisionAsync(encrypted, ProvisioningMode.CreateIfMissing, ct);
        await strict.ProvisionAsync(encrypted, ProvisioningMode.Validate, ct);
    }
}
