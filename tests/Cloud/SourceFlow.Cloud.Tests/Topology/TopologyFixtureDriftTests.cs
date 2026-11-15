using System.Text;
using Shouldly;
using SourceFlow.Testing;
using SourceFlow.Testing.Conformance;

namespace SourceFlow.Cloud.Tests.Topology;

/// <summary>
/// The committed exports in <c>deploy/topology/fixtures</c> are what the libraries export today (UT-TOP-13): the IaC modules
/// are tested against them, so a change in naming or settings must show up as a fixture diff in review. Set
/// <c>SOURCEFLOW_APPROVE=1</c> to rewrite them.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TopologyFixtureDriftTests
{
    public static TheoryData<string, string> Fixtures()
    {
        var data = new TheoryData<string, string>();
        foreach (string transport in TopologyExports.Transports)
        {
            foreach (string fixture in TopologyFixtures.Names)
            {
                data.Add(transport, fixture);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    [Trait("Id", "UT-TOP-13")]
    public void CommittedFixture_MatchesTheExport(string transport, string fixture)
    {
        string exported = TopologyExports.Export(transport, fixture);
        string path = TopologyExports.FixturePath(transport, fixture);
        string? committed = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
        if (committed != exported && string.Equals(Environment.GetEnvironmentVariable(Approvals.ApproveVariable), "1", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, exported, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        committed.ShouldBe(exported, $"{path} is out of date: run the topology tests with {Approvals.ApproveVariable}=1 and review the diff.");
    }

    [Fact]
    [Trait("Id", "UT-TOP-13")]
    public void NoStaleFixtures()
    {
        string[] expected = [.. TopologyExports.Transports.SelectMany(t => TopologyFixtures.Names.Select(f => Path.GetFullPath(TopologyExports.FixturePath(t, f)))).Order(StringComparer.Ordinal)];
        string[] actual = Directory.Exists(TopologyExports.FixturesDirectory)
            ? [.. Directory.EnumerateFiles(TopologyExports.FixturesDirectory, "*.json", SearchOption.AllDirectories).Select(Path.GetFullPath).Order(StringComparer.Ordinal)]
            : [];

        actual.ShouldBe(expected);
    }
}
