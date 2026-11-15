using Microsoft.Extensions.Time.Testing;
using SourceFlow.Testing.Conformance;
using SourceFlow.Transport;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.Core.Tests.Transport;

/// <summary>The in-memory broker as a TCK fixture: real-broker semantics (strict destinations), a fake clock, faults and defects.</summary>
internal sealed class InMemoryTransportFixture : ITransportFixture
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public InMemoryTransportFixture()
    {
        Broker = new InMemoryBroker(_time) { StrictDestinations = true };
        Transport = Broker.CreateTransport();
        Faults = new BrokerFaults(Broker.Faults);
    }

    public InMemoryBroker Broker { get; }

    public ITransport Transport { get; }

    public string Prefix { get; } = "tck-" + Guid.CreateVersion7().ToString("N")[..8];

    public bool SupportsClockControl => true;

    public TimeSpan ReceiveTimeout => TimeSpan.FromSeconds(10);

    public TimeSpan QuietPeriod => TimeSpan.FromMilliseconds(150);

    public ITransportFaults Faults { get; }

    public ValueTask AdvanceClockAsync(TimeSpan by)
    {
        _time.Advance(by);
        Broker.Pulse();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> InjectDefectAsync(TopologyProblemKind defect, TransportTopology topology, CancellationToken cancellationToken)
    {
        switch (defect)
        {
            case TopologyProblemKind.OrderingMismatch:
                Broker.SetOrdered(topology.Endpoint, !topology.OrderedEndpoint);
                return ValueTask.FromResult(true);
            case TopologyProblemKind.MissingDeadLetter:
                Broker.DetachDeadLetterQueue(topology.Endpoint);
                return ValueTask.FromResult(true);
            case TopologyProblemKind.MissingSubscription:
                Broker.Unsubscribe(topology.SubscribedTopics[0], topology.Endpoint);
                return ValueTask.FromResult(true);
            case TopologyProblemKind.FilterDrift:
                Broker.Subscribe(topology.SubscribedTopics[0], topology.Endpoint, "type = 'drifted.v1'");
                return ValueTask.FromResult(true);
            default:
                return ValueTask.FromResult(false);   // provider-specific (for example SNS raw delivery)
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private sealed class BrokerFaults(InMemoryBrokerFaults faults) : ITransportFaults
    {
        public bool Unavailable
        {
            get => faults.Unavailable;
            set => faults.Unavailable = value;
        }

        public int CredentialRefreshes => faults.CredentialRefreshes;

        public void ThrottleNext(int count, TimeSpan retryAfter)
        {
            faults.ThrottleNext(count, retryAfter);
        }

        public void ExpireCredentials()
        {
            faults.ExpireCredentials();
        }

        public void DuplicateNext(int count)
        {
            faults.DuplicateNext(count);
        }
    }
}

/// <summary>TCK-01..45 against the in-memory broker (ADR-014, ADR-033): the reference transport passes every case.</summary>
[Trait("Category", "Conformance")]
public sealed class InMemoryTransportConformanceTests
{
    public static TheoryData<string> CaseIds => new(TransportConformance.Cases.Select(c => c.Id));

    [Fact]
    [Trait("Id", "TCK-00")]
    public void TheKit_HasTheFortyNinePlannedCases()
    {
        TransportConformance.Cases.Select(c => c.Id).ShouldBe(Enumerable.Range(1, 49).Select(i => $"TCK-{i:D2}"));
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    [Trait("Covers", "ADR-014;ADR-033;doc 07 7")]
    public async Task Tck(string id)
    {
        ConformanceCase<ITransportFixture> @case = TransportConformance.Cases.Single(c => c.Id == id);
        await using var fixture = new InMemoryTransportFixture();

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}
