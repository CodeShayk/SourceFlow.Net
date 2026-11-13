using SourceFlow.Studio.Dsl;

namespace SourceFlow.Studio.Tests;

/// <summary>WF-GOLD-01..30: each case's diagnostics (plan 10 §3.1).</summary>
[Trait("Category", "Unit")]
public sealed class GoldenTests
{
    [Theory]
    [Trait("Id", "WF-GOLD-01..30")]
    [Trait("Covers", "WFD-28;WFD-NFR-2")]
    [MemberData(nameof(Cases))]
    public void Diagnostics_MatchTheGoldenFile(string @case)
    {
        WorkflowAnalysisResult result = WorkflowAnalysis.Analyze(Golden.Workflow(@case), Golden.Environment);

        Golden.AssertMatches(@case, "expected.diagnostics.json", Golden.Json(result.Diagnostics));
    }

    public static TheoryData<string> Cases()
    {
        return Golden.Cases();
    }
}
