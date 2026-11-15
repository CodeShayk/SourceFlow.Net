using SourceFlow.Transport;

namespace SourceFlow.Testing.Conformance;

/// <summary>A named topology the conformance kit and the infrastructure-as-code modules are tested with.</summary>
/// <param name="Name">The fixture name (also the file name of its committed export).</param>
/// <param name="Topology">The topology.</param>
public sealed record TopologyFixture(string Name, TransportTopology Topology);

/// <summary>
/// The topology fixtures (doc 07 §5, WP-3.5): every shape a service's topology takes, from an endpoint alone to ordered
/// publication and every kind of filter. The TCK provisions them on each transport; the Terraform and Bicep modules in
/// <c>deploy/topology</c> are applied to them and the result must pass <see cref="ProvisioningMode.Validate"/>.
/// </summary>
/// <remarks>
/// Logical names start with a prefix, so runs against a shared broker do not collide. <c>publisher</c> and <c>subscriber</c>
/// are two services sharing one topic: the publisher owns it, the subscriber only subscribes.
/// </remarks>
public static class TopologyFixtures
{
    /// <summary>The fixture names, in a stable order.</summary>
    public static IReadOnlyList<string> Names { get; } = ["minimal", "full", "ordered", "filtered", "publisher", "subscriber"];

    /// <summary>Every fixture with logical names prefixed by <paramref name="prefix"/>.</summary>
    public static IReadOnlyList<TopologyFixture> All(string prefix = "")
    {
        return [.. Names.Select(name => new TopologyFixture(name, Get(name, prefix)))];
    }

    /// <summary>One fixture by name.</summary>
    public static TransportTopology Get(string name, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string P(string logical) => prefix + logical;
        return name switch
        {
            // An endpoint alone: a service that only receives commands.
            "minimal" => new TransportTopology(P("minimal"), [], [], []),

            // The TCK provisioning topology: a filtered subscription, a publication and a non-default delivery limit.
            "full" => Full(P("full")),

            // Ordered endpoint and publication; the subscribed topic is created ordered when the subscriber starts first.
            "ordered" => new TransportTopology(P("ordered"), [P("ordered-out")], [P("ordered-in")], [P("minimal")])
            {
                OrderedEndpoint = true,
                OrderedTopics = [P("ordered-out")],
                MaxDeliveries = 5,
                Filters = new Dictionary<string, string> { [P("ordered-in")] = CloudEventFilter.ForTypes(["tck.ordered.v1"]).Expression },
            },

            // One filter of each kind: exact types (native everywhere), type prefixes, and one only the receiver evaluates.
            "filtered" => new TransportTopology(P("filtered"), [], [P("typed"), P("prefixed"), P("tenant"), P("everything")], [])
            {
                Filters = new Dictionary<string, string>
                {
                    [P("typed")] = CloudEventFilter.ForTypes(["tck.wanted.v1", "tck.also.v2"]).Expression,
                    [P("prefixed")] = "type LIKE 'tck.%' OR type LIKE 'payments.%'",
                    [P("tenant")] = "type = 'tck.tenant.v1' AND sftenant = 'eu'",
                },
            },

            // Two services sharing a topic: the publisher owns it.
            "publisher" => new TransportTopology(P("publisher"), [P("shared")], [], [P("subscriber")]),
            "subscriber" => new TransportTopology(P("subscriber"), [], [P("shared")], [])
            {
                Filters = new Dictionary<string, string> { [P("shared")] = CloudEventFilter.ForTypes(["tck.shared.v1"]).Expression },
            },

            _ => throw new ArgumentException($"Unknown topology fixture '{name}'; the fixtures are {string.Join(", ", Names)}.", nameof(name)),
        };
    }

    /// <summary>The TCK provisioning topology for <paramref name="endpoint"/> (TCK-35..40).</summary>
    public static TransportTopology Full(string endpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        return new TransportTopology(endpoint, [endpoint + "-out"], [endpoint + "-in"], [])
        {
            MaxDeliveries = 7,
            Filters = new Dictionary<string, string> { [endpoint + "-in"] = "type LIKE 'tck.%'" },
        };
    }
}
