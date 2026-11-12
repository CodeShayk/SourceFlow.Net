using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SourceFlow.Transport;

/// <summary>
/// The provisioning behaviour every transport shares (ADR-017, doc 07 §5). The topology is checked first in every mode, so
/// a malformed filter or an inconsistent topology fails startup before any broker call. Then <see cref="ProvisioningMode.None"/>
/// stops, <see cref="ProvisioningMode.CreateIfMissing"/> creates what is missing, and both it and
/// <see cref="ProvisioningMode.Validate"/> compare the broker with the topology and report every difference at once.
/// </summary>
/// <remarks>
/// Implementations create only what is missing and never change existing resources: a difference is reported, so drift
/// from infrastructure as code is visible instead of silently overwritten.
/// </remarks>
public abstract class TopologyProvisioner
{
    /// <summary>Applies <paramref name="mode"/> to <paramref name="topology"/>.</summary>
    /// <exception cref="ArgumentException">The topology is inconsistent (see <see cref="Check"/>).</exception>
    /// <exception cref="FormatException">A subscription filter is not valid CloudEvents SQL.</exception>
    /// <exception cref="TopologyValidationException">The broker differs from the topology.</exception>
    public async ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken)
    {
        Check(topology);
        switch (mode)
        {
            case ProvisioningMode.None:
                return;
            case ProvisioningMode.Validate:
                break;
            case ProvisioningMode.CreateIfMissing:
                await CreateAsync(topology, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown provisioning mode.");
        }

        IReadOnlyList<TopologyProblem> problems = await ValidateAsync(topology, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            throw new TopologyValidationException(problems);
        }
    }

    /// <summary>Creates the resources of <paramref name="topology"/> that do not exist; existing ones are left as they are.</summary>
    protected abstract ValueTask CreateAsync(TransportTopology topology, CancellationToken cancellationToken);

    /// <summary>Every difference between the broker and <paramref name="topology"/> (empty when they match).</summary>
    protected abstract ValueTask<IReadOnlyList<TopologyProblem>> ValidateAsync(TransportTopology topology, CancellationToken cancellationToken);

    /// <summary>
    /// Checks that a topology is consistent: logical names are present and not URLs or ARNs, the delivery limit is at least
    /// one, ordered topics are published, filters belong to subscribed topics and parse as CloudEvents SQL.
    /// </summary>
    /// <exception cref="ArgumentException">Lists every inconsistency.</exception>
    /// <exception cref="FormatException">A filter is not valid CloudEvents SQL.</exception>
    public static void Check(TransportTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        var errors = new List<string>();
        Name(topology.Endpoint, "the endpoint", errors);
        foreach (string topic in topology.PublishedTopics)
        {
            Name(topic, "a published topic", errors);
        }

        foreach (string topic in topology.SubscribedTopics)
        {
            Name(topic, "a subscribed topic", errors);
        }

        foreach (string endpoint in topology.SendEndpoints)
        {
            Name(endpoint, "a send endpoint", errors);
        }

        if (topology.MaxDeliveries < 1)
        {
            errors.Add($"MaxDeliveries must be at least 1 (found {topology.MaxDeliveries.ToString(CultureInfo.InvariantCulture)})");
        }

        foreach (string topic in topology.OrderedTopics.Where(t => !topology.PublishedTopics.Contains(t, StringComparer.Ordinal)))
        {
            errors.Add($"the ordered topic '{topic}' is not published");
        }

        foreach (string topic in topology.Filters.Keys.Where(t => !topology.SubscribedTopics.Contains(t, StringComparer.Ordinal)))
        {
            errors.Add($"the filter for '{topic}' belongs to a topic the endpoint does not subscribe to");
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException($"The topology of '{topology.Endpoint}' is invalid: {string.Join("; ", errors)}.", nameof(topology));
        }

        foreach (string filter in topology.Filters.Values)
        {
            CloudEventFilter.Parse(filter);   // a malformed filter fails provisioning in every mode
        }
    }

    private static void Name(string? name, string what, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add(what + " has no name");
        }
        else if (name.Contains("://", StringComparison.Ordinal) || name.StartsWith("arn:", StringComparison.OrdinalIgnoreCase) || name.Contains('/', StringComparison.Ordinal))
        {
            errors.Add($"{what} '{name}' looks like a URL or ARN; topologies use logical names and the transport maps them (RT-R5)");
        }
    }
}

