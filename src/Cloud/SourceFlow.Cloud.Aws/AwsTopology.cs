using System.Globalization;
using System.Text.Json.Nodes;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Aws;

/// <summary>
/// The SQS and SNS resources of a topology, as <see cref="AwsTransport.ProvisionAsync"/> creates them (doc 07 §5), for
/// infrastructure as code (<c>deploy/topology/terraform/aws</c>). Computed from the options alone; no AWS call is made.
/// </summary>
/// <remarks>
/// Properties are in SQS/SNS terms: queues have <c>fifo</c>, <c>messageRetentionSeconds</c> and, for the endpoint queue,
/// <c>deadLetterQueue</c>, <c>maxReceiveCount</c> and <c>allowTopics</c> (the topics its queue policy lets SNS send from);
/// queues of remote endpoints (not owned, role <c>remote-endpoint</c>) list the <c>candidates</c> the sender looks up;
/// topics have <c>fifo</c>; subscriptions have <c>topic</c>, <c>queue</c>, <c>rawMessageDelivery</c>, <c>filterPolicy</c>
/// (JSON text or null) and <c>filterPolicyScope</c>.
/// </remarks>
public static class AwsTopology
{
    /// <summary>How long dead-letter queues keep messages: the SQS maximum, for the ingestor and for inspection.</summary>
    public static readonly TimeSpan DeadLetterRetention = TimeSpan.FromDays(14);

    /// <summary>The resources of <paramref name="topology"/> under <paramref name="options"/>.</summary>
    public static PhysicalTopology Describe(AwsTransportOptions options, TransportTopology topology)
    {
        ArgumentNullException.ThrowIfNull(options);
        TopologyProvisioner.Check(topology);
        string prefix = options.NamePrefix ?? string.Empty;
        bool ordered = topology.OrderedEndpoint;
        string endpoint = topology.Endpoint;
        string queue = AwsNaming.Queue(prefix, endpoint, ordered);
        string deadLetters = AwsNaming.DeadLetterQueue(prefix, endpoint, ordered);

        // A subscriber may start before the publisher: a subscribed topic it creates takes the endpoint's ordering.
        string[] subscribed = [.. topology.SubscribedTopics.Distinct(StringComparer.Ordinal)];
        var topics = new Dictionary<string, (string Name, bool Fifo, bool Owned)>(StringComparer.Ordinal);
        foreach (string topic in topology.PublishedTopics.Distinct(StringComparer.Ordinal))
        {
            bool fifo = topology.OrderedTopics.Contains(topic, StringComparer.Ordinal);
            topics[topic] = (AwsNaming.Topic(prefix, topic, fifo), fifo, true);
        }

        foreach (string topic in subscribed.Where(t => !topics.ContainsKey(t)))
        {
            topics[topic] = (AwsNaming.Topic(prefix, topic, ordered), ordered, false);
        }

        var resources = new List<PhysicalResource>
        {
            new(PhysicalResourceKind.Queue, deadLetters, endpoint, Owned: true)
            {
                Role = PhysicalRoles.DeadLetter,
                Properties = new JsonObject
                {
                    ["fifo"] = ordered,
                    ["messageRetentionSeconds"] = Seconds(DeadLetterRetention),
                    ["deadLetterQueue"] = null,
                    ["maxReceiveCount"] = null,
                    ["allowTopics"] = new JsonArray(),
                },
            },
            new(PhysicalResourceKind.Queue, queue, endpoint, Owned: true)
            {
                Properties = new JsonObject
                {
                    ["fifo"] = ordered,
                    ["messageRetentionSeconds"] = Seconds(options.MessageRetention),
                    ["deadLetterQueue"] = deadLetters,
                    ["maxReceiveCount"] = topology.MaxDeliveries,
                    ["allowTopics"] = new JsonArray([.. subscribed.Select(t => topics[t].Name).Order(StringComparer.Ordinal).Select(n => (JsonNode)JsonValue.Create(n))]),
                },
            },
        };

        foreach ((string logical, (string name, bool fifo, bool owned)) in topics)
        {
            resources.Add(new PhysicalResource(PhysicalResourceKind.Topic, name, logical, owned) { Properties = new JsonObject { ["fifo"] = fifo } });
        }

        foreach (string topic in subscribed)
        {
            string? policy = AwsFilterPolicy.Translate(topology.Filters.GetValueOrDefault(topic));
            string topicName = topics[topic].Name;
            resources.Add(new PhysicalResource(PhysicalResourceKind.Subscription, topicName + "/" + queue, $"{topic} → {endpoint}", Owned: true)
            {
                Properties = new JsonObject
                {
                    ["topic"] = topicName,
                    ["queue"] = queue,
                    ["protocol"] = "sqs",
                    ["rawMessageDelivery"] = true,
                    ["filterPolicy"] = policy,
                    ["filterPolicyScope"] = policy is null ? null : "MessageAttributes",
                },
            });
        }

        // Remote endpoints are owned by their services; the sender finds the standard or the FIFO queue by name.
        foreach (string remote in topology.SendEndpoints.Distinct(StringComparer.Ordinal).Where(e => e != endpoint))
        {
            resources.Add(new PhysicalResource(PhysicalResourceKind.Queue, AwsNaming.Base(prefix, remote), remote, Owned: false)
            {
                Role = PhysicalRoles.RemoteEndpoint,
                Properties = new JsonObject
                {
                    ["candidates"] = new JsonArray(JsonValue.Create(AwsNaming.Queue(prefix, remote, ordered: false)), JsonValue.Create(AwsNaming.Queue(prefix, remote, ordered: true))),
                },
            });
        }

        return new PhysicalTopology(AwsTransport.System, resources);
    }

    private static int Seconds(TimeSpan value)
    {
        return (int)value.TotalSeconds;
    }

    internal static string SecondsText(TimeSpan value)
    {
        return Seconds(value).ToString(CultureInfo.InvariantCulture);
    }
}
