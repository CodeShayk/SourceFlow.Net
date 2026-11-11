using Microsoft.Extensions.Time.Testing;
using SourceFlow.Testing.Conformance;
using SourceFlow.Transport;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.Core.Tests.Transport;

internal sealed class InMemoryTransportFixture : ITransportFixture
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public InMemoryTransportFixture()
    {
        Broker = new InMemoryBroker(_time);
        Transport = Broker.CreateTransport();
    }

    public InMemoryBroker Broker { get; }

    public ITransport Transport { get; }

    public string Prefix { get; } = "tck" + Environment.CurrentManagedThreadId;

    public bool SupportsClockControl => true;

    public TimeSpan ReceiveTimeout => TimeSpan.FromSeconds(5);

    public ValueTask AdvanceClockAsync(TimeSpan by)
    {
        _time.Advance(by);
        Broker.Pulse();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>The transport conformance kit against the in-memory broker (TCK, ADR-014).</summary>
[Trait("Category", "Conformance")]
public sealed class InMemoryTransportConformanceTests
{
    public static TheoryData<string> CaseIds => new(TransportConformance.Cases.Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CaseIds))]
    [Trait("Covers", "ADR-014")]
    public async Task Tck(string id)
    {
        ConformanceCase<ITransportFixture> @case = TransportConformance.Cases.Single(c => c.Id == id);
        await using var fixture = new InMemoryTransportFixture();

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}
