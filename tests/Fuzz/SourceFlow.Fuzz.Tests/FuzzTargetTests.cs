using SourceFlow.Messaging;

namespace SourceFlow.Fuzz.Tests;

/// <summary>
/// UT-FUZZ-10..19 (WP-5.5, doc 10 §7): each fuzz target accepts its seeds and survives a bounded, seeded mutation run
/// (repeatable: a finding names its input). Coverage-guided runs with the 2-hour budget per target are libFuzzer in the
/// weekly workflow.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FuzzTargetTests
{
    private const int Iterations = 25_000;

    // UT-FUZZ-10..19, in this order.
    public static TheoryData<string> Targets { get; } =
    [
        "cloudevents-binary", "cloudevents-structured", "aws-wire", "gcp-wire", "azure-wire",
        "registry", "protected-payload", "personal-data", "sfql", "sql",
    ];

    [Theory]
    [MemberData(nameof(Targets))]
    [Trait("Id", "UT-FUZZ-10")]
    [Trait("Id", "UT-FUZZ-11")]
    [Trait("Id", "UT-FUZZ-12")]
    [Trait("Id", "UT-FUZZ-13")]
    [Trait("Id", "UT-FUZZ-14")]
    [Trait("Id", "UT-FUZZ-15")]
    [Trait("Id", "UT-FUZZ-16")]
    [Trait("Id", "UT-FUZZ-17")]
    [Trait("Id", "UT-FUZZ-18")]
    [Trait("Id", "UT-FUZZ-19")]
    [Trait("Covers", "doc 10 7")]
    public void Target_SurvivesASeededMutationRun(string target)
    {
        FuzzTargets.Seeds(target).ShouldNotBeEmpty();

        FuzzFinding? finding = new Mutator(target, seed: 20261113).Run(Iterations, TimeSpan.FromSeconds(60));

        finding.ShouldBeNull(finding?.ToString());
    }

    [Fact]
    [Trait("Id", "UT-FUZZ-20")]
    [Trait("Covers", "doc 10 7")]
    public void EveryTargetIsCovered_AndItsSeedsAreValidInput()
    {
        Targets.Select(t => t.Data).Order().ShouldBe(FuzzTargets.All.Keys.Order());

        // Seeds built by the real encoders decode without being rejected (the mutations start from valid input).
        foreach (byte[] seed in FuzzTargets.Seeds("cloudevents-structured"))
        {
            Should.NotThrow(() => CloudEvents.FromStructured(seed));
        }
    }
}
