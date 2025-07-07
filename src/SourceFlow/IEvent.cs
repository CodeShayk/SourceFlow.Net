using System;

namespace SourceFlow
{
    public interface IEvent<TPayload> : IEvent where TPayload : class, IEventPayload, new()
    {
        /// <summary>
        /// The payload of the event, containing additional data.
        /// </summary>
        TPayload Payload { get; set; }
    }

    /// <summary>
    /// Interface for events in the event-driven architecture.
    /// </summary>
    public interface IEvent
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
}