/// <summary>
/// A transport that can name the physical resources of a topology without calling the broker, for infrastructure as code
/// (ADR-017, <c>dotnet sourceflow topology export</c>).
/// </summary>
public interface ITopologyDescriber
{
    /// <summary>The physical resources <see cref="ITransport.ProvisionAsync"/> would create for <paramref name="topology"/>.</summary>
    PhysicalTopology Describe(TransportTopology topology);
}

/// <summary>The broker resources of a topology.</summary>
/// <param name="Transport">The transport's <see cref="ITransport.SystemName"/>.</param>
/// <param name="Resources">Every resource, in creation order (a resource only refers to resources before it).</param>
public sealed record PhysicalTopology(string Transport, IReadOnlyList<PhysicalResource> Resources);

/// <summary>What a physical resource is.</summary>
public enum PhysicalResourceKind
{
    /// <summary>A queue (SQS queue, Service Bus queue, an endpoint's dead-letter queue).</summary>
    Queue,

    /// <summary>A topic (SNS topic, Pub/Sub topic, Service Bus topic).</summary>
    Topic,

    /// <summary>A subscription (SNS subscription, Pub/Sub subscription, Service Bus subscription).</summary>
    Subscription,

    /// <summary>A subscription rule (Service Bus filter rule).</summary>
    Rule,
}

/// <summary>One broker resource.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Name">The physical name (unique per kind within the topology).</param>
/// <param name="Logical">The logical resource it serves: an endpoint, a topic, or <c>topic → endpoint</c>.</param>
/// <param name="Owned">
/// True when the service owns it (its endpoint, dead letters, subscriptions and published topics). A subscribed topic is
/// owned by its publisher; infrastructure as code creates it there.
/// </param>
public sealed record PhysicalResource(PhysicalResourceKind Kind, string Name, string Logical, bool Owned)
{
    /// <summary>
    /// What the resource is for (<see cref="PhysicalRoles"/>): by default <c>endpoint</c> for a queue, and the kind's name
    /// for the others.
    /// </summary>
    public string Role { get; init; } = Kind switch
    {
        PhysicalResourceKind.Queue => PhysicalRoles.Endpoint,
        PhysicalResourceKind.Topic => PhysicalRoles.Topic,
        PhysicalResourceKind.Subscription => PhysicalRoles.Subscription,
        _ => PhysicalRoles.Rule,
    };

    /// <summary>Provider settings in the provider's own terms and units, as infrastructure-as-code modules consume them.</summary>
    public JsonObject Properties { get; init; } = [];
}

/// <summary>The roles of physical resources, as exported in <c>role</c>.</summary>
public static class PhysicalRoles
{
    /// <summary>The service's input: its queue, or (Pub/Sub) its command topic and subscription.</summary>
    public const string Endpoint = "endpoint";

    /// <summary>The endpoint's dead-letter queue, or (Pub/Sub) its dead-letter topic and subscription.</summary>
    public const string DeadLetter = "dead-letter";

    /// <summary>A publication topic.</summary>
    public const string Topic = "topic";

    /// <summary>The endpoint's subscription to a topic.</summary>
    public const string Subscription = "subscription";

    /// <summary>A subscription filter rule.</summary>
    public const string Rule = "rule";

    /// <summary>Another service's endpoint the service sends commands to (not owned).</summary>
    public const string RemoteEndpoint = "remote-endpoint";
}

/// <summary>Everything <see cref="TopologyExport"/> writes about one service.</summary>
/// <param name="Service">The service name.</param>
/// <param name="Topology">The logical topology.</param>
public sealed record TopologyDocument(string Service, TransportTopology Topology)
{
    /// <summary>The physical resources, when the transport can describe them (<see cref="ITopologyDescriber"/>).</summary>
    public PhysicalTopology? Physical { get; init; }

    /// <summary>The CloudEvents types the service owns and decides (commands it receives on its endpoint).</summary>
    public IReadOnlyList<string> OwnedCommands { get; init; } = [];

