using System;
using System.Collections.Generic;

namespace SourceFlow.Messaging
{
    /// <summary>
    /// Interface for message metadata in the event-driven architecture.
    /// </summary>
    public interface IMetadata
    {
        /// <summary>
        /// Metadata associated with the command, which includes information such as event ID, occurrence time, and sequence number.
        /// </summary>
        Metadata Metadata { get; set; }
    }

    /// <summary>
    /// Represents metadata for commands and events in the event-driven architecture.
    /// </summary>
    public class Metadata
    {
        public Metadata()
        {
            EventId = Guid.NewGuid();
            OccurredOn = DateTime.UtcNow;
            IsReplay = false;
            Properties = new Dictionary<string, object>();
        }

        /// <summary>
        /// Unique identifier for the command/event.
        /// </summary>
        public Guid EventId { get; set; }

        /// <summary>
        /// Indicates whether the command/event is a replay of an existing command.
        /// </summary>
        public bool IsReplay { get; set; }

        /// <summary>
        /// The date and time when the command/event was raised.
        /// </summary>
        public DateTime OccurredOn { get; set; }

        /// <summary>
        /// Sequence number of the command/event within the aggregate's event stream.
        /// </summary>
        public int SequenceNo { get; set; }

        /// <summary>
        /// Additional properties associated with the command/event.
        /// </summary>
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }
}
