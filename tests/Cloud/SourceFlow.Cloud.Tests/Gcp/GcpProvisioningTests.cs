using Google.Cloud.PubSub.V1;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Messaging;
using SourceFlow.Transport;
using ReceivedMessage = SourceFlow.Transport.ReceivedMessage;

namespace SourceFlow.Cloud.Tests.Gcp;

/// <summary>Provisioning plans on the Pub/Sub emulator: the GAP-13 regressions and emulator detection.</summary>
[Trait("Cloud", "Gcp")]
[Trait("Requires", "PubSubEmulator")]
public sealed class GcpProvisioningTests(PubSubEmulatorServer emulator)
{
    [Fact]
    [Trait("Id", "UT-GCP-12")]
    [Trait("Covers", "GAP-13;RT-R4;doc 07 4")]
    public async Task EachSubscribingService_GetsItsOwnSubscription_SoEveryServiceReceivesEveryEvent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GcpTransportFixture f = await GcpTransportFixture.EmulatorAsync(emulator);
        string topic = f.Prefix + ".payments";
        string billing = f.Prefix + ".billing";
        string shipping = f.Prefix + ".shipping";
        await f.Transport.ProvisionAsync(new TransportTopology(f.Prefix + ".payments-svc", [topic], [], []), ProvisioningMode.CreateIfMissing, ct);
        await f.Transport.ProvisionAsync(new TransportTopology(billing, [], [topic], []), ProvisioningMode.CreateIfMissing, ct);
        await f.Transport.ProvisionAsync(new TransportTopology(shipping, [], [topic], []), ProvisioningMode.CreateIfMissing, ct);

        // v2 subscribed every service to one "{topic}-sub", so services competed and each saw a share (GAP-13).
        Subscription billingSubscription = await f.Gcp.Subscriber.GetSubscriptionAsync(new SubscriptionName(PubSubEmulatorServer.ProjectId, GcpNaming.TopicSubscription("", billing, topic)), ct);
        Subscription shippingSubscription = await f.Gcp.Subscriber.GetSubscriptionAsync(new SubscriptionName(PubSubEmulatorServer.ProjectId, GcpNaming.TopicSubscription("", shipping, topic)), ct);
        billingSubscription.TopicAsTopicName.TopicId.ShouldBe(GcpNaming.Topic("", topic));
        shippingSubscription.TopicAsTopicName.TopicId.ShouldBe(GcpNaming.Topic("", topic));

        MessageEnvelope[] events = [.. Enumerable.Range(0, 6).Select(i => GcpMocks.Envelope("k" + i, type: "payments.settled"))];
        await f.Transport.PublishAsync(topic, events, ct);
        foreach (string service in new[] { billing, shipping })
        {
            await using ITransportReceiver receiver = await f.Transport.CreateReceiverAsync(new ReceiverOptions(service, [topic]), ct);
            var seen = new List<MessageId>();
            DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + f.ReceiveTimeout;
            while (seen.Count < events.Length && TimeProvider.System.GetUtcNow() < deadline)
            {
                foreach (ReceivedMessage message in await receiver.ReceiveAsync(10, TimeSpan.FromMilliseconds(200), ct))
                {
                    seen.Add(message.Envelope.MessageId);
                    await message.CompleteAsync(ct);
                }
            }

            seen.Order().ShouldBe(events.Select(e => e.MessageId).Order(), $"{service} receives every event");
        }
    }

    [Fact]
    [Trait("Id", "UT-GCP-13")]
    [Trait("Covers", "GAP-13;GAP-11;ADR-017;doc 07 4")]
    public async Task OrderedTopologies_EnableOrderingOnEverySubscription_WithTheDeadLetterPolicy_AndValidateChecksIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GcpTransportFixture f = await GcpTransportFixture.EmulatorAsync(emulator);
        string endpoint = f.Prefix + ".ledger";
        string topic = f.Prefix + ".ledger-events";
        var topology = new TransportTopology(endpoint, [topic], [topic], []) { OrderedEndpoint = true, OrderedTopics = [topic], MaxDeliveries = 3 };

        await f.Transport.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, ct);
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.Validate, ct);

        // v2 set an ordering key but never enabled ordering on the subscription (GAP-13).
        foreach (string id in new[] { GcpNaming.EndpointSubscription("", endpoint), GcpNaming.TopicSubscription("", endpoint, topic) })
        {
            Subscription subscription = await f.Gcp.Subscriber.GetSubscriptionAsync(new SubscriptionName(PubSubEmulatorServer.ProjectId, id), ct);
            subscription.EnableMessageOrdering.ShouldBeTrue(id);
            subscription.DeadLetterPolicy.DeadLetterTopic.ShouldEndWith("/topics/" + GcpNaming.DeadLetter("", endpoint));
            subscription.DeadLetterPolicy.MaxDeliveryAttempts.ShouldBe(5);   // Pub/Sub's minimum; the label holds the real limit
            subscription.Labels[GcpTransport.MaxDeliveriesLabel].ShouldBe("3");
            subscription.AckDeadlineSeconds.ShouldBe(30);
        }

        // The same names unordered: every subscription is reported, ordering being immutable.
        TopologyValidationException error = await Should.ThrowAsync<TopologyValidationException>(() => f.Transport.ProvisionAsync(topology with { OrderedEndpoint = false, OrderedTopics = [] }, ProvisioningMode.Validate, ct).AsTask());
        error.Problems.Where(p => p.Kind == TopologyProblemKind.OrderingMismatch).Select(p => p.Resource).ShouldBe([endpoint, $"{topic} → {endpoint}"], ignoreOrder: true);
        error.Problems.First().Detail.ShouldContain("message ordering enabled");
    }

    [Fact]
    [Trait("Id", "UT-GCP-04")]
    [Trait("Covers", "doc 07 6")]
    public async Task TheEmulator_IsDetectedThroughPubSubEmulatorHost()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await emulator.EnsureStartedAsync();   // started before the variable changes, so the server reads its own value
        string? previous = Environment.GetEnvironmentVariable("PUBSUB_EMULATOR_HOST");
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", emulator.Host);
        try
        {
            var transport = new GcpTransport(new GcpTransportOptions { ProjectId = PubSubEmulatorServer.ProjectId });   // EmulatorOrProduction
            string endpoint = "tck-" + Guid.CreateVersion7().ToString("N")[^10..] + ".detected";
            await transport.ProvisionAsync(new TransportTopology(endpoint, [], [], []), ProvisioningMode.CreateIfMissing, ct);
            MessageEnvelope sent = GcpMocks.Envelope();
            await transport.SendAsync(endpoint, [sent], ct);
            await using ITransportReceiver receiver = await transport.CreateReceiverAsync(new ReceiverOptions(endpoint, []), ct);

            IReadOnlyList<ReceivedMessage> received = await receiver.ReceiveAsync(1, TimeSpan.FromSeconds(10), ct);

            received.Single().Envelope.ShouldBe(sent);
            await received[0].CompleteAsync(ct);
            (await transport.IsReachableAsync(endpoint, ct)).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", previous);
        }
    }
}
