using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SourceFlow;

/// <summary>Sends commands to their owner: decided locally, or routed to a remote owner (ADR-007).</summary>
public interface ICommandSender
{
    /// <summary>Sends <paramref name="command"/> and returns its result.</summary>
    ValueTask<CommandResult> SendAsync<TCommand>(TCommand command, SendOptions? options = null, CancellationToken cancellationToken = default)
        where TCommand : class, ICommand;
}

/// <summary>Per-send options (doc 03 §5.1).</summary>
public sealed record SendOptions
{
    /// <summary>Becomes the message id (UUIDv5 of the key) and enables the inbox: a repeat returns the original result.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>A caller-side optimistic check, for example from an HTTP <c>If-Match</c> header.</summary>
    public StreamVersion? ExpectedVersion { get; init; }

    /// <summary>The conversation this command belongs to; defaults to the command's own id.</summary>
    public CorrelationId? CorrelationId { get; init; }

    /// <summary>User headers carried in the envelope (at most 8 entries of 256 bytes, doc 04 §2.3).</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>The outcome of sending a command.</summary>
public enum CommandStatus
{
    /// <summary>Accepted; events were appended.</summary>
    Accepted = 1,

    /// <summary>Nothing to do; no events.</summary>
    NoOp = 2,

    /// <summary>Rejected with a business error.</summary>
    Rejected = 3,

    /// <summary>The expected version did not match, or concurrency retries were exhausted.</summary>
    Conflict = 4,

    /// <summary>A repeat of an already processed idempotency key; the original result is returned.</summary>
    Duplicate = 5,

    /// <summary>Handed to the outbox or transport for a remote owner.</summary>
    Dispatched = 6,
}

/// <summary>The result of <see cref="ICommandSender.SendAsync{TCommand}"/>.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="Version">The stream version after the command.</param>
/// <param name="Error">The error for <see cref="CommandStatus.Rejected"/> and <see cref="CommandStatus.Conflict"/>.</param>
/// <param name="Token">A read-your-writes consistency token (doc 14 §6).</param>
public readonly record struct CommandResult(CommandStatus Status, StreamVersion Version, Error? Error, ConsistencyToken Token)
{
    /// <summary>True for <see cref="CommandStatus.Accepted"/>, <see cref="CommandStatus.NoOp"/>,
    /// <see cref="CommandStatus.Duplicate"/> and <see cref="CommandStatus.Dispatched"/>.</summary>
    public bool IsSuccess => Status is CommandStatus.Accepted or CommandStatus.NoOp or CommandStatus.Duplicate or CommandStatus.Dispatched;

    /// <summary>Returns this result, or throws <see cref="CommandFailedException"/> when it is not a success.</summary>
    public CommandResult ThrowIfNotAccepted()
    {
        return IsSuccess ? this : throw new CommandFailedException(this);
    }
}

/// <summary>Thrown by <see cref="CommandResult.ThrowIfNotAccepted"/>.</summary>
public sealed class CommandFailedException : Exception
{
    /// <summary>Creates the exception from a failed result.</summary>
    public CommandFailedException(CommandResult result)
        : base($"Command {result.Status}: {result.Error?.Code} {result.Error?.Message}".TrimEnd())
    {
        Result = result;
    }

    /// <summary>The failed result.</summary>
    public CommandResult Result { get; }
}

/// <summary>
/// A read-your-writes token: the global position of the write plus its tenant (doc 14 §6).
/// The string form is <c>{commit}:{sequence}</c> or <c>{commit}:{sequence}@{tenant}</c>.
/// </summary>
/// <param name="Position">The global position of the last event written.</param>
/// <param name="Tenant">The tenant, when multi-tenant.</param>
public readonly record struct ConsistencyToken(GlobalPosition Position, string? Tenant = null)
{
    /// <summary>No token (nothing was written).</summary>
    public static ConsistencyToken None => default;

    /// <inheritdoc />
    public override string ToString()
    {
        string position = Position.ToString();
        return string.IsNullOrEmpty(Tenant) ? position : string.Create(CultureInfo.InvariantCulture, $"{position}@{Tenant}");
    }

    /// <summary>Parses the string form written by <see cref="ToString"/>.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ConsistencyToken token)
    {
        token = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int at = value.IndexOf('@', StringComparison.Ordinal);
        string position = at < 0 ? value : value[..at];
        string? tenant = at < 0 ? null : value[(at + 1)..];
        if (!GlobalPosition.TryParse(position, out GlobalPosition parsed) || tenant is { Length: 0 })
        {
            return false;
        }

        token = new ConsistencyToken(parsed, tenant);
        return true;
    }
}
