using SourceFlow.Testing.Conformance;

namespace SourceFlow.Cloud.Tests.Azure;

/// <summary>TCK-01..45 against the Service Bus transport on the emulator (pull requests), except TCK-13 (real namespace only): GA parity with the in-memory reference (ADR-014).</summary>
[Trait("Cloud", "Azure")]
[Trait("Requires", "ServiceBusEmulator")]
[Trait("Category", "Conformance")]
public sealed class AzureTransportConformanceTests(ServiceBusEmulatorServer emulator)
{
    // TCK-13 (10,000 messages over 100 sessions and 3 consumers keep per-key order) runs against a real namespace only.
    // On a loaded hosted runner the emulator delivered a session's message for the first time (delivery count 1) after
    // later messages of the same session: an ordering the emulator, not the receiver, broke (a receiver only sees what a
    // session hands it, in sequence). Locally and on Service Bus the case passes; the nightly real-cloud run covers it.
    private static readonly HashSet<string> RealNamespaceOnly = new(StringComparer.Ordinal) { "TCK-13" };

    public static TheoryData<string> CaseIds => new(TransportConformance.Cases.Select(c => c.Id).Where(id => !RealNamespaceOnly.Contains(id)));

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
