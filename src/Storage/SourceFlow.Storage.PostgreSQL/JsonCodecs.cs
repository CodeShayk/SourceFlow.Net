using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using SourceFlow.Messaging;

namespace SourceFlow.Storage.PostgreSQL;

/// <summary>Reflection-free JSON for the <c>metadata</c> and <c>attributes</c> columns (AOT-safe).</summary>
internal static class JsonCodecs
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, SkipValidation = true };

    /// <summary>Event metadata as a JSON object; keys match the SFQL/expression-index paths (doc 05 §2).</summary>
    public static string WriteMetadata(EventMetadata metadata)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            if (!metadata.CorrelationId.IsNone)
            {
                writer.WriteString("correlationId", metadata.CorrelationId.Value);
            }

            if (metadata.CausationId is { } causation)
            {
                writer.WriteString("causationId", causation.ToString());
            }

            writer.WriteString("source", metadata.Source);
            WriteOptional(writer, "actor", metadata.Actor);
            WriteOptional(writer, "traceParent", metadata.TraceParent);
            WriteOptional(writer, "traceState", metadata.TraceState);
            if (metadata.Headers.Count > 0)
            {
                writer.WriteStartObject("headers");
                foreach (KeyValuePair<string, string> header in metadata.Headers)
                {
                    writer.WriteString(header.Key, header.Value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static EventMetadata ReadMetadata(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        Expect(ref reader, JsonTokenType.StartObject);
        CorrelationId correlation = CorrelationId.None;
        MessageId? causation = null;
        string source = string.Empty;
        string? actor = null;
        string? traceParent = null;
        string? traceState = null;
        IReadOnlyDictionary<string, string> headers = FrozenDictionary<string, string>.Empty;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case "correlationId":
                    correlation = new CorrelationId(reader.GetString()!);
                    break;
                case "causationId":
                    causation = MessageId.Parse(reader.GetString()!);
                    break;
                case "source":
                    source = reader.GetString()!;
                    break;
                case "actor":
                    actor = reader.GetString();
                    break;
                case "traceParent":
                    traceParent = reader.GetString();
                    break;
                case "traceState":
                    traceState = reader.GetString();
                    break;
                case "headers":
                    headers = ReadStringMap(ref reader).ToFrozenDictionary(StringComparer.Ordinal);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new EventMetadata
        {
            CorrelationId = correlation,
            CausationId = causation,
            Source = source,
            Actor = actor,
            TraceParent = traceParent,
            TraceState = traceState,
            Headers = headers,
        };
    }

    /// <summary>Envelope attributes (CloudEvents binary-mode map) as a flat JSON object.</summary>
    public static string WriteAttributes(MessageEnvelope envelope)
    {
        Dictionary<string, string> attributes = CloudEvents.ToAttributes(envelope);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> attribute in attributes)
            {
                writer.WriteString(attribute.Key, attribute.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static MessageEnvelope ReadEnvelope(ReadOnlySpan<byte> attributesJson, byte[] body)
    {
        var reader = new Utf8JsonReader(attributesJson);
        Expect(ref reader, JsonTokenType.StartObject);
        return CloudEvents.FromAttributes(ReadStringMap(ref reader), body);
    }

    /// <summary>A list of tags as a JSON array, for the jagged <c>text[]</c> per event (doc 05 §3.1).</summary>
    public static string WriteTags(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return "[]";
        }

        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (string tag in tags)
            {
                writer.WriteStringValue(tag);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static Dictionary<string, string> ReadStringMap(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a JSON object.");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString()!;
            reader.Read();
            map[key] = reader.GetString() ?? string.Empty;
        }

        return map;
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType token)
    {
        if (!reader.Read() || reader.TokenType != token)
        {
            throw new JsonException($"Expected {token}.");
        }
    }
}
