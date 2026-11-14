using SourceFlow.Studio.Dsl;
using SourceFlow.Studio.Dsl.Simulation;

namespace SourceFlow.Studio.Tests;

/// <summary>WF-GOLD-01..31: each case's diagnostics (plan 10 §3.1).</summary>
[Trait("Category", "Unit")]
public sealed class GoldenTests
{
    [Theory]
    [Trait("Id", "WF-GOLD-01..31")]
    [Trait("Covers", "WFD-28;WFD-NFR-2")]
    [MemberData(nameof(Cases))]
    public void Diagnostics_MatchTheGoldenFile(string @case)
    {
        WorkflowAnalysisResult result = WorkflowAnalysis.Analyze(Golden.Workflow(@case), Golden.Environment);

        Golden.AssertMatches(@case, "expected.diagnostics.json", Golden.Json(result.Diagnostics));
    }

    [Theory]
    [Trait("Id", "WF-GOLD-01..31")]
    [Trait("Covers", "WFD-34")]
    [MemberData(nameof(Cases))]
    public void Paths_MatchTheGoldenFile(string @case)
    {
        WorkflowAnalysisResult result = WorkflowAnalysis.Analyze(Golden.Workflow(@case), Golden.Environment);
        if (result.HasErrors)
        {
            return;   // paths and diagrams are for valid workflows
        }

        PathSet paths = PathEnumerator.Enumerate(result.Workflow!);
        string text = string.Join("\n", paths.Paths.Select(p => p.ToString()))
            + "\n\nedges: " + paths.Edges.Count + ", uncovered: " + (paths.Uncovered.Count == 0 ? "none" : string.Join(", ", paths.Uncovered))
            + (paths.Truncated ? "\ntruncated" : string.Empty) + "\n";
        Golden.AssertMatches(@case, "expected.paths.txt", text);
    }

    public static TheoryData<string> Cases()
    {
        return Golden.Cases();
    }
}
