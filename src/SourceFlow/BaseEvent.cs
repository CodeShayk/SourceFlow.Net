using System;

namespace SourceFlow
{
    /// <summary>
    /// Base class for events in the event-driven architecture.
    /// </summary>
    public class BaseEvent<TPayload> : IEvent<TPayload> where TPayload : class, IEventPayload, new()
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BaseEvent"/> class with a specified aggregate id.
        /// </summary>
        public BaseEvent()
        {
            EventId = Guid.NewGuid();
            OccurredOn = DateTime.UtcNow;
            Payload = new TPayload();
        }

        /// <summary>
        /// Unique identifier for the event.
        /// </summary>
        public Guid EventId { get; }

        /// <summary>
        /// Entity entity of the event, indicating where it originated from.
        /// </summary>
        public Source Entity { get; set; }

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
        public int SequenceNo { get; set; }

        /// <summary>
        /// Payload of the event, containing the data associated with the event.
        /// </summary>
        public TPayload Payload { get; set; }
    }
}