using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using Azure.Messaging.ServiceBus.Administration;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Azure;

/// <summary>
/// The Service Bus entities of a topology, as <see cref="AzureTransport.ProvisionAsync"/> creates them (doc 07 §5), for
/// infrastructure as code (<c>deploy/topology/terraform/azure</c>, <c>deploy/topology/bicep</c>). Computed from the options
/// alone; no Azure call is made. Durations are ISO 8601 (<c>PT30S</c>), as ARM takes them.
/// </summary>
/// <remarks>
/// Resource names are paths: a queue or topic name, <c>{topic}/{subscription}</c>, <c>{topic}/{subscription}/{rule}</c>;
/// properties carry the parts. A subscription's first rule is named <c>$Default</c>: ARM creates that rule (match everything)
/// with every subscription, so infrastructure as code replaces it rather than adding a rule beside it. Rule names do not
/// matter to <c>Validate</c>, which compares the filters.
/// </remarks>
public static class AzureTopology
{
    /// <summary>The rule ARM creates with every subscription.</summary>
    public const string DefaultRuleName = "$Default";

    /// <summary>The resources of <paramref name="topology"/> under <paramref name="options"/>.</summary>
    public static PhysicalTopology Describe(AzureTransportOptions options, TransportTopology topology)
    {
        ArgumentNullException.ThrowIfNull(options);
        TopologyProvisioner.Check(topology);
        string prefix = options.NamePrefix ?? string.Empty;
        string endpoint = topology.Endpoint;
        string queue = AzureNaming.Queue(prefix, endpoint);
        string ttl = Iso(options.MessageTimeToLive);

        var resources = new List<PhysicalResource>
        {
            new(PhysicalResourceKind.Queue, queue, endpoint, Owned: true)
            {
                Properties = new JsonObject
                {
                    ["requiresSession"] = topology.OrderedEndpoint,
                    ["maxDeliveryCount"] = topology.MaxDeliveries,
                    ["lockDuration"] = Iso(options.LockDuration),
                    ["defaultMessageTimeToLive"] = ttl,
                    ["deadLetteringOnMessageExpiration"] = true,
                    ["requiresDuplicateDetection"] = options.DuplicateDetectionWindow is not null,
                    ["duplicateDetectionHistoryTimeWindow"] = options.DuplicateDetectionWindow is { } window ? Iso(window) : null,
                },
            },
        };

        var topics = new HashSet<string>(StringComparer.Ordinal);
        foreach (string topic in topology.PublishedTopics.Concat(topology.SubscribedTopics).Where(topics.Add))
        {
            resources.Add(new PhysicalResource(PhysicalResourceKind.Topic, AzureNaming.Topic(prefix, topic), topic, topology.PublishedTopics.Contains(topic, StringComparer.Ordinal))
            {
                Properties = new JsonObject { ["defaultMessageTimeToLive"] = ttl },
            });
        }

        string subscription = AzureNaming.Subscription(prefix, endpoint);
        foreach (string topic in topology.SubscribedTopics.Distinct(StringComparer.Ordinal))
        {
            string topicName = AzureNaming.Topic(prefix, topic);
            string logical = $"{topic} → {endpoint}";
            string path = topicName + "/" + subscription;
            resources.Add(new PhysicalResource(PhysicalResourceKind.Subscription, path, logical, Owned: true)
            {
                Properties = new JsonObject
                {
                    ["topic"] = topicName,
                    ["subscription"] = subscription,
                    ["forwardTo"] = queue,
                    ["defaultMessageTimeToLive"] = ttl,
                },
            });

            IReadOnlyList<CreateRuleOptions> rules = AzureFilter.Rules(topology.Filters.GetValueOrDefault(topic));
            for (int i = 0; i < rules.Count; i++)
            {
                string name = i == 0 ? DefaultRuleName : rules[i].Name;
                var properties = new JsonObject { ["topic"] = topicName, ["subscription"] = subscription, ["rule"] = name, ["correlationProperties"] = null, ["sqlExpression"] = null };
                switch (rules[i].Filter)
                {
                    case CorrelationRuleFilter correlation:
                        properties["filterType"] = "CorrelationFilter";
                        properties["correlationProperties"] = new JsonObject([.. correlation.ApplicationProperties.Select(p =>
                            KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(Convert.ToString(p.Value, CultureInfo.InvariantCulture))))]);
                        break;
                    case TrueRuleFilter:
                        properties["filterType"] = "SqlFilter";
                        properties["sqlExpression"] = "1=1";
                        break;
                    case SqlRuleFilter sql:
                        properties["filterType"] = "SqlFilter";
                        properties["sqlExpression"] = sql.SqlExpression;
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected rule filter {rules[i].Filter.GetType().Name}.");
                }

                resources.Add(new PhysicalResource(PhysicalResourceKind.Rule, path + "/" + name, logical, Owned: true) { Properties = properties });
            }
        }

        // Remote endpoints are owned by their services; commands are sent to their queues.
        foreach (string remote in topology.SendEndpoints.Distinct(StringComparer.Ordinal).Where(e => e != endpoint))
        {
            resources.Add(new PhysicalResource(PhysicalResourceKind.Queue, AzureNaming.Queue(prefix, remote), remote, Owned: false)
            {
                Role = PhysicalRoles.RemoteEndpoint,
            });
        }

        return new PhysicalTopology(AzureTransport.System, resources);
    }

    private static string Iso(TimeSpan value)
    {
        return XmlConvert.ToString(value);
    }
}
