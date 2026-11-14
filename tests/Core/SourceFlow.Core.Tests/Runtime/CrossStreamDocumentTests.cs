using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-PRJ-13 (WP-C.8, CR-31): a cross-stream document re-applied from an earlier position does not double count.</summary>
[Trait("Category", "Unit")]
public sealed class CrossStreamDocumentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-PRJ-13")]
    [Trait("Covers", "doc 06 3;doc 06 5")]
    public async Task ACrossStreamTotal_ReappliedFromAnEarlierPosition_CountsEachEventOnce()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "drips";
            sf.AddModule<CoreTestModule>();
        }, cancellationToken: Ct);
        for (int i = 0; i < 3; i++)
        {
            await host.SendAsync(new Drip(DripId.New(), "drip " + i), cancellationToken: Ct);   // three streams, one total
        }

        LogBatch log = await host.Store.ReadAsync(GlobalPosition.Start, BucketRange.All, 100, Ct);
        StoredEvent[] drips = [.. log.Events.Where(e => e.Type == "test.drip.dripped")];
        drips.Length.ShouldBe(3);
        var total = new DripTotal();

        await ApplyAsync(host, total, drips);            // the subscription applies them
        await ApplyAsync(host, total, drips);            // a repartition starts the new partitions at an earlier checkpoint
        await ApplyAsync(host, total, drips.Skip(1));    // and a replay from the middle

        Drips document = (await host.DocumentAsync<Drips>(total.Collection, "all")).ShouldNotBeNull();
        document.Count.ShouldBe(3, "each event once, although the last two were applied three times");
        document.Last.ShouldBe("drip 2");
    }

    // One subscription batch: a session, a projection scope, the events, then the documents and the commit.
    private static async Task ApplyAsync(SourceFlowTestHost host, DocumentProjectionDescriptor projection, IEnumerable<StoredEvent> events)
    {
        var decoder = new AggregateStore(host.Runtime);
        await using IStorageSession session = await host.Runtime.Storage.Sessions.BeginAsync(SourceFlow.Messaging.MessageEnvelope.DefaultTenant, Ct);
        await using (var scope = new ProjectionScope(host.Runtime, session, host.Services))
        {
            foreach (StoredEvent stored in events)
            {
                await ProjectionApplier.ApplyDocumentAsync(scope, projection, (IEvent)decoder.Deserialize(stored, upcast: true), stored.ToContext(), Ct);
            }

            await scope.CompleteAsync(Ct);
        }

        await session.CommitAsync(Ct);
    }

    // A document fed by every drip stream (one document, "all"), so its version counts writes, not a stream's version.
    private sealed class DripTotal : DocumentProjectionDescriptor
    {
        public override string Name => "test-drip-total";

        public override Type ProjectionType => typeof(DripTotal);

        public override int Version => 1;

        public override IReadOnlyList<Type> HandledEvents { get; } = [typeof(Dripped)];

        public override Type DocumentType => typeof(Drips);

        public override bool TryGetDocumentId(IEvent @event, in EventContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? documentId)
        {
            documentId = "all";
            return true;
        }

        public override object? Apply(IEvent @event, object? current, in EventContext context)
        {
            var dripped = (Dripped)@event;
            return new Drips(dripped.DripId, (((Drips?)current)?.Count ?? 0) + 1, dripped.Value);
        }
    }
}
