using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SourceFlow;

/// <summary>The logical id of a message: UUIDv7 by default, UUIDv5 for idempotency keys (doc 04 §1).</summary>
/// <param name="Value">The UUID.</param>
public readonly record struct MessageId(Guid Value) : IComparable<MessageId>
{
    /// <summary>The empty id.</summary>
    public static MessageId Empty => default;

    /// <summary>True when the id is <see cref="Guid.Empty"/>.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a time-ordered UUIDv7 id from <paramref name="time"/>.</summary>
    public static MessageId New(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return new MessageId(Guid.CreateVersion7(time.GetUtcNow()));
    }

    /// <summary>Parses the canonical (<c>D</c>) or compact (<c>N</c>) form.</summary>
    public static MessageId Parse(string value)
    {
        return new MessageId(Guid.Parse(value, CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public int CompareTo(MessageId other)
    {
        return Value.CompareTo(other.Value);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value.ToString("D", CultureInfo.InvariantCulture);
    }
}

/// <summary>Identifies a conversation or process; process managers use their instance id (doc 03 §6.1).</summary>
/// <param name="Value">The correlation value; empty means none.</param>
public readonly record struct CorrelationId(string Value)
{
    /// <summary>No correlation.</summary>
    public static CorrelationId None => default;

    /// <summary>True when there is no correlation value.</summary>
    public bool IsNone => string.IsNullOrEmpty(Value);

    /// <summary>Uses a message id as the correlation of the conversation it starts.</summary>
    public static CorrelationId From(MessageId id)
    {
        return new CorrelationId(id.ToString());
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value ?? string.Empty;
    }
}

/// <summary>A stream id, <c>{aggregate}-{segment}</c>, at most 200 characters (doc 05 §1.1).</summary>
public readonly record struct StreamId
{
    /// <summary>The maximum length of a stream id.</summary>
    public const int MaxLength = 200;

    /// <summary>Creates a stream id after validating it.</summary>
    /// <exception cref="ArgumentException">The value is empty, too long or contains control characters.</exception>
    public StreamId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaxLength)
        {
            throw new ArgumentException($"A stream id is at most {MaxLength} characters; got {value.Length}.", nameof(value));
        }

        foreach (char c in value)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                throw new ArgumentException("A stream id cannot contain whitespace or control characters.", nameof(value));
            }
        }

        Value = value;
    }

    /// <summary>The stream id text.</summary>
    public string Value { get; }

    /// <summary>Builds <c>{aggregateName}-{segment}</c>.</summary>
    public static StreamId For(string aggregateName, string segment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateName);
        ArgumentException.ThrowIfNullOrWhiteSpace(segment);

        // The key is the text after the last dash (AggregateKey), so a dash in the segment would make it unreadable (ADR-015).
        if (segment.Contains('-', StringComparison.Ordinal))
        {
            throw new ArgumentException($"The id segment '{segment}' contains a dash; a stream id is {{aggregate}}-{{id}} with no dash in the id (ADR-015).", nameof(segment));
        }

        return new StreamId(aggregateName + "-" + segment);
    }

    /// <summary>Builds the stream id of an aggregate instance.</summary>
    public static StreamId For<TId>(string aggregateName, TId id)
        where TId : struct, IStronglyTypedId<TId>
    {
        if (id.IsDefault)
        {
            throw new ArgumentException("The default id value is not a valid aggregate id (GAP-20).", nameof(id));
        }

        return For(aggregateName, id.ToStreamSegment());
    }

    /// <summary>The id segment: the text after the last dash (UT-ABS-13).</summary>
    public string AggregateKey
    {
        get
        {
            string value = Value ?? string.Empty;
            int dash = value.LastIndexOf('-');
            return dash < 0 ? value : value[(dash + 1)..];
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value ?? string.Empty;
    }
}

