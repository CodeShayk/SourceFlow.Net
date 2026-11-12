using System.Globalization;
using System.Text.Json.Nodes;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Gcp;

/// <summary>
/// The Pub/Sub resources of a topology, as <see cref="GcpTransport.ProvisionAsync"/> creates them (doc 07 §5), for
/// infrastructure as code (<c>deploy/topology/terraform/gcp</c>). Computed from the options alone; no Google Cloud call is
/// made. Names are topic and subscription ids in the configured project.
/// </summary>
/// <remarks>
/// Subscriptions carry <c>topic</c>, <c>ackDeadlineSeconds</c>, <c>enableMessageOrdering</c>,
/// <c>messageRetentionDuration</c> (Pub/Sub duration text, for example <c>604800s</c>), <c>filter</c> (empty for none),
/// <c>labels</c>, and for endpoint subscriptions <c>deadLetterTopic</c> and <c>maxDeliveryAttempts</c>. Dead-lettering also
/// needs the project's Pub/Sub service agent to publish to the dead-letter topic and subscribe to each subscription with a
/// dead-letter policy; the Terraform module grants both.
/// </remarks>
public static class GcpTopology
{
    /// <summary>How long the dead-letter subscription keeps messages: the Pub/Sub maximum.</summary>
    public static readonly TimeSpan DeadLetterRetention = TimeSpan.FromDays(7);

    /// <summary>The resources of <paramref name="topology"/> under <paramref name="options"/>.</summary>
    public static PhysicalTopology Describe(GcpTransportOptions options, TransportTopology topology)
    {
        ArgumentNullException.ThrowIfNull(options);
        TopologyProvisioner.Check(topology);
        string prefix = options.NamePrefix ?? string.Empty;
        string endpoint = topology.Endpoint;
        string deadLetters = GcpNaming.DeadLetter(prefix, endpoint);
        string commands = GcpNaming.EndpointTopic(prefix, endpoint);

        var resources = new List<PhysicalResource>
        {
            new(PhysicalResourceKind.Topic, deadLetters, endpoint, Owned: true) { Role = PhysicalRoles.DeadLetter },
            new(PhysicalResourceKind.Subscription, deadLetters, endpoint, Owned: true)
            {
                Role = PhysicalRoles.DeadLetter,
                Properties = Subscription(deadLetters, ordered: false, DeadLetterRetention, filter: null, deadLetter: null, topology),
            },
            new(PhysicalResourceKind.Topic, commands, endpoint, Owned: true) { Role = PhysicalRoles.Endpoint },
            new(PhysicalResourceKind.Subscription, GcpNaming.EndpointSubscription(prefix, endpoint), endpoint, Owned: true)
            {
                Role = PhysicalRoles.Endpoint,
                Properties = Subscription(commands, topology.OrderedEndpoint, options.MessageRetention, filter: null, deadLetters, topology),
            },
        };

        // A subscriber may start before the publisher: the topic is created by whichever starts first (topics have no ordering).
        var topics = new HashSet<string>(StringComparer.Ordinal);
        foreach (string topic in topology.PublishedTopics.Concat(topology.SubscribedTopics).Where(topics.Add))
        {
            resources.Add(new PhysicalResource(PhysicalResourceKind.Topic, GcpNaming.Topic(prefix, topic), topic, topology.PublishedTopics.Contains(topic, StringComparer.Ordinal))
            {
                Role = PhysicalRoles.Topic,
            });
        }

        foreach (string topic in topology.SubscribedTopics.Distinct(StringComparer.Ordinal))
        {
            resources.Add(new PhysicalResource(PhysicalResourceKind.Subscription, GcpNaming.TopicSubscription(prefix, endpoint, topic), $"{topic} → {endpoint}", Owned: true)
            {
                Properties = Subscription(GcpNaming.Topic(prefix, topic), topology.OrderedEndpoint, options.MessageRetention,
                    GcpFilter.Translate(topology.Filters.GetValueOrDefault(topic)), deadLetters, topology),
            });
        }

        // Remote endpoints are owned by their services; commands are published to their command topics.
        foreach (string remote in topology.SendEndpoints.Distinct(StringComparer.Ordinal).Where(e => e != endpoint))
        {
            resources.Add(new PhysicalResource(PhysicalResourceKind.Topic, GcpNaming.EndpointTopic(prefix, remote), remote, Owned: false)
            {
                Role = PhysicalRoles.RemoteEndpoint,
            });
        }

        return new PhysicalTopology(GcpTransport.System, resources);
    }

    private static JsonObject Subscription(string topic, bool ordered, TimeSpan retention, string? filter, string? deadLetter, TransportTopology topology)
    {
        var properties = new JsonObject
        {
            ["topic"] = topic,
            ["ackDeadlineSeconds"] = GcpProvisioner.AckDeadlineSeconds,
            ["enableMessageOrdering"] = ordered,
            ["messageRetentionDuration"] = Duration(retention),
            ["filter"] = filter ?? string.Empty,
            ["expires"] = false,
        };
        if (deadLetter is not null)
        {
            properties["deadLetterTopic"] = deadLetter;
            properties["maxDeliveryAttempts"] = GcpProvisioner.PolicyDeliveries(topology.MaxDeliveries);
            properties["labels"] = new JsonObject { [GcpTransport.MaxDeliveriesLabel] = topology.MaxDeliveries.ToString(CultureInfo.InvariantCulture) };
        }
        else
        {
            properties["deadLetterTopic"] = null;
            properties["maxDeliveryAttempts"] = null;
            properties["labels"] = new JsonObject();
        }

        return properties;
    }

    private static string Duration(TimeSpan value)
    {
        return ((long)value.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
    }
}
