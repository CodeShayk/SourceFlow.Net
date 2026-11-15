using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Google.Protobuf;
using Google.Cloud.PubSub.V1;
using Encoding = System.Text.Encoding;
using SourceFlow.Messaging;

namespace SourceFlow.Cloud.Gcp;

/// <summary>
/// The Pub/Sub wire format: the CloudEvents Pub/Sub binding in binary mode (doc 07 §4). Attributes travel as message
/// attributes named <c>ce-{attribute}</c>; the body is the message data, byte for byte. Pub/Sub limits an attribute value
/// to 1,024 bytes, so a longer value (a long subject, the user headers) is split into numbered chunks
/// (<c>ce-subject#0</c>, <c>ce-subject#1</c>, …) and joined again on receipt. The ordering key is the partition key.
/// </summary>
public static class GcpWire
{
    /// <summary>The prefix of CloudEvents attribute names (the CloudEvents Pub/Sub protocol binding).</summary>
    public const string CloudEventsPrefix = "ce-";

    /// <summary>Why a message was moved to the dead-letter topic by the transport (explicitly, or after the delivery limit).</summary>
    public const string ReasonAttribute = "sf-reason";

    /// <summary>Separates an attribute name from its chunk number.</summary>
    public const char ChunkSeparator = '#';

    /// <summary>The Pub/Sub limit on attributes per message.</summary>
    public const int MaxAttributes = 100;

    /// <summary>The Pub/Sub limit on an attribute value.</summary>
    public const int MaxAttributeValueBytes = 1024;

    /// <summary>The Pub/Sub limit on an ordering key.</summary>
    public const int MaxOrderingKeyBytes = 1024;

    /// <summary>The Pub/Sub limit on a publish request (and so on one message with its attributes).</summary>
    public const int MaxRequestBytes = 10_000_000;

    /// <summary>
    /// The largest envelope body: 9 MiB, so the body, 100 attributes of the largest size and the ordering key always fit
    /// one publish request.
    /// </summary>
    public const int MaxBodyBytes = 9 * 1024 * 1024;

    // Protobuf framing and the topic name, per message and per request.
    private const int MessageOverhead = 64;

    /// <summary>Encodes an envelope for publishing.</summary>
    /// <param name="envelope">The envelope.</param>
    /// <param name="reason">The dead-letter reason, for a move to the dead-letter topic.</param>
    public static GcpOutgoingMessage Encode(MessageEnvelope envelope, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var message = new PubsubMessage { Data = ByteString.CopyFrom(envelope.Body.Span), OrderingKey = OrderingKey(envelope) };
        int budget = MaxAttributes - (reason is null ? 0 : 1);
        foreach ((string name, string value) in CloudEvents.ToAttributes(envelope))
        {
            AddChunked(message, CloudEventsPrefix + name, value);
        }

        if (message.Attributes.Count > budget)
        {
            // Only extensions of tens of kilobytes (a huge subject or actor) need more than 100 chunked attributes.
            throw new MessageTooLargeException(envelope.CloudEventType, Size(message) - message.Data.Length, budget * MaxAttributeValueBytes);
        }

        if (reason is not null)
        {
            message.Attributes[ReasonAttribute] = Truncate(reason, MaxAttributeValueBytes);
        }

        return new GcpOutgoingMessage(envelope, message, Size(message));
    }

    /// <summary>Copies a received message for the dead-letter topic, with the reason (any message, even one that does not decode).</summary>
    public static PubsubMessage ForDeadLetter(PubsubMessage received, string reason)
    {
        ArgumentNullException.ThrowIfNull(received);
        ArgumentNullException.ThrowIfNull(reason);
        var copy = new PubsubMessage { Data = received.Data };
        copy.Attributes.Add(received.Attributes);
        copy.Attributes[ReasonAttribute] = Truncate(reason, MaxAttributeValueBytes);
        return copy;
    }

    /// <summary>Decodes a received message.</summary>
    /// <exception cref="FormatException">The message is not a SourceFlow CloudEvent.</exception>
    public static MessageEnvelope Decode(PubsubMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var chunks = new Dictionary<string, SortedList<int, string>>(StringComparer.Ordinal);
        foreach ((string key, string value) in message.Attributes)
        {
            if (!key.StartsWith(CloudEventsPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string name = key[CloudEventsPrefix.Length..];
            int separator = name.IndexOf(ChunkSeparator, StringComparison.Ordinal);
            if (separator < 0)
            {
                attributes[name] = value;
                continue;
            }

            if (!int.TryParse(name.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                throw new FormatException($"The attribute '{key}' has an invalid chunk number.");
            }

            string chunked = name[..separator];
            if (!chunks.TryGetValue(chunked, out SortedList<int, string>? parts))
            {
                chunks[chunked] = parts = [];
            }

            parts[index] = value;
        }

        foreach ((string name, SortedList<int, string> parts) in chunks)
        {
            if (parts.Keys[^1] != parts.Count - 1)
            {
                throw new FormatException($"The attribute '{CloudEventsPrefix}{name}' is missing chunks.");
            }

            attributes[name] = string.Concat(parts.Values);
        }

        return CloudEvents.FromAttributes(attributes, message.Data.Memory);
    }

    /// <summary>
    /// The ordering key of an envelope: its partition key (per-key order, doc 07 §4), hashed when longer than Pub/Sub
    /// allows; empty without one.
    /// </summary>
    public static string OrderingKey(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        string? key = envelope.EffectivePartitionKey;
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetByteCount(key) <= MaxOrderingKeyBytes
            ? key
            : "k-" + XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(key)).ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>The size Pub/Sub counts against the request limit (data, attributes, ordering key and framing).</summary>
    public static int Size(PubsubMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        int size = message.Data.Length + Encoding.UTF8.GetByteCount(message.OrderingKey) + MessageOverhead;
        foreach ((string key, string value) in message.Attributes)
        {
            size += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value) + 4;
        }

        return size;
    }

    private static void AddChunked(PubsubMessage message, string key, string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaxAttributeValueBytes)
        {
            message.Attributes[key] = value;
            return;
        }

        int index = 0;
        int start = 0;
        while (start < value.Length)
        {
            int length = FittingLength(value.AsSpan(start), MaxAttributeValueBytes);
            message.Attributes[key + ChunkSeparator + index.ToString(CultureInfo.InvariantCulture)] = value.Substring(start, length);
            start += length;
            index++;
        }
    }

    // The longest prefix, in chars, whose UTF-8 form fits the limit without splitting a surrogate pair.
    private static int FittingLength(ReadOnlySpan<char> value, int maxBytes)
    {
        int bytes = 0;
        int i = 0;
        while (i < value.Length)
        {
            int width = char.IsHighSurrogate(value[i]) && i + 1 < value.Length ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(value.Slice(i, width));
            if (bytes + size > maxBytes)
            {
                break;
            }

            bytes += size;
            i += width;
        }

        return i;
    }

    private static string Truncate(string value, int maxBytes)
    {
        return value[..FittingLength(value, maxBytes)];
    }
}

/// <summary>An encoded message, ready for a publish request.</summary>
/// <param name="Envelope">The envelope.</param>
/// <param name="Message">The Pub/Sub message.</param>
/// <param name="Size">The size Pub/Sub counts against the request limit.</param>
public sealed record GcpOutgoingMessage(MessageEnvelope Envelope, PubsubMessage Message, int Size);
