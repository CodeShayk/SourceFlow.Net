using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Transport;

namespace SourceFlow.Testing.Conformance;

/// <summary>
/// A transport under test (ADR-014, ADR-033), with its resources isolated by <see cref="Prefix"/>. Emulator fixtures
/// run on pull requests and real-cloud fixtures nightly; cases a fixture cannot drive (clock control, fault injection)
/// are skipped by the runner, never weakened.
/// </summary>
public interface ITransportFixture : IAsyncDisposable
{
    /// <summary>The transport.</summary>
    ITransport Transport { get; }

    /// <summary>A unique prefix for endpoint and topic names in this case.</summary>
    string Prefix { get; }

    /// <summary>True when <see cref="AdvanceClockAsync"/> moves the broker's clock.</summary>
    bool SupportsClockControl { get; }

    /// <summary>Moves the broker clock forward (delays, locks).</summary>
    ValueTask AdvanceClockAsync(TimeSpan by);

    /// <summary>The longest time to wait for a message to arrive.</summary>
    TimeSpan ReceiveTimeout { get; }

    /// <summary>How long to wait to be sure that nothing (more) arrives.</summary>
    TimeSpan QuietPeriod => TimeSpan.FromMilliseconds(300);

    /// <summary>How late a delayed redelivery may arrive on this broker (real time only).</summary>
    TimeSpan DelayTolerance => TimeSpan.FromSeconds(2);

    /// <summary>Fault injection (emulators and in-memory); null on real cloud, where those cases are skipped.</summary>
    ITransportFaults? Faults => null;

    /// <summary>
    /// Breaks a provisioned resource of <paramref name="topology"/> in the way <paramref name="defect"/> describes, so
    /// <see cref="ProvisioningMode.Validate"/> can be checked. Returns false when the broker cannot express that defect.
    /// </summary>
    ValueTask<bool> InjectDefectAsync(TopologyProblemKind defect, TransportTopology topology, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(false);
    }
}

/// <summary>Broker fault injection for the resilience cases (TCK-32..34).</summary>
public interface ITransportFaults
{
    /// <summary>While true, every broker call fails as in an outage.</summary>
    bool Unavailable { get; set; }

    /// <summary>Throttles the next <paramref name="count"/> calls.</summary>
    void ThrottleNext(int count, TimeSpan retryAfter);

    /// <summary>Expires the credentials; the transport must refresh them on the next call.</summary>
    void ExpireCredentials();

    /// <summary>How many times credentials were refreshed.</summary>
    int CredentialRefreshes { get; }

    /// <summary>Delivers the next <paramref name="count"/> sent messages twice (broker duplicates).</summary>
    void DuplicateNext(int count);
}

/// <summary>
/// The transport conformance kit, TCK-01..45 (ADR-014, plan 06 §3): every transport must pass every case it can run
/// for GA parity. Run each case against a fresh fixture.
/// </summary>
public static class TransportConformance
{
    /// <summary>Every case, ordered by id.</summary>
    public static IReadOnlyList<ConformanceCase<ITransportFixture>> Cases { get; } =
    [
        // Envelope
        new("TCK-01", "Binary mode: every attribute, extension and header round-trips", BinaryRoundTrip),
        new("TCK-02", "Structured mode: a structured CloudEvent document round-trips untouched", StructuredRoundTrip),
        new("TCK-03", "Unicode in subject, tenant, actor, headers and body survives", UnicodeSurvives),
        new("TCK-04", "Extensions beyond the attribute budget overflow into sfext and come back", AttributeOverflow),
        new("TCK-05", "A body of exactly the maximum size is accepted and delivered intact", MaximumBody),
        new("TCK-06", "An oversized body is refused before it is sent, and nothing is delivered", OversizedBody),

        // Send and publish
        new("TCK-07", "Send to an endpoint: delivered once, first delivery count 1", SendReceive),
        new("TCK-08", "A batch with a bad message reports exactly that message and sends the others", PartialBatch),
        new("TCK-09", "A send larger than the batch limit is split and fully delivered in order", BatchLimits),
        new("TCK-10", "Publish to a topic reaches the subscribed endpoint", PublishReceive),
        new("TCK-11", "A missing topic fails Validate and publishing to it fails", MissingTopic),
        new("TCK-12", "Fan-out: 2 services x 2 instances, each service gets each event once, instances compete", FanOut),

        // Ordering
        new("TCK-13", "Ordered endpoint: 10,000 messages over 100 keys and 3 consumers keep per-key order", OrderedAtScale),
        new("TCK-14", "Unordered endpoint: an in-flight message does not block the next one", NoHeadOfLineBlocking),
        new("TCK-15", "Ordered endpoint: ordering survives a redelivery", OrderSurvivesRedelivery),

        // Deduplication
        new("TCK-16", "Broker deduplication behaves as the capabilities declare", DeclaredDeduplication),
        new("TCK-17", "Distinct message ids with identical bodies are never deduplicated", DistinctIdsDelivered),
        new("TCK-18", "With the core: a producer retry with the same message id has one effect", InboxAbsorbsRetries),

        // Settlement
        new("TCK-19", "Complete removes the message", CompleteRemoves),
        new("TCK-20", "RetryLater redelivers after the delay, not before, with a higher delivery count", DelayedRedelivery),
        new("TCK-21", "Renew extends the lock", RenewExtendsLock),
        new("TCK-22", "A crash before complete means a redelivery after the lock", CrashRedelivers),

        // Dead letters
        new("TCK-23", "After the maximum deliveries the broker dead-letters the message", NativeDeadLetter),
        new("TCK-24", "The ingestor parks broker dead letters with the envelope intact", IngestorParks),
        new("TCK-25", "A parked broker dead letter replays to its endpoint and takes effect", ReplayIngested),

        // Filters
        new("TCK-26", "A type filter delivers only the listed types", TypeFilter),
        new("TCK-27", "A CESQL filter with LIKE and an extension delivers only matching events", CesqlFilter),
        new("TCK-28", "Filtering matches the reference CESQL evaluation exactly", FilterEquivalence),

        // Correlation
        new("TCK-29", "Correlation and causation survive a chain of hops", CorrelationChain),
        new("TCK-30", "Trace context continues across hops", TraceContinues),
        new("TCK-31", "Tenant and actor survive a chain of hops", TenantChain),

        // Resilience
        new("TCK-32", "After an outage the same receiver recovers without a restart", OutageRecovery, RequiresFaultInjection: true),
        new("TCK-33", "Throttling is reported as transient with a retry-after", Throttling, RequiresFaultInjection: true),
        new("TCK-34", "Expired credentials are refreshed without failing the call", CredentialRefresh, RequiresFaultInjection: true),

        // Provisioning
        new("TCK-35", "CreateIfMissing is idempotent and leaves a valid topology", ProvisioningIdempotent),
        new("TCK-36", "Validate reports every missing endpoint, topic and subscription at once", ValidateReportsAll),
        new("TCK-37", "Validate detects an ordering mismatch", ValidateOrdering),
        new("TCK-38", "Validate detects a missing dead-letter queue", ValidateDeadLetter),
        new("TCK-39", "Validate detects a missing or misconfigured subscription", ValidateSubscription),
        new("TCK-40", "Validate detects subscription filter drift", ValidateFilter),

        // Payload protection (transport side; the core decrypts, WP-5.1)
        new("TCK-41", "A protected envelope (marker and ciphertext) round-trips byte for byte", ProtectedRoundTrip),
        new("TCK-42", "Bodies are never transcoded: any content type and any bytes", OpaqueBody),
        new("TCK-43", "A tampered ciphertext is delivered as is, for the core to reject", TamperedDelivered),

        // Shutdown and end to end
        new("TCK-44", "Shutdown: completed messages stay completed, the rest are redelivered", ShutdownRedelivers),
        new("TCK-45", "With the core: remote commands once under duplicates, integration events once per service", EndToEnd),
    ];

