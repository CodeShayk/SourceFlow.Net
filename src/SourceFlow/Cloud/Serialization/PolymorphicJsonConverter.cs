using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SourceFlow.Cloud.Serialization;

/// <summary>
/// Base class for polymorphic JSON converters that use $type discriminator
/// </summary>
public abstract class PolymorphicJsonConverter<T> : JsonConverter<T>
{
    protected const string TypeDiscriminator = "$type";

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected StartObject token, got {reader.TokenType}");
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Get the actual type from $type discriminator
        if (!root.TryGetProperty(TypeDiscriminator, out var typeProperty))
        {
            throw new JsonException($"Missing {TypeDiscriminator} discriminator for polymorphic type {typeof(T).Name}");
        }

        var typeString = typeProperty.GetString();
        if (string.IsNullOrEmpty(typeString))
        {
            throw new JsonException($"{TypeDiscriminator} discriminator is empty");
        }

        var actualType = ResolveType(typeString);

        // Deserialize as the actual type
        var json = root.GetRawText();
        return (T?)JsonSerializer.Deserialize(json, actualType, options);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();

        // Write type discriminator
        var actualType = value.GetType();
        writer.WriteString(TypeDiscriminator, GetTypeIdentifier(actualType));

        // Serialize the actual object properties
        var json = JsonSerializer.Serialize(value, actualType, options);
        using var doc = JsonDocument.Parse(json);

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            // Skip $type if it already exists
            if (property.Name == TypeDiscriminator)
                continue;

            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Get type identifier for serialization (e.g., AssemblyQualifiedName or simplified name)
    /// </summary>
    protected virtual string GetTypeIdentifier(Type type)
    {
        return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
    }

    /// <summary>
    /// Resolve type from type identifier
    /// </summary>
    protected virtual Type ResolveType(string typeIdentifier)
    {
        var type = Type.GetType(typeIdentifier);
        if (type == null)
        {
            throw new JsonException(
                $"Cannot resolve type '{typeIdentifier}'. Ensure the assembly containing this type is loaded and the type name is assembly-qualified.");
        }
        return type;
    }
}
