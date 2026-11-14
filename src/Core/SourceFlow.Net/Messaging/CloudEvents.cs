using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace SourceFlow.Messaging;

/// <summary>
/// CloudEvents 1.0 mapping of <see cref="MessageEnvelope"/> (ADR-011, doc 04 §1.1). Binary mode puts attributes in
/// transport headers and the body as data; structured mode is one JSON document. Both round-trip losslessly.
/// </summary>
public static class CloudEvents
{
    /// <summary>The CloudEvents spec version.</summary>
    public const string SpecVersion = "1.0";

    /// <summary>The structured-mode content type.</summary>
    public const string StructuredContentType = "application/cloudevents+json";

    /// <summary>The attribute that carries extensions that did not fit the header budget.</summary>
    public const string OverflowAttribute = "sfext";

    /// <summary>The attribute that carries the user headers (as a JSON object).</summary>
    public const string HeadersAttribute = "sfheaders";

#pragma warning disable CS1591 // attribute names are self-describing
    public const string Id = "id";
    public const string Source = "source";
    public const string Specversion = "specversion";
    public const string Type = "type";
    public const string Subject = "subject";
    public const string Time = "time";
    public const string DataContentType = "datacontenttype";
    public const string Correlation = "sfcorrelationid";
    public const string Causation = "sfcausationid";
    public const string PartitionKey = "sfpartitionkey";
    public const string StreamVersion = "sfstreamversion";
    public const string Tenant = "sftenant";
    public const string Actor = "sfactor";
    public const string Protection = "sfenc";
    public const string Attempts = "sfattempt";
    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";
#pragma warning restore CS1591

    private const string SourcePrefix = "urn:sourceflow:";

    // Required attributes are never moved to the overflow attribute.
    private static readonly FrozenSet<string> Required = new[] { Id, Source, Specversion, Type, Time, DataContentType }.ToFrozenSet();

