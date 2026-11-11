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
