using System.Buffers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace SourceFlow.Catalog;

/// <summary>
/// Describes a type's JSON contract as a JSON Schema 2020-12 document (doc 14 §2.1). The schema is derived from the
/// source-generated <see cref="JsonTypeInfo"/> the serializer itself uses, so it states what is on the wire (naming policy,
/// converters, nullability) with no reflection over members (ADR-029). Member attributes become extensions:
/// <c>x-sourceflow-queryable</c>, <c>x-sourceflow-personal-data</c>, <c>x-sourceflow-renamed-from</c>, <c>x-sourceflow-tag</c>.
/// </summary>
public static class JsonSchemaGenerator
{
    /// <summary>The JSON Schema dialect.</summary>
    public const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    private const int MaxDepth = 8;

    /// <summary>Describes <paramref name="info"/>'s type; nested types are resolved through <paramref name="options"/>.</summary>
    public static JsonObject Generate(JsonTypeInfo info, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(options);
        return Describe(info.Type, info, options, 0);
    }

    /// <summary>Writes a JSON value canonically: object keys sorted ordinally, no whitespace.</summary>
    public static string Canonical(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, node);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The SHA-256 fingerprint of a canonical schema text, as <c>sha256:{hex}</c>.</summary>
    public static string Fingerprint(string canonicalSchema)
    {
        ArgumentNullException.ThrowIfNull(canonicalSchema);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSchema)));
    }

    private static JsonObject Describe(Type clr, JsonTypeInfo? info, JsonSerializerOptions options, int depth)
    {
        Type type = Nullable.GetUnderlyingType(clr) ?? clr;
        if (type == typeof(string))
        {
            return new JsonObject { ["type"] = "string" };
        }

        if (type == typeof(char))
        {
            return new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1 };
        }

        if (typeof(IStronglyTypedId).IsAssignableFrom(type))
        {
            IdBacking backing = type.GetCustomAttribute<StronglyTypedIdAttribute>()?.Backing ?? IdBacking.Guid;
            return backing switch
            {
                IdBacking.Int64 => new JsonObject { ["type"] = "integer", ["format"] = "int64" },
                IdBacking.String => new JsonObject { ["type"] = "string" },
                _ => new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            };
        }

        if (type == typeof(int) || type == typeof(short) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(ushort))
        {
            return new JsonObject { ["type"] = "integer", ["format"] = "int32" };
        }

        if (type == typeof(long) || type == typeof(uint) || type == typeof(ulong))
        {
            return new JsonObject { ["type"] = "integer", ["format"] = "int64" };
        }

        if (type == typeof(decimal))
        {
            return new JsonObject { ["type"] = "number", ["format"] = "decimal" };
        }

        if (type == typeof(double) || type == typeof(float))
        {
            return new JsonObject { ["type"] = "number", ["format"] = type == typeof(double) ? "double" : "float" };
        }

        if (type == typeof(bool))
        {
            return new JsonObject { ["type"] = "boolean" };
        }

        if (type == typeof(DateTimeOffset) || type == typeof(DateTime))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        }

        if (type == typeof(DateOnly))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "date" };
        }

        if (type == typeof(TimeOnly))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "time" };
        }

        if (type == typeof(TimeSpan))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "time-span" };
        }

        if (type == typeof(Guid))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        }

        if (type == typeof(JsonElement) || type == typeof(JsonNode) || type == typeof(object))
        {
            return [];
        }

        info ??= TryGetTypeInfo(type, options);
        if (type.IsEnum)
        {
            bool asText = info is not null && JsonSerializer.SerializeToElement(Enum.ToObject(type, 0), info).ValueKind == JsonValueKind.String;
            return new JsonObject { ["type"] = asText ? "string" : "integer", ["x-sourceflow-enum"] = type.Name };
        }

        if (info is null || depth > MaxDepth)
        {
            return [];
        }

        switch (info.Kind)
        {
            case JsonTypeInfoKind.Enumerable when info.ElementType is { } element:
                return new JsonObject { ["type"] = "array", ["items"] = Describe(element, null, options, depth + 1) };
            case JsonTypeInfoKind.Dictionary when info.ElementType is { } value:
                return new JsonObject { ["type"] = "object", ["additionalProperties"] = Describe(value, null, options, depth + 1) };
            case JsonTypeInfoKind.Object:
                return Object(info, options, depth);
            default:
                return [];
        }
    }

    private static JsonObject Object(JsonTypeInfo info, JsonSerializerOptions options, int depth)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (JsonPropertyInfo property in info.Properties.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            JsonObject schema = Describe(property.PropertyType, null, options, depth + 1);
            ICustomAttributeProvider? attributes = property.AttributeProvider;
            if (attributes is not null)
            {
                if (attributes.IsDefined(typeof(QueryableAttribute), inherit: true))
                {
                    schema["x-sourceflow-queryable"] = true;
                }

                if (attributes.IsDefined(typeof(PersonalDataAttribute), inherit: true))
                {
                    schema["x-sourceflow-personal-data"] = true;
                }

                if (attributes.GetCustomAttributes(typeof(TagAttribute), inherit: true).OfType<TagAttribute>().FirstOrDefault() is { } tag)
                {
                    schema["x-sourceflow-tag"] = tag.Name;
                }

                RenamedFromAttribute[] renames = [.. attributes.GetCustomAttributes(typeof(RenamedFromAttribute), inherit: true).OfType<RenamedFromAttribute>()];
                if (renames.Length > 0)
                {
                    var renamed = new JsonObject();
                    foreach (RenamedFromAttribute rename in renames.OrderBy(r => r.Version))
                    {
                        renamed[rename.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)] = rename.Name;
                    }

                    schema["x-sourceflow-renamed-from"] = renamed;
                }
            }

            properties[property.Name] = schema;
            bool nullableValue = Nullable.GetUnderlyingType(property.PropertyType) is not null;
            bool nullableReference = !property.PropertyType.IsValueType && property.IsGetNullable;
            if (property.IsRequired || (!nullableValue && !nullableReference))
            {
                required.Add((JsonNode?)JsonValue.Create(property.Name));
            }
        }

        var result = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            result["required"] = required;
        }

        return result;
    }

    private static JsonTypeInfo? TryGetTypeInfo(Type type, JsonSerializerOptions options)
    {
        try
        {
            return options.TryGetTypeInfo(type, out JsonTypeInfo? info) ? info : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, JsonNode?> property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (JsonNode? item in array)
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
