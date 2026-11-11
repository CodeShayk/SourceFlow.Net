using SourceFlow.Testing.Conformance;

namespace SourceFlow.Cloud.Tests.Gcp;

/// <summary>TCK-01..45 against the Pub/Sub transport on the emulator (pull requests): GA parity with the in-memory reference (ADR-014).</summary>
[Trait("Cloud", "Gcp")]
[Trait("Requires", "PubSubEmulator")]
[Trait("Category", "Conformance")]
public sealed class GcpTransportConformanceTests(PubSubEmulatorServer emulator)
{
    public static TheoryData<string> CaseIds => new(TransportConformance.Cases.Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CaseIds))]
    [Trait("Covers", "ADR-014;ADR-033;GAP-13;doc 07 4;doc 07 7")]
    public async Task Tck(string id)
    {
        ConformanceCase<ITransportFixture> @case = TransportConformance.Cases.Single(c => c.Id == id);
        await using GcpTransportFixture fixture = await GcpTransportFixture.EmulatorAsync(emulator);

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}

/// <summary>TCK-01..45 against a real project (nightly, <c>SOURCEFLOW_REAL_CLOUD=gcp</c>); fault-injection cases are skipped.</summary>
[Trait("Cloud", "Gcp")]
[Trait("Requires", "RealCloud")]
[Trait("Category", "Conformance")]
public sealed class GcpRealCloudConformanceTests
{
    public static TheoryData<string> CaseIds => new(TransportConformance.Cases.Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CaseIds))]
    [Trait("Covers", "ADR-014;doc 07 7")]
    public async Task Tck(string id)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SOURCEFLOW_REAL_CLOUD") == "gcp", "Set SOURCEFLOW_REAL_CLOUD=gcp and GOOGLE_CLOUD_PROJECT (nightly) to run against a real project.");
        ConformanceCase<ITransportFixture> @case = TransportConformance.Cases.Single(c => c.Id == id);
        Assert.SkipWhen(@case.RequiresFaultInjection, "Faults cannot be injected into a real broker; the emulator run covers this case.");
        await using GcpTransportFixture fixture = GcpTransportFixture.RealCloud();

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}
