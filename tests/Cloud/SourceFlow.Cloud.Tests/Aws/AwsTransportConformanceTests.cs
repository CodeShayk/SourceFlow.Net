using SourceFlow.Testing.Conformance;

namespace SourceFlow.Cloud.Tests.Aws;

/// <summary>TCK-01..45 against the AWS transport on LocalStack (pull requests): GA parity with the in-memory reference (ADR-014).</summary>
[Trait("Cloud", "Aws")]
[Trait("Requires", "LocalStack")]
[Trait("Category", "Conformance")]
public sealed class AwsTransportConformanceTests(LocalStackServer localStack)
{
    public static TheoryData<string> CaseIds => new(TransportConformance.Cases.Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CaseIds))]
    [Trait("Covers", "ADR-014;ADR-033;doc 07 4;doc 07 7")]
    public async Task Tck(string id)
    {
        ConformanceCase<ITransportFixture> @case = TransportConformance.Cases.Single(c => c.Id == id);
        await using AwsTransportFixture fixture = await AwsTransportFixture.LocalStackAsync(localStack);

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}

/// <summary>TCK-01..45 against a real AWS account (nightly, <c>SOURCEFLOW_REAL_CLOUD=aws</c>); fault-injection cases are skipped.</summary>
[Trait("Cloud", "Aws")]
[Trait("Requires", "RealCloud")]
[Trait("Category", "Conformance")]
public sealed class AwsRealCloudConformanceTests
{
    public static TheoryData<string> CaseIds => new(TransportConformance.Cases.Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CaseIds))]
    [Trait("Covers", "ADR-014;doc 07 7")]
    public async Task Tck(string id)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SOURCEFLOW_REAL_CLOUD") == "aws", "Set SOURCEFLOW_REAL_CLOUD=aws (nightly) to run against a real account.");
        ConformanceCase<ITransportFixture> @case = TransportConformance.Cases.Single(c => c.Id == id);
        Assert.SkipWhen(@case.RequiresFaultInjection, "Faults cannot be injected into a real broker; the emulator run covers this case.");
        await using AwsTransportFixture fixture = AwsTransportFixture.RealCloud();

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}
