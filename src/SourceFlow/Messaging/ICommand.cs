using System;

namespace SourceFlow.Messaging
{
    /// <summary>
    /// Interface for commands in the event-driven architecture.
    /// </summary>
    public interface ICommand
    {
        /// <summary>
        /// Unique identifier for the event.
        /// </summary>
        Guid EventId { get; }

        /// <summary>
        /// Source entity of the event, indicating where it originated from.
        /// </summary>
        Source Entity { get; set; }

        /// <summary>
        /// Indicates whether the event is a replay of an existing event.
        /// </summary>
        bool IsReplay { get; set; }

        /// <summary>
        /// The date and time when the event occurred.
        /// </summary>
        DateTime OccurredOn { get; }

        /// <summary>
        /// Sequence number of the event within the aggregate's event stream.
        /// </summary>
        int SequenceNo { get; set; }
    }

    /// <summary>
    /// Represents a command that carries a payload of type <typeparamref name="TPayload"/>.
    /// </summary>
    /// <typeparam name="TPayload">The type of the payload associated with the command. Must be a reference type that implements <see
    /// cref="IPayload"/> and has a parameterless constructor.</typeparam>
    public interface ICommand<TPayload> : ICommand where TPayload : class, IPayload, new()
    {
        /// <summary>
        /// The payload of the command, containing additional data.
        /// </summary>
        TPayload Payload { get; set; }
    }
}