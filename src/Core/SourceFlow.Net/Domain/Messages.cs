namespace SourceFlow;

/// <summary>Marker for every message: commands, domain events, integration events and timeouts (ADR-005).</summary>
public interface IMessage;

/// <summary>A request to change exactly one aggregate. Implement <see cref="ICommand{TAggregate}"/>.</summary>
public interface ICommand : IMessage;

/// <summary>A command that targets an aggregate of type <typeparamref name="TAggregate"/> (DM-R3).</summary>
/// <typeparam name="TAggregate">The aggregate state type that decides this command.</typeparam>
public interface ICommand<TAggregate> : ICommand
    where TAggregate : class;

/// <summary>A domain event: an internal fact appended to an aggregate's stream.</summary>
public interface IEvent : IMessage;

/// <summary>A public, versioned contract published to other services (doc 03 §7).</summary>
public interface IIntegrationEvent : IMessage;

/// <summary>A message a process manager schedules to itself (a durable timer, doc 04 §8).</summary>
public interface ITimeout : IMessage;

/// <summary>The kind of a registered message type.</summary>
public enum MessageKind
{
    /// <summary>A command (<see cref="ICommand"/>).</summary>
    Command = 1,

    /// <summary>A domain event (<see cref="IEvent"/>).</summary>
    Event = 2,

    /// <summary>An integration event (<see cref="IIntegrationEvent"/>).</summary>
    IntegrationEvent = 3,

    /// <summary>A timeout (<see cref="ITimeout"/>).</summary>
    Timeout = 4,

    /// <summary>A reply to a sender, such as <see cref="CommandRejected{TCommand}"/>.</summary>
    Reply = 5,
}

/// <summary>A command owner's reply that a command was rejected (ADR-018, doc 03 §6.1).</summary>
public interface ICommandRejected : IMessage
{
    /// <summary>Why the command was rejected.</summary>
    Error Error { get; }
}

/// <summary>
/// The reply a process manager receives when a command it sent was rejected by its owner (ADR-018). It reaches the
/// sending instance through the correlation when the command is owned locally or routed with <c>ReplyOnReject()</c>.
/// React to it with <c>React(CommandRejected&lt;TCommand&gt; r, ReactionContext&lt;TId&gt; ctx)</c> and list it in the
/// module's JSON context. Its message type is <c>{command type}.rejected</c> with the command's version.
/// </summary>
/// <typeparam name="TCommand">The rejected command.</typeparam>
/// <param name="Command">The command as sent.</param>
/// <param name="Error">Why it was rejected.</param>
public sealed record CommandRejected<TCommand>(
    [property: System.Text.Json.Serialization.JsonPropertyName("command")] TCommand Command,
    [property: System.Text.Json.Serialization.JsonPropertyName("error")] Error Error) : ICommandRejected
    where TCommand : class, ICommand;

/// <summary>Conventions of command replies.</summary>
public static class CommandReplies
{
    /// <summary>The reserved header carrying the endpoint a rejection reply goes to. It never propagates to caused messages.</summary>
    public const string ReplyToHeader = "sf-reply-to";

    /// <summary>The message type name of the rejection reply for a command type.</summary>
    public static string RejectedTypeName(string commandTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandTypeName);
        return commandTypeName + ".rejected";
    }
}

/// <summary>Thrown when generated dispatch receives a message type it was not built for.</summary>
public sealed class UnhandledMessageException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="messageType"/>.</summary>
    public UnhandledMessageException(Type messageType)
        : base($"No handler is generated for message type '{messageType?.FullName}'.")
    {
        MessageType = messageType ?? throw new ArgumentNullException(nameof(messageType));
    }

    /// <summary>The unhandled CLR message type.</summary>
    public Type MessageType { get; }
}
