using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SourceFlow.Messaging;

namespace SourceFlow.Migration;

/// <summary>
/// The explicit allow-list of the v2 wire bridge (doc 07 §8): v2 command and event types, by assembly-qualified name, mapped
/// to v3 message types. A mapped v2 message becomes a v3 envelope with a deterministic id (UUIDv5 of the v2 message id),
/// so a redelivered v2 message is a duplicate for the inbox; an unmapped one is parked. Planned for removal in 4.0.
/// </summary>
public sealed class V2MessageMap
{
    /// <summary>The source of translated envelopes.</summary>
    public const string Source = "sourceflow-v2";

    /// <summary>The header holding the v2 type name as received.</summary>
    public const string V2TypeHeader = "sourceflow-v2-type";

    /// <summary>The header holding the v2 transport message id.</summary>
    public const string V2MessageIdHeader = "sourceflow-v2-message-id";

    /// <summary>The header marking a v2 message that is not on the allow-list.</summary>
    public const string UnmappedHeader = "sourceflow-v2-unmapped";

    private readonly Dictionary<string, V2MessageMapping> _mappings = new(StringComparer.Ordinal);

    /// <summary>The mappings.</summary>
    public IReadOnlyCollection<V2MessageMapping> Mappings => _mappings.Values;

    /// <summary>Maps a v2 type to a v3 message type.</summary>
    /// <param name="v2TypeName">
    /// The v2 assembly-qualified name, for example <c>MyApp.Commands.CreateAccount, MyApp</c>; assembly version, culture
    /// and public key token are ignored.
    /// </param>
    /// <param name="type">The v3 message type name.</param>
    /// <param name="version">The v3 type version.</param>
    /// <param name="convert">
    /// Converts the v2 JSON to the v3 record's JSON; null takes the v2 <c>payload</c> object (or the whole body when it
    /// has none) unchanged.
    /// </param>
    public V2MessageMap Type(string v2TypeName, string type, int version, Func<V2Message, JsonNode?>? convert = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(v2TypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        string key = V2TypeNames.Normalize(v2TypeName);
        if (!_mappings.TryAdd(key, new V2MessageMapping(key, type, version, convert)))
        {
            throw new ArgumentException($"The v2 type '{key}' is already mapped.", nameof(v2TypeName));
        }

        return this;
    }

    /// <summary>The mapping of a v2 type, if it is on the allow-list.</summary>
    public bool TryGet(string v2TypeName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out V2MessageMapping? mapping)
    {
        ArgumentNullException.ThrowIfNull(v2TypeName);
        return _mappings.TryGetValue(V2TypeNames.Normalize(v2TypeName), out mapping);
    }

    /// <summary>The deterministic v3 message id of a v2 message: UUIDv5 of the v2 transport message id.</summary>
    public static MessageId MessageIdOf(string v2MessageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(v2MessageId);
        return new MessageId(MessageIds.CreateVersion5(MessageIds.SourceFlowNamespace, "v2-message\n" + v2MessageId));
    }

    /// <summary>
    /// Translates a v2 message. A mapped type gets its v3 type, version and converted body; an unmapped one keeps its v2
    /// type name and body and is marked with <see cref="UnmappedHeader"/>, so the receive pipeline parks it as an unknown
    /// message type instead of losing it.
    /// </summary>
    /// <exception cref="JsonException">The v2 body is not JSON (mapped types only).</exception>
    public MessageEnvelope Translate(V2Message message, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(time);
        MessageId id = MessageIdOf(message.MessageId);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [V2TypeHeader] = message.V2Type,
            [V2MessageIdHeader] = message.MessageId,
        };

        string type;
        int version;
        ReadOnlyMemory<byte> body;
        if (TryGet(message.V2Type, out V2MessageMapping? mapping))
        {
            type = mapping.Type;
            version = mapping.Version;
            JsonNode? converted = mapping.Convert is null ? message.Payload ?? message.Json : mapping.Convert(message);
            body = converted is null ? "null"u8.ToArray() : Write(converted);
        }
        else
        {
            type = V2TypeNames.Normalize(message.V2Type);
            version = 1;
            body = Encoding.UTF8.GetBytes(message.Body);
            headers[UnmappedHeader] = "true";
        }

        return new MessageEnvelope
        {
            MessageId = id,
            Type = type,
            TypeVersion = version,
            Source = Source,
            Time = time.GetUtcNow(),
            CorrelationId = CorrelationId.From(id),
            Headers = headers,
            Body = body,
        };
    }

    private static byte[] Write(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            node.WriteTo(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>A v2 type on the bridge's allow-list.</summary>
/// <param name="V2Type">The normalized v2 type name.</param>
/// <param name="Type">The v3 message type.</param>
/// <param name="Version">The v3 type version.</param>
/// <param name="Convert">The body conversion, or null for the v2 payload as is.</param>
public sealed record V2MessageMapping(string V2Type, string Type, int Version, Func<V2Message, JsonNode?>? Convert);

/// <summary>A message in the v2 wire format, as the bridge received it.</summary>
public sealed class V2Message
{
    private JsonNode? _json;
    private bool _parsed;

    /// <summary>The v2 <c>CommandType</c> or <c>EventType</c> attribute (an assembly-qualified name).</summary>
    public required string V2Type { get; init; }

    /// <summary>The transport's message id (the SQS or SNS message id).</summary>
    public required string MessageId { get; init; }

    /// <summary>The v2 JSON body (camelCase <c>Command&lt;TPayload&gt;</c> or <c>Event&lt;TEntity&gt;</c>).</summary>
    public required string Body { get; init; }

    /// <summary>True for a v2 event (<c>EventType</c>), false for a command (<c>CommandType</c>).</summary>
    public bool IsEvent { get; init; }

    /// <summary>The v2 <c>EntityId</c> attribute of a command.</summary>
    public int? EntityId { get; init; }

    /// <summary>The v2 <c>SequenceNo</c> attribute of a command.</summary>
    public int? SequenceNo { get; init; }

    /// <summary>The v2 <c>EventName</c> attribute of an event.</summary>
    public string? EventName { get; init; }

    /// <summary>The parsed body.</summary>
    /// <exception cref="JsonException">The body is not JSON.</exception>
    public JsonNode? Json
    {
        get
        {
            if (!_parsed)
            {
                _json = JsonNode.Parse(Body);
                _parsed = true;
            }

            return _json;
        }
    }

    /// <summary>The body's <c>payload</c> (a command's payload, an event's entity), or null.</summary>
    public JsonNode? Payload => Json is JsonObject o ? o["payload"] ?? o["Payload"] : null;
}
