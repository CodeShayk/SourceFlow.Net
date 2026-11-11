using System.Text;
using SourceFlow.Messaging;
using SourceFlow.Transport;

namespace SourceFlow.Testing.Conformance;

/// <summary>A transport under test, with resources isolated by <see cref="Prefix"/>.</summary>
public interface ITransportFixture : IAsyncDisposable
{
    /// <summary>The transport.</summary>
    ITransport Transport { get; }

    /// <summary>A unique prefix for endpoint and topic names in this case.</summary>
    string Prefix { get; }

    /// <summary>True when <see cref="AdvanceClockAsync"/> moves the broker's clock.</summary>
    bool SupportsClockControl { get; }

    /// <summary>Moves the broker clock forward (delayed redelivery).</summary>
    ValueTask AdvanceClockAsync(TimeSpan by);

    /// <summary>The longest time to wait for a message to arrive.</summary>
    TimeSpan ReceiveTimeout { get; }
}

/// <summary>
/// The transport conformance kit (TCK, ADR-014): every transport must pass every mandatory case for GA parity.
/// Run each case against a fresh fixture.
/// </summary>
public static class TransportConformance
{
    /// <summary>Every case, ordered by id.</summary>
    public static IReadOnlyList<ConformanceCase<ITransportFixture>> Cases { get; } =
    [
        new("TCK-01", "Send then receive round-trips the envelope", SendReceive),
        new("TCK-02", "Every envelope attribute and header survives the wire", AttributesSurvive),
        new("TCK-03", "Messages with the same partition key arrive in order", PerKeyOrder),
        new("TCK-04", "A completed message is not redelivered", CompleteRemoves),
        new("TCK-05", "An abandoned message is redelivered with a higher delivery count", AbandonRedelivers),
        new("TCK-06", "A delayed abandon is not visible before the delay", DelayedRedelivery, RequiresClockControl: true),
        new("TCK-07", "Publish fans out one copy per subscribed endpoint", FanOutPerService),
        new("TCK-08", "Competing receivers on one endpoint share the messages without duplicates", CompetingConsumers),
        new("TCK-09", "Bodies above the capability limit are refused", BodyLimit),
        new("TCK-10", "Validate provisioning fails for missing topics; CreateIfMissing creates them", Provisioning),
        new("TCK-11", "Capabilities are declared consistently", Capabilities),
        new("TCK-12", "A 64 KB binary body round-trips byte for byte", BinaryBody),
    ];

    private static string Endpoint(ITransportFixture f, string name)
    {
        return $"{f.Prefix}.{name}";
    }

