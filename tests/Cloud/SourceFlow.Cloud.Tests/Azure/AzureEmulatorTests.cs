using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using SourceFlow.Cloud.Azure;
using SourceFlow.Messaging;
using SourceFlow.Transport;
using ReceivedMessage = SourceFlow.Transport.ReceivedMessage;

namespace SourceFlow.Cloud.Tests.Azure;

/// <summary>Sessions, provisioning and duplicate detection on the Service Bus emulator.</summary>
[Trait("Cloud", "Azure")]
[Trait("Requires", "ServiceBusEmulator")]
public sealed class AzureEmulatorTests(ServiceBusEmulatorServer emulator)
{
    [Fact]
    [Trait("Id", "UT-ASB-21")]
    [Trait("Covers", "doc 07 4;RX-R1")]
    public async Task OrderedEndpoints_UseSessions_AKeysNextMessageWaitsForItsHead_OtherKeysFlow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AzureTransportFixture f = await AzureTransportFixture.EmulatorAsync(emulator);
        string endpoint = f.Prefix + ".ledger";
        await f.Transport.ProvisionAsync(new TransportTopology(endpoint, [], [], []) { OrderedEndpoint = true }, ProvisioningMode.CreateIfMissing, ct);
        MessageEnvelope a1 = AzureMocks.Envelope("a");
        MessageEnvelope a2 = AzureMocks.Envelope("a");
        MessageEnvelope b1 = AzureMocks.Envelope("b");
        await f.Transport.SendAsync(endpoint, [a1, a2, b1], ct);
        await using ITransportReceiver one = await f.Transport.CreateReceiverAsync(new ReceiverOptions(endpoint, []), ct);
        await using ITransportReceiver two = await f.Transport.CreateReceiverAsync(new ReceiverOptions(endpoint, []), ct);

        var heads = new List<ReceivedMessage>();
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + f.ReceiveTimeout;
        while (heads.Count < 2 && TimeProvider.System.GetUtcNow() < deadline)
        {
            heads.AddRange(await one.ReceiveAsync(1, TimeSpan.FromMilliseconds(200), ct));
            heads.AddRange(await two.ReceiveAsync(1, TimeSpan.FromMilliseconds(200), ct));
        }

        heads.Select(h => h.Envelope.MessageId).Order().ShouldBe(new[] { a1.MessageId, b1.MessageId }.Order(), "both keys flow in parallel");
        (await one.ReceiveAsync(10, TimeSpan.FromSeconds(1), ct)).ShouldBeEmpty("a2 waits for a1 on every instance");
        (await two.ReceiveAsync(10, TimeSpan.FromSeconds(1), ct)).ShouldBeEmpty();

        // A delayed retry keeps its place: a1 comes back before a2.
        ReceivedMessage head = heads.Single(h => h.Envelope.MessageId == a1.MessageId);
        await head.AbandonAsync(TimeSpan.FromSeconds(2), ct);
        await heads.Single(h => h.Envelope.MessageId == b1.MessageId).CompleteAsync(ct);
        var order = new List<MessageId>();
        deadline = TimeProvider.System.GetUtcNow() + f.ReceiveTimeout;
        while (order.Count < 2 && TimeProvider.System.GetUtcNow() < deadline)
        {
            foreach (ReceivedMessage message in (await one.ReceiveAsync(1, TimeSpan.FromMilliseconds(200), ct)).Concat(await two.ReceiveAsync(1, TimeSpan.FromMilliseconds(200), ct)))
            {
                order.Add(message.Envelope.MessageId);
                if (message.Envelope.MessageId == a1.MessageId)
                {
                    message.DeliveryCount.ShouldBe(2);
                }

                await message.CompleteAsync(ct);
            }
        }

