namespace SourceFlow;

/// <summary>A business error: a value, not an exception (ADR-018).</summary>
/// <param name="Code">A stable, dot-separated code, for example <c>account.insufficient_funds</c>.</param>
/// <param name="Message">A human-readable description.</param>
/// <param name="Details">Optional structured details.</param>
public sealed record Error(
    [property: System.Text.Json.Serialization.JsonPropertyName("code")] string Code,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string Message,
    [property: System.Text.Json.Serialization.JsonPropertyName("details")] IReadOnlyDictionary<string, object?>? Details = null)
{
    /// <summary>The command is structurally invalid, for example its target id is the default value (GAP-20).</summary>
    public static Error InvalidCommand(string message)
    {
        return new Error("sourceflow.invalid_command", message);
    }

    /// <summary>The caller's expected version did not match the stream version.</summary>
    public static readonly Error VersionMismatch = new("sourceflow.version_mismatch", "The stream is not at the expected version.");

    /// <summary>Optimistic concurrency retries were exhausted.</summary>
    public static readonly Error ConcurrencyConflict = new("sourceflow.concurrency_conflict", "The stream changed concurrently; retries were exhausted.");
}

/// <summary>The outcome of a decision.</summary>
public enum DecisionKind
{
    /// <summary>Nothing changes; the command is idempotently satisfied.</summary>
    NoOp = 0,

    /// <summary>The command is accepted and produces events.</summary>
    Accepted = 1,

    /// <summary>The command is rejected with an <see cref="SourceFlow.Error"/>.</summary>
    Rejected = 2,
}

/// <summary>The pure result of <c>Decide</c>: events to append, a rejection, or nothing (ADR-003, ADR-018).</summary>
public readonly struct Decision
{
    private readonly IEvent[]? _events;

    private Decision(DecisionKind kind, IEvent[]? events, Error? error)
    {
        Kind = kind;
        _events = events;
        Error = error;
    }

    /// <summary>The decision kind.</summary>
    public DecisionKind Kind { get; }

    /// <summary>The events to append (empty unless <see cref="DecisionKind.Accepted"/>).</summary>
    public ReadOnlySpan<IEvent> Events => _events;

    /// <summary>The rejection error (only for <see cref="DecisionKind.Rejected"/>).</summary>
    public Error? Error { get; }

    /// <summary>A decision that changes nothing.</summary>
    public static Decision NoOp => default;

    /// <summary>True when the decision is <see cref="DecisionKind.Accepted"/>.</summary>
    public bool IsAccepted => Kind == DecisionKind.Accepted;

    /// <summary>True when the decision is <see cref="DecisionKind.Rejected"/>.</summary>
    public bool IsRejected => Kind == DecisionKind.Rejected;

    /// <summary>Accepts the command with one event.</summary>
    public static Decision Accept(IEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return new Decision(DecisionKind.Accepted, [@event], null);
    }

    /// <summary>Accepts the command with one or more events (copied).</summary>
    /// <exception cref="ArgumentException">No events were given, or one is null.</exception>
    public static Decision Accept(params ReadOnlySpan<IEvent> events)
    {
        if (events.IsEmpty)
        {
            throw new ArgumentException("An accepted decision needs at least one event; use Decision.NoOp instead.", nameof(events));
        }

        IEvent[] copy = events.ToArray();
        foreach (IEvent e in copy)
        {
            if (e is null)
            {
                throw new ArgumentException("Events cannot be null.", nameof(events));
            }
        }

        return new Decision(DecisionKind.Accepted, copy, null);
    }

    /// <summary>Rejects the command.</summary>
    public static Decision Reject(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Decision(DecisionKind.Rejected, null, error);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Kind switch
        {
            DecisionKind.Accepted => $"Accepted({_events!.Length} event(s))",
            DecisionKind.Rejected => $"Rejected({Error!.Code})",
            _ => "NoOp",
        };
    }
}

/// <summary>Inputs to a decision that the runtime supplies, fixed for the whole decision (doc 03 §4.1).</summary>
/// <param name="Now">The decision time, from <see cref="TimeProvider"/>.</param>
/// <param name="Version">The stream version the state was loaded at; <see cref="StreamVersion.None"/> for a new stream.</param>
/// <param name="CommandId">The id of the command being decided.</param>
/// <param name="CorrelationId">The conversation the command belongs to.</param>
/// <param name="Headers">User headers from the envelope.</param>
public readonly record struct DecisionContext(
    DateTimeOffset Now,
    StreamVersion Version,
    MessageId CommandId,
    CorrelationId CorrelationId,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>True when the stream does not exist yet.</summary>
    public bool IsNew => Version == StreamVersion.None;
}