    private static MessageEnvelope Envelope(string? key = null, string body = "{\"n\":1}")
    {
        return new MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = "tck.sample",
            TypeVersion = 1,
            Source = "tck",
            Time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PartitionKey = key,
            Body = Encoding.UTF8.GetBytes(body),
        };
    }

    private static async Task<IReadOnlyList<ReceivedMessage>> ReceiveAtLeastAsync(ITransportReceiver receiver, int count, ITransportFixture f, CancellationToken ct)
    {
        var received = new List<ReceivedMessage>();
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + f.ReceiveTimeout;
        while (received.Count < count && TimeProvider.System.GetUtcNow() < deadline)
        {
            received.AddRange(await receiver.ReceiveAsync(count - received.Count, TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false));
        }

        return received;
    }

    private static async Task<ITransportReceiver> ReceiverAsync(ITransportFixture f, string endpoint, CancellationToken ct, params string[] topics)
    {
        await f.Transport.ProvisionAsync(new TransportTopology(endpoint, [], topics, []), ProvisioningMode.CreateIfMissing, ct).ConfigureAwait(false);
        return await f.Transport.CreateReceiverAsync(new ReceiverOptions(endpoint, topics), ct).ConfigureAwait(false);
    }

    private static async Task SendReceive(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Endpoint(f, "a");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        MessageEnvelope sent = Envelope();

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        ReceivedMessage received = (await ReceiveAtLeastAsync(receiver, 1, f, ct).ConfigureAwait(false)).Single();

        Check.Equal(sent, received.Envelope, "envelope");
        Check.Equal(1, received.DeliveryCount, "first delivery");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task AttributesSurvive(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Endpoint(f, "b");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        MessageEnvelope sent = Envelope("k-1") with
        {
            CorrelationId = new CorrelationId("corr-7"),
            CausationId = new MessageId(Guid.CreateVersion7()),
            Subject = "bank-account-1",
            StreamVersion = 42,
            TenantId = "eu",
            Actor = "user:7",
            TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            TraceState = "v=1",
            Headers = new Dictionary<string, string> { ["x-origin"] = "web", ["x-üñí"] = "çødé" },
        };

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        ReceivedMessage received = (await ReceiveAtLeastAsync(receiver, 1, f, ct).ConfigureAwait(false)).Single();

        Check.Equal(sent, received.Envelope, "every attribute");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task PerKeyOrder(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Endpoint(f, "c");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        MessageEnvelope[] sent = [.. Enumerable.Range(1, 10).Select(i => Envelope("same-key", $"{{\"n\":{i}}}"))];

        await f.Transport.SendAsync(endpoint, sent, ct).ConfigureAwait(false);
        var order = new List<MessageId>();
        foreach (ReceivedMessage message in await ReceiveAtLeastAsync(receiver, 10, f, ct).ConfigureAwait(false))
        {
            order.Add(message.Envelope.MessageId);
            await message.CompleteAsync(ct).ConfigureAwait(false);
        }

        Check.SequenceEqual(sent.Select(s => s.MessageId), order, "per-key order");
    }

    private static async Task CompleteRemoves(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Endpoint(f, "d");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        await f.Transport.SendAsync(endpoint, [Envelope()], ct).ConfigureAwait(false);

        ReceivedMessage first = (await ReceiveAtLeastAsync(receiver, 1, f, ct).ConfigureAwait(false)).Single();
        await first.CompleteAsync(ct).ConfigureAwait(false);

        Check.Equal(0, (await receiver.ReceiveAsync(10, TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false)).Count, "not redelivered");
    }

    private static async Task AbandonRedelivers(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Endpoint(f, "e");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        await f.Transport.SendAsync(endpoint, [Envelope()], ct).ConfigureAwait(false);

        ReceivedMessage first = (await ReceiveAtLeastAsync(receiver, 1, f, ct).ConfigureAwait(false)).Single();
        await first.AbandonAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
        ReceivedMessage second = (await ReceiveAtLeastAsync(receiver, 1, f, ct).ConfigureAwait(false)).Single();

        Check.Equal(first.Envelope.MessageId, second.Envelope.MessageId, "same message");
        Check.True(second.DeliveryCount > first.DeliveryCount, "delivery count increases");
        await second.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task DelayedRedelivery(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Endpoint(f, "f");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        await f.Transport.SendAsync(endpoint, [Envelope()], ct).ConfigureAwait(false);
        ReceivedMessage first = (await ReceiveAtLeastAsync(receiver, 1, f, ct).ConfigureAwait(false)).Single();

        await first.AbandonAsync(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        Check.Equal(0, (await receiver.ReceiveAsync(10, TimeSpan.Zero, ct).ConfigureAwait(false)).Count, "hidden during the delay");
        await f.AdvanceClockAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);

        ReceivedMessage again = (await ReceiveAtLeastAsync(receiver, 1, f, ct).ConfigureAwait(false)).Single();
        Check.Equal(first.Envelope.MessageId, again.Envelope.MessageId, "redelivered after the delay");
        await again.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task FanOutPerService(ITransportFixture f, CancellationToken ct)
    {
        string topic = Endpoint(f, "events");
        await using ITransportReceiver a = await ReceiverAsync(f, Endpoint(f, "svc-a"), ct, topic).ConfigureAwait(false);
        await using ITransportReceiver b = await ReceiverAsync(f, Endpoint(f, "svc-b"), ct, topic).ConfigureAwait(false);
        MessageEnvelope published = Envelope("k");

        await f.Transport.PublishAsync(topic, [published], ct).ConfigureAwait(false);

        foreach (ITransportReceiver receiver in new[] { a, b })
        {
            ReceivedMessage message = (await ReceiveAtLeastAsync(receiver, 1, f, ct).ConfigureAwait(false)).Single();
            Check.Equal(published.MessageId, message.Envelope.MessageId, "each service gets its copy");
            await message.CompleteAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task CompetingConsumers(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Endpoint(f, "g");
        await using ITransportReceiver one = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        await using ITransportReceiver two = await f.Transport.CreateReceiverAsync(new ReceiverOptions(endpoint, []), ct).ConfigureAwait(false);
        await f.Transport.SendAsync(endpoint, [.. Enumerable.Range(0, 20).Select(i => Envelope("k" + i))], ct).ConfigureAwait(false);

        var seen = new HashSet<MessageId>();
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + f.ReceiveTimeout;
        while (seen.Count < 20 && TimeProvider.System.GetUtcNow() < deadline)
        {
            foreach (ITransportReceiver receiver in new[] { one, two })
            {
                foreach (ReceivedMessage message in await receiver.ReceiveAsync(3, TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false))
                {
                    Check.True(seen.Add(message.Envelope.MessageId), "no duplicate across competing receivers");
                    await message.CompleteAsync(ct).ConfigureAwait(false);
                }
            }
        }

        Check.Equal(20, seen.Count, "every message delivered once");
    }

    private static async Task BodyLimit(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Endpoint(f, "h");
        MessageEnvelope tooLarge = Envelope() with { Body = new byte[f.Transport.Capabilities.MaxBodyBytes + 1] };

        await Check.ThrowsAsync<MessageTooLargeException>(() => f.Transport.SendAsync(endpoint, [tooLarge], ct).AsTask(), "oversized body").ConfigureAwait(false);
    }

    private static async Task Provisioning(ITransportFixture f, CancellationToken ct)
    {
        string topic = Endpoint(f, "missing-topic");
        var topology = new TransportTopology(Endpoint(f, "i"), [topic], [], []);

        await Check.ThrowsAsync<TopologyValidationException>(() => f.Transport.ProvisionAsync(topology, ProvisioningMode.Validate, ct).AsTask(), "validate missing").ConfigureAwait(false);
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, ct).ConfigureAwait(false);
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.Validate, ct).ConfigureAwait(false);
    }

    private static Task Capabilities(ITransportFixture f, CancellationToken ct)
    {
        TransportCapabilities capabilities = f.Transport.Capabilities;
        Check.True(capabilities.MaxBodyBytes >= 64 * 1024, "at least 64 KB bodies");
        Check.True(capabilities.MaxRetention >= TimeSpan.FromDays(1), "at least one day of retention");
        Check.True(!capabilities.NativeDelay || capabilities.MaxDelay > TimeSpan.Zero, "a native delay has a maximum");
        Check.True(!string.IsNullOrWhiteSpace(f.Transport.SystemName), "messaging.system name");
        return Task.CompletedTask;
    }

    private static async Task BinaryBody(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Endpoint(f, "j");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        byte[] body = new byte[64 * 1024];
        new Random(12).NextBytes(body);
        MessageEnvelope sent = Envelope() with { Body = body, ContentType = "application/octet-stream", Protection = "kms:test:key-1" };

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        ReceivedMessage received = (await ReceiveAtLeastAsync(receiver, 1, f, ct).ConfigureAwait(false)).Single();

        Check.True(received.Envelope.Body.Span.SequenceEqual(body), "binary body");
        Check.Equal("kms:test:key-1", received.Envelope.Protection, "protection attribute");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }
}
