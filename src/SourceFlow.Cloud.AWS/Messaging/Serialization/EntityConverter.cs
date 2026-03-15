using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using SourceFlow;

namespace SourceFlow.Cloud.AWS.Messaging.Serialization;

/// <summary>
/// JSON converter for IEntity that preserves the concrete type information during serialization.
/// Used for event payloads which are IEntity types.
/// </summary>
public class EntityConverter : JsonConverter<IEntity>
{
    public override IEntity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Get the type information
        if (!root.TryGetProperty("$type", out var typeProperty))
        {
            throw new JsonException("Entity missing $type property for deserialization");
        }

        var typeName = typeProperty.GetString();
        var type = Type.GetType(typeName);

        if (type == null)
        {
            throw new JsonException($"Could not resolve entity type: {typeName}");
        }

        // Get the entity data
        if (!root.TryGetProperty("$value", out var valueProperty))
        {
            throw new JsonException("Entity missing $value property for deserialization");
        }

        // Deserialize to the concrete type
        var entity = JsonSerializer.Deserialize(valueProperty.GetRawText(), type, options);
        return entity as IEntity ?? throw new JsonException($"Type {typeName} does not implement IEntity");
    }

    public override void Write(Utf8JsonWriter writer, IEntity value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();

        // Write type information
        writer.WriteString("$type", value.GetType().AssemblyQualifiedName);

        // Write the actual entity
        writer.WritePropertyName("$value");
        JsonSerializer.Serialize(writer, value, value.GetType(), options);

        writer.WriteEndObject();
    }
}