    // ================================================================ helpers

    private static string Name(ITransportFixture f, string name)
    {
        return $"{f.Prefix}.{name}";
    }

    private static MessageEnvelope Envelope(string? key = null, string body = "{\"n\":1}", string type = "tck.sample", int version = 1)
    {
        return new MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = type,
            TypeVersion = version,
            Source = "tck",
            Time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PartitionKey = key,
            Body = Encoding.UTF8.GetBytes(body),
        };
    }

    private static TransportTopology Topology(string endpoint, IReadOnlyList<string>? subscribed = null, IReadOnlyList<string>? published = null, bool ordered = false, int maxDeliveries = 10, IReadOnlyDictionary<string, string>? filters = null)
    {
        return new TransportTopology(endpoint, published ?? [], subscribed ?? [], [])
        {
            OrderedEndpoint = ordered,
            OrderedTopics = ordered ? published ?? [] : [],
            MaxDeliveries = maxDeliveries,
            Filters = filters ?? new Dictionary<string, string>(),
        };
    }

    private static async Task<ITransportReceiver> ReceiverAsync(ITransportFixture f, TransportTopology topology, CancellationToken ct, TimeSpan? lockDuration = null)
    {
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, ct).ConfigureAwait(false);
        return await f.Transport.CreateReceiverAsync(new ReceiverOptions(topology.Endpoint, topology.SubscribedTopics)
        {
            Filters = topology.Filters,
            LockDuration = lockDuration ?? TimeSpan.FromSeconds(30),
        }, ct).ConfigureAwait(false);
    }

    private static Task<ITransportReceiver> ReceiverAsync(ITransportFixture f, string endpoint, CancellationToken ct, params string[] topics)
    {
        return ReceiverAsync(f, Topology(endpoint, topics), ct);
    }

    private static async Task<List<ReceivedMessage>> ReceiveAtLeastAsync(ITransportReceiver receiver, int count, ITransportFixture f, CancellationToken ct)
    {
        var received = new List<ReceivedMessage>();
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + f.ReceiveTimeout;
        while (received.Count < count && TimeProvider.System.GetUtcNow() < deadline)
        {
            received.AddRange(await receiver.ReceiveAsync(Math.Min(10, count - received.Count), TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false));
        }

        Check.True(received.Count >= count, $"expected {count} message(s) within {f.ReceiveTimeout}, got {received.Count}");
        return received;
    }

    private static async Task<ReceivedMessage> ReceiveOneAsync(ITransportReceiver receiver, ITransportFixture f, CancellationToken ct)
    {
        return (await ReceiveAtLeastAsync(receiver, 1, f, ct).ConfigureAwait(false)).Single();
    }

    private static async Task<IReadOnlyList<ReceivedMessage>> DrainAsync(ITransportReceiver receiver, ITransportFixture f, CancellationToken ct)
    {
        var received = new List<ReceivedMessage>();
        DateTimeOffset quietUntil = TimeProvider.System.GetUtcNow() + f.QuietPeriod;
        while (TimeProvider.System.GetUtcNow() < quietUntil)
        {
            IReadOnlyList<ReceivedMessage> batch = await receiver.ReceiveAsync(10, TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            foreach (ReceivedMessage message in batch)
            {
                await message.CompleteAsync(ct).ConfigureAwait(false);
            }

            received.AddRange(batch);
            if (batch.Count > 0)
            {
                quietUntil = TimeProvider.System.GetUtcNow() + f.QuietPeriod;
            }
        }

        return received;
    }

    private static async Task NothingAsync(ITransportReceiver receiver, ITransportFixture f, string what, CancellationToken ct)
    {
        IReadOnlyList<ReceivedMessage> unexpected = await receiver.ReceiveAsync(10, f.QuietPeriod, ct).ConfigureAwait(false);
        Check.Equal(0, unexpected.Count, what);
    }

    // Lets time pass on the broker: the fake clock when the fixture has one, the real clock otherwise.
    private static async Task PassAsync(ITransportFixture f, TimeSpan by, CancellationToken ct)
    {
        if (f.SupportsClockControl)
        {
            await f.AdvanceClockAsync(by).ConfigureAwait(false);
        }
        else
        {
            await Task.Delay(by, TimeProvider.System, ct).ConfigureAwait(false);
        }
    }

    private static async Task CompleteAllAsync(IEnumerable<ReceivedMessage> messages, CancellationToken ct)
    {
        foreach (ReceivedMessage message in messages)
        {
            await message.CompleteAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action, string what)
        where T : Exception
    {
        return await Check.ThrowsAsync<T>(action, what).ConfigureAwait(false);
    }

    private static async Task Eventually(Func<Task<bool>> condition, ITransportFixture f, string what, SourceFlowTestHost? host = null)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + (f.ReceiveTimeout * 4);
        while (!await condition().ConfigureAwait(false))
        {
            if (TimeProvider.System.GetUtcNow() >= deadline)
            {
                // Name what the service parked, so a failure says why the effect is missing.
                string parked = host is null ? string.Empty : string.Join("; ", (await host.DeadLetters.ListAsync(new DeadLetterQuery()).ConfigureAwait(false))
                    .Select(d => $"{d.Envelope.Type} {d.Reason}: {d.ErrorMessage}"));
                throw new ConformanceException($"timed out waiting for {what}{(parked.Length > 0 ? " (parked: " + parked + ")" : string.Empty)}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), TimeProvider.System).ConfigureAwait(false);
        }
    }

    // ================================================================ envelope

    private static async Task BinaryRoundTrip(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "binary");
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
            Headers = new Dictionary<string, string> { ["x-origin"] = "web", ["x-request"] = "r-1" },
        };

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        ReceivedMessage received = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);

        Check.Equal(sent, received.Envelope, "every attribute");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task StructuredRoundTrip(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "structured");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        MessageEnvelope inner = Envelope("k") with { Subject = "s-1", Headers = new Dictionary<string, string> { ["x-a"] = "1" } };
        MessageEnvelope sent = Envelope() with { ContentType = CloudEvents.StructuredContentType, Body = CloudEvents.ToStructured(inner) };

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        ReceivedMessage received = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);

        Check.Equal(CloudEvents.StructuredContentType, received.Envelope.ContentType, "structured content type");
        Check.Equal(inner, CloudEvents.FromStructured(received.Envelope.Body), "the structured event inside");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task UnicodeSurvives(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "unicode");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        MessageEnvelope sent = Envelope("clé-🔑", "{\"name\":\"Zoë 日本 🚀\"}") with
        {
            Subject = "compte-ñ-😀",
            TenantId = "租户-1",
            Actor = "üser:Ωmega",
            Headers = new Dictionary<string, string> { ["x-üñí"] = "çødé ✓", ["x-emoji"] = "👍🏽" },
        };

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        ReceivedMessage received = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);

        Check.Equal(sent, received.Envelope, "unicode attributes and body");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task AttributeOverflow(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "overflow");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        var headers = Enumerable.Range(1, EnvelopeLimits.MaxHeaders).ToDictionary(i => $"x-h{i}", i => new string('v', 64) + i);
        MessageEnvelope sent = Envelope("k") with
        {
            CorrelationId = new CorrelationId("c"),
            CausationId = new MessageId(Guid.CreateVersion7()),
            Subject = "s",
            StreamVersion = 1,
            TenantId = "t",
            Actor = "a",
            TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            TraceState = "k=v",
            Protection = "kms:k",
            Headers = headers,
        };
        int attributes = CloudEvents.ToAttributes(sent).Count;

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        ReceivedMessage received = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);

        Check.Equal(sent, received.Envelope, $"{attributes} attributes through a budget of {f.Transport.Capabilities.MaxAttributes}");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task MaximumBody(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "max");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        byte[] body = new byte[f.Transport.Capabilities.MaxBodyBytes];
        new Random(5).NextBytes(body);
        MessageEnvelope sent = Envelope() with { ContentType = "application/octet-stream", Body = body };

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        ReceivedMessage received = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);

        Check.True(received.Envelope.Body.Span.SequenceEqual(body), "the maximum body intact");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task OversizedBody(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "oversized");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        MessageEnvelope tooLarge = Envelope() with { Body = new byte[f.Transport.Capabilities.MaxBodyBytes + 1] };

        TransportSendException error = await ThrowsAsync<TransportSendException>(() => f.Transport.SendAsync(endpoint, [tooLarge], ct).AsTask(), "oversized body").ConfigureAwait(false);

        Check.Equal(tooLarge.MessageId, error.Failures.Single().MessageId, "the failed message");
        Check.True(error.Failures[0].Error is MessageTooLargeException, "refused for its size");
        Check.True(!error.IsTransient, "not retryable");
        await NothingAsync(receiver, f, "nothing delivered", ct).ConfigureAwait(false);
    }

    // ================================================================ send and publish

    private static async Task SendReceive(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "send");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        MessageEnvelope sent = Envelope();

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        ReceivedMessage received = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);

        Check.Equal(sent, received.Envelope, "envelope");
        Check.Equal(1, received.DeliveryCount, "first delivery");
        await received.CompleteAsync(ct).ConfigureAwait(false);
        await NothingAsync(receiver, f, "delivered once", ct).ConfigureAwait(false);
    }

    private static async Task PartialBatch(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "partial");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        MessageEnvelope first = Envelope();
        MessageEnvelope bad = Envelope() with { Body = new byte[f.Transport.Capabilities.MaxBodyBytes + 1] };
        MessageEnvelope last = Envelope();

        TransportSendException error = await ThrowsAsync<TransportSendException>(() => f.Transport.SendAsync(endpoint, [first, bad, last], ct).AsTask(), "partial failure").ConfigureAwait(false);

        Check.SequenceEqual([bad.MessageId], error.Failures.Select(x => x.MessageId), "only the bad message is reported");
        Check.Equal(2, error.Sent, "the others were sent");
        IReadOnlyList<ReceivedMessage> received = await DrainAsync(receiver, f, ct).ConfigureAwait(false);
        Check.SequenceEqual(new[] { first.MessageId, last.MessageId }.Order(), received.Select(r => r.Envelope.MessageId).Order(), "the good messages arrived");
    }

    private static async Task BatchLimits(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "batches");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        int count = (3 * Math.Max(1, f.Transport.Capabilities.MaxBatchSize)) + 1;
        MessageEnvelope[] sent = [.. Enumerable.Range(0, count).Select(i => Envelope("one-key", $"{{\"n\":{i}}}"))];

        await f.Transport.SendAsync(endpoint, sent, ct).ConfigureAwait(false);
        var order = new List<MessageId>();
        while (order.Count < count)
        {
            foreach (ReceivedMessage message in await ReceiveAtLeastAsync(receiver, 1, f, ct).ConfigureAwait(false))
            {
                order.Add(message.Envelope.MessageId);
                await message.CompleteAsync(ct).ConfigureAwait(false);
            }
        }

        Check.SequenceEqual(sent.Select(s => s.MessageId), order, "every message, in order");
    }

    private static async Task PublishReceive(ITransportFixture f, CancellationToken ct)
    {
        string topic = Name(f, "events");
        await using ITransportReceiver receiver = await ReceiverAsync(f, Name(f, "subscriber"), ct, topic).ConfigureAwait(false);
        MessageEnvelope published = Envelope("k");

        await f.Transport.PublishAsync(topic, [published], ct).ConfigureAwait(false);
        ReceivedMessage received = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);

        Check.Equal(published, received.Envelope, "published envelope");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task MissingTopic(ITransportFixture f, CancellationToken ct)
    {
        string topic = Name(f, "missing-topic");
        TransportTopology topology = Topology(Name(f, "publisher"), published: [topic]);
        await f.Transport.ProvisionAsync(topology with { PublishedTopics = [] }, ProvisioningMode.CreateIfMissing, ct).ConfigureAwait(false);

        TopologyValidationException invalid = await ThrowsAsync<TopologyValidationException>(() => f.Transport.ProvisionAsync(topology, ProvisioningMode.Validate, ct).AsTask(), "validate missing").ConfigureAwait(false);
        Check.True(invalid.Problems.Any(p => p.Kind == TopologyProblemKind.MissingTopic && p.Resource == topic), "the missing topic is named");
        await ThrowsAsync<DestinationNotFoundException>(() => f.Transport.PublishAsync(topic, [Envelope()], ct).AsTask(), "publish to a missing topic").ConfigureAwait(false);
    }

    private static async Task FanOut(ITransportFixture f, CancellationToken ct)
    {
        string topic = Name(f, "fanout");
        string a = Name(f, "svc-a");
        string b = Name(f, "svc-b");
        await using ITransportReceiver a1 = await ReceiverAsync(f, a, ct, topic).ConfigureAwait(false);
        await using ITransportReceiver a2 = await ReceiverAsync(f, a, ct, topic).ConfigureAwait(false);
        await using ITransportReceiver b1 = await ReceiverAsync(f, b, ct, topic).ConfigureAwait(false);
        await using ITransportReceiver b2 = await ReceiverAsync(f, b, ct, topic).ConfigureAwait(false);
        MessageEnvelope[] published = [.. Enumerable.Range(0, 20).Select(i => Envelope("k" + i))];

        await f.Transport.PublishAsync(topic, published, ct).ConfigureAwait(false);

        foreach ((string service, ITransportReceiver one, ITransportReceiver two) in new[] { ("a", a1, a2), ("b", b1, b2) })
        {
            var seen = new List<MessageId>();
            DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + f.ReceiveTimeout;
            while (seen.Count < published.Length && TimeProvider.System.GetUtcNow() < deadline)
            {
                foreach (ITransportReceiver instance in new[] { one, two })
                {
                    IReadOnlyList<ReceivedMessage> batch = await instance.ReceiveAsync(3, TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
                    seen.AddRange(batch.Select(m => m.Envelope.MessageId));
                    await CompleteAllAsync(batch, ct).ConfigureAwait(false);
                }
            }

            Check.Equal(seen.Count, seen.Distinct().Count(), $"service {service}: no event twice across its instances");
            Check.SequenceEqual(published.Select(p => p.MessageId).Order(), seen.Order(), $"service {service}: every event once");
            await NothingAsync(one, f, $"service {service}: nothing more", ct).ConfigureAwait(false);
        }
    }

    // ================================================================ ordering

    private static async Task OrderedAtScale(ITransportFixture f, CancellationToken ct)
    {
        if (!f.Transport.Capabilities.OrderedDelivery)
        {
            return;   // the capability is declared off: the core's per-instance order applies (RX-R1)
        }

        TransportTopology topology = Topology(Name(f, "ordered"), ordered: true);
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, ct).ConfigureAwait(false);
        const int Keys = 100;
        const int PerKey = 100;
        MessageEnvelope[] sent = [.. Enumerable.Range(0, PerKey).SelectMany(n => Enumerable.Range(0, Keys).Select(k => Envelope($"key-{k}", $"{{\"key\":{k},\"n\":{n}}}")))];
        await f.Transport.SendAsync(topology.Endpoint, sent, ct).ConfigureAwait(false);

        var positions = sent.Select((s, i) => (s.MessageId, i)).ToDictionary(x => x.MessageId, x => x.i);
        var lastByKey = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<MessageId, bool>();
        int received = 0;
        string? violation = null;
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + (f.ReceiveTimeout * 20);

        async Task ConsumeAsync(int consumer)
        {
            await using ITransportReceiver receiver = await f.Transport.CreateReceiverAsync(new ReceiverOptions(topology.Endpoint, []), ct).ConfigureAwait(false);
            while (Volatile.Read(ref received) < sent.Length && TimeProvider.System.GetUtcNow() < deadline)
            {
                foreach (ReceivedMessage message in await receiver.ReceiveAsync(10, TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false))
                {
                    // Delivery is at least once: a message whose lock lapsed under load may come again after later ones of
                    // its key were handled. The inbox absorbs such a duplicate, so order is checked on first deliveries.
                    if (!seen.TryAdd(message.Envelope.MessageId, true))
                    {
                        await message.CompleteAsync(ct).ConfigureAwait(false);
                        continue;
                    }

                    string key = message.Envelope.PartitionKey!;
                    int position = positions[message.Envelope.MessageId];
                    int previous = lastByKey.GetValueOrDefault(key, -1);
                    if (position <= previous)
                    {
                        // The delivery count tells a redelivery (2+, a lock that lapsed) from a message the broker held back (1).
                        violation ??= $"{key}: position {position} after {previous} (consumer {consumer}, delivery {message.DeliveryCount})";
                    }

                    lastByKey[key] = position;
                    Interlocked.Increment(ref received);
                    await message.CompleteAsync(ct).ConfigureAwait(false);
                }
            }
        }

        await Task.WhenAll(ConsumeAsync(1), ConsumeAsync(2), ConsumeAsync(3)).ConfigureAwait(false);

        Check.Null(violation, "per-key order across 3 consumers");
        Check.Equal(sent.Length, received, "every message delivered");
    }

    private static async Task NoHeadOfLineBlocking(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "unordered");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        MessageEnvelope first = Envelope("same");
        MessageEnvelope second = Envelope("same");
        await f.Transport.SendAsync(endpoint, [first, second], ct).ConfigureAwait(false);

        var held = new List<ReceivedMessage> { await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false) };
        held.Add(await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false));   // while the first is still in flight

        Check.SequenceEqual(new[] { first.MessageId, second.MessageId }.Order(), held.Select(h => h.Envelope.MessageId).Order(), "both in flight at once");
        await CompleteAllAsync(held, ct).ConfigureAwait(false);
    }

    private static async Task OrderSurvivesRedelivery(ITransportFixture f, CancellationToken ct)
    {
        if (!f.Transport.Capabilities.OrderedDelivery)
        {
            return;
        }

        await using ITransportReceiver receiver = await ReceiverAsync(f, Topology(Name(f, "ordered-retry"), ordered: true), ct).ConfigureAwait(false);
        MessageEnvelope first = Envelope("k");
        MessageEnvelope second = Envelope("k");
        await f.Transport.SendAsync(Name(f, "ordered-retry"), [first, second], ct).ConfigureAwait(false);

        ReceivedMessage head = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);
        Check.Equal(first.MessageId, head.Envelope.MessageId, "the head first");
        await NothingAsync(receiver, f, "the next one waits for the head", ct).ConfigureAwait(false);
        await head.AbandonAsync(TimeSpan.Zero, ct).ConfigureAwait(false);

        ReceivedMessage again = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);
        Check.Equal(first.MessageId, again.Envelope.MessageId, "the head again after its redelivery");
        await again.CompleteAsync(ct).ConfigureAwait(false);
        ReceivedMessage next = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);
        Check.Equal(second.MessageId, next.Envelope.MessageId, "then the next");
        await next.CompleteAsync(ct).ConfigureAwait(false);
    }

    // ================================================================ deduplication

    private static async Task DeclaredDeduplication(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "dedup");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        MessageEnvelope sent = Envelope("k");
        TransportCapabilities capabilities = f.Transport.Capabilities;
        Check.True(capabilities.Deduplication == (capabilities.DeduplicationWindow > TimeSpan.Zero), "a deduplication window exactly when deduplication is declared");

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);   // a producer retry
        IReadOnlyList<ReceivedMessage> received = await DrainAsync(receiver, f, ct).ConfigureAwait(false);

        Check.True(received.All(r => r.Envelope.MessageId == sent.MessageId), "the same message");
        Check.Equal(capabilities.Deduplication ? 1 : 2, received.Count, "deliveries as declared (the inbox absorbs the rest)");
    }

    private static async Task DistinctIdsDelivered(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "distinct");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        MessageEnvelope one = Envelope("k", "{\"same\":true}");
        MessageEnvelope two = one with { MessageId = new MessageId(Guid.CreateVersion7()) };

        await f.Transport.SendAsync(endpoint, [one, two], ct).ConfigureAwait(false);

        Check.Equal(2, (await DrainAsync(receiver, f, ct).ConfigureAwait(false)).Count, "both delivered");
    }

    private static async Task InboxAbsorbsRetries(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "owner");
        await using SourceFlowTestHost owner = await HostAsync(f, "tck-owner", endpoint, ct).ConfigureAwait(false);
        var tally = TckTallyId.New();
        MessageEnvelope command = Command(new TckAdd(tally, 5));
        f.Faults?.DuplicateNext(1);

        await f.Transport.SendAsync(endpoint, [command], ct).ConfigureAwait(false);
        await f.Transport.SendAsync(endpoint, [command], ct).ConfigureAwait(false);   // the producer retries

        StreamId stream = StreamId.For(TckTally.AggregateName, tally);
        await Eventually(async () => (await owner.EventsAsync(stream).ConfigureAwait(false)).Count > 0, f, "the command's effect", owner).ConfigureAwait(false);
        await Task.Delay(f.QuietPeriod * 3, TimeProvider.System, ct).ConfigureAwait(false);
        Check.Equal(1, (await owner.EventsAsync(stream).ConfigureAwait(false)).Count, "one effect for every copy");
    }

    // ================================================================ settlement

    private static async Task CompleteRemoves(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "complete");
        await using ITransportReceiver receiver = await ReceiverAsync(f, Topology(endpoint), ct, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await f.Transport.SendAsync(endpoint, [Envelope()], ct).ConfigureAwait(false);

        ReceivedMessage first = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);
        await first.CompleteAsync(ct).ConfigureAwait(false);
        await PassAsync(f, TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);   // past the lock

        await NothingAsync(receiver, f, "not redelivered", ct).ConfigureAwait(false);
    }

    private static async Task DelayedRedelivery(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "delay");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        await f.Transport.SendAsync(endpoint, [Envelope()], ct).ConfigureAwait(false);
        ReceivedMessage first = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);
        TimeSpan delay = f.SupportsClockControl ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(3);

        await first.AbandonAsync(delay, ct).ConfigureAwait(false);
        if (f.SupportsClockControl)
        {
            await f.AdvanceClockAsync(delay - TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        await NothingAsync(receiver, f, "hidden during the delay", ct).ConfigureAwait(false);
        await PassAsync(f, f.SupportsClockControl ? TimeSpan.FromSeconds(2) : delay, ct).ConfigureAwait(false);

        ReceivedMessage again = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);
        Check.Equal(first.Envelope.MessageId, again.Envelope.MessageId, "redelivered after the delay");
        Check.True(again.DeliveryCount > first.DeliveryCount, "the delivery count increases");
        await again.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task RenewExtendsLock(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "renew");
        TimeSpan lockDuration = TimeSpan.FromSeconds(4);
        await using ITransportReceiver holder = await ReceiverAsync(f, Topology(endpoint), ct, lockDuration).ConfigureAwait(false);
        await using ITransportReceiver other = await f.Transport.CreateReceiverAsync(new ReceiverOptions(endpoint, []) { LockDuration = lockDuration }, ct).ConfigureAwait(false);
        await f.Transport.SendAsync(endpoint, [Envelope()], ct).ConfigureAwait(false);
        ReceivedMessage held = await ReceiveOneAsync(holder, f, ct).ConfigureAwait(false);

        for (int i = 0; i < 3; i++)
        {
            await PassAsync(f, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            await held.RenewAsync(ct).ConfigureAwait(false);
        }

        await NothingAsync(other, f, "still locked after 6 s of a 4 s lock, renewed", ct).ConfigureAwait(false);
        await held.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task CrashRedelivers(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "crash");
        TimeSpan lockDuration = TimeSpan.FromSeconds(3);
        ITransportReceiver crashed = await ReceiverAsync(f, Topology(endpoint), ct, lockDuration).ConfigureAwait(false);
        await using ITransportReceiver survivor = await f.Transport.CreateReceiverAsync(new ReceiverOptions(endpoint, []) { LockDuration = lockDuration }, ct).ConfigureAwait(false);
        MessageEnvelope sent = Envelope();
        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);

        ReceivedMessage lost = await ReceiveOneAsync(crashed, f, ct).ConfigureAwait(false);   // the process dies holding it
        await PassAsync(f, lockDuration + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

        ReceivedMessage redelivered = await ReceiveOneAsync(survivor, f, ct).ConfigureAwait(false);
        Check.Equal(sent.MessageId, redelivered.Envelope.MessageId, "redelivered to another instance");
        Check.True(redelivered.DeliveryCount > lost.DeliveryCount, "as a redelivery");
        await redelivered.CompleteAsync(ct).ConfigureAwait(false);
        await lost.CompleteAsync(ct).ConfigureAwait(false);   // a late settlement of a lost lock is harmless
        await crashed.DisposeAsync().ConfigureAwait(false);
        await NothingAsync(survivor, f, "completed once", ct).ConfigureAwait(false);
    }

    // ================================================================ dead letters

    private static async Task NativeDeadLetter(ITransportFixture f, CancellationToken ct)
    {
        if (!f.Transport.Capabilities.NativeDeadLetter)
        {
            return;
        }

        string endpoint = Name(f, "dlq");
        await using ITransportReceiver receiver = await ReceiverAsync(f, Topology(endpoint, maxDeliveries: 2), ct).ConfigureAwait(false);
        await using ITransportReceiver deadLetters = await f.Transport.CreateDeadLetterReceiverAsync(endpoint, ct).ConfigureAwait(false);
        MessageEnvelope poison = Envelope("p") with { Headers = new Dictionary<string, string> { ["x-keep"] = "me" } };
        MessageEnvelope explicitPoison = Envelope("q");
        await f.Transport.SendAsync(endpoint, [poison, explicitPoison], ct).ConfigureAwait(false);

        foreach (ReceivedMessage message in await ReceiveAtLeastAsync(receiver, 2, f, ct).ConfigureAwait(false))
        {
            await (message.Envelope.MessageId == poison.MessageId
                ? message.AbandonAsync(TimeSpan.Zero, ct)
                : message.DeadLetterAsync("tck-poison", ct)).ConfigureAwait(false);
        }

        ReceivedMessage second = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);
        Check.Equal(poison.MessageId, second.Envelope.MessageId, "the second delivery");
        await second.AbandonAsync(TimeSpan.Zero, ct).ConfigureAwait(false);   // the maximum is reached
        await NothingAsync(receiver, f, "not delivered a third time", ct).ConfigureAwait(false);

        List<ReceivedMessage> dead = await ReceiveAtLeastAsync(deadLetters, 2, f, ct).ConfigureAwait(false);
        ReceivedMessage exhausted = dead.Single(d => d.Envelope.MessageId == poison.MessageId);
        Check.Equal(poison, exhausted.Envelope, "the envelope and headers intact");
        Check.True(!string.IsNullOrEmpty(exhausted.DeadLetterReason), "a reason for the max-delivery dead letter");
        Check.Equal("tck-poison", dead.Single(d => d.Envelope.MessageId == explicitPoison.MessageId).DeadLetterReason, "the explicit reason");
        await CompleteAllAsync(dead, ct).ConfigureAwait(false);
    }

    private static async Task IngestorParks(ITransportFixture f, CancellationToken ct)
    {
        if (!f.Transport.Capabilities.NativeDeadLetter)
        {
            return;
        }

        string endpoint = Name(f, "owner");
        MessageEnvelope command = await DeadLetterCommandAsync(f, endpoint, ct).ConfigureAwait(false);
        await using SourceFlowTestHost owner = await HostAsync(f, "tck-owner", endpoint, ct).ConfigureAwait(false);

        DeadLetter? parked = null;
        await Eventually(async () => (parked = (await owner.DeadLetters.ListAsync(new DeadLetterQuery(), ct).ConfigureAwait(false)).FirstOrDefault()) is not null, f, "the ingested dead letter").ConfigureAwait(false);

        Check.Equal(command, parked!.Envelope, "the envelope and headers intact");
        Check.Equal("tck-poison", parked.Reason, "the broker's reason");
        Check.Equal(endpoint, parked.Destination, "replayable to its endpoint");
    }

    private static async Task ReplayIngested(ITransportFixture f, CancellationToken ct)
    {
        if (!f.Transport.Capabilities.NativeDeadLetter)
        {
            return;
        }

        string endpoint = Name(f, "owner");
        MessageEnvelope command = await DeadLetterCommandAsync(f, endpoint, ct).ConfigureAwait(false);
        await using SourceFlowTestHost owner = await HostAsync(f, "tck-owner", endpoint, ct).ConfigureAwait(false);
        DeadLetter? parked = null;
        await Eventually(async () => (parked = (await owner.DeadLetters.ListAsync(new DeadLetterQuery(), ct).ConfigureAwait(false)).FirstOrDefault()) is not null, f, "the ingested dead letter").ConfigureAwait(false);

        await owner.DeadLetters.ReplayAsync(parked!.Id, "tck replay", ct).ConfigureAwait(false);   // what `dotnet sourceflow dlq replay` does

        var tally = JsonSerializer.Deserialize(command.Body.Span, TckAddJson)!.TallyId;
        await Eventually(async () => (await owner.EventsAsync(StreamId.For(TckTally.AggregateName, tally)).ConfigureAwait(false)).Count == 1, f, "the replayed command's effect").ConfigureAwait(false);
    }

    // A command the broker has already dead-lettered before the service starts.
    private static async Task<MessageEnvelope> DeadLetterCommandAsync(ITransportFixture f, string endpoint, CancellationToken ct)
    {
        MessageEnvelope command = Command(new TckAdd(TckTallyId.New(), 3)) with { Headers = new Dictionary<string, string> { ["x-origin"] = "tck" }, CorrelationId = new CorrelationId("tck-corr") };
        await using (ITransportReceiver receiver = await ReceiverAsync(f, Topology(endpoint, maxDeliveries: 1 + 4 + 2), ct).ConfigureAwait(false))
        {
            await f.Transport.SendAsync(endpoint, [command], ct).ConfigureAwait(false);
            ReceivedMessage message = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);
            await message.DeadLetterAsync("tck-poison", ct).ConfigureAwait(false);
        }

        return command;
    }

    // ================================================================ filters

    private static async Task TypeFilter(ITransportFixture f, CancellationToken ct)
    {
        string topic = Name(f, "typed");
        string filter = CloudEventFilter.ForTypes(["tck.wanted.v1", "tck.also.v2"]).Expression;
        await using ITransportReceiver receiver = await ReceiverAsync(f, Topology(Name(f, "picky"), [topic], filters: new Dictionary<string, string> { [topic] = filter }), ct).ConfigureAwait(false);
        MessageEnvelope wanted = Envelope(type: "tck.wanted");
        MessageEnvelope also = Envelope(type: "tck.also", version: 2);
        MessageEnvelope other = Envelope(type: "tck.other");
        MessageEnvelope wrongVersion = Envelope(type: "tck.wanted", version: 2);

        await f.Transport.PublishAsync(topic, [wanted, other, also, wrongVersion], ct).ConfigureAwait(false);

        await AssertFilteredAsync(receiver, f, [wanted, also], [wanted, other, also, wrongVersion], CloudEventFilter.Parse(filter), ct).ConfigureAwait(false);
    }

    private static async Task CesqlFilter(ITransportFixture f, CancellationToken ct)
    {
        string topic = Name(f, "cesql");
        const string Filter = "type LIKE 'tck.order.%' AND sftenant = 'eu'";
        await using ITransportReceiver receiver = await ReceiverAsync(f, Topology(Name(f, "eu-orders"), [topic], filters: new Dictionary<string, string> { [topic] = Filter }), ct).ConfigureAwait(false);
        MessageEnvelope match = Envelope(type: "tck.order.placed") with { TenantId = "eu" };
        MessageEnvelope otherTenant = Envelope(type: "tck.order.placed") with { TenantId = "us" };
        MessageEnvelope otherType = Envelope(type: "tck.invoice.sent") with { TenantId = "eu" };

        await f.Transport.PublishAsync(topic, [match, otherTenant, otherType], ct).ConfigureAwait(false);

        await AssertFilteredAsync(receiver, f, [match], [match, otherTenant, otherType], CloudEventFilter.Parse(Filter), ct).ConfigureAwait(false);
    }

    private static async Task FilterEquivalence(ITransportFixture f, CancellationToken ct)
    {
        string[] filters =
        [
            "type = 'tck.a.v1'",
            "type IN ('tck.a.v1', 'tck.b.v1')",
            "type LIKE 'tck.%' AND NOT (subject = 'skip')",
            "EXISTS sfactor OR sfstreamversion > 5",
            "LOWER(source) = 'tck' AND sftenant <> 'us'",
        ];
        string topic = Name(f, "equivalence");
        MessageEnvelope[] events =
        [
            Envelope(type: "tck.a"),
            Envelope(type: "tck.b") with { Subject = "skip" },
            Envelope(type: "tck.c") with { Actor = "user:1", TenantId = "us" },
            Envelope(type: "other.d") with { StreamVersion = 9 },
            Envelope(type: "tck.e") with { StreamVersion = 2, TenantId = "eu" },
        ];
        var receivers = new List<(ITransportReceiver Receiver, CloudEventFilter Filter)>();
        try
        {
            for (int i = 0; i < filters.Length; i++)
            {
                receivers.Add((await ReceiverAsync(f, Topology(Name(f, $"eq-{i}"), [topic], filters: new Dictionary<string, string> { [topic] = filters[i] }), ct).ConfigureAwait(false), CloudEventFilter.Parse(filters[i])));
            }

            await f.Transport.PublishAsync(topic, events, ct).ConfigureAwait(false);
            foreach ((ITransportReceiver receiver, CloudEventFilter filter) in receivers)
            {
                await AssertFilteredAsync(receiver, f, [.. events.Where(filter.Matches)], events, filter, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach ((ITransportReceiver receiver, _) in receivers)
            {
                await receiver.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Native filters deliver exactly the matches; without them the transport delivers everything and the core filters.
    private static async Task AssertFilteredAsync(ITransportReceiver receiver, ITransportFixture f, IReadOnlyList<MessageEnvelope> expected, IReadOnlyList<MessageEnvelope> all, CloudEventFilter filter, CancellationToken ct)
    {
        IReadOnlyList<ReceivedMessage> received = await DrainAsync(receiver, f, ct).ConfigureAwait(false);
        IEnumerable<MessageId> delivered = received.Select(r => r.Envelope.MessageId).Order();
        if (f.Transport.Capabilities.SubscriptionFilters)
        {
            Check.SequenceEqual(expected.Select(e => e.MessageId).Order(), delivered, $"native filter '{filter.Expression}'");
        }
        else
        {
            Check.SequenceEqual(all.Select(e => e.MessageId).Order(), delivered, "no native filter: everything delivered");
            Check.SequenceEqual(expected.Select(e => e.MessageId).Order(), received.Where(r => filter.Matches(r.Envelope)).Select(r => r.Envelope.MessageId).Order(), "client-side evaluation");
        }
    }

    // ================================================================ correlation

    private static async Task<(MessageEnvelope First, MessageEnvelope Second)> TwoHopsAsync(ITransportFixture f, MessageEnvelope first, Func<MessageEnvelope, MessageEnvelope> next, CancellationToken ct)
    {
        string hop1 = Name(f, "hop-1");
        string hop2 = Name(f, "hop-2");
        await using ITransportReceiver one = await ReceiverAsync(f, hop1, ct).ConfigureAwait(false);
        await using ITransportReceiver two = await ReceiverAsync(f, hop2, ct).ConfigureAwait(false);
        await f.Transport.SendAsync(hop1, [first], ct).ConfigureAwait(false);
        ReceivedMessage a = await ReceiveOneAsync(one, f, ct).ConfigureAwait(false);
        await f.Transport.SendAsync(hop2, [next(a.Envelope)], ct).ConfigureAwait(false);
        await a.CompleteAsync(ct).ConfigureAwait(false);
        ReceivedMessage b = await ReceiveOneAsync(two, f, ct).ConfigureAwait(false);
        await b.CompleteAsync(ct).ConfigureAwait(false);
        return (a.Envelope, b.Envelope);
    }

    private static async Task CorrelationChain(ITransportFixture f, CancellationToken ct)
    {
        MessageEnvelope start = Envelope() with { CorrelationId = new CorrelationId("journey-1") };
        (MessageEnvelope first, MessageEnvelope second) = await TwoHopsAsync(f, start, received => Envelope() with
        {
            CorrelationId = received.CorrelationId,
            CausationId = received.MessageId,
        }, ct).ConfigureAwait(false);

        Check.Equal(new CorrelationId("journey-1"), first.CorrelationId, "correlation on hop 1");
        Check.Equal(new CorrelationId("journey-1"), second.CorrelationId, "correlation on hop 2");
        Check.Equal((MessageId?)start.MessageId, second.CausationId, "causation points at hop 1");
    }

    private static async Task TraceContinues(ITransportFixture f, CancellationToken ct)
    {
        const string Parent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        MessageEnvelope start = Envelope() with { TraceParent = Parent, TraceState = "congo=t61rcWkgMzE" };
        (MessageEnvelope first, MessageEnvelope second) = await TwoHopsAsync(f, start, received => Envelope() with
        {
            TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-b7ad6b7169203331-01",   // same trace, the consumer's span
            TraceState = received.TraceState,
        }, ct).ConfigureAwait(false);

        Check.Equal(Parent, first.TraceParent, "traceparent on hop 1");
        Check.Equal("congo=t61rcWkgMzE", second.TraceState, "tracestate continues");
        Check.True(second.TraceParent!.Split('-')[1] == Parent.Split('-')[1], "the same trace id on hop 2");
    }

    private static async Task TenantChain(ITransportFixture f, CancellationToken ct)
    {
        MessageEnvelope start = Envelope() with { TenantId = "tenant-42", Actor = "user:ada" };
        (_, MessageEnvelope second) = await TwoHopsAsync(f, start, received => Envelope() with { TenantId = received.TenantId, Actor = received.Actor }, ct).ConfigureAwait(false);

        Check.Equal("tenant-42", second.TenantId, "tenant");
        Check.Equal("user:ada", second.Actor, "actor");
    }

    // ================================================================ resilience

    private static async Task OutageRecovery(ITransportFixture f, CancellationToken ct)
    {
        ITransportFaults faults = Check.NotNull(f.Faults, "fault injection");
        string endpoint = Name(f, "outage");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);

        faults.Unavailable = true;
        try
        {
            TransportException send = await ThrowsAsync<TransportException>(() => f.Transport.SendAsync(endpoint, [Envelope()], ct).AsTask(), "send during the outage").ConfigureAwait(false);
            Check.True(send.IsTransient, "an outage is transient");
            await ThrowsAsync<TransportException>(() => receiver.ReceiveAsync(1, TimeSpan.Zero, ct).AsTask(), "receive during the outage").ConfigureAwait(false);
        }
        finally
        {
            faults.Unavailable = false;
        }

        MessageEnvelope after = Envelope();
        await f.Transport.SendAsync(endpoint, [after], ct).ConfigureAwait(false);
        ReceivedMessage received = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);
        Check.Equal(after.MessageId, received.Envelope.MessageId, "the same receiver works again");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task Throttling(ITransportFixture f, CancellationToken ct)
    {
        ITransportFaults faults = Check.NotNull(f.Faults, "fault injection");
        string endpoint = Name(f, "throttle");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);

        faults.ThrottleNext(1, TimeSpan.FromSeconds(2));
        TransportException throttled = await ThrowsAsync<TransportException>(() => f.Transport.SendAsync(endpoint, [Envelope()], ct).AsTask(), "throttled").ConfigureAwait(false);

        Check.True(throttled.IsTransient, "throttling is transient");
        Check.True(throttled.RetryAfter is { } retry && retry > TimeSpan.Zero, "with a retry-after");
        Check.Equal(HandlingOutcome.Transient, new ErrorClassifier().Classify(throttled), "the core retries it");
        await f.Transport.SendAsync(endpoint, [Envelope()], ct).ConfigureAwait(false);
        await (await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false)).CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task CredentialRefresh(ITransportFixture f, CancellationToken ct)
    {
        ITransportFaults faults = Check.NotNull(f.Faults, "fault injection");
        string endpoint = Name(f, "credentials");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        int before = faults.CredentialRefreshes;

        faults.ExpireCredentials();
        await f.Transport.SendAsync(endpoint, [Envelope()], ct).ConfigureAwait(false);

        Check.Equal(before + 1, faults.CredentialRefreshes, "refreshed once");
        await (await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false)).CompleteAsync(ct).ConfigureAwait(false);
    }

    // ================================================================ provisioning

    private static TransportTopology FullTopology(ITransportFixture f, string name)
    {
        return TopologyFixtures.Full(Name(f, name));
    }

    private static async Task ProvisioningIdempotent(ITransportFixture f, CancellationToken ct)
    {
        TransportTopology topology = FullTopology(f, "idem");

        await f.Transport.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, ct).ConfigureAwait(false);
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, ct).ConfigureAwait(false);
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.Validate, ct).ConfigureAwait(false);
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.None, ct).ConfigureAwait(false);
    }

    private static async Task ValidateReportsAll(ITransportFixture f, CancellationToken ct)
    {
        TransportTopology topology = FullTopology(f, "nothing");

        TopologyValidationException error = await ThrowsAsync<TopologyValidationException>(() => f.Transport.ProvisionAsync(topology, ProvisioningMode.Validate, ct).AsTask(), "validate an empty broker").ConfigureAwait(false);

        Check.True(error.Problems.Any(p => p.Kind == TopologyProblemKind.MissingEndpoint && p.Resource == topology.Endpoint), "the endpoint");
        Check.True(error.Problems.Any(p => p.Kind == TopologyProblemKind.MissingTopic && p.Resource == topology.PublishedTopics[0]), "the published topic");
        Check.True(error.Problems.Any(p => p.Resource.Contains(topology.SubscribedTopics[0], StringComparison.Ordinal)), "the subscription");
        Check.True(error.Message.Contains(topology.Endpoint, StringComparison.Ordinal), "the report names them");
    }

    private static Task ValidateOrdering(ITransportFixture f, CancellationToken ct)
    {
        return DefectAsync(f, TopologyProblemKind.OrderingMismatch, "ordering", ct);
    }

    private static Task ValidateDeadLetter(ITransportFixture f, CancellationToken ct)
    {
        return DefectAsync(f, TopologyProblemKind.MissingDeadLetter, "dead-letter", ct);
    }

    private static async Task ValidateSubscription(ITransportFixture f, CancellationToken ct)
    {
        TransportTopology topology = FullTopology(f, "subscription");
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, ct).ConfigureAwait(false);
        bool missing = await f.InjectDefectAsync(TopologyProblemKind.MissingSubscription, topology, ct).ConfigureAwait(false);
        bool misconfigured = !missing && await f.InjectDefectAsync(TopologyProblemKind.Misconfigured, topology, ct).ConfigureAwait(false);
        Check.True(missing || misconfigured, "the fixture can break the subscription");

        TopologyValidationException error = await ThrowsAsync<TopologyValidationException>(() => f.Transport.ProvisionAsync(topology, ProvisioningMode.Validate, ct).AsTask(), "validate").ConfigureAwait(false);
        Check.True(error.Problems.Any(p => p.Kind == (missing ? TopologyProblemKind.MissingSubscription : TopologyProblemKind.Misconfigured)), "the subscription problem is reported");
    }

    private static Task ValidateFilter(ITransportFixture f, CancellationToken ct)
    {
        return DefectAsync(f, TopologyProblemKind.FilterDrift, "filter", ct);
    }

    private static async Task DefectAsync(ITransportFixture f, TopologyProblemKind defect, string name, CancellationToken ct)
    {
        TransportTopology topology = FullTopology(f, name);
        await f.Transport.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, ct).ConfigureAwait(false);
        Check.True(await f.InjectDefectAsync(defect, topology, ct).ConfigureAwait(false), $"the fixture can inject {defect}");

        TopologyValidationException error = await ThrowsAsync<TopologyValidationException>(() => f.Transport.ProvisionAsync(topology, ProvisioningMode.Validate, ct).AsTask(), "validate").ConfigureAwait(false);

        TopologyProblem problem = error.Problems.FirstOrDefault(p => p.Kind == defect) ?? throw new ConformanceException($"{defect} was not reported: {error.Message}");
        Check.True(!string.IsNullOrWhiteSpace(problem.Detail), "with expected and actual values");
    }

    // ================================================================ payload protection (transport side)

    private static async Task ProtectedRoundTrip(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "protected");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        byte[] ciphertext = new byte[64 * 1024];
        new Random(12).NextBytes(ciphertext);
        MessageEnvelope sent = Envelope() with { Body = ciphertext, ContentType = "application/octet-stream", Protection = "kms:test:key-1" };

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        ReceivedMessage received = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);

        Check.True(received.Envelope.Body.Span.SequenceEqual(ciphertext), "the ciphertext byte for byte");
        Check.Equal("kms:test:key-1", received.Envelope.Protection, "the protection marker");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task OpaqueBody(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "opaque");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        byte[] notUtf8 = [0xFF, 0xFE, 0x00, 0xC3, 0x28, 0x7B, 0x22];
        MessageEnvelope sent = Envelope() with { Body = notUtf8, ContentType = "application/x-custom; charset=binary" };

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        ReceivedMessage received = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);

        Check.True(received.Envelope.Body.Span.SequenceEqual(notUtf8), "bytes untouched");
        Check.Equal(sent.ContentType, received.Envelope.ContentType, "content type untouched");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task TamperedDelivered(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "tampered");
        await using ITransportReceiver receiver = await ReceiverAsync(f, endpoint, ct).ConfigureAwait(false);
        byte[] tampered = Encoding.UTF8.GetBytes("sfenc1:not-really-a-valid-ciphertext");
        tampered[^1] ^= 0x5A;
        MessageEnvelope sent = Envelope() with { Body = tampered, ContentType = "application/octet-stream", Protection = "kms:test:key-1" };

        await f.Transport.SendAsync(endpoint, [sent], ct).ConfigureAwait(false);
        ReceivedMessage received = await ReceiveOneAsync(receiver, f, ct).ConfigureAwait(false);

        Check.Equal(sent, received.Envelope, "delivered as sent; decryption, and parking as DecryptionFailed, is the core's");
        await received.CompleteAsync(ct).ConfigureAwait(false);
    }

    // ================================================================ shutdown and end to end

    private static async Task ShutdownRedelivers(ITransportFixture f, CancellationToken ct)
    {
        string endpoint = Name(f, "shutdown");
        TimeSpan lockDuration = TimeSpan.FromSeconds(3);
        ITransportReceiver stopping = await ReceiverAsync(f, Topology(endpoint), ct, lockDuration).ConfigureAwait(false);
        MessageEnvelope[] sent = [Envelope(), Envelope(), Envelope()];
        await f.Transport.SendAsync(endpoint, sent, ct).ConfigureAwait(false);

        List<ReceivedMessage> inFlight = await ReceiveAtLeastAsync(stopping, 3, f, ct).ConfigureAwait(false);
        await inFlight[0].CompleteAsync(ct).ConfigureAwait(false);
        await stopping.DisposeAsync().ConfigureAwait(false);   // stops with two unsettled
        await PassAsync(f, lockDuration + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

        await using ITransportReceiver next = await f.Transport.CreateReceiverAsync(new ReceiverOptions(endpoint, []), ct).ConfigureAwait(false);
        IReadOnlyList<ReceivedMessage> redelivered = await DrainAsync(next, f, ct).ConfigureAwait(false);
        Check.SequenceEqual(inFlight.Skip(1).Select(m => m.Envelope.MessageId).Order(), redelivered.Select(r => r.Envelope.MessageId).Order(),
            "the unsettled messages come back, the completed one does not");
    }

    private static async Task EndToEnd(ITransportFixture f, CancellationToken ct)
    {
        string ownerEndpoint = Name(f, "owner");
        string topic = Name(f, "tallies");
        var observations = new TckObservations();
        await using SourceFlowTestHost owner = await HostAsync(f, "tck-owner", ownerEndpoint, ct, observations, r => r.Publish<TckAdded>().AsIs().To(topic)).ConfigureAwait(false);
        await using SourceFlowTestHost reader = await HostAsync(f, "tck-reader", Name(f, "reader"), ct, observations, r => r.Subscribe(topic, s => s.Only<TckAdded>())).ConfigureAwait(false);
        TckTallyId[] tallies = [TckTallyId.New(), TckTallyId.New(), TckTallyId.New()];

        foreach (TckTallyId tally in tallies)
        {
            MessageEnvelope command = Command(new TckAdd(tally, 2));
            f.Faults?.DuplicateNext(1);
            await f.Transport.SendAsync(ownerEndpoint, [command], ct).ConfigureAwait(false);
            await f.Transport.SendAsync(ownerEndpoint, [command], ct).ConfigureAwait(false);
        }

        await Eventually(() => Task.FromResult(observations.Handled.Count(h => h.Service == "tck-reader") >= tallies.Length), f, "the integration events in the reader").ConfigureAwait(false);
        await Task.Delay(f.QuietPeriod * 3, TimeProvider.System, ct).ConfigureAwait(false);

        foreach (TckTallyId tally in tallies)
        {
            Check.Equal(1, (await owner.EventsAsync(StreamId.For(TckTally.AggregateName, tally)).ConfigureAwait(false)).Count, "one append per command");
        }

        foreach (string service in new[] { "tck-owner", "tck-reader" })
        {
            (string Service, MessageId EventId)[] handled = [.. observations.Handled.Where(h => h.Service == service)];
            Check.Equal(tallies.Length, handled.Length, $"{service}: each event handled once");
            Check.Equal(tallies.Length, handled.Select(h => h.EventId).Distinct().Count(), $"{service}: distinct events");
        }
    }

    private static MessageEnvelope Command(TckAdd command)
    {
        return new MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = "tck.tally.add",
            TypeVersion = 1,
            Source = "tck-client",
            Time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Body = JsonSerializer.SerializeToUtf8Bytes(command, TckAddJson),
        };
    }

    // The service's JSON for the kit's command: the source-generated metadata plus the generated id converter.
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<TckAdd> TckAddJson = CreateTckAddJson();

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<TckAdd> CreateTckAddJson()
    {
        var options = new JsonSerializerOptions(TckJsonContext.Default.Options);   // camelCase, like the service
        options.Converters.Add(new TckTallyId.SourceFlowJsonConverter());
        return (System.Text.Json.Serialization.Metadata.JsonTypeInfo<TckAdd>)options.GetTypeInfo(typeof(TckAdd));
    }

    // A service on the transport under test: the kit's domain, an in-memory store, short polling.
    private static Task<SourceFlowTestHost> HostAsync(ITransportFixture f, string service, string endpoint, CancellationToken ct, TckObservations? observations = null, Action<Runtime.RoutingBuilder>? routing = null)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = service;
            sf.AddModule<TckModule>();
            sf.UseTransport(_ => f.Transport);
            sf.Routing(r =>
            {
                r.Endpoint(endpoint);
                routing?.Invoke(r);
            });
            sf.Configure(o =>
            {
                o.PollInterval = TimeSpan.FromMilliseconds(20);
                o.DeadLetterIngestInterval = TimeSpan.FromMilliseconds(200);
            });
        }, store: new InMemoryStore(), services: s => s.AddSingleton(observations ?? new TckObservations()), cancellationToken: ct);
    }
}
