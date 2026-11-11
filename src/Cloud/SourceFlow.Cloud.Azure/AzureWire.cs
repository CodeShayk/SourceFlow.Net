using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Azure.Messaging.ServiceBus;
using SourceFlow.Messaging;

namespace SourceFlow.Cloud.Azure;

/// <summary>
/// The Service Bus wire format: the CloudEvents AMQP binding in binary mode (doc 07 §4). Attributes travel as application
/// properties named <c>cloudEvents:{attribute}</c> and the body is the message body, byte for byte. The broker message id
/// is the envelope id (for duplicate detection), the session id is the partition key (the message id without one), and
/// the subject is the CloudEvents type. Published events carry <c>sf-topic</c>, so a receiver can apply a topic's
/// client-side filter; a delayed retry carries the deliveries so far in <c>sf-deliveries</c>.
/// </summary>
public static class AzureWire
{
    /// <summary>The prefix of CloudEvents application properties (the CloudEvents AMQP binding).</summary>
    public const string CloudEventsPrefix = "cloudEvents:";

    /// <summary>The logical topic a published event came from.</summary>
    public const string TopicProperty = "sf-topic";

    /// <summary>Deliveries before a delayed retry re-sent the message (a re-sent message starts counting again).</summary>
    public const string DeliveriesProperty = "sf-deliveries";

    /// <summary>The Service Bus limit on a message on the standard tier, and on a batch.</summary>
    public const int MaxMessageBytes = 256 * 1024;

    /// <summary>The largest envelope body: 192 KiB, so the body and its properties (at most 64 KiB) fit a standard-tier message.</summary>
    public const int MaxBodyBytes = 192 * 1024;

    /// <summary>The Service Bus limit on session ids and message ids.</summary>
    public const int MaxIdLength = 128;

    /// <summary>Encodes an envelope for sending.</summary>
    /// <param name="envelope">The envelope.</param>
    /// <param name="topic">The logical topic, for a publish.</param>
    public static ServiceBusMessage Encode(MessageEnvelope envelope, string? topic = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var message = new ServiceBusMessage(BinaryData.FromBytes(envelope.Body))
        {
            MessageId = envelope.MessageId.ToString(),
            SessionId = SessionId(envelope),
            ContentType = envelope.ContentType,
            Subject = envelope.CloudEventType,
        };
        foreach ((string name, string value) in CloudEvents.ToAttributes(envelope))
        {
            message.ApplicationProperties[CloudEventsPrefix + name] = value;
        }

        if (topic is not null)
        {
            message.ApplicationProperties[TopicProperty] = topic;
        }

        return message;
    }

    /// <summary>Decodes a received message from its application properties and body.</summary>
    /// <exception cref="FormatException">The message is not a SourceFlow CloudEvent.</exception>
    public static MessageEnvelope Decode(IReadOnlyDictionary<string, object> properties, BinaryData body)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(body);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, object value) in properties)
        {
            if (name.StartsWith(CloudEventsPrefix, StringComparison.Ordinal) && value is string text)
            {
                attributes[name[CloudEventsPrefix.Length..]] = text;
            }
        }

        return CloudEvents.FromAttributes(attributes, body.ToMemory());
    }

    /// <summary>Decodes a received message.</summary>
    /// <exception cref="FormatException">The message is not a SourceFlow CloudEvent.</exception>
    public static MessageEnvelope Decode(ServiceBusReceivedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Decode(message.ApplicationProperties, message.Body);
    }

    /// <summary>
    /// The session of an envelope: its partition key (per-key order, doc 07 §4), or its id when it has none. Keys longer
    /// than Service Bus allows are hashed.
    /// </summary>
    public static string SessionId(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        string? key = envelope.EffectivePartitionKey;
        if (string.IsNullOrEmpty(key))
        {
            return envelope.MessageId.ToString();
        }

        return key.Length <= MaxIdLength
            ? key
            : "k-" + XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(key)).ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>Deliveries of a message before it was last re-sent by a delayed retry (0 for an original).</summary>
    public static int PreviousDeliveries(IReadOnlyDictionary<string, object> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.TryGetValue(DeliveriesProperty, out object? value) && value is int count && count > 0 ? count : 0;
    }

    /// <summary>
    /// The copy a delayed retry schedules: the same body and properties with the deliveries so far, and a broker message id
    /// of its own, so duplicate detection does not drop it.
    /// </summary>
    public static ServiceBusMessage Retry(ServiceBusReceivedMessage received, int deliveries)
    {
        ArgumentNullException.ThrowIfNull(received);
        var copy = new ServiceBusMessage(received);
        copy.ApplicationProperties[DeliveriesProperty] = deliveries;
        string suffix = "/r" + deliveries.ToString(CultureInfo.InvariantCulture);
        copy.MessageId = (received.MessageId.Length + suffix.Length <= MaxIdLength ? received.MessageId : received.MessageId[..(MaxIdLength - suffix.Length)]) + suffix;
        return copy;
    }
}
