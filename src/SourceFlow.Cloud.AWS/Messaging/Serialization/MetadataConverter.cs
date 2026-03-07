using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using SourceFlow.Messaging;

namespace SourceFlow.Cloud.AWS.Messaging.Serialization;

/// <summary>
/// JSON converter for Metadata to handle Dictionary{string, object} properly.
/// </summary>
public class MetadataConverter : JsonConverter<Metadata>
{
    public override Metadata Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        var metadata = new Metadata();

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.TryGetProperty("eventId", out var eventId))
        {
            metadata.EventId = eventId.GetGuid();
        }

        if (root.TryGetProperty("isReplay", out var isReplay))
        {
            metadata.IsReplay = isReplay.GetBoolean();
        }

        if (root.TryGetProperty("occurredOn", out var occurredOn))
        {
            metadata.OccurredOn = occurredOn.GetDateTime();
        }

        if (root.TryGetProperty("sequenceNo", out var sequenceNo))
        {
            metadata.SequenceNo = sequenceNo.GetInt32();
        }

        if (root.TryGetProperty("properties", out var properties))
        {
            metadata.Properties = JsonSerializer.Deserialize<Dictionary<string, object>>(
                properties.GetRawText(),
                options) ?? new Dictionary<string, object>();
        }

        return metadata;
    }

    public override void Write(Utf8JsonWriter writer, Metadata value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();

        writer.WriteString("eventId", value.EventId);
        writer.WriteBoolean("isReplay", value.IsReplay);
        writer.WriteString("occurredOn", value.OccurredOn);
        writer.WriteNumber("sequenceNo", value.SequenceNo);

        if (value.Properties != null && value.Properties.Count > 0)
        {
            writer.WritePropertyName("properties");
            JsonSerializer.Serialize(writer, value.Properties, options);
        }

        writer.WriteEndObject();
    }
}