/// <summary>The version of a stream: 0 means no stream; the first event is version 1.</summary>
/// <param name="Value">The version number.</param>
public readonly record struct StreamVersion(long Value) : IComparable<StreamVersion>
{
    /// <summary>The version of a stream that does not exist.</summary>
    public static StreamVersion None => default;

    /// <inheritdoc />
    public int CompareTo(StreamVersion other)
    {
        return Value.CompareTo(other.Value);
    }

    /// <summary>Advances the version by <paramref name="count"/> events.</summary>
    public static StreamVersion operator +(StreamVersion version, int count)
    {
        return new StreamVersion(checked(version.Value + count));
    }

    /// <summary>Compares two versions.</summary>
    public static bool operator <(StreamVersion left, StreamVersion right)
    {
        return left.Value < right.Value;
    }

    /// <summary>Compares two versions.</summary>
    public static bool operator >(StreamVersion left, StreamVersion right)
    {
        return left.Value > right.Value;
    }

    /// <summary>Compares two versions.</summary>
    public static bool operator <=(StreamVersion left, StreamVersion right)
    {
        return left.Value <= right.Value;
    }

    /// <summary>Compares two versions.</summary>
    public static bool operator >=(StreamVersion left, StreamVersion right)
    {
        return left.Value >= right.Value;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>The append precondition (doc 05 §3.1).</summary>
public readonly record struct ExpectedVersion
{
    private ExpectedVersion(ExpectedVersionKind kind, StreamVersion version)
    {
        Kind = kind;
        Version = version;
    }

    /// <summary>No precondition.</summary>
    public static ExpectedVersion Any => new(ExpectedVersionKind.Any, StreamVersion.None);

    /// <summary>The stream must not exist.</summary>
    public static ExpectedVersion NoStream => new(ExpectedVersionKind.NoStream, StreamVersion.None);

    /// <summary>The stream must exist.</summary>
    public static ExpectedVersion StreamExists => new(ExpectedVersionKind.StreamExists, StreamVersion.None);

    /// <summary>The kind of precondition.</summary>
    public ExpectedVersionKind Kind { get; }

    /// <summary>The exact version for <see cref="ExpectedVersionKind.Exact"/>.</summary>
    public StreamVersion Version { get; }

    /// <summary>The stream must be at exactly <paramref name="version"/>; <see cref="StreamVersion.None"/> means no stream.</summary>
    public static ExpectedVersion Exact(StreamVersion version)
    {
        return version == StreamVersion.None ? NoStream : new ExpectedVersion(ExpectedVersionKind.Exact, version);
    }

    /// <summary>True when <paramref name="current"/> satisfies this precondition.</summary>
    public bool IsSatisfiedBy(StreamVersion current)
    {
        return Kind switch
        {
            ExpectedVersionKind.Any => true,
            ExpectedVersionKind.NoStream => current == StreamVersion.None,
            ExpectedVersionKind.StreamExists => current != StreamVersion.None,
            _ => current == Version,
        };
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Kind == ExpectedVersionKind.Exact ? $"Exact({Version})" : Kind.ToString();
    }
}

/// <summary>The kind of <see cref="ExpectedVersion"/>.</summary>
public enum ExpectedVersionKind
{
    /// <summary>No precondition.</summary>
    Any = 0,

    /// <summary>The stream must not exist.</summary>
    NoStream = 1,

    /// <summary>The stream must exist.</summary>
    StreamExists = 2,

    /// <summary>The stream must be at an exact version.</summary>
    Exact = 3,
}

/// <summary>
/// A commit-ordered position in the global event log, compared lexicographically (ADR-010).
/// PostgreSQL: <c>(xid8, seq)</c>; SQL Server: <c>(rowversion, 0)</c>; in-memory: <c>(commit, index)</c>.
/// </summary>
/// <param name="Commit">The commit (transaction) order component.</param>
/// <param name="Sequence">The order within the commit.</param>
public readonly record struct GlobalPosition(ulong Commit, ulong Sequence) : IComparable<GlobalPosition>
{
    /// <summary>Before the first event.</summary>
    public static GlobalPosition Start => default;

    /// <inheritdoc />
    public int CompareTo(GlobalPosition other)
    {
        int c = Commit.CompareTo(other.Commit);
        return c != 0 ? c : Sequence.CompareTo(other.Sequence);
    }

    /// <summary>Compares two positions.</summary>
    public static bool operator <(GlobalPosition left, GlobalPosition right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>Compares two positions.</summary>
    public static bool operator >(GlobalPosition left, GlobalPosition right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>Compares two positions.</summary>
    public static bool operator <=(GlobalPosition left, GlobalPosition right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>Compares two positions.</summary>
    public static bool operator >=(GlobalPosition left, GlobalPosition right)
    {
        return left.CompareTo(right) >= 0;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Commit}:{Sequence}");
    }

    /// <summary>Parses the <c>commit:sequence</c> form.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out GlobalPosition position)
    {
        position = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0
            || !ulong.TryParse(value.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out ulong commit)
            || !ulong.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out ulong sequence))
        {
            return false;
        }

        position = new GlobalPosition(commit, sequence);
        return true;
    }
}
