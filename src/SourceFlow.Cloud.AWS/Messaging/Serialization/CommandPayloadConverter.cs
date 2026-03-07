using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using SourceFlow.Messaging;

namespace SourceFlow.Cloud.AWS.Messaging.Serialization;

/// <summary>
/// JSON converter for IPayload that preserves the concrete type information during serialization.
/// </summary>
public class CommandPayloadConverter : JsonConverter<IPayload>
{
    public override IPayload Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Get the type information
        if (!root.TryGetProperty("$type", out var typeProperty))
        {
            throw new JsonException("Payload missing $type property for deserialization");
        }

        var typeName = typeProperty.GetString();
        var type = Type.GetType(typeName);

        if (type == null)
        {
            throw new JsonException($"Could not resolve payload type: {typeName}");
        }

        // Get the payload data
        if (!root.TryGetProperty("$value", out var valueProperty))
        {
            throw new JsonException("Payload missing $value property for deserialization");
        }

        // Deserialize to the concrete type
        var payload = JsonSerializer.Deserialize(valueProperty.GetRawText(), type, options);
        return payload as IPayload ?? throw new JsonException($"Type {typeName} does not implement IPayload");
    }

    public override void Write(Utf8JsonWriter writer, IPayload value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();

        // Write type information
        writer.WriteString("$type", value.GetType().AssemblyQualifiedName);

        // Write the actual payload
        writer.WritePropertyName("$value");
        JsonSerializer.Serialize(writer, value, value.GetType(), options);

        writer.WriteEndObject();
    }
}
