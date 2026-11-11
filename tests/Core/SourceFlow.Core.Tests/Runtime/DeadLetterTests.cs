using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-DLQ-01..10 (plan 05 §3.6, GAP-11): the dead-letter manager.</summary>
[Trait("Category", "Unit")]
public sealed class DeadLetterTests
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-DLQ-01")]
    [Trait("Id", "UT-DLQ-02")]
    [Trait("Id", "UT-DLQ-03")]
    [Trait("Id", "UT-DLQ-04")]
    [Trait("Covers", "GAP-11")]
    public async Task List_FiltersByConsumerReasonTypeAndDate_NewestFirst()
    {
        await using SourceFlowTestHost host = await StartAsync();
        Guid a = await ParkAsync(host, "counters", "PermanentFailure", Monday);
        Guid b = await ParkAsync(host, "counters", "UnknownMessageType", Monday.AddHours(1), type: "x.unknown");
        Guid c = await ParkAsync(host, "projection:x@1", "SubscriptionFailed", Monday.AddDays(1));
        IDeadLetterManager manager = host.DeadLetters;

        (await manager.ListAsync(new DeadLetterQuery(), Ct)).Select(d => d.Id).ShouldBe([c, b, a]);
        (await manager.ListAsync(new DeadLetterQuery(Consumer: "counters"), Ct)).Select(d => d.Id).ShouldBe([b, a]);
        (await manager.ListAsync(new DeadLetterQuery(Reason: "UnknownMessageType"), Ct)).Select(d => d.Id).ShouldBe([b]);
        (await manager.ListAsync(new DeadLetterQuery(MessageType: "x.unknown"), Ct)).Select(d => d.Id).ShouldBe([b]);
        (await manager.ListAsync(new DeadLetterQuery(ParkedAfter: Monday.AddMinutes(30), ParkedBefore: Monday.AddHours(2)), Ct)).Select(d => d.Id).ShouldBe([b]);
        (await manager.ListAsync(new DeadLetterQuery(Limit: 1), Ct)).Select(d => d.Id).ShouldBe([c]);
    }

    [Fact]
    [Trait("Id", "UT-DLQ-05")]
    public async Task Show_ReturnsTheFullEnvelopeAndError()
    {
        await using SourceFlowTestHost host = await StartAsync();
        Guid id = await ParkAsync(host, "counters", "PermanentFailure", Monday);

        DeadLetter shown = (await host.DeadLetters.GetAsync(id, Ct)).ShouldNotBeNull();

        shown.Envelope.Type.ShouldBe("test.counter.increment");
        shown.ErrorMessage.ShouldBe("boom");
        shown.Attempts.ShouldBe(3);
        (await host.DeadLetters.GetAsync(Guid.CreateVersion7(Monday), Ct)).ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "UT-DLQ-06")]
    [Trait("Covers", "ADR-006")]
    public async Task Replay_KeepsTheMessageId_SoTheInboxStillProtects()
    {
        await using SourceFlowTestHost host = await StartAsync();
        var counter = CounterId.New();
        MessageEnvelope command = host.Runtime.CreateEnvelope(new Increment(counter));
        Guid id = await ParkAsync(host, "counters", "PermanentFailure", Monday, command);

        await host.DeadLetters.ReplayAsync(id, "fixed the handler", Ct);
        await host.WaitForIdleAsync();
        await host.Runtime.Transport.SendAsync(host.Runtime.Routing.Endpoint, [command], Ct);   // the same message again
        await host.WaitForIdleAsync();

        (await host.EventsAsync(StreamId.For(Counter.AggregateName, counter))).ShouldHaveSingleItem();
        DeadLetter replayed = (await host.DeadLetters.GetAsync(id, Ct))!;
        replayed.Status.ShouldBe(DeadLetterStatus.Replayed);
        await Should.ThrowAsync<InvalidOperationException>(async () => await host.DeadLetters.ReplayAsync(id, null, Ct));
    }

    [Fact]
    [Trait("Id", "UT-DLQ-07")]
    public async Task Replay_OfASubscriptionDeadLetter_IsNotSupported()
    {
        await using SourceFlowTestHost host = await StartAsync();
        Guid id = await ParkAsync(host, "projection:x@1", "SubscriptionFailed", Monday, destination: null);

        await Should.ThrowAsync<NotSupportedException>(async () => await host.DeadLetters.ReplayAsync(id, null, Ct));
        await Should.ThrowAsync<KeyNotFoundException>(async () => await host.DeadLetters.ReplayAsync(Guid.CreateVersion7(Monday), null, Ct));
    }

    [Fact]
    [Trait("Id", "UT-DLQ-08")]
    public async Task Discard_RecordsTheReason()
    {
        await using SourceFlowTestHost host = await StartAsync();
        Guid id = await ParkAsync(host, "counters", "PermanentFailure", Monday);

        await host.DeadLetters.DiscardAsync(id, "obsolete command", Ct);

        (await host.Store.GetAsync(id, Ct))!.Status.ShouldBe(DeadLetterStatus.Discarded);
        (await host.DeadLetters.ListAsync(new DeadLetterQuery(), Ct)).ShouldBeEmpty();   // parked only, by default
        (await host.DeadLetters.ListAsync(new DeadLetterQuery(Status: DeadLetterStatus.Discarded), Ct)).ShouldHaveSingleItem();
        await Should.ThrowAsync<ArgumentException>(async () => await host.DeadLetters.DiscardAsync(id, " ", Ct));
    }

    [Fact]
    [Trait("Id", "UT-DLQ-09")]
    public async Task Purge_DeletesResolvedDeadLettersBeforeTheCutoff_AndKeepsParkedOnes()
    {
        await using SourceFlowTestHost host = await StartAsync();
        Guid parked = await ParkAsync(host, "counters", "PermanentFailure", Monday);
        Guid discarded = await ParkAsync(host, "counters", "PermanentFailure", Monday);
        await host.DeadLetters.DiscardAsync(discarded, "noise", Ct);

        int purged = await host.DeadLetters.PurgeAsync(host.Time.GetUtcNow().AddMinutes(1), Ct);

        purged.ShouldBe(1);
        (await host.DeadLetters.GetAsync(discarded, Ct)).ShouldBeNull();
        (await host.DeadLetters.GetAsync(parked, Ct)).ShouldNotBeNull();
    }

    [Fact]
    [Trait("Id", "UT-DLQ-10")]
    [Trait("Covers", "doc 08 3")]
    public async Task ReplayAndDiscard_AreCounted()
    {
        await using SourceFlowTestHost host = await StartAsync();
        Guid replay = await ParkAsync(host, "counters", "PermanentFailure", Monday, host.Runtime.CreateEnvelope(new Increment(CounterId.New())));
        Guid discard = await ParkAsync(host, "counters", "PermanentFailure", Monday);
        var counts = new Dictionary<string, long>();
        Meter meter = host.Services.GetRequiredService<SourceFlowMetrics>().Meter;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name.StartsWith("sourceflow.deadletter.", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (counts)
            {
                counts[instrument.Name] = counts.GetValueOrDefault(instrument.Name) + value;
            }
        });
        listener.Start();

        await host.DeadLetters.ReplayAsync(replay, null, Ct);
        await host.DeadLetters.DiscardAsync(discard, "noise", Ct);

        counts["sourceflow.deadletter.replayed"].ShouldBe(1);
        counts["sourceflow.deadletter.discarded"].ShouldBe(1);
    }

    private static Task<SourceFlowTestHost> StartAsync()
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "counters";
            sf.AddModule<CoreTestModule>();
        });
    }

    private static async Task<Guid> ParkAsync(SourceFlowTestHost host, string consumer, string reason, DateTimeOffset parkedAt,
        MessageEnvelope? envelope = null, string? type = null, string? destination = "counters")
    {
        envelope ??= host.Runtime.CreateEnvelope(new Increment(CounterId.New()));
        if (type is not null)
        {
            envelope = envelope with { Type = type };
        }

        var id = Guid.CreateVersion7(parkedAt);
        await host.Store.ParkAsync(new DeadLetter
        {
            Id = id,
            Consumer = consumer,
            Source = destination is null ? "subscription" : "receive",
            Envelope = envelope,
            Destination = destination,
            Reason = reason,
            ErrorType = typeof(InvalidOperationException).FullName,
            ErrorMessage = "boom",
            Attempts = 3,
            FirstFailedAt = parkedAt,
            ParkedAt = parkedAt,
        }, Ct);
        return id;
    }
}
