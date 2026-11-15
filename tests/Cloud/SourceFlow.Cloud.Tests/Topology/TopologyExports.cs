using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Azure;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Testing.Conformance;
using SourceFlow.Transport;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.Cloud.Tests.Topology;

/// <summary>
/// The topology exports of every transport for the TCK topology fixtures (WP-3.5), and where the committed ones live
/// (<c>deploy/topology/fixtures/{transport}/{fixture}.json</c>).
/// </summary>
internal static class TopologyExports
{
    /// <summary>The name prefix of the committed fixtures (an environment prefix, doc 07 §3).</summary>
    public const string FixturePrefix = "dev-";

    /// <summary>The transports, by fixture folder name.</summary>
    public static IReadOnlyList<string> Transports { get; } = ["aws", "gcp", "azure", "in-memory"];

    public static string RepositoryRoot { get; } = FindRoot();

    public static string FixturesDirectory => Path.Combine(RepositoryRoot, "deploy", "topology", "fixtures");

    public static string SchemaPath => Path.Combine(RepositoryRoot, "deploy", "topology", "topology.schema.json");

    public static string ModulesDirectory => Path.Combine(RepositoryRoot, "deploy", "topology");

    /// <summary>The physical description of <paramref name="topology"/> by <paramref name="transport"/> with the name prefix.</summary>
    public static PhysicalTopology Describe(string transport, TransportTopology topology, string namePrefix)
    {
        return transport switch
        {
            "aws" => AwsTopology.Describe(new AwsTransportOptions { NamePrefix = namePrefix }, topology),
            "gcp" => GcpTopology.Describe(new GcpTransportOptions { ProjectId = "sourceflow-fixtures", NamePrefix = namePrefix }, topology),
            "azure" => AzureTopology.Describe(new AzureTransportOptions { NamePrefix = namePrefix }, topology),
            "in-memory" => new InMemoryBroker().CreateTransport().Describe(topology),
            _ => throw new ArgumentException($"Unknown transport '{transport}'.", nameof(transport)),
        };
    }

    /// <summary>The export of a fixture, as committed.</summary>
    public static string Export(string transport, string fixture, string namePrefix = FixturePrefix, string logicalPrefix = "")
    {
        TransportTopology topology = TopologyFixtures.Get(fixture, logicalPrefix);
        return TopologyExport.ToJson(new TopologyDocument(logicalPrefix + fixture, topology) { Physical = Describe(transport, topology, namePrefix) });
    }

    public static string FixturePath(string transport, string fixture)
    {
        return Path.Combine(FixturesDirectory, transport, fixture + ".json");
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SourceFlow.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (SourceFlow.slnx) was not found above " + AppContext.BaseDirectory);
    }
}