        order.ShouldBe([a1.MessageId, a2.MessageId]);
    }

    [Fact]
    [Trait("Id", "UT-ASB-22")]
    [Trait("Covers", "doc 07 4;RT-R4;ADR-017")]
    public async Task Provisioning_CreatesTheQueue_AndAForwardingSubscriptionPerService_WithTheRules()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AzureTransportFixture f = await AzureTransportFixture.EmulatorAsync(emulator);
        string topic = f.Prefix + ".payments";
        string billing = f.Prefix + ".billing";
        string shipping = f.Prefix + ".shipping";
        await f.Transport.ProvisionAsync(new TransportTopology(billing, [], [topic], []) { OrderedEndpoint = true, MaxDeliveries = 6, Filters = new Dictionary<string, string> { [topic] = "type = 'payments.settled.v1'" } }, ProvisioningMode.CreateIfMissing, ct);
        await f.Transport.ProvisionAsync(new TransportTopology(shipping, [], [topic], []), ProvisioningMode.CreateIfMissing, ct);

        QueueProperties queue = (await f.Azure.Administration.GetQueueAsync(AzureNaming.Queue("", billing), ct)).Value;
        queue.RequiresSession.ShouldBeTrue();
        queue.MaxDeliveryCount.ShouldBe(6);
        queue.LockDuration.ShouldBe(TimeSpan.FromSeconds(5));
        queue.DeadLetteringOnMessageExpiration.ShouldBeTrue();
        queue.RequiresDuplicateDetection.ShouldBeFalse();
        SubscriptionProperties subscription = (await f.Azure.Administration.GetSubscriptionAsync(AzureNaming.Topic("", topic), AzureNaming.Subscription("", billing), ct)).Value;
        subscription.ForwardTo.ShouldEndWith("/" + AzureNaming.Queue("", billing), Case.Insensitive);
        var rules = new List<RuleProperties>();
        await foreach (RuleProperties rule in f.Azure.Administration.GetRulesAsync(AzureNaming.Topic("", topic), AzureNaming.Subscription("", billing), ct))
        {
            rules.Add(rule);
        }

        AzureFilter.Describe(rules.Single().Filter).ShouldBe("type=payments.settled.v1");

        // Each service gets every event it subscribed to (the filter is applied by the broker).
        MessageEnvelope settled = AzureMocks.Envelope("p1", type: "payments.settled");
        MessageEnvelope failed = AzureMocks.Envelope("p2", type: "payments.failed");
        await f.Transport.PublishAsync(topic, [settled, failed], ct);
        foreach ((string service, MessageId[] expected) in new[] { (billing, new[] { settled.MessageId }), (shipping, new[] { settled.MessageId, failed.MessageId }) })
        {
            await using ITransportReceiver receiver = await f.Transport.CreateReceiverAsync(new ReceiverOptions(service, [topic]), ct);
            var seen = new List<MessageId>();
            DateTimeOffset quiet = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(3);
            while (TimeProvider.System.GetUtcNow() < quiet)
            {
                foreach (ReceivedMessage message in await receiver.ReceiveAsync(10, TimeSpan.FromMilliseconds(300), ct))
                {
                    seen.Add(message.Envelope.MessageId);
                    await message.CompleteAsync(ct);
                }
            }

            seen.Order().ShouldBe(expected.Order(), service);
        }
    }

    [Fact]
    [Trait("Id", "UT-ASB-23")]
    [Trait("Covers", "ADR-017;doc 07 5")]
    public async Task Validate_ReportsASubscriptionThatDoesNotForward_AndATopicNamedLikeAQueue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AzureTransportFixture f = await AzureTransportFixture.EmulatorAsync(emulator);
        string endpoint = f.Prefix + ".billing";
        string topic = f.Prefix + ".payments";
        var topology = new TransportTopology(endpoint, [], [topic], []);
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, ct);
        string subscription = AzureNaming.Subscription("", endpoint);
        await f.Azure.Administration.DeleteSubscriptionAsync(AzureNaming.Topic("", topic), subscription, ct);
        await f.Azure.Administration.CreateSubscriptionAsync(new CreateSubscriptionOptions(AzureNaming.Topic("", topic), subscription), ct);   // no ForwardTo

        TopologyValidationException error = await Should.ThrowAsync<TopologyValidationException>(() => f.Transport.ProvisionAsync(topology, ProvisioningMode.Validate, ct).AsTask());
        error.Problems.ShouldContain(p => p.Kind == TopologyProblemKind.Misconfigured && p.Resource == $"{topic} → {endpoint}" && p.Detail.Contains("forwards to '(nothing)'"));

        // Queues and topics share names: publishing to the endpoint's name is not a topic.
        TopologyValidationException clash = await Should.ThrowAsync<TopologyValidationException>(() =>
            f.Transport.ProvisionAsync(new TransportTopology(f.Prefix + ".other", [endpoint], [], []), ProvisioningMode.Validate, ct).AsTask());
        clash.Problems.ShouldContain(p => p.Kind == TopologyProblemKind.MissingTopic && p.Detail.Contains("a queue has that name"));
    }

    [Fact]
    [Trait("Id", "UT-ASB-24")]
    [Trait("Covers", "doc 07 4")]
    public async Task DuplicateDetection_WhenConfigured_DropsAReSentMessageId()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await emulator.EnsureStartedAsync();
        string prefix = "tck-" + Guid.CreateVersion7().ToString("N")[^10..];
        var options = new AzureTransportOptions
        {
            ConnectionString = ServiceBusEmulatorServer.ConnectionString(emulator.Host, emulator.AmqpPort),
            AdministrationConnectionString = emulator.AdministrationConnectionString,
            DuplicateDetectionWindow = TimeSpan.FromMinutes(1),
            LockDuration = TimeSpan.FromSeconds(5),
            MessageTimeToLive = TimeSpan.FromHours(1),
            NamePrefix = prefix,
        };
        await using var transport = new AzureTransport(options);
        try
        {
            await transport.ProvisionAsync(new TransportTopology("dedup", [], [], []), ProvisioningMode.CreateIfMissing, ct);
            (await transport.Administration.GetQueueAsync(AzureNaming.Queue(prefix, "dedup"), ct)).Value.RequiresDuplicateDetection.ShouldBeTrue();
            transport.Capabilities.Deduplication.ShouldBeTrue();
            MessageEnvelope sent = AzureMocks.Envelope();

            await transport.SendAsync("dedup", [sent], ct);
            await transport.SendAsync("dedup", [sent], ct);   // a producer retry

            await using ITransportReceiver receiver = await transport.CreateReceiverAsync(new ReceiverOptions("dedup", []), ct);
            var received = new List<ReceivedMessage>();
            DateTimeOffset quiet = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(3);
            while (TimeProvider.System.GetUtcNow() < quiet)
            {
                foreach (ReceivedMessage message in await receiver.ReceiveAsync(10, TimeSpan.FromMilliseconds(300), ct))
                {
                    received.Add(message);
                    await message.CompleteAsync(ct);
                }
            }

            received.Single().Envelope.MessageId.ShouldBe(sent.MessageId);
        }
        finally
        {
            await transport.Administration.DeleteQueueAsync(AzureNaming.Queue(prefix, "dedup"), CancellationToken.None);
        }
    }
}
