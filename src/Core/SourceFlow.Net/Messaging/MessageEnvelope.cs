using System.Collections.Frozen;
using System.Text;

namespace SourceFlow.Messaging;

/// <summary>
/// The single envelope every message travels in: in process, in the outbox, on the wire and in dead letters
/// (doc 04 §1). It maps one-to-one onto CloudEvents attributes (ADR-011, <see cref="CloudEvents"/>).
/// </summary>
public sealed record MessageEnvelope
{
    /// <summary>The default tenant of single-tenant deployments.</summary>
    public const string DefaultTenant = "-";

    /// <summary>The JSON content type.</summary>
    public const string JsonContentType = "application/json";

    /// <summary>The logical id and idempotency key (UUIDv7, or UUIDv5 of an idempotency key).</summary>
    public required MessageId MessageId { get; init; }

    /// <summary>The logical type name, for example <c>bank.account.deposited</c>.</summary>
    public required string Type { get; init; }

    /// <summary>The type's schema version.</summary>
    public required int TypeVersion { get; init; }

    /// <summary>The producing service.</summary>
    public required string Source { get; init; }

    /// <summary>When the message was created (decision time for events).</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>The conversation or process id; defaults to the message id.</summary>
    public CorrelationId CorrelationId { get; init; }

    /// <summary>The message that caused this one.</summary>
    public MessageId? CausationId { get; init; }

    /// <summary>The stream id for events; the target id for commands.</summary>
    public string? Subject { get; init; }

    /// <summary>The ordering key; defaults to <see cref="Subject"/>.</summary>
    public string? PartitionKey { get; init; }

    /// <summary>The stream version, for events.</summary>
    public long? StreamVersion { get; init; }

    /// <summary>The tenant (ADR-022); <see cref="DefaultTenant"/> when single-tenant.</summary>
    public string TenantId { get; init; } = DefaultTenant;

    /// <summary>Who caused the message, for audit (doc 10 §1).</summary>
    public string? Actor { get; init; }

    /// <summary>W3C trace context <c>traceparent</c>.</summary>
    public string? TraceParent { get; init; }

    /// <summary>W3C trace context <c>tracestate</c>.</summary>
    public string? TraceState { get; init; }

    /// <summary>The body content type.</summary>
    public string ContentType { get; init; } = JsonContentType;

    /// <summary>The protection scheme when the body is encrypted, for example <c>kms:aws:…</c>.</summary>
    public string? Protection { get; init; }

    /// <summary>User extension headers (bounded, see <see cref="EnvelopeLimits"/>).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = FrozenDictionary<string, string>.Empty;

    /// <summary>The serialized (and possibly encrypted) payload.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The effective ordering key: <see cref="PartitionKey"/>, else <see cref="Subject"/>.</summary>
    public string? EffectivePartitionKey => PartitionKey ?? Subject;

    /// <summary>The CloudEvents <c>type</c>: <c>{name}.v{version}</c>.</summary>
    public string CloudEventType => CloudEvents.FormatType(Type, TypeVersion);

    /// <inheritdoc />
    public bool Equals(MessageEnvelope? other)
    {
        return other is not null
            && MessageId == other.MessageId
            && Type == other.Type
            && TypeVersion == other.TypeVersion
            && Source == other.Source
            && Time == other.Time
            && CorrelationId == other.CorrelationId
            && CausationId == other.CausationId
            && Subject == other.Subject
            && PartitionKey == other.PartitionKey
            && StreamVersion == other.StreamVersion
            && TenantId == other.TenantId
            && Actor == other.Actor
            && TraceParent == other.TraceParent
            && TraceState == other.TraceState
            && ContentType == other.ContentType
            && Protection == other.Protection
            && Headers.Count == other.Headers.Count
            && Headers.All(h => other.Headers.TryGetValue(h.Key, out string? v) && v == h.Value)
            && Body.Span.SequenceEqual(other.Body.Span);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(MessageId, Type, TypeVersion, Source, Time, Body.Length);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(Type).Append(".v").Append(TypeVersion).Append(' ').Append(MessageId);
        if (Subject is not null)
        {
            builder.Append(" subject=").Append(Subject);
        }

        return builder.ToString();
    }
}

/// <summary>Envelope size and header limits (doc 04 §2.3, NFR-PERF-07).</summary>
public static class EnvelopeLimits
{
    /// <summary>The maximum number of user headers.</summary>
    public const int MaxHeaders = 8;

    /// <summary>The maximum UTF-8 size of a header name or value.</summary>
    public const int MaxHeaderBytes = 256;

    /// <summary>Validates user headers; throws <see cref="ArgumentException"/> with the violated limit.</summary>
    public static void ValidateHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        if (headers.Count > MaxHeaders)
        {
            throw new ArgumentException($"At most {MaxHeaders} user headers are allowed; got {headers.Count}.", nameof(headers));
        }

        foreach (KeyValuePair<string, string> header in headers)
        {
            if (string.IsNullOrEmpty(header.Key) || Encoding.UTF8.GetByteCount(header.Key) > MaxHeaderBytes
                || Encoding.UTF8.GetByteCount(header.Value ?? string.Empty) > MaxHeaderBytes)
            {
                throw new ArgumentException($"Header '{header.Key}' exceeds {MaxHeaderBytes} bytes or has an empty name.", nameof(headers));
            }
        }
    }

    /// <summary>Throws <see cref="MessageTooLargeException"/> when the body exceeds <paramref name="maxBodyBytes"/>.</summary>
    public static void ValidateBody(MessageEnvelope envelope, int maxBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Body.Length > maxBodyBytes)
        {
            throw new MessageTooLargeException(envelope.Type, envelope.Body.Length, maxBodyBytes);
        }
    }
}

/// <summary>A message body exceeds the destination's limit; raised before the outbox insert (NFR-PERF-07).</summary>
public sealed class MessageTooLargeException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public MessageTooLargeException(string type, int size, int limit)
        : base($"Message '{type}' is {size} bytes, above the {limit}-byte limit of its destination.")
    {
        MessageType = type;
        Size = size;
        Limit = limit;
    }

    /// <summary>The logical message type.</summary>
    public string MessageType { get; }

    /// <summary>The body size in bytes.</summary>
    public int Size { get; }

    /// <summary>The limit in bytes.</summary>
    public int Limit { get; }
}
