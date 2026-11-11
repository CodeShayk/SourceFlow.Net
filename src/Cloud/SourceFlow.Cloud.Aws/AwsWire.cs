using System.Globalization;
using System.IO.Hashing;
using System.Text;
using SourceFlow.Messaging;

namespace SourceFlow.Cloud.Aws;

/// <summary>
/// The SQS/SNS wire format: CloudEvents binary mode (doc 07 §4). Attributes travel as String message attributes named
/// <c>ce_{attribute}</c>, at most 10 per message; extensions that do not fit go into <c>ce_sfext</c>. The body is the
/// envelope body as UTF-8 text when SQS can carry it unchanged, otherwise base64 (marked by <c>sf_body</c>), so bodies are
/// never transcoded. Published events also carry <c>sf_topic</c>, so a receiver can apply a topic's client-side filter.
/// </summary>
public static class AwsWire
{
    /// <summary>The prefix of CloudEvents attribute names.</summary>
    public const string CloudEventsPrefix = "ce_";

    /// <summary>Marks a body that is not the envelope body as text: <c>base64</c> or <c>empty</c>.</summary>
    public const string BodyEncodingAttribute = "sf_body";

    /// <summary>The logical topic a published event came from.</summary>
    public const string TopicAttribute = "sf_topic";

    /// <summary>Why a message was moved to the dead-letter queue explicitly.</summary>
    public const string ReasonAttribute = "sf_reason";

    /// <summary>The SQS and SNS limit on message attributes.</summary>
    public const int MaxAttributes = 10;

    /// <summary>The SQS and SNS limit on a message (body plus attribute names, types and values), and on a batch.</summary>
    public const int MaxMessageBytes = 256 * 1024;

    /// <summary>
    /// The largest envelope body that always fits: a body that is not SQS text travels as base64 (4/3 larger), and 16 KiB
    /// stay for the attributes.
    /// </summary>
    public const int MaxBodyBytes = 180 * 1024;

    private const string StringType = "String";
    private const string Base64Encoding = "base64";
    private const string EmptyEncoding = "empty";
    private const string EmptyBody = "-";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Encodes an envelope for sending.</summary>
    /// <param name="envelope">The envelope.</param>
    /// <param name="topic">The logical topic, for a publish.</param>
    /// <param name="reason">The dead-letter reason, for an explicit move to the dead-letter queue.</param>
    public static AwsOutgoingMessage Encode(MessageEnvelope envelope, string? topic = null, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        string? encoding = null;
        string body;
        if (envelope.Body.IsEmpty)
        {
            encoding = EmptyEncoding;
            body = EmptyBody;
        }
        else if (TryText(envelope.Body.Span, out string? text))
        {
            body = text;
        }
        else
        {
            encoding = Base64Encoding;
            body = Convert.ToBase64String(envelope.Body.Span);
        }

        int extra = (encoding is null ? 0 : 1) + (topic is null ? 0 : 1) + (reason is null ? 0 : 1);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string value) in CloudEvents.ToAttributes(envelope, MaxAttributes - extra))
        {
            attributes[CloudEventsPrefix + name] = value;
        }

        AddIfPresent(attributes, BodyEncodingAttribute, encoding);
        AddIfPresent(attributes, TopicAttribute, topic);
        AddIfPresent(attributes, ReasonAttribute, reason);

        int size = Encoding.UTF8.GetByteCount(body);
        foreach ((string name, string value) in attributes)
        {
            size += Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value) + StringType.Length;
        }

        return new AwsOutgoingMessage(envelope, body, attributes, size, GroupId(envelope), envelope.MessageId.ToString());
    }

    /// <summary>Decodes a received message.</summary>
    /// <exception cref="FormatException">The message is not a SourceFlow CloudEvent.</exception>
    public static MessageEnvelope Decode(IReadOnlyDictionary<string, string> attributes, string body)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(body);
        var cloudEvents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string value) in attributes)
        {
            if (name.StartsWith(CloudEventsPrefix, StringComparison.Ordinal))
            {
                cloudEvents[name[CloudEventsPrefix.Length..]] = value;
            }
        }

        byte[] bytes = attributes.GetValueOrDefault(BodyEncodingAttribute) switch
        {
            null => Encoding.UTF8.GetBytes(body),
            Base64Encoding => Convert.FromBase64String(body),
            EmptyEncoding => [],
            string other => throw new FormatException($"Unknown body encoding '{other}'."),
        };

        return CloudEvents.FromAttributes(cloudEvents, bytes);
    }

    /// <summary>
    /// The FIFO message group of an envelope: its partition key (per-key order, doc 07 §4), or its id when it has none.
    /// Keys SQS does not accept as a group id (over 128 characters, or characters outside printable ASCII) are hashed.
    /// </summary>
    public static string GroupId(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        string? key = envelope.EffectivePartitionKey;
        if (key is null)
        {
            return envelope.MessageId.ToString();
        }

        if (key.Length is > 0 and <= 128 && key.All(c => c is >= '!' and <= '~'))
        {
            return key;
        }

        return "k-" + XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(key)).ToString("x16", CultureInfo.InvariantCulture);
    }

    // SQS bodies are XML 1.0 characters: #x9 | #xA | #xD | #x20-#xD7FF | #xE000-#xFFFD | #x10000-#x10FFFF.
    private static bool TryText(ReadOnlySpan<byte> bytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
    {
        text = null;
        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        for (int i = 0; i < decoded.Length; i++)
        {
            char c = decoded[i];
            if (c < 0x20 ? c is not ('\t' or '\n' or '\r') : c is '￾' or '￿')
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                i++;   // a valid pair (strict UTF-8 never yields a lone surrogate)
            }
        }

        text = decoded;
        return true;
    }

    private static void AddIfPresent(Dictionary<string, string> attributes, string name, string? value)
    {
        if (value is not null)
        {
            attributes[name] = value;
        }
    }
}

/// <summary>An encoded message, ready for <c>SendMessageBatch</c> or <c>PublishBatch</c>.</summary>
/// <param name="Envelope">The envelope.</param>
/// <param name="Body">The message body.</param>
/// <param name="Attributes">The message attributes (String type).</param>
/// <param name="Size">The size SQS and SNS count against their limit.</param>
/// <param name="GroupId">The FIFO message group.</param>
/// <param name="DeduplicationId">The FIFO deduplication id (the message id).</param>
public sealed record AwsOutgoingMessage(MessageEnvelope Envelope, string Body, IReadOnlyDictionary<string, string> Attributes, int Size, string GroupId, string DeduplicationId);