    /// <summary>The CloudEvents types published per topic.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> PublishedMessages { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The CloudEvents types sent per remote endpoint.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SentCommands { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>
/// Writes a service's topology as JSON for infrastructure as code (ADR-017, doc 07 §5): the logical topology and, when the
/// transport describes them, the physical resources in the provider's terms. The output is deterministic (sorted, no
/// timestamps), so it can be committed and diffed. <c>deploy/topology/topology.schema.json</c> is its JSON Schema.
/// </summary>
public static class TopologyExport
{
    /// <summary>The schema version written to <c>schemaVersion</c>; incremented on breaking changes.</summary>
    public const int SchemaVersion = 1;

    // Relaxed escaping keeps quotes in filters and non-ASCII names readable; the output is a file, never embedded in HTML.
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The export as JSON text (two-space indentation, LF line endings, trailing newline).</summary>
    public static string ToJson(TopologyDocument document)
    {
        return ToJsonObject(document).ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>The export as a JSON object.</summary>
    public static JsonObject ToJsonObject(TopologyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Service);
        TransportTopology topology = document.Topology ?? throw new ArgumentException("The document has no topology.", nameof(document));
        TopologyProvisioner.Check(topology);

        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["service"] = document.Service,
            ["transport"] = document.Physical?.Transport,
            ["endpoint"] = new JsonObject
            {
                ["name"] = topology.Endpoint,
                ["ordered"] = topology.OrderedEndpoint,
                ["maxDeliveries"] = topology.MaxDeliveries,
                ["commands"] = Strings(document.OwnedCommands),
            },
            ["publishes"] = new JsonArray([.. Sorted(topology.PublishedTopics).Select(topic => (JsonNode)new JsonObject
            {
                ["topic"] = topic,
                ["ordered"] = topology.OrderedTopics.Contains(topic, StringComparer.Ordinal),
                ["messages"] = Strings(document.PublishedMessages.GetValueOrDefault(topic) ?? []),
            })]),
            ["subscribes"] = new JsonArray([.. Sorted(topology.SubscribedTopics).Select(topic => (JsonNode)new JsonObject
            {
                ["topic"] = topic,
                ["filter"] = topology.Filters.GetValueOrDefault(topic),
            })]),
            ["sends"] = new JsonArray([.. Sorted(topology.SendEndpoints).Select(endpoint => (JsonNode)new JsonObject
            {
                ["endpoint"] = endpoint,
                ["commands"] = Strings(document.SentCommands.GetValueOrDefault(endpoint) ?? []),
            })]),
        };

        if (document.Physical is { } physical)
        {
            var seen = new HashSet<(PhysicalResourceKind, string)>();
            var resources = new JsonArray();
            foreach (PhysicalResource resource in physical.Resources)
            {
                if (!seen.Add((resource.Kind, resource.Name)))
                {
                    throw new ArgumentException($"The physical topology names the {resource.Kind} '{resource.Name}' twice.", nameof(document));
                }

                resources.Add((JsonNode)new JsonObject
                {
                    ["kind"] = KindName(resource.Kind),
                    ["name"] = resource.Name,
                    ["logical"] = resource.Logical,
                    ["owned"] = resource.Owned,
                    ["role"] = resource.Role,
                    ["properties"] = SortedCopy(resource.Properties),
                });
            }

            root["resources"] = resources;
        }

        return root;
    }

    /// <summary>The JSON name of a resource kind (<c>queue</c>, <c>topic</c>, <c>subscription</c>, <c>rule</c>).</summary>
    public static string KindName(PhysicalResourceKind kind)
    {
        return kind switch
        {
            PhysicalResourceKind.Queue => "queue",
            PhysicalResourceKind.Topic => "topic",
            PhysicalResourceKind.Subscription => "subscription",
            PhysicalResourceKind.Rule => "rule",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static IEnumerable<string> Sorted(IEnumerable<string> values)
    {
        return values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        return [.. Sorted(values).Select(v => (JsonNode)JsonValue.Create(v))];
    }

    // Object keys sorted at every level (arrays keep their order), so the output does not depend on construction order.
    private static JsonNode? SortedCopy(JsonNode? node)
    {
        return node switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, SortedCopy(p.Value)))),
            JsonArray array => new JsonArray([.. array.Select(SortedCopy)]),
            null => null,
            _ => node.DeepClone(),
        };
    }
}
