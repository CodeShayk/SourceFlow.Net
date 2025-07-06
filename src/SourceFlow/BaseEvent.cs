using System;

namespace SourceFlow
{
    /// <summary>
    /// Base class for events in the event-driven architecture.
    /// </summary>
    public class BaseEvent : IEvent
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BaseEvent"/> class with a specified aggregate id.
        /// </summary>
        /// <param name="aggregateId"></param>
        public BaseEvent(Source source)
        {
            EventId = Guid.NewGuid();
            OccurredOn = DateTime.UtcNow;
            Source = source;
        }

        /// <summary>
        /// Unique identifier for the event.
        /// </summary>
        public Guid EventId { get; }

        public Source Source { get; set; }

        /// <summary>
        /// Indicates whether the event is a replay of an existing event.
        /// </summary>
        public DateTime OccurredOn { get; }

        /// <summary>
        /// Indicates whether the event is a replay of an existing event.
        /// </summary>
        bool IEvent.IsReplay { get; set; }

        /// <summary>
        /// Sequence number of the event within the aggregate's event stream.
        /// </summary>
        int IEvent.SequenceNo { get; set; }
    }
}