    /// <summary>Formats the CloudEvents type, <c>{name}.v{version}</c>.</summary>
    public static string FormatType(string name, int version)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{name}.v{version}");
    }

    /// <summary>Parses <c>{name}.v{version}</c>.</summary>
    public static bool TryParseType(string? value, out string name, out int version)
    {
        name = string.Empty;
        version = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int index = value.LastIndexOf(".v", StringComparison.Ordinal);
        if (index <= 0 || !int.TryParse(value.AsSpan(index + 2), NumberStyles.None, CultureInfo.InvariantCulture, out version) || version < 1)
        {
            return false;
        }

        name = value[..index];
        return true;
    }

    /// <summary>
    /// Maps an envelope to binary-mode attributes. When there are more than <paramref name="maxAttributes"/> attributes,
    /// optional extensions move into one <see cref="OverflowAttribute"/> JSON attribute (SQS has a 10-attribute limit).
    /// </summary>
    public static Dictionary<string, string> ToAttributes(MessageEnvelope envelope, int maxAttributes = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Specversion] = SpecVersion,
            [Id] = envelope.MessageId.ToString(),
            [Source] = SourcePrefix + envelope.Source,
            [Type] = envelope.CloudEventType,
            [Time] = envelope.Time.ToString("O", CultureInfo.InvariantCulture),
            [DataContentType] = envelope.ContentType,
            [Tenant] = envelope.TenantId,
        };

        AddIfPresent(attributes, Subject, envelope.Subject);
        AddIfPresent(attributes, Correlation, envelope.CorrelationId.IsNone ? null : envelope.CorrelationId.Value);
        AddIfPresent(attributes, Causation, envelope.CausationId?.ToString());
        AddIfPresent(attributes, PartitionKey, envelope.PartitionKey);
        AddIfPresent(attributes, StreamVersion, envelope.StreamVersion?.ToString(CultureInfo.InvariantCulture));
        AddIfPresent(attributes, Actor, envelope.Actor);
        AddIfPresent(attributes, Protection, envelope.Protection);
        AddIfPresent(attributes, Attempts, envelope.Attempts?.ToString(CultureInfo.InvariantCulture));
        AddIfPresent(attributes, TraceParent, envelope.TraceParent);
        AddIfPresent(attributes, TraceState, envelope.TraceState);
        if (envelope.Headers.Count > 0)
        {
            attributes[HeadersAttribute] = WriteObject(envelope.Headers);
        }

        if (attributes.Count > maxAttributes)
        {
            if (maxAttributes < Required.Count + 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAttributes), maxAttributes, $"At least {Required.Count + 1} attributes are needed.");
            }

            var overflow = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in attributes.Keys.Where(k => !Required.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray())
            {
                if (attributes.Count + 1 <= maxAttributes)
                {
                    break;
                }

                overflow[key] = attributes[key];
                attributes.Remove(key);
            }

            attributes[OverflowAttribute] = WriteObject(overflow);
        }

        return attributes;
    }

    /// <summary>Maps binary-mode attributes and a body back to an envelope.</summary>
    /// <exception cref="FormatException">
    /// A required attribute is missing, or an attribute is invalid (the only exception for malformed input, so a receiver
    /// that catches it can't be stopped by a crafted message).
    /// </exception>
    public static MessageEnvelope FromAttributes(IReadOnlyDictionary<string, string> attributes, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        try
        {
            return DecodeAttributes(attributes, body);
        }
        catch (Exception e) when (IsMalformed(e))
        {
            throw new FormatException("The CloudEvents attributes are malformed: " + e.Message, e);
        }
    }

    private static MessageEnvelope DecodeAttributes(IReadOnlyDictionary<string, string> attributes, ReadOnlyMemory<byte> body)
    {
        var all = new Dictionary<string, string>(attributes, StringComparer.Ordinal);
        if (all.Remove(OverflowAttribute, out string? overflow))
        {
            foreach (KeyValuePair<string, string> pair in ReadObject(overflow))
            {
                all[pair.Key] = pair.Value;
            }
        }

        string Required(string name)
        {
            return all.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value)
                ? value
                : throw new FormatException($"CloudEvents attribute '{name}' is missing.");
        }

        if (!TryParseType(Required(Type), out string typeName, out int version))
        {
            throw new FormatException($"CloudEvents type '{all[Type]}' is not in the '{{name}}.v{{version}}' form.");
        }

        string source = Required(Source);
        return new MessageEnvelope
        {
            MessageId = MessageId.Parse(Required(Id)),
            Type = typeName,
            TypeVersion = version,
            Source = source.StartsWith(SourcePrefix, StringComparison.Ordinal) ? source[SourcePrefix.Length..] : source,
            Time = DateTimeOffset.Parse(Required(Time), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ContentType = all.TryGetValue(DataContentType, out string? ct) ? ct : MessageEnvelope.JsonContentType,
            Subject = Optional(all, Subject),
            CorrelationId = Optional(all, Correlation) is { } correlation ? new CorrelationId(correlation) : CorrelationId.None,
            CausationId = Optional(all, Causation) is { } causation ? MessageId.Parse(causation) : null,
            PartitionKey = Optional(all, PartitionKey),
            StreamVersion = Optional(all, StreamVersion) is { } sv ? long.Parse(sv, NumberStyles.None, CultureInfo.InvariantCulture) : null,
            TenantId = Optional(all, Tenant) ?? MessageEnvelope.DefaultTenant,
            Actor = Optional(all, Actor),
            Protection = Optional(all, Protection),
            Attempts = Optional(all, Attempts) is { } attempts ? int.Parse(attempts, NumberStyles.None, CultureInfo.InvariantCulture) : null,
            TraceParent = Optional(all, TraceParent),
            TraceState = Optional(all, TraceState),
            Headers = Optional(all, HeadersAttribute) is { } headers ? ReadObject(headers).ToFrozenDictionary(StringComparer.Ordinal) : FrozenDictionary<string, string>.Empty,
            Body = body,
        };
    }

    /// <summary>Writes a structured-mode CloudEvents JSON document. JSON bodies are embedded as <c>data</c>; others as <c>data_base64</c>.</summary>
    public static byte[] ToStructured(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var buffer = new ArrayBufferWriter<byte>(envelope.Body.Length + 512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> pair in ToAttributes(envelope).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            if (envelope.ContentType == MessageEnvelope.JsonContentType && envelope.Protection is null && !envelope.Body.IsEmpty)
            {
                writer.WritePropertyName("data");
                writer.WriteRawValue(envelope.Body.Span, skipInputValidation: false);
            }
            else
            {
                writer.WriteBase64String("data_base64", envelope.Body.Span);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads a structured-mode CloudEvents JSON document.</summary>
    /// <exception cref="FormatException">The document is not a valid CloudEvent (the only exception for malformed input).</exception>
    public static MessageEnvelope FromStructured(ReadOnlyMemory<byte> document)
    {
        try
        {
            return DecodeStructured(document);
        }
        catch (Exception e) when (IsMalformed(e))
        {
            throw new FormatException("The structured CloudEvent is malformed: " + e.Message, e);
        }
    }

    // Wire JSON (doc 10 §2): a duplicated attribute ("type" twice) is rejected, not resolved by the last one winning.
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false, MaxDepth = 64 };

    // What System.Text.Json and the primitive parsers throw for malformed input (FormatException passes through).
    private static bool IsMalformed(Exception e)
    {
        return e is JsonException or InvalidOperationException or OverflowException or (ArgumentException and not ArgumentNullException);
    }

    private static MessageEnvelope DecodeStructured(ReadOnlyMemory<byte> document)
    {
        using JsonDocument json = JsonDocument.Parse(document, Strict);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        ReadOnlyMemory<byte> body = ReadOnlyMemory<byte>.Empty;
        foreach (JsonProperty property in json.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "data":
                    body = System.Text.Encoding.UTF8.GetBytes(property.Value.GetRawText());
                    break;
                case "data_base64":
                    body = property.Value.GetBytesFromBase64();
                    break;
                default:
                    attributes[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
                    break;
            }
        }

        return FromAttributes(attributes, body);
    }

    private static void AddIfPresent(Dictionary<string, string> attributes, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            attributes[name] = value;
        }
    }

    private static string? Optional(Dictionary<string, string> attributes, string name)
    {
        return attributes.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value) ? value : null;
    }

    private static string WriteObject(IEnumerable<KeyValuePair<string, string>> values)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> pair in values.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static Dictionary<string, string> ReadObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, Strict);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return result;
    }
}
