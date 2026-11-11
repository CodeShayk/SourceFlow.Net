using SourceFlow.Testing.Conformance;

namespace SourceFlow.Cloud.Tests.Azure;

/// <summary>TCK-01..45 against the Service Bus transport on the emulator (pull requests): GA parity with the in-memory reference (ADR-014).</summary>
[Trait("Cloud", "Azure")]
[Trait("Requires", "ServiceBusEmulator")]
[Trait("Category", "Conformance")]
public sealed class AzureTransportConformanceTests(ServiceBusEmulatorServer emulator)
{
    public static TheoryData<string> CaseIds => new(TransportConformance.Cases.Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CaseIds))]
    [Trait("Covers", "ADR-014;ADR-033;doc 07 4;doc 07 7")]
    public async Task Tck(string id)
    {
        ConformanceCase<ITransportFixture> @case = TransportConformance.Cases.Single(c => c.Id == id);
        await using AzureTransportFixture fixture = await AzureTransportFixture.EmulatorAsync(emulator);

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}

/// <summary>TCK-01..45 against a real namespace (nightly, <c>SOURCEFLOW_REAL_CLOUD=azure</c>); fault-injection cases are skipped.</summary>
[Trait("Cloud", "Azure")]
[Trait("Requires", "RealCloud")]
[Trait("Category", "Conformance")]
public sealed class AzureRealCloudConformanceTests
{
    public static TheoryData<string> CaseIds => new(TransportConformance.Cases.Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CaseIds))]
    [Trait("Covers", "ADR-014;doc 07 7")]
    public async Task Tck(string id)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SOURCEFLOW_REAL_CLOUD") == "azure", "Set SOURCEFLOW_REAL_CLOUD=azure and AZURE_SERVICEBUS_NAMESPACE (nightly) to run against a real namespace.");
        ConformanceCase<ITransportFixture> @case = TransportConformance.Cases.Single(c => c.Id == id);
        Assert.SkipWhen(@case.RequiresFaultInjection, "Faults cannot be injected into a real broker; the emulator run covers this case.");
        await using AzureTransportFixture fixture = AzureTransportFixture.RealCloud();

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}
