using System;

namespace SourceFlow
{
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
        /// Unique identifier for the aggregate that this event belongs to.
        /// </summary>
        Guid AggregateId { get; }

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