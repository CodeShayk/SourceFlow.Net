using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SourceFlow.Messaging;

/// <summary>Serializes message payloads (doc 04 §2.2). Pluggable per content type; only JSON ships in 3.0.</summary>
public interface IMessageSerializer
{
    /// <summary>The content type written into the envelope.</summary>
    string ContentType { get; }

    /// <summary>Writes <paramref name="message"/> into <paramref name="destination"/>.</summary>
    void Serialize(object message, MessageTypeInfo type, IBufferWriter<byte> destination);

    /// <summary>Reads a message of <paramref name="type"/>; the type must come from the registry (allow-list).</summary>
    object Deserialize(ReadOnlySequence<byte> source, MessageTypeInfo type);
}

/// <summary>Convenience overloads for <see cref="IMessageSerializer"/>.</summary>
public static class MessageSerializerExtensions
{
    /// <summary>Serializes into a new array.</summary>
    public static byte[] SerializeToArray(this IMessageSerializer serializer, object message, MessageTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        var buffer = new ArrayBufferWriter<byte>(256);
        serializer.Serialize(message, type, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Deserializes from contiguous memory.</summary>
    public static object Deserialize(this IMessageSerializer serializer, ReadOnlyMemory<byte> source, MessageTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        return serializer.Deserialize(new ReadOnlySequence<byte>(source), type);
    }
}

/// <summary>
/// The default serializer: System.Text.Json with the modules' source-generated contexts (no reflection; DC-1),
/// camelCase, strict number handling, a bounded depth and no duplicate properties.
/// </summary>
public sealed class SystemTextJsonMessageSerializer : IMessageSerializer
{
    /// <inheritdoc />
    public string ContentType => MessageEnvelope.JsonContentType;

    /// <summary>Creates the shared options for a set of module resolvers and converters.</summary>
    public static JsonSerializerOptions CreateOptions(IEnumerable<IJsonTypeInfoResolver> resolvers, IEnumerable<JsonConverter> converters)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        ArgumentNullException.ThrowIfNull(converters);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.Strict,
            AllowDuplicateProperties = false,
            MaxDepth = 64,
            WriteIndented = false,
            TypeInfoResolver = JsonTypeInfoResolver.Combine([.. resolvers]),
        };

        foreach (JsonConverter converter in converters)
        {
            options.Converters.Add(converter);
        }

        return options;
    }

    /// <inheritdoc />
    public void Serialize(object message, MessageTypeInfo type, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(destination);
        if (!type.ClrType.IsInstanceOfType(message))
        {
            throw new ArgumentException($"Message of type '{message.GetType().Name}' does not match registered type {type}.", nameof(message));
        }

        using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { SkipValidation = false, MaxDepth = type.JsonTypeInfo.Options.MaxDepth });
        JsonSerializer.Serialize(writer, message, type.JsonTypeInfo);
    }

    /// <inheritdoc />
    public object Deserialize(ReadOnlySequence<byte> source, MessageTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var reader = new Utf8JsonReader(source, new JsonReaderOptions { MaxDepth = type.JsonTypeInfo.Options.MaxDepth });
        return JsonSerializer.Deserialize(ref reader, type.JsonTypeInfo)
            ?? throw new JsonException($"The body of {type} is the JSON literal null.");
    }
}
