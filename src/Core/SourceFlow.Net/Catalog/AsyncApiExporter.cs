using System.Text;
using System.Text.Json.Nodes;
using SourceFlow.Messaging;
using SourceFlow.Runtime;

namespace SourceFlow.Catalog;

/// <summary>
/// Exports a service's messaging contract as an AsyncAPI 3.0 document (doc 14 §2.2): the service endpoint receives the
/// commands it owns, each published topic carries its contracts, and every message has its JSON Schema payload and the
/// CloudEvents headers. Tools that read AsyncAPI (for example EventCatalog) can publish it as a browsable catalog.
/// </summary>
public static class AsyncApiExporter
{
    /// <summary>The AsyncAPI version written.</summary>
    public const string AsyncApiVersion = "3.0.0";

    /// <summary>Writes the AsyncAPI document of a service.</summary>
    /// <param name="model">The runtime model.</param>
    /// <param name="routing">The routing table (endpoint and publications).</param>
    /// <param name="catalog">The schema catalog (payloads).</param>
    /// <param name="serviceName">The service name (document title).</param>
    /// <param name="version">The document version (for example the service's release).</param>
    public static string Export(RuntimeModel model, RoutingTable routing, SchemaCatalog catalog, string serviceName, string version = "1.0.0")
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var messages = new JsonObject();
        var channels = new JsonObject();
        var operations = new JsonObject();

        // The service endpoint: the commands this service owns (and its timeouts and replies).
        var endpointMessages = new JsonObject();
        foreach (MessageTypeInfo type in model.Registry.All.Where(t => t.Kind is MessageKind.Command && model.CommandOwners.ContainsKey(t.ClrType)))
        {
            endpointMessages[MessageId(type.Name, type.Version)] = Reference("#/components/messages/" + MessageId(type.Name, type.Version));
            AddMessage(messages, catalog, type.Name, type.Version);
        }

        string endpointId = ChannelId(routing.Endpoint);
        channels[endpointId] = new JsonObject
        {
            ["address"] = routing.Endpoint,
            ["description"] = $"The {serviceName} service endpoint: commands it owns.",
            ["messages"] = endpointMessages,
        };
        operations["receive-" + endpointId] = new JsonObject
        {
            ["action"] = "receive",
            ["channel"] = Reference("#/channels/" + endpointId),
            ["messages"] = new JsonArray([.. endpointMessages.Select(m => (JsonNode)Reference($"#/channels/{endpointId}/messages/{m.Key}"))]),
        };

        // Published topics: the contracts mapped from domain events (integration events, or events published as is).
        foreach (IGrouping<string, PublicationRoute> topic in routing.Publications.Values.SelectMany(r => r).GroupBy(r => r.Destination, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var topicMessages = new JsonObject();
            foreach (Type contract in topic.Select(r => r.ContractType).Distinct())
            {
                if (model.Registry.TryGet(contract, out MessageTypeInfo? type))
                {
                    topicMessages[MessageId(type.Name, type.Version)] = Reference("#/components/messages/" + MessageId(type.Name, type.Version));
                    AddMessage(messages, catalog, type.Name, type.Version);
                }
            }

            string id = ChannelId(topic.Key);
            channels[id] = new JsonObject { ["address"] = topic.Key, ["messages"] = topicMessages };
            operations["publish-" + id] = new JsonObject
            {
                ["action"] = "send",
                ["channel"] = Reference("#/channels/" + id),
                ["messages"] = new JsonArray([.. topicMessages.Select(m => (JsonNode)Reference($"#/channels/{id}/messages/{m.Key}"))]),
            };
        }

        var document = new JsonObject
        {
            ["asyncapi"] = AsyncApiVersion,
            ["info"] = new JsonObject { ["title"] = serviceName, ["version"] = version },
            ["defaultContentType"] = MessageEnvelope.JsonContentType,
            ["channels"] = channels,
            ["operations"] = operations,
            ["components"] = new JsonObject { ["messages"] = messages, ["schemas"] = new JsonObject { ["CloudEventsHeaders"] = CloudEventsHeaders() } },
        };
        return document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine() });
    }

    private static void AddMessage(JsonObject messages, SchemaCatalog catalog, string type, int version)
    {
        string id = MessageId(type, version);
        if (messages.ContainsKey(id) || !catalog.TryGet(type, version, out SchemaEntry? entry))
        {
            return;
        }

        messages[id] = new JsonObject
        {
            ["name"] = type,
            ["title"] = $"{type} v{version}",
            ["contentType"] = MessageEnvelope.JsonContentType,
            ["headers"] = Reference("#/components/schemas/CloudEventsHeaders"),
            ["payload"] = entry.Schema.DeepClone(),
            ["x-sourceflow"] = new JsonObject { ["type"] = type, ["version"] = version, ["kind"] = SchemaEntry.KindName(entry.Kind) },
        };
    }

    // CloudEvents attributes in binary mode (doc 04 §1).
    private static JsonObject CloudEventsHeaders()
    {
        static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["ce_specversion"] = Text("CloudEvents version (1.0)."),
                ["ce_id"] = Text("Message id (UUIDv7)."),
                ["ce_type"] = Text("Logical message type, for example bank.account.opened."),
                ["ce_source"] = Text("Producing service."),
                ["ce_time"] = Text("Creation time (RFC 3339)."),
                ["ce_subject"] = Text("Stream id, when the message concerns one aggregate."),
                ["ce_sftypeversion"] = Text("Message type version."),
                ["ce_sfcorrelationid"] = Text("Correlation id."),
                ["ce_sfcausationid"] = Text("Causation id."),
                ["ce_sftenant"] = Text("Tenant id."),
                ["traceparent"] = Text("W3C trace context."),
            },
            ["required"] = new JsonArray("ce_specversion", "ce_id", "ce_type", "ce_source"),
        };
    }

    private static JsonObject Reference(string pointer)
    {
        return new JsonObject { ["$ref"] = pointer };
    }

    private static string MessageId(string type, int version)
    {
        return ChannelId(type) + "_v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // AsyncAPI component keys: ^[a-zA-Z0-9\.\-_]+$.
    private static string ChannelId(string name)
    {
        var id = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            id.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }

        return id.ToString();
    }
}
